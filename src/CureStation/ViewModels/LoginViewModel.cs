// =========================================================================
// LoginViewModel：程序①登录窗（三级操作员账号）
//
// 干什么：operators 表验谁 + 角色带出（工单/策略/IO/过账/账号管理/点表的
//   门禁依据）。登录面板：用户名下拉列出在册账号（可编辑可选可手输——取
//   ListUsers 实时表，比"登录历史文件"干净：无已删账号残留，零新存储）；
//   密码跟随账号：框里是谁就填谁记住的密码（有→回填+勾选记住，无→清空
//   +取消勾选），分账号独立（复用 Core RunModeSettings 记忆件，DPAPI 密文
//   存 config.ini [setting]，ini 里永无明文）。
// 为什么没有改密面板（V0.0.49 用户拍板）：操作员无改密权（只能登录），
//   改密全部走用户管理窗（admin/dev 重置，免验原密码）——登录窗回归纯登录。
// 与主程序登录窗同款 MVVM 模式：PasswordBox 密码经事件转交（不可绑定，官方
// workaround）；VM 侧改密码（预填/跟随账号）经 PropertyChanged 通知 View
// 回写密码框（见 LoginWindow 的 Wire 回写，防循环靠"同值不再发"）。
// 成功发 RequestClose(true)，角色由 Role 属性带出（DialogService.ShowLogin 读）。
// 怎么改：门禁语义见 OperatorRoleInfo；换存储/ini 路径传构造（测试用临时库
// + 临时 ini 双隔离）。键盘链（回车跳框/Esc）在 View 层 PreviewKeyDown，
// 这里只管业务。
// =========================================================================

using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly OperatorStore _store;
    private readonly string _iniPath;
    private string _userName = "";
    private string _password = "";
    private bool _remember;
    private string _tip = "";
    // 跟随账号防重入：UserName setter 触发 FollowPassword 改 Password/Remember，
    // 通知 View 回写期间不再反向触发（与主程序 LoginViewModel 同款套路）。
    private bool _following;

    public LoginViewModel(OperatorStore? store = null, string? iniPath = null)
    {
        _store = store ?? OperatorStore.Default;
        _iniPath = iniPath ?? AppPaths.ConfigIni;

        // 在册账号下拉（实时表）：表不存在等异常静默降级成空列表——
        // 现场删库后登录窗仍能开（App 启动链会重建表+播种）。
        try
        {
            HistoryUsers = _store.ListUsers().Select(a => a.UserName).ToList();
        }
        catch
        {
            HistoryUsers = [];
        }

        // 预填最后一次登录名 + 该账号记住的密码（无记忆则只填名或全空）。
        var (remember, user, pwd) = RunModeSettings.ReadSavedLogin(_iniPath);
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

    /// <summary>登录成功角色（DialogService.ShowLogin 读这个；null=未成功）。</summary>
    public OperatorRole? Role { get; private set; }

    /// <summary>登录成功用户名（去空格后）。</summary>
    public string LoginName { get; private set; } = "";

    /// <summary>关窗请求（View 订阅：设 DialogResult 并 Close）。</summary>
    public event Action<bool?>? RequestClose;

    /// <summary>用户名下拉候选（在册账号，打开窗那一刻的快照）。</summary>
    public IReadOnlyList<string> HistoryUsers { get; }

    // ---- 双向绑定 ----
    public string UserName
    {
        get => _userName;
        set
        {
            if (!Set(ref _userName, value ?? ""))
                return;
            // 跟随账号：用户名改谁，密码/勾选/提示跟谁（内部已发通知）。
            FollowPassword();
        }
    }

    /// <summary>
    /// 密码（View 经 PasswordChanged 事件转交；构造预填/跟随账号时 VM 直设，
    /// View 侧订阅 PropertyChanged 回写密码框）。
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

    /// <summary>提示条文本（登录失败等错误态）。</summary>
    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>用户名改了就跟出该账号记住的密码（跟随账号语义）。</summary>
    private void FollowPassword()
    {
        if (_following)
            return;
        _following = true;
        try
        {
            var (remember, _, pwd) = RunModeSettings.ReadSavedLogin(_iniPath, _userName);
            _password = remember ? pwd : "";
            _remember = remember;
            Tip = "";
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
        var role = _store.Verify(_userName, _password);
        if (role == null)
        {
            Tip = "用户名或密码错误，请重新输入";
            Password = "";
            return;
        }
        // 登录记忆落盘（勾记住→存本户密文；没勾→清本户）——mFormLogin 同语义。
        RunModeSettings.SaveLogin(_iniPath, _remember, _userName, _password);
        Role = role;
        LoginName = _userName.Trim();
        RequestClose?.Invoke(true);
    }
}
