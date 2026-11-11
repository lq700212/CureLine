// =========================================================================
// SlotProductTraceStore：固化追溯库（库位 ↔ 产品 SN ↔ 固化时长）
//
// 干什么：把"哪个库位放了哪些产品、固化了多久、为什么结束"落成可查询的本地
//   库，供客户追溯与质量核查。两张件：
//   ① 表 slot_product_trace —— **一个产品 SN 一行**（流水）。同一库位放了
//      几个产品就有几行，各行独立记录绑定时刻/上料确认时刻/开计时时刻/
//      离架时刻/实际固化时长/结束原因。这样"某个 SN 固化不足"能逐件查出来。
//   ② 视图 slot_trace_summary —— **一个库位一行**（汇总）。给现场看"这个
//      库位一共放过几个产品、几个正常完成、几个异常终止"用。
//   与绑定簿（config/slotbindings.json）的关系：绑定簿是**在架实时态**
//   （托盘一走就清），本库是**历史流水**（永不删除）——两者互补，不是替代。
//
// 为什么不用 JSON 落盘：客户要求"记录能追溯"，现场要按时间/库位/SN 检索，
//   要统计（几个完成几个异常），JSON 每次全量重写且无法按条件查——SQLite
//   天生干这个，且本项目已有 SqliteHelper 唯一出入口（不引第二个 DB 依赖）。
//
// 线程纪律（铁律 2）：**所有写库调用方必须放后台线程**（扫码事件、过账
//   挂点都在 UI 线程/状态机锁内触发，绝不能同步写库）。本类内部对写操作
//   加锁串行化——SQLite 写锁是文件级的，短连接 + 串行写是最省心的并发策略
//   （与 SqliteHelper 头注释同款口径）。读不锁（短连接 + 30s 命令超时）。
//
// 怎么改：新增追溯维度（如批次号/工单号）= 加列 + EnsureSchema 里补
//   ALTER TABLE（SQLite 不支持 IF NOT EXISTS 的加列，需先查 pragma 决定），
//   两处一致；新结束原因 = SlotTraceEndReason 加值 + 中文映射表补一条。
//
// 编码口径：库位显示一律走 SlotLocationCode.EncodeInRack（双架各一套
//   A01~J12，与主窗卡片标题、扫码绑定同源，禁止另拼字符串）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Data;

/// <summary>
/// 追溯记录的结束原因（产品从库位上"消失"的原因）。
/// 现场质量追溯最关心的就是"它为什么没固化够"——所以原因必须逐件落库，
/// 不能只在日志里留一行（日志会被清、会被刷屏）。
/// </summary>
public enum SlotTraceEndReason
{
    /// <summary>未结束（产品仍在架上，或记录刚建还没到结束点）。</summary>
    None = 0,

    /// <summary>正常放行：固化到点，产品可取。</summary>
    Released = 1,

    /// <summary>中途取出：固化未到点就被拿出（光栅报警确认后补扫 SN 落这条）。</summary>
    TakenOut = 2,

    /// <summary>报警终止：操作员确认后选择"终止计时"，整个库位产品停表。</summary>
    AlarmStopped = 3,

    /// <summary>误报解除：光栅被挡但确认无人取件，纯误触。</summary>
    AlarmFalse = 4,

    /// <summary>人工终止：其他人工干预结束（非报警场景）。</summary>
    ManualStop = 5,
}

/// <summary>结束原因的中文文案（报表列、弹窗、日志三处同源）。</summary>
public static class SlotTraceEndReasonText
{
    /// <summary>枚举 → 中文（None 返回空串——在架件不显示结束原因）。</summary>
    public static string ToText(SlotTraceEndReason reason) => reason switch
    {
        SlotTraceEndReason.Released => "正常放行",
        SlotTraceEndReason.TakenOut => "中途取出",
        SlotTraceEndReason.AlarmStopped => "报警终止",
        SlotTraceEndReason.AlarmFalse => "误报解除",
        SlotTraceEndReason.ManualStop => "人工终止",
        _ => "",
    };

    /// <summary>中文 → 枚举（供报表筛选下拉反查；认不出返回 None）。</summary>
    public static SlotTraceEndReason FromText(string? text) => text switch
    {
        "正常放行" => SlotTraceEndReason.Released,
        "中途取出" => SlotTraceEndReason.TakenOut,
        "报警终止" => SlotTraceEndReason.AlarmStopped,
        "误报解除" => SlotTraceEndReason.AlarmFalse,
        "人工终止" => SlotTraceEndReason.ManualStop,
        _ => SlotTraceEndReason.None,
    };
}

