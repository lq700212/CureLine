// =========================================================================
// SlotBindingSession：扫码绑定会话（扫库位码 → 扫产品码（可多个）→ 再扫库位码=上料结束）
//
// 干什么：把扫码枪扫到的字符串按"上料工序"解释成业务动作，并维护"当前
//   正在等哪一段"的状态：
//     ① 空闲（WaitingLocation）：扫到的必须是库位码（A01~J12），否则报错不前进；
//        扫到库位码 = **开始该库位的上料**（清掉上一轮的产品绑定）。
//     ② 已扫库位（WaitingProduct）：扫到的当作产品条码 → **追加**进该库位的
//        产品清单（一个库位可以放多个产品，逐个扫）；此阶段若扫到
//        · 同一个库位码 → **上料结束**（二次确认，LoadConfirmed=true）；
//        · 另一个库位码 → **拦截**（V0.0.99 防呆）：上一个库的绑定流程还没
//          结束，必须先二次扫它结束——中途换库位会让两边的上料流程同时悬着，
//          操作员自己也说不清刚才那盘扫码算不算数。
//
// 为什么没有"等多久自动放弃"（V0.0.99 用户拍板）：上料结束的唯一信号就是
//   **二次扫同一个库位码**。现场节奏由操作员自己掌握，不引入"扫到一半被
//   超时打断"的新坑；确实要放弃时点【取消】。
//
// 放回再加工产品（V0.0.99，续接固化）：
//   中途取出的产品再加工完放回来时，本类负责三件事：
//     ① **禁止放回原库位**——原库位（取出的那一格）的固化口径已按"整盘统一"
//        计时，把续接件塞回去会与同库位其他产品的固化时间对不上；
//     ② 放到**新库位**、且该库位本轮还没有别的产品时，弹窗问操作员：接着
//        之前固化的时长继续固化（累计达标：之前已固化 8h + 计划 24h → 本盘
//        只需再固化 16h 即放行，累计 = 计划），还是完全重新开始固化；
//     ③ **禁止混装**：同一库位本轮的固化口径（全新 / 续接）由第一个产品
//        定档，后来者口径不同直接拦截——口径不同 = 同库位产品的固化时长
//        不统一，违反现场红线。
//
// 外部依赖由宿主注入（Core 不碰 UI / DB）：
//   · takeOutLookup：查该产品有没有"中途取出"履历（含原库位与累计已固化分钟）；
//   · resumeDecider：弹窗问操作员要哪个口径；
//   · slotOccupiedProvider：查该库位是否"托盘在架且已进入固化流程"（计时中/
//     已放行待取/已终止）——往这样的库位再添新产品会破坏固化时间统一 → 拦截。
//
// 线程约定：全部方法在 UI 线程调用（扫码事件由服务层封送后进；宿主弹窗也在这
//   一层）。状态机在后台线程读"上料是否做完"（报警抑制闸），故本类所有
//   _map 访问都经 _gate 锁保护（锁很短，读多写少）。
// 怎么改：新增扫码段（如"先扫工单码"）= 在 Handle 前加一段状态 + Phase 枚举
//   加值 + Prompt 加文案；绑定落盘由 persist 委托统一出口（测试注入内存版）。
// =========================================================================

using CureLine.Core.Data;

namespace CureLine.Core.Curing;

/// <summary>扫码会话所处阶段（UI 状态点/文案据此切换）。</summary>
public enum SlotBindingPhase
{
    /// <summary>空闲：等待扫库位码。</summary>
    WaitingLocation,

    /// <summary>已扫库位：等待扫产品条码（可扫多个），再扫同库位码=上料结束。</summary>
    WaitingProduct,
}

/// <summary>一次扫码的处理结果类别（日志/提示语分档，测试断言依据）。</summary>
public enum ScanOutcomeKind
{
    /// <summary>空串/纯空白，直接忽略。</summary>
    Ignored,

    /// <summary>等待库位码阶段扫到非库位码（不是 A01~J12 形态）——保留原位，提示重扫。</summary>
    InvalidLocation,

    /// <summary>库位码被接受，该库位开始上料（上一轮绑定被清掉）。</summary>
    LocationAccepted,

    /// <summary>上一个库位的绑定流程还没结束就扫了别的库位码——拦截并提示先结束上一个。</summary>
    LocationFlowNotFinished,

