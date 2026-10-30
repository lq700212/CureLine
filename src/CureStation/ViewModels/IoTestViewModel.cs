// =========================================================================
// IoTestViewModel：IO 耦合器通讯测试窗编排（点表驱动，项目通用）
//
// 干什么：现场接线验证——按点表把全部 DI/DO 通道铺成仓位卡网格（与主窗
//   仓位布局同源；多机台=双架及以上点表时顶部下拉框选"当前测哪台固化架"，
//   一次只显示一台机台的 30 个库位——每行固定 10 库位，避免 60 卡挤一行
//   看不清），DI 只读指示灯（快照轮询刷新）、DO 点击灯钮（写单点，
//   真机走总线 RMW 不误伤同寄存器其它通道）；外加 读取状态/全部关闭/
//   一键遍历（500ms 跑马灯逐路点亮，快速验通断）。
// 为什么"通用"：通道清单完全来自 config/pointtable.json（本就界面可配、
//   校验齐全）——换项目/换耦合器/改通道只改点表，本窗零代码；不写死
//   AgingTestSystem 那种 72 路固定寄存器表。
// 连接共享（对齐 AgingTestSystem V1.23）：本窗不自建 Modbus 连接，复用
//   主窗已启动的 IPointTableBus（与状态机/轮询线程同源连接，写走总线
//   内部锁串行化，与轮询并发安全）。
// 线程纪律（铁律 2）：WritePoint 内部含 TCP IO——一律 Task.Run 后台执行，
//   结果回 UI 线程（UiInvoke）更新灯与日志；快照读（LatestSnapshot）是
//   原子引用读，DispatcherTimer 在 UI 线程直接拉。Bus.Error/
//   ConnectionChanged 后台线程触发——订阅处立即封送；窗口关闭 Detach() 退订。
// 怎么改：新增通道显示形态动 IoTestPointVm；遍历节奏改 SweepIntervalMs。
// =========================================================================

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CureLine.Core.Comms;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>
/// 单通道灯钮（DI=只读指示 / DO=可点击写点）。Exists=false=该仓位没绑这个
/// 方向的点（灰占位）。IsOn 三态：true 亮 / false 灭 / null 未读到（断连或
/// 还没轮到——灰闪文案由界面按 HasValue 分色）。
/// </summary>
public sealed class IoTestPointVm : ViewModelBase
{
    private bool? _isOn;
    private bool _busy;   // 写入中防抖（DO 专用；DI 恒 false）

    public IoTestPointVm(IoPointKey key, string roleText, string functionName,
        string deviceTag = "")
    {
        Key = key;
        RoleText = roleText;
        FunctionName = functionName;
        DeviceTag = deviceTag;
        Exists = true;
    }

    /// <summary>空占位（仓位卡布局保持齐整，没绑的方向显示灰格）。</summary>
    private IoTestPointVm()
    {
        RoleText = "未绑定";
        FunctionName = "";
        DeviceTag = "";
    }

    public static IoTestPointVm Placeholder() => new();

    public IoPointKey Key { get; }
    /// <summary>角色中文（点表角色→文案，IoPointRoleInfo 一处收敛）。</summary>
    public string RoleText { get; }
    /// <summary>IO 表功能名（接线员对得上线的文本；空=只显示角色名）。</summary>
    public string FunctionName { get; }
    /// <summary>所属耦合器（"设备名（IP）"，V0.0.41 多台部署排障定位用；空=单台旧表）。</summary>
    public string DeviceTag { get; }
    /// <summary>是否真绑定了点（false=占位格，不响应点击）。</summary>
    public bool Exists { get; }
    public bool IsInput => Key.IsInput;
    public bool IsOutput => !Key.IsInput;

