// =========================================================================
// ChangePasswordViewModel：修改密码窗的状态 + 命令（MVVM）。
//
// 绑什么：账号下拉（dev 全列，admin 锁定自己）/ 旧密码行显隐 / 三个密码框
//   （经事件转交，PasswordBox 不可绑定，见 LoginViewModel 注释）/
//   提示行 + 静态文本 + 确定取消命令。
// 规则与落盘走 Core（PasswordHasher + UserAccountStore），VM 只编排；
// 成功提示经 IDialogService（VM 不碰 MessageBox）。
// =========================================================================

using CureLine.ViewModels;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;

namespace CimGateway.ViewModels;

public class ChangePasswordViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly UserAccountStore _store = UserAccountStore.Default;
    private readonly string _self;
    private readonly LoginRole _role;
    private string _selectedUser = "";
    private string _oldPassword = "";
    private string _newPassword = "";
    private string _confirmPassword = "";
    private string _tip = "";

    public ChangePasswordViewModel(string currentUser, LoginRole currentRole,
        IDialogService? dialogs = null)
    {
        _self = currentUser;
        _role = currentRole;
        _dialogs = dialogs ?? DialogService.Instance;
        var users = new List<string>();
        foreach (UserAccount a in _store.ListUsers())
        {
            if (_role == LoginRole.Dev || a.UserName == _self)
                users.Add(a.UserName);
        }
        if (users.Count == 0)
            users.Add(_self);
        Users = users;
        _selectedUser = users.Contains(_self) ? _self : users[0];
        OkCommand = new RelayCommand(Ok);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    public event Action<bool?>? RequestClose;

    /// <summary>账号下拉源（dev 全账号，admin 只自己）。</summary>
    public IReadOnlyList<string> Users { get; }

    /// <summary>账号下拉可改（仅 dev；admin 锁定自己）。</summary>
    public bool CanPickUser => _role == LoginRole.Dev;

    public string SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (Set(ref _selectedUser, value))
                Raise(nameof(NeedOldPassword));
        }
    }

    /// <summary>是否需要旧密码（dev 代改他人时不需要）。</summary>
    public bool NeedOldPassword =>
        !(_role == LoginRole.Dev
            && !string.Equals(_selectedUser, _self, StringComparison.Ordinal));

    public string OldPassword
    {
        get => _oldPassword;
        set => Set(ref _oldPassword, value ?? "");
    }

    public string NewPassword
    {
        get => _newPassword;
        set => Set(ref _newPassword, value ?? "");
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => Set(ref _confirmPassword, value ?? "");
    }

    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    public string TitleText => Tr("修改密码");
    public string HeadText => Tr("修改密码");
    public string UserLabel => Tr("账号");
    public string OldLabel => Tr("旧密码");
    public string NewLabel => Tr("新密码");
    public string ConfirmLabel => Tr("确认新密码");
    public string OkText => Tr("确定");
    public string CancelText => Tr("取消");

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    public void RefreshTexts() => RefreshAll();

    private static string Tr(string zh) => LanguageService.Tr(zh);

    private void Ok()
    {
        // 两次新密码一致是纯 UI 事，不进 Core，当场拦。
        if (!string.Equals(_newPassword, _confirmPassword, StringComparison.Ordinal))
        {
            Tip = Tr("两次新密码不一致");
            ConfirmPassword = "";
            return;
        }
        string? err = NeedOldPassword
            ? _store.ChangePassword(_selectedUser, _oldPassword, _newPassword)
            : _store.SetPassword(_selectedUser, _newPassword);
        if (err != null)
        {
            Tip = Tr(err);
            return;
        }
        _dialogs.ShowMessage(Tr("密码修改成功"), Tr("修改密码"));
        RequestClose?.Invoke(true);
    }
}
