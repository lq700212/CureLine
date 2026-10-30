// =========================================================================
// FakeDialogService：IDialogService 测试桩（VM 可测性的另一半）
//
// 用法：new 后按需设返回值（LoginResult/PlcResult），
// 调后查 Calls 断 VM 调了谁。VM 构造注入它，不碰真窗。
// =========================================================================

using CureLine.Core.Infrastructure;
using CimGateway.ViewModels;
using CureLine.ViewModels;

namespace CureLine.Tests.Mocks;

public class FakeDialogService : IDialogService
{
    public List<string> Calls { get; } = new();
    public (bool Ok, LoginRole Role, string User) LoginResult { get; set; } =
        (false, LoginRole.None, "");
    public bool PlcResult { get; set; }
    public bool? ConfirmResult { get; set; } = true;
    public bool? ConfirmOrCancelResult { get; set; } = true;

    public void ShowMessage(string message, string title,
        DialogImage image = DialogImage.Info) =>
        Calls.Add($"Msg:{title}:{message}");

    public bool Confirm(string message, string title)
    {
        Calls.Add($"Confirm:{title}");
        return ConfirmResult ?? false;
    }

    public bool? ConfirmOrCancel(string message, string title)
    {
        Calls.Add($"ConfirmOrCancel:{title}");
        return ConfirmOrCancelResult;
    }

    public (bool Ok, LoginRole Role, string User) ShowLogin()
    {
        Calls.Add("Login");
        return LoginResult;
    }

    public void ShowChangePassword(string currentUser, LoginRole role) =>
        Calls.Add($"ChangePwd:{currentUser}:{role}");

    public bool ShowGatewayConfig()
    {
        Calls.Add("GatewayConfig");
        return PlcResult;
    }

    public void RestartApp() => Calls.Add("Restart");

    public void CreateDesktopShortcut() => Calls.Add("Shortcut");

    public void ShowAlarm(string message, string name) =>
        Calls.Add($"Alarm:{name}");

    public void ShowImage(string imagePath) =>
        Calls.Add($"Image:{imagePath}");

    public void ShowMesLog(int station) =>
        Calls.Add($"MesLog:{station}");

    public void ShowMesDebug() => Calls.Add("MesDebug");
}