    /// <summary>三态状态：null=未读到（断连/未轮询到）。</summary>
    public bool? IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }

    /// <summary>写入中（防抖：上一次 WritePoint 没回来再点会被忽略并提示）。
    /// set 公开给测试编排层（IoTestViewModel 写入链路收放），XAML 只读绑定。</summary>
    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    /// <summary>
    /// 悬停全文：方向首标（DI 输入/DO 输出）+ 角色 + 状态 + 功能名 + 所属耦合器——
    /// 与顶栏"小圆=DI/大圆=DO"说明呼应，悬停就能确认这是输入还是输出通道；
    /// 多台部署时一眼看出这路点挂在哪台耦合器（V0.0.41）。
    /// </summary>
    public string Tip
    {
        get
        {
            string state = IsOn switch
            {
                true => "当前 ON",
                false => "当前 OFF",
                null => "未读到（断连或未轮询）",
            };
            string fn = string.IsNullOrWhiteSpace(FunctionName) ? "" : $"\n{FunctionName}";
            string dev = string.IsNullOrWhiteSpace(DeviceTag) ? "" : $"\n设备：{DeviceTag}";
            return $"{(IsInput ? "DI 输入" : "DO 输出")}·{RoleText}\n{state}{fn}{dev}";
        }
    }
}

/// <summary>仓位卡（与主窗仓位一一对应）：卡内按点表实际绑定动态列 DI/DO 通道。</summary>
public sealed class IoTestSlotVm
{
    public IoTestSlotVm(int rack, int level)
    {
        Rack = rack;
        Level = level;
        // 卡标题=库位编码-层名（主窗同款口径：EncodeInRack 按架内站号换算，每台架
        // 各自一套 A01~J12——切到 2 号机台时第一张卡仍是 A01，与现场库位标签对齐）
        Title = $"{CureLine.Core.Curing.SlotLocationCode.EncodeInRack(rack, level)}-{LevelName(level)}";
    }

    public int Rack { get; }
    public int Level { get; }
    public string Title { get; }
    public ObservableCollection<IoTestPointVm> Inputs { get; } = new();
    public ObservableCollection<IoTestPointVm> Outputs { get; } = new();

    private static string LevelName(int level) => level switch
    {
        1 => "上层", 2 => "中层", _ => "下层",
    };
}

public sealed class IoTestViewModel : ViewModelBase
{
    /// <summary>跑马灯每拍间隔（对齐 AgingTestSystem V1.22 口径 500ms/路）。</summary>
    private const int SweepIntervalMs = 500;

    /// <summary>日志上限（防长开刷内存；超限丢最老）。</summary>
    private const int MaxLogLines = 200;

    private readonly IPointTableBus _bus;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<IoPointKey, IoTestPointVm> _points = new();
    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;

