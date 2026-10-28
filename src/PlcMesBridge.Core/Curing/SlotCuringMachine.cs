// =========================================================================
// SlotCuringMachine：仓位固化状态机（程序①程控核心）
//
// 干什么：吃 IO 总线的点变化边沿，按工艺策略驱动每个仓位（架×层）的状态
//   流转：空闲 →（托盘到位；门控开则等光栅让空）→ 计时中（黄灯）→
//   到点放行（绿灯）→（托盘离开）→ 灭灯回空闲。任何时刻托盘离开都回
//   空闲（计时中取件=计时作废并记日志）。
//
// 线程模型（铁律 2）：灯写 WritePoint 是网络 IO，绝不能进 UI 线程——
//   本类全部动作发生在后台：OnPointChanged 由宿主从总线后台事件转发进来，
//   Pump 由内部 1s 心跳 Timer（后台线程）调；测试注入虚拟时钟手动 Pump
//   （Timer 调 Pump(固定虚拟时间) 幂等无副作用，不干扰测试确定性）。
//   所有可变状态收在 _sync 锁内，三个入口（边沿/心跳/查询）并发安全。
//
// 自愈：
//   ① 灯写"期望 vs 已写"比对（_writtenLamps），写失败不记账 → 下轮 Pump
//      自动补写；稳态下零网络写（去重）。写失败本身不记日志——总线
//      Error 事件已报通讯问题，这里静默重试防刷屏（铁律 4）。
//   ② 断连恢复后宿主调 AlignFromSnapshot：托盘在架且空闲的仓位重新开
//      计时。计时状态不持久化（断电前计了多久无从知道，属报价加价点
//      登记项不脑补）——宁可重计也不放行没固化够的货。
//   ③ 策略热切换：每次托盘到位现取激活策略；进行中的仓位按开计时那一刻
//      锁定的时长走完（改参数不影响在制品，工艺上更安全）。
//
// 日志（铁律 4）：只记边沿——开计时/放行/取件/重启对齐，不刷屏。
//
// 怎么改：新增工艺分支（如暂停计时）在 SlotPhase 加状态 + OnPointChanged/
//   Pump 两处推进逻辑；灯色语义（黄=计时绿=放行）改动要同步
//   RackSlotViewModel 的展示口径与 CuringPolicy 注释。
// =========================================================================

using PlcMesBridge.Core.Comms;

namespace PlcMesBridge.Core.Curing;

/// <summary>仓位流程状态。Idle=空闲；WaitingGate=托盘在但等光栅让空；Curing=计时中；Done=到点放行。</summary>
public enum SlotPhase
{
    Idle,
    WaitingGate,
    Curing,
    Done,
}

/// <summary>仓位状态机快照（record 不可变，锁内构造后随便传）。</summary>
public sealed record SlotStatus(SlotPhase Phase, int TotalMinutes, int RemainMinutes)
{
    /// <summary>中文状态文本（界面卡片直接绑）。</summary>
    public string StateText => Phase switch
    {
        SlotPhase.WaitingGate => "待门控",
        SlotPhase.Curing => RemainMinutes > 0 ? $"计时中 剩{RemainMinutes}分" : "计时中",
        SlotPhase.Done => "放行",
        _ => "空闲",
    };
}

public sealed class SlotCuringMachine : IDisposable
{
    /// <summary>策略缺失时的兜底时长（策略库空/没激活；正常运行时策略自愈，几乎走不到）。</summary>
    private const int FallbackMinutes = 120;

    private readonly object _sync = new();
    private readonly Func<CuringPolicy?> _policyProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;
    private readonly Dictionary<(int Rack, int Level), SlotRuntime> _slots = new();
    /// <summary>成功写过的灯态（IoPointKey→值）：去重 + 失败补写的比对基准。</summary>
    private readonly Dictionary<IoPointKey, bool> _writtenLamps = new();
    private IPointTableBus _bus;
    private Timer? _pumpTimer;

    /// <summary>单仓位运行态（内部可变，只在 _sync 锁内动）。</summary>
    private sealed class SlotRuntime
    {
        public SlotPhase Phase;
        public DateTimeOffset? StartedAt; // Curing 起点（时钟可注入，测试虚拟推进）
        public int TotalMinutes;          // 开计时时锁定的时长（策略热切不干扰在制品）
    }

