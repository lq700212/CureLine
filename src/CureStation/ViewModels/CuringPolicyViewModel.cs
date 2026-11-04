// =========================================================================
// CuringPolicyViewModel：工艺策略窗编排（预置库/自定义/套用/激活/保存/画布直改）
//
// 干什么：策略列表（增删选）+ 参数条（名称/全局时长/光栅门控/断电续算，
//   V0.0.44 起"每仓位覆盖行表"已删——画布取代其全部职能）+ 仓位时长画布
//   （主编辑区：点格子/框选直接设时长，见下）+ 「激活并生效」/「仅保存」/
//   「套用为自定义策略」。三级覆盖口径见 CuringPolicy 顶注释（仓位 > 层 > 全局）。
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
// 可视化画布（主编辑区，2026-10-31 用户拍板"点格子直接改"）：
//   SlotPreviewItems = RackCount×LevelCount 个格子（与主窗仓位网格同源布局），
//   每格显示该仓位解析时长 + 颜色——普通格=时长四档色（绿/黄/橙/红），
//   特殊格（SlotMinutes 配了独立覆盖，真识别查字典非数值推断）=主题蓝
//   Brush.Fresh.Accent 一眼区分；编辑区任一时长字段或仓位行变化即重建。
//   画布点选/框选：选择真源 _selectedKeys（键集合），View 层翻译鼠标
//   （单击/Ctrl单击/拖框→Replace/Toggle/Add/ClearSelection），浮层输入
//   SelectionMinutesText 回车应用=ApplySelectionCommand（逐格写覆盖行）；
//   缩放/平移交互仍在 View 层（RenderTransform 属 View 关切，
//   见 CuringPolicyWindow.xaml.cs）。
// 时间单位（V0.0.53 起，用户要求：页面可切 时/分，全部时间文本跟换；V0.0.56
//   起秒档删除——用户拍板"没有秒级的固化"，且主窗卡片/统计条随单位同步换）：
//   **只换显示/输入口径，存储与状态机恒为分钟**（CuringPolicy.GlobalMinutes/
//   LevelMinutes/SlotMinutes 的 int 分钟语义不动——状态机/持久化/过账零影响）。
//   切换时把编辑区与仓位行文本按旧单位解析成分钟、再按新单位重写（多步无损：
//   时=分钟/60；小时显示 0.#### 精度足够整分钟往返）。
//   解析统一走 TryParseDisplay（支持小数、AwayFromZero 四舍五入到整分钟）。
//   单位真源在 DurationUnit 静态中枢（本程序集共享）：Change() 落盘记忆
//   config.ini [setting] PolicyTimeUnit + 广播主窗立即重刷时长文本；非法值/
//   旧版 "Seconds" 自愈回 Minutes（与 PointBitStyle 同款自愈）。
// 怎么改：新增策略字段 = CuringPolicy 加属性 + 编辑区加控件 + BuildDraft
//   /ApplyDraft 两处同步 + Validator 加规则；预览分档改 PreviewTiers。
// =========================================================================

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
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
    /// <summary>时长文本（当前时间单位口径；保存时按单位换算回分钟）。</summary>
    public string Minutes { get => _minutes; set => Set(ref _minutes, value); }
}

/// <summary>
/// 可视化预览格子（策略窗画布一项）：Rack×Level 位置 + 解析时长文本 +
/// 分档色键（XAML DataTrigger 按 Tier 上色，色值不进 VM）。Tip 悬停全文。
/// IsSelected（INPC）=画布点选/框选态——真源在 VM 的 _selectedKeys 键集合，
/// 格子对象每次 RebuildPreview 全新重建，选中态由 VM 按键恢复，勿只依赖本属性。
/// IsSpecial=该仓位在 SlotMinutes 配了独立覆盖（画布蓝色标识，与四档时长色区分）。
/// Minutes 恒为分钟（分档/校验/保存口径）；MinutesText/Tip 是当前时间单位的
/// 显示文本（VM 按单位格式化后传入，格子自身不认识单位）。
/// </summary>
public sealed class SlotPreviewItem : ViewModelBase
{
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public SlotPreviewItem(int rack, int level, int minutes, string minutesText, string tip,
        bool isSpecial = false)
    {
        Rack = rack;
        Level = level;
        Minutes = minutes;
        MinutesText = minutesText;
        Tip = tip;
        IsSpecial = isSpecial;
        Tier = minutes >= 180 ? "Red" : minutes >= 120 ? "Orange" : minutes >= 60 ? "Yellow" : "Green";
    }

