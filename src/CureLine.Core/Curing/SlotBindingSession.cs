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
//        · 另一个库位码 → 切换待绑库位（操作员扫错/改主意），旧目标保持
//          "上料未确认"状态不变。
// 为什么上料要"扫两次库位码"：现场一个库位要放多个产品，操作员不知道
//   扫了几个才算完。两次扫同一个库位码 = 明确的"上完了"信号，也是
//   **报警抑制的边界**：上料未确认期间操作员的手和产品会挡住光栅，
//   那时报警是误报；确认之后光栅被挡才是真报警（见 SlotCuringMachine
//   的 loadPendingProvider 闸）。库位码重复扫不判错——它就是"结束"的意思。
// 为什么要有超时：操作员扫了库位码后走开，窗口会一直挂着"A01 待扫产品码"，
//   下一个来的人会以为在等他接着扫——超时（[Scanner] SessionTimeoutSeconds，
//   缺省 15 秒）自动回到"请扫库位码"，把界面状态与现场行为对齐。
//   注意：超时只清"当前待绑目标"，**不清该库位的上料未确认状态**——
//   托盘还架在那儿、货还没上完，报警就该继续抑制（宁可漏报不误报）。
// 为什么不自动拦截固化：客户口径是"未绑定只提示、不停机"（流程不能因为
//   漏扫码就断产），所以本类只维护绑定事实，不参与状态机任何判定。
//
// 线程约定：全部方法在 UI 线程调用（扫码事件由服务层封送后进；Tick 由
//   500ms 刷新拍驱动）；类内部无锁、无后台线程，测试可直驱。
// 怎么改：新增扫码段（如"先扫工单码"）= 在 Handle 前加一段状态 + Phase 枚举
//   加值 + Prompt 加文案；绑定落盘由 persist 委托统一出口（测试注入内存版）。
// =========================================================================

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

    /// <summary>库位码被接受，该库位开始上料（进入等产品码阶段）。</summary>
    LocationAccepted,

    /// <summary>等产品码阶段又扫到另一个库位码——切换待绑库位（自愈）。</summary>
    LocationSwitched,

    /// <summary>产品条码已绑定（追加进该库位的产品清单）。</summary>
    ProductBound,

    /// <summary>同一库位扫了重复的产品码——不重复入库，只提示。</summary>
    DuplicateProduct,

    /// <summary>再次扫到同一个库位码——上料结束（二次确认）。</summary>
    LoadConfirmed,
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
    /// <summary>内存绑定表（键=全局架号×层号；与落盘簿同源）。</summary>
    private readonly Dictionary<(int Rack, int Level), SlotBindingRecord> _map = new();

    /// <summary>取当前时间（测试注入假时钟；缺省真实墙钟）。</summary>
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>持久化出口（测试注入内存版；缺省写 config/slotbindings.json）。</summary>
    private readonly Action<SlotBindingBook>? _persist;

    /// <summary>会话日志出口（缺省静默——MainViewModel 注入 LocalLogService）。</summary>
    private readonly Action<string>? _log;

    /// <summary>等产品码阶段的超时秒数（<=0 表示不超时，现场不建议）。</summary>
    private readonly int _timeoutSeconds;

    /// <summary>最近一次扫到库位码的时刻（超时判定锚点）。</summary>
    private DateTimeOffset _pendingAt;

    /// <summary>本次会话变化通知（阶段切换/绑定增删后；UI 线程）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 产品绑定边沿（新增一件产品；参数=架、层、SN）。宿主据此写追溯库
    /// （一个 SN 一行流水）——本类只管内存事实，落库由宿主做（Core 不碰 DB）。
    /// </summary>
    public event Action<int, int, string>? ProductBound;

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
    public int Count => _map.Count;

    /// <summary>已绑定的产品总件数（跨库位合计；诊断用）。</summary>
    public int TotalProductCount => _map.Values.Sum(r => r.ProductCodes.Count);

    /// <summary>
    /// 构造会话。
    /// </summary>
    /// <param name="book">启动时读到的绑定簿（null=空簿，首次启动/文件损坏都走这里）。</param>
    /// <param name="clock">时间源（测试注入假时钟；缺省 DateTimeOffset.Now）。</param>
    /// <param name="timeoutSeconds">等产品码超时秒数（缺省 15；&lt;=0 关闭超时）。</param>
    /// <param name="persist">落盘委托（缺省 SlotBindingStore.Save；测试注入内存版）。</param>
    /// <param name="log">日志委托（缺省静默）。</param>
    public SlotBindingSession(
        SlotBindingBook? book = null,
        Func<DateTimeOffset>? clock = null,
        int timeoutSeconds = 15,
        Action<SlotBindingBook>? persist = null,
        Action<string>? log = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _persist = persist ?? SlotBindingStore.Save;
        _log = log;
        _timeoutSeconds = timeoutSeconds;

        foreach (var record in book?.Bindings ?? [])
        {
            // 同一库位重复记录以最后一条为准（手改文件/旧版本残留的容错）。
            _map[(record.Rack, record.Level)] = record;
        }
    }

    /// <summary>取某库位的绑定（没有返回 null）。</summary>
    public SlotBindingRecord? Get(int rack, int level) =>
        _map.TryGetValue((rack, level), out var record) ? record : null;

    /// <summary>某库位已绑产品件数（0=未绑定；主窗卡片显示用）。</summary>
    public int ProductCount(int rack, int level) =>
        _map.TryGetValue((rack, level), out var record) ? record.ProductCodes.Count : 0;

    /// <summary>某库位已绑产品 SN 清单（空=未绑定；追溯/弹窗用）。</summary>
    public IReadOnlyList<string> GetProducts(int rack, int level) =>
        _map.TryGetValue((rack, level), out var record)
            ? record.ProductCodes.ToList()
            : Array.Empty<string>();

    /// <summary>
    /// 该库位上料是否已确认（第二次扫同一个库位码=上料结束）。
    /// 从未扫过库位码的库位=false（未上料，也没确认）。
    /// </summary>
    public bool IsLoadConfirmed(int rack, int level) =>
        _map.TryGetValue((rack, level), out var record) && record.LoadConfirmed;

    /// <summary>
    /// 该库位是否处于"上料未确认"（= 装了产品但操作员还没说"上完了"）。
    /// 宿主据此抑制该库位的光栅报警（上料时手挡光栅是正常动作，见
    /// SlotCuringMachine 的 loadPendingProvider 闸）。
    /// </summary>
    public bool IsLoadPending(int rack, int level) =>
        _map.TryGetValue((rack, level), out var record) && !record.LoadConfirmed;

    /// <summary>当前绑定簿快照（按架层升序，落盘/诊断用）。</summary>
    public SlotBindingBook Snapshot() => new()
    {
        Bindings = _map.Values
            .OrderBy(b => b.Rack).ThenBy(b => b.Level)
            .Select(Clone)
            .ToList(),
    };

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
            return AcceptLocation(rack, level, switched: false);
        }

        // ---- 等产品码阶段 ----
        if (isLocation)
        {
            // 扫到库位码：同一个 = 上料结束（二次确认）；不同 = 切换待绑库位
            // （操作员扫错/改主意，旧目标保持"上料未确认"不变）。
            bool same = rack == PendingRack && level == PendingLevel;
            return same ? ConfirmLoad() : AcceptLocation(rack, level, switched: true);
        }

        return BindProduct(code);
    }

    /// <summary>
    /// 超时检查（由 500ms UI 拍驱动）：等产品码超过阈値自动回空闲。
    /// 返回 true=本次发生了超时重置（调用方需刷 UI）。
    /// 注意：只清"当前待绑目标"，该库位的上料未确认状态保留——托盘还架着、
    /// 货还没上完，报警仍应抑制（宁可漏报不误报）。
    /// </summary>
    public bool Tick()
    {
        if (Phase != SlotBindingPhase.WaitingProduct || _timeoutSeconds <= 0)
            return false;
        if (_clock() - _pendingAt < TimeSpan.FromSeconds(_timeoutSeconds))
            return false;

        string code = PendingLocationCode;
        ResetPending();
        _log?.Invoke($"扫码超时：库位 {code} 未扫完产品码，已回到『请扫库位码』" +
                     "（该库位仍按上料未确认处理，不报光栅报警）");
        Changed?.Invoke();
        return true;
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
        if (!_map.Remove((rack, level)))
            return false;
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
    /// 扫库位即清旧产品信息）→ 进入等产品码并重置超时锚点。
    /// </summary>
    private ScanOutcome AcceptLocation(int rack, int level, bool switched)
    {
        bool hadOld = _map.Remove((rack, level));
        var record = new SlotBindingRecord
        {
            Rack = rack,
            Level = level,
            BoundAt = _clock(),
            UpdatedAt = _clock(),
            LoadConfirmed = false,
        };
        _map[(rack, level)] = record;
        if (hadOld)
            Persist();   // 旧绑定已清——即使随后超时/取消，库位也保持"未绑定"（安全侧）

        PendingRack = rack;
        PendingLevel = level;
        _pendingAt = _clock();
        Phase = SlotBindingPhase.WaitingProduct;
        Changed?.Invoke();

        string code = SlotLocationCode.EncodeInRack(rack, level);
        string tail = hadOld ? "（旧绑定已清除）" : "";
        string verb = switched ? "已切换待绑库位" : "库位已扫";
        _log?.Invoke($"扫码：{verb} {code}{tail}，请扫产品码（可扫多个，扫完再扫一次库位码结束上料）");
        return new ScanOutcome(
            switched ? ScanOutcomeKind.LocationSwitched : ScanOutcomeKind.LocationAccepted,
            $"{code} 待扫产品码{tail}", rack, level);
    }

    /// <summary>
    /// 绑定产品码（**追加**进该库位产品清单，一个库位可放多个产品）→ 落盘。
    /// 同一 SN 重复扫不重复入库（只提示），避免手抖扫两遍把计数搞乱。
    /// </summary>
    private ScanOutcome BindProduct(string productCode)
    {
        int rack = PendingRack;
        int level = PendingLevel;
        string location = SlotLocationCode.EncodeInRack(rack, level);

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

        record.ProductCodes.Add(productCode);
        record.UpdatedAt = _clock();
        Persist();
        _log?.Invoke($"扫码绑定：库位 {location} ← 产品 {productCode}（本库位 {record.ProductCodes.Count} 件）");
        Changed?.Invoke();
        ProductBound?.Invoke(rack, level, productCode);
        return new ScanOutcome(ScanOutcomeKind.ProductBound,
            $"已绑定 {location} ← {productCode}（本库位 {record.ProductCodes.Count} 件）",
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

        if (!_map.TryGetValue((rack, level), out var record))
        {
            record = new SlotBindingRecord { Rack = rack, Level = level, BoundAt = _clock() };
            _map[(rack, level)] = record;
        }
        record.LoadConfirmed = true;
        record.LoadConfirmedAt = _clock();
        record.UpdatedAt = _clock();
        Persist();
        ResetPending();
        _log?.Invoke($"扫码确认：库位 {location} 上料结束（{record.ProductCodes.Count} 件产品已绑定）");
        Changed?.Invoke();
        LoadConfirmed?.Invoke(rack, level);
        return new ScanOutcome(ScanOutcomeKind.LoadConfirmed,
            $"{location} 上料结束（{record.ProductCodes.Count} 件产品）", rack, level);
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

    /// <summary>回到等库位码（清待绑目标与计时锚点）。</summary>
    private void ResetPending()
    {
        Phase = SlotBindingPhase.WaitingLocation;
        PendingRack = 0;
        PendingLevel = 0;
        _pendingAt = default;
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
        BoundAt = source.BoundAt,
        UpdatedAt = source.UpdatedAt,
        LoadConfirmed = source.LoadConfirmed,
        LoadConfirmedAt = source.LoadConfirmedAt,
    };
}
