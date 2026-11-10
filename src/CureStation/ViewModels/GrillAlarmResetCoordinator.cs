// =========================================================================
// GrillAlarmResetCoordinator：光栅报警"人工复位 + 现场确认"弹窗编排（程序①私有）
//
// 干什么：把"光栅报警"从单纯的人工复位升级为**现场事实确认**三步流程
//   （V0.0.97 用户拍板）：
//     步骤一（清单）：列出正在报警的库位，每个库位两个处置按钮——
//       · 【误报·解除】：现场确认没有人取产品（纯误触）→ 解除报警（红灯灭、
//         蜂鸣停），计时继续，产品留在架上继续固化；
//       · 【有产品取出】：确实取走了产品 → 进入步骤二。
//     步骤二（补扫）：要求操作员把**取出的那些产品的 SN 扫回来**——
//       这个机台是放产品进去固化的，被提前取出的产品固化时间不足，
//       后续质量追溯要靠这个 SN 找到实物。每扫一个 SN 立即落一条
//       "中途取出"记录（带当时的实际固化时长），扫完点【扫完了】。
//     步骤三（计时处置）：确实取出了产品，还要决定这个库位的计时怎么办——
//       · 【继续计时】：库位上**没取出的产品继续固化**（部分取出场景，
//         主流情况——不能因为拿了一件就把整盘货的计时停掉）；
//       · 【终止计时】：**整个库位的产品都终止计时**（都判定无效/都要
//         追回重做），实际已固化时长入库供追溯。
// 数据流（热更关键，用户拍板：报警期间弹窗清单必须实时增减）：
//   MainViewModel 500ms 拍（状态机 GetAlarmSlots 现取清单）→ Refresh(alarms)
//   → 差分同步 Items（新报警立即加行、解除立即删行）→ 弹窗绑 Items 自动跟随
//   ——窗开着时新报警热进出，无需关了重开。复位命令完成后宿主也立即再调
//   一次 Refresh（不等下拍）——点掉哪行哪行即走。
// 弹窗决策（仅人工档弹窗；自动档报警交状态机让空自愈+主窗红卡，不弹窗）：
//   有报警且窗未开 → ShowRequested（首帧/全解除后再报警/出新库位都弹）；
//   全解除 → CloseRequested + Items 清空；
//   用户 ✕ 关窗（NotifyWindowClosed）→ 记"用户已表态"：挂着的旧报警不再
//   连环重弹（不骚扰，也丢不了——主窗卡片仍是红卡），出现**新**库位报警
//   才再弹一枪把名单带回眼前。
// 为什么复位走事件不直接调状态机：本类保持纯 VM（状态机是 MainViewModel
//   私有资产，写灯是网络 IO——宿主收事件 Task.Run 后台处理，铁律 2）。
// 扫码通道：步骤二的补扫 SN 由扫码枪送进本类（TryHandleOutScan）——
//   补扫期间普通绑定通道被挡住（防止操作员扫的产品码被当成新绑定）。
//   详见 MainViewModel.OnBarcodeScanned 的分流。
// 怎么改：弹窗文案/行结构在 GrillAlarmResetItem；弹窗开关细节动 Refresh 的
//   _shown/_dismissed 状态机（改动先跑 GrillAlarmResetCoordinatorTests 剧本）。
// =========================================================================

using System.Collections.ObjectModel;
using CureLine.Core.Curing;
using CureLine.Core.Comms;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>报警确认弹窗当前步骤（三步互斥面板）。</summary>
public enum GrillAlarmDialogStep
{
    /// <summary>步骤一：报警库位清单（每个库位选"误报"或"有产品取出"）。</summary>
    List,

    /// <summary>步骤二：补扫被取出的产品 SN。</summary>
    ScanOut,

    /// <summary>步骤三：计时处置（继续计时 / 终止计时）。</summary>
    TimingDecision,
}