    /// <summary>
    /// 构造即起 1s 心跳（后台线程）。bus 可后续经 Attach 换（重连不丢状态）；
    /// policyProvider 每次托盘到位现取（支持策略窗热切换）；clock/log 可注入（测试）。
    /// </summary>
    public SlotCuringMachine(IPointTableBus bus, Func<CuringPolicy?> policyProvider,
        Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _bus = bus;
        _policyProvider = policyProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log ?? (_ => { });
        _pumpTimer = new Timer(_ => SafePump(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>换总线（重连/点表变更后）：流程状态保留，只换读写通道。</summary>
    public void Attach(IPointTableBus bus)
    {
        lock (_sync)
            _bus = bus;
    }

    public void Dispose()
    {
        _pumpTimer?.Dispose();
        _pumpTimer = null;
    }

    // ---------- 输入入口（宿主从总线后台事件转发，勿在 UI 线程调） ----------

    /// <summary>点变化边沿入口：只认托盘（启停流程）与光栅（门控放行）输入。</summary>
    public void OnPointChanged(PointChangedEventArgs e)
    {
        var key = e.Key;
        if (!key.IsInput)
            return;
        if (key.Role == IoPointRole.TrayDetect)
        {
            lock (_sync)
            {
                var rt = GetOrAdd(key.Rack, key.Level);
                if (e.State)
                    OnTrayArrived(rt, key.Rack, key.Level);
                else
                    OnTrayLeft(rt, key.Rack, key.Level);
            }
        }
        else if (key.Role is IoPointRole.RackLightL or IoPointRole.RackLightR && !e.State)
        {
            // 光栅让空边沿：门控等待中的仓位立即开计时（Pump 有快照兜底，双保险）
            lock (_sync)
            {
                var rt = GetOrAdd(key.Rack, key.Level);
                if (rt.Phase == SlotPhase.WaitingGate && GrillClear(key.Rack, key.Level))
                    StartCuring(rt, key.Rack, key.Level);
            }
        }
        // 光栅亮起（人手伸入）不打断进行中的计时——第一版不暂停（客户口径
        // "计时为主"），如现场要求暂停属新分支，加 SlotPhase.Paused 处理。
    }

    /// <summary>心跳泵：扫描到期仓位放行 + 灯写补写自愈 + 门控兜底推进。可测试直调（虚拟时间）。</summary>
    public void Pump(DateTimeOffset now)
    {
        lock (_sync)
        {
            foreach (var (slot, rt) in _slots)
            {
                switch (rt.Phase)
                {
                    case SlotPhase.Curing when rt.StartedAt is { } start:
                        int elapsed = (int)(now - start).TotalMinutes;
                        if (elapsed >= rt.TotalMinutes)
                        {
                            rt.Phase = SlotPhase.Done;
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, false);
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, true);
                            _log($"架{slot.Rack}层{slot.Level} 固化到点放行（绿灯，共 {rt.TotalMinutes} 分钟）");
                            PersistState(); // 状态变化落盘（开关开时）
                        }
                        else
                        {
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, true); // 补写自愈
                        }
                        break;
                    case SlotPhase.Done:
                        EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, true); // 绿灯保持到取件
                        break;
                    case SlotPhase.WaitingGate:
                        if (GrillClear(slot.Rack, slot.Level))
                            StartCuring(rt, slot.Rack, slot.Level); // 兜底：光栅让空边沿错过也能推进
                        break;
                    case SlotPhase.Idle:
                        // 灭灯补写（只对状态机动过的仓位——_slots 按事件建，未动仓位不进扫描）
                        EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, false);
                        EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, false);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 快照对齐（启动/断连恢复后宿主调）：托盘在架且空闲的仓位——
    /// 激活策略开了 PersistMinutes 且有持久化记录 → 按记录续算（断电期间时间
    /// 自然计入；已放行的直接恢复绿灯）；没记录/开关关 → 按"重新计时"处理
    /// （宁可重计也不放行没固化够的货）。内部幂等：已在 Curing/Done 的仓位不动。
    /// 门控开且光栅亮 → 进 WaitingGate（WaitingGate 不持久化——没开计时，重启重走门控）。
    /// </summary>
    public void AlignFromSnapshot()
    {
        lock (_sync)
        {
            var snap = _bus.LatestSnapshot;

            // 持久化恢复准备：开关开才读记录（开关关=零文件 IO，行为与不持久化完全一致）。
            // 记录合法性过滤：时长 ≤0 或 Curing 缺起点 → 视为脏数据丢弃走重计。
            Dictionary<(int Rack, int Level), CuringSlotRecord>? saved = null;
            if (_policyProvider() is { PersistMinutes: true })
            {
                var reasons = new List<string>();
                saved = CuringStateStore.Load(reasons)?.Slots
                    .Where(r => r.Minutes > 0 && (
                        r.Phase == nameof(SlotPhase.Done) ||
                        (r.Phase == nameof(SlotPhase.Curing) && r.StartedAt != default)))
                    .GroupBy(r => (r.Rack, r.Level))
                    .ToDictionary(g => g.Key, g => g.First());
                foreach (var r in reasons)
                    _log(r);
            }

            foreach (var (key, state) in snap.States)
            {
                if (!key.IsInput || key.Role != IoPointRole.TrayDetect || !state)
                    continue;
                var rt = GetOrAdd(key.Rack, key.Level);
                if (rt.Phase != SlotPhase.Idle)
                    continue; // 已在流程中（幂等）
                if (saved != null && saved.TryGetValue((key.Rack, key.Level), out var rec))
                {
                    RestoreFromRecord(rt, key.Rack, key.Level, rec);
                    continue;
                }
                _log($"架{key.Rack}层{key.Level} 重启对齐：托盘在架，重新开始计时");
                OnTrayArrived(rt, key.Rack, key.Level);
            }
        }
    }

    /// <summary>按持久化记录恢复仓位（Curing 续算 / Done 恢复放行态），灯随状态重写。</summary>
    private void RestoreFromRecord(SlotRuntime rt, int rack, int level, CuringSlotRecord rec)
    {
        if (rec.Phase == nameof(SlotPhase.Done))
        {
            rt.Phase = SlotPhase.Done;
            rt.StartedAt = rec.StartedAt;
            rt.TotalMinutes = rec.Minutes;
            EnsureLamp(rack, level, IoPointRole.LampYellow, false);
            EnsureLamp(rack, level, IoPointRole.LampGreen, true);
            _log($"架{rack}层{level} 重启恢复：断电前已固化到点，保持放行（绿灯，共 {rec.Minutes} 分钟）");
            return;
        }
        // Curing：按记录起点续算——断电期间流逝的时间自然计入固化时长
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = rec.StartedAt;
        rt.TotalMinutes = rec.Minutes;
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        int remain = Math.Max(0, rec.Minutes - (int)Math.Ceiling((_clock() - rec.StartedAt).TotalMinutes));
        _log($"架{rack}层{level} 重启恢复：续算断电前计时，剩 {remain} 分钟（共 {rec.Minutes} 分钟）");
    }

    /// <summary>查仓位状态（UI 500ms 刷用）。空闲/没动过回 null（UI 走灯色反推口径）。</summary>
    public SlotStatus? GetStatus(int rack, int level)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue((rack, level), out var rt) || rt.Phase == SlotPhase.Idle)
                return null;
            int remain = 0;
            if (rt.Phase == SlotPhase.Curing && rt.StartedAt is { } start)
            {
                var elapsed = _clock() - start;
                remain = Math.Max(0, rt.TotalMinutes - (int)Math.Ceiling(elapsed.TotalMinutes));
            }
            return new SlotStatus(rt.Phase, rt.TotalMinutes, remain);
        }
    }

    // ---------- 流转（全部假定已在 _sync 锁内） ----------

    private void OnTrayArrived(SlotRuntime rt, int rack, int level)
    {
        if (rt.Phase is SlotPhase.Curing or SlotPhase.Done)
            return; // 已在流程中（传感器抖动/重复边沿），不重置计时
        var policy = _policyProvider();
        int minutes = policy?.ResolveMinutes(rack, level) ?? FallbackMinutes;
        if (policy is { GrillGate: true } && !GrillClear(rack, level))
        {
            rt.Phase = SlotPhase.WaitingGate;
            rt.TotalMinutes = minutes;
            rt.StartedAt = null;
            _log($"架{rack}层{level} 托盘到位，等光栅让空后开始计时（{minutes} 分钟）");
            return;
        }
        StartCuring(rt, rack, level, minutes);
    }

    private void StartCuring(SlotRuntime rt, int rack, int level, int? minutes = null)
    {
        var policy = _policyProvider();
        rt.TotalMinutes = minutes ?? policy?.ResolveMinutes(rack, level) ?? FallbackMinutes;
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = _clock();
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false); // 防残留绿灯
        _log($"架{rack}层{level} 开始固化计时 {rt.TotalMinutes} 分钟（黄灯）");
        PersistState(); // 状态变化落盘（开关开时）
    }

    private void OnTrayLeft(SlotRuntime rt, int rack, int level)
    {
        if (rt.Phase == SlotPhase.Idle)
            return;
        string note = rt.Phase switch
        {
            SlotPhase.Curing => "计时中取件，计时作废",
            SlotPhase.Done => "取件完成",
            SlotPhase.WaitingGate => "托盘离开（未开计时）",
            _ => "托盘离开",
        };
        rt.Phase = SlotPhase.Idle;
        rt.StartedAt = null;
        rt.TotalMinutes = 0;
        EnsureLamp(rack, level, IoPointRole.LampYellow, false);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        _log($"架{rack}层{level} {note}，回空闲");
        PersistState(); // 状态变化落盘（开关开时）：取件仓位从记录中移除
    }

    // ---------- 计时状态持久化（PersistMinutes 开关，断电续算） ----------

    /// <summary>
    /// 持久化进行中仓位（每个状态变化点调用；全量重写 curingstate.json）。
    /// 开关关 = 零文件 IO，行为与"不持久化"完全一致（缺省口径：宁可重计）。
    /// WaitingGate 不落盘——没开计时，重启后对齐自然重走门控。
    /// 落盘失败只记日志：持久化是加分项不是命脉，下次状态变化再写，绝不影响程控流程。
    /// </summary>
    private void PersistState()
    {
        if (_policyProvider() is not { PersistMinutes: true })
            return;
        try
        {
            var book = new CuringStateBook();
            foreach (var (slot, rt) in _slots)
            {
                if (rt.Phase is not (SlotPhase.Curing or SlotPhase.Done))
                    continue;
                book.Slots.Add(new CuringSlotRecord
                {
                    Rack = slot.Rack,
                    Level = slot.Level,
                    Phase = rt.Phase.ToString(),
                    StartedAt = rt.StartedAt ?? _clock(),
                    Minutes = rt.TotalMinutes,
                });
            }
            CuringStateStore.Save(book);
        }
        catch (Exception ex)
        {
            _log($"计时状态落盘失败（下次状态变化自动重试）：{ex.Message}");
        }
    }

    // ---------- 灯写与辅助 ----------

    /// <summary>
    /// 写灯（去重）：与上次成功写的值相同则跳过（稳态零网络写）；
    /// 失败不记账 → 下轮 Pump 自动补写。红灯本状态机不碰（留给将来异常语义）。
    /// </summary>
    private void EnsureLamp(int rack, int level, IoPointRole role, bool state)
    {
        var key = new IoPointKey(false, role, rack, level);
        if (_writtenLamps.TryGetValue(key, out var written) && written == state)
            return;
        try
        {
            if (_bus.WritePoint(key, state))
                _writtenLamps[key] = state;
            // 写失败静默：总线 Error 事件已报通讯问题，这里下轮重试即可（铁律 4）
        }
        catch (Exception ex)
        {
            // 只兜"总线已释放"等极端竞态（正常失败走 false 不抛）——发生即异常路径，记边沿
            _log($"架{rack}层{level} 写灯异常：{ex.Message}");
        }
    }

    /// <summary>光栅是否让空：左右都没亮才算让（没绑定 null 视为让空，不卡没配光栅的仓位）。</summary>
    private bool GrillClear(int rack, int level)
    {
        var snap = _bus.LatestSnapshot;
        var left = snap.Get(new IoPointKey(true, IoPointRole.RackLightL, rack, level));
        var right = snap.Get(new IoPointKey(true, IoPointRole.RackLightR, rack, level));
        return left != true && right != true;
    }

    private SlotRuntime GetOrAdd(int rack, int level)
    {
        if (!_slots.TryGetValue((rack, level), out var rt))
            _slots[(rack, level)] = rt = new SlotRuntime();
        return rt;
    }

    private void SafePump()
    {
        try
        {
            Pump(_clock());
        }
        catch (Exception ex)
        {
            // 心跳线程兜底：任何异常不炸线程，记边沿（铁律 4：异常本身即边沿）
            _log($"状态机心跳异常：{ex.Message}");
        }
    }
}