/// <summary>追溯流水的一行（产品 SN 维度）。</summary>
public sealed class SlotTraceRow
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>全局架号（点表口径 1~N）。</summary>
    public int Rack { get; set; }

    /// <summary>层号（1~3）。</summary>
    public int Level { get; set; }

    /// <summary>库位显示码（A01~J12，客户标签口径）。</summary>
    public string LocationCode { get; set; } = "";

    /// <summary>产品 SN（二维码原文）。</summary>
    public string ProductSn { get; set; } = "";

    /// <summary>扫码绑定时刻。</summary>
    public string BoundAt { get; set; } = "";

    /// <summary>上料确认时刻（二次扫同库位码=上料结束）；空=尚未确认。</summary>
    public string LoadConfirmedAt { get; set; } = "";

    /// <summary>开计时时刻；空=未开计时。</summary>
    public string CuringStartedAt { get; set; } = "";

    /// <summary>计划固化时长（分钟）；0=未知。</summary>
    public int PlannedMinutes { get; set; }

    /// <summary>离架时刻；空=仍在架。</summary>
    public string LeftAt { get; set; } = "";

    /// <summary>实际固化时长（分钟）；-1=未开过计时。</summary>
    public int CuredMinutes { get; set; } = -1;

    /// <summary>结束原因（枚举）。</summary>
    public SlotTraceEndReason EndReason { get; set; } = SlotTraceEndReason.None;

    /// <summary>
    /// 是否经历过光栅报警（1=是）。
    /// </summary>
    public bool AlarmFlag { get; set; }

    /// <summary>
    /// 该产品"进入本轮前"已累计固化的分钟（V0.0.99 续接固化）。
    ///
    /// 干什么：中途取出再加工的产品放回时选【接着之前固化继续】——它之前
    ///   已固化过 N 分钟，本轮只需再固化（计划 − N）即累计达标。这 N 落在
    ///   本列，追溯库盖章时"实际固化(分)"= 本轮时长 + N = **累计**时长，
    ///   正好等于计划时长（达标口径）。
    /// 口径：全新件 = 0；选【完全重新开始固化】的放回件也 = 0（前段作废）。
    /// </summary>
    public int PriorCuredMinutes { get; set; }

    /// <summary>操作员备注（如"中途取出2件"）。</summary>
    public string OperatorNote { get; set; } = "";
}

/// <summary>库位汇总的一行（视图口径：一个库位一条）。</summary>
public sealed class SlotTraceSummaryRow
{
    /// <summary>全局架号。</summary>
    public int Rack { get; set; }

    /// <summary>层号。</summary>
    public int Level { get; set; }

    /// <summary>库位显示码。</summary>
    public string LocationCode { get; set; } = "";

    /// <summary>该库位累计放过多少件产品。</summary>
    public int ProductCount { get; set; }

    /// <summary>正常放行件数。</summary>
    public int FinishedCount { get; set; }

    /// <summary>异常终止件数（中途取出/报警终止/误报解除/人工终止）。</summary>
    public int AbnormalCount { get; set; }

    /// <summary>仍在架件数（没有结束原因）。</summary>
    public int ActiveCount { get; set; }

    /// <summary>首次绑定时刻。</summary>
    public string FirstBoundAt { get; set; } = "";

    /// <summary>最近一次离架时刻。</summary>
    public string LastLeftAt { get; set; } = "";

    /// <summary>累计固化时长（分钟，求和）。</summary>
    public int CuredMinutes { get; set; }
}

/// <summary>报表筛选条件（时间范围/库位码/SN/结束原因，空=不限）。</summary>
public sealed class SlotTraceFilter
{
    /// <summary>绑定时刻起（含）；空=不限。</summary>
    public string? From { get; set; }

    /// <summary>绑定时刻止（含）；空=不限。</summary>
    public string? To { get; set; }

    /// <summary>库位码（支持 A01 精确 / A 前缀模糊）；空=不限。</summary>
    public string? LocationCode { get; set; }

    /// <summary>产品 SN 关键字（模糊包含）；空=不限。</summary>
    public string? ProductSn { get; set; }

    /// <summary>结束原因；None=不限。</summary>
    public SlotTraceEndReason EndReason { get; set; } = SlotTraceEndReason.None;
}

