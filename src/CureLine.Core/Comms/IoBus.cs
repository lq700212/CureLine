// =========================================================================
// IPointTableBus：IO 调度总线抽象（固化程控与点表配置窗共同的通讯出口）
//
// 干什么：把"多台 IO 耦合器 × 多扩展块 × 点位绑定"收敛成一条总线——
//   业务/界面只看语义键（角色+仓位）取点/写点，不看 Modbus 地址。
// 为什么抽象：现场联调一次成功前必须 Mock 联调；换底座（品牌/协议）
//   只换实现，状态机与界面零改（对齐 kaleidoscope 分层宗旨）。
//
// 实现：
//   ModbusPointTableBus —— 真机（每台耦合器一个 kaleidoscope ModbusTcpIoController，
//     块状布局逐块 FC04/FC03 读；DO 写走"读-改-写"防误伤同寄存器其它通道；断连
//     5s 节流重连——对齐铁律"读写失败即断连标记+节流重连"）。
//   MockPointTableBus —— 演示态（托盘/光栅顺序流转，无硬件跑通全流程）。
//
// 线程契约（与现有 PLC 客户端一致）：
//   Start/Stop/WritePoint 可在任意线程调；快照 LatestSnapshot 原子换引用读；
//   PointChanged/ConnectionChanged/Error 全部后台线程触发，UI 订阅方必须封送。
// =========================================================================

using System.Collections.Concurrent;
using Kaleidoscope.Models;
using Kaleidoscope.Services;

namespace CureLine.Core.Comms;

public interface IPointTableBus : IDisposable
{
    /// <summary>启动总线（校验失败抛_ARGUMENT 异常由调用方先 Validate；建连失败不算——后台自愈重连）。</summary>
    void Start(PointTableConfig config);

    /// <summary>停止并释放全部连接（幂等）。</summary>
    void Stop();

    /// <summary>全部设备是否都已连接（Mock 恒 true）。</summary>
    bool IsConnected { get; }

    /// <summary>连接摘要文本（如"1/1 台已连"），界面状态卡直接显示。</summary>
    string ConnectionSummary { get; }

    /// <summary>最新快照（不可变引用，读侧原子取——轮询线程整份换成新 Dictionary）。</summary>
    PointTableSnapshot LatestSnapshot { get; }

    /// <summary>点变化边沿（后台线程）。</summary>
    event EventHandler<PointChangedEventArgs>? PointChanged;

    /// <summary>总连接态变化边沿（true=已连，false=有设备掉线；后台线程）。</summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>通讯错误文本（后台线程，只记边沿不刷屏）。</summary>
    event EventHandler<string>? Error;

    /// <summary>
    /// 写输出点（按语义键定位）。
    /// 真机：读-改-写单个寄存器位，不误伤同寄存器其它 DO 通道；
    /// Mock：直接入内存并在后续读取可见。
    /// 返回 false=点未绑定/通讯失败（不抛，调用方记日志/亮黄牌）。
    /// </summary>
    bool WritePoint(IoPointKey key, bool state);
}

/// <summary>
/// 真机 Modbus TCP 总线。轮询策略（单线程串行扫全部设备——耦合器单连接，
/// 并发多线程反而抢同一 TCP，串行简单且峰值带宽可控）：
///   每周期每台：逐块 FC04 读 DI 寄存器 1 个 + FC03 读 DO 寄存器 1 个；
///   10 块设备 = 20 帧/周期；周期取全场最小 PollIntervalMs（缺省 250ms）。
/// 重连策略：轮询前发现未连接 → 5 秒节流重连（ReadInputRegisters 等内部
///   断连标记由 kaleidoscope 静态保证；重连失败只 Error 一次边沿）。
/// </summary>
public sealed class ModbusPointTableBus : IPointTableBus
{
    private readonly object _syncRoot = new();
    private readonly List<CouplerRuntime> _devices = new();
    private readonly ConcurrentDictionary<IoPointKey, CouplerPoint> _writeIndex = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly List<string> _lastErrors = new();
    // 停机静默阀：Stop 后残留线程（连接超时兜圈 >StopCore 的等待上限时）仍可能
    // 冒 Error——订阅方是 UI/VM，事件晚到会把新总线的连接文本覆盖成旧总线报错
    // （热切换偶发，实测踩中）。置位后一切出站事件拦截，对象内部状态照常收敛。
    private volatile bool _stopped;