    public int Rack { get; }
    public int Level { get; }
    public int Minutes { get; }
    /// <summary>格子大数字显示文本（当前时间单位，含小数时按 0.#### 格式）。</summary>
    public string MinutesText { get; }
    /// <summary>仓位短标（"架1-层1"，格子第二行显示；完整句进 ToolTip）。</summary>
    public string PositionText => $"架{Rack}-层{Level}";
    /// <summary>时长分档色键：Green(&lt;60)/Yellow(60~119)/Orange(120~179)/Red(≥180)。</summary>
    public string Tier { get; }
    /// <summary>
    /// 是否特殊仓位（SlotMinutes 配了独立覆盖）。真识别=查覆盖字典存在性，
    /// 不靠"时长与层/全局不同"推断——覆盖值恰好等于层/全局时仍算特殊（可识别回删）。
    /// </summary>
    public bool IsSpecial { get; }
    public string Tip { get; }
}

public sealed class CuringPolicyViewModel : ViewModelBase
{
    /// <summary>套用目标：自定义策略的固定名（预置编辑内容套用到它身上）。</summary>
    public const string CustomName = "自定义";

    private readonly IDialogService _dialogs;
    private string _activeName = "";

    private PolicyTimeUnit _timeUnit = PolicyTimeUnit.Minutes;

    /// <summary>
    /// 页面时间单位（时/分）：只换显示/输入口径，策略存储与状态机恒为分钟。
    /// 切换时把编辑区/仓位行文本按旧单位换算成分钟再按新单位重写（无损往返）；
    /// 单位真源与记忆落盘在 DurationUnit 静态中枢（Change() 写 ini + 广播，
    /// 主窗卡片/统计条立即按新单位重拼文本）——本字段与之同步，setter 是
    /// 唯一写点，开窗时从 DurationUnit.Current 取初值。
    /// </summary>
    public PolicyTimeUnit TimeUnit
    {
        get => _timeUnit;
        set
        {
            if (value == _timeUnit)
                return;
            var oldUnit = _timeUnit;
            _timeUnit = value;
            Raise(nameof(TimeUnit));
            Raise(nameof(TimeUnitIndex));
            Raise(nameof(TimeUnitSuffix));
            Raise(nameof(TimeUnitName));
            Raise(nameof(LevelHeaderText));
            Raise(nameof(GlobalMinutesToolTip));
            Raise(nameof(SelectionInputToolTip));
            Raise(nameof(LegendGreenText));
            Raise(nameof(LegendYellowText));
            Raise(nameof(LegendOrangeText));
            Raise(nameof(LegendRedText));
            RescaleEditorTexts(oldUnit);   // 编辑区/仓位行文本换单位（分钟值不变）
            RebuildPreview();              // 画布数字/ToolTip/浮层摘要随单位刷新
            DurationUnit.Change(value);    // 落盘记忆 + 广播主窗同步（放最后：本窗全就绪再通知）
        }
    }

    /// <summary>下拉选中索引（枚举值即顺序：0=时 1=分）。</summary>
    public int TimeUnitIndex
    {
        get => (int)TimeUnit;
        set
        {
            if (value is >= (int)PolicyTimeUnit.Hours and <= (int)PolicyTimeUnit.Minutes)
                TimeUnit = (PolicyTimeUnit)value;
        }
    }

    /// <summary>下拉选项（顺序与枚举一致；秒档已删——没有秒级的固化）。</summary>
    public IReadOnlyList<string> TimeUnitOptions { get; } = ["时", "分"];

    /// <summary>输入框后缀短词：时/分。</summary>
    public string TimeUnitSuffix => TimeUnit == PolicyTimeUnit.Hours ? "时" : "分";

