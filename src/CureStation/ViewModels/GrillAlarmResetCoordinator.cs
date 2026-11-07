// =========================================================================
// GrillAlarmResetCoordinator：光栅报警"人工复位"弹窗编排（V0.0.69，程序①私有）
//
// 干什么：人工复位模式下，正在报警的库位清单 + 弹窗开关决策 + 复位请求派发，
//   三个职责一处收口（纯 VM：ObservableCollection + RelayCommand，无 WPF
//   依赖，测试直接 new 喂数组断言）。
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
// 怎么改：弹窗文案/行结构在 GrillAlarmResetItem；弹窗开关细节动 Refresh 的
//   _shown/_dismissed 状态机（改动先跑 GrillAlarmResetCoordinatorTests 剧本）。
// =========================================================================

using System.Collections.ObjectModel;
using CureLine.Core.Curing;
using CureLine.Core.Comms;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>
/// 报警复位窗一行（库位 + 解除按钮）。Rack/Level 是状态机口径（复位派发用），
/// Caption 给操作员看（客户库位编码 + 架层号，与主窗卡片标题同源——弹窗里
/// "A03" 和现场枢纽卡片对得上，不用换算）。行内容只读，无需 INPC 写。
/// </summary>
public sealed class GrillAlarmResetItem
{
    /// <summary>复位请求回调（Coordinator 工厂注入——回收发 ResetRequested 事件）。</summary>
    private readonly Action<GrillAlarmResetItem> _onReset;

    /// <summary>构造即挂好命令（回调在字段里，readonly 属性可以构造内赋值）。</summary>
    internal GrillAlarmResetItem(int rack, int level, Action<GrillAlarmResetItem> onReset)
    {
        Rack = rack;
        Level = level;
        _onReset = onReset;
        Reset = new RelayCommand(() => _onReset(this));
    }

    public int Rack { get; }
    public int Level { get; }

    /// <summary>行文案："A03 · 架1-层3"（库位编码=主窗卡片标题口径）。</summary>
    public string Caption => $"{SlotLocationCode.EncodeInRack(Rack, Level)} · 架{Rack}-层{Level}";

    /// <summary>解除按钮命令（重复点安全：状态机幂等，已解除的行下一拍即删）。</summary>
    public RelayCommand Reset { get; }
}

public sealed class GrillAlarmResetCoordinator : ViewModelBase
{
    private readonly Func<GrillAlarmResetMode> _modeProvider;

    public GrillAlarmResetCoordinator(Func<GrillAlarmResetMode> modeProvider)
    {
        _modeProvider = modeProvider;
        ResetAllCommand = new RelayCommand(
            () => ResetAllRequested?.Invoke(),
            () => Items.Count > 0);
    }

    /// <summary>正在报警的库位行（弹窗绑定它——差分热更：新报警加行/解除删行）。</summary>
    public ObservableCollection<GrillAlarmResetItem> Items { get; } = new();

    /// <summary>全部解除（一键清当前清单全部报警；无报警时按钮灰）。</summary>
    public RelayCommand ResetAllCommand { get; }

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
                        it => ResetRequested?.Invoke(it.Rack, it.Level)));

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
}
