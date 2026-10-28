// =========================================================================
// CureStationViewModelTests：程序① VM 用例（主窗网格/点表配置 VM/Fake 桩）
// 铁律：新增 VM 编排必须补用例——模拟点表 + FakeDialogService，TestScope 隔离。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.Core.Infrastructure;
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
}