    /// <summary>该库位托盘在架且已进入固化流程，不允许再往里添新产品——拦截。</summary>
    SlotOccupiedBlocked,

    /// <summary>产品条码已绑定（追加进该库位的产品清单）。</summary>
    ProductBound,

    /// <summary>同一库位重复扫同一 SN——不重复入库，只提示。</summary>
    DuplicateProduct,

    /// <summary>再次扫到同一个库位码——上料结束（二次确认）。</summary>
    LoadConfirmed,

    /// <summary>该产品是中途从**本库位**取出的——禁止放回原库位（须换新库位）。</summary>
    ReturnToOriginBlocked,

    /// <summary>该库位本轮已定档一种固化口径，本产品口径与之冲突（混装）——拦截。</summary>
    CureBasisConflict,

    /// <summary>
    /// 放回件在"固化时长口径"确认窗点了【取消】——操作员主动放弃，不绑定。
    /// 与红线拦截不同：提示用 3 秒轻浮层即可，不弹"扫码被拦截"窗（V0.0.100）。
    /// </summary>
    ReloadCancelled,
}

/// <summary>扫码处理结果（Message 可直接显示/记日志；架层与产品码供调用方回填 UI）。</summary>
public sealed record ScanOutcome(
    ScanOutcomeKind Kind,
    string Message,
    int Rack = 0,
    int Level = 0,
    string ProductCode = "");

/// <summary>
/// 扫码绑定会话（见文件头注释）。生命周期与主窗一致；
/// <see cref="Changed"/> 在每次阶段/绑定变化后触发（UI 线程）。
/// </summary>
public sealed class SlotBindingSession
{
    /// <summary>状态锁：_map 被 UI 线程写、被状态机后台线程读（报警抑制闸）。</summary>
    private readonly object _gate = new();

    /// <summary>内存绑定表（键=全局架号×层号；与落盘簿同源）。</summary>
    private readonly Dictionary<(int Rack, int Level), SlotBindingRecord> _map = new();

    /// <summary>取当前时间（测试注入假时钟；缺省真实墙钟）。</summary>
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>持久化出口（测试注入内存版；缺省写 config/slotbindings.json）。</summary>
    private readonly Action<SlotBindingBook>? _persist;

    /// <summary>会话日志出口（缺省静默——MainViewModel 注入 LocalLogService）。</summary>
    private readonly Action<string>? _log;

    /// <summary>
    /// 查产品的"中途取出"履历（宿主注入；返回 null=该产品当前没有待放回状态，
    /// 按全新件处理）。履历含原库位与累计已固化分钟——放回原库位拦截与续接
    /// 口径确认都要用它。
    /// </summary>
    private readonly Func<string, SlotTakeOutInfo?>? _takeOutLookup;

    /// <summary>
    /// 弹窗问操作员：续接产品放新库位时，固化时长按哪个口径算（宿主注入，
    /// 内部弹窗；返回 Cancel=本次不放这个产品）。
    /// </summary>
    private readonly Func<SlotReloadPrompt, SlotReloadChoice>? _resumeDecider;

    /// <summary>
    /// 查该库位是否"托盘在架且已进入固化流程"（宿主注入=读状态机；null=恒否）。
    /// 干什么用：往这样的库位再添新产品 → 同库位产品的固化起点不同、时间
    /// 不统一 → 拦截。
    /// </summary>
    private readonly Func<int, int, bool>? _slotOccupiedProvider;

    /// <summary>本次会话变化通知（阶段切换/绑定增删后；UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 产品绑定边沿（新增一件产品；参数=架、层、SN、续接前段已固化分钟）。
    /// 宿主据此写追溯库（一个 SN 一行流水）——本类只管内存事实，落库由宿主做
    /// （Core 不碰 DB）。
    /// </summary>
    public event Action<int, int, string, int>? ProductBound;

    /// <summary>
    /// 上料确认边沿（二次扫同库位码=上料结束；参数=架、层）。宿主据此给
    /// 追溯库盖"上料确认时刻"，并把该库位从"报警抑制"名单里放出来。
    /// </summary>
    public event Action<int, int>? LoadConfirmed;

    /// <summary>当前阶段。</summary>
    public SlotBindingPhase Phase { get; private set; } = SlotBindingPhase.WaitingLocation;

