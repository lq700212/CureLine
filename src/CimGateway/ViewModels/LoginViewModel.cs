// =========================================================================
// LoginViewModel：登录窗的状态 + 命令（MVVM）。
//
// 绑什么：用户名（双向）/ 记住勾选（双向）/ 提示行 + 全部静态文本
//   （中英切换调 RefreshTexts 全刷）/ 确定取消命令。
// PasswordBox 密码传值说明：WPF PasswordBox.Password 不是依赖属性，
//   官方不支持绑定；View 用 PasswordChanged 事件一行转交
//   （见 LoginWindow.xaml.cs），这是社区标准 workaround，不是破窗。
// 校验/记忆走 Core（UserAccountStore + RunModeSettings），VM 只编排。
// 成功关窗：VM 发 RequestClose(true)，View 订阅后设 DialogResult 并 Close
//   （VM 不碰 Window，保持可测）；结果由 Role/UserName 属性带出。
// =========================================================================

using PlcMesBridge.ViewModels;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Security;

namespace CimGateway.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private string _userName = "";
    private string _password = "";
    private bool _remember;
    private string _tip = "";
    private bool _following;

    public LoginViewModel()
    {
        // 预填最后一次登录名 + 该账号记住的密码（无记忆则只填名或全空）。
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(AppPaths.ConfigIni);
        if (!string.IsNullOrEmpty(user))
            _userName = user;
        if (remember)
        {
            _password = pwd;
            _remember = true;
        }
        OkCommand = new RelayCommand(Ok);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    /// <summary>登录成功角色（DialogService.ShowLogin 读这个）。</summary>
    public LoginRole Role { get; private set; } = LoginRole.None;

    /// <summary>登录成功用户名（去空格后；未成功为空）。</summary>
    public string LoginName { get; private set; } = "";

    /// <summary>关窗请求（View 订阅：设 DialogResult 并 Close）。</summary>
    public event Action<bool?>? RequestClose;

    // ---- 双向绑定 ----
    public string UserName
    {
        get => _userName;
        set
        {
            if (!Set(ref _userName, value))
                return;
            // 跟随账号：用户名改谁，密码/勾选/Tip 跟谁（内部已发通知）。
            FollowPassword();
        }
    }

    /// <summary>
    /// 密码（View 经 PasswordChanged 事件转交；构造预填/跟随账号时直接设，
    /// View 侧同步回写密码框，见 LoginWindow.ApplyPassword）。
    /// </summary>
    public string Password
    {
        get => _password;
        set => Set(ref _password, value ?? "");
    }

    public bool Remember
    {
        get => _remember;
        set => Set(ref _remember, value);
    }

    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    // ---- 静态文本（语言切换 RefreshTexts 全刷）----
    public string TitleText => Tr("登录");
    public string HeadText => Tr("请登录");
    public string UserLabel => Tr("用户名");
    public string PwdLabel => Tr("密码");
    public string RememberText => Tr("记住密码");
    public string OkText => Tr("确定");
    public string CancelText => Tr("取消");

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public void RefreshTexts() => RefreshAll();

    private static string Tr(string zh) => LanguageService.Tr(zh);

    /// <summary>用户名改了就跟出该账号记住的密码（跟随账号语义）。</summary>
    private void FollowPassword()
    {
        if (_following)
            return;
        _following = true;
        try
        {
            var (remember, _, pwd) = RunModeSettings.ReadSavedLogin(
                AppPaths.ConfigIni, _userName);
            _password = remember ? pwd : "";
            _remember = remember;
            _tip = "";
            Raise(nameof(Password));
            Raise(nameof(Remember));
            Raise(nameof(Tip));
        }
        finally
        {
            _following = false;
        }
    }

    private void Ok()
    {
        LoginRole role = UserAccountStore.Default.Verify(_userName, _password);
        if (role == LoginRole.None)
        {
            Tip = Tr("用户名或密码错误");
            Password = "";
            return;
        }
        RunModeSettings.SaveLogin(AppPaths.ConfigIni, _remember, _userName, _password);
        Role = role;
        LoginName = _userName.Trim();
        RequestClose?.Invoke(true);
    }
}
