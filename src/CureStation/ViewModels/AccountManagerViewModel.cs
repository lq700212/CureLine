// =========================================================================
// AccountManagerViewModel：用户管理窗编排（三级操作员账号增删 / 代改密码）
//
// 干什么：账号列表（开发者>管理员>操作员降序）+ 三组操作：
//   ① 新增账号：用户名/角色/密码——管理员只有"操作员"可选；开发者可建
//     "操作员/管理员"；"开发者"角色不开放创建（预置唯一，防最高权限扩散）。
//   ② 重置密码：选中账号按"新密码+确认"直接重置（免验原密码）——
//     operator 无改密权，改密全走此处（登录窗改密面板已随 V0.0.49 删除）；
//     admin 可重置 operator/admin（含自己），dev 可重置所有人（含自己）。
//   ③ 删除：admin 只能删 operator；dev 可删 operator/admin；dev 永不可删。
// 授权双层：VM 层做 UI 置灰/原因提示（CanResetSelected/CanDeleteSelected/
//   SelectedActionHint），Store 层再兜底一道（AddUser/RemoveUser/SetPassword
//   都带 actor 参数，规则一致）。
// 门禁：本窗仅管理员/开发者可开（主窗把关，VM 不重复查）——operator 无菜单
//   入口（主窗"设置"按钮整钮禁用）。
// 怎么改：新增操作 = Store 加方法（actor 兜底）+ VM 加命令 + 测试补用例。
// =========================================================================

using System.Collections.ObjectModel;
using CureLine.Core.Security;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>账号列表行（DataGrid 两列：用户名/角色中文）。</summary>
public sealed class AccountRow
{
    public string UserName { get; init; } = "";
    public OperatorRole Role { get; init; }
    public string RoleText => OperatorRoleInfo.DisplayName(Role);
}

public sealed class AccountManagerViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly OperatorStore _store;
    private readonly OperatorRole _actor;

    public ObservableCollection<AccountRow> Users { get; } = new();

    private AccountRow? _selectedUser;
    public AccountRow? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (Set(ref _selectedUser, value))
            {
                // 选中变化：刷新按钮可用性与原因提示；重置密码框清空（换人不留旧输入）
                NewPassword = "";
                NewPassword2 = "";
                Raise(nameof(CanResetSelected));
                Raise(nameof(CanDeleteSelected));
                Raise(nameof(SelectedActionHint));
            }
        }
    }

    // ---- 新增账号编辑区 ----

    private string _editUserName = "";
    public string EditUserName { get => _editUserName; set => Set(ref _editUserName, value ?? ""); }

    private string _editPassword = "";
    public string EditPassword { get => _editPassword; set => Set(ref _editPassword, value ?? ""); }

    /// <summary>新增角色下拉候选（中文文案；admin 只见操作员，dev 加管理员）。</summary>
    public IReadOnlyList<string> CreateRoleOptions { get; }

    private string _editRoleText = "操作员";
    public string EditRoleText { get => _editRoleText; set => Set(ref _editRoleText, value ?? "操作员"); }

    // ---- 重置密码编辑区（选中账号全新密码，免验原密码） ----

    private string _newPassword = "";
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value ?? ""); }

    private string _newPassword2 = "";
    public string NewPassword2 { get => _newPassword2; set => Set(ref _newPassword2, value ?? ""); }

    public RelayCommand AddUserCommand { get; }
    public RelayCommand RemoveUserCommand { get; }
    public RelayCommand ResetPasswordCommand { get; }

    public AccountManagerViewModel(IDialogService dialogs, OperatorRole actor, OperatorStore? store = null)
    {
        _dialogs = dialogs;
        _actor = actor;
        _store = store ?? OperatorStore.Default;
        CreateRoleOptions = actor == OperatorRole.Dev
            ? ["操作员", "管理员"]
            : ["操作员"];
        AddUserCommand = new RelayCommand(AddUser);
        RemoveUserCommand = new RelayCommand(RemoveUser, CanDeleteSelected);
        ResetPasswordCommand = new RelayCommand(ResetPassword, CanResetSelected);
        Reload();
    }

    private void Reload()
    {
        Users.Clear();
        foreach (var u in _store.ListUsers())
            Users.Add(new AccountRow { UserName = u.UserName, Role = u.Role });
    }

    // ---------- 可用性与提示（UI 层；Store 层另有 actor 兜底） ----------

    /// <summary>能否重置选中账号密码：选中存在，且（我是开发者 或 目标不是开发者）。</summary>
    public bool CanResetSelected() =>
        SelectedUser != null && (_actor == OperatorRole.Dev || SelectedUser.Role != OperatorRole.Dev);

    /// <summary>能否删除选中账号：开发者不可删；管理员点管理员也不可删（只有开发者能删）。</summary>
    public bool CanDeleteSelected() =>
        SelectedUser != null && SelectedUser.Role != OperatorRole.Dev &&
        !(_actor == OperatorRole.Admin && SelectedUser.Role == OperatorRole.Admin);

    /// <summary>选中不可操作时的原因提示（空=选中可操作或未选中）。</summary>
    public string SelectedActionHint
    {
        get
        {
            var s = SelectedUser;
            if (s == null)
                return "";
            if (s.Role == OperatorRole.Dev)
                return "开发者账号受保护：不可删除，密码仅开发者本人可在此修改。";
            if (_actor == OperatorRole.Admin && s.Role == OperatorRole.Admin)
                return "管理员账号可在此重置密码，但删除需要开发者。";
            return "";
        }
    }

    // ---------- 三个操作 ----------

    private void AddUser()
    {
        // 角色中文文案 → 枚举（下拉候选即本表，异常值兜底操作员）
        var role = EditRoleText == "管理员" ? OperatorRole.Admin : OperatorRole.Operator;
        var err = _store.AddUser(EditUserName, role, EditPassword, _actor);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "新增失败");
            return;
        }
        string name = EditUserName.Trim();
        EditPassword = "";
        EditUserName = "";
        Reload();
        _dialogs.ShowMessage($"账号 {name}（{OperatorRoleInfo.DisplayName(role)}）已新增。", "新增成功");
    }

    private void ResetPassword()
    {
        var s = SelectedUser;
        // 兜底：程序态直调命令绕不过 CanExecute（RelayCommand 直调不判灰），这里再拦一道
        if (s == null || !CanResetSelected())
            return;
        if (!string.Equals(NewPassword, NewPassword2, StringComparison.Ordinal))
        {
            _dialogs.ShowMessage("两次输入的新密码不一致。", "重置失败");
            NewPassword = "";
            NewPassword2 = "";
            return;
        }
        if (!_dialogs.Confirm(
                $"确定把账号 {s.UserName} 的密码重置为输入的新密码？（免验原密码，立即生效）",
                "重置确认"))
            return;
        var err = _store.SetPassword(s.UserName, NewPassword, _actor);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "重置失败");
            return;
        }
        NewPassword = "";
        NewPassword2 = "";
        _dialogs.ShowMessage($"账号 {s.UserName} 密码已重置，请通知其本人。", "重置成功");
    }

    private void RemoveUser()
    {
        var s = SelectedUser;
        // 兜底：程序态直调命令绕不过 CanExecute（RelayCommand 直调不判灰），这里再拦一道
        if (s == null || !CanDeleteSelected())
            return;
        if (!_dialogs.Confirm($"确定删除账号 {s.UserName}？", "删除确认"))
            return;
        var err = _store.RemoveUser(s.UserName, _actor);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "删除失败");
            return;
        }
        Reload();
    }
}
