// =========================================================================
// CuringPolicyViewModel：工艺策略窗编排（策略库增删改/激活/保存）
//
// 干什么：策略列表（增删选）+ 编辑面板（名称/全局时长/光栅门控/每层覆盖
//   4 框/每仓位覆盖行表）+ 「激活并生效」+「保存」（先校验后落盘，错弹框
//   不写文件）。三级覆盖口径见 CuringPolicy 顶注释（仓位 > 层 > 全局）。
// 编辑模式：与点表配置窗同款——选中即回显编辑区，保存前 Flush 回对象；
//   数字解析失败当场收集错误，保存时先弹（比校验更早拦截，防静默丢输入）。
// 怎么改：新增策略字段 = CuringPolicy 加属性 + 编辑区加控件 + Sync/Flush
//   两处同步 + Validator 加规则，四处一致；测试照 CuringPolicyVmTests 补。
// =========================================================================

using System.Collections.ObjectModel;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.ViewModels;

namespace CureStation.ViewModels;

/// <summary>每仓位覆盖行（DataGrid 可编辑三列；保存时解析回 SlotMinutes）。</summary>
public sealed class SlotMinuteRow : ViewModelBase
{
    private string _rack = "";
    public string Rack { get => _rack; set => Set(ref _rack, value); }
    private string _level = "";
    public string Level { get => _level; set => Set(ref _level, value); }
    private string _minutes = "";
    public string Minutes { get => _minutes; set => Set(ref _minutes, value); }
}

