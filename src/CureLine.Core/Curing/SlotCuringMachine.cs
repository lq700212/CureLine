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
//   BlockedOn      = 遮挡=ON：ON=被挡、OFF=让空——常开型光栅口径；
//   BlockedOff（缺省）= 遮挡=OFF：OFF=被挡、ON=让空——现场实测常闭安全光栅
//     （V0.0.99 用户现场实测拍板：现场为常闭，全项目缺省一律按本档解释；
//     需要常开时在策略窗光栅条切【常开】，随策略激活生效）。
//   本类只认"被挡/让空"语义（IsBlocked 一处解释原始电平），门控/报警/复位/
//   排障日志全部经它判定——加新光栅品牌先看枚举注释，别在调用点散写
//   "== true"（极性切换即全部反相）。provider 与复位模式同款：边沿/心跳
//   每拍现取，换策略/激活后下一拍生效；缺省 null=恒 BlockedOff（Core 独立/
//   测试 API 缺省同口径——程序①现场由激活策略的 GrillPolarity 决定）。
//
// 复位模式（V0.0.69，用户拍板二选一，见 GrillAlarmResetMode）：
//   Auto = 自动复位：光栅让空（边沿+Pump 兜底双保险）即自动复位；
//   Manual（现场缺省，V0.0.71 用户拍板）= 人工复位：让空不复位——
//     红灯与机台蜂鸣保持，边沿与 Pump 兜底都不再自动解除（日志改说"等人工
//     确认"），唯一解除口=宿主按钮链调 ManualResetGrillAlarm（程序①"报警
//     复位"窗逐项/全部确认）；到点放行（绿灯接替）与取件离开（自然回空闲）
//     路径人工档也保留——报警只管"人能不能靠近"，固化计时/放行独立运作
//     （客户口径"计时不变"）。
//   模式经构造注入 Func<GrillAlarmResetMode> provider（缺省 null=恒 Auto，
//   这是 Core 独立使用/测试的 API 缺省；程序①现场由激活策略的
//   GrillResetMode 决定=Manual），边沿/心跳每拍现取——换策略/激活后下一拍
//   即生效（勿缓存进字段）。
//
// 报警总开关（V0.0.90，用户拍板"关闭则不报警，默认开启"）：
//   grillAlarmEnabledProvider（可空，每拍现取）= false 时整条报警链静默——
//   计时中被挡不再亮红灯/蜂鸣/进报警复位窗，已挂着的报警当场解除（写
//   "报警总开关已关闭"日志，红灯灭黄灯复亮）；重新开启后下一拍兜底补
//   报警（关闭期间被挡的边缘不再漏报）。注意开关只关"报警"——门控闸
//   （托盘到位要光栅让空才开计时）是工艺防呆，不受本开关影响。null=
//   恒开启（Core 独立/测试缺省——既有行为零改动；程序①现场由激活策略的
//   GrillAlarmEnabled 决定）。
//
// 上料抑制（V0.0.97，用户拍板"上料时不该报警"；V0.0.99 扩到"完全没扫码"）：
//   loadPendingProvider（可空，每拍现取）= true 时**该库位的上料流程还没
//   做完**（操作员已扫库位码、货还没上完——手和产品会挡住光栅，那是正常动作，
//   报警是误报；连库位码都还没扫的托盘也算——手还没离开就挡了光栅）。
//   此期间该库位的光栅报警整条链静默：不亮红灯、不记报警日志、不进报警
//   复位窗；上料确认（二次扫同库位码）后下一拍恢复正常报警——被挡边沿若
//   发生在抑制期间会被吞掉，Pump 的补报兜底会在抑制解除后的下一拍按快照
//   补报（与报警总开关同款双保险）。
//   null=恒不抑制（Core 独立/测试缺省——既有行为零改动；程序①现场由
//   扫码会话的上料确认状态决定）。
//
// 库位占用（V0.0.99，扫码防呆用）：IsSlotOccupied 查"该库位托盘在架且已
//   进入固化流程（等门控/计时中/已放行待取/已终止）"——往这样的库位再
//   添新产品会让同库位产品的固化起点不同、时间不统一（现场红线）→ 扫码
//   时直接拦截。托盘刚放上还没扫码（绑定记录为空）不算占用。
//
// 报警终止（V0.0.97）：报警弹窗里操作员确认"确实取出了产品"后，可选择
//   **终止计时**——该库位整个产品的固化计时就此停表（不再等到点放行），
//   实际已固化时长入库供追溯。终止后该库位进 Terminated 相位（黄灯灭、
//   不放行绿灯），托盘离开时按"实际经过时长"记账。
//
// 线程模型（铁律 2）：灯写 WritePoint 是网络 IO，绝不能进 UI 线程——
//   本类全部动作发生在后台：OnPointChanged 由宿主从总线后台事件转发进来，
//   Pump 由内部 1s 心跳 Timer（后台线程）调；测试注入虚拟时钟手动 Pump
//   （Timer 调 Pump(固定虚拟时间) 幂等无副作用，不干扰测试确定性）。
//   所有可变状态收在 _sync 锁内，三个入口（边沿/心跳/查询）并发安全。
//
// 锁瘦身·延迟 IO（V0.0.109，构造 deferLampWrites=true 时启用；缺省 false=
//   同步直写老行为，Core 独立/既有测试零改动）：
//   【为什么】_sync 是全机大锁，UI 500ms 拍要抢 31 次（GetStatus×30 +
//   GetAlarmSlots）——库 DedupEffectSink 类头铁律"apply 执行必须在锁外"
//   曾被宿主外层大锁架空：Pump 补写浪潮（开窗/Attach 清账/断连恢复后
//   几十盏 × RMW×300ms 超时）全在锁内逐盏打网络，UI 抢不到锁整段冻结
//   （"打开后卡一阵"的根因）。defer 模式把真实 IO 全部移出 _sync：
//   ① 灯写：EnsureLamp 的 apply 换 QueueLampWrite——锁内只入队
//     （pending 字典 latest-wins 覆盖，同 key 只留最新期望值）并立即返回
//     true 让库记账；方法尾锁外 DrainLampWrites() 逐项真写，写失败/异常
//     → Invalidate(key) 清账 → 下一拍 Pump 的 EnsureLamp 账本不匹配重新
//     入队补写——与库"失败不记账下轮补写"完全同构，库零改动。
//   ② 落盘：PersistState 拆两半——锁内 RequestPersist() 只置标志（微秒），
//     方法尾锁外 FlushPersist() 短锁重建全量快照后写盘（latest-wins：
//     flush 期间又有变化就下一轮 flush 再写，最终状态一致，断电续算
//     语义不变；写盘次数从"每变化点一次"收敛为"每入口尾一次"，只减不增）。
//   挂点=每个可能 EnsureLamp/RequestPersist 的公开方法 lock 块结束后
//   FlushDeferredIo()（灯 drain + 落盘 flush 一起收口，Interlocked 防重入：
//   两线程同时收口时后到者跳过，前者的 batch 循环会把后来入队的项一起
//   处理完）。同线程串行真写——补写浪潮时长不变，只是不再挡 UI 抢锁。
//
// 自愈：
//   ① 灯写"期望 vs 已写"比对（库 DedupEffectSink 期望值簿，V0.0.106 原语化
//      接入——机制本体下沉 Kaleidoscope.Flowing，见下"库原语接入注记"），
//      写失败不记账 → 下轮 Pump 自动补写；稳态下零网络写（去重）。写失败
//      本身不记日志——总线 Error 事件已报通讯问题，这里静默重试防刷屏（铁律 4）。
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
// 库原语接入注记（V0.0.106，状态机三期：本项目血泪沉库后回接，库 V1.10.0）：
//   ① 政策源 PolicySource<T>：复位模式/光栅极性/报警总开关/固化策略四个
//     "每拍现取勿缓存"的 provider 收口成库类型——纪律由类型承载（Current
//     每次现调 provider 绝不缓存；provider 缺位回缺省，异常照旧透传）。
//     带参查询（_loadPendingProvider/_resumeBaselineProvider，Func<r,l,…>）
//     与函数桩（_clock/_durationText，缺省=活时钟/缺省格式化）形态不对口，
//     留宿主勿硬套。
//   ② 执行器 DedupEffectSink<IoPointKey,bool>：手写账本 _writtenLamps 换库
//     （同值跳过/失败不记账下轮补写，三态语义零改动）；apply 走
//     WriteLampThroughBus（异常记边沿日志返 false——保住"写灯异常"排障
//     口径）；库失败日志口传 Debug 流，正式日志保持静默（断连期防刷屏，
//     铁律 4）。锁序红线：本类 _sync → 库内部锁单向嵌套，绝不反向。
//   ③ 【新行为，缺口补上】账本清口 InvalidateLampBook：换总线（Attach）与
//     断连恢复/重启对齐（AlignFromSnapshot）时清账——耦合器断电重启后灯
//     实际全灭而账本仍记"已写"，原实现无清账口，会挂"账上亮着实际灭着"
//     的陈旧窗口；清账后下轮 Pump 对已记账灯全量补写一轮（几十盏量级），
//     稳态去重随后恢复零网络写。
//   ④ 信号源 ISignalSource/PollingSignalSource：IoBus 的轮询契约（先整拍
//     换快照再发边沿，V0.0.64 血泪）即库基类的提炼来源——总线骨架（Task
//     停机 Wait(2000)/停机静默阀/5s 重连节流/错误 10s 去重）是现场实证
//     防线且强于库基类停机口（Join 500ms 限时），不换骨架只做契约对位
//     （见 IoBus 类头注记）；今后新信号源一律继承库基类，勿再手写轮询契约。
//
// 怎么改：新增工艺分支（如暂停计时）在 SlotPhase 加状态 + OnPointChanged/
//   Pump 两处推进逻辑；灯色语义（黄=计时绿=放行）改动要同步
//   RackSlotViewModel 的展示口径与 CuringPolicy 注释。
// =========================================================================

