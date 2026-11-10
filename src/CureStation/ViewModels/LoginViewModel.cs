// =========================================================================
// LoginViewModel：程序①登录窗（三级操作员账号）
//
// 干什么：operators 表验谁 + 角色带出（工单/策略/IO/过账/账号管理/点表的
//   门禁依据）。登录面板：用户名下拉列出在册账号（可编辑可选可手输——取
//   ListUsers 实时表，比"登录历史文件"干净：无已删账号残留，零新存储）；
//   密码跟随账号：框里是谁就填谁记住的密码（有→回填+勾选记住，无→清空
//   +取消勾选），分账号独立（复用 Core RunModeSettings 记忆件，DPAPI 密文
//   存 config.ini [setting]，ini 里永无明文）。
// 修改密码面板（V0.0.93 用户拍板加回——V0.0.49 曾删）：登录窗左下"修改
//   密码"按钮进改密模式（同窗面板互斥切换，不弹新窗）。改的是"自己的
//   密码"：先验旧密码（Verify 拿到本人角色作 actor），再按新密码规则
//   （PasswordHasher.ValidateNewPassword：≥6 位/不得与旧相同——UI 与
//   Store 同一口径）落 SetPassword。防锁死红线不受影响：dev 密码只有
//   dev 本人可改——登录窗恰好先验旧密=本人到场。勾了"记住密码"的账号
//   改密后同步更新记忆密文（否则下次回填旧密码必登录失败）。成功后切回
//   登录模式并把新密码填进登录密码框，直接点登录即可。
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
    private bool _tipIsError = true; // 提示条配色：true=红（错误，缺省）false=绿（成功）
    private bool _isChangeMode;      // true=修改密码面板（登录面板隐藏）
    private string _oldPassword = "";
    private string _newPassword = "";
    private string _confirmPassword = "";
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
        EnterChangeModeCommand = new RelayCommand(EnterChangeMode);
        BackToLoginCommand = new RelayCommand(BackToLogin);
        ChangePasswordCommand = new RelayCommand(ChangePassword);
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

    /// <summary>提示条文本（登录失败/改密结果）。</summary>
    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    /// <summary>提示条配色（true=红错误缺省 / false=绿成功——改密成功用）。</summary>
    public bool TipIsError
    {
        get => _tipIsError;
        private set => Set(ref _tipIsError, value);
    }

    /// <summary>true=修改密码面板（登录面板隐藏；同窗互斥切换不弹新窗）。</summary>
    public bool IsChangeMode
    {
        get => _isChangeMode;
        private set
        {
            if (Set(ref _isChangeMode, value))
                Raise(nameof(ModeTitle));
        }
    }

    /// <summary>顶部横幅标题（随模式切换）。</summary>
    public string ModeTitle => _isChangeMode ? "修改密码" : "账号登录";

    // ---- 修改密码三框（View 经 PasswordChanged 事件转交，同 Password 模式） ----
    public string OldPassword { get => _oldPassword; set => Set(ref _oldPassword, value ?? ""); }
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value ?? ""); }
    public string ConfirmPassword { get => _confirmPassword; set => Set(ref _confirmPassword, value ?? ""); }

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand EnterChangeModeCommand { get; }
    public RelayCommand BackToLoginCommand { get; }
    public RelayCommand ChangePasswordCommand { get; }

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
            TipIsError = true;
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
            ShowTip("用户名或密码错误，请重新输入", true);
            Password = "";
            return;
        }
        // 登录记忆落盘（勾记住→存本户密文；没勾→清本户）——mFormLogin 同语义。
        RunModeSettings.SaveLogin(_iniPath, _remember, _userName, _password);
        Role = role;
        LoginName = _userName.Trim();
        RequestClose?.Invoke(true);
    }

    // ---------- 修改密码（V0.0.93 加回：本人验旧密改自己密码） ----------

    /// <summary>进修改密码面板：清三框与提示，从零输入。</summary>
    private void EnterChangeMode()
    {
        OldPassword = NewPassword = ConfirmPassword = "";
        Tip = "";
        TipIsError = true;
        IsChangeMode = true;
    }

    /// <summary>返回登录面板：同样清三框与提示（不残留半截输入）。</summary>
    private void BackToLogin()
    {
        OldPassword = NewPassword = ConfirmPassword = "";
        Tip = "";
        TipIsError = true;
        IsChangeMode = false;
    }

    /// <summary>提示条统一出口：文本 + 配色一起给（红=错误缺省，绿=成功）。</summary>
    private void ShowTip(string text, bool isError)
    {
        TipIsError = isError;
        Tip = text;
    }

    /// <summary>
    /// 确认修改：验旧密码（拿本人角色作 actor）→ 两次一致 → 新密码规则 →
    /// SetPassword 落库 → 同步"记住密码"记忆 → 切回登录并把新密码填进登录框。
    /// 每一步失败都留在改密面板红字提示（旧密错清旧密框，规则/一致性错清两个新框）。
    /// </summary>
    private void ChangePassword()
    {
        var u = _userName.Trim();
        if (string.IsNullOrEmpty(u))
        {
            ShowTip("请先选择或输入用户名", true);
            return;
        }
        // 防枚举与登录同口径：用户不存在/旧密码错一律"旧密码不正确"。
        var role = _store.Verify(u, _oldPassword);
        if (role == null)
        {
            ShowTip("旧密码不正确，请重新输入", true);
            OldPassword = "";
            return;
        }
        if (!string.Equals(_newPassword, _confirmPassword, StringComparison.Ordinal))
        {
            ShowTip("两次输入的新密码不一致", true);
            NewPassword = "";
            ConfirmPassword = "";
            return;
        }
        // 规则统一走 PasswordHasher（≥6 位/不得与旧相同），UI 与 Store 同一口径；
        // Store 的 SetPassword 里还会再兜一道（用户管理窗"免验旧密"场景旧密传 null）。
        var rule = PasswordHasher.ValidateNewPassword(_newPassword, _oldPassword);
        if (rule != null)
        {
            ShowTip(rule, true);
            NewPassword = "";
            ConfirmPassword = "";
            return;
        }
        var err = _store.SetPassword(u, _newPassword, role.Value);
        if (err != null)
        {
            ShowTip(err, true);
            return;
        }
        // 勾了"记住密码"的账号：记忆密文同步换新（否则下次回填旧密码必登录失败）。
        var (remember, _, _) = RunModeSettings.ReadSavedLogin(_iniPath, u);
        if (remember)
            RunModeSettings.SaveLogin(_iniPath, true, u, _newPassword);
        var newPwd = _newPassword; // 先存：清框会把字段一起清掉，再赋登录框就成空串了
        OldPassword = NewPassword = ConfirmPassword = "";
        IsChangeMode = false;
        Password = newPwd; // 新密码直接填进登录密码框，点登录即可
        ShowTip("密码修改成功，请用新密码登录", false);
    }
}
