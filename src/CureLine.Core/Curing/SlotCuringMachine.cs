// =========================================================================
// SlotCuringMachine：仓位固化状态机（程序①程控核心）
//
// 干什么：吃 IO 总线的点变化边沿，按工艺策略驱动每个仓位（架×层）的状态
//   流转：空闲 →（托盘到位；门控开则等光栅让空）→ 计时中（黄灯）→
//   到点放行（绿灯）→（托盘离开）→ 灭灯回空闲。任何时刻托盘离开都回
//   空闲（计时中取件=计时作废并记日志）。
//
// 光栅两用（V0.0.63 客户口径收口："计时不变，报警复位"）：
//   ① 门控闸：只在托盘到位那一瞬间当闸门（防人手还在架内就开计时）；
//   ② 报警：计时一经开始，光栅任何 ON/OFF 都不停表、不重置、不作废——
//      唯一能打断计时的仍是托盘离开（取件）。中途光栅 ON（人手伸入）只
//      做安全报警：红灯点亮（黄绿灭），光栅让空即自动复位（红灯灭、黄灯
//      复亮）。WaitingGate（本来就在等门控）与 Done（取件伸入是正常动作）
//      不触发报警；报警态不落盘（curingstate.json 不加字段）——重启对齐/
//      续算恢复按当前光栅快照重判（人手仍在架内则重新进报警态）。
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
//   ③ 策略热切换：每次托盘到位现取激活策略（新到位的仓位立即按新时长）；
//      在制仓位两个口径：a) 默认沿走"开计时那一刻锁定的时长"（兜底安全）；
//      b) 宿主收口调 ApplyPolicyHotChange（策略窗保存后）做单向上调热更——
//      只延不缩：新解析值比锁定值大才延长并同步落盘，调小一律不动（调小
//      立即生效=自动放行没固化够的货，工艺红线）。
//   ④ 过账挂点（P3.2 后半）：TrayArrived/CuringStarted/TrayLeft 三事件供宿主
//      接单线过账（CuringMesPoster）——只发"真实边沿"，重启对齐不发（防 MES
//      重复过账）；处理器禁止同步回调状态机（锁内触发红线，见事件注释）。
//
// 日志（铁律 4）：只记边沿——开计时/放行/取件/重启对齐，不刷屏。
//   时长文本可由宿主注入（durationText，如程序①按显示单位把 1440 分钟读成
//   "24小时"）；不注入时统一"X 分钟"老口径——Core 不背显示单位静态。
//
// 怎么改：新增工艺分支（如暂停计时）在 SlotPhase 加状态 + OnPointChanged/
//   Pump 两处推进逻辑；灯色语义（黄=计时绿=放行）改动要同步
//   RackSlotViewModel 的展示口径与 CuringPolicy 注释。
// =========================================================================

using CureLine.Core.Comms;

namespace CureLine.Core.Curing;

/// <summary>仓位流程状态。Idle=空闲；WaitingGate=托盘在但等光栅让空；Curing=计时中；Done=到点放行。</summary>
public enum SlotPhase
{
    Idle,
    WaitingGate,
    Curing,
    Done,
}

/// <summary>仓位状态机快照（record 不可变，锁内构造后随便传）。</summary>
/// <remarks>
/// 旧版 StateText（"计时中 剩X分"中文拼串）已删：文本随显示单位（时/分）换算，
/// 属 UI 口径——拼装收在程序① RackSlotViewModel.Refresh（缓存快照自己拼），
/// Core 恒为分钟数据（Phase/TotalMinutes/RemainMinutes）。
/// </remarks>
public sealed record SlotStatus(SlotPhase Phase, int TotalMinutes, int RemainMinutes);

public sealed class SlotCuringMachine : IDisposable
{
    /// <summary>策略缺失时的兜底时长（策略库空/没激活；正常运行时策略自愈，几乎走不到）。</summary>
    private const int FallbackMinutes = 120;

    private readonly object _sync = new();
    private readonly Func<CuringPolicy?> _policyProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string> _log;
    /// <summary>日志时长文本格式化（宿主注入跟随显示单位；null=缺省"X 分钟"口径）。</summary>
    private readonly Func<int, string>? _durationText;
    private readonly Dictionary<(int Rack, int Level), SlotRuntime> _slots = new();
    /// <summary>成功写过的灯态（IoPointKey→值）：去重 + 失败补写的比对基准。</summary>
    private readonly Dictionary<IoPointKey, bool> _writtenLamps = new();
    private IPointTableBus _bus;
    private Timer? _pumpTimer;