using System.Diagnostics;
using CureLine.Core.Comms;
using Kaleidoscope.Flowing;

namespace CureLine.Core.Curing;

/// <summary>
/// 仓位流程状态。Idle=空闲；WaitingGate=托盘在但等光栅让空；Curing=计时中；
/// Done=到点放行；Terminated=报警确认后人工终止计时（V0.0.97）。
/// </summary>
public enum SlotPhase
{
    Idle,
    WaitingGate,
    Curing,
    Done,
    Terminated,
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
/// <remarks>
/// Terminated（V0.0.97）：报警确认后人工终止计时的标志（Phase=Terminated）。
/// 第 5 参缺省 false：既有构造点零改动。
/// </remarks>
public sealed record SlotStatus(SlotPhase Phase, int TotalMinutes, int RemainMinutes,
    bool AlarmGrill = false, bool Terminated = false);

public sealed class SlotCuringMachine : IDisposable
{
    /// <summary>策略缺失时的兜底时长（策略库空/没激活；正常运行时策略自愈，几乎走不到）。</summary>
    private const int FallbackMinutes = 120;

    private readonly object _sync = new();
    /// <summary>
    /// 固化策略政策源（库原语 PolicySource，V0.0.106）：构造必给 provider，
    /// 每次读 Current 现调绝不缓存（策略窗热切换即生效）；provider 返回
    /// null=没策略（消费点自判兜底）。异常照旧透传（与原手写口径一致）。
    /// </summary>
    private readonly PolicySource<CuringPolicy> _policy;
    private readonly Func<DateTimeOffset> _clock;   // 函数桩（缺省=活时钟），非政策值，不入 PolicySource
    private readonly Action<string> _log;
    /// <summary>日志时长文本格式化（宿主注入跟随显示单位；null=缺省"X 分钟"口径）。
    /// 函数桩形态（缺省=缺省格式化行为），非政策值，不入 PolicySource。</summary>
    private readonly Func<int, string>? _durationText;
    /// <summary>
    /// 报警复位模式政策源（库原语，V0.0.106 收口；V0.0.69 起每拍现取）：
    /// null provider=恒 Auto——Core 独立/测试缺省，程序①现场由激活策略的
    /// GrillResetMode 决定=Manual；策略激活/切换后下一拍即生效（Current
    /// 每次现调绝不缓存）；provider 须线程安全纯读（后台线程逐拍调）。
    /// </summary>
    private readonly PolicySource<GrillAlarmResetMode> _resetMode;
    /// <summary>
    /// 光栅信号极性政策源（库原语，V0.0.106 收口；V0.0.72 起每拍现取）：
    /// null provider=恒 BlockedOff（常闭）——Core 独立/测试缺省同口径，
    /// 程序①现场由激活策略的 GrillPolarity 决定；provider 须线程安全纯读。
    /// </summary>
    private readonly PolicySource<GrillSignalPolarity> _grillPolarity;
    /// <summary>
    /// 报警总开关政策源（库原语，V0.0.106 收口；V0.0.90 起每拍现取）：
    /// null provider=恒开启——Core 独立/测试缺省，程序①现场由激活策略的
    /// GrillAlarmEnabled 决定；provider 须线程安全纯读（后台线程逐拍调）。
    /// </summary>
    private readonly PolicySource<bool> _grillAlarmEnabled;
    /// <summary>
    /// 上料未确认提供者（V0.0.97；null=恒不抑制——Core 独立/测试缺省，
    /// 程序①现场由扫码会话的上料确认状态决定）。true=该库位正在上料
    /// （操作员已扫库位码、货还没上完），此期间该库位的光栅报警整条链
    /// 静默（手挡光栅是上料正常动作，报了是误报）。边沿/心跳每拍现取；
    /// provider 须线程安全（后台线程逐拍调）。
    /// 【带参查询，留宿主】Func&lt;r,l,…&gt; 形态非无参政策值，PolicySource
    /// 不对口（库原语接入注记①）。
    /// </summary>
    private readonly Func<int, int, bool>? _loadPendingProvider;
    /// <summary>
    /// 续接基线提供者（V0.0.99，可空，每拍现取）= 该库位本轮"进入前累计已固化
    /// 分钟"的最小值（扫码会话登记的续接件前段时长）；null=恒 0=不缩短。
    /// 干什么用：中途取出再加工的产品放回来时选【接着之前固化继续】——
    /// 之前已固化 8 小时、计划 24 小时，本盘只需再固化 16 小时即累计达标
    /// （累计达标口径）。基线取各续接件的**最小值**——保证库里每件产品的
    /// 累计固化都 ≥ 计划时长（宁多勿少：固化不足是质量事故）。
    /// 托盘到位那一刻现读（绑了续接件就缩短本盘时长）；后台线程调用，
    /// provider 须线程安全纯读。
    /// 【带参查询，留宿主】同 _loadPendingProvider（库原语接入注记①）。
    /// </summary>
    private readonly Func<int, int, int>? _resumeBaselineProvider;
    private readonly Dictionary<(int Rack, int Level), SlotRuntime> _slots = new();
    /// <summary>
    /// 灯写执行器（库原语 DedupEffectSink，V0.0.106 换库）：期望值簿（同值
    /// 跳过稳态零网络写 / 失败不记账下轮 Pump 补写），机制本体下沉
    /// Kaleidoscope.Flowing——原手写 Dictionary 账本退役。apply=
    /// WriteLampThroughBus（异常记边沿日志返 false）；库失败日志口传 Debug
    /// 流保持正式日志静默（断连期防刷屏，铁律 4）。清账口=InvalidateLampBook
    /// （Attach/AlignFromSnapshot 调，见类头注记③）。
    /// </summary>
    private readonly DedupEffectSink<IoPointKey, bool> _lampSink;
    private IPointTableBus _bus;
    private Timer? _pumpTimer;