    /// <summary>描述性文案单位名：小时/分钟（图例/ToolTip/Expander 用）。</summary>
    public string TimeUnitName => TimeUnit == PolicyTimeUnit.Hours ? "小时" : "分钟";

    /// <summary>每层覆盖折叠区标题（单位随切换）。</summary>
    public string LevelHeaderText => $"每层覆盖（{TimeUnitName}，空=用全局）";

    /// <summary>全局时长输入框 ToolTip（单位随切换）。</summary>
    public string GlobalMinutesToolTip =>
        $"全局固化时长（{TimeUnitName}）——所有仓位的打底值，被每层/每仓位覆盖";

    /// <summary>浮层时长输入框 ToolTip（单位随切换）。</summary>
    public string SelectionInputToolTip =>
        $"时长（{TimeUnitName}，计时精度为分钟），回车应用";

    // 图例四档阈值（分钟语义不变，文案随单位换算）：60/120/180 分钟三档边界。

    // 图例是描述文案（非可编辑值），小时档用 0.## 显示更紧凑（1.98/2.98），
    // 保持 0.#### 会在图例列里折成两行（V0.0.53 目检修）。

    /// <summary>图例绿档：< 60 分钟。</summary>
    public string LegendGreenText => $"跟随层/全局：< {FormatMinutesShort(60)} {TimeUnitName}";

    /// <summary>图例黄档：60 ~ 119 分钟。</summary>
    public string LegendYellowText =>
        $"跟随层/全局：{FormatMinutesShort(60)} ~ {FormatMinutesShort(119)} {TimeUnitName}";

    /// <summary>图例橙档：120 ~ 179 分钟。</summary>
    public string LegendOrangeText =>
        $"跟随层/全局：{FormatMinutesShort(120)} ~ {FormatMinutesShort(179)} {TimeUnitName}";

    /// <summary>图例红档：≥ 180 分钟。</summary>
    public string LegendRedText => $"跟随层/全局：≥ {FormatMinutesShort(180)} {TimeUnitName}";

