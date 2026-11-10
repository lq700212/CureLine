// =========================================================================
// TraceReportViewModelTests：固化追溯报表窗 VM（V0.0.97）
//
// 干什么：锁住报表窗的三条口径——
//   ① 查询后台查库 → 回填明细行与统计卡（统计卡与表格同一口径）；
//   ② 筛选条件生效（库位码/SN/结束原因/时间范围）与重置；
//   ③ 导出 CSV：导当前筛选下的**全部**记录（不分页），UTF-8 带 BOM，
//      首行表头 + 每件一行。
// VM 无 WPF 依赖：UiInvoke 在测试环境（无 SynchronizationContext）同步执行，
// 查询走后台线程 → 用 WaitUntil 等回填完成。
// =========================================================================

using System.Diagnostics;
using System.Text;
using CureLine.Core.Data;
using CureLine.Core.Infrastructure;

namespace CureLine.Tests;

public class TraceReportViewModelTests : IDisposable
{
    private readonly string _tmp;

    public TraceReportViewModelTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "CureReport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    /// <summary>建带 schema 的追溯库（临时文件）。</summary>
    private SlotProductTraceStore MakeStore()
    {
        var db = new SqliteHelper(Path.Combine(_tmp, "report.db3"));
        db.EnsureDatabase();
        var store = new SlotProductTraceStore(db);
        store.EnsureSchema();
        return store;
    }

    private static DateTimeOffset At(int hour, int minute) =>
        new DateTimeOffset(DateTime.Today.AddHours(hour).AddMinutes(minute));

    /// <summary>等异步回填完成（查询走后台线程）。</summary>
    private static void WaitUntil(Func<bool> condition, string what = "等待超时")
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
            Thread.Sleep(10);
        Assert.True(condition(), what);
    }

    [Fact(DisplayName = "报表：查询回填明细行与统计卡（同一次查询口径）")]
    public void Query_FillsRowsAndStats()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.AddBound(2, 1, "SN-2", At(8, 1));
        store.MarkLeft(1, 1, At(9, 0), 120, SlotTraceEndReason.Released);

        var vm = new CureStation.ViewModels.TraceReportViewModel(store);
        vm.QueryAsync();
        WaitUntil(() => vm.Rows.Count > 0);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("2", vm.TotalText);
        Assert.Equal("1", vm.FinishedText);
        Assert.Equal("1", vm.ActiveText);
        Assert.Equal("0", vm.AbnormalText);
        Assert.Equal("2", vm.SlotCountText);
        Assert.Contains("共 2 条", vm.PageText);
    }

    [Fact(DisplayName = "报表：筛选按库位码收窄并可重置")]
    public void Query_FilterByLocation_AndReset()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.AddBound(2, 1, "SN-2", At(8, 1));

        var vm = new CureStation.ViewModels.TraceReportViewModel(store);
        vm.LocationText = "A01";
        vm.QueryAsync();
        WaitUntil(() => vm.Rows.Count > 0);
        Assert.Single(vm.Rows);
        Assert.Equal("A01", vm.Rows[0].LocationCode);

        vm.ResetFilters();
        WaitUntil(() => vm.Rows.Count == 2);
        Assert.Equal("", vm.LocationText);
        Assert.Equal("全部", vm.ReasonText);
    }

    [Fact(DisplayName = "报表：结束原因下拉含全部选项 + 筛选生效")]
    public void Query_FilterByEndReason()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.MarkLeft(1, 1, At(9, 0), 30, SlotTraceEndReason.TakenOut);

        var vm = new CureStation.ViewModels.TraceReportViewModel(store);
        Assert.Equal("全部", vm.ReasonOptions[0]);
        Assert.Contains("中途取出", vm.ReasonOptions);

        vm.ReasonText = "中途取出";
        vm.QueryAsync();
        WaitUntil(() => vm.Rows.Count > 0);
        Assert.Single(vm.Rows);
        Assert.Equal(SlotTraceEndReason.TakenOut, vm.Rows[0].EndReason);
    }

    [Fact(DisplayName = "报表：导出 CSV 导当前筛选下的全部记录（含表头/中文不乱码）")]
    public void Export_WritesAllFilteredRowsWithHeader()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        store.AddBound(1, 1, "SN-2", At(8, 1));
        store.AddBound(2, 1, "SN-3", At(8, 2));

        string path = Path.Combine(_tmp, "out.csv");
        var vm = new CureStation.ViewModels.TraceReportViewModel(store, () => path);
        vm.ExportCommand.Execute(null);
        WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Contains("产品SN", lines[0]);
        Assert.Contains("结束原因", lines[0]);
        Assert.Equal(4, lines.Length);           // 表头 + 3 件
        Assert.Contains(lines, l => l.Contains("SN-1"));
        Assert.Contains(lines, l => l.Contains("SN-3"));

        // UTF-8 BOM：Excel 打开中文不乱码
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact(DisplayName = "报表：页签互斥（明细/汇总）")]
    public void View_ShowDetailToggle_SwitchesMode()
    {
        var store = MakeStore();
        store.AddBound(1, 1, "SN-1", At(8, 0));
        var vm = new CureStation.ViewModels.TraceReportViewModel(store);
        Assert.True(vm.ShowDetail);
        Assert.False(vm.ShowSummary);

        vm.ShowDetail = false;
        Assert.False(vm.ShowDetail);
        Assert.True(vm.ShowSummary);
    }
}
