// =========================================================================
// LoginViewModel：程序①登录窗（四级操作员）
//
// 干什么：operators 表验谁 + 级别带出（工单页/策略窗/点表/权限管理的门禁依据）。
// 与主程序登录窗同款 MVVM 模式：PasswordBox 密码经事件转交（不可绑定，官方
// workaround）；成功发 RequestClose(true)，级别由 Level 属性带出。
// 怎么改：门禁级别语义见 OperatorLevelInfo；换存储传构造（测试用临时库）。
// =========================================================================

using PlcMesBridge.Core.Security;
using PlcMesBridge.ViewModels;

namespace CureStation.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly OperatorStore _store;
    private string _userName = "";
    private string _password = "";
    private string _tip = "";

    public LoginViewModel(OperatorStore? store = null)
    {
        _store = store ?? OperatorStore.Default;
        OkCommand = new RelayCommand(Ok);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    /// <summary>登录成功级别（DialogService.ShowLogin 读这个；null=未成功）。</summary>
    public OperatorLevel? Level { get; private set; }

    /// <summary>登录成功用户名（去空格后）。</summary>
    public string LoginName { get; private set; } = "";

    /// <summary>关窗请求（View 订阅：设 DialogResult 并 Close）。</summary>
    public event Action<bool?>? RequestClose;

    public string UserName
    {
        get => _userName;
        set => Set(ref _userName, value ?? "");
    }

    /// <summary>密码（View 经 PasswordChanged 事件转交；不可绑定，官方 workaround）。</summary>
    public string Password
    {
        get => _password;
        set => Set(ref _password, value ?? "");
    }

    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void Ok()
    {
        var level = _store.Verify(_userName, _password);
        if (level == null)
        {
            Tip = "用户名或密码错误";
            Password = "";
            return;
        }
        Level = level;
        LoginName = _userName.Trim();
        RequestClose?.Invoke(true);
    }
}
