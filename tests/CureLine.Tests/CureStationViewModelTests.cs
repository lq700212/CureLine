// =========================================================================
// CureStationViewModelTests：程序① VM 用例（主窗网格/点表配置 VM/Fake 桩）
// 铁律：新增 VM 编排必须补用例——模拟点表 + FakeDialogService，TestScope 隔离。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Core.Security;
using CureLine.Core.Services;
using CureLine.Tests.Mocks;
using CureStation.ViewModels;   // DurationUnit/PolicyTimeUnit（V0.0.58 顶栏全局时长用例）
using Xunit;

namespace CureLine.Tests;

public class CureStationViewModelTests
{
    // ---------- 主窗 VM ----------

    /// <summary>临时目录下补建 config/（WritePrivateProfileString 不自动建目录）。</summary>
    private static void EnsureConfigDir() =>
        Directory.CreateDirectory(AppPaths.ConfigDir);

    [Fact]
    public void MainVm_WithDefaultTemplate_Builds30Slots_MockConnected()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // Simulate=1 → Mock 总线（无硬件）；临时目录 config.ini
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.Equal(30, vm.Slots.Count);                       // 10 架 × 3 层
        Assert.True(vm.IsConnected);                            // Mock 恒连通
        Assert.Contains("演示", vm.ConnectionText);
    }

    [Fact]
    public void MainVm_CardShowsBoundProductCount_NotSn()
    {
        // V0.0.97：卡片不再显示产品 SN（原 V0.0.84~V0.0.86 的动态行数区/
        // 摘要/小屏降档一整套随之退役），改为显示"已绑 N 件"。
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 空位：无绑定 → 文案空、整行收起（不留空白）
        Assert.All(vm.Slots, s => Assert.False(s.HasBindingText));
        Assert.All(vm.Slots, s => Assert.Equal(0, s.BoundProductCount));
        Assert.All(vm.Slots, s => Assert.Equal(new System.Windows.Thickness(0), s.BindingRowMargin));

        // 有绑定：显示件数，不显示任何 SN 原文
        vm.Slots[0].Refresh(PointTableSnapshot.Empty, true, null, 0, 3);
        Assert.Equal("已绑 3 件", vm.Slots[0].BindingText);
        Assert.Equal(3, vm.Slots[0].BoundProductCount);
        Assert.DoesNotContain("SN", vm.Slots[0].BindingText);
    }

    [Fact]
    public void MainVm_Slots_RowMajorOrder_WithCustomerLocationCodes()
    {
        // 布局契约：三排=上中下三层、一排 10 站——集合行优先（先层后架），
        // 主窗 UniformGrid（Columns=RackCount/Rows=LevelCount）逐格填充即得
        // 机台实景；乱序会导致"层"错行显示。卡片主标题=客户库位编码（Date Matrix）。
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.Equal(10, vm.RackCount);
        Assert.Equal(3, vm.LevelCount);
        // 行优先顺序：第一排=上层 10 站（架 1~10），第二排=中层……
        Assert.Equal(1, vm.Slots[0].Rack);
        Assert.Equal(1, vm.Slots[0].Level);
        Assert.Equal(2, vm.Slots[1].Rack);
        Assert.Equal(1, vm.Slots[1].Level);
        Assert.Equal(10, vm.Slots[9].Rack);
        Assert.Equal(1, vm.Slots[9].Level);
        Assert.Equal(1, vm.Slots[10].Rack);
        Assert.Equal(2, vm.Slots[10].Level);
        Assert.Equal(10, vm.Slots[29].Rack);
        Assert.Equal(3, vm.Slots[29].Level);
        // 库位编码对照客户表首末卡（A01=架1上层，J12=架10下层）
        Assert.Equal("A01", vm.Slots[0].Code);
        Assert.Equal("B02", vm.Slots[1].Code);
        Assert.Equal("J12", vm.Slots[29].Code);
    }

    [Fact]
    public void MainVm_EmptyPointTable_SelfHealsWithDefaultTemplate()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.Contains(fake.Calls, c => c.Contains("配置自愈"));
        // 落盘校验：再 Load 得到同一张表（自愈有据可查）。
        // V0.0.41 起自愈=现场口径：10 台耦合器（每台 1 架 3 层），IP 预填现值 .20~.29
        var loaded = IoPointTableStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(10, loaded!.Devices.Count);
        Assert.All(loaded.Devices, d => Assert.Equal(1, d.BlockCount));
        // 塔灯口径（V0.0.65）：第 2 台加挂机台总监塔灯 5 路 → 23，其余台 18
        Assert.Equal(18, loaded.Devices[0].Bindings.Count);
        Assert.Equal(23, loaded.Devices[1].Bindings.Count);
        Assert.Equal(18, loaded.Devices[9].Bindings.Count);
        Assert.Equal("192.168.1.20", loaded.Devices[0].Ip);
        Assert.Equal("192.168.1.29", loaded.Devices[^1].Ip);
        Assert.Equal(0x1000, loaded.Devices[0].DiStartAddress);
        Assert.Equal(0x2000, loaded.Devices[0].DoStartAddress);
    }

    [Fact]
    public void MainVm_BadIpRealMode_DoesNotCrashAndShowsErrorState()
    {
        using var scope = new TestScope();
        // Simulate=0 → 真机总线；IP 不通时连接失败但程序不崩（错误态展示）
        var config = IoPointTableStore.BuildDefaultTemplate("T", "127.0.0.1", 1, 1);
        config.Devices[0].Port = 1;                 // 立即拒绝，不等超时
        config.Devices[0].TimeoutMs = 300;
        IoPointTableStore.Save(config);

        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        // BuildSlots 下限 10×3：1 架 1 层配置也铺 30 张占位卡（未绑定显示"—"）
        Assert.Equal(30, vm.Slots.Count);
        vm.Dispose();
    }

    [Fact]
    public void MainVm_SingleRack_SingleGroupNoHeader()
    {
        // 单架回归：分组数=1、标题条隐藏（主窗视觉与旧版完全一致）
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.Equal(1, vm.GroupCount);
        var group = Assert.Single(vm.RackGroups);
        Assert.False(group.IsHeaderVisible);
        Assert.Equal(30, group.Slots.Count);
    }

    [Fact]
    public void MainVm_TwoRackDevices_Builds60Slots_TwoGroupsWithInRackCodes()
    {
        // 双架（V0.0.20）：两台耦合器（架 1~10 + 架 11~20）→ 60 卡、两组上下堆叠；
        // 平铺 Slots 行优先契约不破坏（Slots[30]=架11层1）；第二架库位编码按
        // 架内站号换算回 A01 起（库位标签贴每台架自己的仓位上）
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var d1 = IoPointTableStore.BuildDefaultTemplate("1号架", "127.0.0.1", 10, 3, 1);
        var d2 = IoPointTableStore.BuildDefaultTemplate("2号架", "127.0.0.2", 10, 3, 11);
        IoPointTableStore.Save(new PointTableConfig { Devices = { d1.Devices[0], d2.Devices[0] } });

        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.Equal(60, vm.Slots.Count);
        Assert.Equal(20, vm.RackCount);
        Assert.Equal(2, vm.GroupCount);
        Assert.Equal(2, vm.RackGroups.Count);
        Assert.Equal("1号固化架", vm.RackGroups[0].Title);
        Assert.Equal("2号固化架", vm.RackGroups[1].Title);
        Assert.All(vm.RackGroups, g => Assert.True(g.IsHeaderVisible));
        Assert.Equal(10, vm.RackGroups[0].Columns);
        Assert.Equal(3, vm.RackGroups[0].Rows);

        // 平铺顺序契约延续（先层后架——整层 20 站连续，与单架"行=层"口径一致）：
        // Slots[0]=(架1,层1)、Slots[19]=(架20,层1)、Slots[30]=(架11,层2)、Slots[59]=(架20,层3)
        Assert.Equal(1, vm.Slots[0].Level);
        Assert.Equal(20, vm.Slots[19].Rack);
        Assert.Equal(1, vm.Slots[19].Level);
        Assert.Equal(11, vm.Slots[30].Rack);
        Assert.Equal(2, vm.Slots[30].Level);
        Assert.Equal(20, vm.Slots[59].Rack);
        Assert.Equal(3, vm.Slots[59].Level);

        // 分组切分：组 0=架 1~10、组 1=架 11~20，组内行优先（先层后架）
        Assert.Equal(1, vm.RackGroups[0].Slots[0].Rack);
        Assert.Equal(10, vm.RackGroups[0].Slots[9].Rack);
        Assert.Equal(2, vm.RackGroups[0].Slots[10].Level);   // 组 0 第二排=中层（行优先同平铺契约）
        Assert.Equal(11, vm.RackGroups[1].Slots[0].Rack);
        Assert.Equal(12, vm.RackGroups[1].Slots[1].Rack);
        Assert.Equal(11, vm.RackGroups[1].Slots[10].Rack);   // 组 1 第二排首格=架 11
        Assert.Equal(2, vm.RackGroups[1].Slots[10].Level);
        Assert.Equal(20, vm.RackGroups[1].Slots[29].Rack);
        Assert.All(vm.RackGroups.SelectMany(g => g.Slots), s => Assert.Same(
            vm.Slots.First(x => x.Rack == s.Rack && x.Level == s.Level), s)); // 同批实例

        // 库位编码：第二架架内换算（架 11→A01、架 12→B02、架 20 层 3→J12）
        Assert.Equal("A01", vm.RackGroups[1].Slots[0].Code);
        Assert.Equal("B02", vm.RackGroups[1].Slots[1].Code);
        Assert.Equal("J12", vm.RackGroups[1].Slots[29].Code);
        // 平铺位置编码同源（Slots[31]=架12层2 → 对角编码 B03）
        Assert.Equal("B03", vm.Slots[31].Code);
        // 第一架编码不变——单架回归锚点见 MainVm_Slots_RowMajorOrder（平铺下标随总架数变化，此处按 (架,层) 断言）
        Assert.Equal("A01", vm.Slots.First(s => s.Rack == 1 && s.Level == 1).Code);
        Assert.Equal("J12", vm.Slots.First(s => s.Rack == 10 && s.Level == 3).Code);
    }

    // ---------- 点表配置 VM ----------

    [Fact]
    public void PointTableVm_GenerateTemplate_Fills185BindingsWithBeacon()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.TemplateIp = "192.168.1.20";
        vm.TemplateRacks = "10";
        vm.TemplateLevels = "3";

        vm.GenerateTemplateCommand.Execute(null);

        var dev = Assert.Single(vm.Devices);
        Assert.Equal(10, dev.BlockCount);
        // 单台模板带塔灯（V0.0.66）：180 仓位点 + 机台总监塔灯五件套（位 11~15）
        Assert.Equal(185, dev.Bindings.Count);
        var beacons = dev.Bindings.Where(b => b.Rack == 0 && b.Level == 0).ToList();
        Assert.Equal(5, beacons.Count);
        Assert.All(beacons, b => Assert.False(b.IsInput));
        Assert.Equal([11, 12, 13, 14, 15], beacons.Select(b => b.Bit).OrderBy(b => b).ToArray());
        Assert.Equal(
            [IoPointRole.BeaconRed, IoPointRole.BeaconYellow, IoPointRole.BeaconGreen,
                IoPointRole.BeaconBlue, IoPointRole.Buzzer],
            beacons.OrderBy(b => b.Bit).Select(b => b.Role).ToArray());
        Assert.Contains(fake.Calls, c => c.Contains("缺省模板已生成"));
    }

    [Fact]
    public void PointTableVm_SaveValidationFailure_BlocksClose()
    {
        var fake = new FakeCureDialogService();
        using var scope = new TestScope();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        // 空设备列表（没生成模板也没添加）→ 校验必拦：至少一台
        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("保存失败"));
    }

    [Fact]
    public void PointTableVm_SaveValid_WritesConfigFile()
    {
        using var scope = new TestScope();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateTemplateCommand.Execute(null);   // 现成合法模板

        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = IoPointTableStore.Load();
        Assert.NotNull(loaded);
        // 单台模板带塔灯（V0.0.66）：185 点（180 仓位点 + 机台总监塔灯 5 路）；回读幂等不叠加
        Assert.Equal(185, loaded!.Devices[0].Bindings.Count);
    }

    [Fact]
    public void PointTableVm_GenerateMachine_BuildsDevicesWithSteppedIps()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.BatchCountText = "3";
        vm.BatchIpFirstText = "192.168.1.20";
        vm.BatchIpStepText = "1";

        vm.GenerateMachineCommand.Execute(null);    // ConfirmResult 缺省 true → 确认通过

        Assert.Equal(3, vm.Devices.Count);
        Assert.Equal("192.168.1.20", vm.Devices[0].Ip);
        Assert.Equal("192.168.1.22", vm.Devices[^1].Ip);
        Assert.Equal(18, vm.Devices[0].Bindings.Count);  // 1 架 3 层仓位点
        Assert.Equal(23, vm.Devices[1].Bindings.Count);  // 塔灯宿主台：18 + 机台总监灯 5 路（自动档）
        Assert.Equal(18, vm.Devices[2].Bindings.Count);
        Assert.Equal("IO耦合器1", vm.Devices[0].Name);
        Assert.Contains(fake.Calls, c => c.Contains("批量生成完成"));
    }

    [Fact]
    public void PointTableVm_GenerateMachine_CancelConfirm_KeepsDevices()
    {
        var fake = new FakeCureDialogService { ConfirmResult = false };
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.AddDeviceCommand.Execute(null);          // 先手工加一台，验证不被覆盖清掉
        vm.BatchCountText = "3";

        vm.GenerateMachineCommand.Execute(null);

        Assert.Single(vm.Devices);
        Assert.Contains(fake.Calls, c => c.StartsWith("Confirm:批量生成"));
    }

    [Fact]
    public async Task PointTableVm_TestAll_ReportsPerDeviceResult()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        // 本机回环 + 端口 1（必然拒绝）+ 300ms 超时：两台测试亚秒级完成
        vm.DeviceIp = "127.0.0.1";
        vm.EditDevicePort = "1";
        vm.EditDeviceTimeoutMs = "300";
        vm.AddDeviceCommand.Execute(null);
        vm.Devices[0].Port = 1;                     // 设备1 直接改 Raw（TestAll 只刷选中设备编辑区）
        vm.Devices[0].TimeoutMs = 300;
        vm.DeviceName = "测试B";
        vm.AddDeviceCommand.Execute(null);          // 设备2（选中态）：TestAll 会把编辑区值刷进它
        Assert.Equal(2, vm.Devices.Count);

        vm.TestAllCommand.Execute(null);
        // 逐台后台跑（每台连接拒绝快速失败），轮询等 IsTesting 落回 false
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (vm.IsTesting && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(vm.IsTesting);
        Assert.Equal(2, vm.TestResults.Count);
        Assert.All(vm.TestResults, r => Assert.False(r.Ok));
    }

    [Fact]
    public void PointTableVm_InlineEdit_WritesThroughToRaw()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateTemplateCommand.Execute(null);

        // 表格直编（V0.0.45；点号 V0.0.47 开放）：行 VM 属性写穿 Raw 所见即所得——
        // 角色/功能名/架/层/块/点号/方向
        var row = vm.Bindings.First(b => b.Raw.IsInput);
        row.Role = IoPointRole.TrayDetect;
        row.FunctionName = "手动改名";
        row.Rack = 3;
        row.Level = 2;
        row.Block = 2;
        row.Bit = 7;
        row.IsInput = false;

        Assert.Equal(IoPointRole.TrayDetect, row.Raw.Role);
        Assert.Equal("手动改名", row.Raw.FunctionName);
        Assert.Equal(3, row.Raw.Rack);
        Assert.Equal(2, row.Raw.Level);
        Assert.Equal(2, row.Raw.Block);
        Assert.Equal(7, row.Raw.Bit);      // 点号写穿（现场换接线挪备用通道的 UI 入口）
        Assert.False(row.Raw.IsInput);
        // 仓位列跟随架/层刷新
        Assert.Equal("架3层2", row.SlotText);
    }

    [Fact]
    public void PointTableVm_SelectedDeviceChange_SyncsDeviceEditor()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateMachineCommand.Execute(null);   // 批量生成 10 台（IP .20~.29）

        // V0.0.45 修的 bug：切换设备后设备名/IP 编辑区必须跟随回显
        // （此前 setter 漏调 SyncDeviceEditor，编辑区停在上台设备的值）
        var target = vm.Devices[3];   // IO耦合器4（192.168.1.23）
        vm.SelectedDevice = target;

        Assert.Equal(target.Name, vm.DeviceName);
        Assert.Equal(target.Ip, vm.DeviceIp);
        Assert.Equal("502", vm.EditDevicePort);
        Assert.Contains("IO耦合器4", vm.CurrentDeviceText);

        // 换回第一台同样跟随
        vm.SelectedDevice = vm.Devices[0];
        Assert.Equal(vm.Devices[0].Name, vm.DeviceName);
        Assert.Equal(vm.Devices[0].Ip, vm.DeviceIp);
    }

    [Fact]
    public void PointTableVm_AddAndDeleteBinding_SyncsBothCollections()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateTemplateCommand.Execute(null);
        int rawBefore = vm.SelectedDevice!.Bindings.Count;
        int rowsBefore = vm.Bindings.Count;

        // ＋添加行：Raw 集合与行集合双边同步加，新行选中（预留位缺省值）
        vm.AddBindingCommand.Execute(null);
        Assert.Equal(rawBefore + 1, vm.SelectedDevice.Bindings.Count);
        Assert.Equal(rowsBefore + 1, vm.Bindings.Count);
        Assert.Equal(IoPointRole.Reserved, vm.SelectedBinding!.Raw.Role);
        Assert.Equal(3, vm.SelectedBinding.Raw.Bit);

        // 表格里直改新行（写穿 Raw）后删除：双边同步删、选中替代行
        vm.SelectedBinding.Role = IoPointRole.LampRed;
        Assert.Equal(IoPointRole.LampRed, vm.SelectedBinding.Raw.Role);
        var deletedRow = vm.SelectedBinding;
        vm.DeleteBindingCommand.Execute(null);
        Assert.Equal(rawBefore, vm.SelectedDevice.Bindings.Count);
        Assert.Equal(rowsBefore, vm.Bindings.Count);
        // 被删的行两集合都不在了（选中行是替代行，属集合内正常成员）
        Assert.DoesNotContain(vm.Bindings, b => ReferenceEquals(b.Raw, deletedRow.Raw));
        Assert.DoesNotContain(vm.SelectedDevice.Bindings, b => ReferenceEquals(b, deletedRow.Raw));
        Assert.NotSame(deletedRow, vm.SelectedBinding);
    }

    [Fact]
    public void PointTableVm_RoleOptions_TextIsChineseButValueStaysEnum()
    {
        // 角色下拉显示中文化（V0.0.37）：Text=中文文案与左侧表格"角色"列同一来源
        // IoPointRoleInfo.ToText（两处永远同口径）；Value=英文枚举——落盘 JSON /
        // 校验 / 业务键全链不改，只改显示
        var options = CureStation.ViewModels.PointTableViewModel.RoleOptions;
        Assert.Equal(Enum.GetValues<IoPointRole>().Length, options.Count);
        Assert.All(options, o => Assert.Equal(IoPointRoleInfo.ToText(o.Value), o.Text));
        for (int i = 0; i < options.Count; i++)
            Assert.Equal((IoPointRole)i, options[i].Value);
    }

    // ---------- 点号显示形态（V0.0.48：单下拉六形态，显示/编辑两口径） ----------

    private static readonly CureStation.ViewModels.PointTableViewModel.BitDisplayStyle
        StyleXY = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.TerminalXY,
        StyleIQ = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.TerminalIQ,
        StyleHex = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.Hex0x,
        StyleDec = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.Dec,
        StyleOct = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.Oct,
        StyleBin = CureStation.ViewModels.PointTableViewModel.BitDisplayStyle.Bin;

    [Fact]
    public void PointTableVm_BitDisplay_SixStyles_TerminalXYIsDefault()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var vm = new CureStation.ViewModels.PointTableViewModel(new FakeCureDialogService());
        vm.GenerateTemplateCommand.Execute(null);
        var input = vm.Bindings.First(b => b.IsInput);
        var output = vm.Bindings.First(b => !b.IsInput);

        // 缺省=端子号 X/Y（GFNB 丝印口径）：输入 X+hex、输出 Y+hex
        Assert.Equal(StyleXY, vm.BitStyle);
        input.Raw.Bit = 10;
        output.Raw.Bit = 15;
        Assert.Equal("XA", input.BitDisplay);
        Assert.Equal("YF", output.BitDisplay);

        // 六形态逐切（Bit 存储恒为十进制，只换显示）；IsInput 变更实时换前缀
        vm.BitStyle = StyleIQ;
        Assert.Equal("IA", input.BitDisplay);
        Assert.Equal("QF", output.BitDisplay);
        vm.BitStyle = StyleHex;
        Assert.Equal("0xA", input.BitDisplay);
        Assert.Equal("0xF", output.BitDisplay);
        vm.BitStyle = StyleDec;
        Assert.Equal("10", input.BitDisplay);
        Assert.Equal("15", output.BitDisplay);
        vm.BitStyle = StyleOct;
        input.Raw.Bit = 12;
        Assert.Equal("14", input.BitDisplay);            // 12 = 八进制 14
        vm.BitStyle = StyleBin;
        Assert.Equal("1100", input.BitDisplay);          // 12 = 二进制 1100

        // 事件广播：口径切换/方向变更 → 每行 BitDisplay 通知（XAML 绑定刷新源）
        int raises = 0;
        input.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(input.BitDisplay)) raises++; };
        vm.BitStyle = StyleXY;
        input.IsInput = !input.IsInput;                  // 前缀随方向换（Y→X 侧）
        Assert.Equal(2, raises);
    }

    [Fact]
    public void PointTableVm_BitEdit_ParsesVisibleForm_FallsBackOnGarbage()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var vm = new CureStation.ViewModels.PointTableViewModel(new FakeCureDialogService());
        vm.GenerateTemplateCommand.Execute(null);
        var input = vm.Bindings.First(b => b.IsInput);
        var output = vm.Bindings.First(b => !b.IsInput);

        // 端子号口径（缺省）：编辑=裸 hex 数字；"看到什么输什么"——带前缀也吃
        input.Raw.Bit = 10;
        Assert.Equal("A", input.BitEdit);                // 裸数字（前缀是展示口径）
        input.BitEdit = "B";
        Assert.Equal(11, input.Raw.Bit);
        input.BitEdit = "XB";                            // 带本行方向前缀直输
        Assert.Equal(11, input.Raw.Bit);
        Assert.Equal("XB", input.BitDisplay);

        // 输出行剥 Y 前缀；串行的 X 不接（防手滑写串行→值原地不动）
        output.Raw.Bit = 15;
        Assert.Equal("F", output.BitEdit);
        output.BitEdit = "Y7";
        Assert.Equal(7, output.Raw.Bit);
        output.BitEdit = "X7";                           // X≠Y 行前缀 → 解析失败弹回
        Assert.Equal(7, output.Raw.Bit);
        Assert.Equal("7", output.BitEdit);

        // 垃圾输入（非数字/负数）：值原地不动，编辑框弹回原显示
        input.Raw.Bit = 9;
        input.BitEdit = "GG";
        Assert.Equal(9, input.Raw.Bit);
        Assert.Equal("9", input.BitEdit);
        input.BitEdit = "-3";                            // HexNumber 无符号 + bit>=0 双拒
        Assert.Equal(9, input.Raw.Bit);
        input.BitEdit = "1F";                            // 越界（31≥16）行内放行写穿——保存前校验统一拦
        Assert.Equal(31, input.Raw.Bit);

        // 0x/0o/0b 显式前缀跨口径（现场复制文本宽容）；且优先于当前口径
        vm.BitStyle = StyleDec;
        Assert.Equal("31", input.BitEdit);
        input.BitEdit = "0x1A";
        Assert.Equal(26, input.Raw.Bit);
        vm.BitStyle = StyleOct;
        input.BitEdit = "17";
        Assert.Equal(15, input.Raw.Bit);
        vm.BitStyle = StyleBin;
        input.BitEdit = "1010";
        Assert.Equal(10, input.Raw.Bit);
    }

    [Fact]
    public void PointTableVm_BitStyle_SingleIniKey_PersistsAndHealsBadValue()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var vm = new CureStation.ViewModels.PointTableViewModel(new FakeCureDialogService());
        Assert.Equal(StyleXY, vm.BitStyle);              // 缺省端子号 X/Y

        vm.BitStyle = StyleDec;
        Assert.Equal("Dec", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "PointBitStyle", ""));

        var vm2 = new CureStation.ViewModels.PointTableViewModel(new FakeCureDialogService());
        Assert.Equal(StyleDec, vm2.BitStyle);            // 重开窗记住选择

        // 手编 ini 写坏值：自愈回缺省（Theme 同款口径），不炸不猜档
        IniFile.Write(AppPaths.ConfigIni, "setting", "PointBitStyle", "zzz");
        var vm3 = new CureStation.ViewModels.PointTableViewModel(new FakeCureDialogService());
        Assert.Equal(StyleXY, vm3.BitStyle);
    }

    [Fact(DisplayName = "顶栏连接短词：起停/配置/演示/连通/部分台/错误详情六态抓重点")]
    public void MainVm_DeriveConnectionShort_CoversKeyStates()
    {
        string Short(bool c, string s) =>
            CureStation.ViewModels.MainViewModel.DeriveConnectionShort(c, s);
        Assert.Equal("未启动", Short(false, "未启动"));
        Assert.Equal("已停止", Short(false, "已停止"));
        Assert.Equal("未配置设备", Short(false, "未配置设备"));
        Assert.StartsWith("演示模式", Short(true, "演示模式（2 台配置）"));
        Assert.Equal("已连接", Short(true, "1/1 台已连"));
        Assert.Equal("已连接", Short(true, "A线OP30固化架IO耦合器 192.168.1.20:502"));
        Assert.Equal("2/3 台已连", Short(false, "2/3 台已连"));     // 部分台在线——进度口径有信息量
        Assert.Equal("未连接", Short(false, "0/1 台已连"));         // 全断与灯点红色同拍
        Assert.Equal("未连接",
            Short(false, "A线OP30固化架IO耦合器 192.168.1.20:502 连接超时（3000ms），请检查 IP/端口"));
    }

    // ---------- 工艺策略 VM（P3.1 状态机配套） ----------

    [Fact]
    public void PolicyVm_LoadFromDisk_SelectsActivePolicy()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例断言按分钟字面编写，显式写盘铺分口径
        // （策略 VM 构造 LoadTimeUnit 以盘上 ini 为准，仅改内存静态无效）
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var book = new CuringPolicyBook
        {
            Policies =
            {
                new CuringPolicy { Name = "甲" },
                new CuringPolicy { Name = "乙", GlobalMinutes = 30 },
            },
            ActiveName = "乙",
        };
        CuringPolicyStore.Save(book);

        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 自愈补回「自定义」到队首（书里没有它——恒存在自愈，V0.0.57）；
        // 缺省选中仍是激活策略"乙"（不受自愈影响）。
        Assert.Equal(3, vm.Policies.Count);
        Assert.Equal("自定义", vm.Policies[0].Name);
        Assert.Equal("乙", vm.SelectedPolicy!.Name);  // 缺省选中激活策略
        Assert.Equal("30", vm.EditGlobalMinutes);     // 编辑区已回显
    }

    [Fact]
    public void PolicyVm_Save_WritesOverridesToDisk()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        vm.EditGlobalMinutes = "90";
        vm.EditLevel2Min = "45";
        vm.EditPersistMinutes = true;   // 断电续算勾选 → 落盘往返
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "3",
            Level = "2",
            Minutes = "20",
        });
        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        var p = loaded!.Policies[0];
        Assert.Equal(90, p.GlobalMinutes);
        Assert.Equal(45, p.LevelMinutes[2]);
        Assert.Equal(20, p.SlotMinutes["3-2"]);
        Assert.True(p.PersistMinutes);
    }

    [Fact]
    public void PolicyVm_SaveBadNumber_BlocksClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.EditGlobalMinutes = "abc";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("数字格式"));
    }

    [Fact]
    public void PolicyVm_Activate_SetsActiveNameOnDisk()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.AddPolicyCommand.Execute(null);   // 新策略（自动选中）
        vm.EditName = "快固";
        vm.EditGlobalMinutes = "30";

        vm.ActivateCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("快固", loaded!.ActiveName);
        // 缺省库 6 策略（1 自定义 + 5 预置，V0.0.20 三轮）+ 本用例新增 1
        Assert.Equal(7, loaded.Policies.Count);
    }

    // ---------- 画布点选编辑（V0.0.43：点格子直接改时长 + 特殊格蓝色识别） ----------

    /// <summary>按架/层取画布格子（测试辅助）。</summary>
    private static CureStation.ViewModels.SlotPreviewItem Cell(
        CureStation.ViewModels.CuringPolicyViewModel vm, int rack, int level) =>
        vm.SlotPreviewItems.First(x => x.Rack == rack && x.Level == level);

    [Fact]
    public void PolicyVm_CanvasSelectSingleAndApply_WritesSlotRow()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 单击 3-2 → 浮层数据就绪 → 输 45 应用
        vm.ReplaceSelection(new[] { Cell(vm, 3, 2) });
        Assert.True(vm.HasSelection);
        Assert.True(Cell(vm, 3, 2).IsSelected);
        Assert.Contains("架3-层2", vm.SelectionSummaryText);
        vm.SelectionMinutesText = "45";
        vm.ApplySelectionCommand.Execute(null);

        var row = Assert.Single(vm.SlotRows, r => r.Rack == "3" && r.Level == "2");
        Assert.Equal("45", row.Minutes);
        // 行通知自动重建：格子时长/特殊标识即刷（45 分 + 单独设置过）
        var cell = Cell(vm, 3, 2);
        Assert.Equal(45, cell.Minutes);
        Assert.True(cell.IsSpecial);
        Assert.Contains("特殊仓位", cell.Tip);
        // 选择保持（连续改不丢），输入已清空
        Assert.True(vm.HasSelection);
        Assert.Equal("", vm.SelectionMinutesText);
    }

    [Fact]
    public void PolicyVm_CanvasApplyMulti_UpdatesExistingRow()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        // 已有手填行 3-2=20（画布应用应更新它而不是加重复行）
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "3",
            Level = "2",
            Minutes = "20",
        });
        int rowsBefore = vm.SlotRows.Count;

        // 框选两格（3-2 已有行 / 7-1 无行）批量应用 60
        vm.ReplaceSelection(new[] { Cell(vm, 3, 2), Cell(vm, 7, 1) });
        Assert.Contains("等 2 格", vm.SelectionSummaryText);
        vm.SelectionMinutesText = "60";
        vm.ApplySelectionCommand.Execute(null);

        Assert.Equal("60", vm.SlotRows.First(r => r.Rack == "3" && r.Level == "2").Minutes);
        Assert.Single(vm.SlotRows, r => r.Rack == "3" && r.Level == "2");   // 无重复行
        Assert.Single(vm.SlotRows, r => r.Rack == "7" && r.Level == "1");   // 新增一行
        Assert.Equal(rowsBefore + 1, vm.SlotRows.Count);
        Assert.Equal(60, Cell(vm, 7, 1).Minutes);
        Assert.True(Cell(vm, 7, 1).IsSpecial);
    }

    [Fact]
    public void PolicyVm_CanvasApplyInvalidInput_ShowsMessageAndBlocks()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.ReplaceSelection(new[] { Cell(vm, 1, 1) });

        vm.SelectionMinutesText = "abc";
        vm.ApplySelectionCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("≥1"));
        Assert.Empty(vm.SlotRows);

        vm.SelectionMinutesText = "0";
        vm.ApplySelectionCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("≥1"));
        Assert.Empty(vm.SlotRows);

        // 空输入静默忽略（误触回车不弹框）
        vm.SelectionMinutesText = "";
        vm.ApplySelectionCommand.Execute(null);
        Assert.Empty(vm.SlotRows);
        Assert.False(vm.CloseDialog);
    }

    [Fact]
    public void PolicyVm_CanvasToggleAndClear_ManagesSelection()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // Ctrl+单击两次：第一次加选、第二次减选（回到无选择）
        vm.ToggleItem(Cell(vm, 2, 1));
        Assert.True(vm.HasSelection);
        vm.ToggleItem(Cell(vm, 2, 1));
        Assert.False(vm.HasSelection);

        // 选择 → 点空白/清空：高亮全清
        vm.ReplaceSelection(new[] { Cell(vm, 1, 1), Cell(vm, 2, 2), Cell(vm, 3, 3) });
        Assert.True(vm.HasSelection);
        vm.ClearSelection();
        Assert.False(vm.HasSelection);
        Assert.All(vm.SlotPreviewItems, x => Assert.False(x.IsSelected));
    }

    [Fact]
    public void PolicyVm_RebuildPreview_RestoresSelection()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.ReplaceSelection(new[] { Cell(vm, 4, 2) });

        // 改编辑区任一字段触发 RebuildPreview（格子对象全新重建）——选中态按键恢复
        vm.EditGlobalMinutes = "100";
        Assert.True(vm.HasSelection);
        Assert.True(Cell(vm, 4, 2).IsSelected);
    }

    [Fact]
    public void PolicyVm_CanvasApplySelectAll_PromotesToGlobal()
    {
        // 画布全选应用 = 改全局（用户实测报障：全选格子应用时长后，标题栏全局框
        // 不同步——V0.0.55 起"每格同值"在语义上就等价设全局，数据归一不铺覆盖行）。
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        // 先制造"全局 120 + 两处覆盖（仓位行 + 手填 SlotRow）"的脏状态
        vm.EditGlobalMinutes = "120";
        vm.EditLevel2Min = "90";
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "3",
            Level = "2",
            Minutes = "20",
        });
        Assert.Equal(90, Cell(vm, 1, 2).Minutes);      // 层覆盖生效中

        // 框选全部格子（整画布铺满）统一应用 1440 分（=24 时）
        vm.ReplaceSelection(vm.SlotPreviewItems);
        Assert.Equal(vm.RackCount * vm.LevelCount, vm.SlotPreviewItems.Count(x => x.IsSelected));
        vm.SelectionMinutesText = "1440";
        vm.ApplySelectionCommand.Execute(null);

        // 编辑区全局框同步（24 时=1440 分），层覆盖框清空
        Assert.Equal("1440", vm.EditGlobalMinutes);
        Assert.Equal("", vm.EditLevel2Min);
        // 仓位覆盖清光（含手填 3-2 行），数据归一不再有特殊格
        Assert.Empty(vm.SlotRows);
        Assert.All(vm.SlotPreviewItems, x => { Assert.Equal(1440, x.Minutes); Assert.False(x.IsSpecial); });
        // 选择保持 + 输入清空（同单格应用的手感）
        Assert.True(vm.HasSelection);
        Assert.Equal("", vm.SelectionMinutesText);
    }

    [Fact]
    public void PolicyVm_CanvasApplySelectAll_HoursUnits_SyncsGlobalBox()
    {
        // 全选升格走 FormatMinutes：小时单位下全局框应写"24"（单位显示口径），
        // 底层仍归一为 1440 分钟——两处数字一致且入库存分钟。
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Hours;   // 页面口径=时

        vm.ReplaceSelection(vm.SlotPreviewItems.ToList());
        vm.SelectionMinutesText = "24";
        vm.ApplySelectionCommand.Execute(null);

        Assert.Equal("24", vm.EditGlobalMinutes);      // 全局框=24 时（与画布同口径）
        Assert.All(vm.SlotPreviewItems, x => Assert.Equal(1440, x.Minutes));   // 底层分钟
        Assert.Empty(vm.SlotRows);
    }

    [Fact]
    public void PolicyVm_CanvasApplyPartial_NeverTouchesGlobal()
    {
        // 非全选（≤ 格子总数）照旧写仓位覆盖行，全局框不动——升格只认"铺满画布"。
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.EditGlobalMinutes = "120";

        vm.ReplaceSelection(new[] { Cell(vm, 3, 2), Cell(vm, 7, 1) });
        vm.SelectionMinutesText = "60";
        vm.ApplySelectionCommand.Execute(null);

        Assert.Equal("120", vm.EditGlobalMinutes);     // 全局不动
        Assert.Single(vm.SlotRows, r => r.Rack == "3" && r.Level == "2");
        Assert.Single(vm.SlotRows, r => r.Rack == "7" && r.Level == "1");
        Assert.Equal(60, Cell(vm, 3, 2).Minutes);
    }

    [Fact]
    public void PolicyVm_IsSpecial_TrueOnlyForOverrideSlots_EvenWhenEqual()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        // 全局 45；再给 5-1 单独配 45（覆盖值=全局值）——真识别按覆盖字典判，
        // 5-1 仍是特殊格（蓝色），未配覆盖的 3-1（同为 45 分）不是
        vm.EditGlobalMinutes = "45";
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "5",
            Level = "1",
            Minutes = "45",
        });

        Assert.Equal(45, Cell(vm, 5, 1).Minutes);
        Assert.True(Cell(vm, 5, 1).IsSpecial);
        Assert.Equal(45, Cell(vm, 3, 1).Minutes);
        Assert.False(Cell(vm, 3, 1).IsSpecial);
    }

    [Fact]
    public void PolicyVm_ClearSelectionOverride_RestoresFollowLevel()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.EditLevel2Min = "90";   // 层覆盖 90（跟随目标）
        // 画布设 3-2=45（特殊格）
        vm.ReplaceSelection(new[] { Cell(vm, 3, 2) });
        vm.SelectionMinutesText = "45";
        vm.ApplySelectionCommand.Execute(null);
        Assert.True(Cell(vm, 3, 2).IsSpecial);
        Assert.Single(vm.SlotRows);

        // 浮层【清除】→ 覆盖行删除，格子回层覆盖 90（非特殊）
        vm.ClearSelectionOverrideCommand.Execute(null);
        Assert.Empty(vm.SlotRows);
        Assert.False(Cell(vm, 3, 2).IsSpecial);
        Assert.Equal(90, Cell(vm, 3, 2).Minutes);
        Assert.True(vm.HasSelection);   // 选择保持（可继续操作），输入已清
        Assert.Equal("", vm.SelectionMinutesText);
    }

    [Fact]
    public void MainVm_Simulate_ShowsActivePolicyText()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 策略自愈：缺省库落盘（V0.0.20 三轮起激活名="自定义"）+ 顶栏显示激活策略
        Assert.Contains("自定义", vm.PolicyText);
        Assert.True(File.Exists(CuringPolicyStore.DefaultPath));
    }

    [Fact]
    public void MainVm_CuringPolicySaved_ReloadsActivePolicy()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；顶栏/编辑链断言按分钟字面编写，写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        Assert.Contains("自定义", vm.PolicyText);   // 缺省库激活名（V0.0.20 三轮）

        // 登录管理员（策略窗有门禁：管理员+，未登录会被 Gate 拦下）→ 激活"快固"落盘 → 顶栏刷新
        fake.LoginResult = ("admin", OperatorRole.Admin);
        vm.LoginCommand.Execute(null);
        var book = new CuringPolicyBook
        {
            Policies =
            {
                new CuringPolicy { Name = "标准固化" },
                new CuringPolicy { Name = "快固", GlobalMinutes = 30 },
            },
            ActiveName = "快固",
        };
        CuringPolicyStore.Save(book);
        fake.CuringPolicyResult = true;
        vm.OpenCuringPolicyCommand.Execute(null);

        Assert.Contains("快固", vm.PolicyText);
        Assert.Contains(fake.Calls, c => c == "ShowCuringPolicy");
    }

    [Fact]
    public void MainVm_PolicyText_ShowsGlobalMinutes()
    {
        // V0.0.58：顶栏策略段带全局时长（"策略：快固 · 全局24小时"）——用户改了
        // 全局时长后 evidently 看到变化（此前顶栏只有名字，改参零反馈被误判"没有热更"）
        using var scope = new TestScope();
        EnsureConfigDir();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        try
        {
            IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
            var book = new CuringPolicyBook
            {
                Policies =
                {
                    new CuringPolicy { Name = "标准固化" },
                    new CuringPolicy { Name = "快固", GlobalMinutes = 1440 },
                },
                ActiveName = "快固",
            };
            CuringPolicyStore.Save(book);
            var fake = new FakeCureDialogService();
            using var vm = new CureStation.ViewModels.MainViewModel(fake);

            // 分钟口径：缺省 Minutes → "1440分"整串
            Assert.Equal("策略：快固 · 全局1440分", vm.PolicyText);

            // 切小时 → DurationUnit.Change 广播链顶栏同步换口径（智能省略零段）
            DurationUnit.Change(PolicyTimeUnit.Hours);
            Assert.Equal("策略：快固 · 全局24小时", vm.PolicyText);
        }
        finally
        {
            DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        }
    }

    // ---------- 登录与权限门禁（P3.2） ----------

    [Fact]
    public void MainVm_Gate_BlocksWithoutLogin()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // V0.0.95 用户拍板：未登录=设置整钮禁用+悬停提示（下拉全是管理功能）
        Assert.False(vm.IsSettingsEnabled);
        Assert.Contains("未登录", vm.SettingsToolTip);

        vm.OpenWorkOrderCommand.Execute(null);   // 未登录 → 拦下
        Assert.Contains(fake.Calls, c => c.Contains("未登录"));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("ShowWorkOrder"));

        vm.OpenAccountsCommand.Execute(null);   // 未登录 → 用户管理也拦
        Assert.DoesNotContain(fake.Calls, c => c == "ShowAccounts");
    }

    [Fact]
    public void MainVm_Login_AndRoleGate()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 操作员登录：工单可开；设置按钮整钮禁用；用户管理/点表/演示全拦
        fake.LoginResult = ("operator", OperatorRole.Operator);
        vm.LoginCommand.Execute(null);
        // 顶栏格式"操作员：operator"（角色名开头，不带 LEVEL 字样）
        Assert.Equal("操作员：operator", vm.OperatorText);
        Assert.False(vm.IsSettingsEnabled);     // operator → 设置按钮不可用（用户拍板）
        Assert.Contains("操作员", vm.SettingsToolTip); // 悬停提示给出原因（V0.0.95）

        vm.OpenWorkOrderCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.StartsWith("ShowWorkOrder:Operator"));

        vm.OpenAccountsCommand.Execute(null);
        vm.OpenPointTableCommand.Execute(null);
        vm.ToggleSimulateCommand.Execute(null);
        Assert.Equal(3, fake.Calls.Count(c => c.Contains("权限不足")));
        Assert.DoesNotContain(fake.Calls, c => c == "ShowAccounts");
        Assert.DoesNotContain(fake.Calls, c => c == "ShowPointTable");

        // 管理员登录：策略/IO/过账/演示/用户管理全放行；点表仍拦 + 菜单项隐藏
        fake.LoginResult = ("admin", OperatorRole.Admin);
        vm.LoginCommand.Execute(null);
        Assert.True(vm.IsSettingsEnabled);
        Assert.False(vm.ShowPointTableItem);    // admin 点表项直接没有（用户拍板）
        vm.OpenAccountsCommand.Execute(null);
        Assert.Equal(OperatorRole.Admin, fake.AccountActor);
        vm.OpenPointTableCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("权限不足"));
        Assert.DoesNotContain(fake.Calls, c => c == "ShowPointTable");

        // 开发者登录：点表放行 + 菜单项可见
        fake.LoginResult = ("dev", OperatorRole.Dev);
        vm.LoginCommand.Execute(null);
        Assert.True(vm.ShowPointTableItem);
        vm.OpenPointTableCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowPointTable");
    }

    [Fact(DisplayName = "扫码枪配置：仅 dev 可见可开，保存后主窗热更新")]
    public void MainVm_ScannerConfig_OnlyDevAndHotApplies()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 启动期未启用（TestScope 统一预写 [Scanner] Enabled=0 的测试护栏）：
        // 补"跳过原因"边沿日志——此前 Enabled=0 时完全静默，现场"胶囊=未启用 +
        // 日志无提示"排障无头（本行文案与 ScannerOptions.SkipNotEnabledText
        // 同源；VM 层不复制文案，只断言前缀段）。
        Assert.Contains(LocalLogService.GetRecentLines(),
            l => l.Contains("扫码枪：未启用（config.ini [Scanner] Enabled=0）"));

        // 未登录：菜单项不显示 + 命令被 Gate 拦（先提示登录）
        Assert.False(vm.ShowScannerConfigItem);
        vm.OpenScannerConfigCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("未登录"));
        Assert.DoesNotContain(fake.Calls, c => c == "ShowScannerConfig");

        // operator / admin：菜单项都不显示；admin 命令被权限拦
        fake.LoginResult = ("operator", OperatorRole.Operator);
        vm.LoginCommand.Execute(null);
        Assert.False(vm.ShowScannerConfigItem);

        fake.LoginResult = ("admin", OperatorRole.Admin);
        vm.LoginCommand.Execute(null);
        Assert.False(vm.ShowScannerConfigItem);
        vm.OpenScannerConfigCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("权限不足"));
        Assert.DoesNotContain(fake.Calls, c => c == "ShowScannerConfig");

        // dev：菜单项显示 + 保存后主窗按新配置热重建（TestScope 预写 Enabled=0 → 未启用）
        fake.LoginResult = ("dev", OperatorRole.Dev);
        vm.LoginCommand.Execute(null);
        Assert.True(vm.ShowScannerConfigItem);
        fake.ScannerConfigResult = true;
        vm.OpenScannerConfigCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowScannerConfig");
        Assert.Contains(fake.Calls, c => c.Contains("扫码枪配置已保存并生效"));
        Assert.Equal("扫码：未启用", vm.ScanText);
    }

    // ---------- 日志悬浮窗 + 主题切换（方案 D · 双主题，2026-10-29） ----------

    [Fact(DisplayName = "ShowLog 门禁：未登录先拦")]
    public void MainVm_ShowLog_BlocksWithoutLogin()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        vm.ShowLogCommand.Execute(null);   // 未登录 → Gate 拦（L1 起）
        Assert.DoesNotContain(fake.Calls, c => c == "ShowLog");
        Assert.Contains(fake.Calls, c => c.Contains("未登录"));
    }

    [Fact(DisplayName = "ShowLog：登录后放行 + 快照含日志目录与仓位统计")]
    public void MainVm_ShowLog_PassesWithSnapshot()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        fake.LoginResult = ("operator", OperatorRole.Operator);  // 登录即可开（只读不碰配置）
        vm.LoginCommand.Execute(null);
        vm.ShowLogCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowLog");
        Assert.NotNull(fake.LogSnapshot);
        Assert.NotNull(fake.LogSnapshot!.GeneratedAtText);
        Assert.Contains(fake.LogSnapshot.Rows, r => r.Label == "日志目录");
        Assert.Contains(fake.LogSnapshot.Rows, r => r.Label == "仓位" && r.Value.Contains("30"));
        Assert.Contains(fake.LogSnapshot.Rows, r => r.Label == "连接状态");
    }

    [Fact(DisplayName = "主题切换：翻目标 + 写回 ini + 文案跟随")]
    public void MainVm_ToggleTheme_SwitchesAndWritesIni()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        IniFile.Write(AppPaths.ConfigIni, "setting", "Theme", "Light");  // 起手 Light 态
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        try
        {
            Assert.Equal("深色", vm.ToggleThemeText); // 浅色下按钮显示目标"深色"
            vm.ToggleThemeCommand.Execute(null);
            Assert.Equal("Dark", CureStation.Themes.ThemeManager.Current);
            Assert.Equal("Dark", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Theme", "?"));  // 落盘记忆
            Assert.Equal("浅色", vm.ToggleThemeText); // 深色下按钮显示目标"浅色"

            vm.ToggleThemeCommand.Execute(null);
            Assert.Equal("Light", CureStation.Themes.ThemeManager.Current);
            Assert.Equal("Light", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Theme", "?"));
        }
        finally
        {
            // 恢复全局静态（ThemeManager.Current 是静态记账，防止污染其它用例）
            CureStation.Themes.ThemeManager.ApplyTheme("Light");
        }
    }

    // ---------- 演示模式开关（Mock 数据热切换，2026-10-30） ----------

    [Fact(DisplayName = "演示开关门禁：未登录拦下、操作员拦下（需管理员）")]
    public void MainVm_ToggleSimulate_GateBlocksBelowAdmin()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "0");   // 起手真机态
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        Assert.False(vm.IsSimulate);

        vm.ToggleSimulateCommand.Execute(null);   // 未登录 → Gate 拦
        Assert.Contains(fake.Calls, c => c.Contains("未登录"));
        Assert.False(vm.IsSimulate);
        Assert.Equal("0", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"));

        fake.LoginResult = ("operator", OperatorRole.Operator);   // 操作员不够（需管理员）
        vm.LoginCommand.Execute(null);
        vm.ToggleSimulateCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("权限不足"));
        Assert.False(vm.IsSimulate);
        Assert.Equal("0", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"));
        Assert.Equal("演示：关", vm.SimulateText);
    }

    [Fact(DisplayName = "演示开关：管理员切换写回 ini + 文案跟随 + 总线热切换")]
    public void MainVm_ToggleSimulate_WritesConfigAndSwitchesBus()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 起手真机态（Simulate=0）：IP 立即拒绝不等超时（同 BadIpRealMode 手法）
        var config = IoPointTableStore.BuildDefaultTemplate("T", "127.0.0.1", 1, 1);
        config.Devices[0].Port = 1;
        config.Devices[0].TimeoutMs = 300;
        IoPointTableStore.Save(config);
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "0");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        fake.LoginResult = ("admin", OperatorRole.Admin);
        vm.LoginCommand.Execute(null);

        // 关 → 开：Mock 总线接管（ConnectionSummary 带"演示模式"）
        vm.ToggleSimulateCommand.Execute(null);
        Assert.True(vm.IsSimulate);
        Assert.Equal("演示：开", vm.SimulateText);
        Assert.Equal("1", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"));
        Assert.Contains("演示模式", vm.ConnectionText);

        // 开 → 关：切回真机总线（ini 翻回 0；真总线后台重连不阻塞断言）
        vm.ToggleSimulateCommand.Execute(null);
        Assert.False(vm.IsSimulate);
        Assert.Equal("演示：关", vm.SimulateText);
        Assert.Equal("0", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"));

        // 防抖收尾：真总线刚起后台连接线程就 Dispose 有竞态残留（偶发撞串行下一用例），
        // 切回 Mock 态再交 Dispose——Mock 线程无 TCP 资源，干净收场
        vm.ToggleSimulateCommand.Execute(null);
        Assert.True(vm.IsSimulate);
    }

    [Fact(DisplayName = "演示开关初始态：与 ini 同源（=1 起手即开）")]
    public void MainVm_ToggleSimulate_InitialFromConfig()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        Assert.True(vm.IsSimulate);              // 构造即 Mock（顶栏按钮高亮态）
        Assert.Equal("演示：开", vm.SimulateText);
        Assert.Contains("演示", vm.ConnectionText);
    }

    // ---------- 工单窗 VM（P3.2） ----------

    [Fact]
    public void WorkOrderVm_Save_RoundTrip_AllFields()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin);

        vm.EditRtRcNo = "R-001";
        vm.EditRtPreset = "100";
        vm.EditRtProduced = "37";
        vm.EditLfRcNo = "L-002";
        vm.EditLfPreset = "80";
        vm.EditLfProduced = "12";
        vm.EditEmpNo = "EMP001";
        vm.EditBcNo = "BC-0001";
        vm.EditPtNo1 = "PT-A01";
        vm.EditPtNo2 = "PT-B02";
        vm.EditUploadEnabled = true;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = WorkOrderStore.Load()!;
        Assert.Equal("R-001", loaded.Right.RcNo);
        Assert.Equal(100, loaded.Right.PresetQty);
        Assert.Equal(37, loaded.Right.ProducedQty);
        Assert.Equal("L-002", loaded.Left.RcNo);
        Assert.Equal(12, loaded.Left.ProducedQty);
        Assert.Equal("EMP001", loaded.EmpNo);
        Assert.Equal("BC-0001", loaded.BcNo);
        Assert.Equal("PT-A01", loaded.PtNo1);
        Assert.Equal("PT-B02", loaded.PtNo2);
        Assert.True(loaded.UploadEnabled);
    }

    [Fact]
    public void WorkOrderVm_RoleGate_BlocksLowRoleEdits()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 预置工单：途程/数量/制程已有值
        WorkOrderStore.Save(new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "原途程", PresetQty = 50, ProducedQty = 5 },
            PtNo1 = "原制程",
        });
        var fake = new FakeCureDialogService();

        // 未登录：数量也锁（连清零都不给——日常录入以登录为准）
        var vm0 = new CureStation.ViewModels.WorkOrderViewModel(fake, null);
        Assert.False(vm0.CanEditDaily);
        Assert.False(vm0.CanEditRoute);
        Assert.False(vm0.CanEditProcess);

        // 操作员：条码/工号/数量/清零可录；途程/制程全锁（就算绕过 IsEnabled 也兜底回原值）
        var vm1 = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Operator);
        Assert.True(vm1.CanEditDaily);
        Assert.False(vm1.CanEditRoute);
        Assert.False(vm1.CanEditProcess);
        vm1.EditRtRcNo = "越权改";      // 绕过 IsEnabled 直改属性
        vm1.EditPtNo1 = "越权制程";
        vm1.EditRtPreset = "88";        // 数量是操作员权限
        vm1.EditRtProduced = "9";
        vm1.ClearCountLfCommand.Execute(null);   // L 侧清零（操作员可点）
        vm1.EditEmpNo = "EMP009";
        vm1.EditBcNo = "BC-9";
        vm1.SaveCommand.Execute(null);
        Assert.True(vm1.CloseDialog);
        var loaded1 = WorkOrderStore.Load()!;
        Assert.Equal("原途程", loaded1.Right.RcNo);   // 操作员改不动途程
        Assert.Equal("原制程", loaded1.PtNo1);        // 制程同样锁
        Assert.Equal(88, loaded1.Right.PresetQty);    // 数量可改
        Assert.Equal(9, loaded1.Right.ProducedQty);
        Assert.Equal(0, loaded1.Left.ProducedQty);    // 清零生效
        Assert.Equal("EMP009", loaded1.EmpNo);
        Assert.Equal("BC-9", loaded1.BcNo);

        // 管理员：途程/数量/制程全解锁
        var vm2 = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin);
        Assert.True(vm2.CanEditRoute);
        Assert.True(vm2.CanEditProcess);
    }

    [Fact]
    public void WorkOrderVm_SaveBadNumber_BlocksClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin);
        vm.EditRtPreset = "abc";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("数字格式"));
    }

    // ---------- 工位布局开关（单/双工位，DualSide） ----------

    [Fact]
    public void WorkOrderVm_DualSide_BothPanelsWithSideLabels()
    {
        // 双工位开关开（老口径）：R/L 双面板全显、标题与按钮文案带工位字样
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin,
            postOptions: new CuringMesOptions { DualSide = true, Side = "R" });

        Assert.True(vm.IsDualSide);
        Assert.True(vm.ShowRightPanel);
        Assert.True(vm.ShowLeftPanel);
        Assert.True(vm.ShowPanelHeaders); // 双工位：R/L 子标题显示（V0.0.93 布局）
        Assert.Equal("工单信息（R/L 双工位）", vm.WindowTitle);
        Assert.Equal("R 工位", vm.RightPanelTitle);
        Assert.Equal("L 工位", vm.LeftPanelTitle);
        Assert.Equal("R 计数清零", vm.ClearRText);
        Assert.Equal("L 计数清零", vm.ClearLText);
        Assert.Equal("上传 R 物料", vm.UploadRText);
        Assert.Equal("上传 L 物料", vm.UploadLText);
    }

    [Fact]
    public void WorkOrderVm_SingleSideR_RightPanelOnly_NoSideLabels()
    {
        // 单工位缺省（Side=R）：只显 R 侧、全界面文案不带 R/L；隐藏的 L 侧
        // 编辑数据原样往返（保存后 L 槽仍是盘上原值——单工位不动另一侧数据）
        using var scope = new TestScope();
        EnsureConfigDir();
        WorkOrderStore.Save(new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "R-KEEP", PresetQty = 10 },
            Left = new WorkOrderSlot { RcNo = "L-KEEP", PresetQty = 20, ProducedQty = 3 },
        });
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin,
            postOptions: new CuringMesOptions { DualSide = false, Side = "R" });

        Assert.False(vm.IsDualSide);
        Assert.True(vm.ShowRightPanel);
        Assert.False(vm.ShowLeftPanel);
        Assert.False(vm.ShowPanelHeaders); // 单工位：R/L 子标题隐藏（卡片标题已是"生产信息"）
        Assert.Equal("工单信息", vm.WindowTitle);
        Assert.Equal("计数清零", vm.ClearRText);
        Assert.Equal("物料上传", vm.UploadRText);
        Assert.Equal("R-KEEP", vm.EditRtRcNo);

        Assert.Equal("L-KEEP", vm.EditLfRcNo); // 隐藏侧照常回显（Flush 原样落回）
        vm.SaveCommand.Execute(null);
        var loaded = WorkOrderStore.Load()!;
        Assert.Equal("R-KEEP", loaded.Right.RcNo);
        Assert.Equal("L-KEEP", loaded.Left.RcNo);
        Assert.Equal(20, loaded.Left.PresetQty);
        Assert.Equal(3, loaded.Left.ProducedQty);
    }

    [Fact]
    public void WorkOrderVm_SingleSideL_LeftSlotAsMain()
    {
        // 单工位但工位=L：主面板用 Left 槽（Side 对应槽——报文口径与界面一致）；
        // 修改预设数量落回 Left，Right 槽数据原样不动
        using var scope = new TestScope();
        EnsureConfigDir();
        WorkOrderStore.Save(new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "R-KEEP", PresetQty = 10 },
            Left = new WorkOrderSlot { RcNo = "L-MAIN", PresetQty = 5 },
        });
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin,
            postOptions: new CuringMesOptions { DualSide = false, Side = "L" });

        Assert.False(vm.ShowRightPanel);
        Assert.True(vm.ShowLeftPanel);
        Assert.False(vm.ShowPanelHeaders);
        Assert.Equal("L-MAIN", vm.EditLfRcNo); // 视图↔槽映射恒定：L 视图=Left 槽

        vm.EditLfPreset = "66";
        vm.SaveCommand.Execute(null);
        var loaded = WorkOrderStore.Load()!;
        Assert.Equal(66, loaded.Left.PresetQty);
        Assert.Equal(10, loaded.Right.PresetQty); // Right 槽原样
    }

    // ---------- 用户管理窗 VM（V0.0.49 三级角色重做） ----------

    [Fact]
    public void AccountVm_AsAdmin_CreatesAndDeletesOperatorOnly()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("123456", "test-admin-pwd", "dev123");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.AccountManagerViewModel(
            fake, OperatorRole.Admin, store);

        // 种子三账号（dev/admin/operator），角色下拉只见"操作员"
        Assert.Equal(3, vm.Users.Count);
        Assert.Equal(["操作员"], vm.CreateRoleOptions);

        // 新增：弱密码拦下弹框（admin 视角角色下拉只有"操作员"——UI 层就选不出
        // "管理员/开发者"选项；Store 对 admin 建 admin 的兜底拦截另有 Store 层用例）
        vm.EditUserName = "op1";
        vm.EditPassword = "123";
        vm.AddUserCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("新增失败"));

        // 新增操作员成功 → 列表刷新为四个
        vm.EditPassword = "abc123";
        vm.AddUserCommand.Execute(null);
        Assert.Equal(4, vm.Users.Count);
        Assert.Contains(vm.Users, u => u.UserName == "op1" && u.RoleText == "操作员");

        // 删 admin 行：按钮不可用（CanDeleteSelected=false）+ 提示文案；Confirm 都不该弹
        vm.SelectedUser = vm.Users.First(u => u.UserName == "admin");
        Assert.False(vm.CanDeleteSelected());
        Assert.Contains("开发者", vm.SelectedActionHint);
        fake.Calls.Clear();
        vm.RemoveUserCommand.Execute(null);   // CanExecute=false，Command 直接无效
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("Confirm:删除"));

        // 删 op1 → Confirm 后删掉（剩三个）
        vm.SelectedUser = vm.Users.First(u => u.UserName == "op1");
        Assert.True(vm.CanDeleteSelected());
        vm.RemoveUserCommand.Execute(null);
        Assert.Equal(3, vm.Users.Count);
    }

    [Fact]
    public void AccountVm_AsDev_CanCreateAdminAndDeleteAdmin_DevUntouchable()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm-dev.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("123456", "test-admin-pwd", "dev123");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.AccountManagerViewModel(
            fake, OperatorRole.Dev, store);

        // 开发者视角：角色下拉两选
        Assert.Equal(["操作员", "管理员"], vm.CreateRoleOptions);

        // 选中自己（dev 行）：删除禁用，但重置密码可用（dev 改自己）
        vm.SelectedUser = vm.Users.First(u => u.UserName == "dev");
        Assert.False(vm.CanDeleteSelected());
        Assert.True(vm.ResetPasswordCommand.CanExecute(null));

        // 选中 admin 行：删除/重置都可用 —— 建第二个 admin 再删，全链验证
        vm.EditUserName = "a2";
        vm.EditRoleText = "管理员";
        vm.EditPassword = "abc123";
        vm.AddUserCommand.Execute(null);
        Assert.Equal(OperatorRole.Admin, store.Verify("a2", "abc123"));
        Assert.Equal(4, vm.Users.Count);
        vm.SelectedUser = vm.Users.First(u => u.UserName == "a2");
        Assert.True(vm.CanDeleteSelected());
        Assert.Equal("", vm.SelectedActionHint);
        vm.RemoveUserCommand.Execute(null);
        Assert.Equal(3, vm.Users.Count);
        Assert.Null(store.Verify("a2", "abc123"));    // 已删
    }

    [Fact]
    public void AccountVm_ResetPassword_AdminBoundaryAndSuccess()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm-pwd.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("123456", "test-admin-pwd", "dev123");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.AccountManagerViewModel(
            fake, OperatorRole.Admin, store);

        // admin 选中 dev 行：重置禁用（开发者密码只有开发者本人可改）
        vm.SelectedUser = vm.Users.First(u => u.UserName == "dev");
        Assert.False(vm.CanResetSelected());
        Assert.False(vm.ResetPasswordCommand.CanExecute(null));
        Assert.Contains("开发者", vm.SelectedActionHint);

        // admin 重置 operator：两次一致 → Confirm → 成功弹框 + 库里新密码可登录
        vm.SelectedUser = vm.Users.First(u => u.UserName == "operator");
        vm.NewPassword = "new-op-pwd";
        vm.NewPassword2 = "new-op-pwd";
        vm.ResetPasswordCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.StartsWith("Confirm:重置确认"));
        Assert.Contains(fake.Calls, c => c.Contains("重置成功"));
        Assert.Equal(OperatorRole.Operator, store.Verify("operator", "new-op-pwd"));
        Assert.Null(store.Verify("operator", "123456"));   // 旧密码失效

        // 两次不一致 → 弹框拦下（Store 未动，旧密码仍有效）
        fake.Calls.Clear();
        vm.SelectedUser = vm.Users.First(u => u.UserName == "operator");
        vm.NewPassword = "abc123";
        vm.NewPassword2 = "abc124";
        vm.ResetPasswordCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("重置失败"));
        Assert.Equal(OperatorRole.Operator, store.Verify("operator", "new-op-pwd"));
    }

    // ---------- 单线过账（P3.2 后半） ----------

    [Fact]
    public void WorkOrderVm_UploadMaterial_SavesFirstThenTriggers()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        string? sideSeen = null;
        WorkOrderBook? diskAtCallback = null; // 回调触发那一刻的盘上工单
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin,
            uploadMaterial: s =>
            {
                sideSeen = s;
                diskAtCallback = WorkOrderStore.Load();
            });

        vm.EditBcNo = "BC-NEW-01";      // 刚录的条码（未保存）
        vm.EditUploadEnabled = true;
        vm.UploadRMaterialCommand.Execute(null);

        Assert.Equal("R", sideSeen);
        Assert.NotNull(diskAtCallback);                       // 回调触发时盘上已有工单
        Assert.Equal("BC-NEW-01", diskAtCallback!.BcNo);      // 先保存（刚录条码已落盘）再上传
        Assert.False(vm.CloseDialog);                          // 上传不关窗（与保存按钮区分）
        Assert.Contains("已提交", vm.UploadStatusText);
    }

    [Fact]
    public void WorkOrderVm_NoUploadService_ButtonDisabled()
    {
        var fake = new FakeCureDialogService();
        // 不传 uploadMaterial（=过账服务未装配）→ 按钮禁用，点了也不炸
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorRole.Admin);

        Assert.False(vm.CanUpload);
        vm.UploadRMaterialCommand.Execute(null); // 防御：直接执行也不抛
        Assert.Equal("", vm.UploadStatusText);
    }

    [Fact]
    public void MesPostConfigVm_Save_RoundTrip_WritesIniAndApplies()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.MesPostConfigViewModel(fake);

        vm.EditEnabled = true;
        vm.EditSide = "L";
        vm.EditTimeUnitIndex = 1;    // Minutes
        vm.EditInAtIndex = 1;        // Start
        vm.EditPostAbortOut = false;
        vm.EditMacNo = "MAC-01";
        vm.EditPlant = "P1";
        vm.EditUrl27 = "http://10.0.0.1/api0027/";
        vm.EditUrl30 = "http://10.0.0.1/api0030/";
        vm.EditUrl32 = "http://10.0.0.1/api0032/";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var o = CuringMesOptions.Load(); // ini 往返
        Assert.True(o.Enabled);
        Assert.Equal("L", o.Side);
        Assert.Equal(CuringTimeUnit.Minutes, o.TimeUnit);
        Assert.Equal(PostInAt.Start, o.InAt);
        Assert.False(o.PostAbortOut);
        Assert.Equal("MAC-01", o.MacNo);
        Assert.Equal("P1", o.Plant);
        // URL 落 [MES] 节（与主程序同键）+ 本进程 MesConfig 立即生效
        Assert.Equal("http://10.0.0.1/api0027/",
            IniFile.ReadStr(AppPaths.ConfigIni, "MES", "Url_API0027", ""));
        Assert.Equal("http://10.0.0.1/api0032/", MesConfig.UrlApi0032);
    }

    [Fact]
    public void MesPostConfigVm_BadUrl_BlocksSave()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.MesPostConfigViewModel(fake);
        vm.EditUrl30 = "ftp://not-http"; // 非 http/https → 校验拦下

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("URL 校验"));
    }

    [Fact]
    public void MainVm_OpenPostConfig_GateAndHotUpdate()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        Assert.Equal("过账：未启用", vm.PostingText); // 缺省关

        // 未登录 → 拦
        vm.OpenPostConfigCommand.Execute(null);
        Assert.DoesNotContain(fake.Calls, c => c == "ShowMesPostConfig");

        // 操作员 → 拦（过账配置管理员+）
        fake.LoginResult = ("operator", OperatorRole.Operator);
        vm.LoginCommand.Execute(null);
        vm.OpenPostConfigCommand.Execute(null);
        Assert.DoesNotContain(fake.Calls, c => c == "ShowMesPostConfig");

        // 管理员 → 放行；ini 预置 Enabled=1（模拟现场保存过）→ 保存成功后重读配置
        // 热更新过账服务 + 顶栏状态刷新（不重启）
        fake.LoginResult = ("admin", OperatorRole.Admin);
        vm.LoginCommand.Execute(null);
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "Enabled", "1");
        fake.MesPostConfigResult = true;
        vm.OpenPostConfigCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowMesPostConfig");
        Assert.Contains("已启用", vm.PostingText);

        // DualSide=1 → 双工位：两卡全显（列数 2）+ 顶栏带"（R 工位）"字样（V0.0.47）
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "DualSide", "1");
        vm.OpenPostConfigCommand.Execute(null);
        Assert.True(vm.IsDualSide);
        Assert.Equal(2, vm.WoCardColumns);
        Assert.True(vm.ShowWoCardR);
        Assert.True(vm.ShowWoCardL);
        Assert.Contains("工位）", vm.PostingText);

        // 切回单工位（Side 缺省 R）→ 只显 R 卡（列数 1）+ 顶栏去工位字样
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "DualSide", "0");
        vm.OpenPostConfigCommand.Execute(null);
        Assert.False(vm.IsDualSide);
        Assert.Equal(1, vm.WoCardColumns);
        Assert.True(vm.ShowWoCardR);
        Assert.False(vm.ShowWoCardL);
        Assert.DoesNotContain("工位", vm.PostingText);
    }

    [Fact]
    public void MainVm_SingleSideL_OnlyLCard()
    {
        // 单工位配 Side=L（现场实体可以是 L 工位机）：只显 L 卡——对齐工单窗
        // "只显 Side 侧"口径（视图↔槽映射恒定 R↔Right/L↔Left，V0.0.47 主窗补齐）
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "Enabled", "1");
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "Side", "L");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        Assert.False(vm.IsDualSide);
        Assert.True(vm.ShowWoCardL);
        Assert.False(vm.ShowWoCardR);
        Assert.Equal(1, vm.WoCardColumns);
    }

    // ---------- 登录窗 VM（V0.0.20 记住/跟随账号；V0.0.49 改密面板删除回归纯登录） ----------

    /// <summary>登录 VM 测试装配：临时库播种 + 临时 ini，双隔离（不碰现场 config）。</summary>
    private static (CureStation.ViewModels.LoginViewModel Vm, OperatorStore Store, string IniPath) NewLoginVm(
        TestScope scope, string dbName = "login-vm.db3")
    {
        var store = new OperatorStore(new SqliteHelper(Path.Combine(scope.TempDir, dbName)));
        store.EnsureSeeded("operator-seed", "test-admin-pwd", "dev123");
        string ini = Path.Combine(scope.TempDir, "login.ini");
        return (new CureStation.ViewModels.LoginViewModel(store, ini), store, ini);
    }

    [Fact]
    public void LoginVm_Constructor_PrefillsSavedUserAndPasswordAndHistory()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        store.AddUser("op1", OperatorRole.Operator, "abc123", OperatorRole.Admin);
        RunModeSettings.SaveLogin(ini, true, "op1", "abc123");

        // 重新构造（模拟现场第二次打开登录窗）：回填最后账号 + 它记住的密码 + 勾选。
        var vm2 = new CureStation.ViewModels.LoginViewModel(store, ini);
        Assert.Equal("op1", vm2.UserName);
        Assert.Equal("abc123", vm2.Password);
        Assert.True(vm2.Remember);
        // 下拉候选=在册账号实时表（含播种的 admin 与新增的 op1）。
        Assert.Contains("admin", vm2.HistoryUsers);
        Assert.Contains("op1", vm2.HistoryUsers);
    }

    [Fact]
    public void LoginVm_UserNameChange_FollowsRememberedPasswordPerAccount()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        store.AddUser("op1", OperatorRole.Operator, "abc123", OperatorRole.Admin);
        RunModeSettings.SaveLogin(ini, true, "op1", "abc123");

        // 切到没记过密码的账号 → 密码清空 + 取消勾选（不残留别人的密码）。
        vm.UserName = "admin";
        Assert.Equal("", vm.Password);
        Assert.False(vm.Remember);

        // 切回 op1 → 跟出它记住的密码 + 勾选记住（各户独立）。
        vm.UserName = "op1";
        Assert.Equal("abc123", vm.Password);
        Assert.True(vm.Remember);
    }

    [Fact]
    public void LoginVm_Ok_Success_SavesLoginAndBringsOutRole()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.UserName = "admin";
        vm.Password = "test-admin-pwd";
        vm.Remember = true;
        vm.OkCommand.Execute(null);

        Assert.Equal(OperatorRole.Admin, vm.Role);
        Assert.Equal("admin", vm.LoginName);
        Assert.True(closed);
        // 记住落盘：SavedUser=admin 且该账号密文可解回密码（ini 里永无明文）。
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(ini);
        Assert.True(remember);
        Assert.Equal("admin", user);
        Assert.Equal("test-admin-pwd", pwd);
    }

    [Fact]
    public void LoginVm_Ok_WrongPassword_ShowsTipAndStaysOpen()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.UserName = "admin";
        vm.Password = "wrong-pwd";
        vm.OkCommand.Execute(null);

        Assert.Null(closed);                       // 不关窗（留在登录面板重试）
        Assert.Null(vm.Role);
        Assert.Contains("错误", vm.Tip);
        Assert.Equal("", vm.Password);             // 密码清空重输
    }

    // ---------- 登录窗"修改密码"面板（V0.0.93 加回：本人验旧密改自己密码） ----------

    [Fact]
    public void LoginVm_ChangePassword_Success_UpdatesStoreSyncsRememberAndFillsLoginBox()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        store.AddUser("op1", OperatorRole.Operator, "abc123", OperatorRole.Admin);
        vm.UserName = "op1";
        RunModeSettings.SaveLogin(ini, true, "op1", "abc123"); // 该账号勾了"记住密码"

        vm.EnterChangeModeCommand.Execute(null);
        Assert.True(vm.IsChangeMode);
        Assert.Equal("修改密码", vm.ModeTitle); // 横幅随模式切换

        vm.OldPassword = "abc123";
        vm.NewPassword = "xyz789";
        vm.ConfirmPassword = "xyz789";
        vm.ChangePasswordCommand.Execute(null);

        // 切回登录面板 + 新密码已填进登录密码框（直接点登录即可）+ 绿色成功提示
        Assert.False(vm.IsChangeMode);
        Assert.Equal("xyz789", vm.Password);
        Assert.Equal("密码修改成功，请用新密码登录", vm.Tip);
        Assert.False(vm.TipIsError);
        Assert.Equal("", vm.OldPassword);          // 改密三框清空不残留
        // 库里换新：新密可登录、旧密作废
        Assert.Equal(OperatorRole.Operator, store.Verify("op1", "xyz789"));
        Assert.Null(store.Verify("op1", "abc123"));
        // 记住密码记忆同步换新（否则下次回填旧密码必登录失败）
        var (remember, _, pwd) = RunModeSettings.ReadSavedLogin(ini, "op1");
        Assert.True(remember);
        Assert.Equal("xyz789", pwd);
    }

    [Fact]
    public void LoginVm_ChangePassword_WrongOldPassword_ShowsErrorAndKeepsOldPassword()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.UserName = "admin";
        vm.EnterChangeModeCommand.Execute(null);

        vm.OldPassword = "wrong-old";
        vm.NewPassword = "xyz789";
        vm.ConfirmPassword = "xyz789";
        vm.ChangePasswordCommand.Execute(null);

        Assert.True(vm.IsChangeMode);              // 留在改密面板
        Assert.True(vm.TipIsError);
        Assert.Contains("旧密码", vm.Tip);
        Assert.Equal("", vm.OldPassword);          // 旧密框清空重输
        // 库未动：原密码仍可登录
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "test-admin-pwd"));
    }

    [Fact]
    public void LoginVm_ChangePassword_ConfirmMismatch_BlocksAndClearsNewBoxes()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.UserName = "admin";
        vm.EnterChangeModeCommand.Execute(null);

        vm.OldPassword = "test-admin-pwd";
        vm.NewPassword = "xyz789";
        vm.ConfirmPassword = "xyz780";
        vm.ChangePasswordCommand.Execute(null);

        Assert.True(vm.TipIsError);
        Assert.Contains("不一致", vm.Tip);
        Assert.Equal("", vm.NewPassword);          // 两个新框清空重输
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "test-admin-pwd")); // 密码未改
    }

    [Fact]
    public void LoginVm_ChangePassword_ShortOrSameAsOld_BlocksByRule()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.UserName = "admin";
        vm.EnterChangeModeCommand.Execute(null);
        vm.OldPassword = "test-admin-pwd";

        // ① 新密码不足 6 位
        vm.NewPassword = "abc";
        vm.ConfirmPassword = "abc";
        vm.ChangePasswordCommand.Execute(null);
        Assert.True(vm.TipIsError);
        Assert.Contains("至少 6 位", vm.Tip);
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "test-admin-pwd"));

        // ② 新密码与旧密码相同（UI 层先拦——用户管理窗重置场景免验旧密，Store 不比旧）
        vm.NewPassword = "test-admin-pwd";
        vm.ConfirmPassword = "test-admin-pwd";
        vm.ChangePasswordCommand.Execute(null);
        Assert.Contains("不能与旧密码相同", vm.Tip);
        Assert.Equal(OperatorRole.Admin, store.Verify("admin", "test-admin-pwd"));
    }

    [Fact]
    public void LoginVm_ChangePassword_EmptyUserName_Blocks()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.UserName = "";
        vm.EnterChangeModeCommand.Execute(null);

        vm.OldPassword = "abc123";
        vm.NewPassword = "xyz789";
        vm.ConfirmPassword = "xyz789";
        vm.ChangePasswordCommand.Execute(null);

        Assert.True(vm.TipIsError);
        Assert.Contains("用户名", vm.Tip);
        Assert.True(vm.IsChangeMode);
    }

    [Fact]
    public void LoginVm_BackToLogin_ClearsInputsAndReturns()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.EnterChangeModeCommand.Execute(null);
        vm.OldPassword = "abc123";
        vm.NewPassword = "xyz789";

        vm.BackToLoginCommand.Execute(null);

        Assert.False(vm.IsChangeMode);
        Assert.Equal("账号登录", vm.ModeTitle);
        Assert.Equal("", vm.OldPassword);          // 半截输入不残留
        Assert.Equal("", vm.NewPassword);
        Assert.Equal("", vm.ConfirmPassword);
        Assert.Equal("", vm.Tip);
        Assert.True(vm.TipIsError);                // 配色复位
    }

    // ---------- 工艺策略：预置库/套用/拦截/可视化预览（V0.0.20 三轮） ----------

    [Fact]
    public void PolicyVm_DefaultBook_HasPresetsAndCustomActive()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 缺省库：1 个自定义（非预置）+ 5 个预置；激活名=自定义；开窗默认选中它
        Assert.Equal(6, vm.Policies.Count);
        Assert.Equal(5, vm.Policies.Count(p => p.IsPreset));
        Assert.Contains(vm.Policies, p => p.Name == "自定义" && !p.IsPreset);
        Assert.False(vm.SelectedIsPreset);
        Assert.Equal("自定义", vm.SelectedPolicy!.Name);
        Assert.Equal("自定义", vm.ActiveName);   // 生效策略（列表"生效中"徽标的比对真源）
    }

    [Fact]
    public void PolicyVm_DefaultBook_PresetSamplesResolve()
    {
        // 预置样例的三级覆盖口径（教学样例：现场套用后照此改）
        var book = CuringPolicyStore.BuildDefaultBook();
        var layered = book.Policies.First(p => p.Name == "分层固化");
        Assert.Equal(90, layered.ResolveMinutes(1, 1));   // 层1 覆盖
        Assert.Equal(120, layered.ResolveMinutes(1, 2));  // 层2 覆盖
        Assert.Equal(150, layered.ResolveMinutes(1, 3));  // 层3 覆盖
        Assert.Equal(120, layered.ResolveMinutes(1, 4));  // 层4 没配→回全局

        var special = book.Policies.First(p => p.Name == "特殊仓位示例");
        Assert.Equal(45, special.ResolveMinutes(3, 2));   // 仓位覆盖
        Assert.Equal(180, special.ResolveMinutes(7, 1));  // 仓位覆盖
        Assert.Equal(120, special.ResolveMinutes(1, 1));  // 没配→回全局

        var deep = book.Policies.First(p => p.Name == "深度固化");
        Assert.True(deep.GrillGate);                      // 深度固化带光栅门控
        Assert.Equal("自定义", book.ActiveName);
        Assert.False(book.ResolveActive()!.IsPreset);     // 生效的是自定义
    }

    [Fact]
    public void PolicyVm_OldJsonWithoutIsPreset_AllCustom()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 老版本 JSON（无 IsPreset 字段）——反序列化缺省 false=全按自定义，兼容零迁移
        File.WriteAllText(CuringPolicyStore.DefaultPath,
            """{"Policies":[{"Name":"甲","GlobalMinutes":60},{"Name":"乙","GlobalMinutes":30}],"ActiveName":"乙"}""");

        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.All(loaded!.Policies, p => Assert.False(p.IsPreset));
        Assert.Equal("乙", loaded.ResolveActive()!.Name);
    }

    [Fact]
    public void PolicyVm_SelectedIsPreset_FlipsWithSelection()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        Assert.False(vm.SelectedIsPreset);                 // 缺省选中自定义
        vm.SelectedPolicy = vm.Policies.First(p => p.IsPreset);
        Assert.True(vm.SelectedIsPreset);
        vm.SelectedPolicy = vm.Policies.First(p => !p.IsPreset);
        Assert.False(vm.SelectedIsPreset);
    }

    [Fact]
    public void PolicyVm_ApplyTemplate_DefaultTargetCustom_WritesAndActivates()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 选预置"快速固化"改参（现场改工艺时长的典型路径）；目标不动=缺省「自定义」
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.EditGlobalMinutes = "90";
        Assert.Equal("自定义", vm.ApplyTargetName);   // 套用目标缺省「自定义」（旧口径）

        vm.ApplyTemplateCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("自定义", loaded!.ActiveName);        // 套用即激活目标
        Assert.Equal("自定义", vm.ActiveName);             // VM 徽标真源同步
        var custom = loaded.Policies.First(p => p.Name == "自定义" && !p.IsPreset);
        Assert.Equal(90, custom.GlobalMinutes);            // 编辑值落到自定义
        var preset = loaded.Policies.First(p => p.Name == "快速固化");
        Assert.Equal(60, preset.GlobalMinutes);            // 预置本体保持原样（模板可回溯）
        Assert.True(preset.IsPreset);
        Assert.False(vm.SelectedIsPreset);                 // 套用后选中的是目标
        Assert.Equal(90, vm.SelectedPolicy!.GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_ApplyTemplate_ReplacesOverridesNotMerges()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 第一次套用"分层固化"：自定义带上层覆盖
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "分层固化");
        vm.ApplyTemplateCommand.Execute(null);
        Assert.Equal(90, vm.SelectedPolicy!.LevelMinutes[1]);

        // 第二次套用"快速固化"（无层覆盖）：自定义覆盖行整体替换，不残留
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.ApplyTemplateCommand.Execute(null);
        Assert.Empty(vm.SelectedPolicy!.LevelMinutes);
        Assert.Equal("", vm.EditLevel2Min);                // 编辑区回显同步（无残留旧值）

        var loaded = CuringPolicyStore.Load();
        var custom = loaded!.Policies.First(p => p.Name == "自定义" && !p.IsPreset);
        Assert.Empty(custom.LevelMinutes);
        Assert.Equal(60, custom.GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_LoadFromDisk_HealsMissingCustom()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 盘面缺「自定义」（用户删掉它的老盘面/手工改 JSON）——载入自愈补回
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "标准固化", GlobalMinutes = 120, IsPreset = true } },
            ActiveName = "标准固化",
        };
        CuringPolicyStore.Save(book);
        var fake = new FakeCureDialogService();

        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 「自定义」补回队首（与 BuildDefaultBook 口径一致），缺省全局 120、非预置
        Assert.Contains(vm.Policies, p => p.Name == "自定义" && !p.IsPreset);
        var healed = vm.Policies.First(p => p.Name == "自定义");
        Assert.False(healed.IsPreset);
        Assert.Equal(120, healed.GlobalMinutes);
        Assert.Equal("自定义", vm.Policies[0].Name);   // 队首——激活名失效时 ResolveActive 兜底回它
        // 套用目标名单也含「自定义」（恒可落点），选中维持原激活策略
        Assert.Contains("自定义", vm.ApplyTargets);
        Assert.Equal("标准固化", vm.SelectedPolicy!.Name);
    }

    [Fact]
    public void PolicyVm_ApplyTemplate_AfterHeal_StillWorks()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        // 盘面只有预置（无自定义）→ 自愈补回后套用照常落到"自定义"并激活（旧
        // CreatesCustomIfMissing 用例替换而来：自愈补对象后套用走"整体替换"分支）。
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "标准固化", GlobalMinutes = 120, IsPreset = true } },
            ActiveName = "标准固化",
        };
        CuringPolicyStore.Save(book);
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "标准固化");
        vm.EditGlobalMinutes = "80";

        vm.ApplyTemplateCommand.Execute(null);

        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("自定义", loaded!.ActiveName);
        Assert.Equal(80, loaded!.Policies.First(p => p.Name == "自定义" && !p.IsPreset).GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_ApplyTemplate_BadNumberBlocks()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());   // 等价自愈后的盘面
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.EditGlobalMinutes = "abc";

        vm.ApplyTemplateCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("数字格式"));
        var loaded = CuringPolicyStore.Load();
        // 被拦后自定义保持缺省值 120（编辑错误未污染既有自定义策略）
        Assert.Equal(120, loaded!.Policies.First(p => p.Name == "自定义" && !p.IsPreset).GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_ApplyTemplate_ToUserCreatedPolicy()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 用户先自建"产线A工艺"，预置"快速固化"改参后套用到它（V0.0.57 需求②）
        vm.AddPolicyCommand.Execute(null);
        vm.EditName = "产线A工艺";
        vm.SaveCommand.Execute(null);   // 先落库（关闭链触发——仅保存入列表即可复用）
        Assert.Contains(vm.Policies, p => p.Name == "产线A工艺");

        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.ApplyTargetName = "产线A工艺";
        vm.EditGlobalMinutes = "75";

        vm.ApplyTemplateCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("产线A工艺", loaded!.ActiveName);     // 套用即激活目标
        var target = loaded!.Policies.First(p => p.Name == "产线A工艺");
        Assert.Equal(75, target.GlobalMinutes);            // 参数落到用户自建策略
        Assert.False(target.IsPreset);
        var custom = loaded!.Policies.First(p => p.Name == "自定义");
        Assert.Equal(120, custom.GlobalMinutes);           // 「自定义」不被牵动
        Assert.Equal(60, loaded!.Policies.First(p => p.Name == "快速固化").GlobalMinutes);
        Assert.Equal("产线A工艺", vm.SelectedPolicy!.Name);
        Assert.Equal("产线A工艺", vm.ActiveName);
    }

    [Fact]
    public void PolicyVm_ApplyTargets_ListsNonPresetAndDefaultsCustom()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 缺省库：非预置只有「自定义」→ 名单就一项，缺省选中它
        var target = vm.ApplyTargets;
        Assert.Single(target);
        Assert.Equal("自定义", target[0]);
        Assert.Equal("自定义", vm.ApplyTargetName);

        // 用户自建（AddPolicy 即进名单，对象名还是"策略7"）→ 名单两项，选中不跳
        vm.AddPolicyCommand.Execute(null);
        Assert.Equal(2, vm.ApplyTargets.Count);
        Assert.Contains("策略7", vm.ApplyTargets);
        Assert.Contains("自定义", vm.ApplyTargets);
        Assert.Equal("自定义", vm.ApplyTargetName);

        // 改名保存（草稿名落库对象）→ 名单按库面刷新出新名
        vm.EditName = "产线B工艺";
        vm.SaveCommand.Execute(null);
        Assert.Contains("产线B工艺", vm.ApplyTargets);
        Assert.Equal("自定义", vm.ApplyTargetName);

        // 删掉自建项 → 名单回退单项目标，选中仍「自定义」
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "产线B工艺");
        vm.RemovePolicyCommand.Execute(null);
        Assert.Single(vm.ApplyTargets);
        Assert.Equal("自定义", vm.ApplyTargetName);
    }

    [Fact]
    public void PolicyVm_CustomPolicy_CannotBeRemoved()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "自定义");

        vm.RemovePolicyCommand.Execute(null);

        // 拦截兜提示 + 列表原样（「自定义」恒存在——系统缺省策略防删）
        Assert.Contains(fake.Calls, c => c.Contains("不能删除"));
        Assert.Contains(vm.Policies, p => p.Name == "自定义");
        Assert.Equal(6, vm.Policies.Count);
        Assert.Equal("自定义", CuringPolicyStore.Load()!.ActiveName);
    }

    [Fact]
    public void PolicyVm_RenameActivePolicy_FollowsActiveName()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Equal("自定义", vm.ActiveName);   // 缺省库生效策略

        // 把生效中的「自定义」改名"老配方"（仅保存）——激活名必须跟随，
        // 否则激活名指向已不存在的旧名，重启兜底回缺省 120、现场时长凭空变。
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "自定义");
        vm.EditName = "老配方";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.Equal("老配方", loaded!.ActiveName);
        Assert.Equal("老配方", vm.ActiveName);
        Assert.Equal(120, loaded!.Policies.First(p => p.Name == "老配方").GlobalMinutes);
        // 重开窗：自愈补回缺省「自定义」+ 生效策略（老配方）缺省选中
        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Contains(reopened.Policies, p => p.Name == "自定义" && !p.IsPreset);
        Assert.Equal("老配方", reopened.SelectedPolicy!.Name);
        Assert.Equal("老配方", reopened.ActiveName);
    }

    [Fact]
    public void PolicyVm_Preset_SaveActivateRemove_Blocked()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());   // 等价自愈后的盘面
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "标准固化");
        Assert.True(vm.SelectedIsPreset);

        // 保存拦（出厂模板保持原样——出口只有"套用所选模板"）
        vm.SaveCommand.Execute(null);
        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("套用"));

        // 激活拦（激活只认自定义）
        vm.ActivateCommand.Execute(null);
        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("激活"));
        Assert.Equal("自定义", CuringPolicyStore.Load()!.ActiveName);

        // 删除拦（预置永远可回溯）
        int before = vm.Policies.Count;
        vm.RemovePolicyCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("不能删除"));
        Assert.Equal(before, vm.Policies.Count);
    }

    [Fact]
    public void PolicyVm_SaveFailure_DoesNotPolluteSelected()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var book = new CuringPolicyBook
        {
            Policies =
            {
                new CuringPolicy { Name = "甲", GlobalMinutes = 120 },
                new CuringPolicy { Name = "乙", GlobalMinutes = 30 },
            },
            ActiveName = "甲",
        };
        CuringPolicyStore.Save(book);
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 改名撞"乙"（重名校验拦）——先改副本语义：选中对象不被半截编辑污染
        vm.EditName = "乙";
        vm.EditGlobalMinutes = "90";
        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("校验"));
        Assert.Equal(120, vm.SelectedPolicy!.GlobalMinutes);   // 未被 90 污染
        Assert.Equal("甲", vm.SelectedPolicy.Name);
    }

    [Fact]
    public void PolicyVm_Preview_RebuildsOnEdit()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例按分钟字面输入/断言，显式写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 缺省库自定义 120：30 格全 120（Orange 档）
        Assert.Equal(30, vm.SlotPreviewItems.Count);
        Assert.All(vm.SlotPreviewItems, i => Assert.Equal(120, i.Minutes));
        Assert.All(vm.SlotPreviewItems, i => Assert.Equal("Orange", i.Tier));

        // 改全局时长 → 全格跟随（30 分钟落 Green 档：<60）
        vm.EditGlobalMinutes = "30";
        Assert.All(vm.SlotPreviewItems, i => Assert.Equal(30, i.Minutes));
        Assert.All(vm.SlotPreviewItems, i => Assert.Equal("Green", i.Tier));

        // 层覆盖：层1 变 10（Green），其余仍 30
        vm.EditLevel1Min = "10";
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 1), i => Assert.Equal(10, i.Minutes));
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 2), i => Assert.Equal(30, i.Minutes));

        // 仓位覆盖：3-2=45 只动一格
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "3",
            Level = "2",
            Minutes = "45",
        });
        Assert.Equal(45, vm.SlotPreviewItems.First(i => i.Rack == 3 && i.Level == 2).Minutes);
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 2 && i.Rack != 3), i => Assert.Equal(30, i.Minutes));

        // 数字错兜底照画：全局回落原选中值 120，但层1 覆盖 10 与仓位 3-2=45 仍生效
        vm.EditGlobalMinutes = "abc";
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 1), i => Assert.Equal(10, i.Minutes));
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 2 && i.Rack != 3), i => Assert.Equal(120, i.Minutes));
        Assert.Equal(45, vm.SlotPreviewItems.First(i => i.Rack == 3 && i.Level == 2).Minutes);

        // 清空仓位行 → 该格回落层/全局
        vm.SlotRows.Clear();
        Assert.Equal(120, vm.SlotPreviewItems.First(i => i.Rack == 3 && i.Level == 2).Minutes);
    }

    [Fact]
    public void PolicyVm_Preview_GridFollowsRackCount()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        // 双架点表（架数 11→垫底仍 10？不——DialogService 传最大架号；VM 下限 10）：
        // 直接验证构造参数链（测试绕过 DialogService，传 11）
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake, rackCount: 11, levelCount: 3);
        Assert.Equal(11, vm.RackCount);
        Assert.Equal(33, vm.SlotPreviewItems.Count);
        Assert.Equal(11, vm.SlotPreviewItems.Count(i => i.Level == 1));
    }

    // ---------- 时间单位切换（V0.0.53；V0.0.56 秒档删除，只留 时/分） ----------

    [Fact]
    public void PolicyVm_TimeUnitSwitch_ConvertsEditorCanvasLegendAndFloating()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        // 本用例测"分→时→分"转换链——VM 构造 LoadTimeUnit 以盘上 ini 为准
        // （临时目录无键会落到缺省 Hours），必须先写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.ReplaceSelection(new[] { Cell(vm, 1, 1) });

        // 分：编辑区保持分钟字面
        Assert.Equal(CureStation.ViewModels.PolicyTimeUnit.Minutes, vm.TimeUnit);
        Assert.Equal("分", vm.TimeUnitSuffix);
        Assert.Equal("120", vm.EditGlobalMinutes);
        Assert.Equal("120", Cell(vm, 1, 1).MinutesText);
        Assert.Contains("当前 120 分", vm.SelectionSummaryText);
        Assert.Equal("跟随层/全局：< 60 分钟", vm.LegendGreenText);
        Assert.Equal("每层覆盖（分钟，空=用全局）", vm.LevelHeaderText);
        Assert.Contains("分钟", vm.GlobalMinutesToolTip);
        Assert.Equal(1, vm.TimeUnitIndex);

        // 切小时：120 分钟 = 2 小时——编辑区/画布/浮层/图例/标题全换
        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Hours;
        Assert.Equal("2", vm.EditGlobalMinutes);
        Assert.Equal("2", Cell(vm, 1, 1).MinutesText);
        Assert.Contains("当前 2 时", vm.SelectionSummaryText);
        Assert.Contains("固化 2 小时", Cell(vm, 1, 1).Tip);
        Assert.Equal("跟随层/全局：< 1 小时", vm.LegendGreenText);
        Assert.Equal("跟随层/全局：1 ~ 1.98 小时", vm.LegendYellowText);
        Assert.Equal("每层覆盖（小时，空=用全局）", vm.LevelHeaderText);
        Assert.Contains("小时", vm.SelectionInputToolTip);
        Assert.Equal(0, vm.TimeUnitIndex);
        Assert.Equal(["时", "分"], vm.TimeUnitOptions);   // 下拉两档：秒已删

        // 回分钟：无损
        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Minutes;
        Assert.Equal("120", vm.EditGlobalMinutes);
        Assert.Equal("120", Cell(vm, 1, 1).MinutesText);
    }

    [Fact]
    public void PolicyVm_TimeUnitSwitch_ConvertsUnsavedEditsAndSlotRowsLosslessly()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺省口径 2026-11-09 改=时；本用例测"分→时→分"无损转换，先写盘铺分口径
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        vm.EditGlobalMinutes = "90";       // 未保存的编辑
        vm.EditLevel2Min = "45";
        vm.SlotRows.Add(new CureStation.ViewModels.SlotMinuteRow
        {
            Rack = "3", Level = "2", Minutes = "20",
        });

        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Hours;
        Assert.Equal("1.5", vm.EditGlobalMinutes);
        Assert.Equal("0.75", vm.EditLevel2Min);
        Assert.Equal("0.3333", vm.SlotRows[0].Minutes);   // 20 分 = 0.3333 时（0.####）
        Assert.Equal(90, Cell(vm, 1, 1).Minutes);         // 内部分钟值不变
        Assert.Equal(45, Cell(vm, 1, 2).Minutes);
        Assert.Equal(20, Cell(vm, 3, 2).Minutes);

        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Minutes;
        Assert.Equal("90", vm.EditGlobalMinutes);
        Assert.Equal("45", vm.EditLevel2Min);
        Assert.Equal("20", vm.SlotRows[0].Minutes);
    }

    [Fact]
    public void PolicyVm_TimeUnitHours_DecimalInputSavesMinutes()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Hours;
        vm.EditGlobalMinutes = "1.5";      // 1.5 小时 = 90 分钟

        vm.ActivateCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.Equal(90, loaded!.Policies.First(p => p.Name == "自定义").GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_TimeUnit_RememberedInIni_InvalidFallsBackToHours()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.TimeUnit = CureStation.ViewModels.PolicyTimeUnit.Hours;
        Assert.Equal("Hours", IniFile.ReadStr(
            AppPaths.ConfigIni, "setting", "PolicyTimeUnit", ""));

        // 关窗再打开沿用记忆单位（120 分钟按小时回显 2）
        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Equal(CureStation.ViewModels.PolicyTimeUnit.Hours, reopened.TimeUnit);
        Assert.Equal("2", reopened.EditGlobalMinutes);

        // 非法值（含旧版存量 "Seconds"，档已删）自愈回缺省（2026-11-09 拍板：缺省=时）
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Bogus");
        var bad = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Equal(CureStation.ViewModels.PolicyTimeUnit.Hours, bad.TimeUnit);

        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Seconds");
        var legacy = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Equal(CureStation.ViewModels.PolicyTimeUnit.Hours, legacy.TimeUnit);
        Assert.Equal("2", legacy.EditGlobalMinutes);   // 120 分钟按小时口径回显
    }

    [Fact]
    public void PolicyVm_AddPolicy_ActivateSave_ReopenVisible_AndRaisesRequestClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        // 用户实测回归：添加策略 → 激活保存 → 关窗再打开，列表必须含新增项。
        vm.AddPolicyCommand.Execute(null);
        vm.EditName = "慢速固化";
        vm.ActivateCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        Assert.True(closed);   // View 据此 DialogResult=true（主窗刷新 + 提示"已保存"）

        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.Contains(reopened.Policies, p => p.Name == "慢速固化");
        Assert.Equal("慢速固化", reopened.SelectedPolicy!.Name);   // 激活策略缺省选中
        Assert.Equal("慢速固化", CuringPolicyStore.Load()!.ActiveName);
    }

    [Fact]
    public void PolicyVm_SaveFailure_DoesNotRaiseRequestClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        bool raised = false;
        vm.RequestClose += _ => raised = true;

        vm.EditGlobalMinutes = "abc";
        vm.SaveCommand.Execute(null);

        Assert.False(raised);
        Assert.False(vm.CloseDialog);
    }

    // ---------- 光栅四项统一跟随策略（V0.0.96 用户拍板） ----------
    // 门控/报警总开关/信号类型/报警解除全部是策略字段：策略窗只编辑草稿，
    // 保存/激活/套用才落库；运行期状态机读激活策略（MainViewModel.GetActivePolicy）。
    // 旧版三个全局 ini 键（GrillAlarmResetMode/GrillSignalPolarity/
    // GrillAlarmEnabled）已删除，勿再加回。

    /// <summary>铺一份只含激活「自定义」策略的库（光栅字段按 configure 注入）。</summary>
    private static void SaveActivePolicy(Action<CuringPolicy> configure)
    {
        EnsureConfigDir();
        var p = new CuringPolicy { Name = CureStation.ViewModels.CuringPolicyViewModel.CustomName };
        configure(p);
        CuringPolicyStore.Save(new CuringPolicyBook
        {
            Policies = { p },
            ActiveName = p.Name,
        });
    }

    [Fact]
    public void PolicyVm_GrillResetModeSegments_FollowSelectedPolicyAndSave()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 激活「自定义」的字段缺省 = Manual（V0.0.71 用户拍板：现场默认人工确认）
        Assert.True(vm.IsGrillManualReset);
        Assert.False(vm.IsGrillAutoReset);

        // 切自动：只改草稿，不写全局 ini
        vm.IsGrillAutoReset = true;
        Assert.True(vm.IsGrillAutoReset);
        Assert.False(vm.IsGrillManualReset);
        Assert.Equal("", IniFile.ReadStr(
            AppPaths.ConfigIni, "setting", "GrillAlarmResetMode", ""));

        // 【仅保存】才落策略库（字段随策略走）
        vm.SaveCommand.Execute(null);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(GrillAlarmResetMode.Auto, loaded!.Policies[0].GrillResetMode);

        // 重开回显所选（激活）策略的自动档
        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.True(reopened.IsGrillAutoReset);
        Assert.False(reopened.IsGrillManualReset);
    }

    [Fact]
    public void PolicyVm_GrillSettings_SwitchPolicy_RestoresWholeSet()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var book = new CuringPolicyBook
        {
            Policies =
            {
                new CuringPolicy
                {
                    Name = "自定义",
                    GrillResetMode = GrillAlarmResetMode.Manual,
                    GrillPolarity = GrillSignalPolarity.BlockedOn,
                    GrillAlarmEnabled = true,
                },
                new CuringPolicy
                {
                    Name = "快固",
                    GrillResetMode = GrillAlarmResetMode.Auto,
                    GrillPolarity = GrillSignalPolarity.BlockedOff,
                    GrillAlarmEnabled = false,
                },
            },
            ActiveName = "自定义",
        };
        CuringPolicyStore.Save(book);
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(new FakeCureDialogService());

        // 激活策略「自定义」整套回显
        Assert.True(vm.IsGrillManualReset);
        Assert.True(vm.IsGrillNormallyOpen);
        Assert.True(vm.IsGrillAlarmEnabled);

        // 换选「快固」→ 三项一起换，不存在某一项停留在上一策略/全局
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快固");
        Assert.True(vm.IsGrillAutoReset);
        Assert.False(vm.IsGrillManualReset);
        Assert.True(vm.IsGrillNormallyClosed);
        Assert.False(vm.IsGrillAlarmEnabled);
        Assert.Contains("遮挡=OFF", vm.GrillSignalToolTip);
        Assert.Equal("已关闭", vm.GrillAlarmSwitchText);
    }

    // ---------- 光栅信号极性（V0.0.72：BlockedOn=遮挡 ON / BlockedOff=遮挡 OFF；
    //            V0.0.96 起是策略字段；V0.0.99 用户现场实测"常闭"→ 全项目
    //            缺省=常闭） ----------

    [Fact]
    public void PolicyVm_GrillSignalInverted_TogglesDraftAndSavesToPolicy()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 策略字段缺省 = 常闭（遮挡=OFF，V0.0.99 用户现场实测口径）
        Assert.True(vm.GrillSignalInverted);
        Assert.True(vm.IsGrillNormallyClosed);
        Assert.False(vm.IsGrillNormallyOpen);
        Assert.Contains("遮挡=OFF", vm.GrillSignalToolTip);

        // 选常开：只改草稿，不写任何全局 ini 键（旧键已删）
        vm.IsGrillNormallyOpen = true;
        Assert.False(vm.GrillSignalInverted);
        Assert.True(vm.IsGrillNormallyOpen);
        Assert.Contains("遮挡=ON", vm.GrillSignalToolTip);
        Assert.Equal("", IniFile.ReadStr(
            AppPaths.ConfigIni, "setting", "GrillSignalPolarity", ""));

        // 【仅保存】才落策略库字段，随策略走
        vm.SaveCommand.Execute(null);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal(GrillSignalPolarity.BlockedOn, loaded!.Policies[0].GrillPolarity);

        // 重开回显激活策略的常开档
        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.True(reopened.IsGrillNormallyOpen);
        Assert.False(reopened.IsGrillNormallyClosed);
    }

    // ---------- 报警总开关（V0.0.90：策略窗光栅带"光栅报警"滑块——开=报警
    //            （缺省），关=整条报警链静默（门控不受影响）；
    //            V0.0.96 起是策略字段，随策略保存/激活） ----------

    [Fact]
    public void PolicyVm_GrillAlarmEnabled_TogglesDraftAndSavesToPolicy()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 策略字段缺省 = 开启（用户拍板"默认开启报警"）
        Assert.True(vm.IsGrillAlarmEnabled);
        Assert.Equal("已开启", vm.GrillAlarmSwitchText);

        // 切【关】：只改草稿 + 文字换口径，不写全局 ini（旧键已删）
        vm.IsGrillAlarmEnabled = false;
        Assert.False(vm.IsGrillAlarmEnabled);
        Assert.Equal("已关闭", vm.GrillAlarmSwitchText);
        Assert.Equal("", IniFile.ReadStr(
            AppPaths.ConfigIni, "setting", "GrillAlarmEnabled", ""));

        // 保存 → 策略库字段随策略落盘
        vm.SaveCommand.Execute(null);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.False(loaded!.Policies[0].GrillAlarmEnabled);

        // 重开回显关态
        var reopened = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        Assert.False(reopened.IsGrillAlarmEnabled);
        Assert.Equal("已关闭", reopened.GrillAlarmSwitchText);
    }

    // 注：旧 ini 键 GrillAlarmEnabled"只有显式 0 才算关"的解析语义已随全局键删除，
    //     安全缺省改由【策略反序列化】保证（旧 JSON 缺字段 = 开），见
    //     CuringPolicyTests.Store_LegacyJsonMissingGrillFields_UsesSafeDefaults。

    // ---------- 光栅门控条文案（V0.0.74：开关按钮自身显示状态 + 副文换口径） ----------

    [Fact]
    public void PolicyVm_GrillGateText_FollowsSwitch()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(new FakeCureDialogService());

        // 关（缺省）→ 按钮写"已关闭"、副文给"到位即计时"口径
        vm.EditGrillGate = false;
        Assert.Equal("已关闭", vm.GrillGateSwitchText);
        Assert.Contains("到位即开计时", vm.GrillGateDetailText);

        // 开 → 字面立即换（按钮即状态显示，副文给门控生效口径）
        vm.EditGrillGate = true;
        Assert.Equal("已启用", vm.GrillGateSwitchText);
        Assert.Contains("让空", vm.GrillGateDetailText);

        // 回关（双向）
        vm.EditGrillGate = false;
        Assert.Equal("已关闭", vm.GrillGateSwitchText);
    }

    [Fact]
    public void MainVm_AlarmPresenter_ManualMode_ShowsWindowAndClearsFeed()
    {
        using var scope = new TestScope();
        // 复位模式是策略字段（V0.0.96）：铺一条激活策略指定人工档
        SaveActivePolicy(p => p.GrillResetMode = GrillAlarmResetMode.Manual);
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 人工档首帧报警（测试直驱 presenter，绕过 500ms 定时器）：
        // → Fake 收到 ShowGrillAlarmReset 且编排器就是主窗那份（单源）
        vm.AlarmPresenter.Refresh([(1, 1)]);
        Assert.Contains(fake.Calls, c => c == "ShowGrillAlarmReset");
        Assert.Same(vm.AlarmPresenter, fake.AlarmPresenter);

        // 用户 ✕ 关窗（弹窗 Closed 回调）→ 旧报警不连环重弹
        fake.AlarmPresenter!.NotifyWindowClosed();
        fake.AlarmPresenter.Refresh([(1, 1)]);
        Assert.Single(fake.Calls.Where(c => c == "ShowGrillAlarmReset").ToList());

        // 新库位报警 → 再弹一枪（热更决策）
        vm.AlarmPresenter.Refresh([(1, 1), (2, 2)]);
        Assert.Equal(2, fake.Calls.Count(c => c == "ShowGrillAlarmReset"));

        // 全解除 → 自动收窗（CloseGrillAlarmReset）
        vm.AlarmPresenter.Refresh([]);
        Assert.Contains(fake.Calls, c => c == "CloseGrillAlarmReset");
    }

    [Fact]
    public void MainVm_AlarmPresenter_AutoMode_NeverShowsWindow()
    {
        using var scope = new TestScope();
        // 自动档策略（随策略保存/激活，不再读全局 ini）
        SaveActivePolicy(p => p.GrillResetMode = GrillAlarmResetMode.Auto);
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        vm.AlarmPresenter.Refresh([(1, 1)]);
        vm.AlarmPresenter.Refresh([(1, 1), (3, 3)]);
        Assert.DoesNotContain(fake.Calls, c => c == "ShowGrillAlarmReset");
        Assert.Empty(vm.AlarmPresenter.Items);   // 自动档清单恒空（不接管复位）
    }
}