    // ---------- 过账挂点（P3.2 后半，宿主接 CuringMesPoster） ----------
    // 三个事件都在 _sync 锁内触发。【红线】处理器禁止同步回调状态机任何公共
    // 方法（_sync 重入死锁）——只做轻量投递（过账服务内部 Task.Run 即返回）。
    // "真实边沿"=现场传感器/光栅实际变化；重启对齐（AlignFromSnapshot）不发——
    // 物料断电前已进站，重发会造成 MES 重复过账。

    /// <summary>托盘真实到位（真实边沿）。单线过账进站挂点（Arrive 口径）。</summary>
    public event Action<int, int>? TrayArrived;

    /// <summary>开计时（真实边沿，含门控放行与兜底推进）。进站挂点（Start 口径）。</summary>
    public event Action<int, int>? CuringStarted;

    /// <summary>
    /// 托盘离开（真实边沿）。参数：架、层、离开时相位、已固化秒数
    /// （Done=计划时长；Curing 中途取件=实际经过；WaitingGate=0）。
    /// 单线过账出站挂点（API0027 固化时间 + API0030 出站）。
    /// </summary>
    public event Action<int, int, SlotPhase, int>? TrayLeft;

    /// <summary>单仓位运行态（内部可变，只在 _sync 锁内动）。</summary>
    private sealed class SlotRuntime
    {
        public SlotPhase Phase;
        public DateTimeOffset? StartedAt; // Curing 起点（时钟可注入，测试虚拟推进）
        public int TotalMinutes;          // 开计时时锁定的时长（在制热更单向上调，见 ApplyPolicyHotChange）
        public bool AlarmGrill;           // Curing 中光栅被挡的报警态（红灯亮着）——瞬时安全态不落盘
    }

