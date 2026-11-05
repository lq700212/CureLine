// =========================================================================
// IoBusTests：通讯总线（Mock 读写往返/事件、空配置防呆、停机幂等）
// 真机 Modbus 不联网测——只测契约行为（连接测试路径属现场验证）。
// =========================================================================

using CureLine.Core.Comms;
using Pausing = System.Threading.Tasks;

namespace CureLine.Tests;

public class IoBusTests
{
    private static PointTableConfig DefaultConfig() =>
        IoPointTableStore.BuildDefaultTemplate("T", "192.168.1.20", 10, 3);

    // ---------- 真机总线：契约防呆 ----------

    [Fact]
    public void ModbusBus_StartEmpty_Throws()
    {
        using var bus = new ModbusPointTableBus();
        Assert.Throws<ArgumentException>(() => bus.Start(new PointTableConfig()));
    }

    [Fact]
    public void ModbusBus_StopBeforeStart_IsSafe()
    {
        // 停机幂等：没 Start 也能 Dispose（现场热更/异常路径不能炸）
        var bus = new ModbusPointTableBus();
        bus.Stop();
        bus.Dispose();
        bus.Dispose();
        Assert.Equal("已停止", bus.ConnectionSummary);
    }

    // ---------- Mock 总线 ----------

    [Fact]
    public void MockBus_WritePoint_TogglesSnapshotAndEvent()
    {
        using var bus = new MockPointTableBus();
        var key = new IoPointKey(false, IoPointRole.LampRed, 1, 1);
        // Mock 总线 Start 后自动演示线程会并发发托盘/光栅边沿（模拟仓位流转）——
        // 断言"事件序列里存在写入回显"而非"最后一个事件是它"（竞态安全）；
        // 收集容器必须并发安全：TickLoop 后台 Add 与本线程 enumerate 并发，
        // List 会爆 "Collection was modified"（长跑偶发，实测踩中）
        var events = new System.Collections.Concurrent.ConcurrentQueue<PointChangedEventArgs>();
        bus.PointChanged += (_, e) => events.Enqueue(e);

        bus.Start(DefaultConfig());
        Assert.True(bus.IsConnected);

        Assert.True(bus.WritePoint(key, true));
        Assert.True(bus.LatestSnapshot.Get(key));
        Assert.Contains(events, e => e.Key == key && e.State);

        Assert.True(bus.WritePoint(key, false));
        Assert.False(bus.LatestSnapshot.Get(key));
        Assert.Contains(events, e => e.Key == key && !e.State);
    }

    [Fact]
    public void MockBus_EdgeHandler_SeesSnapshotContainingEdgeValue()
    {
        // 边沿×快照顺序契约（V0.0.64，真机与 Mock 轮询统一）：先整份换快照、
        // 再发差异边沿——订阅方（状态机光栅门控/报警复位在边沿处理时读
        // LatestSnapshot 查双让空）必须能看到本边沿的新值。handler 与快照
        // 替换同步执行（同锁内逐点发边沿），记录"事件值+事件时刻快照值"
        // 成对比对即可锁定契约，不在后台线程内断言。
        using var bus = new MockPointTableBus();
        var atEdge = new System.Collections.Concurrent
            .ConcurrentDictionary<IoPointKey, (bool State, bool SnapVal)>();
        bus.PointChanged += (_, e) =>
            atEdge[e.Key] = (e.State, bus.LatestSnapshot.Get(e.Key) ?? false);

        bus.Start(DefaultConfig()); // 演示线程的托盘/光栅边沿同样同步进入 handler
        var lamp = new IoPointKey(false, IoPointRole.LampRed, 1, 1);
        Assert.True(bus.WritePoint(lamp, true));
        Assert.True(bus.WritePoint(lamp, false));
        bus.Stop();

        Assert.True(atEdge.ContainsKey(lamp)); // 写灯回显边沿必入对表
        Assert.All(atEdge, kv =>
            Assert.Equal(kv.Value.State, kv.Value.SnapVal)); // 每条边沿时刻快照=事件值
    }