    // 语义键 →（设备、块、位、寄存器地址）；启动时由绑定构建（校验已保证唯一）。
    private sealed record CouplerPoint(IoCouplerDevice Device, int Block, int Bit, ushort DoAddress);

    // 一台耦合器的运行时（kaleidoscope 控制器 + 断连时间戳用于重连节流）。
    private sealed class CouplerRuntime
    {
        public required IoCouplerDevice Device { get; init; }
        public required ModbusTcpIoController Controller { get; init; }
        public DateTime LastReconnectAttempt = DateTime.MinValue;
        public bool Connected;
    }

    // ---------------- IPointTableBus 契约 ----------------

    public bool IsConnected { get; private set; }
    public string ConnectionSummary { get; private set; } = "未启动";

    private volatile PointTableSnapshot _snapshot = PointTableSnapshot.Empty;
    public PointTableSnapshot LatestSnapshot => _snapshot;

    public event EventHandler<PointChangedEventArgs>? PointChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Error;

    public void Start(PointTableConfig config)
    {
        lock (_syncRoot)
        {
            if (_pollTask != null)
                StopCore(); // 重复 Start = 先停后启（换配置热刷场景）
            _stopped = false;
            if (config.Devices.Count == 0)
            {
                // 调用方必须先过 PointTableValidator（空表校验已拦）——这里兜底禁止静默起空总线
                throw new ArgumentException("至少需要一台 IO 耦合器（Start 前请先校验点表）。");
            }
            foreach (var device in config.Devices)
            {
                var controller = new ModbusTcpIoController();
                controller.OnError += (_, msg) => RaiseError($"{device.Name}:{msg}");
                _devices.Add(new CouplerRuntime { Device = device, Controller = controller });
            }

            // 写索引：语义键 → 物理点（Reserved 不入；同键重复已由校验拦截）
            foreach (var device in config.Devices)
            foreach (var b in device.Bindings)
                if (b.Role != IoPointRole.Reserved && !b.IsInput)
                    _writeIndex[b.Key] = new CouplerPoint(
                        device, b.Block, b.Bit, device.DoAddressOf(b.Block));

            // 周期 = 全场最频繁轮询值
            int pollMs = config.Devices.Count > 0
                ? config.Devices.Min(d => d.PollIntervalMs) : 250;

            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoop(pollMs), _cts.Token);
        }
    }

    public void Stop()
    {
        lock (_syncRoot) { StopCore(); }
    }

    public bool WritePoint(IoPointKey key, bool state)
    {
        if (!_writeIndex.TryGetValue(key, out var pt))
            return false; // 没绑定（Reserved/缺配）——静默 false，调用方日志决定

        var rt = _devices.FirstOrDefault(d => d.Device == pt.Device);
        if (rt == null || !rt.Connected)
            return false;

        // 读-改-写（kaleidoscope 红线：写单个位必须 RMW，否则覆盖同寄存器其它通道）
        var regs = rt.Controller.ReadHoldingRegisters(pt.DoAddress, 1);
        if (regs == null || regs.Length != 1)
            return false;
        ushort value = regs[0];
        value = state
            ? (ushort)(value | (1 << pt.Bit))
            : (ushort)(value & ~(1 << pt.Bit));
        return rt.Controller.WriteSingleRegister(pt.DoAddress, value);
    }

    public void Dispose() => Stop();

    // ---------------- 内部 ----------------

    // 后台轮询主循环：重连 → 扫全部设备 → 建快照 → 发边沿。异常全部落 Error 边沿。
    private async void PollLoop(int pollMs)
    {
        // 为什么 catch OCE：Task.Delay 会被 Stop 的 Cancel 打断，async void 里
        // 未处理 OCE 会整进程崩——取消是常态路径必须静默吃掉。
        try
        {
            var token = _cts!.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception ex)
                {
                    // 轮询是总线的心跳，这里只兜底防线程死——具体错误在 PollOnce 内已发 Error。
                    RaiseError($"轮询异常兜底：{ex.Message}");
                }
                await Task.Delay(pollMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径，静默退出
        }
        catch (Exception ex)
        {
            RaiseError($"轮询线程异常退出：{ex.Message}");
        }
    }

    private void PollOnce()
    {
        var states = new Dictionary<IoPointKey, bool>();
        int connected = 0;

        lock (_syncRoot)
        {
            foreach (var rt in _devices)
            {
                if (TryEnsureConnected(rt) && ScanPoints(rt, states))
                    connected++;
            }

            bool nowConnected = connected == _devices.Count;
            IsConnected = nowConnected;
            ConnectionSummary = _devices.Count == 0
                ? "未配置设备"
                : $"{connected}/{_devices.Count} 台已连";
            _snapshot = new PointTableSnapshot(states, DateTimeOffset.Now);

            if (nowConnected != _lastConnectedState)
            {
                _lastConnectedState = nowConnected;
                ConnectionChanged?.Invoke(this, nowConnected);
            }
        }
    }

    private bool _lastConnectedState;

    /// <summary>确保单台已连（未连则 5 秒节流重连）。连接失败不 Error 刷屏——由 kaleidoscope 边沿上报。</summary>
    private bool TryEnsureConnected(CouplerRuntime rt)
    {
        if (rt.Connected)
            return true;

        // 铁律 3：断连后按 5s 节流重建（首次马上试，之后 5 秒一次）
        if ((DateTime.Now - rt.LastReconnectAttempt) < TimeSpan.FromSeconds(5))
            return false;
        rt.LastReconnectAttempt = DateTime.Now;

        rt.Connected = rt.Controller.Connect(ToKaleidoConfig(rt.Device));
        if (rt.Connected)
            RaiseError($"{rt.Device.Name} 已连上（{rt.Device.Ip}）");
        return rt.Connected;
    }

    /// <summary>扫一台的 DI/DO：逐块各 1 寄存器；全部帧成功才算 connected（半失败本轮丢弃）。</summary>
    private bool ScanPoints(CouplerRuntime rt, Dictionary<IoPointKey, bool> states)
    {
        var device = rt.Device;
        var inputRoles = device.Bindings.Where(b => b.IsInput).ToList();
        var outputRoles = device.Bindings.Where(b => !b.IsInput).ToList();

        foreach (var b in inputRoles)
        {
            var regs = rt.Controller.ReadInputRegisters(device.DiAddressOf(b.Block), 1);
            if (regs == null || regs.Length != 1)
                return false;
            StoreBit(states, b, (regs[0] & (1 << b.Bit)) != 0, device.InvertInputs);
        }

        foreach (var b in outputRoles)
        {
            var regs = rt.Controller.ReadHoldingRegisters(device.DoAddressOf(b.Block), 1);
            if (regs == null || regs.Length != 1)
                return false;
            StoreBit(states, b, (regs[0] & (1 << b.Bit)) != 0, device.InvertOutputs);
        }

        return true;
    }

    // 快照与边沿：状态与上次不同才发事件（同一拍只发变了的那一条）。
    private void StoreBit(Dictionary<IoPointKey, bool> states, IoPointBinding b,
        bool raw, bool invert)
    {
        bool value = invert ? !raw : raw;
        var key = b.Key;
        bool changed = !_snapshot.States.TryGetValue(key, out var old) || old != value;
        states[key] = value;
        if (changed)
            PointChanged?.Invoke(this, new PointChangedEventArgs(key, value));
    }

    private static IoConfig ToKaleidoConfig(IoCouplerDevice d) => new()
    {
        PlcAddress = d.Ip,
        PlcPort = d.Port,
        IoUnitId = d.UnitId,
        IoInputRegisterStartAddress = d.DiStartAddress,
        IoOutputRegisterStartAddress = d.DoStartAddress,
        TotalInputs = d.BlockCount * d.PointsPerBlock,
        TotalOutputs = d.BlockCount * d.PointsPerBlock,
        InvertInputs = d.InvertInputs,
        InvertOutputs = d.InvertOutputs,
        TcpSendTimeoutMs = d.TimeoutMs,
        TcpReceiveTimeoutMs = d.TimeoutMs,
    };

    private void RaiseError(string msg)
    {
        if (_stopped)
            return; // 停机静默阀（StopCore 置位）：残留线程的报错不再出门
        lock (_lastErrors)
        {
            // 只记边沿：同文本 10 秒内不重复发
            if (_lastErrors.TakeLast(10).Contains(msg))
                return;
            _lastErrors.Add(msg);
        }
        Error?.Invoke(this, msg);
    }

    private void StopCore()
    {
        _stopped = true; // 先竖静默阀：此后残留线程再冒的 Error 一律拦下
        _cts?.Cancel();
        // 等轮询线程退出再放资源：Cancel 后立即 Dispose CTS 会与在飞的
        // Task.Delay(token) 竞态爆 ObjectDisposedException——async void 外层
        // catch 会冒"轮询线程异常退出"脏 Error 事件，旧总线 Dispose 后仍触发，
        // 把新总线的顶栏连接文本覆盖成错误文案（热切换/重连偶发，实测踩中）。
        try { _pollTask?.Wait(2000); } catch { /* 任务内异常已在 PollLoop 兜底 */ }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
        _lastConnectedState = false;
        foreach (var rt in _devices)
        {
            rt.Controller.Disconnect();
            rt.Controller.Dispose();
        }
        _devices.Clear();
        _writeIndex.Clear();
        IsConnected = false;
        ConnectionSummary = "已停止";
        _snapshot = PointTableSnapshot.Empty;
    }
}

