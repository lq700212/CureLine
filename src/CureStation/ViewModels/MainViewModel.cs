// =========================================================================
// MainViewModel：固化程控主窗编排（程序①）
//
// 干什么：① 启动时读点表（config/pointtable.json）+ 工艺策略
//   （config/curingpolicy.json，缺则自愈缺省库）→ 造总线（Mock/真机按
//   config.ini [setting] Simulate 键）+ 仓位状态机后半台跑；
//   ② 500ms 拉快照刷仓位网格（状态机状态一并注入卡片）；
//   ③ 点表配置窗/工艺策略窗命令（保存后重建总线/刷新策略即生效）。
// 为什么 500ms 轮询 UI：状态机的灯写与计时全在后台（铁律 2），UI 只读
//   快照与状态机状态，500ms 全量刷最稳（事件驱动 UI 属后续优化）。
// 怎么改：新增窗体命令经 IDialogService；总线边沿经 _machine 转发（后台
//   线程直调，不碰 UI）；重连只换总线通道（Attach）不丢仓位流程。
// =========================================================================

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CureStation.Themes;
using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Core.Services;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>仓位数据行：架×层的实时状态（卡片网格直接绑这些 bool/文本）。</summary>
public sealed class RackSlotViewModel : ViewModelBase
{
    public int Rack { get; set; }
    public int Level { get; set; }

    public string Title => $"架{Rack}-{LevelName(Level)}";

    /// <summary>
    /// 客户库位编码（Date Matrix 格式，如 A01/J12）——与现场机台实景编排一致，
    /// 是卡片主标题；后续扫码绑定工序以它为库位主键（换算逻辑收在
    /// Core 的 SlotLocationCode，界面显示与扫码绑定同源，防口径分叉）。
    /// 双架口径（V0.0.20）：按架内站号换算（全局架号 11~20 → 站 1~10 → A01~J12），
    /// 每台架各自贴同一套标签；单架（架 1~10）与原 Encode 完全等价。
    /// Rack/Level 构造后不变，计算属性无需 INPC。
    /// </summary>
    public string Code => SlotLocationCode.EncodeInRack(Rack, Level);

    private static string LevelName(int level) => level switch
    {
        1 => "上层", 2 => "中层", _ => "下层",
    };

    // 展示状态（bool 转中文文本，卡片尾部一行放"层名 + 托盘"）
    /// <summary>卡片尾部托盘文案："◈ 托盘在"（有货）/ "无托盘"（空位）/ "—"(读不到)。</summary>
    private string _trayFoot = "—";
    public string TrayFoot
    {
        get => _trayFoot;
        private set => Set(ref _trayFoot, value);
    }
    private string _grillText = "—";
    public string GrillText
    {
        get => _grillText;
        private set => Set(ref _grillText, value);
    }
    private string _lampText = "灭";
    public string LampText
    {
        get => _lampText;
        private set => Set(ref _lampText, value);
    }
    private string _slotStateText = "空闲";
    public string SlotStateText
    {
        get => _slotStateText;
        private set => Set(ref _slotStateText, value);
    }

    // ---------- 卡片视觉状态（Kind，新卡模板六态状态口径，2026-10-29） ----------
    // 六态键与主题字典 Brush.Tint*/Brush.Head*/Brush.SlotBorder*/Brush.CardGlow*
    // 的 XAML 触发器同名（改键名要同步：这里 + 两字典 + MainWindow.xaml 触发器）。
    // Kind=新模板/统计条的六态业务口径（Alarm=红灯占用、Gate=待门控、Curing=计时）。

    /// <summary>空闲/待门控/计时中/放行/红灯占用/未配置（统计条与卡片触发器同源）。</summary>
    private string _kind = "Idle";
    public string Kind
    {
        get => _kind;
        private set => Set(ref _kind, value);
    }

    private int _remainMin;
    /// <summary>剩余固化分钟（状态机 Curing 精确值；灯色反推=0）。</summary>
    public int RemainMin
    {
        get => _remainMin;
        private set => Set(ref _remainMin, value);
    }

    private int _totalMin;
    /// <summary>本仓锁定总时长分钟（>0 才有精确计时数据）。</summary>
    public int TotalMin
    {
        get => _totalMin;
        private set
        {
            if (Set(ref _totalMin, value))
                Raise(nameof(HasDetail)); // 派生属性手动补通知（卡内"大数字/状态字"切换依据）
        }
    }

    private int _progressPct;
    /// <summary>进度百分比 0~100（(总-剩)/总；未计时=0）。</summary>
    public int ProgressPct
    {
        get => _progressPct;
        private set => Set(ref _progressPct, value);
    }

    /// <summary>有精确计时数据（状态机 Curing 且 TotalMin&gt;0）——卡内大数字+进度条只在它成立时展示，
    /// 否则显示状态字（灯色反推的"计时中"没有精确数值，硬显 0 反误导）。</summary>
    public bool HasDetail => TotalMin > 0;

    /// <summary>层名（"上层/中层/下层"）——卡片尾部一行与 ToolTip 共用；
    /// Rack/Level 构造后不变，计算属性无需 INPC。</summary>
    public string LayerName => LevelName(Level);

