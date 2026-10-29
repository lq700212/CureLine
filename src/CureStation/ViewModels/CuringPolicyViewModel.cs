// =========================================================================
// CuringPolicyViewModel：工艺策略窗编排（预置库/自定义/套用/激活/保存/可视化预览）
//
// 干什么：策略列表（增删选）+ 编辑面板（名称/全局时长/光栅门控/每层覆盖
//   4 框/每仓位覆盖行表）+ 仓位时长可视化预览（可拖动/缩放画布的数据侧）+
//   「激活并生效」/「仅保存」/「套用为自定义策略」。三级覆盖口径见
//   CuringPolicy 顶注释（仓位 > 层 > 全局）。
// 预置/自定义口径（V0.0.20，对齐 AgingTestSystem 配方管理的"模板+即改即落"）：
//   预置策略（IsPreset=true，出厂模板）——可选中查看/编辑参数、可【套用为
//   自定义策略】（把编辑内容写入"自定义"策略并激活）；不能直接保存/激活/
//   删除（激活只认自定义——预置是起点模板，保证出厂推荐永远能回溯）。
//   自定义策略——原有全套（仅保存/激活/删除/新增）。
//   打开窗口缺省选中激活策略；缺省库激活名="自定义"（新现场开窗即自定义）。
// 编辑模式：与点表配置窗同款——选中即回显编辑区，保存前把编辑区构建成
//   草稿（BuildDraftFromEditor，数字解析失败进 errors），校验通过才把草稿
//   拷入选中对象（先改副本防脏：校验失败原对象不受污染，对齐
//   AgingTestSystem 大扫荡教训）。可视化预览也吃草稿——编辑参数实时反映。
// 可视化预览：SlotPreviewItems = RackCount×LevelCount 个格子（与主窗仓位
//   网格同源布局），每格显示该仓位解析时长 + 时长分档色（绿/黄/橙/红）；
//   编辑区任一时长字段或仓位行变化即重建。缩放/平移交互在 View 层
//   （RenderTransform 属 View 关切，见 CuringPolicyWindow.xaml.cs）。
// 怎么改：新增策略字段 = CuringPolicy 加属性 + 编辑区加控件 + BuildDraft
//   /ApplyDraft 两处同步 + Validator 加规则；预览分档改 PreviewTiers。
// =========================================================================

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CureLine.Core.Curing;
using CureLine.ViewModels;

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

/// <summary>
/// 可视化预览格子（策略窗画布一项）：Rack×Level 位置 + 解析时长文本 +
/// 分档色键（XAML DataTrigger 按 Tier 上色，色值不进 VM）。Tip 悬停全文。
/// </summary>
public sealed class SlotPreviewItem
{
    public SlotPreviewItem(int rack, int level, int minutes)
    {
        Rack = rack;
        Level = level;
        Minutes = minutes;
        Tier = minutes >= 180 ? "Red" : minutes >= 120 ? "Orange" : minutes >= 60 ? "Yellow" : "Green";
        Tip = $"架{rack} 层{level}：固化 {minutes} 分钟";
    }

    public int Rack { get; }
    public int Level { get; }
    public int Minutes { get; }
    public string MinutesText => Minutes.ToString();
    /// <summary>仓位短标（"架1-层1"，格子第二行显示；完整句进 ToolTip）。</summary>
    public string PositionText => $"架{Rack}-层{Level}";
    /// <summary>时长分档色键：Green(&lt;60)/Yellow(60~119)/Orange(120~179)/Red(≥180)。</summary>
    public string Tier { get; }
    public string Tip { get; }
}

public sealed class CuringPolicyViewModel : ViewModelBase
{
    /// <summary>套用目标：自定义策略的固定名（预置编辑内容套用到它身上）。</summary>
    public const string CustomName = "自定义";

    private readonly IDialogService _dialogs;
    private readonly List<string> _flushErrors = new();
    private string _activeName = "";

    public ObservableCollection<CuringPolicy> Policies { get; } = new();
    public ObservableCollection<SlotMinuteRow> SlotRows { get; } = new();

    /// <summary>预览画布的架数（与主窗仓位网格同源：点表最大架号，下限 10）。</summary>
    public int RackCount { get; }

    /// <summary>预览画布的层数（与主窗同源：点表最大层号，下限 3）。</summary>
    public int LevelCount { get; }

    /// <summary>可视化预览格子（RackCount×LevelCount 个，RebuildPreview 重建引用）。</summary>
    public IReadOnlyList<SlotPreviewItem> SlotPreviewItems { get; private set; } = [];