    /// <summary>
    /// 当前机台的仓位卡（固定 30 张：每行 10 库位 × LevelCount 层）——多机台时
    /// 随顶部下拉框切换重建（卡是 <see cref="_allSlots"/> 同批实例，切换零重建）；
    /// 单机台场景即全部卡，与旧版等价。
    /// </summary>
    public ObservableCollection<IoTestSlotVm> Slots { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // ---------- 多机台选择（双架及以上一次只看一台，界面固定 30 库位） ----------

    /// <summary>
    /// 全部仓位卡（按全局架号铺满，与主窗平铺集合同口径）——先建完全量再按
    /// 机台段摘 30 张进 Slots，切换只是换引用，不重新解析点表/建卡。
    /// </summary>
    private readonly Dictionary<(int Rack, int Level), IoTestSlotVm> _allSlots = new();

    /// <summary>有绑定点的机台段序号（全局架号每 10 站一台架：1~10=1号、11~20=2号…）。</summary>
    private readonly List<int> _rackGroups = new();

    /// <summary>机台下拉选项（"1号固化架"/"2号固化架"…）——只列有绑定点的机台段，
    /// rackStart=11 的单机台点表不会冒出空荡荡的"1号固化架"选项。</summary>
    public ObservableCollection<string> Machines { get; } = new();

    /// <summary>
    /// 当前选中的机台（0 起对应 <see cref="Machines"/> 下标；XAML ComboBox 的
    /// SelectedIndex 绑定）。变化即重建显示卡。
    /// </summary>
    private int _selectedMachineOrdinal;
    public int SelectedMachineOrdinal
    {
        get => _selectedMachineOrdinal;
        set
        {
            if (Set(ref _selectedMachineOrdinal, value))
                RebuildVisibleSlots();
        }
    }

    /// <summary>单机台（仅 1 台）隐藏下拉框——视觉与旧版完全一致（同主窗"组数>1 才显标题条"）。</summary>
    public bool IsMachinePickerVisible => Machines.Count > 1;

    /// <summary>显示列数：每行固定 10 库位（机台边界=行边界，与主窗分组列数同源）。</summary>
    public int Columns => CureLine.Core.Curing.SlotLocationCode.RacksPerRow;

    /// <summary>预览网格尺寸（与主窗仓位网格同源：点表最大架/层号，垫底 10×3）。</summary>
    public int RackCount { get; }
    public int LevelCount { get; }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    private string _connectionText = "未启动";
    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    private bool _sweeping;
    public bool Sweeping
    {
        get => _sweeping;
        private set => Set(ref _sweeping, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand AllOffCommand { get; }
    public RelayCommand SweepCommand { get; }
    public RelayCommand<IoTestPointVm> TogglePointCommand { get; }

    public IoTestViewModel(IPointTableBus bus, PointTableConfig config)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        ArgumentNullException.ThrowIfNull(config);

        // 网格尺寸与主窗 BuildSlots 同口径（最大架/层号垫底 10×3）
        RackCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.RacksPerRow,
            config.Devices.SelectMany(d => d.Bindings).Where(b => b.Rack > 0)
                .Select(b => b.Rack).DefaultIfEmpty(1).Max());
        LevelCount = Math.Max(CureLine.Core.Curing.SlotLocationCode.Levels,
            config.Devices.SelectMany(d => d.Bindings).Where(b => b.Level > 0)
                .Select(b => b.Level).DefaultIfEmpty(1).Max());

        // 仓位卡铺格（行优先：先层后架，与主窗一致）+ 按绑定挂通道（点表怎么配就怎么显示；
        // Reserved=未用位不进测试窗）。绑定携带所属设备上下文（V0.0.41：多台部署
        // 时通道悬停能定位到"哪台耦合器"——排障不用回点表窗对行）。
        var bySlot = config.Devices
            .SelectMany(d => d.Bindings.Select(b => (Device: d, B: b)))
            .Where(x => x.B.Role != IoPointRole.Reserved && x.B.Rack > 0 && x.B.Level > 0)
            .GroupBy(x => (x.B.Rack, x.B.Level))
            .ToDictionary(g => g.Key, g => g.ToList());
        for (int level = 1; level <= LevelCount; level++)
        for (int rack = 1; rack <= RackCount; rack++)
        {
            var slot = new IoTestSlotVm(rack, level);
            if (bySlot.TryGetValue((rack, level), out var entries))
            {
                foreach (var x in entries.Where(x => x.B.IsInput).OrderBy(x => (int)x.B.Role))
                {
                    var vm = new IoTestPointVm(x.B.Key, IoPointRoleInfo.ToText(x.B.Role),
                        x.B.FunctionName, $"{x.Device.Name}（{x.Device.Ip}）");
                    slot.Inputs.Add(vm);
                    _points[x.B.Key] = vm;
                }
                foreach (var x in entries.Where(x => !x.B.IsInput).OrderBy(x => (int)x.B.Role))
                {
                    var vm = new IoTestPointVm(x.B.Key, IoPointRoleInfo.ToText(x.B.Role),
                        x.B.FunctionName, $"{x.Device.Name}（{x.Device.Ip}）");
                    slot.Outputs.Add(vm);
                    _points[x.B.Key] = vm;
                }
            }
            // 卡内补空占位保持三格齐整（输入/输出各至少 3 格，视觉对齐）
            while (slot.Inputs.Count < 3) slot.Inputs.Add(IoTestPointVm.Placeholder());
            while (slot.Outputs.Count < 3) slot.Outputs.Add(IoTestPointVm.Placeholder());
            _allSlots[(rack, level)] = slot;
        }

        // 机台下拉（多机台=双架及以上）：全局架号每 10 站一台固化架，只按"有点位
        // 绑定"的机台段出选项（rackStart=11 的单机台点表对应"2号固化架"，不会
        // 冒出空档的"1号固化架"）；一个绑定架都没有（空表）按 1 号机台兜底铺占位卡。
        _rackGroups.AddRange(bySlot.Keys
            .Select(b => CureLine.Core.Curing.SlotLocationCode.RackGroupOf(b.Rack))
            .Distinct().OrderBy(g => g));
        if (_rackGroups.Count == 0)
            _rackGroups.Add(1);
        foreach (int g in _rackGroups)
            Machines.Add($"{g}号固化架");
        RebuildVisibleSlots(first: true);   // 按初始选中（第一台机台上屏，随后随下拉切换）

        RefreshCommand = new RelayCommand(PollOnce);
        AllOffCommand = new RelayCommand(AllOff);
        SweepCommand = new RelayCommand(ToggleSweep);
        TogglePointCommand = new RelayCommand<IoTestPointVm>(TogglePoint);

        // 共享总线的连接态/错误桥接（后台线程触发——立即封送；Detach 时退订）
        _bus.Error += OnBusError;
        _bus.ConnectionChanged += OnBusConnectionChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500), // 与总线轮询节奏对齐，纯快照读不打扰
        };
        _timer.Tick += (_, _) => PollOnce();
        _timer.Start();