/// <summary>报表顶部的统计数字（统计卡数据源）。</summary>
public sealed class SlotTraceStats
{
    /// <summary>符合条件的记录总数（件）。</summary>
    public int Total { get; set; }

    /// <summary>正常放行件数。</summary>
    public int Finished { get; set; }

    /// <summary>异常终止件数（中途取出等——质量追溯最关心的一档）。</summary>
    public int Abnormal { get; set; }

    /// <summary>仍在架件数（未结束）。</summary>
    public int Active { get; set; }

    /// <summary>涉及库位数（去重）。</summary>
    public int SlotCount { get; set; }
}

/// <summary>
/// 固化追溯库存取（表 slot_product_trace + 视图 slot_trace_summary）。
/// 构造注入 SqliteHelper（测试用临时库，不碰现场库）。
/// </summary>
public sealed class SlotProductTraceStore
{
    private readonly SqliteHelper _db;

    /// <summary>写锁：同进程内写操作串行（SQLite 写锁是文件级的，避免 SQLITE_BUSY）。</summary>
    private readonly object _writeGate = new();

    /// <summary>构造（注入数据库句柄）。</summary>
    public SlotProductTraceStore(SqliteHelper db)
    {
        _db = db;
    }

    /// <summary>
    /// 建表 + 建视图（幂等；启动时装配调一次即可，删表重建也安全）。
    /// 列名用英文 snake_case（中文列名是老 VB 项目遗留，新表不复制）。
    ///
    /// 迁移：老库（V0.0.98 建的那张）缺 prior_cured_minutes 列——SQLite 不
    /// 支持 "ADD COLUMN IF NOT EXISTS"，故先查 pragma table_info 确认列在不在，
    /// 不在再 ALTER TABLE ADD COLUMN（幂等：第二次启动列已存在就不动）。
    /// 列缺省 0 = 全新件（前段无固化），历史行不需要回填。
    /// </summary>
    public void EnsureSchema()
    {
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "CREATE TABLE IF NOT EXISTS slot_product_trace (" +
                "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "rack INTEGER NOT NULL, " +
                "level INTEGER NOT NULL, " +
                "location_code TEXT NOT NULL, " +
                "product_sn TEXT NOT NULL, " +
                "bound_at TEXT NOT NULL, " +
                "load_confirmed_at TEXT, " +
                "curing_started_at TEXT, " +
                "planned_minutes INTEGER NOT NULL DEFAULT 0, " +
                "left_at TEXT, " +
                "cured_minutes INTEGER NOT NULL DEFAULT -1, " +
                "end_reason INTEGER NOT NULL DEFAULT 0, " +
                "alarm_flag INTEGER NOT NULL DEFAULT 0, " +
                "prior_cured_minutes INTEGER NOT NULL DEFAULT 0, " +
                "operator_note TEXT NOT NULL DEFAULT '')", null);

            // 老库补列（幂等：列已在则跳过）。
            if (!ColumnExists("slot_product_trace", "prior_cured_minutes"))
            {
                _db.ExecuteNonQuery(
                    "ALTER TABLE slot_product_trace " +
                    "ADD COLUMN prior_cured_minutes INTEGER NOT NULL DEFAULT 0", null);
            }