    private CuringPolicy? _selectedPolicy;
    public CuringPolicy? SelectedPolicy
    {
        get => _selectedPolicy;
        set
        {
            if (Set(ref _selectedPolicy, value))
            {
                Raise(nameof(SelectedIsPreset));
                SyncEditorFromPolicy();
            }
        }
    }

    /// <summary>选中项是否预置策略（XAML 按钮/提示切换的开关；无选中=false）。</summary>
    public bool SelectedIsPreset => SelectedPolicy?.IsPreset ?? false;

    private SlotMinuteRow? _selectedSlotRow;
    public SlotMinuteRow? SelectedSlotRow
    {
        get => _selectedSlotRow;
        set => Set(ref _selectedSlotRow, value);
    }

    // ---------- 编辑区（选中策略回显；保存前构建草稿回写） ----------

    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editGlobalMinutes = "";
    public string EditGlobalMinutes
    {
        get => _editGlobalMinutes;
        set { if (Set(ref _editGlobalMinutes, value)) RebuildPreview(); }
    }

    private bool _editGrillGate;
    public bool EditGrillGate { get => _editGrillGate; set => Set(ref _editGrillGate, value); }

    private bool _editPersistMinutes;
    public bool EditPersistMinutes { get => _editPersistMinutes; set => Set(ref _editPersistMinutes, value); }

    private string _editLevel1Min = "";
    public string EditLevel1Min
    {
        get => _editLevel1Min;
        set { if (Set(ref _editLevel1Min, value)) RebuildPreview(); }
    }
    private string _editLevel2Min = "";
    public string EditLevel2Min
    {
        get => _editLevel2Min;
        set { if (Set(ref _editLevel2Min, value)) RebuildPreview(); }
    }
    private string _editLevel3Min = "";
    public string EditLevel3Min
    {
        get => _editLevel3Min;
        set { if (Set(ref _editLevel3Min, value)) RebuildPreview(); }
    }
    private string _editLevel4Min = "";
    public string EditLevel4Min
    {
        get => _editLevel4Min;
        set { if (Set(ref _editLevel4Min, value)) RebuildPreview(); }
    }

    // ---------- 命令 ----------

    public RelayCommand AddPolicyCommand { get; }
    public RelayCommand RemovePolicyCommand { get; }
    public RelayCommand AddSlotRowCommand { get; }
    public RelayCommand RemoveSlotRowCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand ApplyToCustomCommand { get; }

    /// <summary>保存/激活/套用成功后置 true（DialogService 据此返回 true，主窗刷新策略）。</summary>
    public bool CloseDialog { get; private set; }