        IsConnected = _bus.IsConnected;
        ConnectionText = _bus.ConnectionSummary;
        PollOnce();
        AddLog("通讯测试窗打开（共享主窗总线连接，不另建 TCP）。");
    }

    /// <summary>窗口关闭时调用：停计时器/停遍历/退订总线事件（防悬挂订阅）。</summary>
    public void Detach()
    {
        _timer.Stop();
        StopSweep();
        _bus.Error -= OnBusError;
        _bus.ConnectionChanged -= OnBusConnectionChanged;
    }

    /// <summary>
    /// 按选中机台重建当前显示卡（固定 30 张：每行 10 库位整行铺，先层后架行优先——
    /// 与 UniformGrid RacksPerRow×LevelCount 的铺格顺序同契）。卡全量已建
    /// （_allSlots），切换只是把一个机台段（每 10 个全局架号一台）的卡引用装回；
    /// 段内缺架（点表自由配）时网格右下留空，属"点表没配"的责任区非显示缺陷。
    /// </summary>
    /// <param name="first">开窗初始填充：不记切换日志（开窗已有一句开场白）。</param>
    private void RebuildVisibleSlots(bool first = false)
    {
        Slots.Clear();
        int ordinal = Math.Max(0, Math.Min(_selectedMachineOrdinal, _rackGroups.Count - 1));
        int group = _rackGroups[ordinal];
        int start = (group - 1) * CureLine.Core.Curing.SlotLocationCode.RacksPerRow + 1;
        int end = group * CureLine.Core.Curing.SlotLocationCode.RacksPerRow;
        for (int level = 1; level <= LevelCount; level++)
        for (int rack = start; rack <= end; rack++)
            if (_allSlots.TryGetValue((rack, level), out var slot))
                Slots.Add(slot);
        if (!first)
            AddLog($"[机台] 切换到 {Machines[ordinal]}（全局架 {start}~{end}，每行 10 库位）。");
    }

    private void OnBusError(object? sender, string e) =>
        UiInvoke(() => AddLog($"[总线] {e}"));

    private void OnBusConnectionChanged(object? sender, bool connected) =>
        UiInvoke(() =>
        {
            IsConnected = connected;
            ConnectionText = _bus.ConnectionSummary;
            AddLog(connected ? "[总线] 已连接" : "[总线] 有设备掉线（后台自动重连）");
        });

    /// <summary>
    /// 拉一次快照刷新全部通道灯（纯内存读：LatestSnapshot 原子换引用，
    /// UI 线程可直调——真正网络 IO 在总线自己的轮询线程）。
    /// </summary>
    private void PollOnce()
    {
        var snapshot = _bus.LatestSnapshot;
        foreach (var (key, vm) in _points)
            vm.IsOn = snapshot.Get(key);
        IsConnected = _bus.IsConnected;
        ConnectionText = _bus.ConnectionSummary;
    }

    /// <summary>点 DO 灯钮（View 经参数转交）。写入中防抖；失败弹回+日志（不弹框）。</summary>
    public void TogglePoint(IoTestPointVm? point)
    {
        if (point == null || !point.Exists || point.IsInput)
            return;
        if (point.Busy)
        {
            AddLog($"[{point.RoleText}] 上一次写入还在执行，忽略本次点击。");
            return;
        }
        bool target = !(point.IsOn ?? false);
        point.Busy = true;
        var key = point.Key;
        string roleText = point.RoleText;
        Task.Run(() =>
        {
            bool ok = _bus.WritePoint(key, target);   // 总线内部锁串行化，与轮询并发安全
            UiInvoke(() =>
            {
                point.Busy = false;
                if (ok)
                {
                    point.IsOn = target;
                    AddLog($"[{roleText}] 写 {(target ? "ON" : "OFF")} 成功。");
                }
                else
                {
                    AddLog($"[{roleText}] 写 {(target ? "ON" : "OFF")} 失败（未连接或点未绑定），灯弹回。");
                    point.IsOn = _bus.LatestSnapshot.Get(key);   // 按真实读数回显，不误导
                }
            });
        });
    }

    /// <summary>全部 DO 关闭（接线测完复位；逐路写并汇总结果）。</summary>
    private void AllOff()
    {
        StopSweep();
        var dos = _points.Values.Where(p => p.IsOutput).ToList();
        if (dos.Count == 0)
        {
            AddLog("全部关闭：点表里没有 DO 通道。");
            return;
        }
        AddLog($"全部关闭：开始写 {dos.Count} 路 DO OFF…");
        Task.Run(() =>
        {
            int ok = 0;
            foreach (var p in dos)
            {
                if (_bus.WritePoint(p.Key, false))
                    ok++;
            }
            UiInvoke(() =>
            {
                AddLog($"全部关闭完成：成功 {ok}/{dos.Count}。");
                PollOnce();
            });
        });
    }

    /// <summary>一键遍历开关：启动跑马灯（逐路点亮验接线）/ 停止并全部关闭。</summary>
    private void ToggleSweep()
    {
        if (Sweeping)
        {
            StopSweep();
            AddLog("遍历已停止。");
            return;
        }
        var dos = _points.Values.Where(p => p.IsOutput).ToList();
        if (dos.Count == 0)
        {
            AddLog("一键遍历：点表里没有 DO 通道，无从遍历。");
            return;
        }
        Sweeping = true;
        _sweepCts = new CancellationTokenSource();
        var token = _sweepCts.Token;
        AddLog($"一键遍历开始：{dos.Count} 路 DO，{SweepIntervalMs}ms/路跑马灯（再次点停止）。");
        _sweepTask = Task.Run(async () =>
        {
            int index = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var current = dos[index % dos.Count];
                    if (index % dos.Count == 0 && index > 0)
                        AddLog($"[遍历] 第 {index / dos.Count} 轮完成。");
                    // 只动本拍两路（清上一拍、点本拍），同寄存器其它位由总线 RMW 保位
                    _bus.WritePoint(dos[(index - 1 + dos.Count) % dos.Count].Key, false);
                    bool ok = _bus.WritePoint(current.Key, true);
                    string name = current.RoleText;
                    UiInvoke(() => AddLog(ok
                        ? $"[遍历] 点亮 {name}"
                        : $"[遍历] 点亮 {name} 失败（未连接？）"));
                    index++;
                    await Task.Delay(SweepIntervalMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止路径
            }
        });
    }

    /// <summary>停止跑马灯（幂等）：取消任务后把全部 DO 关掉（防灯留在半亮状态）。</summary>
    private void StopSweep()
    {
        if (!Sweeping)
            return;
        Sweeping = false;
        _sweepCts?.Cancel();
        var dos = _points.Values.Where(p => p.IsOutput).ToList();
        var task = _sweepTask;
        Task.Run(async () =>
        {
            if (task != null)
            {
                try { await task; } catch { /* 取消异常已在任务内处理 */ }
            }
            foreach (var p in dos)
                _bus.WritePoint(p.Key, false);
            UiInvoke(() =>
            {
                AddLog("[遍历] 已全部关闭。");
                PollOnce();
            });
        });
        _sweepCts?.Dispose(); // 可空传播：Sweeping 门保证非空，此处仅为可空流分析兜底
        _sweepCts = null;
    }

    private void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }
}
