// =========================================================================
// IoTestViewModelTests：IO 通讯测试窗 VM 用例（点表驱动/快照刷新/写点回显/遍历）
// 桩：ScriptedPointBus（快照脚本驱动 + Writes 录制 + WriteSucceeds 失败注入）。
// 异步等待：PollUntil 轮询条件（UiInvoke 无 SynchronizationContext 时同步执行，
// Task.Run 回调在本测试进程内立即生效，等待只是兜底时序）。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Tests.Mocks;
using Xunit;

namespace CureLine.Tests;

public class IoTestViewModelTests
{
    /// <summary>异步条件等待（兜底时序；条件 2 秒内不成立判失败）。</summary>
    private static async Task PollUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        Assert.True(condition(), $"等待超时：{what}");
    }

    /// <summary>缺省模板点表（10 架 3 层全绑三 DI + 三 DO，与现场同款）。</summary>
    private static PointTableConfig DefaultConfig() =>
        IoPointTableStore.BuildDefaultTemplate("测试耦合器", "127.0.0.1", 10, 3);

    private static IoPointKey Key(bool isInput, IoPointRole role, int rack, int level) =>
        new(isInput, role, rack, level);

    [Fact]
    public void IoTestVm_BuildsSlotsFromPointTable()
    {
        var bus = new ScriptedPointBus();
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());

        // 网格与主窗同源：当前机台固定 30 卡（10 库位/行 × 3 层）；单机台（10 架
        // 全绑）下拉框只有"1号固化架"一项 → 隐藏选择器，视觉与旧版等价
        Assert.Equal(10, vm.RackCount);
        Assert.Equal(3, vm.LevelCount);
        Assert.Equal(30, vm.Slots.Count);
        Assert.Single(vm.Machines);
        Assert.False(vm.IsMachinePickerVisible);

        // 每仓位：DI 3 路（左/右光栅、托盘）+ DO 3 路（红/黄/绿），全绑定非占位
        var slot = vm.Slots.First(s => s.Rack == 1 && s.Level == 1);
        Assert.Equal(3, slot.Inputs.Count);
        Assert.Equal(3, slot.Outputs.Count);
        Assert.All(slot.Inputs, p => Assert.True(p.Exists));
        Assert.All(slot.Outputs, p => Assert.True(p.Exists));
        Assert.All(slot.Inputs, p => Assert.True(p.IsInput));
        Assert.All(slot.Outputs, p => Assert.True(p.IsOutput));

        // 双架点表（rackStart=11 生成架 11~20）：显示仍是当前机台的 30 卡（每行
        // 固定 10 库位），机台选项只列"有绑定"的段——只出"2号固化架"一项
        var config2 = IoPointTableStore.BuildDefaultTemplate("测试2", "127.0.0.2", 10, 3, rackStart: 11);
        var vm2 = new CureStation.ViewModels.IoTestViewModel(bus, config2);
        Assert.Equal(20, vm2.RackCount);
        Assert.Equal(30, vm2.Slots.Count);
        Assert.All(vm2.Slots, s => Assert.InRange(s.Rack, 11, 20));
        Assert.Equal(new[] { "2号固化架" }, vm2.Machines);
        Assert.False(vm2.IsMachinePickerVisible);

        // 卡标题=库位编码-层名（架内站号口径，与主窗同源：架 11 层 1 → A01-上层）
        Assert.Equal("A01-上层", vm2.Slots[0].Title);
    }

    [Fact]
    public void IoTestVm_MachinePicker_SwitchesSlots()
    {
        // 双架点表：架 1~10（第一台耦合器）+ 架 11~20（第二台，rackStart=11）
        var config = DefaultConfig();
        var second = IoPointTableStore.BuildDefaultTemplate("测试2", "127.0.0.2", 10, 3, rackStart: 11);
        config.Devices.Add(second.Devices[0]);
        var bus = new ScriptedPointBus();
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, config);

        // 下拉两个选项（只列有绑定的机台段），默认选中第一项=1 号固化架
        Assert.Equal(new[] { "1号固化架", "2号固化架" }, vm.Machines);
        Assert.True(vm.IsMachinePickerVisible);
        Assert.Equal(0, vm.SelectedMachineOrdinal);
        Assert.All(vm.Slots, s => Assert.InRange(s.Rack, 1, 10));

        // 切到 2 号机台：显示架 11~20 的 30 卡，库位编码按架内站号重新起算
        vm.SelectedMachineOrdinal = 1;
        Assert.Equal(30, vm.Slots.Count);
        Assert.All(vm.Slots, s => Assert.InRange(s.Rack, 11, 20));
        Assert.Equal("A01-上层", vm.Slots[0].Title);
        // 行优先契约：第一行=层 1 的 10 架（先层后架，与主窗一致）
        Assert.Equal(Enumerable.Range(11, 10).ToArray(),
            vm.Slots.Take(10).Select(s => s.Rack).ToArray());
        Assert.Contains(vm.LogLines, l => l.Contains("[机台] 切换到 2号固化架"));

        // 切回 1 号：回架 1~10
        vm.SelectedMachineOrdinal = 0;
        Assert.Equal(30, vm.Slots.Count);
        Assert.All(vm.Slots, s => Assert.InRange(s.Rack, 1, 10));
    }

    [Fact]
    public void IoTestVm_EmptySlot_GetsPlaceholders()
    {
        // 点表删掉 (5,2) 的绑定 → 该仓位卡补占位（Exists=false，灰格齐整不响应点击）
        var config = DefaultConfig();
        var device = config.Devices[0];
        device.Bindings.RemoveAll(b => b.Rack == 5 && b.Level == 2);
        var bus = new ScriptedPointBus();
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, config);

        var slot = vm.Slots.First(s => s.Rack == 5 && s.Level == 2);
        Assert.All(slot.Inputs, p => Assert.False(p.Exists));
        Assert.All(slot.Outputs, p => Assert.False(p.Exists));
        Assert.Equal(3, slot.Inputs.Count);   // 占位补齐三格，视觉齐整
    }

    [Fact]
    public void IoTestVm_PollOnce_ReflectsSnapshotThreeStates()
    {
        var bus = new ScriptedPointBus();
        var config = DefaultConfig();
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, config);

        var left = vm.Slots.First(s => s.Rack == 1 && s.Level == 1)
            .Inputs.First(p => p.Exists);
        var red = vm.Slots.First(s => s.Rack == 1 && s.Level == 1)
            .Outputs.First(p => p.Exists);

        // 构造尾 PollOnce：快照空 → 全部"未读到"（null）
        Assert.Null(left.IsOn);
        Assert.Null(red.IsOn);

        // 快照设左光栅 ON → 刷新后 true；红灯光设仍 null
        bus.SetSnapshotSilently(Key(true, IoPointRole.RackLightL, 1, 1), true);
        vm.RefreshCommand.Execute(null);
        Assert.True(left.IsOn);
        Assert.Null(red.IsOn);

        // 设红灯 OFF → false（显式灭，区别于未读）
        bus.SetSnapshotSilently(Key(false, IoPointRole.LampRed, 1, 1), false);
        vm.RefreshCommand.Execute(null);
        Assert.False(red.IsOn);
    }

    [Fact]
    public async Task IoTestVm_TogglePoint_WritesAndEchoes()
    {
        var bus = new ScriptedPointBus();
        bus.Start(DefaultConfig());
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());
        var red = vm.Slots.First(s => s.Rack == 2 && s.Level == 1)
            .Outputs.First(p => p.Exists);

        // 点亮：写 true + 回显 ON
        vm.TogglePoint(red);
        await PollUntilAsync(() => !red.Busy, "第一次写入完成");
        Assert.True(red.IsOn);
        Assert.Contains(bus.Writes, w => w.Key == red.Key && w.State);

        // 再点：写 false
        vm.TogglePoint(red);
        await PollUntilAsync(() => !red.Busy, "第二次写入完成");
        Assert.False(red.IsOn);
        Assert.Contains(bus.Writes, w => w.Key == red.Key && !w.State);

        // 日志边沿记成功
        Assert.Contains(vm.LogLines, l => l.Contains("写 ON 成功"));
        Assert.Contains(vm.LogLines, l => l.Contains("写 OFF 成功"));
    }

    [Fact]
    public async Task IoTestVm_TogglePoint_WriteFails_EchoesSnapshot()
    {
        var bus = new ScriptedPointBus();
        bus.Start(DefaultConfig());
        bus.WriteSucceeds = false;    // 模拟通讯失败
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());
        var yellow = vm.Slots.First(s => s.Rack == 1 && s.Level == 1)
            .Outputs.First(p => p.Exists);

        vm.TogglePoint(yellow);
        await PollUntilAsync(() => !yellow.Busy, "失败路径返回");

        Assert.Null(yellow.IsOn);     // 灯弹回真实读数（快照无值=null），不误导"亮着"
        Assert.Contains(vm.LogLines, l => l.Contains("失败"));
        Assert.Empty(bus.Writes);
    }

    [Fact]
    public void IoTestVm_TogglePoint_IgnoresInputAndBusy()
    {
        var bus = new ScriptedPointBus();
        bus.Start(DefaultConfig());
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());
        var di = vm.Slots.First(s => s.Rack == 1 && s.Level == 1).Inputs.First(p => p.Exists);
        // 占位格（缺省模板全绑无空仓——直接构造占位验证路径）
        var placeholder = CureStation.ViewModels.IoTestPointVm.Placeholder();

        // DI 只读：点了不写（测试窗不驱动输入侧）；占位格不写
        vm.TogglePoint(di);
        vm.TogglePoint(placeholder);
        Assert.Empty(bus.Writes);

        // Busy 防抖：写入中再点被忽略并记日志
        var doVm = vm.Slots.First(s => s.Rack == 1 && s.Level == 1).Outputs.First(p => p.Exists);
        doVm.Busy = true;
        vm.TogglePoint(doVm);
        Assert.Contains(vm.LogLines, l => l.Contains("忽略本次点击"));
        doVm.Busy = false;
    }

    [Fact]
    public async Task IoTestVm_AllOff_WritesAllDosOff()
    {
        var bus = new ScriptedPointBus();
        bus.Start(DefaultConfig());
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());
        int doCount = vm.Slots.Sum(s => s.Outputs.Count(o => o.Exists));
        Assert.Equal(90, doCount);    // 30 仓位 × 3 色

        vm.AllOffCommand.Execute(null);
        await PollUntilAsync(() =>
            vm.LogLines.Any(l => l.Contains("全部关闭完成")), "全部关闭汇总");

        // 90 路 DO 全部写 OFF，无一路 ON
        Assert.Equal(doCount, bus.Writes.Count(w => !w.State));
        Assert.DoesNotContain(bus.Writes, w => w.State);
    }

    [Fact]
    public async Task IoTestVm_Sweep_StartStop()
    {
        var bus = new ScriptedPointBus();
        bus.Start(DefaultConfig());
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());

        // 启动：进入遍历态，开始逐路写
        vm.SweepCommand.Execute(null);
        Assert.True(vm.Sweeping);
        await PollUntilAsync(() => bus.Writes.Count > 0, "遍历开始写点");

        // 停止：退出遍历态 + 全部 DO 写 OFF + 汇总日志
        vm.SweepCommand.Execute(null);
        Assert.False(vm.Sweeping);
        await PollUntilAsync(() =>
            vm.LogLines.Any(l => l.Contains("已全部关闭")), "停止后全关汇总");

        // 停止后最后一批写应全 OFF（跑马灯残留灯被清掉）
        Assert.DoesNotContain(bus.Writes.TakeLast(90), w => w.State);
        Assert.Contains(vm.LogLines, l => l.Contains("一键遍历开始"));
    }

    [Fact]
    public void IoTestVm_ConnectionAndErrorBridge()
    {
        var bus = new ScriptedPointBus();
        var vm = new CureStation.ViewModels.IoTestViewModel(bus, DefaultConfig());

        // 连接态桥接（UiInvoke 无上下文同步执行）
        bus.RaiseConnection(true);
        Assert.True(vm.IsConnected);
        bus.RaiseError("测试错误边沿");
        Assert.Contains(vm.LogLines, l => l.Contains("测试错误边沿"));

        // Detach 退订：错误不再进日志
        int count = vm.LogLines.Count;
        vm.Detach();
        bus.RaiseError("退订后不应出现");
        Assert.Equal(count, vm.LogLines.Count);
    }
}