    // ---------- 锁瘦身·延迟 IO（V0.0.109，见类头"锁瘦身·延迟 IO"节） ----------
    /// <summary>true=灯写与落盘全部移出 _sync 锁（生产宿主启用）；false=同步直写
    /// 老行为（Core 独立/既有测试缺省，用例零改动）。</summary>
    private readonly bool _deferLampWrites;
    /// <summary>延迟灯写队列闸：只保护字典换批，不含真写（真写在 _sync 外）。</summary>
    private readonly object _lampQueueLock = new();
    /// <summary>待写真灯（锁内入队 latest-wins：同 key 只留最新期望值，防乱序）。
    /// drain 线程整批换走后逐项写——入队与换批都在 _lampQueueLock 内。</summary>
    private Dictionary<IoPointKey, bool> _lampPending = new();
    /// <summary>drain 防重入标志（0/1）：并发收口时后到者跳过，前者的循环
    /// 会把后来入队的项一起处理完。</summary>
    private int _draining;
    /// <summary>状态落盘待办标志 0/1（锁内 Interlocked 置位，锁外 flush 取走）
    /// ——A2：锁内不再直接 File.WriteAllText。int+Interlocked（bool 过不了
    /// Interlocked 泛型约束）：置位/取走都是原子且带全屏障，跨线程可见无虞。</summary>
    private int _persistPending;
    /// <summary>落盘 flush 防重入标志（0/1），与 drain 各自独立。</summary>
    private int _flushingPersist;

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

    /// <summary>
    /// 光栅报警产生（V0.0.97；参数=架、层）。宿主据此给追溯库该库位的
    /// 未结束记录盖"经历过报警"标记（现场回查"这个库位报警过没有"用）。
    /// 只在报警**真正亮起**的那一次触发（状态去重，重复遮挡不重复发）。
    /// </summary>
    public event Action<int, int>? GrillAlarmRaised;

    /// <summary>单仓位运行态（内部可变，只在 _sync 锁内动）。</summary>
    private sealed class SlotRuntime
    {
        public SlotPhase Phase;
        public DateTimeOffset? StartedAt; // Curing 起点（时钟可注入，测试虚拟推进）
        public int TotalMinutes;          // 开计时时锁定的时长（在制热更单向上调，见 ApplyPolicyHotChange）
        public bool AlarmGrill;           // Curing 中光栅被挡的报警态（红灯亮着）——瞬时安全态不落盘
        public DateTimeOffset? TerminatedAt; // 报警确认后人工终止计时的时刻（V0.0.97；算实际固化时长用）
    }