/// <summary>
/// 报警复位窗一行（库位 + 处置按钮）。Rack/Level 是状态机口径（复位派发用），
/// Caption 给操作员看（客户库位编码 + 架层号，与主窗卡片标题同源——弹窗里
/// "A03" 和现场枢纽卡片对得上，不用换算）。行内容只读，无需 INPC 写。
/// </summary>
public sealed class GrillAlarmResetItem
{
    /// <summary>复位请求回调（Coordinator 工厂注入——回收发 ResetRequested 事件）。</summary>
    private readonly Action<GrillAlarmResetItem> _onReset;

    /// <summary>"有产品取出"回调（Coordinator 工厂注入）。</summary>
    private readonly Action<GrillAlarmResetItem> _onTakeOut;

    /// <summary>构造即挂好命令（回调在字段里，readonly 属性可以构造内赋值）。</summary>
    internal GrillAlarmResetItem(int rack, int level,
        Action<GrillAlarmResetItem> onReset, Action<GrillAlarmResetItem> onTakeOut)
    {
        Rack = rack;
        Level = level;
        _onReset = onReset;
        _onTakeOut = onTakeOut;
        Reset = new RelayCommand(() => _onReset(this));
        TakeOut = new RelayCommand(() => _onTakeOut(this));
    }

    public int Rack { get; }
    public int Level { get; }

    /// <summary>行文案："A03 · 架1-层3"（库位编码=主窗卡片标题口径）。</summary>
    public string Caption => $"{SlotLocationCode.EncodeInRack(Rack, Level)} · 架{Rack}-层{Level}";

    /// <summary>解除按钮命令（重复点安全：状态机幂等，已解除的行下一拍即删）。</summary>
    public RelayCommand Reset { get; }

    /// <summary>"有产品取出"按钮命令（进入补扫步骤）。</summary>
    public RelayCommand TakeOut { get; }
}

/// <summary>补扫到的一个产品 SN（步骤二清单行——现场看扫了几个、是哪几个）。</summary>
public sealed class GrillOutScanItem
{
    /// <summary>构造（架/层/SN/时刻）。</summary>
    internal GrillOutScanItem(int rack, int level, string sn, DateTimeOffset at)
    {
        Rack = rack;
        Level = level;
        Sn = sn;
        At = at;
    }

    /// <summary>所属库位（架）。</summary>
    public int Rack { get; }

    /// <summary>所属库位（层）。</summary>
    public int Level { get; }

    /// <summary>扫到的产品 SN 原文。</summary>
    public string Sn { get; }

    /// <summary>扫描时刻。</summary>
    public DateTimeOffset At { get; }
}

public sealed class GrillAlarmResetCoordinator : ViewModelBase
{
    private readonly Func<GrillAlarmResetMode> _modeProvider;

    /// <summary>
    /// 取某库位当前已固化分钟数（宿主注入：状态机 GetStatus 的 Total-Remain）。
    /// 用于"中途取出"记录的固化时长——取出的产品固化不足多少分钟，是追溯
    /// 最关键的数。拿不到返回 0（记录里留 0，报表照常显示）。
    /// </summary>
    private readonly Func<int, int, int> _curedMinutesProvider;

    public GrillAlarmResetCoordinator(Func<GrillAlarmResetMode> modeProvider,
        Func<int, int, int>? curedMinutesProvider = null)
    {
        _modeProvider = modeProvider;
        _curedMinutesProvider = curedMinutesProvider ?? ((_, _) => 0);
        ResetAllCommand = new RelayCommand(
            () => ResetAllRequested?.Invoke(),
            () => Items.Count > 0);
        CommandFinishScan = new RelayCommand(() => EnterTimingDecision());
        CommandKeepTiming = new RelayCommand(() => ApplyTimingDecision(terminate: false));
        CommandStopTiming = new RelayCommand(() => ApplyTimingDecision(terminate: true));
        CommandBackToList = new RelayCommand(() => GoToList());
    }