    /// <summary>统一落 Kind/Remain/Total/Progress（保持不变量：Total=0 → Remain=0、Pct=0）。</summary>
    private void ApplyKind(string kind, int remainMinutes, int totalMinutes)
    {
        Kind = kind;
        TotalMin = Math.Max(0, totalMinutes);
        RemainMin = TotalMin > 0 ? Math.Clamp(remainMinutes, 0, TotalMin) : 0;
        ProgressPct = TotalMin > 0
            ? Math.Clamp((int)Math.Round(100.0 * (TotalMin - RemainMin) / TotalMin,
                MidpointRounding.AwayFromZero), 0, 100)
            : 0;
    }

    /// <summary>
    /// 一拍快照刷新（调用方 500ms 定时；数据从语义键直查快照）。
    /// machineStatus=状态机状态（非空闲时展示口径以状态机为准——剩余分钟数
    /// 灯色反推不出来）；空闲/没动过的仓位走灯色反推口径（与骨架期一致）。
    /// </summary>
    public void Refresh(PointTableSnapshot snapshot, bool hasTable, SlotStatus? machineStatus = null)
    {
        if (!hasTable)
        {
            SlotStateText = "未配置";
            ApplyKind("NotConfigured", 0, 0);
            return;
        }

        var tray = snapshot.Get(new(true, IoPointRole.TrayDetect, Rack, Level));
        var grillL = snapshot.Get(new(true, IoPointRole.RackLightL, Rack, Level));
        var grillR = snapshot.Get(new(true, IoPointRole.RackLightR, Rack, Level));
        var red = snapshot.Get(new(false, IoPointRole.LampRed, Rack, Level));
        var yellow = snapshot.Get(new(false, IoPointRole.LampYellow, Rack, Level));
        var green = snapshot.Get(new(false, IoPointRole.LampGreen, Rack, Level));

        TrayFoot = tray == true ? "◈ 托盘在" : tray == false ? "无托盘" : "—";
        GrillText = grillL == null && grillR == null ? "—"
            : $"L:{BoolText(grillL)} R:{BoolText(grillR)}";
        LampText = red == true ? "红灯" : yellow == true ? "黄灯"
            : green == true ? "绿灯" : "灭";
        SlotStateText = machineStatus switch
        {
            { Phase: SlotPhase.Curing } => machineStatus.StateText, // "计时中 剩X分"
            { Phase: SlotPhase.Done } => "放行",
            { Phase: SlotPhase.WaitingGate } => "待门控",
            _ => mapState(tray, red, yellow, green),                 // 空闲走灯色反推
        };
        // Kind（新口径）：状态机给精确状态的仓位以状态机为准（含剩余分钟）；
        // 状态机空闲/未涉足的仓位灯色反推——红=Alarm（占用）、黄=Curing（无精确
        // 数值，HasDetail=false 显示状态字）、绿=Done、托盘在=Gate（待判定）、全空=Idle。
        if (machineStatus is { Phase: SlotPhase.Curing } cur)
            ApplyKind("Curing", cur.RemainMinutes, cur.TotalMinutes);
        else if (machineStatus is { Phase: SlotPhase.Done })
            ApplyKind("Done", 0, 0);
        else if (machineStatus is { Phase: SlotPhase.WaitingGate })
            ApplyKind("Gate", 0, 0);
        else if (red == true) ApplyKind("Alarm", 0, 0);
        else if (yellow == true) ApplyKind("Curing", 0, 0);
        else if (green == true) ApplyKind("Done", 0, 0);
        else if (tray == true) ApplyKind("Gate", 0, 0);
        else ApplyKind("Idle", 0, 0);
    }

    private static string BoolText(bool? v) => v == true ? "ON" : v == false ? "OFF" : "—";

    private static string mapState(bool? tray, bool? red, bool? yellow, bool? green) =>
        red == true ? "占用" : yellow == true ? "计时中" : green == true ? "放行"
            : tray == true ? "待判定" : "空闲";
}

/// <summary>
/// 固化架分组（V0.0.20 双架支持）：一台固化架 = 一个分组 = 一块 10×3 网格，
/// 主窗按组上下堆叠（组间带标题条），贴机台实景（两台架各 10 站 3 层）。
/// 组内卡片是 <see cref="MainViewModel.Slots"/> 平铺集合的同批实例（引用共享，
/// 500ms 刷一遍即两组同步），构建后全部只读、无需 INPC。
/// </summary>
public sealed class RackGroupViewModel
{
    /// <summary>分组标题（"1号固化架"/"2号固化架"——全局架号段归属）。</summary>
    public string Title { get; }

    /// <summary>组内仓位卡（行优先：先层后架，与平铺契约一致）。</summary>
    public IReadOnlyList<RackSlotViewModel> Slots { get; }

    /// <summary>组内列数（每架 10 站，与 SlotLocationCode.RacksPerRow 同源）。</summary>
    public int Columns => SlotLocationCode.RacksPerRow;

    /// <summary>组内行数（层数，跟随点表）。</summary>
    public int Rows { get; }

