// =========================================================================
// DialogService：IDialogService 的 WPF 真实现（生产用单例）
//
// 干什么：把服务调用翻译成真窗体/真提示框；VM 侧只认接口。
// 线程：WPF 窗体必须在 UI 线程建——VM 构造/命令都在 UI 线程调服务，
//   只有 MainWindowVM 的后台节拍不直接调服务（先 Post 回 UI 再调命令）。
// 怎么改：新增窗体时接口加方法、这里加实现，两处同步。
// =========================================================================

using System.Diagnostics;
using System.IO;
using System.Windows;
using PlcMesBridge.Core.Infrastructure;

namespace CimGateway.ViewModels;

public sealed class DialogService : IDialogService
{
    public static IDialogService Instance { get; } = new DialogService();

    private DialogService()
    {
    }

    // 所有弹窗挂主窗为 Owner：CenterOwner 的有主可依（居主窗中央），
    // 关主窗时非模态窗（报警/图片/日志/调试）跟着收，不残留。
    private static T Prep<T>(T w) where T : Window
    {
        if (Application.Current?.MainWindow != null && !ReferenceEquals(w, Application.Current.MainWindow))
            w.Owner = Application.Current.MainWindow;
        return w;
    }

    private static MessageBoxImage ToImage(DialogImage image) => image switch
    {
        DialogImage.Warning => MessageBoxImage.Warning,
        DialogImage.Error => MessageBoxImage.Error,
        DialogImage.Question => MessageBoxImage.Question,
        _ => MessageBoxImage.Information,
    };

    public void ShowMessage(string message, string title, DialogImage image = DialogImage.Info) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, ToImage(image));

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool? ConfirmOrCancel(string message, string title) =>
        MessageBox.Show(message, title,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public (bool Ok, LoginRole Role, string User) ShowLogin()
    {
        var w = Prep(new LoginWindow());
        if (w.ShowDialog() != true)
            return (false, LoginRole.None, "");
        return (true, w.ViewModel.Role, w.ViewModel.UserName);
    }

    public void ShowChangePassword(string currentUser, LoginRole role)
    {
        Prep(new ChangePasswordWindow(currentUser, role)).ShowDialog();
    }

    public bool ShowPlcConfig()
    {
        var w = Prep(new PlcConfigWindow());
        return w.ShowDialog() == true && w.ViewModel.PlcChanged;
    }

    public void ShowAlarm(string message, string name) =>
        Prep(new MesAlarmWindow(message, name)).Show();

    public void ShowImage(string imagePath) =>
        Prep(new ImageWindow(imagePath)).Show();

    public void ShowMesLog(int station) =>
        Prep(new MesLogWindow(station)).Show();

    public void ShowMesDebug() =>
        Prep(new MesDebugWindow()).Show();

    public void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null)
        {
            ShowMessage(LanguageService.Tr("设置已保存，重启软件后生效"),
                LanguageService.Tr("设置"));
            return;
        }
        // 先拉新进程再退当前进程（主窗关窗跳过退出确认，保证旧进程一定退出）。
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    public void CreateDesktopShortcut()
    {
        try
        {
            // net8 取当前 exe 全路径（单文件/普通发布都准；极端拿不到才回退基址拼进程名）。
            string exePath = Environment.ProcessPath
                ?? Path.Combine(AppPaths.BaseDir,
                    Process.GetCurrentProcess().ProcessName + ".exe");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var (result, path) = DesktopShortcut.EnsureOnDesktop(exePath, desktop);
            string msg = result == ShortcutResult.Created
                ? LanguageService.Tr("桌面快捷方式已创建") + "\n" + path
                : LanguageService.Tr("桌面快捷方式已存在") + "\n" + path;
            ShowMessage(msg, LanguageService.Tr("设置"));
        }
        catch (Exception ex)
        {
            // COM 被禁/桌面不可写等：原样报原因，不吞异常（方便现场排查）。
            ShowMessage(LanguageService.Tr("发生错误:") + ex.Message,
                LanguageService.Tr("设置"), DialogImage.Warning);
        }
    }
}