    /// <summary>当前待绑产品码的库位架号（Phase=WaitingProduct 时有效，否则 0）。</summary>
    public int PendingRack { get; private set; }

    /// <summary>当前待绑产品码的库位层号（Phase=WaitingProduct 时有效，否则 0）。</summary>
    public int PendingLevel { get; private set; }

    /// <summary>当前待绑库位显示码（"A01"；空闲时为空串）。</summary>
    public string PendingLocationCode => Phase == SlotBindingPhase.WaitingProduct
        ? SlotLocationCode.EncodeInRack(PendingRack, PendingLevel)
        : "";

    /// <summary>UI 指引文案（统计条扫码胶囊直接显示）。</summary>
    public string Prompt => Phase == SlotBindingPhase.WaitingProduct
        ? $"{PendingLocationCode} 待扫产品码（扫完再扫一次库位码结束上料）"
        : "请扫库位码";

    /// <summary>已绑定的库位数（诊断/状态卡用；一个库位可有多个产品）。</summary>
    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>已绑定的产品总件数（跨库位合计；诊断用）。</summary>
    public int TotalProductCount { get { lock (_gate) return _map.Values.Sum(r => r.ProductCodes.Count); } }

    /// <summary>
    /// 构造会话。
    /// </summary>
    /// <param name="book">启动时读到的绑定簿（null=空簿，首次启动/文件损坏都走这里）。</param>
    /// <param name="clock">时间源（测试注入假时钟；缺省 DateTimeOffset.Now）。</param>
    /// <param name="persist">落盘委托（缺省 SlotBindingStore.Save；测试注入内存版）。</param>
    /// <param name="log">日志委托（缺省静默）。</param>
    /// <param name="takeOutLookup">
    /// 查产品的中途取出履历（缺省 null=一律当全新件处理——Core 独立可用，
    /// 现场由宿主注入追溯库台账）。
    /// </param>
    /// <param name="resumeDecider">
    /// 续接口径确认弹窗（缺省 null=不放续接件，等同"取消"）。
    /// </param>
    /// <param name="slotOccupiedProvider">
    /// 查库位是否托盘在架且已进入固化流程（缺省 null=恒否）。
    /// </param>
    public SlotBindingSession(
        SlotBindingBook? book = null,
        Func<DateTimeOffset>? clock = null,
        Action<SlotBindingBook>? persist = null,
        Action<string>? log = null,
        Func<string, SlotTakeOutInfo?>? takeOutLookup = null,
        Func<SlotReloadPrompt, SlotReloadChoice>? resumeDecider = null,
        Func<int, int, bool>? slotOccupiedProvider = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _persist = persist ?? SlotBindingStore.Save;
        _log = log;
        _takeOutLookup = takeOutLookup;
        _resumeDecider = resumeDecider;
        _slotOccupiedProvider = slotOccupiedProvider;

        foreach (var record in book?.Bindings ?? [])
        {
            // 同一库位重复记录以最后一条为准（手改文件/旧版本残留的容错）。
            _map[(record.Rack, record.Level)] = record;
        }
    }

    /// <summary>取某库位的绑定（没有返回 null）。</summary>
    public SlotBindingRecord? Get(int rack, int level)
    {
        lock (_gate)
            return _map.TryGetValue((rack, level), out var record) ? record : null;
    }

    /// <summary>某库位已绑产品件数（0=未绑定；主窗卡片显示用）。</summary>
    public int ProductCount(int rack, int level)
    {
        lock (_gate)
            return _map.TryGetValue((rack, level), out var record) ? record.ProductCodes.Count : 0;
    }

    /// <summary>某库位已绑产品 SN 清单（空=未绑定；追溯/弹窗用）。</summary>
    public IReadOnlyList<string> GetProducts(int rack, int level)
    {
        lock (_gate)
            return _map.TryGetValue((rack, level), out var record)
                ? record.ProductCodes.ToList()
                : Array.Empty<string>();
    }

    /// <summary>
    /// 该库位上料是否已确认（第二次扫同一个库位码=上料结束）。
    /// 从未扫过库位码的库位=false（未上料，也没确认）。
    /// </summary>
    public bool IsLoadConfirmed(int rack, int level)
    {
        lock (_gate)
            return _map.TryGetValue((rack, level), out var record) && record.LoadConfirmed;
    }

