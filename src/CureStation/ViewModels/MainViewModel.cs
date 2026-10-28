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
using System.Windows.Threading;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Security;
using PlcMesBridge.Core.Services;
using PlcMesBridge.ViewModels;

namespace CureStation.ViewModels;

/// <summary>仓位数据行：架×层的实时状态（卡片网格直接绑这些 bool/文本）。</summary>
public sealed class RackSlotViewModel : ViewModelBase
{
    public int Rack { get; set; }
    public int Level { get; set; }

    public string Title => $"架{Rack}-{LevelName(Level)}";

    private static string LevelName(int level) => level switch
    {
        1 => "上层", 2 => "中层", _ => "下层",
    };

    // 展示状态（bool 转中文文本 + 背景色，骨架期字符串化减少 XAML 转换器）
    private string _trayText = "—";
    public string TrayText
    {
        get => _trayText;
        private set => Set(ref _trayText, value);
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

    /// <summary>卡片背景色键名（xaml 绑成 Brush，缺省=空闲灰）。红/黄/绿/空闲。</summary>
    private string _cardState = "Idle";
    public string CardState
    {
        get => _cardState;
        private set => Set(ref _cardState, value);
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
            return;
        }

        var tray = snapshot.Get(new(true, IoPointRole.TrayDetect, Rack, Level));
        var grillL = snapshot.Get(new(true, IoPointRole.RackLightL, Rack, Level));
        var grillR = snapshot.Get(new(true, IoPointRole.RackLightR, Rack, Level));
        var red = snapshot.Get(new(false, IoPointRole.LampRed, Rack, Level));
        var yellow = snapshot.Get(new(false, IoPointRole.LampYellow, Rack, Level));
        var green = snapshot.Get(new(false, IoPointRole.LampGreen, Rack, Level));

        TrayText = tray == true ? "托盘在" : tray == false ? "托盘无" : "—";
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
        CardState = machineStatus switch
        {
            { Phase: SlotPhase.Curing } => "Yellow",
            { Phase: SlotPhase.Done } => "Green",
            _ => red == true ? "Red" : yellow == true ? "Yellow"
                : green == true ? "Green" : "Idle",
        };
    }

    private static string BoolText(bool? v) => v == true ? "ON" : v == false ? "OFF" : "—";

    private static string mapState(bool? tray, bool? red, bool? yellow, bool? green) =>
        red == true ? "占用" : yellow == true ? "计时中" : green == true ? "放行"
            : tray == true ? "待判定" : "空闲";
}

/// <summary>主窗 VM。总线生命周期：Dispose 与重建；競争以 lock 保证。</summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _refreshTimer;
    private readonly object _busLock = new();

    public ObservableCollection<RackSlotViewModel> Slots { get; } = new();

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

    private PointTableConfig? _config;
    private IPointTableBus? _bus;
    private SlotCuringMachine? _machine;   // 仓位状态机（与 VM 同生命周期，总线可换不丢流程）
    private CuringPolicyBook? _policyBook; // 工艺策略库（策略窗保存后经 LoadPolicyBook 重读）

    public RelayCommand OpenPointTableCommand { get; }
    public RelayCommand OpenCuringPolicyCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand OpenWorkOrderCommand { get; }
    public RelayCommand OpenOperatorsCommand { get; }

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        OpenPointTableCommand = new RelayCommand(OpenPointTable);
        OpenCuringPolicyCommand = new RelayCommand(OpenCuringPolicy);
        ReconnectCommand = new RelayCommand(() => StartBus(_config));
        LoginCommand = new RelayCommand(Login);
        OpenWorkOrderCommand = new RelayCommand(OpenWorkOrder);
        OpenOperatorsCommand = new RelayCommand(OpenOperators);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => RefreshSlots();

        // 启动装配：读点表 + 工艺策略（都自愈）→ 总线 Start + 状态机
        var config = LoadOrDefault();
        _config = config;   // 记住当前点表（重连按钮复用，勿传 null）
        BuildSlots(config);
        LoadPolicyBook();
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

    /// <summary>仓位网格骨架：架×层全组合铺满（占位结构来自点表绑定，缺省 10 架 3 层=30 仓位）。</summary>
    private void BuildSlots(PointTableConfig config)
    {
        // 架号/层数列取最大绑定值（点表多大网格多大；没绑定时 10×3 缺省）
        int racks = Math.Max(10, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Rack > 0).Select(b => b.Rack).DefaultIfEmpty(10).Max());
        int levels = Math.Max(3, config.Devices.SelectMany(d => d.Bindings)
            .Where(b => b.Level > 0).Select(b => b.Level).DefaultIfEmpty(3).Max());

        Slots.Clear();
        for (int rack = 1; rack <= racks; rack++)
        for (int level = 1; level <= levels; level++)
            Slots.Add(new RackSlotViewModel { Rack = rack, Level = level });
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
            // key 与主程序 PlcMesBridge 同名沿用（跨程序一致的运维习惯）。
            bool simulate = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
            IPointTableBus bus = simulate ? new MockPointTableBus() : new ModbusPointTableBus();
            bus.Start(config);

            // 状态机接线（全部后台线程——铁律 2）：边沿直转（machine 内部有锁）；
            // 断连恢复对齐快照（托盘在架的仓位重新计时）；连接态/错误照旧回 UI。
            bus.PointChanged += (_, e) => _machine?.OnPointChanged(e);
            bus.ConnectionChanged += (_, connected) =>
            {
                if (connected)
                    _machine?.AlignFromSnapshot();
                UiInvoke(() =>
                {
                    IsConnected = connected;
                    ConnectionText = bus.ConnectionSummary;
                });
            };
            bus.Error += (_, msg) => UiInvoke(() =>
                ConnectionText = msg.Length > 40 ? msg[..40] + "…" : msg);

            // 状态机：首次创建（带总线），重连只换通道（Attach，流程状态不丢）。
            // 顺序刻意"先建/换妥新通道、再停旧总线"——状态机中途不会摸到已释放对象。
            if (_machine == null)
                _machine = new SlotCuringMachine(bus, GetActivePolicy,
                    log: m => LocalLogService.Write(m));
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
        OperatorText = $"操作员：{_currentUserName}（{OperatorLevelInfo.DisplayName(_currentLevel.Value)}）";
        LocalLogService.Write($"操作员登录：{_currentUserName}（{OperatorLevelInfo.ToText(_currentLevel.Value)}）");
    }

    /// <summary>工单信息窗：L1+ 可开（窗内编辑区按级别再细分禁用）。</summary>
    private void OpenWorkOrder()
    {
        if (!Gate(OperatorLevel.Level1, "工单信息"))
            return;
        var saved = _dialogs.ShowWorkOrder(_currentLevel);
        if (saved)
            _dialogs.ShowMessage("工单信息已保存。", "保存成功");
    }

    /// <summary>权限管理窗：L4 专属。</summary>
    private void OpenOperators()
    {
        if (!Gate(OperatorLevel.Level4, "权限管理"))
            return;
        _dialogs.ShowOperators();
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
}