    public CuringPolicyViewModel(IDialogService dialogs, int rackCount = 10, int levelCount = 3)
    {
        _dialogs = dialogs;
        // 预览画布尺寸与主窗仓位网格同源（下限=客户库位编码表口径 10×3）——
        // 点表配了更多架就铺更多，与主窗展示一致。
        RackCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.RacksPerRow, rackCount);
        LevelCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.Levels, levelCount);
        AddPolicyCommand = new RelayCommand(AddPolicy);
        RemovePolicyCommand = new RelayCommand(RemovePolicy, () => SelectedPolicy != null);
        AddSlotRowCommand = new RelayCommand(AddSlotRow);
        RemoveSlotRowCommand = new RelayCommand(RemoveSlotRow, () => SelectedSlotRow != null);
        SaveCommand = new RelayCommand(Save);
        ActivateCommand = new RelayCommand(Activate, () => SelectedPolicy != null);
        ApplyToCustomCommand = new RelayCommand(ApplyToCustom, () => SelectedPolicy != null);

        // 仓位覆盖行增删/行内编辑都影响预览：集合变化时对新增行挂 INPC 重建；
        // 行属性变化（Add/Remove 后集合通知 + 行内字段通知）统一走 RebuildPreview。
        SlotRows.CollectionChanged += OnSlotRowsChanged;

        LoadFromDisk();
        RebuildPreview();
    }

    private void OnSlotRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (SlotMinuteRow row in e.NewItems)
                row.PropertyChanged += OnSlotRowPropertyChanged;
        if (e.OldItems != null)
            foreach (SlotMinuteRow row in e.OldItems)
                row.PropertyChanged -= OnSlotRowPropertyChanged;
        RebuildPreview();
    }

    private void OnSlotRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RebuildPreview();

    private void LoadFromDisk()
    {
        var book = CuringPolicyStore.Load() ?? CuringPolicyStore.BuildDefaultBook();
        Policies.Clear();
        foreach (var p in book.Policies)
            Policies.Add(p);
        _activeName = book.ActiveName;
        SelectedPolicy = book.ResolveActive(); // 缺省选中激活策略（新库=“自定义”）
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
        EditPersistMinutes = p.PersistMinutes;
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
        RebuildPreview();
    }

    private static string LevelText(CuringPolicy p, int level) =>
        p.LevelMinutes.TryGetValue(level, out var m) && m > 0 ? m.ToString() : "";

    private void ClearEditor()
    {
        EditName = "";
        EditGlobalMinutes = "";
        EditGrillGate = false;
        EditPersistMinutes = false;
        EditLevel1Min = EditLevel2Min = EditLevel3Min = EditLevel4Min = "";
        SlotRows.Clear();
        SelectedSlotRow = null;
    }

    /// <summary>
    /// 编辑区 → 全新草稿对象（不改选中策略——先改副本：校验失败/预览重建时
    /// 原对象不受污染）。数字解析失败记入 errors（引用进参，不累积到实例状态），
    /// 失败字段用原选中策略的值兜底（预览照画，保存时再拦）。
    /// </summary>
    private CuringPolicy BuildDraftFromEditor(List<string> errors)
    {
        var src = SelectedPolicy;
        var draft = new CuringPolicy
        {
            Name = EditName.Trim(),
            IsPreset = src?.IsPreset ?? false,
            GrillGate = EditGrillGate,
            PersistMinutes = EditPersistMinutes,
        };
        draft.GlobalMinutes = ParseOrError(EditGlobalMinutes, src?.GlobalMinutes ?? 120,
            "全局时长", draft.Name, errors);
        SetLevelDraft(draft, 1, EditLevel1Min, src, errors);
        SetLevelDraft(draft, 2, EditLevel2Min, src, errors);
        SetLevelDraft(draft, 3, EditLevel3Min, src, errors);
        SetLevelDraft(draft, 4, EditLevel4Min, src, errors);
        foreach (var row in SlotRows)
        {
            bool blank = string.IsNullOrWhiteSpace(row.Rack) &&
                string.IsNullOrWhiteSpace(row.Level) &&
                string.IsNullOrWhiteSpace(row.Minutes);
            if (blank)
                continue; // 全空行忽略
            int rack = ParseOrError(row.Rack, 0, $"仓位行 架号 \"{row.Rack}\"", draft.Name, errors);
            int level = ParseOrError(row.Level, 0, $"仓位行 层号 \"{row.Level}\"", draft.Name, errors);
            int minutes = ParseOrError(row.Minutes, 0, $"仓位行 时长 \"{row.Minutes}\"", draft.Name, errors);
            if (rack > 0 && level > 0 && minutes > 0)
                draft.SlotMinutes[CuringPolicy.SlotKey(rack, level)] = minutes;
        }
        return draft;
    }

    private void SetLevelDraft(CuringPolicy draft, int level, string text,
        CuringPolicy? src, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text))
            return; // 空=不覆盖该层（回退全局）
        int minutes = ParseOrError(text, src?.LevelMinutes.TryGetValue(level, out var m) == true && m > 0 ? m : 0,
            $"层{level} 时长 \"{text}\"", draft.Name, errors);
        if (minutes > 0)
            draft.LevelMinutes[level] = minutes;
    }

    private static int ParseOrError(string text, int fallback, string what, string policyName,
        List<string> errors)
    {
        if (int.TryParse(text?.Trim(), out var v))
            return v;
        errors.Add($"策略[{policyName}] {what} 不是数字。");
        return fallback;
    }

    /// <summary>把草稿参数拷入选中策略（校验通过后的落库动作；引用不变，逐字段拷）。</summary>
    private void ApplyDraftToSelected(CuringPolicy draft)
    {
        var p = SelectedPolicy!;
        p.Name = draft.Name;
        p.GlobalMinutes = draft.GlobalMinutes;
        p.GrillGate = draft.GrillGate;
        p.PersistMinutes = draft.PersistMinutes;
        p.LevelMinutes = draft.LevelMinutes;
        p.SlotMinutes = draft.SlotMinutes;
    }

    // ---------- 可视化预览 ----------

    /// <summary>
    /// 重建预览格子（RackCount×LevelCount，行优先与主窗一致）。吃编辑区草稿：
    /// 编辑全局/每层/仓位任一时长即实时反映（数字错时按原值兜底照画）。
    /// </summary>
    private void RebuildPreview()
    {
        var errors = new List<string>();   // 预览用完即弃：错值已兜底，不弹不打扰
        var draft = BuildDraftFromEditor(errors);
        var items = new List<SlotPreviewItem>(RackCount * LevelCount);
        for (int level = 1; level <= LevelCount; level++)
        for (int rack = 1; rack <= RackCount; rack++)
            items.Add(new SlotPreviewItem(rack, level, draft.ResolveMinutes(rack, level)));
        SlotPreviewItems = items;
        Raise(nameof(SlotPreviewItems));
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
        if (SelectedPolicy.IsPreset)
        {
            _dialogs.ShowMessage("预置策略是出厂模板，不能删除。\n如需按它定制，请【套用为自定义策略】后修改。",
                "删除失败");
            return;
        }
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

    /// <summary>
    /// 套用为自定义策略（预置策略专用出口）：把编辑区内容写入"自定义"策略
    /// （不存在则创建，存在则覆盖更新——对齐 AgingTestSystem 重名询问语义，
    /// 这里目标名固定=自定义，直接覆盖并提示），落盘并激活"自定义"。
    /// </summary>
    private void ApplyToCustom()
    {
        if (SelectedPolicy == null || !SelectedPolicy.IsPreset)
            return;
        var errors = new List<string>();
        var draft = BuildDraftFromEditor(errors);
        if (errors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", errors), "套用失败（数字格式）");
            return;
        }
        draft.Name = CustomName;
        draft.IsPreset = false;
        var book = new CuringPolicyBook { Policies = [.. Policies] };
        var existing = book.Policies.FirstOrDefault(p =>
            string.Equals((p.Name ?? "").Trim(), CustomName, StringComparison.OrdinalIgnoreCase)
            && !p.IsPreset);
        if (existing != null)
        {
            // 覆盖"自定义"既有参数（预置模板的参数整体替换，不残留旧覆盖行）
            existing.GlobalMinutes = draft.GlobalMinutes;
            existing.GrillGate = draft.GrillGate;
            existing.PersistMinutes = draft.PersistMinutes;
            existing.LevelMinutes = draft.LevelMinutes;
            existing.SlotMinutes = draft.SlotMinutes;
        }
        else
        {
            book.Policies.Add(draft);
        }
        book.ActiveName = CustomName;
        var validation = CuringPolicyValidator.ValidateBook(book);
        if (!validation.Ok)
        {
            _dialogs.ShowMessage(validation.ToString(), "套用失败（校验不过）");
            return;
        }
        CuringPolicyStore.Save(book);
        _activeName = book.ActiveName;
        // 刷新列表与选中（自定义是库内对象的新状态——重建集合保 UI 同步）
        Policies.Clear();
        foreach (var p in book.Policies)
            Policies.Add(p);
        SelectedPolicy = Policies.First(p =>
            string.Equals((p.Name ?? "").Trim(), CustomName, StringComparison.OrdinalIgnoreCase)
            && !p.IsPreset);
        CloseDialog = true;   // 主窗统一提示（与保存/激活同一条链）
    }

    /// <summary>
    /// 保存/激活共用：构建草稿 → 数字错先拦 → **草稿替换选中项进库校验**
    /// （改名撞车等重名错在草稿上拦，选中对象零污染——先改副本完整语义）→
    /// 校验过才 ApplyDraftToSelected + 落盘。激活额外写 ActiveName=草稿名。
    /// 预置策略一律拦下（保存走套用、激活不认预置）。
    /// </summary>
    private void SaveCore(bool activate)
    {
        if (SelectedPolicy == null)
            return;
        if (SelectedPolicy.IsPreset)
        {
            _dialogs.ShowMessage(activate
                ? "预置策略不能直接激活（激活只认自定义策略）。\n请先【套用为自定义策略】，再激活它。"
                : "预置策略不能直接保存（出厂模板保持原样）。\n请用【套用为自定义策略】把修改落到自定义策略。",
                "预置策略");
            return;
        }
        var errors = new List<string>();
        var draft = BuildDraftFromEditor(errors);
        if (errors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", errors), "保存失败（数字格式）");
            return;
        }
        var book = new CuringPolicyBook();
        foreach (var p in Policies)
            book.Policies.Add(ReferenceEquals(p, SelectedPolicy) ? draft : p);
        book.ActiveName = activate ? draft.Name : _activeName;
        var validation = CuringPolicyValidator.ValidateBook(book);
        if (!validation.Ok)
        {
            _dialogs.ShowMessage(validation.ToString(), "保存失败（校验不过）");
            return;
        }
        ApplyDraftToSelected(draft);
        CuringPolicyStore.Save(book);
        _activeName = book.ActiveName;
        CloseDialog = true;
        // 成功提示由主窗统一弹（OpenCuringPolicy 收到 true 后提示），与点表窗同款
    }
}
