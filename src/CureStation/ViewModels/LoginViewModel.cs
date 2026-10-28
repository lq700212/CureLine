// =========================================================================
// LoginViewModel：程序①登录窗（四级操作员）——登录 + 改密双面板
//
// 干什么：operators 表验谁 + 级别带出（工单页/策略窗/点表/权限管理的门禁
//   依据）；外加"修改密码"面板（对齐参考 HJVision mFormLogin 功能清单）：
//   ① 用户名下拉列出在册账号（可编辑可选可手输——取 ListUsers 实时表，
//     比 mFormLogin 的"登录历史文件"更干净：无已删账号残留，零新存储）；
//   ② 密码跟随账号：框里是谁就填谁记住的密码（有→回填+勾选记住，无→清空
//     +取消勾选），分账号独立（复用 Core RunModeSettings 记忆件，DPAPI 密文
//     存 config.ini [setting]，ini 里永无明文）；
//   ③ 改密四道闸：账号在册 → 原密码验对 → 规则（≥6 位 + 不得与旧相同）→
//     两次一致，过了走 SetPassword 写盘即生效；记住勾选时同步新密码到记忆
//     （否则下次回填旧密码直接失败）。
// 与主程序登录窗同款 MVVM 模式：PasswordBox 密码经事件转交（不可绑定，官方
// workaround）；VM 侧改密码（预填/跟随/改密成功清空）经 PropertyChanged 通知
// View 回写密码框（见 LoginWindow 的 Wire 回写，防循环靠"同值不再发"）。
// 成功发 RequestClose(true)，级别由 Level 属性带出（DialogService.ShowLogin
// 读它，签名不变）。
// 怎么改：门禁级别语义见 OperatorLevelInfo；换存储/ini 路径传构造（测试用
// 临时库 + 临时 ini 双隔离）。键盘链（回车跳框/Esc 回面板）在 View 层
// PreviewKeyDown，这里只管业务。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Security;
using PlcMesBridge.ViewModels;

namespace CureStation.ViewModels;

public sealed class LoginViewModel : ViewModelBase
{
    private readonly OperatorStore _store;
    private readonly string _iniPath;
    private string _userName = "";
    private string _password = "";
    private bool _remember;
    private string _tip = "";
    private bool _tipIsError = true;
    private bool _isChangePwd;
    private string _oldPassword = "";
    private string _newPassword = "";
    private string _newPassword2 = "";
    private string _bannerText = "账号登录";
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
        ChangePwdCommand = new RelayCommand(ChangePwd);
        BackCommand = new RelayCommand(Back);
        SavePwdCommand = new RelayCommand(SavePwd);
    }

    /// <summary>登录成功级别（DialogService.ShowLogin 读这个；null=未成功）。</summary>
    public OperatorLevel? Level { get; private set; }

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

    /// <summary>提示条文本（登录失败/改密各态共用）。</summary>
    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    /// <summary>提示条性质：true=错误（红条）/ false=成功（绿条）。</summary>
    public bool TipIsError
    {
        get => _tipIsError;
        private set => Set(ref _tipIsError, value);
    }

    /// <summary>面板开关：false=登录面板（缺省）/ true=修改密码面板。</summary>
    public bool IsChangePwd
    {
        get => _isChangePwd;
        private set => Set(ref _isChangePwd, value);
    }

    /// <summary>居中横幅（随面板切换："账号登录" / "修改密码（xxx）"）。</summary>
    public string BannerText
    {
        get => _bannerText;
        private set => Set(ref _bannerText, value);
    }

    /// <summary>原密码（View 转交，改密面板）。</summary>
    public string OldPassword
    {
        get => _oldPassword;
        set => Set(ref _oldPassword, value ?? "");
    }

    /// <summary>新密码（View 转交，改密面板）。</summary>
    public string NewPassword
    {
        get => _newPassword;
        set => Set(ref _newPassword, value ?? "");
    }

    /// <summary>确认新密码（View 转交，改密面板）。</summary>
    public string NewPassword2
    {
        get => _newPassword2;
        set => Set(ref _newPassword2, value ?? "");
    }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ChangePwdCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SavePwdCommand { get; }

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
        var level = _store.Verify(_userName, _password);
        if (level == null)
        {
            TipIsError = true;
            Tip = "用户名或密码错误，请重新输入";
            Password = "";
            return;
        }
        // 登录记忆落盘（勾记住→存本户密文；没勾→清本户）——mFormLogin 同语义。
        RunModeSettings.SaveLogin(_iniPath, _remember, _userName, _password);
        Level = level;
        LoginName = _userName.Trim();
        RequestClose?.Invoke(true);
    }

    /// <summary>进改密面板：账号必须先在册（框里填谁就改谁，要验原密码）。</summary>
    private void ChangePwd()
    {
        if (!_store.Exists(_userName))
        {
            TipIsError = true;
            Tip = "账号不存在（只有程序里已创建的账号才能改密）";
            return;
        }
        OldPassword = "";
        NewPassword = "";
        NewPassword2 = "";
        IsChangePwd = true;
        BannerText = $"修改密码（{_userName.Trim()}）";
        Tip = "";
    }

    /// <summary>改密面板返回登录面板（清错误提示，密码保留重进直接重试）。</summary>
    private void Back()
    {
        IsChangePwd = false;
        BannerText = "账号登录";
        Tip = "";
    }

    /// <summary>
    /// 保存新密码：四道闸（在册 → 原密码验对 → 规则 ≥6 位且不得与旧相同 →
    /// 两次一致）过了走 SetPassword 写盘即生效；记住勾选同步新密码到记忆
    /// （没勾清本户，下次全手输）；成功回登录面板并清登录密码框。
    /// </summary>
    private void SavePwd()
    {
        string u = _userName.Trim();
        if (!_store.Exists(u))
        {
            TipIsError = true;
            Tip = "账号不存在（只有程序里已创建的账号才能改密）";
            return;
        }
        if (_store.Verify(u, _oldPassword) == null)
        {
            TipIsError = true;
            Tip = "原密码不正确，请重新输入";
            OldPassword = "";
            return;
        }
        string? rule = PasswordHasher.ValidateNewPassword(_newPassword, _oldPassword);
        if (rule != null)
        {
            TipIsError = true;
            Tip = rule + "，请重新输入";
            NewPassword = "";
            NewPassword2 = "";
            return;
        }
        if (_newPassword != _newPassword2)
        {
            TipIsError = true;
            Tip = "两次输入的新密码不一致，请重新输入";
            NewPassword = "";
            NewPassword2 = "";
            return;
        }
        string? err = _store.SetPassword(u, _newPassword);
        if (err != null)
        {
            TipIsError = true;
            Tip = err;
            return;
        }
        // 记忆同步新密码（SaveLogin 一个调用两义：勾记住=存新密文，没勾=清本户）。
        RunModeSettings.SaveLogin(_iniPath, _remember, u, _newPassword);
        IsChangePwd = false;
        BannerText = "账号登录";
        TipIsError = false;
        Tip = "密码修改成功，下次登录请用新密码";
        Password = "";
    }
}
