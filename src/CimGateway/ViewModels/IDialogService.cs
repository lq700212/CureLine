// =========================================================================
// IDialogService：对话框服务（VM 开窗/提示的唯一出口，MVVM 可测性的关键）
//
// 干什么：VM 不许直接 new Window / MessageBox（那样 VM 就测不了），
//   一律走本服务；测试用 FakeDialogService 桩，断言 VM 调了谁。
// 为什么按窗体给专用方法：通用 ShowDialog(Type) 靠反射又难传参，
//   专用方法签名即文档，调用方一眼看懂；新增窗体来这里加一个。
// 怎么改：实现是 WPF 真窗（见 DialogService）；VM 构造注入 IDialogService，
//   默认用 DialogService.Instance（生产），测试传 Fake（见 Tests/Mocks）。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CimGateway.ViewModels;

public enum DialogImage
{
    Info,
    Warning,
    Error,
    Question,
}

public interface IDialogService
{
    /// <summary>提示框（确定）。返回前阻塞，纯通知。</summary>
    void ShowMessage(string message, string title,
        DialogImage image = DialogImage.Info);

    /// <summary>是否框（是/否）。返回 true=是。</summary>
    bool Confirm(string message, string title);

    /// <summary>是否取消框（是/否/取消）。返回 true=是，false=否，null=取消。</summary>
    bool? ConfirmOrCancel(string message, string title);

    /// <summary>登录窗。成功返回 (true, 角色, 用户名），取消 (false, None, "")。</summary>
    (bool Ok, LoginRole Role, string User) ShowLogin();

    /// <summary>修改密码窗（需当前登录人 + 角色，dev 可代改）。</summary>
    void ShowChangePassword(string currentUser, LoginRole role);

    /// <summary>PLC 配置窗。返回 true 表示保存了配置（调用方重启）。</summary>
    bool ShowPlcConfig();

    /// <summary>报警窗（非模态置顶，直接 Show）。</summary>
    void ShowAlarm(string message, string name);

    /// <summary>图片查看窗（非模态，直接 Show）。</summary>
    void ShowImage(string imagePath);

    /// <summary>日志窗（-1 单机全量 / >=0 分机台，非模态，直接 Show）。</summary>
    void ShowMesLog(int station);

    /// <summary>调试窗（非模态，直接 Show）。</summary>
    void ShowMesDebug();

    /// <summary>程序化重启（先拉新进程再退当前进程，跳过退出确认）。</summary>
    void RestartApp();

    /// <summary>创建桌面快捷方式（逻辑在 Core，服务只弹结果提示）。</summary>
    void CreateDesktopShortcut();
}
