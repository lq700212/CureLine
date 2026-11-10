// =========================================================================
// SlotProductTraceStoreTests：固化追溯库（产品 SN 流水表 + 库位汇总视图）
//
// 干什么：锁住追溯库的四条口径——
//   ① 一件产品一行（同一库位多个产品=多行），绑定/确认/开计时/离架逐段盖章；
//   ② 中途取出只盖那一个 SN（同库位其余产品不受影响）；
//   ③ 统计卡口径（总数/正常/异常/在架/库位数）与筛选一致；
//   ④ 库位汇总视图（一个库位一行：件数/正常/异常/在架/累计固化时长）。
// 全部用临时库（TestScope 隔离），不碰现场 DataBase\CureLine.db3。
// =========================================================================

using CureLine.Core.Data;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class SlotProductTraceStoreTests : IDisposable
{
    private readonly string _tmp;

    public SlotProductTraceStoreTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "CureTrace_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    /// <summary>建一个带 schema 的追溯库（临时文件）。</summary>
    private SlotProductTraceStore MakeStore()
    {
        var db = new SqliteHelper(Path.Combine(_tmp, "trace.db3"));
        db.EnsureDatabase();
        var store = new SlotProductTraceStore(db);
        store.EnsureSchema();
        return store;
    }

    /// <summary>构造测试时刻（**本地时区**——库里存的是本地时间字符串，
    /// 用本地时区构造才能让"绑定时刻=期望值"的断言与运行机器时区无关）。</summary>
    private static DateTimeOffset At(int hour = 8, int minute = 0) =>
        new DateTimeOffset(DateTime.Today.AddHours(hour).AddMinutes(minute));

    /// <summary>库里的时间字符串格式（与 Store.FormatTime 同款）。</summary>
    private static string Stamp(int hour, int minute) =>
        DateTime.Today.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-dd HH:mm:ss");

    // ---------- 续接固化（V0.0.99：前段时长 + 本轮 = 累计） ----------

    [Fact(DisplayName = "追溯库：续接件的累计固化时长=前段+本轮（达标口径）")]
    public void Store_ResumeProduct_AccumulatesPriorMinutes()
    {
        var store = MakeStore();

        // 续接件：前段已固化 480 分钟（8 小时），本轮计划 1440（24 小时）
        store.AddBound(1, 1, "SN-R", At(), priorMinutes: 480);
        store.MarkCuringStarted(1, 1, At(8, 1), 1440);
        store.MarkLeft(1, 1, At(8, 2), 960, SlotTraceEndReason.Released);

        var row = Assert.Single(store.Query(new SlotTraceFilter(), 0, 10));
        Assert.Equal(480, row.PriorCuredMinutes);
        // 实际固化 = 本轮 960 + 前段 480 = 1440 = 计划时长（累计达标）
        Assert.Equal(1440, row.CuredMinutes);
        Assert.Equal(1440, row.PlannedMinutes);
    }

    [Fact(DisplayName = "追溯库：全新件的累计=本轮（前段为 0）")]
    public void Store_FreshProduct_CuredEqualsCurrentRound()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-N", At());
        store.MarkLeft(1, 1, At(8, 2), 120, SlotTraceEndReason.Released);

        var row = Assert.Single(store.Query(new SlotTraceFilter(), 0, 10));
        Assert.Equal(0, row.PriorCuredMinutes);
        Assert.Equal(120, row.CuredMinutes);
    }

    [Fact(DisplayName = "追溯库：中途取出登记待放回台账（放回时弹窗问口径的基准）")]
    public void Store_TakenOut_RegistersTakeOutLedger()
    {
        var store = MakeStore();
        store.AddBound(2, 1, "SN-R", At());
        store.MarkCuringStarted(2, 1, At(8, 1), 1440);

        int total = store.MarkTakenOut(2, 1, "SN-R", At(8, 2), 480, alarmFlag: true,
            note: "报警确认取出（补扫）");

        Assert.Equal(480, total);              // 累计已固化分钟
        var info = store.TryGetTakeOut("SN-R");
        Assert.NotNull(info);
        Assert.Equal(2, info!.Rack);
        Assert.Equal(1, info.Level);
        Assert.Equal(480, info.CuredMinutes);
        Assert.Equal("B02", info.LocationCode);
        Assert.Single(store.GetTakeOutLedger());
    }

    [Fact(DisplayName = "追溯库：重新上料=出台账（不再算待放回件）")]
    public void Store_Rebound_RemovesFromTakeOutLedger()
    {
        var store = MakeStore();
        store.AddBound(2, 1, "SN-R", At());
        store.MarkTakenOut(2, 1, "SN-R", At(8, 2), 480);
        Assert.NotNull(store.TryGetTakeOut("SN-R"));

        // 放回新库位重新上料 → 待放回台账移除
        store.AddBound(3, 1, "SN-R", At(9, 0));
        Assert.Null(store.TryGetTakeOut("SN-R"));
        Assert.Empty(store.GetTakeOutLedger());
    }

    [Fact(DisplayName = "追溯库：重启后台账从库里装载（待放回件不丢）")]
    public void Store_LoadTakeOutIndex_RestoresLedger()
    {
        var store = MakeStore();
        store.AddBound(2, 1, "SN-R", At());
        store.MarkTakenOut(2, 1, "SN-R", At(8, 2), 480);
        store.AddBound(2, 2, "SN-N", At());
        store.MarkTakenOut(2, 2, "SN-N", At(8, 3), 60);

        // 模拟重启：新实例从同一个库装载台账
        var db = new SqliteHelper(Path.Combine(_tmp, "trace.db3"));
        var reopened = new SlotProductTraceStore(db);
        int n = reopened.LoadTakeOutIndex();

        Assert.Equal(2, n);
        Assert.Equal(480, reopened.TryGetTakeOut("SN-R")!.CuredMinutes);
        Assert.Equal(60, reopened.TryGetTakeOut("SN-N")!.CuredMinutes);
    }

    [Fact(DisplayName = "追溯库：一件产品一行（同库位多产品=多行）+ 逐段盖章")]
    public void Store_BoundAndStamped_PerProductRows()
    {
        var store = MakeStore();

        store.AddBound(1, 1, "SN-1", At());
        store.AddBound(1, 1, "SN-2", At(8, 1));
        store.MarkLoadConfirmed(1, 1, At(8, 5));
        store.MarkCuringStarted(1, 1, At(8, 6), 120);

        var rows = store.Query(new SlotTraceFilter(), 0, 50);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("A01", r.LocationCode);
            Assert.False(string.IsNullOrEmpty(r.LoadConfirmedAt));
            Assert.False(string.IsNullOrEmpty(r.CuringStartedAt));
            Assert.Equal(120, r.PlannedMinutes);
            Assert.Equal(SlotTraceEndReason.None, r.EndReason);
        });
        Assert.Equal(new[] { "SN-2", "SN-1" }, rows.Select(r => r.ProductSn).ToArray());  // 倒序
    }

    [Fact(DisplayName = "追溯库：离架盖实际固化时长与结束原因（正常放行）")]
    public void Store_MarkLeft_Released()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At());
        store.MarkCuringStarted(1, 1, At(8, 0), 120);
        store.MarkLeft(1, 1, At(10, 0), 120, SlotTraceEndReason.Released);

        var row = store.Query(new SlotTraceFilter(), 0, 10).Single();
        Assert.Equal(120, row.CuredMinutes);
        Assert.Equal(SlotTraceEndReason.Released, row.EndReason);
        Assert.False(string.IsNullOrEmpty(row.LeftAt));
    }

    [Fact(DisplayName = "追溯库：中途取出只盖那一个 SN（同库位其余不受影响）")]
    public void Store_TakenOut_OnlyThatSn()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At());
        store.AddBound(1, 1, "SN-2", At());
        store.MarkCuringStarted(1, 1, At(8, 0), 120);
        store.MarkTakenOut(1, 1, "SN-1", At(8, 30), 30);

        var rows = store.Query(new SlotTraceFilter(), 0, 10);
        var taken = rows.Single(r => r.ProductSn == "SN-1");
        var other = rows.Single(r => r.ProductSn == "SN-2");
        Assert.Equal(SlotTraceEndReason.TakenOut, taken.EndReason);
        Assert.Equal(30, taken.CuredMinutes);
        Assert.True(taken.AlarmFlag);
        Assert.Equal(SlotTraceEndReason.None, other.EndReason);   // 另一个继续固化
        Assert.Equal(-1, other.CuredMinutes);
    }

    [Fact(DisplayName = "追溯库：统计卡口径（总数/正常/异常/在架/库位数）")]
    public void Store_Stats_CountsMatchFilter()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.AddBound(1, 1, "SN-2", At(8, 1));
        store.AddBound(2, 1, "SN-3", At(8, 2));
        store.MarkCuringStarted(1, 1, At(8, 0), 120);

        // 1-1：SN-1 中途取出（异常），SN-2 随后正常放行（库位级盖章只盖未结束件）
        store.MarkTakenOut(1, 1, "SN-1", At(8, 30), 30);
        store.MarkLeft(1, 1, At(9, 0), 60, SlotTraceEndReason.Released);
        // 2-1：SN-3 仍在架（未结束）

        var stats = store.Stats(new SlotTraceFilter());
        Assert.Equal(3, stats.Total);
        Assert.Equal(1, stats.Finished);
        Assert.Equal(1, stats.Abnormal);
        Assert.Equal(1, stats.Active);
        Assert.Equal(2, stats.SlotCount);
    }

    [Fact(DisplayName = "追溯库：筛选（库位码/SN/结束原因/时间范围）")]
    public void Store_Filter_NarrowsResults()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-A", At(8, 0));
        store.AddBound(2, 1, "SN-B", At(9, 0));
        store.MarkLeft(1, 1, At(10, 0), 120, SlotTraceEndReason.Released);

        Assert.Equal(1, store.Count(new SlotTraceFilter { LocationCode = "A01" }));
        Assert.Equal(1, store.Count(new SlotTraceFilter { ProductSn = "SN-B" }));
        Assert.Equal(1, store.Count(new SlotTraceFilter { EndReason = SlotTraceEndReason.Released }));
        Assert.Equal(0, store.Count(new SlotTraceFilter { EndReason = SlotTraceEndReason.TakenOut }));
        Assert.Equal(1, store.Count(new SlotTraceFilter { From = Stamp(8, 30) }));  // 只剩 9:00 那条
        Assert.Equal(1, store.Count(new SlotTraceFilter { To = Stamp(8, 30) }));    // 只剩 8:00 那条
    }

    [Fact(DisplayName = "追溯库：库位汇总视图（一个库位一行，件数/正常/异常/在架）")]
    public void Store_SummaryView_GroupsBySlot()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.AddBound(1, 1, "SN-2", At(8, 1));
        store.AddBound(2, 1, "SN-3", At(8, 2));
        store.MarkTakenOut(1, 1, "SN-1", At(8, 30), 20);
        store.MarkLeft(1, 1, At(9, 0), 60, SlotTraceEndReason.Released);
        store.MarkLeft(2, 1, At(9, 2), 90, SlotTraceEndReason.Released);

        var rows = store.QuerySummary(new SlotTraceFilter(), 0, 50);
        Assert.Equal(2, rows.Count);
        var a01 = rows.Single(r => r.LocationCode == "A01");
        Assert.Equal(2, a01.ProductCount);
        Assert.Equal(1, a01.FinishedCount);
        Assert.Equal(1, a01.AbnormalCount);
        Assert.Equal(0, a01.ActiveCount);
        Assert.Equal(80, a01.CuredMinutes);   // 20 + 60
        var b02 = rows.Single(r => r.LocationCode == "B02");   // 架2层1 → B02
        Assert.Equal(1, b02.ProductCount);
        Assert.Equal(1, b02.FinishedCount);
        Assert.Equal(0, b02.AbnormalCount);
    }

    [Fact(DisplayName = "追溯库：经历报警标记（现场回查用）")]
    public void Store_MarkAlarm_SetsFlag()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At());
        store.MarkAlarm(1, 1);
        Assert.True(store.Query(new SlotTraceFilter(), 0, 10).Single().AlarmFlag);
    }

    [Fact(DisplayName = "追溯库：重复建表幂等（启动每次都调）")]
    public void Store_EnsureSchema_IsIdempotent()
    {
        var store = MakeStore();
        store.EnsureSchema();
        store.EnsureSchema();
        store.AddBound(1, 1, "SN-1", At());
        Assert.Equal(1, store.Count(new SlotTraceFilter()));
    }
}
