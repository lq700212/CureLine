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
}