            // 库位汇总视图：一个库位一行（现场看"这个库位放过几个、几个正常"）。
            // 用 COUNT/SUM 聚合，异常件 = 结束原因非 0 且非"正常放行"。
            // last_bound_at = 该库位最近一次绑定时刻——报表"时间范围"筛汇总时
            // 按"最近一次绑定落在范围内"判定（V0.0.100 修正：此前按 first_bound_at
            // 筛，老库位晚些再绑产品时筛"今天"反而看不到，与明细口径对不上）。
            // 视图是派生数据，先 DROP 再建零风险——老库（无 last_bound_at 列）重启即升级。
            _db.ExecuteNonQuery("DROP VIEW IF EXISTS slot_trace_summary", null);
            _db.ExecuteNonQuery(
                "CREATE VIEW slot_trace_summary AS " +
                "SELECT rack, level, location_code, " +
                "COUNT(1) AS product_count, " +
                "SUM(CASE WHEN end_reason = 1 THEN 1 ELSE 0 END) AS finished_count, " +
                "SUM(CASE WHEN end_reason <> 0 AND end_reason <> 1 THEN 1 ELSE 0 END) AS abnormal_count, " +
                "SUM(CASE WHEN end_reason = 0 THEN 1 ELSE 0 END) AS active_count, " +
                "MIN(bound_at) AS first_bound_at, " +
                "MAX(bound_at) AS last_bound_at, " +
                "MAX(left_at) AS last_left_at, " +
                "SUM(CASE WHEN cured_minutes > 0 THEN cured_minutes ELSE 0 END) AS cured_minutes " +
                "FROM slot_product_trace GROUP BY rack, level, location_code", null);
        }
    }

    /// <summary>
    /// 列是否存在（pragma table_info；SQLite 无 ADD COLUMN IF NOT EXISTS，
    /// 迁移前必须先问一句）。
    /// 用表值函数 pragma_table_info(@t) 而非 PRAGMA 语句——PRAGMA 不支持
    /// 绑定参数（只能拼字符串，违反"SQL 一律参数化"红线），表值函数可以。
    /// </summary>
    private bool ColumnExists(string table, string column)
    {
        var tableData = _db.ExecuteDataTable(
            "SELECT name FROM pragma_table_info(@t)", new Dictionary<string, object?> { ["@t"] = table });
        foreach (System.Data.DataRow r in tableData.Rows)
        {
            if (string.Equals(r["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------- 写入（调用方必须放后台线程） ----------------

    /// <summary>
    /// 扫码绑定：新增一件产品（该库位可能有多件，逐件一行）。
    ///
    /// priorMinutes=续接件"进入本轮前累计已固化分钟"（0=全新件）——落库后
    /// 追溯库盖章时"实际固化(分)"= 本轮时长 + 本值 = **累计**时长（达标口径）。
    ///
    /// 返回新记录的自增 id（0=写失败）。
    /// </summary>
    public long AddBound(int rack, int level, string productSn, DateTimeOffset at, int priorMinutes = 0)
    {
        if (string.IsNullOrWhiteSpace(productSn))
            return 0;
        string sn = productSn.Trim();
        long id;
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "INSERT INTO slot_product_trace " +
                "(rack, level, location_code, product_sn, bound_at, prior_cured_minutes) " +
                "VALUES (@rack, @level, @loc, @sn, @bound, @prior)", new Dictionary<string, object?>
                {
                    ["@rack"] = rack,
                    ["@level"] = level,
                    ["@loc"] = SlotLocationCode.EncodeInRack(rack, level),
                    ["@sn"] = sn,
                    ["@bound"] = FormatTime(at),
                    ["@prior"] = priorMinutes < 0 ? 0 : priorMinutes,
                });
            object? idObj = _db.ExecuteScalar(
                "SELECT last_insert_rowid()", null);
            id = idObj == null ? 0 : Convert.ToInt64(idObj);
        }
        // 重新上料 → 该产品不再是"待放回"件（出台账）。
        // 放在写锁之外：字典操作有自己的锁，嵌套无必要。
        RemoveTakeOut(sn);
        return id;
    }

    /// <summary>
    /// 上料确认（二次扫同库位码=上料结束）：给该库位所有"尚未结束"的记录盖
    /// 确认时刻。只更新未结束的——已结束的历史件不受影响。
    /// </summary>
    public void MarkLoadConfirmed(int rack, int level, DateTimeOffset at)
    {
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "UPDATE slot_product_trace SET load_confirmed_at = @at " +
                "WHERE rack = @rack AND level = @level AND end_reason = 0 " +
                "AND (load_confirmed_at IS NULL OR load_confirmed_at = '')",
                new Dictionary<string, object?>
                {
                    ["@at"] = FormatTime(at),
                    ["@rack"] = rack,
                    ["@level"] = level,
                });
        }
    }

    /// <summary>
    /// 开计时：给该库位所有未结束记录盖开计时时刻与计划时长。
    /// </summary>
    public void MarkCuringStarted(int rack, int level, DateTimeOffset at, int plannedMinutes)
    {
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "UPDATE slot_product_trace SET curing_started_at = @at, planned_minutes = @pm " +
                "WHERE rack = @rack AND level = @level AND end_reason = 0",
                new Dictionary<string, object?>
                {
                    ["@at"] = FormatTime(at),
                    ["@pm"] = plannedMinutes,
                    ["@rack"] = rack,
                    ["@level"] = level,
                });
        }
    }

    /// <summary>
    /// 离架（托盘离开 / 报警终止）：给该库位所有未结束记录盖离架时刻、
    /// 实际固化时长与结束原因。curedMinutes=-1 表示没开过计时。
    ///
    /// 累计口径（V0.0.99）：实际固化时长 = 本轮时长 + 该件的"前段已固化
    /// 分钟"（prior_cured_minutes）——续接件放回后累计正好达计划时长；
    /// 全新件 prior=0，累计=本轮，两者同一口径可比。
    /// </summary>
    public void MarkLeft(int rack, int level, DateTimeOffset at, int curedMinutes,
        SlotTraceEndReason reason, bool alarmFlag = false, string? note = null)
    {
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "UPDATE slot_product_trace SET left_at = @at, " +
                "cured_minutes = CASE WHEN @cm < 0 THEN -1 ELSE @cm + prior_cured_minutes END, " +
                "end_reason = @er, " +
                "alarm_flag = CASE WHEN @af = 1 THEN 1 ELSE alarm_flag END, " +
                "operator_note = CASE WHEN @note IS NULL OR @note = '' THEN operator_note ELSE @note END " +
                "WHERE rack = @rack AND level = @level AND end_reason = 0",
                new Dictionary<string, object?>
                {
                    ["@at"] = FormatTime(at),
                    ["@cm"] = curedMinutes,
                    ["@er"] = (int)reason,
                    ["@af"] = alarmFlag ? 1 : 0,
                    ["@note"] = note,
                    ["@rack"] = rack,
                    ["@level"] = level,
                });
        }
    }

    /// <summary>
    /// 中途取出（单件）：只给这一件 SN 盖离架时刻与"中途取出"原因——
    /// 同一库位里其他产品继续固化，不受影响（这正是报警确认里
    /// 【继续计时】分支要表达的语义）。
    ///
    /// 累计口径同 MarkLeft：本轮时长 + 前段时长 = 累计（续接件再被取出时
    /// 累计要接着算，否则"接着之前固化继续"的基准会算少）。
    ///
    /// 返回该件的**累计**已固化分钟（-1=没开过计时）；调用方据此登记
    /// "待放回"台账（放回时弹窗问口径要用这个数）。
    /// </summary>
    public int MarkTakenOut(int rack, int level, string productSn, DateTimeOffset at,
        int curedMinutes, bool alarmFlag = true, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(productSn))
            return -1;
        string sn = productSn.Trim();
        int total = -1;
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "UPDATE slot_product_trace SET left_at = @at, " +
                "cured_minutes = CASE WHEN @cm < 0 THEN -1 ELSE @cm + prior_cured_minutes END, " +
                "end_reason = @er, " +
                "alarm_flag = 1, " +
                "operator_note = CASE WHEN @note IS NULL OR @note = '' THEN operator_note ELSE @note END " +
                "WHERE rack = @rack AND level = @level AND product_sn = @sn AND end_reason = 0",
                new Dictionary<string, object?>
                {
                    ["@at"] = FormatTime(at),
                    ["@cm"] = curedMinutes,
                    ["@er"] = (int)SlotTraceEndReason.TakenOut,
                    ["@note"] = note,
                    ["@rack"] = rack,
                    ["@level"] = level,
                    ["@sn"] = sn,
                });
            // 回读累计值（写完再读一次，保证台账登记的数与库里一致）。
            object? v = _db.ExecuteScalar(
                "SELECT cured_minutes FROM slot_product_trace " +
                "WHERE rack = @rack AND level = @level AND product_sn = @sn AND end_reason = @er " +
                "ORDER BY id DESC LIMIT 1",
                new Dictionary<string, object?>
                {
                    ["@rack"] = rack,
                    ["@level"] = level,
                    ["@sn"] = sn,
                    ["@er"] = (int)SlotTraceEndReason.TakenOut,
                });
            total = v == null || v == DBNull.Value ? -1 : Convert.ToInt32(v);
        }
        // 登记"待放回"台账：这件产品被中途取出过，再放回来时要问固化口径。
        if (total >= 0)
        {
            RegisterTakeOut(new SlotTakeOutInfo
            {
                ProductSn = sn,
                Rack = rack,
                Level = level,
                CuredMinutes = total,
                At = at,
            });
        }
        return total;
    }

    /// <summary>
    /// 标记经历过光栅报警（该库位所有未结束记录盖 alarm_flag=1）——
    /// 现场回查"这个库位报警过没有"用。
    /// </summary>
    public void MarkAlarm(int rack, int level)
    {
        lock (_writeGate)
        {
            _db.ExecuteNonQuery(
                "UPDATE slot_product_trace SET alarm_flag = 1 " +
                "WHERE rack = @rack AND level = @level AND end_reason = 0",
                new Dictionary<string, object?>
                {
                    ["@rack"] = rack,
                    ["@level"] = level,
                });
        }
    }

    // ---------------- 查询（报表用；调用方放后台线程） ----------------

    /// <summary>按条件查流水（默认按绑定时刻倒序，最新的在最前）。</summary>
    public List<SlotTraceRow> Query(SlotTraceFilter filter, int skip, int take)
    {
        var (where, ps) = BuildWhere(filter);
        string sql =
            "SELECT id, rack, level, location_code, product_sn, bound_at, " +
            "COALESCE(load_confirmed_at,'') AS load_confirmed_at, " +
            "COALESCE(curing_started_at,'') AS curing_started_at, " +
            "planned_minutes, COALESCE(left_at,'') AS left_at, cured_minutes, " +
            "end_reason, alarm_flag, prior_cured_minutes, operator_note " +
            "FROM slot_product_trace" + where + " ORDER BY id DESC LIMIT @take OFFSET @skip";
        ps["@take"] = take;
        ps["@skip"] = skip;

        var rows = new List<SlotTraceRow>();
        var table = _db.ExecuteDataTable(sql, ps);
        foreach (System.Data.DataRow r in table.Rows)
        {
            rows.Add(new SlotTraceRow
            {
                Id = Convert.ToInt64(r["id"]),
                Rack = Convert.ToInt32(r["rack"]),
                Level = Convert.ToInt32(r["level"]),
                LocationCode = r["location_code"]?.ToString() ?? "",
                ProductSn = r["product_sn"]?.ToString() ?? "",
                BoundAt = r["bound_at"]?.ToString() ?? "",
                LoadConfirmedAt = r["load_confirmed_at"]?.ToString() ?? "",
                CuringStartedAt = r["curing_started_at"]?.ToString() ?? "",
                PlannedMinutes = Convert.ToInt32(r["planned_minutes"]),
                LeftAt = r["left_at"]?.ToString() ?? "",
                CuredMinutes = Convert.ToInt32(r["cured_minutes"]),
                EndReason = (SlotTraceEndReason)Convert.ToInt32(r["end_reason"]),
                AlarmFlag = Convert.ToInt32(r["alarm_flag"]) != 0,
                PriorCuredMinutes = Convert.ToInt32(r["prior_cured_minutes"]),
                OperatorNote = r["operator_note"]?.ToString() ?? "",
            });
        }
        return rows;
    }

    /// <summary>按条件数流水条数（分页用）。</summary>
    public int Count(SlotTraceFilter filter)
    {
        var (where, ps) = BuildWhere(filter);
        object? v = _db.ExecuteScalar(
            "SELECT COUNT(1) FROM slot_product_trace" + where, ps);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    /// <summary>
    /// 统计卡数据源：总数 / 正常 / 异常 / 在架 / 涉及库位数。
    /// 一条 SQL 聚合出来（不拉明细进内存统计——数据量大时省时省内存）。
    /// </summary>
    public SlotTraceStats Stats(SlotTraceFilter filter)
    {
        var (where, ps) = BuildWhere(filter);
        // 多列聚合取**整行**（ExecuteScalar 只回第一列——用错会永远拿到 0）。
        var row = _db.ExecuteDataRow(
            "SELECT COUNT(1), " +
            "SUM(CASE WHEN end_reason = 1 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN end_reason <> 0 AND end_reason <> 1 THEN 1 ELSE 0 END), " +
            "SUM(CASE WHEN end_reason = 0 THEN 1 ELSE 0 END), " +
            "COUNT(DISTINCT rack || '-' || level) " +
            "FROM slot_product_trace" + where, ps);
        var s = new SlotTraceStats();
        if (row == null)
            return s;
        s.Total = ToInt(row[0]);
        s.Finished = ToInt(row[1]);
        s.Abnormal = ToInt(row[2]);
        s.Active = ToInt(row[3]);
        s.SlotCount = ToInt(row[4]);
        return s;
    }

    /// <summary>
    /// 库位汇总（视图口径：一个库位一行）。筛选口径：库位码模糊 + 时间范围
    /// 按"最近一次绑定"落在范围内。
    /// </summary>
    public List<SlotTraceSummaryRow> QuerySummary(SlotTraceFilter filter, int skip, int take)
    {
        var (where, ps) = BuildSummaryWhere(filter);
        string sql =
            "SELECT rack, level, location_code, product_count, finished_count, " +
            "abnormal_count, active_count, COALESCE(first_bound_at,'') AS first_bound_at, " +
            "COALESCE(last_left_at,'') AS last_left_at, cured_minutes " +
            "FROM slot_trace_summary" + where +
            " ORDER BY rack, level LIMIT @take OFFSET @skip";
        ps["@take"] = take;
        ps["@skip"] = skip;

        var rows = new List<SlotTraceSummaryRow>();
        var table = _db.ExecuteDataTable(sql, ps);
        foreach (System.Data.DataRow r in table.Rows)
        {
            rows.Add(new SlotTraceSummaryRow
            {
                Rack = Convert.ToInt32(r["rack"]),
                Level = Convert.ToInt32(r["level"]),
                LocationCode = r["location_code"]?.ToString() ?? "",
                ProductCount = Convert.ToInt32(r["product_count"]),
                FinishedCount = Convert.ToInt32(r["finished_count"]),
                AbnormalCount = Convert.ToInt32(r["abnormal_count"]),
                ActiveCount = Convert.ToInt32(r["active_count"]),
                FirstBoundAt = r["first_bound_at"]?.ToString() ?? "",
                LastLeftAt = r["last_left_at"]?.ToString() ?? "",
                CuredMinutes = Convert.ToInt32(r["cured_minutes"]),
            });
        }
        return rows;
    }

    /// <summary>库位汇总条数（分页用）。</summary>
    public int CountSummary(SlotTraceFilter filter)
    {
        var (where, ps) = BuildSummaryWhere(filter);
        object? v = _db.ExecuteScalar(
            "SELECT COUNT(1) FROM slot_trace_summary" + where, ps);
        return v == null ? 0 : Convert.ToInt32(v);
    }

    // ---------------- 待放回台账（V0.0.99：中途取出后再放回） ----------------

    /// <summary>
    /// 待放回台账锁（字典读写；UI 线程读、后台线程写）。
    /// </summary>
    private readonly object _takeOutGate = new();

    /// <summary>
    /// 待放回台账（SN → 取出履历）：内存索引，扫码判定时**同步纯内存查**
    /// （铁律 2：UI 线程不做 DB IO，所以库里查出来的结果缓存在这里）。
    /// 真源仍是 SQLite（slot_product_trace 里 end_reason=中途取出 的行）——
    /// 本索引是它的运行期投影，启动时后台装载一次，之后随事件增量维护。
    /// </summary>
    private readonly Dictionary<string, SlotTakeOutInfo> _takeOutIndex = new();

    /// <summary>
    /// 查某产品当前是不是"待放回"件（中途取出过、尚未重新上料）。
    /// 返回 null=没有取出履历（全新产品，按正常上料处理）。
    /// 线程安全（UI 线程读 / 后台线程写都可以）。
    /// </summary>
    public SlotTakeOutInfo? TryGetTakeOut(string? productSn)
    {
        if (string.IsNullOrWhiteSpace(productSn))
            return null;
        lock (_takeOutGate)
        {
            return _takeOutIndex.TryGetValue(productSn.Trim(), out var info) ? info : null;
        }
    }

    /// <summary>
    /// 登记一件"待放回"产品（中途取出时调；同 SN 覆盖=又取出了一次）。
    /// </summary>
    private void RegisterTakeOut(SlotTakeOutInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ProductSn))
            return;
        lock (_takeOutGate)
            _takeOutIndex[info.ProductSn.Trim()] = info;
    }

    /// <summary>
    /// 移出待放回台账（产品重新上料时调——它已经回到产线上了）。
    /// </summary>
    private void RemoveTakeOut(string? productSn)
    {
        if (string.IsNullOrWhiteSpace(productSn))
            return;
        lock (_takeOutGate)
            _takeOutIndex.Remove(productSn.Trim());
    }

    /// <summary>
    /// 装载待放回台账（启动时后台调一次，DB IO——铁律 2）。
    ///
    /// 判定口径：某个 SN 的**最新一条**记录是"中途取出"（end_reason=2）
    /// → 该产品当前是待放回件。若它后来重新上料过，会有更新的一条记录
    /// （end_reason=0 或放行），自然就不在结果里了——不需要额外的状态位。
    /// </summary>
    public int LoadTakeOutIndex()
    {
        var table = _db.ExecuteDataTable(
            "SELECT t.rack, t.level, t.product_sn, t.cured_minutes, t.left_at " +
            "FROM slot_product_trace t " +
            "WHERE t.end_reason = @er " +
            "  AND t.id = (SELECT MAX(t2.id) FROM slot_product_trace t2 " +
            "               WHERE t2.product_sn = t.product_sn)",
            new Dictionary<string, object?> { ["@er"] = (int)SlotTraceEndReason.TakenOut });

        var items = new List<SlotTakeOutInfo>();
        foreach (System.Data.DataRow r in table.Rows)
        {
            string sn = r["product_sn"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(sn))
                continue;
            items.Add(new SlotTakeOutInfo
            {
                ProductSn = sn,
                Rack = Convert.ToInt32(r["rack"]),
                Level = Convert.ToInt32(r["level"]),
                CuredMinutes = Convert.ToInt32(r["cured_minutes"]),
                At = ParseTime(r["left_at"]?.ToString()),
            });
        }

        lock (_takeOutGate)
        {
            _takeOutIndex.Clear();
            foreach (var info in items)
                _takeOutIndex[info.ProductSn] = info;
        }
        return items.Count;
    }

    /// <summary>
    /// 待放回清单（报表/弹窗用；台账是内存投影，直接拷出去即可）。
    /// </summary>
    public List<SlotTakeOutInfo> GetTakeOutLedger()
    {
        lock (_takeOutGate)
            return _takeOutIndex.Values.OrderBy(i => i.At).ToList();
    }

    // ---------------- 内部 ----------------

    /// <summary>时间统一格式（与 cure_record 一致：yyyy-MM-dd HH:mm:ss，字典序=时间序）。</summary>
    private static string FormatTime(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// 时间文本 → 时间点（台账装载时还原取出时刻；解析不了回纪元——
    /// 台账只用来排序与展示时刻，解析失败不影响判定）。
    /// </summary>
    private static DateTimeOffset ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DateTimeOffset.MinValue;
        return DateTimeOffset.TryParse(text, out var t) ? t : DateTimeOffset.MinValue;
    }

    /// <summary>流水筛选条件 → WHERE 子句 + 参数字典（一律参数化，禁止拼接）。</summary>
    private static (string Where, Dictionary<string, object?> Params) BuildWhere(SlotTraceFilter filter)
    {
        var parts = new List<string>();
        var ps = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(filter.From))
        {
            parts.Add("bound_at >= @from");
            ps["@from"] = filter.From;
        }
        if (!string.IsNullOrWhiteSpace(filter.To))
        {
            parts.Add("bound_at <= @to");
            ps["@to"] = filter.To;
        }
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
        {
            parts.Add("location_code LIKE @loc");
            ps["@loc"] = filter.LocationCode.Trim() + "%";
        }
        if (!string.IsNullOrWhiteSpace(filter.ProductSn))
        {
            parts.Add("product_sn LIKE @sn");
            ps["@sn"] = "%" + filter.ProductSn.Trim() + "%";
        }
        if (filter.EndReason != SlotTraceEndReason.None)
        {
            parts.Add("end_reason = @er");
            ps["@er"] = (int)filter.EndReason;
        }
        return (parts.Count > 0 ? " WHERE " + string.Join(" AND ", parts) : "", ps);
    }

    /// <summary>汇总筛选条件 → WHERE 子句 + 参数字典（时间范围按最近一次绑定时刻口径）。</summary>
    private static (string Where, Dictionary<string, object?> Params) BuildSummaryWhere(SlotTraceFilter filter)
    {
        var parts = new List<string>();
        var ps = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(filter.LocationCode))
        {
            parts.Add("location_code LIKE @loc");
            ps["@loc"] = filter.LocationCode.Trim() + "%";
        }
        if (!string.IsNullOrWhiteSpace(filter.From))
        {
            parts.Add("last_bound_at >= @from");
            ps["@from"] = filter.From;
        }
        if (!string.IsNullOrWhiteSpace(filter.To))
        {
            parts.Add("last_bound_at <= @to");
            ps["@to"] = filter.To;
        }
        return (parts.Count > 0 ? " WHERE " + string.Join(" AND ", parts) : "", ps);
    }

    /// <summary>聚合结果转 int（SQLite SUM 空集返回 null）。</summary>
    private static int ToInt(object? v) =>
        v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
}