    /// <summary>
    /// 构造即起 1s 心跳（后台线程）。bus 可后续经 Attach 换（重连不丢状态）；
    /// policyProvider 每次托盘到位现取（支持策略窗热切换）；clock/log/durationText
    /// 可注入（测试：clock/log；显示单位：durationText——缺省 null 走"X 分钟"）。
    /// durationText 在后台线程逐条调用，须线程安全（纯函数即可）。
    /// resetModeProvider（V0.0.69，可空）=报警复位模式每拍现取（null=恒自动
    /// 复位——Core 独立/测试缺省；程序①现场由激活策略的 GrillResetMode 决定，
    /// 宿主传"读激活策略"的 lambda，换策略/激活无需重建状态机）。
    /// grillPolarityProvider（V0.0.72，可空）=光栅信号极性每拍现取（null=恒
    /// BlockedOff 遮挡=OFF——常闭安全光栅，V0.0.99 用户现场实测拍板为全项目
    /// 缺省；"常开/常闭"由策略窗光栅条编辑并随策略激活，宿主同样传"读激活
    /// 策略"的 lambda，无需重建状态机）。
    /// grillAlarmEnabledProvider（V0.0.90，可空）=报警总开关每拍现取（null=
    /// 恒开启——Core 独立/测试缺省；程序①现场由激活策略的 GrillAlarmEnabled
    /// 决定，false=整条报警链静默，见类头"报警总开关"）。
    /// loadPendingProvider（V0.0.97，可空）=上料未确认每拍现取（null=恒不
    /// 抑制——Core 独立/测试缺省；程序①现场=扫码会话的上料确认状态，
    /// true=该库位正在上料，此期间不报光栅报警，见类头"上料抑制"）。
    /// resumeBaselineProvider（V0.0.99，可空）=续接基线每拍现取（null=恒 0
    /// =不缩短；程序①现场=扫码会话登记的续接件前段时长，本盘时长 =
    /// 计划 − 基线，累计达标口径）。
    /// deferLampWrites（V0.0.109，缺省 false）=锁瘦身高档：true=灯写与落盘
    /// 全部移出 _sync 锁（构造签名见类头"锁瘦身·延迟 IO"）——生产宿主
    /// 启用（UI 500ms 拍抢锁不再被补写浪潮挡住）；false=同步直写老行为
    /// （Core 独立/既有测试缺省，用例与灯写时序断言零改动）。
    /// </summary>
    public SlotCuringMachine(IPointTableBus bus, Func<CuringPolicy?> policyProvider,
        Func<DateTimeOffset>? clock = null, Action<string>? log = null,
        Func<int, string>? durationText = null,
        Func<GrillAlarmResetMode>? resetModeProvider = null,
        Func<GrillSignalPolarity>? grillPolarityProvider = null,
        Func<bool>? grillAlarmEnabledProvider = null,
        Func<int, int, bool>? loadPendingProvider = null,
        Func<int, int, int>? resumeBaselineProvider = null,
        bool deferLampWrites = false)
    {
        _bus = bus;
        // 四个无参政策值收口库 PolicySource（构造签名不变——provider 仍按原
        // 形态注入，fallback=原手写缺省口径，见字段注释与类头注记①）。
        // null!=策略可空语义（provider 返 null=没策略，消费点自判）；provider
        // 包装层 ! 断言收敛泛型替换后的可空性差异警告（运行时照传 null）。
        _policy = new PolicySource<CuringPolicy>(() => policyProvider()!, null!);
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log ?? (_ => { });
        _durationText = durationText;
        _resetMode = new PolicySource<GrillAlarmResetMode>(resetModeProvider, GrillAlarmResetMode.Auto);
        _grillPolarity = new PolicySource<GrillSignalPolarity>(grillPolarityProvider, GrillSignalPolarity.BlockedOff);
        _grillAlarmEnabled = new PolicySource<bool>(grillAlarmEnabledProvider, true);
        _loadPendingProvider = loadPendingProvider;
        _resumeBaselineProvider = resumeBaselineProvider;
        // 灯写执行器换库（V0.0.106）：apply 走 WriteLampThroughBus（异常记
        // 边沿日志返 false，保住"写灯异常"排障口径）；库失败日志口传 Debug
        // 流——正式日志保持静默（写失败=总线 Error 事件已报通讯层故障，
        // 断连期逐拍刷屏违反铁律 4，诊断走 Debug 流与库 FailedCount）。
        // V0.0.109 锁瘦身高档（deferLampWrites=true）：apply 换 QueueLampWrite
        // ——锁内只入队立即记账，真写由方法尾锁外 DrainLampWrites 执行，
        // 失败 Invalidate 清账下轮补写（语义同构，见类头）。
        _deferLampWrites = deferLampWrites;
        _lampSink = new DedupEffectSink<IoPointKey, bool>(
            _deferLampWrites ? QueueLampWrite : (Func<IoPointKey, bool, bool>)WriteLampThroughBus,
            m => Debug.WriteLine(m));
        _pumpTimer = new Timer(_ => SafePump(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 换总线（重连/点表变更后）：流程状态保留，只换读写通道。
    /// 【V0.0.106】换通道时清灯写账本——新总线上灯态未知（耦合器可能换过/
    /// 断电重启过），旧账"已写"全作废，下轮 Pump 全量补写一轮（见类头注记③）。
    /// </summary>
    public void Attach(IPointTableBus bus)
    {
        lock (_sync)
        {
            _bus = bus;
            InvalidateLampBook();
        }
        // defer 档：清账后的全量补写浪潮在锁外逐盏真写（几十盏 × RMW）——
        // UI 抢锁不再被挡；写失败自动清账由 Pump 兜底补写。
        FlushDeferredIo();
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
            // defer 档：到位/离开边沿的灭灯/亮灯写总线移到锁外（见类头"锁瘦身"）
            FlushDeferredIo();
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
                else if (rt.Phase == SlotPhase.Curing && !rt.AlarmGrill && AlarmEnabled() &&
                         !LoadPending(key.Rack, key.Level))
                {
                    // 两个静默闸（任一成立即不报警，零写灯零日志）：
                    // ① 【报警总开关 V0.0.90】关闭态：被挡边沿吞掉；重开开关后
                    //    由 Pump 兜底按快照补报（见 Pump Curing 分支），不至于
                    //    下一次光栅动作才想起来。
                    // ② 【上料抑制 V0.0.97】该库位正在上料（货还没上完，手挡
                    //    光栅是正常动作）——抑制解除（上料确认）后由 Pump 补报
                    //    兜底按快照补报，不至于漏报。
                    SetGrillAlarm(rt, key.Rack, key.Level, on: true);
                }
            }
            // defer 档：报警亮灯/复位灯写移到锁外（见类头"锁瘦身"）
            FlushDeferredIo();
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
                            RequestPersist(); // 状态变化落盘（开关开时；锁内置标志，锁外 FlushPersist 写盘）
                        }
                        else if (rt.AlarmGrill && !AlarmEnabled())
                        {
                            // 【报警总开关 V0.0.90】关闭态兜底解除：挂在旧报警直接收场
                            // （红灯灭黄灯复亮，日志注明缘由）。倒的第一道口是宿主在
                            // 策略激活/切换后立即调 ApplyAlarmSwitch（不等拍），
                            // 本兜底保证漏接（provider 异常/时序）最迟 1 秒也收敛
                            // ——与复位兜底同款双保险。
                            SetGrillAlarm(rt, slot.Rack, slot.Level, on: false,
                                offNote: "报警总开关已关闭");
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
                        else if (!GrillClear(slot.Rack, slot.Level) && AlarmEnabled() &&
                                 !LoadPending(slot.Rack, slot.Level))
                        {
                            // 【报警总开关 V0.0.90】重开开关的补报兜底：关闭期间被挡的
                            // 边沿被吞（没进报警态），开关重新打开后不再等下一次光栅
                            // 动作——下一拍按快照补报（与 StartCuring 开计时复核同语义
                            // ：计时中被挡=该报警）。关闭态走到这里=继续静默零网络写。
                            // 【上料抑制 V0.0.97】上料中同样静默：抑制解除（上料确认）
                            // 后的下一拍，这里会按快照把漏掉的报警补上——不漏报。
                            SetGrillAlarm(rt, slot.Rack, slot.Level, on: true);
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
                    case SlotPhase.Terminated:
                        // 【报警终止 V0.0.97】计时已停表：不等到点放行（不放绿灯），
                        // 黄灯灭；红灯保持到报警被解除（灯色归报警态管）。
                        EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampYellow, false);
                        EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampGreen, false);
                        if (rt.AlarmGrill)
                            EnsureLamp(slot.Rack, slot.Level, IoPointRole.LampRed, true);
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
            ApplyAlarmSwitch(); // 报警总开关每拍兜底（V0.0.90：关闭态现存报警最迟 1 秒收敛）
        }
        // defer 档（V0.0.109）：每拍的灯写补写自愈与落盘都在锁外执行——
        // 补写浪潮（开窗/清账/断连恢复后几十盏 × RMW×300ms）不再挡 UI 抢锁。
        FlushDeferredIo();
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
        var policy = _policy.Current;
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
                RequestPersist(); // 延长落盘（锁内置标志），断电恢复按新时长续算
        }
        // defer 档：延长后的落盘在锁外写盘（见类头"锁瘦身"）
        FlushDeferredIo();
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

