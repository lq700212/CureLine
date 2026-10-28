// =========================================================================
// MainViewModel：固化程控主窗编排（程序①骨架）
//
// 干什么：① 启动时读点表（config/pointtable.json）→ 造总线（Mock/真机按
//   config.ini [setting] Simulate 键）后半台跑；② 500ms 拉快照刷仓位网格；
//   ③ 点表配置窗命令（编辑保存后重建总线即生效）。
// 为什么 500ms 轮询 UI 而不走事件订阅：骨架期网格全量刷新最稳（事件边沿
//   属于状态机 Phase 3 的活），500ms 挂 UI 帧，实测肉眼顺畅且负载低。
// 怎么改：新增窗体命令经 IDialogService；仓位状态机接入时从"拉快照"
//   升级为"边沿事件驱动+计时器"，本 VM 只增不改晾缓存。
// =========================================================================

using System.Collections.ObjectModel;
using System.Windows.Threading;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Infrastructure;
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

    /// <summary>一拍快照刷新（调用方 500ms 定时；数据从语义键直查快照）。</summary>
    public void Refresh(PointTableSnapshot snapshot, bool hasTable)
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
        SlotStateText = mapState(tray, red, yellow, green);
        CardState = red == true ? "Red" : yellow == true ? "Yellow"
            : green == true ? "Green" : "Idle";
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

    private PointTableConfig? _config;
    private IPointTableBus? _bus;

    public RelayCommand OpenPointTableCommand { get; }
    public RelayCommand ReconnectCommand { get; }

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        OpenPointTableCommand = new RelayCommand(OpenPointTable);
        ReconnectCommand = new RelayCommand(() => StartBus(_config));
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _refreshTimer.Tick += (_, _) => RefreshSlots();

        // 启动装配：读点表 → 兜缺省模板（首次启动直接可演示）→ 总线 Start
        var config = LoadOrDefault();
        _config = config;   // 记住当前点表（重连按钮复用，勿传 null）
        BuildSlots(config);
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
            _bus?.Dispose();
            _bus = null;
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
            bus.ConnectionChanged += (_, connected) => UiInvoke(() =>
            {
                IsConnected = connected;
                ConnectionText = bus.ConnectionSummary;
            });
            bus.Error += (_, msg) => UiInvoke(() =>
                ConnectionText = msg.Length > 40 ? msg[..40] + "…" : msg);

            _bus = bus;
            IsConnected = _bus.IsConnected;
            ConnectionText = _bus.ConnectionSummary;
        }
    }

    private void RefreshSlots()
    {
        var snapshot = _bus?.LatestSnapshot;
        foreach (var slot in Slots)
            slot.Refresh(snapshot ?? PointTableSnapshot.Empty, snapshot != null);
    }

    private void OpenPointTable()
    {
        var saved = _dialogs.ShowPointTable();
        var config = IoPointTableStore.Load() ?? LoadOrDefault();
        _config = config;   // 点表可能已改，重连按钮要用新表
        BuildSlots(config);
        StartBus(config);
        if (saved)
            _dialogs.ShowMessage("点表已保存并生效。", "保存成功");
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        lock (_busLock)
        {
            _bus?.Dispose();
            _bus = null;
        }
    }
}