/// <summary>
/// Mock 总线（Amsterdam 演示态，无硬件跑通全流程）：
///   托盘/光栅按仓位顺序流转：托盘到 → 左右光栅亮 → 保持 → 下一仓位。
///   灯（DO）由状态机写入，直接在快照可见——演示"写生效"。
///   每 30 秒随机"断连 3 秒"一次，演示重连与黄牌（可关）。
/// </summary>
public sealed class MockPointTableBus : IPointTableBus
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cts;
    private Task? _tickTask;
    private int _boundPoints; // 绑定语义点数（演示推进用）
    private Dictionary<IoPointKey, IoPointBinding> _bindings = new();
    // 停机静默阀：同 Modbus 总线（Stop 后残留线程的 Error 不再出门）。
    private volatile bool _stopped;

    private volatile PointTableSnapshot _snapshot = PointTableSnapshot.Empty;
    private readonly Dictionary<string, bool> _manualLamps = new();

    public bool IsConnected { get; private set; }
    public string ConnectionSummary { get; private set; } = "未启动";

    private int _mockPollMs;
    private bool _mockDrop;

    public PointTableSnapshot LatestSnapshot => _snapshot;

    public event EventHandler<PointChangedEventArgs>? PointChanged;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? Error;

    // 演示进度：仓位游标 + 各仓位阶段（0=空闲 1=托盘到 2=光栅亮 3=放行完成）
    private readonly Dictionary<int, int> _stageByRack = new();

    public void Start(PointTableConfig config)
    {
        lock (_syncRoot)
        {
            if (_tickTask != null)
                StopCore();
            _stopped = false;
            IsConnected = true;
            ConnectionSummary = $"演示模式（{config.Devices.Count} 台配置）";
            _boundPoints = config.Devices.SelectMany(d => d.Bindings)
                .Count(b => b.Role != IoPointRole.Reserved);
            _bindings = config.Devices.SelectMany(d => d.Bindings)
                .Where(b => b.Role != IoPointRole.Reserved)
                .ToDictionary(b => b.Key, b => b);
            _mockPollMs = config.Devices.Count > 0
                ? Math.Max(80, config.Devices.Min(d => d.PollIntervalMs)) : 250;
            _mockDrop = config.Devices.Count > 0; // 有配置才演示掉线

            // 首拍前预建基线快照（全 false，不发事件）——之后差分只发真变化，
            // 否则第一次写入会被"初次全点事件"淹掉（订阅方拿到的边沿二义）。
            var baseline = new Dictionary<IoPointKey, bool>(_bindings.Count);
            foreach (var key in _bindings.Keys)
                baseline[key] = false;
            _snapshot = new PointTableSnapshot(baseline, DateTimeOffset.Now);

            _cts = new CancellationTokenSource();
            _tickTask = Task.Run(() => TickLoop(_cts.Token));
            RaiseError("演示模式启动（Mock 通讯，无真实硬件）");
        }
    }

    public void Stop()
    {
        lock (_syncRoot) { StopCore(); }
    }

    // 停止：掐演示线程 + 快照回空（幂等；已写值清除避免复启残留旧灯）
    private void StopCore()
    {
        _stopped = true; // 先竖静默阀（同 Modbus 总线）
        _cts?.Cancel();
        // 同 Modbus 总线 StopCore：先等演示线程退出再 Dispose CTS（防在飞
        // Task.Delay(token) 摸到已释放 CTS 冒脏 Error 事件）。
        try { _tickTask?.Wait(2000); } catch { /* 任务内异常已在 TickLoop 兜底 */ }
        _cts?.Dispose();
        _cts = null;
        _tickTask = null;
        IsConnected = false;
        ConnectionSummary = "已停止";
        _stageByRack.Clear();
        _manualLamps.Clear();
        _bindings = new();
        _snapshot = PointTableSnapshot.Empty;
    }

    public void Dispose() => Stop();

    public bool WritePoint(IoPointKey key, bool state)
    {
        lock (_syncRoot)
        {
            if (!_bindings.ContainsKey(key))
                return false;
            _manualLamps[key.ToString()] = state;
            PushManual(key, state);
            return true;
        }
    }

    // 演示推进节拍
    private async void TickLoop(CancellationToken token)
    {
        // 同 PollLoop：取消是常态路径，OCE 必须静默吃掉防 async void 崩进程。
        try
        {
            int tick = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    AdvanceDemo(tick++);
                }
                catch (Exception ex)
                {
                    RaiseError($"演示异常兜底：{ex.Message}");
                }
                await Task.Delay(_mockPollMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            RaiseError($"演示线程异常退出：{ex.Message}");
        }
    }

    private void AdvanceDemo(int tick)
    {
        lock (_syncRoot)
        {
            // 约每 40 拍走一个仓位：托盘到 → 光栅亮 → 阶段 3 放行
            if (tick % 40 == 0)
            {
                int rackMax = _bindings.Keys.Select(k => k.Rack)
                    .DefaultIfEmpty(1).Max();
                int rack = (tick / 40) % Math.Max(1, rackMax) + 1;
                _stageByRack[rack] = (_stageByRack.TryGetValue(rack, out var s) ? s : 0) + 1;
            }
            else if (tick % 10 == 0)
            {
                // 阶段推进到"粗格亮"之后回落，演示边沿
                foreach (var rack in _stageByRack.Keys.ToList())
                    if (++_stageByRack[rack] > 3)
                        _stageByRack[rack] = 0;
            }

            // 每 120 拍之后才演示掉线（前 ~30 秒连稳，给现场一个"开得起来"的体感；
            // 掉 12 拍后恢复——180 秒周期）
            bool dropping = _mockDrop && tick >= 120 && tick % 480 < 12;
            bool nowConnected = !dropping;
            if (IsConnected != nowConnected)
            {
                IsConnected = nowConnected;
                ConnectionChanged?.Invoke(this, nowConnected);
                RaiseError(nowConnected ? "演示：已重新连上" : "演示：模拟掉线（3 秒后恢复）");
            }
            ConnectionSummary = IsConnected
                ? "演示模式（已连）" : "演示模式（示意掉线）";

            RebuildSnapshot();
        }
    }

    private void PushManual(IoPointKey key, bool state)
    {
        PointChanged?.Invoke(this, new PointChangedEventArgs(key, state));
        RebuildSnapshot();
    }

    // 快照 = 全部绑定点的演示值（灯读人工写值，光栅/托盘按阶段）；只发变化边沿
    private void RebuildSnapshot()
    {
        var states = new Dictionary<IoPointKey, bool>(_bindings.Count);
        foreach (var (key, b) in _bindings)
        {
            bool value;
            if (!key.IsInput)
                value = _manualLamps.TryGetValue(key.ToString(), out var manual) && manual;
            else
            {
                int stage = _stageByRack.TryGetValue(b.Rack, out var s) ? s : 0;
                value = key.Role switch
                {
                    IoPointRole.TrayDetect => stage is >= 1 and <= 3,
                    IoPointRole.RackLightL => stage is >= 2 and <= 3,
                    IoPointRole.RackLightR => stage is >= 2 and <= 3,
                    _ => false,
                };
            }
            states[key] = value;
        }

        // 边沿真发：对比旧快照逐点找变化（全量重发会把无关点事件灌满订阅方，干扰 UI）
        var old = _snapshot;
        foreach (var (key, value) in states)
            if (!old.States.TryGetValue(key, out var was) || was != value)
                PointChanged?.Invoke(this, new PointChangedEventArgs(key, value));

        _snapshot = new PointTableSnapshot(states, DateTimeOffset.Now);
    }

    private void RaiseError(string msg)
    {
        if (_stopped)
            return; // 停机静默阀（StopCore 置位）：残留线程的报错不再出门
        Error?.Invoke(this, msg);
    }
}