    /// <summary>正在报警的库位行（弹窗绑定它——差分热更：新报警加行/解除删行）。</summary>
    public ObservableCollection<GrillAlarmResetItem> Items { get; } = new();

    /// <summary>全部按误报解除（一键清当前清单全部报警；无报警时按钮灰）。</summary>
    public RelayCommand ResetAllCommand { get; }

    // ---------- 三步流程（V0.0.97） ----------

    private GrillAlarmDialogStep _step = GrillAlarmDialogStep.List;
    /// <summary>当前步骤（XAML 三块互斥面板按此切换显隐）。</summary>
    public GrillAlarmDialogStep Step
    {
        get => _step;
        private set => Set(ref _step, value);
    }

    private int _currentRack;
    /// <summary>正在处理的库位（架）；步骤二/三有效，步骤一为 0。</summary>
    public int CurrentRack
    {
        get => _currentRack;
        private set => Set(ref _currentRack, value);
    }

    private int _currentLevel;
    /// <summary>正在处理的库位（层）；步骤二/三有效，步骤一为 0。</summary>
    public int CurrentLevel
    {
        get => _currentLevel;
        private set => Set(ref _currentLevel, value);
    }

    /// <summary>正在处理的库位显示码（"A03"；步骤一为空串）。</summary>
    public string CurrentLocationCode =>
        Step == GrillAlarmDialogStep.List ? "" : SlotLocationCode.EncodeInRack(CurrentRack, CurrentLevel);

    /// <summary>步骤二扫到的产品 SN 清单（现场核对"扫了几个"）。</summary>
    public ObservableCollection<GrillOutScanItem> OutScans { get; } = new();

    /// <summary>步骤二：补扫完成（进入计时处置）。</summary>
    public RelayCommand CommandFinishScan { get; }

    /// <summary>步骤三：继续计时（未取出的产品继续固化）。</summary>
    public RelayCommand CommandKeepTiming { get; }

    /// <summary>步骤三：终止计时（整个库位产品停表）。</summary>
    public RelayCommand CommandStopTiming { get; }

    /// <summary>返回步骤一（放弃本次补扫/处置，回到清单）。</summary>
    public RelayCommand CommandBackToList { get; }

    /// <summary>步骤二/三的标题文案（"处理 A03"）。</summary>
    public string CurrentCaption => Step == GrillAlarmDialogStep.List
        ? ""
        : $"{CurrentLocationCode} · 架{CurrentRack}-层{CurrentLevel}";

    /// <summary>步骤二的提示文案（要操作员做什么）。</summary>
    string ScanOutHint => OutScans.Count == 0
        ? "请把刚才取出的产品码逐个扫回来（可扫多个），扫完点【扫完了】。"
        : $"已扫 {OutScans.Count} 件。继续扫可追加，扫完点【扫完了】。";

    /// <summary>步骤二的提示文案（公开给 XAML 绑定）。</summary>
    public string ScanOutHintText => ScanOutHint;

    /// <summary>步骤三的提示文案（要操作员决定什么）。</summary>
    public string TimingHintText =>
        "确认取出了产品后，请选择该库位的固化计时怎么处理：未取出的产品继续固化，还是整库位全部终止。";

    // ---------- 弹窗开关决策的小账本（见类头） ----------

    private bool _shown;                 // 弹窗在展示（宿主窗关了经 NotifyWindowClosed 落账）
    private bool _dismissedByUser;       // 用户 ✕ 后不连环重弹（出新库位报警才再弹）
    private HashSet<(int Rack, int Level)> _lastKeys = new(); // 上拍报警集（判"新库位"）

    /// <summary>弹窗待开（宿主经 IDialogService.ShowGrillAlarmReset 走真窗；测试断言事件）。</summary>
    public event Action? ShowRequested;

    /// <summary>弹窗该收（全解除/切回自动模式；未开为 no-op——宿主实现幂等）。</summary>
    public event Action? CloseRequested;