    /// <summary>
    /// 该库位上料是否**尚未完成**（没扫过库位码、或扫了还没二次确认）。
    ///
    /// 干什么用：光栅报警抑制闸——上料期间操作员的手和产品会挡住光栅，那是
    /// 正常动作，不是异常。所以"上料没做完"的库位，光栅被挡**不报警**
    /// （V0.0.97 起；V0.0.99 扩到"完全没扫码"的库位——现场托盘放上、
    /// 手还没离开就挡光栅，此时连库位码都还没扫）。
    ///
    /// 与"已绑了多少产品"无关：只看上料确认这一步走没走完。
    /// </summary>
    public bool IsLoadUnconfirmed(int rack, int level) => !IsLoadConfirmed(rack, level);

    /// <summary>
    /// 该库位本轮"续接基线"（分钟）：库里续接件"进入本轮前累计已固化时长"
    /// 的最小值——库位本次计时 = 计划时长 − 该基线（累计达标口径）。
    ///
    /// 为什么取最小值：同库位产品各自的累计都要达到计划时长，本次时长按
    /// "最省的那个"作基准 → 谁都不会固化不足（宁多勿少：固化不足是质量
    /// 事故，多固化只是多花点时间）。全新件库位返回 0。
    /// </summary>
    public int GetResumeBaselineMinutes(int rack, int level)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((rack, level), out var record))
                return 0;
            return record.ResumeBaselineMinutes;
        }
    }

    /// <summary>
    /// 某产品在某库位本轮的"续接前段已固化分钟"（0=该产品按全新件计时）。
    /// 追溯库写"累计固化时长"时用（累计 = 前段 + 本轮实际）。
    /// </summary>
    public int GetProductPriorMinutes(int rack, int level, string productSn)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((rack, level), out var record) || record.ResumePriorMinutes == null)
                return 0;
            return record.ResumePriorMinutes.TryGetValue(productSn, out int prior) ? prior : 0;
        }
    }

    /// <summary>当前绑定簿快照（按架层升序，落盘/诊断用）。</summary>
    public SlotBindingBook Snapshot()
    {
        lock (_gate)
        {
            return new SlotBindingBook
            {
                Bindings = _map.Values
                    .OrderBy(b => b.Rack).ThenBy(b => b.Level)
                    .Select(Clone)
                    .ToList(),
            };
        }
    }

    /// <summary>
    /// 处理一条扫码文本（扫码枪原样输出，方法内部 Trim）。
    /// 返回结果供调用方决定提示方式；任何输入都不会抛异常。
    /// </summary>
    public ScanOutcome Handle(string? rawCode)
    {
        string code = (rawCode ?? "").Trim();
        if (code.Length == 0)
            return new ScanOutcome(ScanOutcomeKind.Ignored, "空扫码已忽略");

        bool isLocation = SlotLocationCode.TryDecode(code, out int rack, out int level);

        if (Phase == SlotBindingPhase.WaitingLocation)
        {
            if (!isLocation)
            {
                _log?.Invoke($"扫码：'{code}' 不是库位码（应为 A01~J12），请重扫库位码");
                return new ScanOutcome(ScanOutcomeKind.InvalidLocation,
                    $"'{code}' 不是库位码（应为 A01~J12）");
            }
            return AcceptLocation(rack, level);
        }

        // ---- 等产品码阶段 ----
        if (isLocation)
        {
            // 扫到库位码：同一个 = 上料结束（二次确认）；不同的 = 拦截
            // （上一个绑定流程还没结束——必须先把它结束，见文件头"防呆"）。
            if (rack == PendingRack && level == PendingLevel)
                return ConfirmLoad();

            string pending = PendingLocationCode;
            _log?.Invoke($"扫码：库位 {pending} 的绑定还没结束，扫 {code} 已拦截（先二次扫 {pending} 完成绑定）");
            return new ScanOutcome(ScanOutcomeKind.LocationFlowNotFinished,
                $"{pending} 绑定未结束，请再扫一次 {pending} 结束绑定",
                PendingRack, PendingLevel);
        }

        return BindProduct(code);
    }

    /// <summary>
    /// 取消当前扫码（UI【取消】按钮）：等产品码阶段回空闲并提示。
    /// 返回 true=确实取消了待绑状态；false=本来就在等库位码（按钮应隐藏）。
    /// </summary>
    public bool Cancel()
    {
        if (Phase != SlotBindingPhase.WaitingProduct)
            return false;
        string code = PendingLocationCode;
        ResetPending();
        _log?.Invoke($"扫码已取消：库位 {code} 结束待绑，回到『请扫库位码』");
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 清除某库位绑定（托盘离开/取件时调：产品已拿走，绑定随之作废）。
    /// 返回 true=确有条目被删（调用方记一行边沿日志）。
    /// </summary>
    public bool Remove(int rack, int level)
    {
        lock (_gate)
        {
            if (!_map.Remove((rack, level)))
                return false;
        }
        if (Phase == SlotBindingPhase.WaitingProduct &&
            PendingRack == rack && PendingLevel == level)
        {
            ResetPending();   // 正在等这个库位的产品码，托盘走了→不用再等了
        }
        Persist();
        Changed?.Invoke();
        return true;
    }

    // ---------------- 内部 ----------------

    /// <summary>
    /// 接受库位码 = 开始该库位的上料：清除该库位上一轮的产品绑定（客户口径：
    /// 扫库位即清旧产品信息）→ 进入等产品码。
    /// </summary>
    private ScanOutcome AcceptLocation(int rack, int level)
    {
        string code = SlotLocationCode.EncodeInRack(rack, level);

        // 库位托盘在架且已进入固化流程（计时中/已放行待取/已终止）→ 不许再往
        // 里添新产品：同一库位产品的固化起点必须一致，往在制品里加货会让固化
        // 时长不统一（现场红线）。
        // 注：托盘刚放上、还没扫码（绑定记录为空）不算占用——那是正常上料流程。
        if (_slotOccupiedProvider?.Invoke(rack, level) == true && ProductCount(rack, level) > 0)
        {
            _log?.Invoke($"扫码：库位 {code} 正在固化，禁止再往该库位添加产品");
            return new ScanOutcome(ScanOutcomeKind.SlotOccupiedBlocked,
                $"{code} 正在固化，不能再往这个库位加产品（同一库位的固化时间必须统一）",
                rack, level);
        }

        bool hadOld;
        lock (_gate)
        {
            hadOld = _map.Remove((rack, level));
            var record = new SlotBindingRecord
            {
                Rack = rack,
                Level = level,
                BoundAt = _clock(),
                UpdatedAt = _clock(),
                LoadConfirmed = false,
            };
            _map[(rack, level)] = record;
        }
        if (hadOld)
            Persist();   // 旧绑定已清——即使随后取消，库位也保持"未绑定"（安全侧）

        PendingRack = rack;
        PendingLevel = level;
        Phase = SlotBindingPhase.WaitingProduct;
        Changed?.Invoke();

        string tail = hadOld ? "（旧绑定已清除）" : "";
        _log?.Invoke($"扫码：库位已扫 {code}{tail}，请扫产品码（可扫多个，扫完再扫一次库位码结束上料）");
        return new ScanOutcome(ScanOutcomeKind.LocationAccepted,
            $"{code} 待扫产品码{tail}", rack, level);
    }

    /// <summary>
    /// 绑定产品码（**追加**进该库位产品清单，一个库位可放多个产品）→ 落盘。
    ///
    /// 放回件（V0.0.99）三道闸：
    ///   ① 取出的原库位 == 当前库位 → 拦截（不许放回原库位）；
    ///   ② 取出的原库位 != 当前库位、且该库位本轮还没有别的产品 → 问操作员
    ///      口径（续接 / 全新重开）；
    ///   ③ 口径与库位本轮已定档的不一致 → 拦截（禁混装）。
    /// </summary>
    private ScanOutcome BindProduct(string productCode)
    {
        int rack = PendingRack;
        int level = PendingLevel;
        string location = SlotLocationCode.EncodeInRack(rack, level);

        // 库位本轮口径（口径由第一个产品定档）
        SlotCureBasis slotBasis;
        lock (_gate)
        {
            if (!_map.TryGetValue((rack, level), out var record))
            {
                record = new SlotBindingRecord
                {
                    Rack = rack,
                    Level = level,
                    BoundAt = _clock(),
                };
                _map[(rack, level)] = record;
            }
            if (record.ProductCodes.Contains(productCode))
            {
                _log?.Invoke($"扫码：库位 {location} 的产品 {productCode} 已绑定过，忽略重复");
                return new ScanOutcome(ScanOutcomeKind.DuplicateProduct,
                    $"{location} 的 {productCode} 已绑定过", rack, level, productCode);
            }
            slotBasis = record.CureBasis;
        }

        var takeOut = _takeOutLookup?.Invoke(productCode);

        // ---- ① 放回原库位 → 拦截 ----
        if (takeOut != null && takeOut.Rack == rack && takeOut.Level == level)
        {
            _log?.Invoke($"扫码：产品 {productCode} 是从本库位中途取出的，禁止放回原库位");
            return new ScanOutcome(ScanOutcomeKind.ReturnToOriginBlocked,
                $"{productCode} 是从本库位中途取出的，不能放回本库位，请放到其他库位",
                rack, level, productCode);
        }

        int priorMinutes = 0;   // 本产品"进入本轮前累计已固化分钟"（0=全新件）
        SlotCureBasis basis = SlotCureBasis.Fresh;

        if (takeOut != null)
        {
            // ---- ② 放新库位 → 问操作员固化时长口径（续接 / 全新重开） ----
            // 库里已有产品时**也要问**：放回件的前段固化时长要不要作废，关系到
            // 这件货够不够固化够——不能不问就替操作员决定（默认当全新 = 前段
            // 作废 = 可能固化不足）。问完再按 ③ 判混装。
            // 例外：库位口径已定档为"续接"时不再问——口径由首个产品定档，后来
            // 者必须跟着（此时本件也按续接处理）。
            var prompt = new SlotReloadPrompt
            {
                ProductSn = productCode,
                LocationCode = location,
                Rack = rack,
                Level = level,
                PreviousLocationCode = takeOut.LocationCode,
                PriorMinutes = takeOut.CuredMinutes,
            };
            SlotCureBasis wanted;
            if (slotBasis == SlotCureBasis.Resume)
            {
                // 库里已按续接计时 → 本件也按续接（口径一致，不再打扰操作员）
                wanted = SlotCureBasis.Resume;
                priorMinutes = takeOut.CuredMinutes;
            }
            else
            {
                var choice = _resumeDecider?.Invoke(prompt) ?? SlotReloadChoice.Cancel;
                if (choice == SlotReloadChoice.Cancel)
                {
                    _log?.Invoke($"扫码：产品 {productCode} 的放回流程已取消（未绑定）");
                    // 主动取消≠红线拦截：单独归类，宿主给轻提示不弹拦截窗（V0.0.100）。
                    return new ScanOutcome(ScanOutcomeKind.ReloadCancelled,
                        $"已取消放入 {productCode}", rack, level, productCode);
                }
                if (choice == SlotReloadChoice.Resume)
                {
                    wanted = SlotCureBasis.Resume;
                    priorMinutes = takeOut.CuredMinutes;   // 续接累计达标
                }
                else
                {
                    wanted = SlotCureBasis.Fresh;              // 完全重新开始（前段作废）
                }
            }

            // ---- ③ 禁混装：口径与库位已定档的不一致 → 拦截 ----
            if (slotBasis != SlotCureBasis.Unset && wanted != slotBasis)
            {
                string wantText = wanted == SlotCureBasis.Resume ? "续接累计" : "全新计时";
                string slotText = slotBasis == SlotCureBasis.Resume ? "续接累计" : "全新计时";
                _log?.Invoke($"扫码：库位 {location} 本轮按{slotText}计时，{productCode} 按{wantText}放入属混装，拦截");
                return new ScanOutcome(ScanOutcomeKind.CureBasisConflict,
                    $"{location} 本轮按{slotText}固化，{productCode} 不能按{wantText}放入（同一库位固化时间必须统一）",
                    rack, level, productCode);
            }
            basis = wanted;
        }
        else
        {
            // 全新件：库里已按续接计时 → 混装拦截
            if (slotBasis == SlotCureBasis.Resume)
            {
                _log?.Invoke($"扫码：库位 {location} 本轮按续接累计计时，全新产品 {productCode} 不能放入（禁混装）");
                return new ScanOutcome(ScanOutcomeKind.CureBasisConflict,
                    $"{location} 本轮按续接累计固化，不能放入全新产品 {productCode}（同一库位固化时间必须统一）",
                    rack, level, productCode);
            }
            basis = SlotCureBasis.Fresh;
        }

        int count;
        lock (_gate)
        {
            if (!_map.TryGetValue((rack, level), out var record))
            {
                record = new SlotBindingRecord { Rack = rack, Level = level, BoundAt = _clock() };
                _map[(rack, level)] = record;
            }
            record.ProductCodes.Add(productCode);
            if (basis == SlotCureBasis.Resume)
            {
                // 续接件登记前段时长（键存在即表示续接口径，含"前段 0 分钟"）
                if (record.ResumePriorMinutes == null)
                    record.ResumePriorMinutes = new Dictionary<string, int>();
                record.ResumePriorMinutes[productCode] = priorMinutes;
            }
            if (record.CureBasis == SlotCureBasis.Unset)
                record.CureBasis = basis;   // 口径由第一个产品定档
            record.UpdatedAt = _clock();
            count = record.ProductCodes.Count;
        }

        Persist();
        _log?.Invoke($"扫码绑定：库位 {location} ← 产品 {productCode}" +
            (basis == SlotCureBasis.Resume ? $"（续接：之前已固化 {priorMinutes} 分钟）" : "") +
            $"（本库位 {count} 件）");
        Changed?.Invoke();
        ProductBound?.Invoke(rack, level, productCode, priorMinutes);
        return new ScanOutcome(ScanOutcomeKind.ProductBound,
            $"已绑定 {location} ← {productCode}（本库位 {count} 件）",
            rack, level, productCode);
    }

    /// <summary>
    /// 再次扫到同一个库位码 = 上料结束（二次确认）：给该库位盖"已确认"标志，
    /// 回"请扫库位码"，并抛 LoadConfirmed 边沿（宿主据此解除该库位的报警抑制）。
    /// </summary>
    private ScanOutcome ConfirmLoad()
    {
        int rack = PendingRack;
        int level = PendingLevel;
        string location = SlotLocationCode.EncodeInRack(rack, level);

        int count;
        lock (_gate)
        {
            if (!_map.TryGetValue((rack, level), out var record))
            {
                record = new SlotBindingRecord { Rack = rack, Level = level, BoundAt = _clock() };
                _map[(rack, level)] = record;
            }
            record.LoadConfirmed = true;
            record.LoadConfirmedAt = _clock();
            record.UpdatedAt = _clock();
            count = record.ProductCodes.Count;
        }
        Persist();
        ResetPending();
        _log?.Invoke($"扫码确认：库位 {location} 上料结束（{count} 件产品）");
        Changed?.Invoke();
        LoadConfirmed?.Invoke(rack, level);
        return new ScanOutcome(ScanOutcomeKind.LoadConfirmed,
            $"{location} 上料结束（{count} 件产品）", rack, level);
    }

    /// <summary>落盘（失败只记日志——内存绑定照常生效，重启重扫即可）。</summary>
    private void Persist()
    {
        try
        {
            _persist?.Invoke(Snapshot());
        }
        catch (Exception ex)
        {
            _log?.Invoke($"库位绑定保存失败（{ex.Message}），内存绑定继续有效，重启后需重扫");
        }
    }

    /// <summary>回到等库位码（清待绑目标）。</summary>
    private void ResetPending()
    {
        Phase = SlotBindingPhase.WaitingLocation;
        PendingRack = 0;
        PendingLevel = 0;
    }

    /// <summary>
    /// 克隆记录——落盘时传副本（调用方/JSON 序列化与内存对象解耦，
    /// 防外部改动内存记录绕过 Persist 造成盘上/内存不一致）。
    /// </summary>
    private static SlotBindingRecord Clone(SlotBindingRecord source) => new()
    {
        Rack = source.Rack,
        Level = source.Level,
        ProductCodes = source.ProductCodes.ToList(),
        CureBasis = source.CureBasis,
        ResumePriorMinutes = source.ResumePriorMinutes == null
            ? new Dictionary<string, int>()
            : new Dictionary<string, int>(source.ResumePriorMinutes),
        BoundAt = source.BoundAt,
        UpdatedAt = source.UpdatedAt,
        LoadConfirmed = source.LoadConfirmed,
        LoadConfirmedAt = source.LoadConfirmedAt,
    };
}
