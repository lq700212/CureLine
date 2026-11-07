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
//      唯一能打断计时的仍是托盘离开（取件）。中途光栅"被挡"（人手伸入）
//      只做安全报警：红灯点亮（黄绿灭），光栅让空即自动复位（红灯灭、黄灯
//      复亮）。WaitingGate（本来就在等门控）与 Done（取件伸入是正常动作）
//      不触发报警；报警态不落盘（curingstate.json 不加字段）——重启对齐/
//      续算恢复按当前光栅快照重判（人手仍在架内则重新进报警态）。
//      复位是双保险（V0.0.64）：让空边沿即时判快照复位（总线已保证边沿
//      处理时快照含本次边沿值，见 IoBus 轮询契约）；边沿若错过（让空与
//      其他变化同拍/掉线期间），Pump 每秒按快照双让空兜底复位——红灯
//      最多挂 1 秒，不再挂到到点放行。复位判定是"双侧全让空"（一侧让空
//      另一侧仍挡=人手还在架区，不复位）；V0.0.68 排障增强：让空边沿
//      到达但判定被拒时记边沿日志明说哪侧仍挡（此前静默，现场只能看
//      到"复位不了"却不知道是信号没让空）——Pump 兜底不带此日志（每秒
//      拍会刷屏）。
//
// 光栅信号极性（V0.0.72，见 GrillSignalPolarity）：
//   BlockedOn（缺省）= 遮挡=ON：ON=被挡、OFF=让空——V0.0.63~V0.0.71 口径；
//   BlockedOff      = 遮挡=OFF：OFF=被挡、ON=让空——现场实测常闭安全光栅。
//   本类只认"被挡/让空"语义（IsBlocked 一处解释原始电平），门控/报警/复位/
//   排障日志全部经它判定——加新光栅品牌先看枚举注释，别在调用点散写
//   "== true"（极性切换即全部反相）。provider 与复位模式同款：边沿/心跳
//   每拍现取，宿主勾选后下一拍生效；缺省 null=恒 BlockedOn（Core 独立/
//   测试 API 缺省——程序①现场由 Hub 的 ini 决定）。
//
// 复位模式（V0.0.69，用户拍板二选一，见 GrillAlarmResetMode）：
//   Auto = 自动复位：光栅让空（边沿+Pump 兜底双保险）即自动复位；
//   Manual（程序①现场缺省，V0.0.71 用户拍板）= 人工复位：让空不复位——
//     红灯与机台蜂鸣保持，边沿与 Pump 兜底都不再自动解除（日志改说"等人工
//     确认"），唯一解除口=宿主按钮链调 ManualResetGrillAlarm（程序①"报警
//     复位"窗逐项/全部确认）；到点放行（绿灯接替）与取件离开（自然回空闲）
//     路径人工档也保留——报警只管"人能不能靠近"，固化计时/放行独立运作
//     （客户口径"计时不变"）。
//   模式经构造注入 Func<GrillAlarmResetMode> provider（缺省 null=恒 Auto，
//   这是 Core 独立使用/测试的 API 缺省；程序①现场缺省由宿主 ini 决定=
//   Manual），边沿/心跳每拍现取——宿主配置热切后下一拍即生效（勿缓存进字段）。
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
/// <remarks>
/// AlarmGrill（V0.0.67）：光栅报警标志。Curing 中报警按客户口径"计时不变"，
/// Phase 仍是 Curing——UI 靠本标志把卡切 Alarm 口径（红灯 DO 反推分支在精确
/// 态路线下永远轮不到，此前卡牌报警不显示的根因即缺这个标志）。
/// 第 4 参缺省 false：既有 3 参构造点（DocShot 演示帧/测试）零改动。
/// </remarks>
public sealed record SlotStatus(SlotPhase Phase, int TotalMinutes, int RemainMinutes, bool AlarmGrill = false);

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
    /// <summary>
    /// 报警复位模式提供者（V0.0.69；null=恒 Auto——Core 独立/测试缺省，
    /// 程序①现场缺省由 Hub 的 ini 决定=Manual）。边沿/心跳每拍现取，
    /// 宿主热切模式（策略窗按钮）下一拍即生效；provider 须线程安全（后台线程逐拍调）。
    /// </summary>
    private readonly Func<GrillAlarmResetMode>? _resetModeProvider;
    /// <summary>
    /// 光栅信号极性提供者（V0.0.72；null=恒 BlockedOn——Core 独立/测试缺省，
    /// 程序①现场由 Hub 的 ini 决定，策略窗勾选框热切）。边沿/心跳每拍现取；
    /// provider 须线程安全（后台线程逐拍调）。
    /// </summary>
    private readonly Func<GrillSignalPolarity>? _grillPolarityProvider;
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
    /// resetModeProvider（V0.0.69，可空）=报警复位模式每拍现取（null=恒自动
    /// 复位——Core 独立/测试缺省；程序①现场缺省 Manual 由 Hub 决定；宿主传
    /// 静态中枢读点，热切模式无需重建状态机）。
    /// grillPolarityProvider（V0.0.72，可空）=光栅信号极性每拍现取（null=恒
    /// BlockedOn 遮挡=ON——现场"被挡=OFF"由程序①策略窗勾选切换，宿主同样
    /// 传静态中枢读点，无需重建状态机）。
    /// </summary>
    public SlotCuringMachine(IPointTableBus bus, Func<CuringPolicy?> policyProvider,
        Func<DateTimeOffset>? clock = null, Action<string>? log = null,
        Func<int, string>? durationText = null,
        Func<GrillAlarmResetMode>? resetModeProvider = null,
        Func<GrillSignalPolarity>? grillPolarityProvider = null)
    {
        _bus = bus;
        _policyProvider = policyProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log ?? (_ => { });
        _durationText = durationText;
        _resetModeProvider = resetModeProvider;
        _grillPolarityProvider = grillPolarityProvider;
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
            // 光栅边沿按计时相位分派（口径见类头"光栅两用/复位模式/信号极性"）。
            // 【极性】原始电平先经 IsBlocked 解释成本义的"挡/空"（V0.0.72）：
            //   BlockedOn 时 ON=挡、OFF=空；BlockedOff 时 OFF=挡、ON=空。
            //   下面所有分支只谈"让空/被挡"，勿再直接判 e.State（换光栅品牌
            //   改配置即整体反相，散写 == true 会漏改一处就反一半）。
            // ① 让空边沿 + 门控等待中 → 立即开计时（Pump 有快照兜底，双保险）；
            // ② 让空边沿 + 计时中报警态 + 双光栅全让空 → 报警复位
            //    （红灯灭、黄灯复亮）——一侧让空另一侧仍挡不算（人手还在架区）；
            //    【自动复位模式限定，人工档 ②c 处理】GrillClear 读快照判定：
            //    V0.0.64 起总线契约保证边沿处理时快照已含本次边沿值（先整拍
            //    换快照再发边沿），此判定永远成立；边沿若错过由 Pump 快照
            //    兜底复位（双保险）；
            // ②b 让空但复位判定被拒（自动档）：记"哪侧仍挡"排障日志（V0.0.68）；
            // ②c 让空（人工复位档 V0.0.69）：不复位——红灯与蜂鸣保持，
            //    记边沿日志"待人工确认"（解除口=ManualResetGrillAlarm）；
            // ③ 被挡边沿 + 计时中未报警 → 报警亮红灯（计时不动——客户口径
            //    "固化计时不需要变化，报警复位就可以"）。WaitingGate（本来就在等
            //    门控）与 Done（取件伸入是正常动作）不触发报警。如现场要求计时中
            //    暂停表，属新分支：加 SlotPhase.Paused 处理。
            lock (_sync)
            {
                var rt = GetOrAdd(key.Rack, key.Level);
                bool blocked = IsBlocked(e.State, Polarity());
                if (!blocked)
                {
                    if (rt.Phase == SlotPhase.WaitingGate && GrillClear(key.Rack, key.Level))
                        StartCuring(rt, key.Rack, key.Level);
                    else if (rt.Phase == SlotPhase.Curing && rt.AlarmGrill &&
                        ResetMode() == GrillAlarmResetMode.Auto && GrillClear(key.Rack, key.Level))
                        SetGrillAlarm(rt, key.Rack, key.Level, on: false);
                    else if (rt.Phase == SlotPhase.Curing && rt.AlarmGrill &&
                        ResetMode() == GrillAlarmResetMode.Auto)
                    {
                        // ②b 自动档复位被拒：该侧光栅让空,但复位判定没过(本仓位还有
                        // 一侧仍挡=人手还在架区)——V0.0.68 前这里是静默的:现场
                        // 拿开手后红灯不灭,程序一个字都不说,排障的人只能看到"复位
                        // 不了"三个字。现在把拒绝原因写成边沿日志(只在报警态的该
                        // 仓位发生,边沿稀疏不会刷屏;Pump 兜底不带——每秒拍会刷屏),
                        // 让现场一眼看到"哪侧光栅信号没让回空"。
                        _log($"架{key.Rack}层{key.Level} 光栅让空但另一侧仍挡" +
                            $"（{GrillSideText(key.Rack, key.Level)}），复位暂缓、红灯保持");
                    }
                    else if (rt.Phase == SlotPhase.Curing && rt.AlarmGrill)
                    {
                        // ②c 人工档（V0.0.69）：让空不复位——红灯与机台蜂鸣保持，
                        // 记边沿日志"待人工确认"（红灯保持是工艺要求，不是故障；
                        // 解除口=报警复位窗的 ManualResetGrillAlarm）。侧读数带上，
                        // 现场"这条报警为什么还挂着"有据可查；边沿稀疏不刷屏。
                        _log($"架{key.Rack}层{key.Level} 光栅让空（" +
                            $"{GrillSideText(key.Rack, key.Level)}），人工复位模式待确认，红灯保持");
                    }
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
                        else if (rt.AlarmGrill && ResetMode() == GrillAlarmResetMode.Auto &&
                            GrillClear(slot.Rack, slot.Level))
                        {
                            // 快照兜底复位（V0.0.64，与下方 WaitingGate 兜底开计时同构）：
                            // 光栅让空边沿错过（让空与边沿同拍/掉线期间）时，边沿复位
                            // 判定会落空——只要快照双让空立即复位，红灯不再挂到到点/取件
                            // 【自动档限定，人工档保持红灯等确认（V0.0.69）】
                            SetGrillAlarm(rt, slot.Rack, slot.Level, on: false);
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
                            // 红灯补灭自愈（V0.0.64）：复位写灯失败时残留的红灯在这里
                            // 兜住（与同轮黄灯补写同性质；去重后稳态零网络写）
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampRed, false);
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
            // 机台塔灯每拍聚合（V0.0.65）：兜住边沿挂点之外的聚合变化——到点放行
            // （Curing→Done）与快照兜底复位都发生在这层循环内；同时兼每秒自愈补写
            // （与仓位灯补写同性质，EnsureLamp 去重后稳态零网络写）。
            RefreshMachineBeacon();
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

    /// <summary>查仓位状态（UI 500ms 刷用）。空闲/没动过回 null（UI 走灯色反推口径）。
    /// 报警标志随快照带出（AlarmGrill）——计时中光栅报警 Phase 仍是 Curing（计时不变），
    /// UI 由此把卡切报警口径（V0.0.67，此前快照没带标志卡牌就显示不出报警）。</summary>
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
            return new SlotStatus(rt.Phase, rt.TotalMinutes, remain, rt.AlarmGrill);
        }
    }

    /// <summary>
    /// 当前报警复位模式（每拍现取 provider，勿缓存）：null provider=恒 Auto
    /// （Core 独立/测试缺省；程序①现场缺省 Manual 由 Hub 决定）。
    /// 后台线程（边沿/心跳）逐次调用——provider 必须线程安全纯读（宿主传
    /// 静态中枢读点，见程序① GrillAlarmResetModeHub）。
    /// </summary>
    private GrillAlarmResetMode ResetMode() =>
        _resetModeProvider?.Invoke() ?? GrillAlarmResetMode.Auto;

    /// <summary>
    /// 当前光栅信号极性（V0.0.72，每拍现取 provider，勿缓存）：null provider=
    /// 恒 BlockedOn（遮挡=ON——Core 独立/测试缺省；程序①现场由 Hub 的 ini 决定，
    /// 策略窗勾选切换）。后台线程（边沿/心跳）逐次调用——provider 必须线程安全
    /// 纯读（宿主传静态中枢读点，见程序① GrillSignalPolarityHub）。
    /// </summary>
    private GrillSignalPolarity Polarity() =>
        _grillPolarityProvider?.Invoke() ?? GrillSignalPolarity.BlockedOn;

    /// <summary>
    /// 人工复位确认（V0.0.69，宿主"报警复位"窗【解除报警】按钮的唯一解除口）：
    /// 报警态的仓位解除报警（红灯灭、黄灯复亮、机台蜂鸣停），计时不动。
    /// 不要求光栅已让空——操作员点按钮=人工确认现场已处理，按钮是权威；
    /// 当时光栅读数进日志（人工复位行）供追溯。
    /// 返回 true=确实复位了；false=该仓位不在报警态（已复位/流程已走/未知
    /// 仓位，本身就是幂等安全——重复点按钮无害）。
    /// 线程：任意线程可调（锁内动作；宿主从后台 Task 调，勿在 UI 线程直调——
    /// 内部 EnsureLamp 是写灯网络 IO，铁律 2）。
    /// </summary>
    public bool ManualResetGrillAlarm(int rack, int level)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue((rack, level), out var rt) || !rt.AlarmGrill)
                return false;
            SetGrillAlarm(rt, rack, level, on: false, manual: true);
            return true;
        }
    }

    /// <summary>
    /// 当前正在光栅报警的仓位清单（升序：层内架号顺序），供宿主"报警复位"
    /// 窗列清单（人工复位模式）——与 SetGrillAlarm 记账同源（AlarmGrill 态），
    /// 弹窗清单必能复位成功（不混入灯色反推的"红灯亮"非状态机报警）。
    /// 线程：任意线程可调（锁内快照）。
    /// </summary>
    public IReadOnlyList<(int Rack, int Level)> GetAlarmSlots()
    {
        lock (_sync)
        {
            return _slots
                .Where(kv => kv.Value.AlarmGrill)
                .Select(kv => (kv.Key.Rack, kv.Key.Level))
                .OrderBy(a => a.Item2).ThenBy(a => a.Item1)
                .ToList();
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
            RefreshMachineBeacon(); // 等门控也算"忙碌"——塔灯黄灯即时（后续开计行走 StartCuring 尾）
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
        RefreshMachineBeacon(); // 机台塔灯同步：新仓位开计时，全机聚合即时跟随（黄/红）
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
        RefreshMachineBeacon(); // 机台塔灯同步：取件/取走后全机聚合即时跟随（蜂鸣停，回绿或黄）
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

    /// <summary>
    /// 机台总监塔灯聚合刷新（V0.0.65，仅 <see cref="_sync"/> 锁内调用）：
    /// 全机一套四色塔灯+蜂鸣，优先级 红 &gt; 绿 &gt; 黄（V0.0.67 黄绿互换，
    /// 现场口径"没有来料在固化=待料黄灯"）——
    ///   任一仓位 AlarmGrill → 红+蜂鸣长鸣；否则任一仓位 Curing/WaitingGate
    ///   （计时中/等门控）→ 绿（设备运转中）；否则 → 黄常亮（待料待机，
    ///   含全空与"已全部放行待取件"——放行态不在固化）。蓝不写
    ///   （预留现场自接）。每态一次性写四路（红黄绿+蜂），EnsureLamp 逐路去重，
    ///   稳态零网络写；未绑定塔灯时 WritePoint 静默 false 零网络成本。
    /// 挂点=每个可能改变聚合态的转移收尾（StartCuring/OnTrayArrived 等门控/
    /// OnTrayLeft/SetGrillAlarm/Pump 尾），到点放行走 Pump 尾兜住——覆盖清单见
    /// 本方法头注释与 SlotCuringMachineTests 用例。
    /// </summary>
    private void RefreshMachineBeacon()
    {
        bool anyAlarm = false, anyBusy = false;
        foreach (var rt in _slots.Values)
        {
            if (rt.AlarmGrill)
            {
                anyAlarm = true;
                break; // 红优先：一个报警即定全机塔灯
            }
            if (rt.Phase is SlotPhase.Curing or SlotPhase.WaitingGate)
                anyBusy = true;
        }

        if (anyAlarm)
        {
            // 报警总指示：红常亮 + 蜂鸣长鸣（复位/放行/取件才停）
            EnsureLamp(0, 0, IoPointRole.BeaconRed, true);
            EnsureLamp(0, 0, IoPointRole.BeaconYellow, false);
            EnsureLamp(0, 0, IoPointRole.BeaconGreen, false);
            EnsureLamp(0, 0, IoPointRole.Buzzer, true);
        }
        else if (anyBusy)
        {
            // 有来料在固化/等门控：绿常亮=设备运转中（黄红灭防残留，蜂停）
            EnsureLamp(0, 0, IoPointRole.BeaconRed, false);
            EnsureLamp(0, 0, IoPointRole.BeaconYellow, false);
            EnsureLamp(0, 0, IoPointRole.BeaconGreen, true);
            EnsureLamp(0, 0, IoPointRole.Buzzer, false);
        }
        else
        {
            // 全机无固化任务（空闲/已成放行待取件）：黄常亮=待料待机（用户口径）
            EnsureLamp(0, 0, IoPointRole.BeaconRed, false);
            EnsureLamp(0, 0, IoPointRole.BeaconYellow, true);
            EnsureLamp(0, 0, IoPointRole.BeaconGreen, false);
            EnsureLamp(0, 0, IoPointRole.Buzzer, false);
        }
    }

    /// <summary>
    /// 光栅是否让空：左右都没被挡才算让（没绑定 null 视为让空，不卡没配光栅的仓位）。
    /// 两侧共用同一次 Polarity 快照（V0.0.72）——极端情况下热切极性恰好插在
    /// 两次读之间时，不会一侧按旧档、一侧按新档解释（判定自相矛盾）。
    /// </summary>
    private bool GrillClear(int rack, int level)
    {
        var polarity = Polarity();
        var snap = _bus.LatestSnapshot;
        var left = snap.Get(new IoPointKey(true, IoPointRole.RackLightL, rack, level));
        var right = snap.Get(new IoPointKey(true, IoPointRole.RackLightR, rack, level));
        return !IsBlocked(left, polarity) && !IsBlocked(right, polarity);
    }

    /// <summary>
    /// 原始电平 → 业务语义"是否被挡"（V0.0.72 光栅极性唯一解释口）：
    /// 状态机的门控/报警/复位全走这里，禁止在调用点直接判 `== true`。
    /// null（未绑定/未读到）恒 false=不让——与 GrillClear 的旧口径一致，
    /// 没配光栅的仓位永不卡流程。
    /// </summary>
    private static bool IsBlocked(bool? raw, GrillSignalPolarity polarity) =>
        raw is bool value && (polarity == GrillSignalPolarity.BlockedOff ? !value : value);

    /// <summary>
    /// 光栅两侧读数文本（"L=挡 R=空"），供电日志说明复位判定当场被谁堵：
    /// 与 GrillClear 同源（快照原子换引用）+ 按当前极性解释原始电平（V0.0.72，
    /// 现场日志说"挡/空"就与亲眼看到的一致，不用自己换算 ON/OFF）。
    /// 没绑定的侧显示"—"（null=未读到，与让空同义但排障时值得区分）。
    /// </summary>
    private string GrillSideText(int rack, int level)
    {
        var polarity = Polarity();
        var snap = _bus.LatestSnapshot;
        var left = snap.Get(new IoPointKey(true, IoPointRole.RackLightL, rack, level));
        var right = snap.Get(new IoPointKey(true, IoPointRole.RackLightR, rack, level));
        string Flag(bool? v) => v == null ? "—" : IsBlocked(v, polarity) ? "挡" : "空";
        return $"L={Flag(left)} R={Flag(right)}";
    }

    /// <summary>
    /// 光栅报警收口（V0.0.63 客户口径"固化计时不需要变化，报警复位就可以"）：
    /// 仅 Curing 相位有意义。亮起：红灯独亮（黄绿灭——三色灯"红=禁止靠近"现场
    /// 语义）+ 一条边沿日志；复位：红灯灭、黄灯复亮 + 一条边沿日志。报警态
    /// （rt.AlarmGrill）就是状态比对闸——重复遮挡/重复让空边沿在这里短路，
    /// 日志只记边沿不刷屏（铁律 4）。写灯失败不记账 → 下轮 Pump 报警分支
    /// 补写自愈（与黄灯补写同性质）。复位不动计时（StartedAt 原样）。
    /// manual（V0.0.69）：解除来源口径——缺省 false=光栅自动复位链（原文案）；
    /// true=操作员在"报警复位"窗点【解除报警】（文案"人工复位确认"+当时
    /// 光栅读数），现场对账一眼分清自动/人工。
    /// </summary>
    private void SetGrillAlarm(SlotRuntime rt, int rack, int level, bool on,
        bool manual = false)
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
            _log(manual
                ? $"架{rack}层{level} 人工复位确认（{GrillSideText(rack, level)}），报警解除，计时继续"
                : $"架{rack}层{level} 光栅让空，报警复位（红灯灭），计时继续");
        }
        RefreshMachineBeacon(); // 机台塔灯同步：报警→红+蜂鸣，复位→黄（计时继续，蜂鸣即停）
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