    /// <summary>分钟 → 当前单位的简短显示（图例用，0.##）。</summary>
    private string FormatMinutesShort(int minutes)
    {
        double value = TimeUnit == PolicyTimeUnit.Hours ? minutes / 60.0 : minutes;
        return value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    public ObservableCollection<CuringPolicy> Policies { get; } = new();
    public ObservableCollection<SlotMinuteRow> SlotRows { get; } = new();

    /// <summary>预览画布的架数（与主窗仓位网格同源：点表最大架号，下限 10）。</summary>
    public int RackCount { get; }

    /// <summary>预览画布的层数（与主窗同源：点表最大层号，下限 3）。</summary>
    public int LevelCount { get; }

    /// <summary>
    /// 画布格子区定宽（列数 × 单格 68 = 64 格宽 + 左右 Margin 4）：格子尺寸锁定
    /// 不随窗口宽度/架号文字浮动（V0.0.44 用户定调默认饱满大小），想更大滚轮
    /// 缩放；架数变化按列数线性加宽（双架 20 列也放得下，画布自身可滚动区外）。
    /// </summary>
    public double PreviewGridWidth => RackCount * 68.0;

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

    // ---------- 画布点选编辑（2026-10-31 用户拍板：点格子直接改时长，少摆输入框） ----------
    //
    // 选择真源 = _selectedKeys（"架-层"键集合）：格子对象每次 RebuildPreview 全新
    // 重建引用，选中态由按键恢复（SyncSelectionFlags）；View 层只做鼠标→格子翻译
    // （命中/框选/Ctrl 叠加），选择与写值全在 VM（可测）。浮层输入（SelectionMinutesText）
    // 回车/应用 = ApplySelectionCommand：逐格写 SlotRows（有则改无则增）→ 行通知自动
    // 重建预览（数字/四档色/特殊蓝即刷）。预置策略走同一编辑区草稿，套用为自定义时落库。

    private readonly HashSet<string> _selectedKeys = new();

    /// <summary>有选中格子（浮层显隐开关）。</summary>
    public bool HasSelection => _selectedKeys.Count > 0;

    /// <summary>浮层标题：单格 = "架X-层Y · 当前 N 分"；多格 = "架X-层Y、架X-层Z 等 N 格"。</summary>
    public string SelectionSummaryText
    {
        get
        {
            if (_selectedKeys.Count == 0)
                return "未选择";
            if (_selectedKeys.Count == 1)
            {
                var key = _selectedKeys.First();
                var it = SlotPreviewItems.FirstOrDefault(x => $"{x.Rack}-{x.Level}" == key);
                return it == null
                    ? key
                    : $"架{it.Rack}-层{it.Level} · 当前 {FormatMinutes(it.Minutes)} {TimeUnitSuffix}";
            }
            var names = SlotPreviewItems
                .Where(x => _selectedKeys.Contains($"{x.Rack}-{x.Level}"))
                .Select(x => $"架{x.Rack}-层{x.Level}").Take(2).ToList();
            return string.Join("、", names) + $" 等 {_selectedKeys.Count} 格";
        }
    }

    private string _selectionMinutesText = "";
    /// <summary>浮层时长输入（应用后清空，便于连改下一批）。</summary>
    public string SelectionMinutesText
    {
        get => _selectionMinutesText;
        set => Set(ref _selectionMinutesText, value);
    }

    private static string KeyOf(SlotPreviewItem it) => $"{it.Rack}-{it.Level}";

    /// <summary>替换选择（单击/框选——View 层命中后调）。</summary>
    public void ReplaceSelection(IEnumerable<SlotPreviewItem> items)
    {
        _selectedKeys.Clear();
        foreach (var it in items)
            _selectedKeys.Add(KeyOf(it));
        SyncSelection();
    }

    /// <summary>加选（Ctrl+框选：在既有选择上并集）。</summary>
    public void AddSelection(IEnumerable<SlotPreviewItem> items)
    {
        foreach (var it in items)
            _selectedKeys.Add(KeyOf(it));
        SyncSelection();
    }

    /// <summary>单击加选/减选一格（Ctrl+单击）。</summary>
    public void ToggleItem(SlotPreviewItem item)
    {
        if (!_selectedKeys.Remove(KeyOf(item)))
            _selectedKeys.Add(KeyOf(item));
        SyncSelection();
    }

    /// <summary>清空选择（点空白 / Esc）。</summary>
    public void ClearSelection()
    {
        if (_selectedKeys.Count == 0)
            return;
        _selectedKeys.Clear();
        SyncSelection();
    }

    /// <summary>选择变化：恢复格子高亮 + 通知浮层标题/显隐 + 清输入。</summary>
    private void SyncSelection()
    {
        SyncSelectionFlags();
        Raise(nameof(HasSelection));
        Raise(nameof(SelectionSummaryText));
        SelectionMinutesText = "";   // 新选择清输入（防上一批残值误应用）
        ApplySelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionOverrideCommand.RaiseCanExecuteChanged();
    }

    /// <summary>只恢复格子 IsSelected（RebuildPreview 重建引用后调；不动输入/通知）。</summary>
    private void SyncSelectionFlags()
    {
        foreach (var it in SlotPreviewItems)
            it.IsSelected = _selectedKeys.Contains($"{it.Rack}-{it.Level}");
    }

    /// <summary>
    /// 浮层【应用】：把输入时长写到全部选中格的 SlotRows 覆盖行（有则改无则增，
    /// 行匹配宽松化——表里手填可能带空格；写入用规范数字文本）。空输入静默忽略
    /// （浮层保持打开，误触回车不弹框）；非数字/≤0 弹框拦。
    /// 例外——**全选应用升格为改全局**：选中格铺满整个画布（架×层一个不少）时，
    /// "把每格都设成同一个值"在数据语义上就等于设全局（上层=len 层=len），
    /// 此时同步编辑区全局时长框 + 清掉全部仓位/层覆盖行，数据归一（否则 30 条
    /// 冗余覆盖行把值盖住，全局框还是旧值，用户看到的两处数字对不上）。
    /// </summary>
    private void ApplySelection()
    {
        if (_selectedKeys.Count == 0)
            return;
        var text = (SelectionMinutesText ?? "").Trim();
        if (text.Length == 0)
            return;
        // 按当前单位解析（支持小数）；底层仍是整分钟。
        if (!TryParseDisplay(text, TimeUnit, out var minutes) || minutes < 1)
        {
            _dialogs.ShowMessage($"固化时长需为 ≥1 分钟（当前单位：{TimeUnitName}）。", "设置时长");
            return;
        }
        // 全选升格为改全局：同步编辑区（全局框通知会自动 RebuildPreview——
        // 覆盖清掉后所有格子回层/全局口径），再清仓位/层覆盖行，数据归一。
        if (_selectedKeys.Count == RackCount * LevelCount)
        {
            EditGlobalMinutes = FormatMinutes(minutes);
            EditLevel1Min = "";
            EditLevel2Min = "";
            EditLevel3Min = "";
            EditLevel4Min = "";
            SlotRows.Clear();   // 集合通知链会自动重建预览，此处不必再调 RebuildPreview
            SelectionMinutesText = "";
            return;
        }
        foreach (var key in _selectedKeys)
        {
            var parts = key.Split('-');
            int rack = int.Parse(parts[0]), level = int.Parse(parts[1]);
            var row = SlotRows.FirstOrDefault(r =>
                int.TryParse((r.Rack ?? "").Trim(), out var rr) && rr == rack &&
                int.TryParse((r.Level ?? "").Trim(), out var ll) && ll == level);
            if (row != null)
                row.Minutes = FormatMinutes(minutes);
            else
                SlotRows.Add(new SlotMinuteRow
                {
                    Rack = rack.ToString(),
                    Level = level.ToString(),
                    Minutes = FormatMinutes(minutes),
                });
        }
        SelectionMinutesText = "";
    }

    /// <summary>
    /// 浮层【清除】：删掉选中格的 SlotMinutes 覆盖行（画布取代覆盖表后的"恢复
    /// 跟随层/全局"出口——设了能删，闭环）。无覆盖行的格子静默跳过；行删除的
    /// 集合通知自动重建预览（蓝格回四档色、右值回层/全局）。
    /// </summary>
    private void ClearSelectionOverride()
    {
        if (_selectedKeys.Count == 0)
            return;
        foreach (var key in _selectedKeys)
        {
            var parts = key.Split('-');
            int rack = int.Parse(parts[0]), level = int.Parse(parts[1]);
            var row = SlotRows.FirstOrDefault(r =>
                int.TryParse((r.Rack ?? "").Trim(), out var rr) && rr == rack &&
                int.TryParse((r.Level ?? "").Trim(), out var ll) && ll == level);
            if (row != null)
                SlotRows.Remove(row);
        }
        SelectionMinutesText = "";
    }

    // ---------- 命令 ----------

    public RelayCommand AddPolicyCommand { get; }
    public RelayCommand RemovePolicyCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand ApplyToCustomCommand { get; }
    public RelayCommand ApplySelectionCommand { get; }
    public RelayCommand ClearSelectionOverrideCommand { get; }

    /// <summary>保存/激活/套用成功后置 true（测试断言用；View 经 RequestClose 关窗）。</summary>
    public bool CloseDialog { get; private set; }

    /// <summary>
    /// 关窗请求（View 订阅：设 DialogResult=true 并 Close）——保存/激活/套用成功后触发。
    /// 这是 ShowDialog 返回值链的关键：不触发则主窗收到 false、不刷新激活策略、
    /// 不弹"已保存"提示（用户会误以为没保存成功，V0.0.53 修）。
    /// 与 LoginViewModel.RequestClose 同款模式（VM 不 new Window）。
    /// </summary>
    public event Action<bool>? RequestClose;

    public CuringPolicyViewModel(IDialogService dialogs, int rackCount = 10, int levelCount = 3)
    {
        _dialogs = dialogs;
        // 预览画布尺寸与主窗仓位网格同源（下限=客户库位编码表口径 10×3）——
        // 点表配了更多架就铺更多，与主窗展示一致。
        RackCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.RacksPerRow, rackCount);
        LevelCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.Levels, levelCount);
        LoadTimeUnit();   // 单位真源在 DurationUnit 中枢（含 ini 记忆自愈）；加载策略前定好口径
        AddPolicyCommand = new RelayCommand(AddPolicy);
        RemovePolicyCommand = new RelayCommand(RemovePolicy, () => SelectedPolicy != null);
        SaveCommand = new RelayCommand(Save);
        ActivateCommand = new RelayCommand(Activate, () => SelectedPolicy != null);
        ApplyToCustomCommand = new RelayCommand(ApplyToCustom, () => SelectedPolicy != null);
        ApplySelectionCommand = new RelayCommand(ApplySelection, () => HasSelection);
        ClearSelectionOverrideCommand = new RelayCommand(ClearSelectionOverride, () => HasSelection);

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