    /// <summary>复位请求（参数=架/层；宿主接住 Task.Run 后台调状态机，勿同步回调窗）。</summary>
    public event Action<int, int>? ResetRequested;

    /// <summary>全部复位请求（宿主一次拉快照清单串行复位，防并发写总线错乱）。</summary>
    public event Action? ResetAllRequested;

    /// <summary>
    /// "有产品取出"确认（参数=架/层）：进入步骤二补扫。宿主可据此记一行
    /// 日志（现场确认确实取出了产品——这是质量追溯的第一手证据）。
    /// </summary>
    public event Action<int, int>? TakeOutConfirmed;

    /// <summary>
    /// 补扫到一件被取出的产品（参数=架、层、SN、当时已固化分钟数）：
    /// 宿主据此写追溯库"中途取出"记录（该 SN 固化不足，时长=当时已固化）。
    /// </summary>
    public event Action<int, int, string, int>? OutScanRecorded;

    /// <summary>
    /// 计时处置决定（参数=架、层、是否终止计时）：true=终止整个库位的计时，
    /// false=未取出的产品继续固化。宿主据此调状态机 TerminateCuring
    /// （终止时写灯是网络 IO，必须后台调——铁律 2）。
    /// </summary>
    public event Action<int, int, bool>? TimingDecisionRequested;

    /// <summary>
    /// 每拍刷新（500ms 拍 / 复位即时回刷 / 模式切换广播，共用唯一入口）：
    /// alarms=状态机当前报警清单（自动档宿主照喂，本方法按模式自行取舍——
    /// 自动档清清单不弹窗，人工档差分热更+弹窗决策）。
    /// </summary>
    public void Refresh(IReadOnlyList<(int Rack, int Level)> alarms)
    {
        if (_modeProvider() != GrillAlarmResetMode.Manual)
        {
            // ——自动档：不弹窗、清单清空（报警交给状态机让空自愈+主窗红卡展示）。
            //   用户表态的小账本一并复位（切回人工档时重新弹第一枪）；
            //   空集不清集合（避免每拍 Reset 事件打扰绑定）。
            if (Items.Count > 0)
                Items.Clear();
            _lastKeys.Clear();
            _dismissedByUser = false;
            if (_shown)
            {
                _shown = false;
                CloseRequested?.Invoke();
            }
        }
        else
        {
            // ——人工档：差分同步 Items（热更核心：窗开着也在涨/消）——
            var keys = alarms.OrderBy(a => a.Level).ThenBy(a => a.Rack).ToList();
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!keys.Contains((Items[i].Rack, Items[i].Level)))
                    Items.RemoveAt(i);
            foreach (var (rack, level) in keys)
                if (!Items.Any(it => it.Rack == rack && it.Level == level))
                    Items.Add(new GrillAlarmResetItem(rack, level,
                        it => ResetRequested?.Invoke(it.Rack, it.Level),
                        it => BeginTakeOut(it)));

            bool hasNew = keys.Any(k => !_lastKeys.Contains(k));
            _lastKeys = keys.ToHashSet();

            if (keys.Count == 0)
            {
                // 全解除：用户表态作废（下次报警重新弹第一枪）；窗开着就收。
                _dismissedByUser = false;
                if (_shown)
                {
                    _shown = false;
                    CloseRequested?.Invoke();
                }
            }
            else if (!_shown && (hasNew || !_dismissedByUser))
            {
                // 还有报警且窗没开就弹——除非"用户刚 ✕ 且无新库位"（挂着的旧
                // 报警不连环骚扰；新库位（hasNew）/首帧/全解除后再报警都弹）。
                _shown = true;
                _dismissedByUser = false;
                ShowRequested?.Invoke();
            }
        }

        // 统一出口刷新派生文本与按钮灰态（Items 的行集合变化由
        // CollectionChanged 直达 UI；这些是按 Count 算的展示属性，必须手动重读）
        Raise(nameof(AlarmCountText));
        Raise(nameof(HasItems));
        Raise(nameof(IsEmptyPromptVisible));
        ResetAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>弹窗被用户关闭（窗 Closed 回调宿主转发进来）：记表态，等下一枪。</summary>
    public void NotifyWindowClosed()
    {
        _shown = false;
        _dismissedByUser = true;
    }