public sealed class CuringPolicyViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly List<string> _flushErrors = new();
    private string _activeName = "";

    public ObservableCollection<CuringPolicy> Policies { get; } = new();
    public ObservableCollection<SlotMinuteRow> SlotRows { get; } = new();

    private CuringPolicy? _selectedPolicy;
    public CuringPolicy? SelectedPolicy
    {
        get => _selectedPolicy;
        set { if (Set(ref _selectedPolicy, value)) SyncEditorFromPolicy(); }
    }

    private SlotMinuteRow? _selectedSlotRow;
    public SlotMinuteRow? SelectedSlotRow
    {
        get => _selectedSlotRow;
        set => Set(ref _selectedSlotRow, value);
    }

    // ---------- 编辑区（选中策略回显；保存前 Flush 回对象） ----------

    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editGlobalMinutes = "";
    public string EditGlobalMinutes { get => _editGlobalMinutes; set => Set(ref _editGlobalMinutes, value); }

    private bool _editGrillGate;
    public bool EditGrillGate { get => _editGrillGate; set => Set(ref _editGrillGate, value); }

    private string _editLevel1Min = "";
    public string EditLevel1Min { get => _editLevel1Min; set => Set(ref _editLevel1Min, value); }
    private string _editLevel2Min = "";
    public string EditLevel2Min { get => _editLevel2Min; set => Set(ref _editLevel2Min, value); }
    private string _editLevel3Min = "";
    public string EditLevel3Min { get => _editLevel3Min; set => Set(ref _editLevel3Min, value); }
    private string _editLevel4Min = "";
    public string EditLevel4Min { get => _editLevel4Min; set => Set(ref _editLevel4Min, value); }

    // ---------- 命令 ----------

    public RelayCommand AddPolicyCommand { get; }
    public RelayCommand RemovePolicyCommand { get; }
    public RelayCommand AddSlotRowCommand { get; }
    public RelayCommand RemoveSlotRowCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ActivateCommand { get; }

    /// <summary>保存/激活成功后置 true（DialogService 据此返回 true，主窗刷新策略）。</summary>
    public bool CloseDialog { get; private set; }

    public CuringPolicyViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        AddPolicyCommand = new RelayCommand(AddPolicy);
        RemovePolicyCommand = new RelayCommand(RemovePolicy, () => SelectedPolicy != null);
        AddSlotRowCommand = new RelayCommand(AddSlotRow);
        RemoveSlotRowCommand = new RelayCommand(RemoveSlotRow, () => SelectedSlotRow != null);
        SaveCommand = new RelayCommand(Save);
        ActivateCommand = new RelayCommand(Activate, () => SelectedPolicy != null);
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        var book = CuringPolicyStore.Load() ?? CuringPolicyStore.BuildDefaultBook();
        Policies.Clear();
        foreach (var p in book.Policies)
            Policies.Add(p);
        _activeName = book.ActiveName;
        SelectedPolicy = book.ResolveActive(); // 缺省选中激活策略（触发回显）
    }

    // ---------- 编辑区同步 ----------

    private void SyncEditorFromPolicy()
    {
        if (SelectedPolicy == null)
        {
            ClearEditor();
            return;
        }
        var p = SelectedPolicy;
        EditName = p.Name;
        EditGlobalMinutes = p.GlobalMinutes.ToString();
        EditGrillGate = p.GrillGate;
        EditLevel1Min = LevelText(p, 1);
        EditLevel2Min = LevelText(p, 2);
        EditLevel3Min = LevelText(p, 3);
        EditLevel4Min = LevelText(p, 4);
        SlotRows.Clear();
        foreach (var (key, minutes) in p.SlotMinutes)
        {
            var parts = key.Split('-');
            SlotRows.Add(new SlotMinuteRow
            {
                Rack = parts.ElementAtOrDefault(0) ?? "",
                Level = parts.ElementAtOrDefault(1) ?? "",
                Minutes = minutes.ToString(),
            });
        }
    }

    private static string LevelText(CuringPolicy p, int level) =>
        p.LevelMinutes.TryGetValue(level, out var m) && m > 0 ? m.ToString() : "";

    private void ClearEditor()
    {
        EditName = "";
        EditGlobalMinutes = "";
        EditGrillGate = false;
        EditLevel1Min = EditLevel2Min = EditLevel3Min = EditLevel4Min = "";
        SlotRows.Clear();
        SelectedSlotRow = null;
    }

    /// <summary>编辑区 → 选中策略对象（保存/激活前调；数字解析失败进 _flushErrors）。</summary>
    private void FlushEditorToPolicy()
    {
        _flushErrors.Clear();
        if (SelectedPolicy == null)
            return;
        var p = SelectedPolicy;
        p.Name = EditName.Trim();
        p.GlobalMinutes = ParseOrError(EditGlobalMinutes, p.GlobalMinutes, "全局时长", p.Name);
        p.GrillGate = EditGrillGate;
        p.LevelMinutes.Clear();
        SetLevel(1, EditLevel1Min);
        SetLevel(2, EditLevel2Min);
        SetLevel(3, EditLevel3Min);
        SetLevel(4, EditLevel4Min);
        p.SlotMinutes.Clear();
        foreach (var row in SlotRows)
        {
            bool blank = string.IsNullOrWhiteSpace(row.Rack) &&
                string.IsNullOrWhiteSpace(row.Level) &&
                string.IsNullOrWhiteSpace(row.Minutes);
            if (blank)
                continue; // 全空行忽略
            int rack = ParseOrError(row.Rack, 0, $"仓位行 架号 \"{row.Rack}\"", p.Name);
            int level = ParseOrError(row.Level, 0, $"仓位行 层号 \"{row.Level}\"", p.Name);
            int minutes = ParseOrError(row.Minutes, 0, $"仓位行 时长 \"{row.Minutes}\"", p.Name);
            if (rack > 0 && level > 0 && minutes > 0)
                p.SlotMinutes[CuringPolicy.SlotKey(rack, level)] = minutes;
        }
    }

    private void SetLevel(int level, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return; // 空=不覆盖该层（回退全局）
        var p = SelectedPolicy!;
        int minutes = ParseOrError(text, 0, $"层{level} 时长 \"{text}\"", p.Name);
        if (minutes > 0)
            p.LevelMinutes[level] = minutes;
    }

    private int ParseOrError(string text, int fallback, string what, string policyName)
    {
        if (int.TryParse(text?.Trim(), out var v))
            return v;
        _flushErrors.Add($"策略[{policyName}] {what} 不是数字。");
        return fallback;
    }

    // ---------- 命令实现 ----------

    private void AddPolicy()
    {
        var policy = new CuringPolicy { Name = $"策略{Policies.Count + 1}" };
        Policies.Add(policy);
        SelectedPolicy = policy;
    }

    private void RemovePolicy()
    {
        if (SelectedPolicy == null)
            return;
        var p = SelectedPolicy;
        SelectedPolicy = null;
        Policies.Remove(p);
        SelectedPolicy = Policies.FirstOrDefault();
    }

    private void AddSlotRow()
    {
        SlotRows.Add(new SlotMinuteRow());
        SelectedSlotRow = SlotRows.LastOrDefault();
    }

    private void RemoveSlotRow()
    {
        if (SelectedSlotRow == null)
            return;
        int index = SlotRows.IndexOf(SelectedSlotRow);
        SlotRows.Remove(SelectedSlotRow);
        SelectedSlotRow = SlotRows.Count > 0
            ? SlotRows[Math.Clamp(index, 0, SlotRows.Count - 1)]
            : null;
    }

    private void Save() => SaveCore(activate: false);

    private void Activate() => SaveCore(activate: true);

    /// <summary>保存/激活共用：Flush → 数字错先拦 → 整库校验 → 落盘。激活额外写 ActiveName。</summary>
    private void SaveCore(bool activate)
    {
        FlushEditorToPolicy();
        if (_flushErrors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", _flushErrors), "保存失败（数字格式）");
            return;
        }
        var book = new CuringPolicyBook { Policies = [.. Policies] };
        book.ActiveName = activate
            ? SelectedPolicy?.Name ?? ""
            : _activeName;
        var validation = CuringPolicyValidator.ValidateBook(book);
        if (!validation.Ok)
        {
            _dialogs.ShowMessage(validation.ToString(), "保存失败（校验不过）");
            return;
        }
        CuringPolicyStore.Save(book);
        _activeName = book.ActiveName;
        CloseDialog = true;
        // 成功提示由主窗统一弹（OpenCuringPolicy 收到 true 后提示），与点表窗同款
    }
}