            // 【V0.0.106】对齐即清灯写账本：断连恢复/重启对齐意味着耦合器可能
            // 断电重启过（灯实际全灭而账本仍记"已写"）——不清账会挂"账上亮着
            // 实际灭着"的陈旧窗口。清账后本轮对齐与下轮 Pump 按相位全量补写
            // （RestoreFromRecord/Pump 各分支的 EnsureLamp 稳态去重随后恢复）。
            InvalidateLampBook();

            // 持久化恢复准备：开关开才读记录（开关关=零文件 IO，行为与不持久化完全一致）。
            // 记录合法性过滤：时长 ≤0 或 Curing 缺起点 → 视为脏数据丢弃走重计。
            Dictionary<(int Rack, int Level), CuringSlotRecord>? saved = null;
            if (_policy.Current is { PersistMinutes: true })
            {
                var reasons = new List<string>();
                saved = CuringStateStore.Load(reasons)?.Slots
                    .Where(r => r.Minutes > 0 && (
                        r.Phase == nameof(SlotPhase.Done) ||
                        r.Phase == nameof(SlotPhase.Terminated) ||
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
            // defer 档：对齐后的全量补写浪潮在锁外逐盏真写（几十盏 × RMW）
            FlushDeferredIo();
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
        if (rec.Phase == nameof(SlotPhase.Terminated))
        {
            // 【报警终止 V0.0.97】断电前已人工终止计时——恢复成终止态（不放行绿灯、
            // 不续算），产品留在架上等取件，固化时长按断电前记录（不足计划时长）。
            rt.Phase = SlotPhase.Terminated;
            rt.StartedAt = rec.StartedAt;
            rt.TotalMinutes = rec.Minutes;
            rt.TerminatedAt = rec.TerminatedAt ?? rec.StartedAt;
            EnsureLamp(rack, level, IoPointRole.LampRed, false);
            EnsureLamp(rack, level, IoPointRole.LampYellow, false);
            EnsureLamp(rack, level, IoPointRole.LampGreen, false);
            _log($"架{rack}层{level} 重启恢复：断电前已终止计时，保持终止态（不再计时）");
            return;
        }
        // Curing：按记录起点续算——断电期间流逝的时间自然计入固化时长
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = rec.StartedAt;
        rt.TotalMinutes = rec.Minutes;
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        EnsureLamp(rack, level, IoPointRole.LampRed, false); // 防残留红灯（报警态不落盘，见下）
        if (!GrillClear(rack, level) && AlarmEnabled() && !LoadPending(rack, level))
            SetGrillAlarm(rt, rack, level, on: true); // 续算恢复即复核：人手仍在架内 → 重新进报警态（报警总开关关则静默，V0.0.90；上料中则静默，V0.0.97）
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
            return new SlotStatus(rt.Phase, rt.TotalMinutes, remain, rt.AlarmGrill,
                rt.Phase == SlotPhase.Terminated);
        }
    }

    /// <summary>
    /// 当前报警复位模式（库政策源薄壳 V0.0.106——"每拍现取勿缓存"纪律由
    /// PolicySource 类型承载：Current 每次现调 provider）：null provider=恒
    /// Auto（Core 独立/测试缺省；程序①现场由激活策略的 GrillResetMode 决定）。
    /// 后台线程（边沿/心跳）逐次调用——provider 必须线程安全纯读（宿主传
    /// "读激活策略"的 lambda，见 MainViewModel.GetActivePolicy）。
    /// </summary>
    private GrillAlarmResetMode ResetMode() => _resetMode.Current;

    /// <summary>
    /// 当前光栅信号极性（V0.0.72；库政策源薄壳 V0.0.106，每拍现取由 Current
    /// 承载）：null provider=恒 BlockedOff（遮挡=OFF——常闭安全光栅，V0.0.99
    /// 用户现场实测拍板为全项目缺省；Core 独立/测试缺省同口径）。程序①现场
    /// 由激活策略的 GrillPolarity 决定。后台线程（边沿/心跳）逐次调用——
    /// provider 必须线程安全纯读。
    /// </summary>
    private GrillSignalPolarity Polarity() => _grillPolarity.Current;

    /// <summary>
    /// 当前报警总开关（V0.0.90；库政策源薄壳 V0.0.106，每拍现取由 Current
    /// 承载）：null provider=恒开启（Core 独立/测试缺省；程序①现场由激活
    /// 策略的 GrillAlarmEnabled 决定）——false=整条报警链静默（被挡不报、
    /// 已挂的立即解除），门控判定不受影响。后台线程（边沿/心跳）逐次调用——
    /// provider 必须线程安全纯读（宿主传"读激活策略"的 lambda）。
    /// </summary>
    private bool AlarmEnabled() => _grillAlarmEnabled.Current;

    /// <summary>
    /// 该库位是否正在上料（V0.0.97，每拍现取 provider，勿缓存）：true=该库位的
    /// 上料流程还没做完（已扫库位码货还没上完，或连库位码都没扫）——手和产品
    /// 会挡住光栅，那是上料正常动作，报警是误报。此期间该库位不报光栅报警
    /// （整条报警链静默）。
    /// null provider=恒不抑制（Core 独立/测试缺省——既有行为零改动；
    /// 程序①现场由扫码会话的上料确认状态决定）。
    /// 后台线程（边沿/心跳）逐次调用——provider 必须线程安全纯读。
    /// </summary>
    private bool LoadPending(int rack, int level) =>
        _loadPendingProvider?.Invoke(rack, level) ?? false;

    /// <summary>
    /// 该库位是否"托盘在架且已进入固化流程"（V0.0.99，扫码防呆用）：
    /// 相位为等门控 / 计时中 / 已放行待取 / 已终止都算——这四种都意味着
    /// "这一盘已经定型了"，往里再添新产品会让同库位产品的固化起点不同、
    /// 固化时间不统一（现场红线）→ 扫码时必须拦截。
    /// 空闲（托盘不在架或托盘刚放上未过门控）不算占用——那是正常上料流程。
    /// 线程：任意线程可调（锁内读，与 GetStatus 同款）。
    /// </summary>
    public bool IsSlotOccupied(int rack, int level)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue((rack, level), out var rt))
                return false;
            return rt.Phase == SlotPhase.WaitingGate
                || rt.Phase == SlotPhase.Curing
                || rt.Phase == SlotPhase.Done
                || rt.Phase == SlotPhase.Terminated;
        }
    }

    /// <summary>
    /// 人工复位确认（V0.0.69，宿主"报警复位"窗【解除报警】按钮的唯一解除口）：
    /// 报警态的仓位解除报警（红灯灭、黄灯复亮、机台蜂鸣停），计时不动。
    /// 不要求光栅已让空——操作员点按钮=人工确认现场已处理，按钮是权威；
    /// 当时光栅读数进日志（人工复位行）供追溯。
    /// 返回 true=确实复位了；false=该仓位不在报警态（已复位/流程已走/未知
    /// 仓位，本身就是幂等安全——重复点按钮无害）。
    /// 线程：任意线程可调（锁内动作；宿主从后台 Task 调，勿在 UI 线程直调——
    /// 内部 EnsureLamp 是写灯网络 IO，铁律 2；defer 高档 V0.0.109 锁内只入队
    /// 真写移锁外，宿主调用线程口径不变）。
    /// </summary>
    public bool ManualResetGrillAlarm(int rack, int level)
    {
        bool reset;
        lock (_sync)
        {
            if (!_slots.TryGetValue((rack, level), out var rt) || !rt.AlarmGrill)
            {
                reset = false;
            }
            else
            {
                SetGrillAlarm(rt, rack, level, on: false, manual: true);
                reset = true;
            }
        }
        // defer 档：复位灯写（红灯灭/黄灯复亮/蜂停）在锁外真写，复位链路零锁内网络 IO
        if (reset)
            FlushDeferredIo();
        return reset;
    }

    /// <summary>
    /// 终止该库位的固化计时（V0.0.97，报警弹窗"确实取出了产品 + 终止计时"
    /// 分支的唯一出口）：该库位**整个产品的固化计时就此停表**——不再等到点
    /// 放行（不放绿灯），实际已固化时长随出参返回给调用方入库追溯
    /// （不足计划时长=产品被提前取走，质量追溯最关心的就是这个数）。
    ///
    /// 为什么不做"只停取出的那几件"：状态机按库位计时（一个库位一个托盘），
    /// 单件产品的计时由追溯库逐件记账（MarkTakenOut）；库位级只有"继续 /
    /// 终止"两个口径。
    ///
    /// 报警态不受影响：终止计时不等于解除报警，红灯与蜂鸣照旧（解除口仍是
    /// 人工复位确认）——现场口径"报警归报警，计时归计时"两件事分开确认。
    ///
    /// 返回 false=该库位不在计时中（已放行/已终止/空位，无需终止——幂等安全，
    /// 重复点按钮无害）。curedSeconds=实际已固化秒数（未开计时=0）。
    /// 线程：任意线程可调（锁内动作；内部 EnsureLamp 是写灯网络 IO，勿在 UI
    /// 线程直调——宿主从后台 Task 调）。
    /// </summary>
    public bool TerminateCuring(int rack, int level, out int curedSeconds)
    {
        bool terminated;
        lock (_sync)
        {
            curedSeconds = 0;
            if (!_slots.TryGetValue((rack, level), out var rt) || rt.Phase != SlotPhase.Curing)
            {
                terminated = false;
            }
            else
            {
                if (rt.StartedAt is { } start)
                    curedSeconds = Math.Max(0, (int)(_clock() - start).TotalSeconds);
                rt.Phase = SlotPhase.Terminated;
                rt.TerminatedAt = _clock();
                // 黄灯灭（不再计时）、绿灯不放行；红灯保持（报警未解除）。
                EnsureLamp(rack, level, IoPointRole.LampYellow, false);
                EnsureLamp(rack, level, IoPointRole.LampGreen, false);
                _log($"架{rack}层{level} 人工终止计时（已固化 {MinutesText(curedSeconds / 60)}，" +
                     $"计划 {MinutesText(rt.TotalMinutes)}），产品不再等到点放行");
                RequestPersist(); // 状态变化落盘（开关开时）：断电恢复仍是终止态
                RefreshMachineBeacon(); // 塔灯同步：不再算"固化中"，回待料黄灯
                terminated = true;
            }
        }
        // defer 档：终止灯写与落盘在锁外收口（见类头"锁瘦身"）
        if (terminated)
            FlushDeferredIo();
        return terminated;
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

    /// <summary>
    /// 报警总开关收敛（V0.0.90）：当前开关关闭时把所有现存报警当场解除
    /// （红灯灭、黄灯复亮、机台蜂鸣停、塔灯回正常聚合色，日志注明
    /// "报警总开关已关闭"+ 当时光栅读数）；开启时零动作（免遍历快速路径）。
    /// 调用时机：① 宿主策略激活/切换后立即调一次（MainViewModel.LoadPolicyBook，
    /// 后台 Task.Run——写灯=网络 IO 铁律 2）——不等心跳，红灯当场灭；
    /// ② Pump 每拍兜底（provider 只读、漏接广播也最迟 1 秒收敛）。
    /// 线程：任意线程可调（锁内动作；内部 EnsureLamp 是网络 IO，勿在 UI
    /// 线程直调）。
    /// </summary>
    public void ApplyAlarmSwitch()
    {
        if (AlarmEnabled())
            return;
        lock (_sync)
        {
            foreach (var (slot, rt) in _slots)
            {
                if (!rt.AlarmGrill)
                    continue;
                SetGrillAlarm(rt, slot.Rack, slot.Level, on: false,
                    offNote: "报警总开关已关闭");
            }
        }
        // defer 档：开关关闭的批量解除灯写在锁外真写（Pump 每拍兜底也走这里）
        FlushDeferredIo();
    }

    // ---------- 流转（全部假定已在 _sync 锁内） ----------

    private void OnTrayArrived(SlotRuntime rt, int rack, int level, bool realEdge)
    {
        if (rt.Phase is SlotPhase.Curing or SlotPhase.Done or SlotPhase.Terminated)
            return; // 已在流程中（传感器抖动/重复边沿），不重置计时
        // 真实到位恒发（进站挂点，Arrive 口径）——门控等放行与直接开计时两路
        // 都要发，否则"门控关直接开计时"的仓位进站就漏了；重启对齐（realEdge
        // =false）不发，防 MES 重复过账。
        if (realEdge)
            TrayArrived?.Invoke(rack, level);
        var policy = _policy.Current;
        // 本次实际要固化的时长 = 计划时长 − 续接基线（V0.0.99 累计达标口径：
        // 之前已固化 N 分钟的续接件，本盘只需再固化 计划 − N 即累计达标）。
        int planMinutes = policy?.ResolveMinutes(rack, level) ?? FallbackMinutes;
        int minutes = ApplyResumeBaseline(planMinutes, rack, level);
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

    /// <summary>
    /// 续接基线（分钟）：该库位本轮"进入前累计已固化时长"的最小值——由宿主
    /// 注入（扫码会话的库位续接登记；null=恒 0=不缩短）。
    /// 每拍现取（托盘到位那一刻现读——绑了续接件就缩短本盘时长）。
    /// </summary>
    private int ResumeBaseline(int rack, int level) =>
        _resumeBaselineProvider?.Invoke(rack, level) ?? 0;

    /// <summary>
    /// 计划时长 → 本次实际固化时长（V0.0.99 累计达标口径）：
    /// 本次 = 计划 − 续接基线（各续接件前段时长的最小值，保证库里每件产品的
    /// 累计固化都 ≥ 计划时长——宁多勿少：固化不足是质量事故）。
    /// 下限 1 分钟（0 分钟=秒放行，等于没固化）。
    /// </summary>
    private int ApplyResumeBaseline(int plannedMinutes, int rack, int level)
    {
        int baseline = ResumeBaseline(rack, level);
        if (baseline <= 0)
            return plannedMinutes;
        return Math.Max(1, plannedMinutes - baseline);
    }

    private void StartCuring(SlotRuntime rt, int rack, int level, int? minutes = null, bool realEdge = true)
    {
        var policy = _policy.Current;
        rt.TotalMinutes = minutes ?? ApplyResumeBaseline(
            policy?.ResolveMinutes(rack, level) ?? FallbackMinutes, rack, level);
        rt.Phase = SlotPhase.Curing;
        rt.StartedAt = _clock();
        EnsureLamp(rack, level, IoPointRole.LampYellow, true);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false); // 防残留绿灯
        int baseline = ResumeBaseline(rack, level);
        _log($"架{rack}层{level} 开始固化计时 {MinutesText(rt.TotalMinutes)}（黄灯）" +
            (baseline > 0
                ? $"（续接：前段已固化 {MinutesText(baseline)}，本次累计 {MinutesText(rt.TotalMinutes + baseline)}）"
                : ""));
        if (realEdge)
            CuringStarted?.Invoke(rack, level); // 过账进站挂点（Start 口径，Poster 内按配置取舍）
        // 开计时即复核光栅：门控关（GrillGate=false）的现场人手也可能恰好在架内，
        // 计时一开始就"被挡"——按报警语义立即亮红（SetGrillAlarm 内有状态去重，
        // 正常门控放行路径此刻光栅已让空，走到这里必然零操作）。
        // 【上料抑制 V0.0.97】正在上料的库位不报（手挡光栅是上料正常动作）。
        if (!GrillClear(rack, level) && AlarmEnabled() && !LoadPending(rack, level))
            SetGrillAlarm(rt, rack, level, on: true); // 报警总开关关则静默（V0.0.90）
        RequestPersist(); // 状态变化落盘（开关开时；锁内置标志，锁外 FlushPersist 写盘）
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
            // 【报警终止 V0.0.97】终止时刻 − 开计时时刻 = 实际已固化时长
            // （不够计划时长=提前取走，追溯库据此判"固化不足"）。
            SlotPhase.Terminated when rt.StartedAt is { } s2 =>
                Math.Max(0, (int)((rt.TerminatedAt ?? _clock()) - s2).TotalSeconds),
            _ => 0, // WaitingGate：没开计时
        };
        string note = leavePhase switch
        {
            SlotPhase.Curing => "计时中取件，计时作废",
            SlotPhase.Done => "取件完成",
            SlotPhase.WaitingGate => "托盘离开（未开计时）",
            SlotPhase.Terminated => "终止计时后取件",
            _ => "托盘离开",
        };
        rt.Phase = SlotPhase.Idle;
        rt.StartedAt = null;
        rt.TotalMinutes = 0;
        rt.TerminatedAt = null;
        // 报警态随取件一并消除（人手在架内直接端走托盘的场景）：红灯也让位，
        // 与黄绿同批灭灯——不额外记"复位"日志，取件日志（下方 note）已是本边沿。
        rt.AlarmGrill = false;
        EnsureLamp(rack, level, IoPointRole.LampRed, false);
        EnsureLamp(rack, level, IoPointRole.LampYellow, false);
        EnsureLamp(rack, level, IoPointRole.LampGreen, false);
        _log($"架{rack}层{level} {note}，回空闲");
        RefreshMachineBeacon(); // 机台塔灯同步：取件/取走后全机聚合即时跟随（蜂鸣停，回绿或黄）
        RequestPersist(); // 状态变化落盘（开关开时）：取件仓位从记录中移除
        // 过账出站挂点（唯一调用方 OnPointChanged 恒为真实边沿，无对齐路径）
        TrayLeft?.Invoke(rack, level, leavePhase, curedSeconds);
    }

    // ---------- 计时状态持久化（PersistMinutes 开关，断电续算） ----------

    /// <summary>
    /// 请求落盘（每个状态变化点调用，锁内）——V0.0.109 锁瘦身：只置标志
    /// 不碰文件（File.WriteAllText 移到 FlushPersist 锁外执行）。开关关时
    /// 连标志都不置（零文件 IO，行为与"不持久化"完全一致，缺省口径：宁可重计）。
    /// WaitingGate 不落盘——没开计时，重启后对齐自然重走门控。
    /// </summary>
    private void RequestPersist()
    {
        if (_policy.Current is not { PersistMinutes: true })
            return;
        Interlocked.Exchange(ref _persistPending, 1); // 置待办（锁内原子置位）
    }

    /// <summary>
    /// 落盘收口（锁外调，挂点在 FlushDeferredIo）：开关开且有待办时重建全量
    /// 快照并写盘。latest-wins——flush 期间又有状态变化就下一轮再写（全量
    /// 快照天然幂等，中间态少写一次无害，最终状态一致）；"下次状态变化自动
    /// 重试"的排障口径保持（写失败不清标志？——写失败标志已被取走，但下次
    /// 状态变化会重新置位，与原语义等价）。
    /// </summary>
    private void FlushPersist()
    {
        if (_persistPending == 0)
            return;
        if (Interlocked.Exchange(ref _flushingPersist, 1) == 1)
            return;
        try
        {
            while (Interlocked.Exchange(ref _persistPending, 0) == 1)
            {
                var book = BuildPersistBook(); // 短锁内重建快照（纯内存，微秒级）
                try
                {
                    CuringStateStore.Save(book); // 真文件 IO 在 _sync 锁外
                }
                catch (Exception ex)
                {
                    _log($"计时状态落盘失败（下次状态变化自动重试）：{ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _flushingPersist, 0);
        }
    }

    /// <summary>重建进行中仓位的全量快照（Curing/Done/Terminated 三相位入簿）。
    /// 仅 FlushPersist 调用——短锁内纯内存遍历，绝不做文件 IO。</summary>
    private CuringStateBook BuildPersistBook()
    {
        lock (_sync)
        {
            var book = new CuringStateBook();
            foreach (var (slot, rt) in _slots)
            {
                if (rt.Phase is not (SlotPhase.Curing or SlotPhase.Done or SlotPhase.Terminated))
                    continue;
                book.Slots.Add(new CuringSlotRecord
                {
                    Rack = slot.Rack,
                    Level = slot.Level,
                    Phase = rt.Phase.ToString(),
                    StartedAt = rt.StartedAt ?? _clock(),
                    Minutes = rt.TotalMinutes,
                    TerminatedAt = rt.TerminatedAt,
                });
            }
            return book;
        }
    }

    // ---------- 锁瘦身·延迟灯写（V0.0.109，deferLampWrites=true 启用） ----------

    /// <summary>
    /// 延迟灯写 apply 注入口（替代 WriteLampThroughBus）：锁内只入队（同 key
    /// latest-wins 覆盖，防乱序）并立即返回 true 让库记账——真写延迟到方法尾
    /// 锁外 DrainLampWrites；写失败由其 Invalidate 清账，下一拍 Pump 的
    /// EnsureLamp 账本不匹配重新入队补写——与库"失败不记账下轮补写"完全
    /// 同构。本方法只在 _sync 锁内被库回调（EnsureLamp 链），绝不做网络 IO。
    /// </summary>
    private bool QueueLampWrite(IoPointKey key, bool state)
    {
        lock (_lampQueueLock)
        {
            _lampPending[key] = state;
        }
        return true;
    }

    /// <summary>
    /// 灯写收口（锁外调）：整批换走待写队列逐项真写。写失败/异常 → Invalidate
    /// 清账（下轮 Pump 补写）；WriteLampThroughBus 自带 try-catch 兜底与
    /// "写灯异常"排障日志，口径不变。_bus 现取——总线热切换（Attach）后
    /// pending 里的项写到新总线（值仍是最新期望态）；若总线上写失败自动
    /// 清账走补写链，时序无洞。
    /// 防重入：并发收口时后到者跳过——先到者的 batch 循环会把后来入队的
    /// 项一起处理完（while 到队列空为止）。
    /// </summary>
    private void DrainLampWrites()
    {
        if (!_deferLampWrites)
            return;
        if (Interlocked.Exchange(ref _draining, 1) == 1)
            return;
        try
        {
            while (true)
            {
                Dictionary<IoPointKey, bool> batch;
                lock (_lampQueueLock)
                {
                    if (_lampPending.Count == 0)
                        return;
                    batch = _lampPending;
                    _lampPending = new Dictionary<IoPointKey, bool>();
                }
                foreach (var (key, state) in batch)
                {
                    if (!WriteLampThroughBus(key, state))
                        _lampSink.Invalidate(key); // 清账 → 下一拍 Pump 补写
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
        }
    }

    /// <summary>
    /// 延迟 IO 收口（defer 高档的锁外挂点，V0.0.109）：灯写 drain + 状态落盘
    /// flush 一起收口。挂点=每个可能 EnsureLamp/RequestPersist 的公开方法
    /// lock 块结束之后（Pump/OnPointChanged/Attach/AlignFromSnapshot/
    /// ManualResetGrillAlarm/TerminateCuring/ApplyPolicyHotChange/
    /// ApplyAlarmSwitch）；defer=false 时两者都是零成本快速路径。
    /// </summary>
    private void FlushDeferredIo()
    {
        DrainLampWrites();
        FlushPersist();
    }

    // ---------- 灯写与辅助 ----------

    /// <summary>
    /// 日志用时长文本：宿主注入了 durationText 就按它说（跟随显示单位），
    /// 没注入统一"X 分钟"老口径（缺省值与测试断言、历史日志观感都不变）。
    /// </summary>
    private string MinutesText(int minutes) =>
        _durationText != null ? _durationText(minutes) : $"{minutes} 分钟";

    /// <summary>
    /// 写灯（去重，库原语薄壳 V0.0.106）：与上次成功写的值相同则跳过（稳态
    /// 零网络写）；失败不记账 → 下轮 Pump 自动补写——三态语义由库
    /// DedupEffectSink 承载（_lampSink，见类头注记②）。红灯走光栅报警语义
    /// （SetGrillAlarm，V0.0.63 起：计时中人手伸入亮红、让空复位——不再是
    /// "永不写"的预留位）。锁序红线：本方法在 _sync 锁内调（库内部锁单向
    /// 嵌套，绝不反向）。
    /// </summary>
    private void EnsureLamp(int rack, int level, IoPointRole role, bool state) =>
        _lampSink.Ensure(new IoPointKey(false, role, rack, level), state);

    /// <summary>
    /// 灯写真执行口（库 apply 注入）：写当前总线，返回 true=成功（库记账）。
    /// 异常只兜"总线已释放"等极端竞态（正常失败走 false 不抛）——记边沿
    /// 日志后返 false，库按失败处理不记账、下轮补写（保住原"写灯异常"
    /// 排障口径）。_bus 每次现取（Attach 换总线后自动跟随，闭包捕获字段访问）。
    /// </summary>
    private bool WriteLampThroughBus(IoPointKey key, bool state)
    {
        try
        {
            return _bus.WritePoint(key, state);
        }
        catch (Exception ex)
        {
            _log($"架{key.Rack}层{key.Level} 写灯异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 清空灯写账本（库 Invalidate 逐键清，下轮 Pump 对已记账灯全量补写一轮）：
    /// 换总线（Attach）与断连恢复/重启对齐（AlignFromSnapshot）时调——耦合器
    /// 断电重启后灯实际全灭而账本仍记"已写"，不清账会挂"账上亮着实际灭着"的
    /// 陈旧窗口（原实现无清账口，V0.0.106 原语化接入补上的缺口，见类头注记③）。
    /// 补写规模=账本键数（几十盏量级），稳态去重随后恢复零网络写。仅 _sync 锁内调。
    /// </summary>
    private void InvalidateLampBook()
    {
        foreach (var key in _lampSink.ExpectedSnapshot())
            _lampSink.Invalidate(key);
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
    /// offNote（V0.0.90）：第三解除来源——"报警总开关已关闭"这类程序性解除
    /// （光栅读数带着，说明此刻人手可能还在架区，红灯是开关压灭的不是让空的）；
    /// manual 优先（同拍两来源都到时文案归人工口径）。
    /// </summary>
    private void SetGrillAlarm(SlotRuntime rt, int rack, int level, bool on,
        bool manual = false, string? offNote = null)
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
            GrillAlarmRaised?.Invoke(rack, level);   // 报警边沿（追溯库打标记用）
        }
        else
        {
            EnsureLamp(rack, level, IoPointRole.LampRed, false);
            EnsureLamp(rack, level, IoPointRole.LampYellow, true);
            _log(manual
                ? $"架{rack}层{level} 人工复位确认（{GrillSideText(rack, level)}），报警解除，计时继续"
                : offNote != null
                    ? $"架{rack}层{level} {offNote}（{GrillSideText(rack, level)}），报警解除，计时继续"
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