    /// <summary>弹窗标题计数文本（"2 个库位正在报警"）。</summary>
    public string AlarmCountText => Items.Count == 0
        ? "暂无正在报警的库位"
        : $"{Items.Count} 个库位正在报警";

    /// <summary>有无报警行（弹窗说明行与空态提示的显隐开关）。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>空清单提示（切自动模式落空、或复位全部完成后短暂显示）。</summary>
    public bool IsEmptyPromptVisible => Items.Count == 0;

    // ---------------- 三步流程（V0.0.97） ----------------

    /// <summary>
    /// 步骤一 → 步骤二：操作员确认"确实取出了产品"，开始补扫被取出的 SN。
    /// （误报分支不走这里——误报直接点【误报·解除】即可。）
    /// </summary>
    private void BeginTakeOut(GrillAlarmResetItem item)
    {
        CurrentRack = item.Rack;
        CurrentLevel = item.Level;
        OutScans.Clear();
        Step = GrillAlarmDialogStep.ScanOut;
        TakeOutConfirmed?.Invoke(item.Rack, item.Level);
        RaiseStepTexts();
    }

    /// <summary>
    /// 补扫通道（步骤二）：扫码枪扫到的码走这里——**不进普通绑定通道**
    /// （防止操作员补扫的产品码被当成新绑定写进在架清单）。
    /// 返回 true=本次扫码已被补扫通道消费（宿主不要再走普通绑定）。
    /// 非步骤二、或空码 → 返回 false（交回普通通道处理）。
    /// </summary>
    public bool TryHandleOutScan(string? rawCode)
    {
        if (Step != GrillAlarmDialogStep.ScanOut)
            return false;
        string sn = (rawCode ?? "").Trim();
        if (sn.Length == 0)
            return false;
        // 扫到库位码不是产品码——忽略（不让操作员误扫库位码把流程搞乱）。
        if (SlotLocationCode.TryDecode(sn, out _, out _))
            return true;
        if (OutScans.Any(o => o.Sn == sn))
            return true;   // 重复扫同一件：忽略但仍算"已被本通道消费"
        OutScans.Add(new GrillOutScanItem(CurrentRack, CurrentLevel, sn, DateTimeOffset.Now));
        OutScanRecorded?.Invoke(CurrentRack, CurrentLevel, sn, _curedMinutesProvider(CurrentRack, CurrentLevel));
        RaiseStepTexts();
        return true;
    }

    /// <summary>步骤二 → 步骤三：补扫完成，进入计时处置。</summary>
    private void EnterTimingDecision()
    {
        Step = GrillAlarmDialogStep.TimingDecision;
        RaiseStepTexts();
    }

    /// <summary>
    /// 步骤三 → 步骤一：计时处置完成，回到清单继续处理其它报警库位。
    /// </summary>
    private void ApplyTimingDecision(bool terminate)
    {
        int rack = CurrentRack;
        int level = CurrentLevel;
        TimingDecisionRequested?.Invoke(rack, level, terminate);
        GoToList();
    }

    /// <summary>回到步骤一（清掉补扫清单与当前库位）。</summary>
    private void GoToList()
    {
        CurrentRack = 0;
        CurrentLevel = 0;
        OutScans.Clear();
        Step = GrillAlarmDialogStep.List;
        RaiseStepTexts();
    }

    /// <summary>步骤相关的派生文本统一刷新（步骤切换/补扫计数变化时调）。</summary>
    private void RaiseStepTexts()
    {
        Raise(nameof(CurrentCaption));
        Raise(nameof(CurrentLocationCode));
        Raise(nameof(ScanOutHintText));
        Raise(nameof(TimingHintText));
    }
}
