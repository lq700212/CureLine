// =========================================================================
// ViewModel 测试：VM 编排逻辑（Fake 对话框 + TestScope 隔离落盘）。
//
// 覆盖：登录失败/跟随账号、主窗登录门禁与 gating、
//   修改密码 VM（规则/代改显隐）。后台节拍/真窗不在此测（UIA 冒烟覆盖）。
// 历史：模式窗 VM 用例已随单机链路删除（P2.1）。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Tests.Mocks;
using CimGateway.ViewModels;
using CureLine.ViewModels;

namespace CureLine.Tests;

public class ViewModelTests : IDisposable
{
    private readonly TestScope _scope = new();

    public ViewModelTests()
    {
        // 落盘目录先建（SQLite/kernel32 都不会自动建目录，与生产装配语义一致）。
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DatabaseFile)!);
        Directory.CreateDirectory(AppPaths.ConfigDir);
    }

    public void Dispose() => _scope.Dispose();

    private void SeedAccounts()
    {
        // 种到默认库（VM 经 UserAccountStore.Default 读同一路径；TestScope 已隔离）。
        var store = new UserAccountStore(new SqliteHelper(AppPaths.DatabaseFile));
        store.EnsureSeeded("test-admin-pwd", "test-dev-pwd");
    }

    [Fact(DisplayName = "登录VM失败给提示成功带角色")]
    public void LoginVm_FailTips_SuccessCarriesRole()
    {
        SeedAccounts();
        var vm = new LoginViewModel();
        vm.UserName = "admin";
        vm.Password = "wrong";
        vm.OkCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("用户名或密码错误"), vm.Tip);
        Assert.Equal(LoginRole.None, vm.Role);

        vm.Password = "test-admin-pwd";
        bool? closed = null;
        vm.RequestClose += r => closed = r;
        vm.OkCommand.Execute(null);
        Assert.Equal(LoginRole.Admin, vm.Role);
        Assert.Equal("admin", vm.LoginName);
        Assert.Equal(true, closed);
    }

    [Fact(DisplayName = "登录VM用户名跟随账号密码")]
    public void LoginVm_FollowsAccountPassword()
    {
        SeedAccounts();
        string ini = AppPaths.ConfigIni;
        RunModeSettings.SaveLogin(ini, true, "dev", "test-dev-pwd");
        var vm = new LoginViewModel();
        Assert.Equal("dev", vm.UserName);
        Assert.True(vm.Remember);
        vm.UserName = "admin";
        Assert.False(vm.Remember);
        Assert.Equal("", vm.Password);
    }

    [Fact(DisplayName = "主窗VM未登录拦登录后放行")]
    public void MainVm_Gating_BlocksThenPasses()
    {
        SeedAccounts();
        var fake = new FakeDialogService
        {
            LoginResult = (true, LoginRole.Dev, "dev"),
            PlcResult = true,
        };
        using var vm = new MainWindowViewModel(fake);
        // 未登录点 PLC 设置：提示登录，不开配置窗
        vm.ShowGatewayCommand.Execute(null);
        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
        Assert.DoesNotContain("GatewayConfig", fake.Calls);
        // 登录后：开配置窗；保存返回 true 走热重建连接（V0.0.41 起
        // RestartConnections 后台重建，程序化重启链路已删，不再有 Restart 调用）
        vm.LoginCommand.Execute(null);
        Assert.Equal($"dev ({LanguageService.Tr("切换/退出")})", vm.LoginHeader);
        Assert.True(vm.GatewayVisible);
        vm.ShowGatewayCommand.Execute(null);
        Assert.Contains("GatewayConfig", fake.Calls);
    }

    [Fact(DisplayName = "主窗VM退出登录清门禁")]
    public void MainVm_Logout_ClearsGating()
    {
        SeedAccounts();
        var fake = new FakeDialogService
        {
            LoginResult = (true, LoginRole.Admin, "admin"),
            ConfirmOrCancelResult = true,
        };
        using var vm = new MainWindowViewModel(fake);
        vm.LoginCommand.Execute(null);
        Assert.Contains("admin", vm.LoginHeader);
        Assert.False(vm.GatewayVisible);
        // 再点登录 → 确认退出（Yes）→ 清态
        vm.LoginCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("登录"), vm.LoginHeader);
        Assert.False(vm.GatewayVisible);
    }

    [Fact(DisplayName = "改密码VM两次不一致拦成功提示")]
    public void ChangePwdVm_MismatchBlocks_SuccessNotifies()
    {
        SeedAccounts();
        var fake = new FakeDialogService();
        var vm = new ChangePasswordViewModel("admin", LoginRole.Admin, fake);
        Assert.False(vm.CanPickUser);
        Assert.True(vm.NeedOldPassword);
        vm.NewPassword = "a";
        vm.ConfirmPassword = "b";
        vm.OkCommand.Execute(null);
        Assert.Equal(LanguageService.Tr("两次新密码不一致"), vm.Tip);

        vm.OldPassword = "test-admin-pwd";
        vm.NewPassword = "new-pwd-1";
        vm.ConfirmPassword = "new-pwd-1";
        bool? closed = null;
        vm.RequestClose += r => closed = r;
        vm.OkCommand.Execute(null);
        Assert.Equal(true, closed);
        Assert.Contains(fake.Calls, c => c.StartsWith("Msg:"));
    }

    [Fact(DisplayName = "改密码VMdev代改藏旧密码行")]
    public void ChangePwdVm_DevProxy_HidesOldRow()
    {
        SeedAccounts();
        var fake = new FakeDialogService();
        var vm = new ChangePasswordViewModel("dev", LoginRole.Dev, fake);
        Assert.True(vm.CanPickUser);
        Assert.True(vm.NeedOldPassword);
        vm.SelectedUser = "admin";
        Assert.False(vm.NeedOldPassword);
    }
}
