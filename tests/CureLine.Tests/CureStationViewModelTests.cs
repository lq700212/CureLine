// =========================================================================
// CureStationViewModelTests：程序① VM 用例（主窗网格/点表配置 VM/Fake 桩）
// 铁律：新增 VM 编排必须补用例——模拟点表 + FakeDialogService，TestScope 隔离。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Core.Security;
using CureLine.Tests.Mocks;
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
        // 落盘校验：再 Load 得到同一张表（自愈有据可查）
        var loaded = IoPointTableStore.Load();
        Assert.NotNull(loaded);
        Assert.Single(loaded!.Devices);
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
    public void PointTableVm_GenerateTemplate_Fills180Bindings()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.TemplateIp = "192.168.1.20";
        vm.TemplateRacks = "10";
        vm.TemplateLevels = "3";

        vm.GenerateTemplateCommand.Execute(null);

        var dev = Assert.Single(vm.Devices);
        Assert.Equal(10, dev.BlockCount);
        Assert.Equal(180, dev.Bindings.Count);
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
        Assert.Equal(180, loaded!.Devices[0].Bindings.Count);
    }

    [Fact]
    public void PointTableVm_ApplyBinding_UpdatesRawRow()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateTemplateCommand.Execute(null);

        // 选中首行（架1层1 左光栅）→ 编辑面板改角色为托盘 + 功能名 → 应用
        vm.SelectedBinding = vm.Bindings.First(b => b.Raw.IsInput);
        vm.EditRole = IoPointRole.TrayDetect;
        vm.EditFunction = "手动改名";
        vm.ApplyBindingCommand.Execute(null);

        Assert.Equal(IoPointRole.TrayDetect, vm.SelectedBinding.Raw.Role);
        Assert.Equal("手动改名", vm.SelectedBinding.Raw.FunctionName);
        Assert.Equal("托盘有无检测", vm.SelectedBinding.RoleText);
    }

    [Fact]
    public void PointTableVm_SelectedBindingChange_ReechoesEditor()
    {
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.PointTableViewModel(fake);
        vm.GenerateTemplateCommand.Execute(null);

        // 生成模板后编辑面板自动回显首行（换设备重建网格时 setter 联动）
        Assert.Equal(vm.Bindings[0].Raw.Bit.ToString(), vm.EditBit);
        Assert.Equal(vm.Bindings[0].Raw.FunctionName, vm.EditFunction);

        // 点选另一行（位号 2 的输入行=托盘检测）→ 编辑面板立刻切到该行。
        // 此前编辑面板停留首行不随选中变化（V0.0.36 修复的 bug）；
        // DataGrid SelectedItem 双向绑定走的就是 VM 层 setter，此用例同路径。
        var row = vm.Bindings.First(b => b.Raw.IsInput && b.Raw.Bit == 2);
        Assert.NotEqual(row.Raw.FunctionName, vm.Bindings[0].Raw.FunctionName); // 前提：两行内容确实不同
        vm.SelectedBinding = row;

        Assert.Equal(row.Raw.Block.ToString(), vm.EditBlock);
        Assert.Equal(row.Raw.Bit.ToString(), vm.EditBit);
        Assert.Equal(row.Raw.IsInput, vm.EditIsInput);
        Assert.Equal(row.Raw.Role, vm.EditRole);
        Assert.Equal(row.Raw.Rack.ToString(), vm.EditRack);
        Assert.Equal(row.Raw.Level.ToString(), vm.EditLevel);
        Assert.Equal(row.Raw.FunctionName, vm.EditFunction);
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

        Assert.Equal(2, vm.Policies.Count);
        Assert.Equal("乙", vm.SelectedPolicy!.Name);  // 缺省选中激活策略
        Assert.Equal("30", vm.EditGlobalMinutes);     // 编辑区已回显
    }

    [Fact]
    public void PolicyVm_Save_WritesOverridesToDisk()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        vm.EditGlobalMinutes = "90";
        vm.EditLevel2Min = "45";
        vm.EditPersistMinutes = true;   // 断电续算勾选 → 落盘往返
        vm.AddSlotRowCommand.Execute(null);
        vm.SlotRows[0].Rack = "3";
        vm.SlotRows[0].Level = "2";
        vm.SlotRows[0].Minutes = "20";
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
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);
        Assert.Contains("自定义", vm.PolicyText);   // 缺省库激活名（V0.0.20 三轮）

        // 登录 L4（策略窗现在有门禁：L3+，未登录会被 Gate 拦下）→ 激活"快固"落盘 → 顶栏刷新
        fake.LoginResult = ("admin", OperatorLevel.Level4);
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

    // ---------- 登录与权限门禁（P3.2） ----------

    [Fact]
    public void MainVm_Gate_BlocksWithoutLogin()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        vm.OpenWorkOrderCommand.Execute(null);   // 未登录 → 拦下
        Assert.Contains(fake.Calls, c => c.Contains("未登录"));
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("ShowWorkOrder"));

        vm.OpenOperatorsCommand.Execute(null);   // 未登录 → 权限管理也拦
        Assert.DoesNotContain(fake.Calls, c => c == "ShowOperators");
    }

    [Fact]
    public void MainVm_Login_AndLevelGate()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // L1 登录：工单可开、权限管理拦（需 L4）、策略拦（需 L3）
        fake.LoginResult = ("op1", OperatorLevel.Level1);
        vm.LoginCommand.Execute(null);
        // 顶栏格式"操作员：op1"（V0.0.20 口径：权限短名开头，不带 LEVEL 字样）
        Assert.Equal("操作员：op1", vm.OperatorText);

        vm.OpenWorkOrderCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.StartsWith("ShowWorkOrder:Level1"));

        vm.OpenOperatorsCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("权限不足"));

        // L4 重登：全部放行
        fake.LoginResult = ("admin", OperatorLevel.Level4);
        vm.LoginCommand.Execute(null);
        vm.OpenOperatorsCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowOperators");
        vm.OpenPointTableCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowPointTable");
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

        fake.LoginResult = ("op1", OperatorLevel.Level1);  // L1 即可开（只读不碰配置）
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

    [Fact(DisplayName = "演示开关门禁：未登录拦下、L1 拦下（需 L4）")]
    public void MainVm_ToggleSimulate_GateBlocksBelowLevel4()
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

        fake.LoginResult = ("op1", OperatorLevel.Level1);   // L1 不够（需 L4）
        vm.LoginCommand.Execute(null);
        vm.ToggleSimulateCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("权限不足"));
        Assert.False(vm.IsSimulate);
        Assert.Equal("0", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0"));
        Assert.Equal("演示：关", vm.SimulateText);
    }

    [Fact(DisplayName = "演示开关：L4 切换写回 ini + 文案跟随 + 总线热切换")]
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
        fake.LoginResult = ("admin", OperatorLevel.Level4);
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
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level4);

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
    public void WorkOrderVm_LevelGate_BlocksLowLevelEdits()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 预置工单：途程/制程已有值
        WorkOrderStore.Save(new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "原途程", PresetQty = 50, ProducedQty = 5 },
            PtNo1 = "原制程",
        });
        var fake = new FakeCureDialogService();

        // L1：途程/数量/制程全锁，只能录员工工号/条码
        var vm1 = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level1);
        Assert.False(vm1.CanEditRoute);
        Assert.False(vm1.CanEditProcess);
        vm1.EditRtRcNo = "越权改";      // 就算绕过 IsEnabled 直改属性，Flush 也兜底回原值
        vm1.EditPtNo1 = "越权制程";
        vm1.EditEmpNo = "EMP009";
        vm1.SaveCommand.Execute(null);
        Assert.True(vm1.CloseDialog);
        var loaded1 = WorkOrderStore.Load()!;
        Assert.Equal("原途程", loaded1.Right.RcNo);   // L1 改不动途程
        Assert.Equal("原制程", loaded1.PtNo1);        // L1 改不动制程
        Assert.Equal("EMP009", loaded1.EmpNo);        // 员工工号可录

        // L2：途程/数量/清零可改，制程仍锁
        var vm2 = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level2);
        Assert.True(vm2.CanEditRoute);
        Assert.False(vm2.CanEditProcess);
        vm2.ClearCountRtCommand.Execute(null);        // 计数清零（L2 权限）
        Assert.Equal("0", vm2.EditRtProduced);

        // L3：制程解锁
        var vm3 = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level3);
        Assert.True(vm3.CanEditProcess);
    }

    [Fact]
    public void WorkOrderVm_SaveBadNumber_BlocksClose()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level4);
        vm.EditRtPreset = "abc";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("数字格式"));
    }

    // ---------- 权限管理窗 VM（P3.2） ----------

    [Fact]
    public void OperatorVm_AddRemove_RoundTrip()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("test-admin-pwd");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.OperatorViewModel(fake, store);

        Assert.Single(vm.Users); // 播种的 admin

        // 新增：弱密码拦下弹框
        vm.EditUserName = "op1";
        vm.EditLevel = OperatorLevel.Level1;
        vm.EditPassword = "123";
        vm.AddUserCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("新增失败"));

        // 新增成功 → 列表刷新
        vm.EditPassword = "abc123";
        vm.AddUserCommand.Execute(null);
        Assert.Equal(2, vm.Users.Count);
        Assert.Contains(vm.Users, u => u.UserName == "op1");

        // 删 admin（最后一个 L4）→ 防锁死弹框
        vm.SelectedUser = vm.Users.First(u => u.UserName == "admin");
        vm.RemoveUserCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.Contains("删除失败"));

        // 删 op1 → Confirm 后删掉
        vm.SelectedUser = vm.Users.First(u => u.UserName == "op1");
        vm.RemoveUserCommand.Execute(null);
        Assert.Single(vm.Users);
    }

    [Fact]
    public void OperatorVm_SetLevelAndPassword_RoundTrip()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm2.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("test-admin-pwd");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.OperatorViewModel(fake, store);

        vm.SelectedUser = vm.Users.First(u => u.UserName == "admin");
        vm.EditLevel = OperatorLevel.Level3;
        vm.SetLevelCommand.Execute(null);        // 降最后一个 L4 → 防锁死
        Assert.Contains(fake.Calls, c => c.Contains("改级别失败"));

        // 加第二个 L4 后，降级放行（重开 VM 拿最新列表）
        store.AddUser("boss", OperatorLevel.Level4, "abc123");
        var vm2 = new CureStation.ViewModels.OperatorViewModel(fake, store);
        vm2.SelectedUser = vm2.Users.First(u => u.UserName == "admin");
        vm2.EditLevel = OperatorLevel.Level3;
        vm2.SetLevelCommand.Execute(null);
        Assert.DoesNotContain(fake.Calls.Skip(1), c => c.Contains("改级别失败")); // 第二次成功
        Assert.Equal(OperatorLevel.Level3, store.Verify("admin", "test-admin-pwd"));
    }

    [Fact]
    public void OperatorVm_SetPassword_RoundTrip()
    {
        using var scope = new TestScope();
        var dbPath = Path.Combine(scope.TempDir, "ops-vm3.db3");
        var store = new OperatorStore(new SqliteHelper(dbPath));
        store.EnsureSeeded("test-admin-pwd");
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.OperatorViewModel(fake, store);

        vm.SelectedUser = vm.Users.First(u => u.UserName == "admin");
        vm.EditPassword = "new-pwd-9";
        vm.SetPasswordCommand.Execute(null);

        Assert.Contains(fake.Calls, c => c.Contains("改密码成功"));
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "new-pwd-9"));
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
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level4,
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
        var vm = new CureStation.ViewModels.WorkOrderViewModel(fake, OperatorLevel.Level4);

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

        // L1 → 拦（过账配置 L4 专属）
        fake.LoginResult = ("op1", OperatorLevel.Level1);
        vm.LoginCommand.Execute(null);
        vm.OpenPostConfigCommand.Execute(null);
        Assert.DoesNotContain(fake.Calls, c => c == "ShowMesPostConfig");

        // L4 → 放行；ini 预置 Enabled=1（模拟现场保存过）→ 保存成功后重读配置
        // 热更新过账服务 + 顶栏状态刷新（不重启）
        fake.LoginResult = ("admin", OperatorLevel.Level4);
        vm.LoginCommand.Execute(null);
        IniFile.Write(AppPaths.ConfigIni, "CuringMes", "Enabled", "1");
        fake.MesPostConfigResult = true;
        vm.OpenPostConfigCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c == "ShowMesPostConfig");
        Assert.Contains("已启用", vm.PostingText);
    }

    // ---------- 登录窗 VM（V0.0.20 对齐 HJVision mFormLogin：记住/跟随/改密） ----------

    /// <summary>登录 VM 测试装配：临时库播种 + 临时 ini，双隔离（不碰现场 config）。</summary>
    private static (CureStation.ViewModels.LoginViewModel Vm, OperatorStore Store, string IniPath) NewLoginVm(
        TestScope scope, string dbName = "login-vm.db3")
    {
        var store = new OperatorStore(new SqliteHelper(Path.Combine(scope.TempDir, dbName)));
        store.EnsureSeeded("test-admin-pwd");
        string ini = Path.Combine(scope.TempDir, "login.ini");
        return (new CureStation.ViewModels.LoginViewModel(store, ini), store, ini);
    }

    [Fact]
    public void LoginVm_Constructor_PrefillsSavedUserAndPasswordAndHistory()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        store.AddUser("op1", OperatorLevel.Level1, "abc123");
        RunModeSettings.SaveLogin(ini, true, "op1", "abc123");

        // 重新构造（模拟现场第二次打开登录窗）：回填最后账号 + 它记住的密码 + 勾选。
        var vm2 = new CureStation.ViewModels.LoginViewModel(store, ini);
        Assert.Equal("op1", vm2.UserName);
        Assert.Equal("abc123", vm2.Password);
        Assert.True(vm2.Remember);
        // 下拉候选=在册账号实时表（含播种的 admin 与新增的 op1）。
        Assert.Contains("admin", vm2.HistoryUsers);
        Assert.Contains("op1", vm2.HistoryUsers);
        Assert.False(vm2.IsChangePwd);
        Assert.Equal("账号登录", vm2.BannerText);
    }

    [Fact]
    public void LoginVm_UserNameChange_FollowsRememberedPasswordPerAccount()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        store.AddUser("op1", OperatorLevel.Level1, "abc123");
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
    public void LoginVm_Ok_Success_SavesLoginAndBringsOutLevel()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        bool? closed = null;
        vm.RequestClose += ok => closed = ok;

        vm.UserName = "admin";
        vm.Password = "test-admin-pwd";
        vm.Remember = true;
        vm.OkCommand.Execute(null);

        Assert.Equal(OperatorLevel.Level4, vm.Level);
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
        Assert.Null(vm.Level);
        Assert.Contains("错误", vm.Tip);
        Assert.True(vm.TipIsError);
        Assert.Equal("", vm.Password);             // 密码清空重输
    }

    [Fact]
    public void LoginVm_ChangePwd_FullChain_FourGatesThenSuccess()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);

        // ① 入口闸：账号不存在（未播种的 ghost）→ 不进面板。
        vm.UserName = "ghost";
        vm.ChangePwdCommand.Execute(null);
        Assert.False(vm.IsChangePwd);
        Assert.Contains("账号不存在", vm.Tip);

        // 进面板（admin 在册）：横幅带账号名，三框清空。
        vm.UserName = "admin";
        vm.ChangePwdCommand.Execute(null);
        Assert.True(vm.IsChangePwd);
        Assert.Contains("admin", vm.BannerText);
        vm.Remember = true;   // 用户勾了记住 → 改密成功后记忆同步新密码（见末尾断言）

        // ② 原密码闸：验不对 → 拦，库未变。
        vm.OldPassword = "not-the-old";
        vm.NewPassword = "abc123";
        vm.NewPassword2 = "abc123";
        vm.SavePwdCommand.Execute(null);
        Assert.True(vm.IsChangePwd);
        Assert.Contains("原密码不正确", vm.Tip);
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "test-admin-pwd"));

        // ③ 规则闸：新密码不足 6 位 → 拦。
        vm.OldPassword = "test-admin-pwd";
        vm.NewPassword = "abc";
        vm.NewPassword2 = "abc";
        vm.SavePwdCommand.Execute(null);
        Assert.Contains("至少", vm.Tip);

        // ④ 一致性闸：两次不一致 → 拦。
        vm.NewPassword = "abc123";
        vm.NewPassword2 = "abc123d";
        vm.SavePwdCommand.Execute(null);
        Assert.Contains("不一致", vm.Tip);
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "test-admin-pwd"));

        // 全过 → 写盘即生效：新密码可登录、旧密码失效；回登录面板 + 绿色成功提示；
        // 记住勾选开着 → 记忆同步新密码（否则下次回填旧密码直接失败的坑）。
        vm.NewPassword = "abc123";
        vm.NewPassword2 = "abc123";
        vm.SavePwdCommand.Execute(null);
        Assert.False(vm.IsChangePwd);
        Assert.Equal("账号登录", vm.BannerText);
        Assert.False(vm.TipIsError);
        Assert.Contains("成功", vm.Tip);
        Assert.Equal(OperatorLevel.Level4, store.Verify("admin", "abc123"));
        Assert.Null(store.Verify("admin", "test-admin-pwd"));
        var (remember, _, pwd) = RunModeSettings.ReadSavedLogin(ini, "admin");
        Assert.True(remember);
        Assert.Equal("abc123", pwd);
    }

    [Fact]
    public void LoginVm_ChangePwd_NoRemember_ClearsSavedPassword()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        // 前置：admin 曾勾记住。
        RunModeSettings.SaveLogin(ini, true, "admin", "test-admin-pwd");

        vm.UserName = "admin";      // 跟随会把 admin 的旧记忆勾上（前置 SaveLogin 过）
        vm.Remember = false;        // 用户显式取消勾选 → 改密成功应清本户记忆
        vm.OldPassword = "test-admin-pwd";
        vm.NewPassword = "abc123";
        vm.NewPassword2 = "abc123";
        vm.SavePwdCommand.Execute(null);
        Assert.False(vm.TipIsError);

        // 没勾记住改密 → 本户记忆清掉（下次全手输，不留旧密码影子）。
        var (remember, _, _) = RunModeSettings.ReadSavedLogin(ini, "admin");
        Assert.False(remember);
    }

    [Fact]
    public void LoginVm_Back_ReturnsToLoginPanel()
    {
        using var scope = new TestScope();
        var (vm, store, ini) = NewLoginVm(scope);
        vm.UserName = "admin";
        vm.ChangePwdCommand.Execute(null);
        Assert.True(vm.IsChangePwd);

        vm.BackCommand.Execute(null);
        Assert.False(vm.IsChangePwd);
        Assert.Equal("账号登录", vm.BannerText);
        Assert.Equal("", vm.Tip);
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
    public void PolicyVm_ApplyToCustom_WritesCustomActivatesAndKeepsPreset()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 选预置"快速固化"改参（现场改工艺时长的典型路径）
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.EditGlobalMinutes = "90";

        vm.ApplyToCustomCommand.Execute(null);

        Assert.True(vm.CloseDialog);
        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("自定义", loaded!.ActiveName);        // 套用即激活自定义
        var custom = loaded.Policies.First(p => p.Name == "自定义" && !p.IsPreset);
        Assert.Equal(90, custom.GlobalMinutes);            // 编辑值落到自定义
        var preset = loaded.Policies.First(p => p.Name == "快速固化");
        Assert.Equal(60, preset.GlobalMinutes);            // 预置本体保持原样（模板可回溯）
        Assert.True(preset.IsPreset);
        Assert.False(vm.SelectedIsPreset);                 // 套用后选中的是自定义
        Assert.Equal(90, vm.SelectedPolicy!.GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_ApplyToCustom_ReplacesOverridesNotMerges()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        // 第一次套用"分层固化"：自定义带上层覆盖
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "分层固化");
        vm.ApplyToCustomCommand.Execute(null);
        Assert.Equal(90, vm.SelectedPolicy!.LevelMinutes[1]);

        // 第二次套用"快速固化"（无层覆盖）：自定义覆盖行整体替换，不残留
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.ApplyToCustomCommand.Execute(null);
        Assert.Empty(vm.SelectedPolicy!.LevelMinutes);
        Assert.Equal("", vm.EditLevel2Min);                // 编辑区回显同步（无残留旧值）

        var loaded = CuringPolicyStore.Load();
        var custom = loaded!.Policies.First(p => p.Name == "自定义" && !p.IsPreset);
        Assert.Empty(custom.LevelMinutes);
        Assert.Equal(60, custom.GlobalMinutes);
    }

    [Fact]
    public void PolicyVm_ApplyToCustom_CreatesCustomIfMissing()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 盘上只有预置策略（无自定义）——套用应创建并激活
        var book = new CuringPolicyBook
        {
            Policies = { new CuringPolicy { Name = "标准固化", GlobalMinutes = 120, IsPreset = true } },
            ActiveName = "标准固化",
        };
        CuringPolicyStore.Save(book);
        var fake = new FakeCureDialogService();
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);

        vm.ApplyToCustomCommand.Execute(null);

        var loaded = CuringPolicyStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("自定义", loaded!.ActiveName);
        Assert.Contains(loaded.Policies, p => p.Name == "自定义" && !p.IsPreset);
    }

    [Fact]
    public void PolicyVm_ApplyToCustom_BadNumberBlocks()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        var fake = new FakeCureDialogService();
        CuringPolicyStore.Save(CuringPolicyStore.BuildDefaultBook());   // 等价自愈后的盘面
        var vm = new CureStation.ViewModels.CuringPolicyViewModel(fake);
        vm.SelectedPolicy = vm.Policies.First(p => p.Name == "快速固化");
        vm.EditGlobalMinutes = "abc";

        vm.ApplyToCustomCommand.Execute(null);

        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("数字格式"));
        var loaded = CuringPolicyStore.Load();
        // 被拦后自定义保持缺省值 120（编辑错误未污染既有自定义策略）
        Assert.Equal(120, loaded!.Policies.First(p => p.Name == "自定义" && !p.IsPreset).GlobalMinutes);
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

        // 保存拦（出厂模板保持原样——出口只有"套用为自定义"）
        vm.SaveCommand.Execute(null);
        Assert.False(vm.CloseDialog);
        Assert.Contains(fake.Calls, c => c.Contains("套用为自定义"));

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
        vm.AddSlotRowCommand.Execute(null);
        vm.SlotRows[0].Rack = "3";
        vm.SlotRows[0].Level = "2";
        vm.SlotRows[0].Minutes = "45";
        Assert.Equal(45, vm.SlotPreviewItems.First(i => i.Rack == 3 && i.Level == 2).Minutes);
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 2 && i.Rack != 3), i => Assert.Equal(30, i.Minutes));

        // 数字错兜底照画：全局回落原选中值 120，但层1 覆盖 10 与仓位 3-2=45 仍生效
        vm.EditGlobalMinutes = "abc";
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 1), i => Assert.Equal(10, i.Minutes));
        Assert.All(vm.SlotPreviewItems.Where(i => i.Level == 2 && i.Rack != 3), i => Assert.Equal(120, i.Minutes));
        Assert.Equal(45, vm.SlotPreviewItems.First(i => i.Rack == 3 && i.Level == 2).Minutes);

        // 清空仓位行 → 该格回落层/全局
        vm.RemoveSlotRowCommand.Execute(null);
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
}