    /// <summary>读上次选择的时间单位：先重读 ini（盘上为准），再取 DurationUnit 中枢值——
    /// 静态中枢在进程内只初始化一次，外部/MES 改过 ini 或测试手改后开新窗必须重读。</summary>
    private void LoadTimeUnit()
    {
        DurationUnit.LoadFromIni();
        _timeUnit = DurationUnit.Current;
    }

    // ---------- 时间单位换算（显示文本 ↔ 分钟） ----------

    /// <summary>分钟 → 当前单位显示文本（时=÷60；0.#### 精度保证整分钟无损往返）。</summary>
    private string FormatMinutes(int minutes)
    {
        double value = TimeUnit == PolicyTimeUnit.Hours ? minutes / 60.0 : minutes;
        return value.ToString("0.####", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 当前单位文本 → 分钟（支持小数；四舍五入到整分钟——状态机计时精度是分钟）。
    /// NaN/无穷/超 int 范围一律算格式错。
    /// </summary>
    private static bool TryParseDisplay(string? text, PolicyTimeUnit unit, out int minutes)
    {
        minutes = 0;
        if (!double.TryParse((text ?? "").Trim(), NumberStyles.Float,
                CultureInfo.CurrentCulture, out var value))
            return false;
        double m = unit == PolicyTimeUnit.Hours ? value * 60.0 : value;
        if (double.IsNaN(m) || double.IsInfinity(m) || m is < int.MinValue or > int.MaxValue)
            return false;
        minutes = (int)Math.Round(m, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>
    /// 单位切换时重写编辑区/仓位行的时长文本：按旧单位解析成分钟、按新单位格式化。
    /// 解析失败的文本原样保留（切换不吞用户输入错误，保存时照常拦并提示）。
    /// 空文本保持空（层覆盖/仓位行的"空=跟随"语义不变）。
    /// </summary>
    private void RescaleEditorTexts(PolicyTimeUnit oldUnit)
    {
        string Convert(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? text
                : TryParseDisplay(text, oldUnit, out var minutes)
                    ? FormatMinutes(minutes)
                    : text;

        EditGlobalMinutes = Convert(EditGlobalMinutes);
        EditLevel1Min = Convert(EditLevel1Min);
        EditLevel2Min = Convert(EditLevel2Min);
        EditLevel3Min = Convert(EditLevel3Min);
        EditLevel4Min = Convert(EditLevel4Min);
        foreach (var row in SlotRows)
            row.Minutes = Convert(row.Minutes);
    }

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
        EditGlobalMinutes = FormatMinutes(p.GlobalMinutes);
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
                Minutes = FormatMinutes(minutes),
            });
        }
        RebuildPreview();
    }

    private string LevelText(CuringPolicy p, int level) =>
        p.LevelMinutes.TryGetValue(level, out var m) && m > 0 ? FormatMinutes(m) : "";

    private void ClearEditor()
    {
        EditName = "";
        EditGlobalMinutes = "";
        EditGrillGate = false;
        EditPersistMinutes = false;
        EditLevel1Min = EditLevel2Min = EditLevel3Min = EditLevel4Min = "";
        SlotRows.Clear();
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
            // 架号/层号是物理位置整数，恒按十进制解析——不能走时间单位换算
            // （否则小时模式下架"3"会被当成 180 分钟，绑定错仓位）。
            int rack = ParseIndexOrError(row.Rack, 0, $"仓位行 架号 \"{row.Rack}\"", draft.Name, errors);
            int level = ParseIndexOrError(row.Level, 0, $"仓位行 层号 \"{row.Level}\"", draft.Name, errors);
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

    /// <summary>按当前时间单位解析数字；失败记一条（带单位提示），回退 fallback。</summary>
    private int ParseOrError(string text, int fallback, string what, string policyName,
        List<string> errors)
    {
        if (TryParseDisplay(text, TimeUnit, out var minutes))
            return minutes;
        errors.Add($"策略[{policyName}] {what} 不是数字（当前单位：{TimeUnitName}）。");
        return fallback;
    }

    /// <summary>解析物理编号（架号/层号）：恒十进制整数，不随页面时间单位换算。</summary>
    private static int ParseIndexOrError(string text, int fallback, string what, string policyName,
        List<string> errors)
    {
        if (int.TryParse((text ?? "").Trim(), out var value))
            return value;
        errors.Add($"策略[{policyName}] {what} 不是整数。");
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
    /// 特殊格识别=查草稿 SlotMinutes 覆盖字典存在性（真识别，非时长数值推断）；
    /// 重建后按键恢复选中态（格子对象全新，IsSelected 归 false 必须补）。
    /// </summary>
    private void RebuildPreview()
    {
        var errors = new List<string>();   // 预览用完即弃：错值已兜底，不弹不打扰
        var draft = BuildDraftFromEditor(errors);
        var items = new List<SlotPreviewItem>(RackCount * LevelCount);
        for (int level = 1; level <= LevelCount; level++)
        for (int rack = 1; rack <= RackCount; rack++)
        {
            var key = CuringPolicy.SlotKey(rack, level);
            int minutes = draft.ResolveMinutes(rack, level);
            bool special = draft.SlotMinutes.ContainsKey(key);
            string display = FormatMinutes(minutes);
            string tip = $"架{rack} 层{level}：固化 {display} {TimeUnitName}" +
                         (special ? "（特殊仓位：单独设置过）" : "");
            items.Add(new SlotPreviewItem(rack, level, minutes, display, tip, special));
        }
        SlotPreviewItems = items;
        Raise(nameof(SlotPreviewItems));
        SyncSelectionFlags();
        Raise(nameof(SelectionSummaryText));   // 重建后单格标题的"当前 N 分"可能已变
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
        if (!TrySave(book))
            return;
        _activeName = book.ActiveName;
        // 刷新列表与选中（自定义是库内对象的新状态——重建集合保 UI 同步）
        Policies.Clear();
        foreach (var p in book.Policies)
            Policies.Add(p);
        SelectedPolicy = Policies.First(p =>
            string.Equals((p.Name ?? "").Trim(), CustomName, StringComparison.OrdinalIgnoreCase)
            && !p.IsPreset);
        CloseDialog = true;   // 主窗统一提示（与保存/激活同一条链）
        RequestClose?.Invoke(true);
    }

    /// <summary>
    /// 落盘策略库（带可见失败提示）：写盘异常不许静默——现场只读目录/占用时
    /// 用户必须知道"没保存成功"，而不是关窗重开发现白改（V0.0.53）。
    /// </summary>
    private bool TrySave(CuringPolicyBook book)
    {
        try
        {
            CuringPolicyStore.Save(book);
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage(
                $"工艺策略落盘失败：{ex.Message}\n请检查程序目录 config 文件夹是否可写（磁盘/权限/占用）。",
                "保存失败");
            return false;
        }
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
        // 先落盘成功才同步选中对象（写盘失败不污染内存里的选中策略）。
        if (!TrySave(book))
            return;
        ApplyDraftToSelected(draft);
        _activeName = book.ActiveName;
        CloseDialog = true;
        // 关窗经 RequestClose → DialogResult=true → 主窗 LoadPolicyBook + 统一提示。
        // （V0.0.53 前 CloseDialog 无人消费：窗口不关、ShowDialog 恒 false，
        //   主窗不刷新激活策略也不提示，用户误以为"没保存/没新增"。）
        RequestClose?.Invoke(true);
    }
}