    [Fact]
    public void MockBus_WriteUnbound_ReturnsFalse()
    {
        using var bus = new MockPointTableBus();
        bus.Start(DefaultConfig());
        // 模板只有 10 架：架 11 没绑 → false（不炸不误写）
        Assert.False(bus.WritePoint(new IoPointKey(false, IoPointRole.LampRed, 11, 1), true));
    }

    [Fact]
    public void MockBus_Stop_ClearsSnapshot()
    {
        var bus = new MockPointTableBus();
        bus.Start(DefaultConfig());
        bus.Stop();
        Assert.Same(PointTableSnapshot.Empty, bus.LatestSnapshot);
        Assert.Equal("已停止", bus.ConnectionSummary);
        bus.Dispose(); // 幂等
    }

    [Fact]
    public async Pausing.Task MockBus_DemoLoop_ProducesInputStates()
    {
        // 演示态推进（80ms 拍）：2.5 秒内快照应出现至少一个"true"输入点（托盘/光栅）
        var config = DefaultConfig();
        config.Devices[0].PollIntervalMs = 80;
        using var bus = new MockPointTableBus();
        bus.Start(config);

        var deadline = DateTime.UtcNow.AddSeconds(2.5);
        bool sawTrue = false;
        while (!sawTrue && DateTime.UtcNow < deadline)
        {
            await Pausing.Task.Delay(100);
            sawTrue = bus.LatestSnapshot.States.Any(k => k.Key.IsInput && k.Value);
        }
        Assert.True(sawTrue, "演示态 2.5 秒内应出现至少一个输入点为 true");
    }

    // ---------- 扫描读取计划（V0.0.41 按块去重：180 帧/周期 → 20 帧） ----------

    [Fact]
    public void ScanPlan_SingleDeviceTenBlocks_GroupsByBlockAndDirection()
    {
        // 旧单台布局（1 台 10 块 18 绑定/块）：计划 = 10 块 × 2 方向 = 20 组，
        // 每组 9 绑定（3 层 × 3 点）共用一帧——同块同方向绝不重复读
        var device = IoPointTableStore.BuildDefaultTemplate("T", "192.168.1.20", 10, 3).Devices[0];
        var plan = ScanPlanner.BuildScanPlan(device);
        Assert.Equal(20, plan.Count);
        Assert.All(plan, g => Assert.Equal(9, g.Bindings.Count));
        foreach (var g in plan)
        {
            Assert.Equal(g.IsInput ? device.DiAddressOf(g.Block) : device.DoAddressOf(g.Block),
                g.Address);
            Assert.All(g.Bindings, b => Assert.Equal((b.IsInput, b.Block), (g.IsInput, g.Block)));
        }
        // 帧序稳定：块号升序、同块输入在前（现场抓包对照口径）
        Assert.Equal(plan.OrderBy(g => g.Block).ThenBy(g => g.IsInput).Select(g => (g.Block, g.IsInput)),
            plan.Select(g => (g.Block, g.IsInput)));
    }

    [Fact]
    public void ScanPlan_SiteLayout_TenDevicesOneBlockEach_TwentyFramesTotal()
    {
        // 现场口径（10 台 × 每台 1 块；V0.0.65 第 2 台 DO 段尾位 11~15 挂机台总监
        // 塔灯五件套）：塔灯与仓位灯同块同方向同帧，帧结构不变——每台仍 DI 1 帧 +
        // DO 1 帧，全场 20 帧/周期；仅宿主台 DO 组绑定数 9→14（9 仓位 + 5 塔灯）。
        var config = IoPointTableStore.BuildMachineTemplate();
        int total = 0;
        for (int i = 0; i < config.Devices.Count; i++)
        {
            var device = config.Devices[i];
            var plan = ScanPlanner.BuildScanPlan(device);
            Assert.Equal(2, plan.Count);                       // DI 1 帧 + DO 1 帧
            bool beaconHost = i == 1;                          // 缺省自动档：第 2 台挂塔灯
            foreach (var g in plan)
                Assert.Equal(!g.IsInput && beaconHost ? 14 : 9, g.Bindings.Count);
            total += plan.Count;
        }
        Assert.Equal(20, total);
    }
}