    /// <summary>是否显示分组标题条：单架（组数=1）不显示，主窗视觉与单架版完全一致。</summary>
    public bool IsHeaderVisible { get; }

    public RackGroupViewModel(string title, IReadOnlyList<RackSlotViewModel> slots,
        int rows, bool isHeaderVisible)
    {
        Title = title;
        Slots = slots;
        Rows = rows;
        IsHeaderVisible = isHeaderVisible;
    }
}

/// <summary>主窗 VM。总线生命周期：Dispose 与重建；競争以 lock 保证。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _refreshTimer;
    private readonly object _busLock = new();

    public ObservableCollection<RackSlotViewModel> Slots { get; } = new();

    /// <summary>
    /// 固化架分组视图（V0.0.20 双架支持）：主窗主内容按组上下堆叠——
    /// 单架 1 组（无标题条，视觉同单架版），双架 2 组（各带"N号固化架"标题条）。
    /// 组内卡片与 Slots 同批实例。
    /// </summary>
    public ObservableCollection<RackGroupViewModel> RackGroups { get; } = new();

    /// <summary>分组数——主窗外层 UniformGrid.Rows 绑定它（纵向每组均分一档）。</summary>
    private int _groupCount = 1;
    public int GroupCount
    {
        get => _groupCount;
        private set => Set(ref _groupCount, value);
    }

    /// <summary>
    /// 网格列数（每排站数/架数）——主窗 UniformGrid.Columns 绑定它：
    /// 窗口拉伸（全屏/不同分辨率）时 10 张卡均分一行宽度，与机台"一排 10 站"一致。
    /// </summary>
    private int _rackCount = 10;
    public int RackCount
    {
        get => _rackCount;
        private set => Set(ref _rackCount, value);
    }

    /// <summary>网格行数（层数）——UniformGrid.Rows 绑定，三排=上中下三层。</summary>
    private int _levelCount = 3;
    public int LevelCount
    {
        get => _levelCount;
        private set => Set(ref _levelCount, value);
    }

    private string _connectionText = "未启动";
    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    /// <summary>当前生效策略（顶栏展示；策略窗激活后经 LoadPolicyBook 刷新）。</summary>
    private string _policyText = "策略：未加载";
    public string PolicyText
    {
        get => _policyText;
        private set => Set(ref _policyText, value);
    }

    /// <summary>当前登录操作员（顶栏展示；登录窗带出，级别是各窗门禁依据）。</summary>
    private string _currentUserName = "";
    private OperatorLevel? _currentLevel;

    private string _operatorText = "未登录";
    public string OperatorText
    {
        get => _operatorText;
        private set => Set(ref _operatorText, value);
    }

    /// <summary>是否有操作员在线（未登录时各功能入口一律拦下）。</summary>
    public bool IsLoggedIn => _currentLevel.HasValue;

    /// <summary>单线过账状态（顶栏展示；过账服务后台线程回调，经 UiInvoke 封送）。</summary>
    private string _postingText = "过账：—";
    public string PostingText
    {
        get => _postingText;
        private set => Set(ref _postingText, value);
    }

    /// <summary>过账是否启用（工单卡角标 DataTrigger 出"过账启用/过账未启用"文案）。</summary>
    private bool _postingEnabled;
    public bool PostingEnabled
    {
        get => _postingEnabled;
        private set => Set(ref _postingEnabled, value);
    }

    /// <summary>演示模式当前态（true=Mock 总线流转中；顶栏开关按钮 DataTrigger 高亮）。</summary>
    private bool _isSimulate;
    public bool IsSimulate
    {
        get => _isSimulate;
        private set => Set(ref _isSimulate, value);
    }

    /// <summary>演示开关按钮文案：显示当前模式（"演示：开"=Mock 演示中 / "演示：关"=真机通讯中）。</summary>
    private string _simulateText = "演示：关";
    public string SimulateText
    {
        get => _simulateText;
        private set => Set(ref _simulateText, value);
    }

    // ---------- 顶部统计条（方案 D，2026-10-29；RefreshSlots 每拍聚合） ----------
    // 五态口径与卡片 Kind 同名（Idle/Gate/Curing/Done/Alarm），详见 SlotStatistics。

    private int _idleCount;
    public int IdleCount { get => _idleCount; private set => Set(ref _idleCount, value); }

    private int _curingCount;
    public int CuringCount { get => _curingCount; private set => Set(ref _curingCount, value); }

    private int _gateCount;
    public int GateCount { get => _gateCount; private set => Set(ref _gateCount, value); }

    private int _doneCount;
    public int DoneCount { get => _doneCount; private set => Set(ref _doneCount, value); }

    private int _alarmCount;
    public int AlarmCount { get => _alarmCount; private set => Set(ref _alarmCount, value); }

    private string _nextCloseText = "暂无计时";
    /// <summary>⚡剩余最短（计时中最早到点的库位，操作员一眼锁定下一个取件口）。</summary>
    public string NextCloseText
    {
        get => _nextCloseText;
        private set => Set(ref _nextCloseText, value);
    }

    // ---------- 下部工单带（信息带） ----------
    // 数据源=config/workorder.json（WorkOrderStore）。LastWriteTime 变了才重读
    // （500ms 同拍不反复碰磁盘）；工单窗保存返回 true 也主动刷一次。

    private string _workOrderRcNoR = "—";
    public string WorkOrderRcNoR { get => _workOrderRcNoR; private set => Set(ref _workOrderRcNoR, value); }

    private string _workOrderQtyR = "0/0";
    public string WorkOrderQtyR { get => _workOrderQtyR; private set => Set(ref _workOrderQtyR, value); }

    private int _workOrderPctR;
    public int WorkOrderPctR { get => _workOrderPctR; private set => Set(ref _workOrderPctR, value); }

    private string _workOrderRcNoL = "—";
    public string WorkOrderRcNoL { get => _workOrderRcNoL; private set => Set(ref _workOrderRcNoL, value); }

    private string _workOrderQtyL = "0/0";
    public string WorkOrderQtyL { get => _workOrderQtyL; private set => Set(ref _workOrderQtyL, value); }

    private int _workOrderPctL;
    public int WorkOrderPctL { get => _workOrderPctL; private set => Set(ref _workOrderPctL, value); }

    // ---------- 主题切换（双主题 G2/G1） ----------

    private string _toggleThemeText = "深色";
    /// <summary>主题切换按钮文案：显示【目标】主题名——浅色下显示"深色"（点了切去 G1），反之"浅色"。</summary>
    public string ToggleThemeText
    {
        get => _toggleThemeText;
        private set => Set(ref _toggleThemeText, value);
    }

    private PointTableConfig? _config;
    private IPointTableBus? _bus;
    private SlotCuringMachine? _machine;   // 仓位状态机（与 VM 同生命周期，总线可换不丢流程）
    private CuringPolicyBook? _policyBook; // 工艺策略库（策略窗保存后经 LoadPolicyBook 重读）
    private CuringMesPoster? _poster;      // 单线过账服务（P3.2 后半；配置热更新不换实例）

    public RelayCommand OpenPointTableCommand { get; }
    public RelayCommand OpenCuringPolicyCommand { get; }
    public RelayCommand OpenPostConfigCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand OpenWorkOrderCommand { get; }
    public RelayCommand OpenOperatorsCommand { get; }
    public RelayCommand OpenIoTestCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleSimulateCommand { get; }

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        OpenPointTableCommand = new RelayCommand(OpenPointTable);
        OpenCuringPolicyCommand = new RelayCommand(OpenCuringPolicy);
        OpenPostConfigCommand = new RelayCommand(OpenPostConfig);
        ReconnectCommand = new RelayCommand(() => StartBus(_config));
        LoginCommand = new RelayCommand(Login);
        OpenWorkOrderCommand = new RelayCommand(OpenWorkOrder);
        OpenOperatorsCommand = new RelayCommand(OpenOperators);
        OpenIoTestCommand = new RelayCommand(OpenIoTest);
        ShowLogCommand = new RelayCommand(ShowLog);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ToggleSimulateCommand = new RelayCommand(ToggleSimulate);
        // 主题按钮文案对齐 App.OnStartup 已应用的当前主题（测试态无 App：ThemeManager
        // 缺省 Light，同样落"深色"文案，现场首点即切 G1，行为一致）。
        ToggleThemeText = ThemeManager.Current == "Dark" ? "浅色" : "深色";
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => RefreshSlots();

        // 单线过账装配（P3.2 后半）：读 [CuringMes] 配置 → 建过账服务 →
        // 顶栏状态随 PostingChanged 刷新（后台线程回调，UiInvoke 封送）。
        var postOptions = CuringMesOptions.Load();
        _poster = new CuringMesPoster(postOptions,
            () => WorkOrderStore.Load(), log: m => LocalLogService.Write(m));
        _poster.PostingChanged += t => UiInvoke(() => PostingText = t);
        CuringMesPosterHolder.Default = _poster; // 工单窗上传动作的全局访问点
        PostingText = postOptions.Enabled
            ? $"过账：已启用（{CuringMesOptions.NormSide(postOptions.Side)} 工位）"
            : "过账：未启用";
        PostingEnabled = postOptions.Enabled;

        // 启动装配：读点表 + 工艺策略（都自愈）→ 总线 Start + 状态机
        var config = LoadOrDefault();
        _config = config;   // 记住当前点表（重连按钮复用，勿传 null）
        BuildSlots(config);
        LoadPolicyBook();
        // 演示开关初始态：与 StartBus 同源读 ini——顶栏按钮状态与实际总线类型一致
        IsSimulate = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
        SimulateText = IsSimulate ? "演示：开" : "演示：关";
        StartBus(config);
        _refreshTimer.Start();
    }

    // 点表读取：文件没了/空表 → 建缺省模板并立即落盘（现场第一次开机就有东西看）。
    private PointTableConfig LoadOrDefault()
    {
        var config = IoPointTableStore.Load();
        if (config == null)
        {
            config = IoPointTableStore.BuildDefaultTemplate("A线OP30固化架IO", "192.168.1.20", 10, 3);
            IoPointTableStore.Save(config);
            _dialogs.ShowMessage(
                "点表配置为空，已按 Audi cover module IO 表生成缺省模板并保存。\n" +
                "菜单可打开点表配置窗调整（IP/绑定）。",
                "配置自愈");
        }
        return config;
    }

    /// <summary>
    /// 工艺策略装载：缺/坏 → 缺省库落盘自愈（静默不弹框——策略有缺省值可直接跑，
    /// 与点表"要用户改 IP"不同，不打扰操作员；自愈原因进操作日志）。
    /// 激活名同步到顶栏 PolicyText。
    /// </summary>
    private void LoadPolicyBook()
    {
        var reasons = new List<string>();
        var book = CuringPolicyStore.Load(reasons: reasons);
        if (book == null)
        {
            book = CuringPolicyStore.BuildDefaultBook();
            CuringPolicyStore.Save(book); // 自愈落盘（现场删了配置文件也能一键回来）
            foreach (var r in reasons)
                LocalLogService.Write(r);
        }
        _policyBook = book;
        PolicyText = $"策略：{book.ResolveActive()?.Name ?? "无"}";
    }

    /// <summary>状态机取当前激活策略（每次托盘到位现取——策略窗激活后热切换生效）。</summary>
    private CuringPolicy? GetActivePolicy() => _policyBook?.ResolveActive();

    /// <summary>
    /// 仓位网格骨架：架×层全组合铺满（占位结构来自点表绑定，缺省 10 架 3 层=30 仓位）。
    /// 排布口径（与现场机台实景一致）：三排=上中下三层，一排 10 站——集合按
    /// "行优先"生成（先层后架），UniformGrid（Columns=RackCount/Rows=LevelCount）
    /// 逐格填充即得三排十站布局；卡片主标题用客户库位编码（SlotLocationCode）。
    /// 双架（V0.0.20）：全局架号每 10 站切一组（1~10=1号架、11~20=2号架），
    /// 主窗按组上下堆叠各铺 10×3；平铺 Slots 顺序不变（回归锚点）。
    /// 注意：顺序是布局契约，改前先看 MainWindow.xaml 的 UniformGrid 与
    /// MainVm_Slots_RowMajorOrder 用例——乱序会导致"层"错行显示。
    /// </summary>
    private void BuildSlots(PointTableConfig config)
    {
        // 架号/层数列取最大绑定值（点表多大网格多大；没绑定时按客户编码表 10×3 缺省）
        int racks = Math.Max(SlotLocationCode.RacksPerRow, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Rack > 0).Select(b => b.Rack).DefaultIfEmpty(SlotLocationCode.RacksPerRow).Max());
        int levels = Math.Max(SlotLocationCode.Levels, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Level > 0).Select(b => b.Level).DefaultIfEmpty(SlotLocationCode.Levels).Max());

        var all = new Dictionary<(int Rack, int Level), RackSlotViewModel>();
        for (int level = 1; level <= levels; level++)
        for (int rack = 1; rack <= racks; rack++)
        {
            var slot = new RackSlotViewModel { Rack = rack, Level = level };
            Slots.Add(slot);
            all[(rack, level)] = slot;
        }
        RackCount = racks;
        LevelCount = levels;

        // 分组视图：全局架号每 RacksPerRow(10) 站一台架，组内行优先（同平铺契约）。
        // 组数>1 才显示标题条——单架现场主窗与旧版视觉完全一致。
        RackGroups.Clear();
        int groupCount = (racks + SlotLocationCode.RacksPerRow - 1) / SlotLocationCode.RacksPerRow;
        for (int g = 1; g <= groupCount; g++)
        {
            int start = (g - 1) * SlotLocationCode.RacksPerRow + 1;
            int end = Math.Min(g * SlotLocationCode.RacksPerRow, racks);
            var groupSlots = new List<RackSlotViewModel>();
            for (int level = 1; level <= levels; level++)
            for (int rack = start; rack <= end; rack++)
                groupSlots.Add(all[(rack, level)]);
            RackGroups.Add(new RackGroupViewModel(
                $"{g}号固化架", groupSlots, levels, groupCount > 1));
        }
        GroupCount = groupCount;
    }

    private void StartBus(PointTableConfig? config)
    {
        lock (_busLock)
        {
            if (config == null || config.Devices.Count == 0)
            {
                ConnectionText = "未配置设备";
                return;
            }
            var validation = PointTableValidator.Validate(config);
            if (!validation.Ok)
            {
                // 点表坏了照样起（模拟模式兜底），现场在配置窗修正
                ConnectionText = "点表有误（见点表配置窗）";
                _dialogs.ShowMessage(string.Join("\r\n", validation.Errors), "点表校验失败");
                return;
            }

            // 演示开关：config.ini [setting] Simulate=1 → Mock 总线（无硬件全流程），
            // key 与主程序 CimGateway（原 PlcMesBridge）同名沿用（跨程序一致的运维习惯）。
            bool simulate = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
            IPointTableBus bus = simulate ? new MockPointTableBus() : new ModbusPointTableBus();
            bus.Start(config);

            // 状态机接线（全部后台线程——铁律 2）：边沿直转（machine 内部有锁）；
            // 断连恢复对齐快照（托盘在架的仓位重新计时）；连接态/错误照旧回 UI。
            // 三事件回调统一比对 sender==_bus——总线热切换（重连/演示开关）时旧总线
            // Dispose 后残留线程仍可能晚发事件（连接超时兜圈超过停机等待上限），
            // 不比对会把新总线的顶栏文本/状态机喂上旧总线的报错与边沿（实测踩中）。
            bus.PointChanged += (s, e) =>
            {
                if (ReferenceEquals(s, _bus))
                    _machine?.OnPointChanged(e);
            };
            bus.ConnectionChanged += (s, connected) =>
            {
                if (!ReferenceEquals(s, _bus))
                    return;
                if (connected)
                    _machine?.AlignFromSnapshot();
                UiInvoke(() =>
                {
                    IsConnected = connected;
                    ConnectionText = bus.ConnectionSummary;
                });
            };
            bus.Error += (s, msg) => UiInvoke(() =>
            {
                if (ReferenceEquals(s, _bus))
                    ConnectionText = msg;
            });
            // 顶栏连接状态列是 * 弹性宽（不挤右侧按钮），错误文本不截断完整显示；
            // 极窄窗口由 XAML TextTrimming 省略号兜底 + ToolTip 悬停看全文。

            // 状态机：首次创建（带总线），重连只换通道（Attach，流程状态不丢）。
            // 顺序刻意"先建/换妥新通道、再停旧总线"——状态机中途不会摸到已释放对象。
            if (_machine == null)
            {
                _machine = new SlotCuringMachine(bus, GetActivePolicy,
                    log: m => LocalLogService.Write(m));
                // 过账挂点（P3.2 后半）：状态机三事件 → 过账服务（只在创建时订阅
                // 一次——重连 Attach 换通道不重复订阅）。事件在状态机锁内触发，
                // Poster 方法内部 Task.Run 立即返回，不重入不阻塞。
                _machine.TrayArrived += (rack, level) => _poster?.OnTrayArrived(rack, level);
                _machine.CuringStarted += (rack, level) => _poster?.OnCuringStarted(rack, level);
                _machine.TrayLeft += (rack, level, phase, sec) =>
                    _poster?.OnTrayLeft(rack, level, phase, sec);
            }
            else
                _machine.Attach(bus);
            _machine.AlignFromSnapshot(); // 首启/点表变更后按当前快照对齐

            var oldBus = _bus;
            _bus = bus;
            oldBus?.Dispose();
            IsConnected = _bus.IsConnected;
            ConnectionText = _bus.ConnectionSummary;
        }
    }

    private void RefreshSlots()
    {
        var snapshot = _bus?.LatestSnapshot;
        foreach (var slot in Slots)
        {
            var status = _machine?.GetStatus(slot.Rack, slot.Level);
            slot.Refresh(snapshot ?? PointTableSnapshot.Empty, snapshot != null, status);
        }
        // 统计条 + 工单带同拍刷（挂在同一 500ms tick 内，不另起定时器；UI 线程）。
        RefreshStats();
        RefreshWorkOrder();
    }

    private void OpenPointTable()
    {
        if (!Gate(OperatorLevel.Level4, "点表配置"))
            return;
        var saved = _dialogs.ShowPointTable();
        var config = IoPointTableStore.Load() ?? LoadOrDefault();
        _config = config;   // 点表可能已改，重连按钮要用新表
        BuildSlots(config);
        StartBus(config);
        if (saved)
            _dialogs.ShowMessage("点表已保存并生效。", "保存成功");
    }

    /// <summary>工艺策略窗：保存/激活后重读策略库（激活名可能已换，热切换生效）。</summary>
    private void OpenCuringPolicy()
    {
        if (!Gate(OperatorLevel.Level3, "工艺策略"))
            return;
        var saved = _dialogs.ShowCuringPolicy();
        if (saved)
        {
            LoadPolicyBook();
            _dialogs.ShowMessage("工艺策略已保存并生效。", "保存成功");
        }
    }

    // ---------- 登录与权限门禁（P3.2） ----------

    /// <summary>
    /// 门禁统一出口：未登录先拦（提示登录），级别不够提示所需级别。
    /// 返回 true=放行。各功能所需级别：工单 L1 / 工艺策略 L3 / 点表·权限管理 L4。
    /// </summary>
    private bool Gate(OperatorLevel required, string what)
    {
        if (!_currentLevel.HasValue)
        {
            _dialogs.ShowMessage("请先登录（顶栏『登录』按钮）。", "未登录");
            return false;
        }
        if (!OperatorLevelInfo.AtLeast(_currentLevel, required))
        {
            _dialogs.ShowMessage(
                $"{what}需要 {OperatorLevelInfo.DisplayName(required)} 及以上，" +
                $"当前 {OperatorLevelInfo.DisplayName(_currentLevel.Value)}。",
                "权限不足");
            return false;
        }
        return true;
    }

    /// <summary>登录窗：成功后记下操作员与级别（再点可换账号重登）。</summary>
    private void Login()
    {
        var result = _dialogs.ShowLogin();
        if (result == null)
            return;
        (_currentUserName, _currentLevel) = result.Value;
        // 顶栏格式"管理员：admin"——权限短名开头（V0.0.20 口径：不带"操作员"前缀、
        // 不带 LEVEL 字样），级别全名只在权限管理窗展示。
        OperatorText = $"{OperatorLevelInfo.ShortName(_currentLevel.Value)}：{_currentUserName}";
        LocalLogService.Write($"操作员登录：{_currentUserName}（{OperatorLevelInfo.ToText(_currentLevel.Value)}）");
    }

    /// <summary>工单信息窗：L1+ 可开（窗内编辑区按级别再细分禁用）。</summary>
    private void OpenWorkOrder()
    {
        if (!Gate(OperatorLevel.Level1, "工单信息"))
            return;
        var saved = _dialogs.ShowWorkOrder(_currentLevel);
        if (saved)
        {
            // 工单窗刚落盘——主动重读一次（下部工单带立见新号，不等 500ms 下一拍；
            // LastWriteTime 戳变了 RefreshWorkOrder 自然重读，这里显式调只是不等待）。
            RefreshWorkOrder();
            _dialogs.ShowMessage("工单信息已保存。", "保存成功");
        }
    }

    /// <summary>权限管理窗：L4 专属。</summary>
    private void OpenOperators()
    {
        if (!Gate(OperatorLevel.Level4, "权限管理"))
            return;
        _dialogs.ShowOperators();
    }

    /// <summary>
    /// IO 通讯测试窗：L3+（工程师——现场接线的角色；比点表 L4 低一档，
    /// 因为测试只动 IO 不改配置）。共享当前总线与点表（null 拦下——总线
    /// 还没启动时无连接可共享）。
    /// </summary>
    private void OpenIoTest()
    {
        if (!Gate(OperatorLevel.Level3, "IO通讯测试"))
            return;
        if (_bus == null || _config == null)
        {
            _dialogs.ShowMessage("总线尚未启动（无连接可共享）。\n请先在点表配置窗保存点表并重连。", "IO通讯测试");
            return;
        }
        _dialogs.ShowIoTest(_bus, _config);
    }

    /// <summary>
    /// 过账配置窗：L4 专属。保存成功后热更新过账服务配置（不重启即生效）
    /// 并刷新顶栏状态。
    /// </summary>
    private void OpenPostConfig()
    {
        if (!Gate(OperatorLevel.Level4, "过账配置"))
            return;
        var saved = _dialogs.ShowMesPostConfig();
        if (saved)
        {
            var options = CuringMesOptions.Load();
            _poster?.UpdateOptions(options);
            PostingText = options.Enabled
                ? $"过账：已启用（{CuringMesOptions.NormSide(options.Side)} 工位）"
                : "过账：未启用";
            PostingEnabled = options.Enabled;
            _dialogs.ShowMessage("过账配置已保存并生效。", "保存成功");
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        lock (_busLock)
        {
            _machine?.Dispose(); // 先停状态机心跳（不再写灯），再停总线
            _machine = null;
            _bus?.Dispose();
            _bus = null;
        }
    }

    // ---------- 顶部统计条 + 下部工单带（方案 D 信息带，2026-10-29） ----------

    /// <summary>
    /// 卡片集合 → 统计条：五态计数 + ⚡剩余最短。纯函数收在 Core 的
    /// SlotStatistics（可测，主 VM 只做搬运）；500ms 同拍，UI 线程。
    /// </summary>
    private void RefreshStats()
    {
        IEnumerable<SlotStatItem> items = Slots.Select(s => new SlotStatItem(s.Kind, s.RemainMin, s.Code));
        var (idle, curing, gate, done, alarm) = SlotStatistics.Count(items);
        IdleCount = idle;
        CuringCount = curing;
        GateCount = gate;
        DoneCount = done;
        AlarmCount = alarm;
        NextCloseText = SlotStatistics.NextClose(items, prefix: "⚡");
    }

    /// <summary>
    /// 工单簿 → 底部信息带六件事（R/L 各：单号文本—空单号显示"—" / "已产/预设"文本 /
    /// 产出百分比 0~100——预设为 0 时 0%）。纯映射静态函数（测试锁口径不用起 VM）。
    /// </summary>
    public static (string RcNoR, string QtyR, int PctR, string RcNoL, string QtyL, int PctL)
        SummarizeWorkOrder(WorkOrderBook book)
    {
        static (string, string, int) One(WorkOrderSlot s) => (
            string.IsNullOrWhiteSpace(s.RcNo) ? "—" : s.RcNo,
            $"{Math.Max(0, s.ProducedQty)}/{Math.Max(0, s.PresetQty)}",
            s.PresetQty > 0
                ? Math.Clamp(100 * Math.Min(s.ProducedQty, s.PresetQty) / s.PresetQty, 0, 100)
                : 0);
        var r = One(book.Right);
        var l = One(book.Left);
        return (r.Item1, r.Item2, r.Item3, l.Item1, l.Item2, l.Item3);
    }

    // 工单带磁盘读节流：LastWriteTime 戳 + 首刷标记——同拍戳没变直接跳过读文件。
    private DateTime _workOrderStampUtc = DateTime.MinValue;
    private bool _workOrderLoaded;

    /// <summary>
    /// 刷下部工单带：文件"最后写时间戳"变了才 Load（避免 500ms 反复碰盘）；
    /// 缺文件按空工单兜底（WorkOrderStore 自愈语义）。工单窗保存后也会被
    /// OpenWorkOrder 主动调一次（不靠 500ms 兜底，操作员保存即见）。
    /// </summary>
    private void RefreshWorkOrder()
    {
        string path = WorkOrderStore.DefaultPath;
        try
        {
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_workOrderLoaded && stamp == _workOrderStampUtc)
                return;
            _workOrderStampUtc = stamp;
        }
        catch
        {
            // 取时间戳失败沿用上次状态（下一拍再试；文件 IO 出错不拖 UI 拍）
            return;
        }
        _workOrderLoaded = true;

        var book = WorkOrderStore.Load() ?? WorkOrderStore.BuildDefault();
        var (rcR, qtyR, pctR, rcL, qtyL, pctL) = SummarizeWorkOrder(book);
        WorkOrderRcNoR = rcR;
        WorkOrderQtyR = qtyR;
        WorkOrderPctR = pctR;
        WorkOrderRcNoL = rcL;
        WorkOrderQtyL = qtyL;
        WorkOrderPctL = pctL;
    }

    // ---------- 日志悬浮窗（顶栏【日志】；非模态单例由 DialogService 防重复） ----------

    /// <summary>日志/控制台悬浮窗：L1+（只读不碰配置，操作员即可开）。</summary>
    private void ShowLog()
    {
        if (!Gate(OperatorLevel.Level1, "日志悬浮窗"))
            return;
        _dialogs.ShowLog(BuildSystemSnapshot());
    }

    /// <summary>
    /// 日志悬浮窗右侧"系统状态卡"快照（打开一刻定格）——顺手把当日日志目录
    /// 一并带上，现场找日志文件直接看路径。
    /// </summary>
    public LogSystemSnapshot BuildSystemSnapshot()
    {
        var rows = new List<SystemStatusRow>
        {
            new("连接状态", IsConnected ? "已连接" : "未连接"),
            new("总线", string.IsNullOrWhiteSpace(ConnectionText) ? "—" : ConnectionText),
            new("激活策略", _policyBook?.ResolveActive()?.Name ?? "无"),
            new("过账", PostingText),
            new("登录", OperatorText),
            new("仓位", $"{Slots.Count} 个（空闲{IdleCount} 计时{CuringCount} 待门控{GateCount} " +
                        $"放行{DoneCount} 红灯{AlarmCount}）"),
            new("日志目录", AppPaths.TodayLogDir()),
        };
        return new LogSystemSnapshot(DateTime.Now.ToString("HH:mm:ss"), rows);
    }

    // ---------- 双主题切换（Light=G2 浅色 / Dark=G1 深空） ----------

    /// <summary>
    /// 主题切换：目标名 → ThemeManager 换字典（UI 线程，DynamicResource 全窗生效）
    /// → 写回 ini（现场记住，下次启动同主题；写盘失败只丢记忆不回切）→
    /// 按钮文案翻新目标。切完记一笔操作日志（含主题名，方便现场回溯）。
    /// </summary>
    private void ToggleTheme()
    {
        var target = ThemeManager.ToggleTarget;
        ThemeManager.ApplyTheme(target);
        try
        {
            IniFile.Write(AppPaths.ConfigIni, "setting", "Theme", target);
        }
        catch
        {
            // 写盘失败不回切——界面已换，只是没记住（下次启动回旧主题）
        }
        ToggleThemeText = target == "Dark" ? "浅色" : "深色";
        LocalLogService.Write($"主题切换：{(target == "Dark" ? "深色（G1）" : "浅色（G2）")}");
    }

    // ---------- 演示模式开关（Mock 数据热切换，2026-10-30） ----------

    /// <summary>
    /// 演示模式开关：写回 [setting] Simulate 后立即经 StartBus 重建总线
    /// （Mock↔真机热切换，无需重启；状态机 Attach 换通道流程状态不丢，
    /// 快照对齐语义与"重新连接"完全同链）。门禁 L4——与点表/过账配置同级：
    /// 演示边沿会照常驱动状态机与过账链（Mock 托盘流转也会触发 MES 过账），
    /// 影响生产数据口径，不给 L3。ini 写失败时配置保持旧值，但内存态已翻转——
    /// 与主题切换"写盘失败只丢记忆"口径一致（下次启动按盘上值起）。
    /// </summary>
    private void ToggleSimulate()
    {
        if (!Gate(OperatorLevel.Level4, "演示模式切换"))
            return;
        bool target = !IsSimulate;
        IniFile.Write(AppPaths.ConfigIni, "setting", "Simulate", target ? "1" : "0");
        IsSimulate = target;
        SimulateText = target ? "演示：开" : "演示：关";
        LocalLogService.Write(target
            ? "演示模式开启（Mock 总线，无真实硬件）"
            : "演示模式关闭（真机 Modbus 总线）");
        StartBus(_config);
    }
}
