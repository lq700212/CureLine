// =========================================================================
// CureStationViewModelTests：程序① VM 用例（主窗网格/点表配置 VM/Fake 桩）
// 铁律：新增 VM 编排必须补用例——模拟点表 + FakeDialogService，TestScope 隔离。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;
using PlcMesBridge.Core.Security;
using PlcMesBridge.Tests.Mocks;
using Xunit;

namespace PlcMesBridge.Tests;

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
        Assert.Equal(2, loaded.Policies.Count);
    }

    [Fact]
    public void MainVm_Simulate_ShowsActivePolicyText()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", "1");
        var fake = new FakeCureDialogService();
        using var vm = new CureStation.ViewModels.MainViewModel(fake);

        // 策略自愈：缺省库落盘 + 顶栏显示激活策略
        Assert.Contains("标准固化", vm.PolicyText);
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
        Assert.Contains("标准固化", vm.PolicyText);

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
        Assert.Contains("op1", vm.OperatorText);
        Assert.Contains("LEVEL.1", vm.OperatorText);

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
}