    /// <summary>
    /// 构造即起 1s 心跳（后台线程）。bus 可后续经 Attach 换（重连不丢状态）；
    /// policyProvider 每次托盘到位现取（支持策略窗热切换）；clock/log/durationText
    /// 可注入（测试：clock/log；显示单位：durationText——缺省 null 走"X 分钟"）。
    /// durationText 在后台线程逐条调用，须线程安全（纯函数即可）。
    /// </summary>
    public SlotCuringMachine(IPointTableBus bus, Func<CuringPolicy?> policyProvider,
        Func<DateTimeOffset>? clock = null, Action<string>? log = null,
        Func<int, string>? durationText = null)
    {
        _bus = bus;
        _policyProvider = policyProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log ?? (_ => { });
        _durationText = durationText;
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
                    OnTrayArrived(rt, key.Rack, key.Level, realEdge: true);
                else
                    OnTrayLeft(rt, key.Rack, key.Level);
            }
        }
        else if (key.Role is IoPointRole.RackLightL or IoPointRole.RackLightR)
        {
            // 光栅边沿按计时相位分派（口径见类头"光栅两用"）：
            // ① 让空（OFF 边沿）+ 门控等待中 → 立即开计时（Pump 有快照兜底，双保险）；
            // ② 让空（OFF 边沿）+ 计时中报警态 + 双光栅全让空 → 报警复位
            //    （红灯灭、黄灯复亮）——一侧让空另一侧仍挡不算（人手还在架区）；
            // ③ 亮起（ON 边沿）+ 计时中未报警 → 报警亮红灯（计时不动——客户口径
            //    "固化计时不需要变化，报警复位就可以"）。WaitingGate（本来就在等
            //    门控）与 Done（取件伸入是正常动作）不触发报警。如现场要求计时中
            //    暂停表，属新分支：加 SlotPhase.Paused 处理。
            lock (_sync)
            {
                var rt = GetOrAdd(key.Rack, key.Level);
                if (!e.State)
                {
                    if (rt.Phase == SlotPhase.WaitingGate && GrillClear(key.Rack, key.Level))
                        StartCuring(rt, key.Rack, key.Level);
                    else if (rt.Phase == SlotPhase.Curing && rt.AlarmGrill
                        && GrillClear(key.Rack, key.Level))
                        SetGrillAlarm(rt, key.Rack, key.Level, on: false);
                }
                else if (rt.Phase == SlotPhase.Curing && !rt.AlarmGrill)
                {
                    SetGrillAlarm(rt, key.Rack, key.Level, on: true);
                }
            }
        }
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
                            // 报警中到点：放行接替报警（绿灯让位红灯——取件作业中不再"禁止靠近"）
                            bool alarmWasOn = rt.AlarmGrill;
                            rt.AlarmGrill = false;
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampRed, false);
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, false);
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, true);
                            _log(alarmWasOn
                                ? $"架{slot.Rack}层{slot.Level} 固化到点放行（绿灯，共 {MinutesText(rt.TotalMinutes)}，报警随放行解除）"
                                : $"架{slot.Rack}层{slot.Level} 固化到点放行（绿灯，共 {MinutesText(rt.TotalMinutes)}）");
                            PersistState(); // 状态变化落盘（开关开时）
                        }
                        else if (rt.AlarmGrill)
                        {
                            // 计时中报警态：红灯亮/黄绿灭补写自愈（去重后稳态零网络写；
                            // 报警灯早于黄灯写入失败时靠这里重试，与黄灯补写同性质）
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampRed, true);
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, false);
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, false);
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
    /// 策略热更收口（宿主在策略窗保存/激活后调）：把当前激活策略解析出的
    /// 时长应用到"在制"仓位——【只延不缩】。
    ///
    /// 为什么只延：在制调小立即生效=把还没固化满的货提前放行（工艺事故），
    /// 所以调小一律不动——在制品保持开计时那一刻锁定的时长走完；调大则
    /// 允许延长（客户临时"多固化一会儿再做"是常见诉求）并同步重写持久化
    /// 记录（开关开时），断电恢复按延长后的时长续算。
    ///
    /// 谁不动：WaitingGate（没开计时，开计时那一刻会重新按策略解析）、
    /// Done（已放行，改在制品时长无意义）、Idle（无在制品）。
    /// 线程：任意线程可调（锁内遍历，只改内存+小文件落盘）；有延长才记
    /// 日志/落盘——无变化零动作（铁律 4：只记边沿）。
    /// </summary>
    public void ApplyPolicyHotChange()
    {
        var policy = _policyProvider();
        if (policy == null)
            return;
        lock (_sync)
        {
            bool changed = false;
            foreach (var (slot, rt) in _slots)
            {
                if (rt.Phase != SlotPhase.Curing)
                    continue;
                int newMinutes = policy.ResolveMinutes(slot.Rack, slot.Level);
                if (newMinutes > rt.TotalMinutes)
                {
                    _log($"架{slot.Rack}层{slot.Level} 策略热更：在制固化时长 " +
                        $"{MinutesText(rt.TotalMinutes)} 延长为 {MinutesText(newMinutes)}（只延不缩）");
                    rt.TotalMinutes = newMinutes;
                    changed = true;
                }
            }
            if (changed)
                PersistState(); // 延长同步落盘（开关开时），断电恢复按新时长续算
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
                OnTrayArrived(rt, key.Rack, key.Level, realEdge: false);
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
            EnsureLamp(rack, level, IoPointRole.LampRed, false); // 防断电前红灯寄存器残留
            EnsureLamp(rack, level, IoPointRole.LampYellow, false);
            EnsureLamp(rack, level, IoPointRole.LampGreen, true);
            _log($"架{rack}层{level} 重启恢复：断电前已固化到点，保持放行（绿灯，共 {MinutesText(rec.Minutes)}）");
            return;
        }
        // Curing：按记录起点续算——断电期间流逝的时间自然计入固化时长
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = rec.StartedAt;
        rt.TotalMinutes = rec.Minutes;
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        EnsureLamp(rack, level, IoPointRole.LampRed, false); // 防残留红灯（报警态不落盘，见下）
        if (!GrillClear(rack, level))
            SetGrillAlarm(rt, rack, level, on: true); // 续算恢复即复核：人手仍在架内 → 重新进报警态
        int remain = Math.Max(0, rec.Minutes - (int)Math.Ceiling((_clock() - rec.StartedAt).TotalMinutes));
        _log($"架{rack}层{level} 重启恢复：续算断电前计时，剩 {MinutesText(remain)}（共 {MinutesText(rec.Minutes)}）");
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

    private void OnTrayArrived(SlotRuntime rt, int rack, int level, bool realEdge)
    {
        if (rt.Phase is SlotPhase.Curing or SlotPhase.Done)
            return; // 已在流程中（传感器抖动/重复边沿），不重置计时
        // 真实到位恒发（进站挂点，Arrive 口径）——门控等放行与直接开计时两路
        // 都要发，否则"门控关直接开计时"的仓位进站就漏了；重启对齐（realEdge
        // =false）不发，防 MES 重复过账。
        if (realEdge)
            TrayArrived?.Invoke(rack, level);
        var policy = _policyProvider();
        int minutes = policy?.ResolveMinutes(rack, level) ?? FallbackMinutes;
        if (policy is { GrillGate: true } && !GrillClear(rack, level))
        {
            rt.Phase = SlotPhase.WaitingGate;
            rt.TotalMinutes = minutes;
            rt.StartedAt = null;
            _log($"架{rack}层{level} 托盘到位，等光栅让空后开始计时（{MinutesText(minutes)}）");
            return;
        }
        StartCuring(rt, rack, level, minutes, realEdge);
    }

    private void StartCuring(SlotRuntime rt, int rack, int level, int? minutes = null, bool realEdge = true)
    {
        var policy = _policyProvider();
        rt.TotalMinutes = minutes ?? policy?.ResolveMinutes(rack, level) ?? FallbackMinutes;
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = _clock();
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false); // 防残留绿灯
        _log($"架{rack}层{level} 开始固化计时 {MinutesText(rt.TotalMinutes)}（黄灯）");
        if (realEdge)
            CuringStarted?.Invoke(rack, level); // 过账进站挂点（Start 口径，Poster 内按配置取舍）
        // 开计时即复核光栅：门控关（GrillGate=false）的现场人手也可能恰好在架内，
        // 计时一开始就"被挡"——按报警语义立即亮红（SetGrillAlarm 内有状态去重，
        // 正常门控放行路径此刻光栅已让空，走到这里必然零操作）。
        if (!GrillClear(rack, level))
            SetGrillAlarm(rt, rack, level, on: true);
        PersistState(); // 状态变化落盘（开关开时）
    }

    private void OnTrayLeft(SlotRuntime rt, int rack, int level)
    {
        if (rt.Phase == SlotPhase.Idle)
            return;
        // 过账出站参数先快照（状态清零前）：离开相位 + 已固化秒数
        SlotPhase leavePhase = rt.Phase;
        int curedSeconds = leavePhase switch
        {
            SlotPhase.Done => rt.TotalMinutes * 60,
            SlotPhase.Curing when rt.StartedAt is { } start =>
                Math.Max(0, (int)(_clock() - start).TotalSeconds),
            _ => 0, // WaitingGate：没开计时
        };
        string note = leavePhase switch
        {
            SlotPhase.Curing => "计时中取件，计时作废",
            SlotPhase.Done => "取件完成",
            SlotPhase.WaitingGate => "托盘离开（未开计时）",
            _ => "托盘离开",
        };
        rt.Phase = SlotPhase.Idle;
        rt.StartedAt = null;
        rt.TotalMinutes = 0;
        // 报警态随取件一并消除（人手在架内直接端走托盘的场景）：红灯也让位，
        // 与黄绿同批灭灯——不额外记"复位"日志，取件日志（下方 note）已是本边沿。
        rt.AlarmGrill = false;
        EnsureLamp(rack, level, IoPointRole.LampRed, false);
        EnsureLamp(rack, level, IoPointRole.LampYellow, false);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        _log($"架{rack}层{level} {note}，回空闲");
        PersistState(); // 状态变化落盘（开关开时）：取件仓位从记录中移除
        // 过账出站挂点（唯一调用方 OnPointChanged 恒为真实边沿，无对齐路径）
        TrayLeft?.Invoke(rack, level, leavePhase, curedSeconds);
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
    /// 日志用时长文本：宿主注入了 durationText 就按它说（跟随显示单位），
    /// 没注入统一"X 分钟"老口径（缺省值与测试断言、历史日志观感都不变）。
    /// </summary>
    private string MinutesText(int minutes) =>
        _durationText != null ? _durationText(minutes) : $"{minutes} 分钟";

    /// <summary>
    /// 写灯（去重）：与上次成功写的值相同则跳过（稳态零网络写）；
    /// 失败不记账 → 下轮 Pump 自动补写。红灯走光栅报警语义（SetGrillAlarm，
    /// V0.0.63 起：计时中人手伸入亮红、让空复位——不再是"永不写"的预留位）。
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

    /// <summary>
    /// 光栅报警收口（V0.0.63 客户口径"固化计时不需要变化，报警复位就可以"）：
    /// 仅 Curing 相位有意义。亮起：红灯独亮（黄绿灭——三色灯"红=禁止靠近"现场
    /// 语义）+ 一条边沿日志；复位：红灯灭、黄灯复亮 + 一条边沿日志。报警态
    /// （rt.AlarmGrill）就是状态比对闸——重复 ON 边沿/重复 OFF 边沿在这里短路，
    /// 日志只记边沿不刷屏（铁律 4）。写灯失败不记账 → 下轮 Pump 报警分支
    /// 补写自愈（与黄灯补写同性质）。复位不动计时（StartedAt 原样）。
    /// </summary>
    private void SetGrillAlarm(SlotRuntime rt, int rack, int level, bool on)
    {
        if (rt.AlarmGrill == on)
            return; // 状态去重：冗余边沿不重复写灯、不重复记日志
        rt.AlarmGrill = on;
        if (on)
        {
            EnsureLamp(rack, level, IoPointRole.LampRed, true);
            EnsureLamp(rack, level, IoPointRole.LampYellow, false);
            EnsureLamp(rack, level, IoPointRole.LampGreen, false);
            _log($"架{rack}层{level} 光栅被挡（人手伸入），报警（红灯），计时继续");
        }
        else
        {
            EnsureLamp(rack, level, IoPointRole.LampRed, false);
            EnsureLamp(rack, level, IoPointRole.LampYellow, true);
            _log($"架{rack}层{level} 光栅让空，报警复位（红灯灭），计时继续");
        }
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
