// =========================================================================
// OperatorViewModel：权限管理窗编排（四级操作员账号增删改）
//
// 干什么：账号列表（四级混排，级别降序）+ 编辑面板（新增：用户名/级别/密码；
//   选中：改级别/改密码/删除）。防锁死红线在 Store 层（最后一个 L4 删/降拒），
//   错误弹框展示。门禁：本窗仅 LEVEL.4 可开（主窗把关，VM 不重复查）。
// 怎么改：新增操作 = Store 加方法 + VM 加命令 + 测试补用例，三处一致。
// =========================================================================

using System.Collections.ObjectModel;
using CureLine.Core.Security;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>账号列表行（DataGrid 两列：用户名/级别中文）。</summary>
public sealed class OperatorRow
{
    public string UserName { get; init; } = "";
    public OperatorLevel Level { get; init; }
    public string LevelText => OperatorLevelInfo.DisplayName(Level);
}

public sealed class OperatorViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly OperatorStore _store;

    public ObservableCollection<OperatorRow> Users { get; } = new();

    private OperatorRow? _selectedUser;
    public OperatorRow? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (Set(ref _selectedUser, value))
            {
                // 选中回显：用户名/级别进编辑区（改级按钮用）；密码框永不回显
                EditUserName = value?.UserName ?? "";
                EditLevel = value?.Level ?? OperatorLevel.Level1;
            }
        }
    }

    private string _editUserName = "";
    public string EditUserName { get => _editUserName; set => Set(ref _editUserName, value ?? ""); }

    private OperatorLevel _editLevel = OperatorLevel.Level1;
    public OperatorLevel EditLevel { get => _editLevel; set => Set(ref _editLevel, value); }

    private string _editPassword = "";
    public string EditPassword { get => _editPassword; set => Set(ref _editPassword, value ?? ""); }

    /// <summary>级别下拉源（四级全列）。</summary>
    public static IReadOnlyList<OperatorLevel> AllLevels { get; } =
        [OperatorLevel.Level1, OperatorLevel.Level2, OperatorLevel.Level3, OperatorLevel.Level4];

    public RelayCommand AddUserCommand { get; }
    public RelayCommand RemoveUserCommand { get; }
    public RelayCommand SetLevelCommand { get; }
    public RelayCommand SetPasswordCommand { get; }

    public OperatorViewModel(IDialogService dialogs, OperatorStore? store = null)
    {
        _dialogs = dialogs;
        _store = store ?? OperatorStore.Default;
        AddUserCommand = new RelayCommand(AddUser);
        RemoveUserCommand = new RelayCommand(RemoveUser, () => SelectedUser != null);
        SetLevelCommand = new RelayCommand(SetLevel, () => SelectedUser != null);
        SetPasswordCommand = new RelayCommand(SetPassword, () => SelectedUser != null);
        Reload();
    }

    private void Reload()
    {
        Users.Clear();
        foreach (var u in _store.ListUsers())
            Users.Add(new OperatorRow { UserName = u.UserName, Level = u.Level });
    }

    private void AddUser()
    {
        var err = _store.AddUser(EditUserName, EditLevel, EditPassword);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "新增失败");
            return;
        }
        EditPassword = "";
        Reload();
        _dialogs.ShowMessage($"账号 {EditUserName.Trim()} 已新增。", "新增成功");
    }

    private void RemoveUser()
    {
        if (SelectedUser == null)
            return;
        if (!_dialogs.Confirm($"确定删除账号 {SelectedUser.UserName}？", "删除确认"))
            return;
        var err = _store.RemoveUser(SelectedUser.UserName);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "删除失败");
            return;
        }
        Reload();
    }

    private void SetLevel()
    {
        if (SelectedUser == null)
            return;
        var err = _store.SetLevel(SelectedUser.UserName, EditLevel);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "改级别失败");
            return;
        }
        Reload();
        _dialogs.ShowMessage($"账号 {SelectedUser.UserName} 级别已调整。", "调整成功");
    }

    private void SetPassword()
    {
        if (SelectedUser == null)
            return;
        var err = _store.SetPassword(SelectedUser.UserName, EditPassword);
        if (err != null)
        {
            _dialogs.ShowMessage(err, "改密码失败");
            return;
        }
        EditPassword = "";
        _dialogs.ShowMessage($"账号 {SelectedUser.UserName} 密码已重置。", "改密码成功");
    }
}
