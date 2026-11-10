// =========================================================================
// TraceReportViewModel：固化追溯报表窗编排（程序①私有）
//
// 干什么：把本地追溯库（SQLite：产品 SN 流水表 + 库位汇总视图）做成一个
//   可筛选、可翻页、可导出的表格预览界面——客户要"记录能追溯"，现场要能
//   按时间/库位/SN 查到某个产品固化了多久、为什么没固化够。
// 界面口径参考 HJVision「视觉数据报表」（筛选行 + 统计卡行 + 圆角边框表格
//   + 底栏分页/导出），但配色走本项目主题键板（双主题同名），不照抄硬编码色。
// 数据纪律：
//   ① 查库一律后台线程（UI 线程禁 DB IO——铁律 2），结果封送回 UI 线程填表；
//   ② 统计卡与表格用**同一次查询**的口径（否则两处数字对不上，现场会不信）；
//   ③ 导出导的是**当前筛选下的全部记录**（不分页）——现场导出就是为了给
//      质量部看全量，导半页会被追着问"另一半呢"。
// 怎么改：加筛选维度 = 加一个 VM 属性 + BuildFilter 里加一行 + 表格加一列；
//   新统计卡 = 加一个统计属性 + Stats 里加一行 + XAML 加一张卡。
// =========================================================================

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CureLine.Core.Data;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

/// <summary>追溯报表窗 VM（纯 VM，无 WPF 依赖——测试可直驱）。</summary>
public sealed class TraceReportViewModel : ViewModelBase
{
    /// <summary>每页行数（本地库数据量不大，200 足够一屏；HJVision 同款思路）。</summary>
    public const int PageSize = 200;

    private readonly SlotProductTraceStore _store;

    /// <summary>
    /// 导出路径询问委托（宿主注入：UI 线程弹保存对话框；返回 null=用户取消）。
    /// VM 不直接 new 任何 UI 窗（纪律）。
    /// </summary>
    private readonly Func<string?>? _askSavePath;

    /// <summary>构造（注入追溯库；askSavePath 可空=不支持导出）。</summary>
    public TraceReportViewModel(SlotProductTraceStore store, Func<string?>? askSavePath = null)
    {
        _store = store;
        _askSavePath = askSavePath;
        ReasonOptions = new ObservableCollection<string> { "全部" };
        foreach (var reason in new[]
                 {
                     SlotTraceEndReason.Released, SlotTraceEndReason.TakenOut,
                     SlotTraceEndReason.AlarmStopped, SlotTraceEndReason.AlarmFalse,
                     SlotTraceEndReason.ManualStop,
                 })
        {
            ReasonOptions.Add(SlotTraceEndReasonText.ToText(reason));
        }

        QueryCommand = new RelayCommand(() => QueryAsync(resetPage: true));
        ResetCommand = new RelayCommand(() => ResetFilters());
        PrevPageCommand = new RelayCommand(() => QueryAsync(resetPage: false), () => Page > 1);
        NextPageCommand = new RelayCommand(() => QueryAsync(resetPage: false), () => Page < PageCount);
        ExportCommand = new RelayCommand(() => ExportCsv());
        // 页签命令（V0.0.97：页签钮用 Command 切视图，不用 Radio 的 IsChecked
        // 双向绑定——Radio 未勾选时 IsChecked=null，双向绑 bool 属性会把 null
        // 写回 false，开窗瞬间视图被误切走。Command 链无此坑，勾选态仍由
        // Radio 的 GroupName 互斥自动管）。
        ShowDetailCommand = new RelayCommand(() => ShowDetail = true);
        ShowSummaryCommand = new RelayCommand(() => ShowDetail = false);
    }

    // ---------------- 筛选条件 ----------------

    private string _fromText = "";
    /// <summary>绑定时刻起（yyyy-MM-dd；空=不限）。</summary>
    public string FromText
    {
        get => _fromText;
        set => Set(ref _fromText, value);
    }

    private string _toText = "";
    /// <summary>绑定时刻止（yyyy-MM-dd；空=不限）。</summary>
    public string ToText
    {
        get => _toText;
        set => Set(ref _toText, value);
    }

    private string _locationText = "";
    /// <summary>库位码（如 A01；支持前缀模糊；空=不限）。</summary>
    public string LocationText
    {
        get => _locationText;
        set => Set(ref _locationText, value);
    }

    private string _snText = "";
    /// <summary>产品 SN 关键字（模糊包含；空=不限）。</summary>
    public string SnText
    {
        get => _snText;
        set => Set(ref _snText, value);
    }

    private string _reasonText = "全部";
    /// <summary>结束原因（"全部"=不限）。</summary>
    public string ReasonText
    {
        get => _reasonText;
        set => Set(ref _reasonText, value);
    }

    /// <summary>结束原因下拉项（"全部" + 各原因中文）。</summary>
    public ObservableCollection<string> ReasonOptions { get; }

    // ---------------- 统计卡 ----------------

    private string _totalText = "0";
    /// <summary>统计卡：记录总数（件）。</summary>
    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    private string _finishedText = "0";
    /// <summary>统计卡：正常放行（件）。</summary>
    public string FinishedText
    {
        get => _finishedText;
        private set => Set(ref _finishedText, value);
    }

    private string _abnormalText = "0";
    /// <summary>统计卡：异常终止（件——质量追溯最关心的一档）。</summary>
    public string AbnormalText
    {
        get => _abnormalText;
        private set => Set(ref _abnormalText, value);
    }

    private string _activeText = "0";
    /// <summary>统计卡：仍在架（件）。</summary>
    public string ActiveText
    {
        get => _activeText;
        private set => Set(ref _activeText, value);
    }

    private string _slotCountText = "0";
    /// <summary>统计卡：涉及库位数。</summary>
    public string SlotCountText
    {
        get => _slotCountText;
        private set => Set(ref _slotCountText, value);
    }

    // ---------------- 表格数据 ----------------

    /// <summary>产品明细行（流水表口径）。</summary>
    public ObservableCollection<SlotTraceRow> Rows { get; } = new();

    /// <summary>库位汇总行（视图口径）。</summary>
    public ObservableCollection<SlotTraceSummaryRow> SummaryRows { get; } = new();

    private bool _showDetail = true;
    /// <summary>当前看产品明细（true）/ 库位汇总（false）。</summary>
    public bool ShowDetail
    {
        get => _showDetail;
        set
        {
            if (Set(ref _showDetail, value))
                Raise(nameof(ShowSummary));
        }
    }

    /// <summary>当前看库位汇总（ShowDetail 取反——XAML 第二只页签钮只显示不回写）。</summary>
    public bool ShowSummary => !ShowDetail;

    /// <summary>切到产品明细视图（页签钮命令）。</summary>
    public RelayCommand ShowDetailCommand { get; }

    /// <summary>切到库位汇总视图（页签钮命令）。</summary>
    public RelayCommand ShowSummaryCommand { get; }

    // ---------------- 分页 ----------------

    private int _page = 1;
    /// <summary>当前页（1 起）。</summary>
    public int Page
    {
        get => _page;
        private set
        {
            if (Set(ref _page, value))
            {
                Raise(nameof(PageText));
                PrevPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private int _pageCount = 1;
    /// <summary>总页数（至少 1）。</summary>
    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (Set(ref _pageCount, value))
            {
                Raise(nameof(PageText));
                PrevPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _pageText = "第 1/1 页";
    /// <summary>底栏页码文本（"第 1/3 页 · 共 420 条"）。</summary>
    public string PageText
    {
        get => _pageText;
        private set => Set(ref _pageText, value);
    }

    private string _statusText = "";
    /// <summary>底栏状态文本（查询结果摘要 / 导出结果）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ---------------- 命令 ----------------

    /// <summary>查询（重新查第一页）。</summary>
    public RelayCommand QueryCommand { get; }

    /// <summary>重置筛选条件（清空并重查）。</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>上一页。</summary>
    public RelayCommand PrevPageCommand { get; }

    /// <summary>下一页。</summary>
    public RelayCommand NextPageCommand { get; }

    /// <summary>导出当前筛选的全部记录为 CSV。</summary>
    public RelayCommand ExportCommand { get; }

    // ---------------- 查询 ----------------

    /// <summary>
    /// 执行查询（后台线程查库 → 回 UI 线程填表）。resetPage=true=回到第一页。
    /// </summary>
    public void QueryAsync(bool resetPage = true)
    {
        if (resetPage)
            Page = 1;
        var filter = BuildFilter();
        int skip = (Page - 1) * PageSize;
        Task.Run(() =>
        {
            var stats = _store.Stats(filter);
            var rows = _store.Query(filter, skip, PageSize);
            var total = _store.Count(filter);
            var summary = _store.QuerySummary(filter, 0, 500);
            return (stats, rows, total, summary);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                StatusText = "查询失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误");
                return;
            }
            var (stats, rows, total, summary) = t.Result;
            UiInvoke(() =>
            {
                Rows.Clear();
                foreach (var r in rows)
                    Rows.Add(r);
                SummaryRows.Clear();
                foreach (var r in summary)
                    SummaryRows.Add(r);
                TotalText = stats.Total.ToString("N0");
                FinishedText = stats.Finished.ToString("N0");
                AbnormalText = stats.Abnormal.ToString("N0");
                ActiveText = stats.Active.ToString("N0");
                SlotCountText = stats.SlotCount.ToString("N0");
                PageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (Page > PageCount)
                    Page = PageCount;
                PageText = $"第 {Page}/{PageCount} 页 · 共 {total} 条";
                StatusText = $"共 {total} 条记录 · 本页 {rows.Count} 条";
            });
        });
    }

    /// <summary>重置筛选条件并重查。</summary>
    public void ResetFilters()
    {
        FromText = "";
        ToText = "";
        LocationText = "";
        SnText = "";
        ReasonText = "全部";
        QueryAsync(resetPage: true);
    }

    /// <summary>VM 属性 → Core 筛选对象（日期补成整天区间）。</summary>
    private SlotTraceFilter BuildFilter()
    {
        return new SlotTraceFilter
        {
            From = ParseDay(FromText, startOfDay: true),
            To = ParseDay(ToText, startOfDay: false),
            LocationCode = string.IsNullOrWhiteSpace(LocationText) ? null : LocationText.Trim(),
            ProductSn = string.IsNullOrWhiteSpace(SnText) ? null : SnText.Trim(),
            EndReason = ReasonText == "全部"
                ? SlotTraceEndReason.None
                : SlotTraceEndReasonText.FromText(ReasonText),
        };
    }

    /// <summary>日期文本 → 库里的时间字符串（yyyy-MM-dd HH:mm:ss）。</summary>
    private static string? ParseDay(string? text, bool startOfDay)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime day))
            return null;
        return startOfDay
            ? day.ToString("yyyy-MM-dd 00:00:00")
            : day.ToString("yyyy-MM-dd 23:59:59");
    }

    // ---------------- 导出 ----------------

    /// <summary>
    /// 导出当前筛选结果为 CSV（UTF-8 带 BOM——Excel 打开中文不乱码）。
    /// 后台线程写文件（UI 线程禁文件 IO 阻塞——铁律 2）；保存路径由宿主在
    /// UI 线程询问（SaveFileDialog 必须在 UI 线程）。
    /// </summary>
    private void ExportCsv()
    {
        if (_askSavePath == null)
        {
            StatusText = "本窗不支持导出";
            return;
        }
        var filter = BuildFilter();
        string? path = _askSavePath();
        if (string.IsNullOrWhiteSpace(path))
            return;
        Task.Run(() =>
        {
            var rows = _store.Query(filter, 0, 100000);
            var sb = new StringBuilder();
            sb.AppendLine("绑定时刻,库位码,架,层,产品SN,上料确认,开计时,计划时长(分),离架时刻,实际固化(分),结束原因,报警,备注");
            foreach (var r in rows)
            {
                sb.AppendLine(string.Join(",",
                    Csv(r.BoundAt),
                    Csv(r.LocationCode),
                    r.Rack.ToString(CultureInfo.InvariantCulture),
                    r.Level.ToString(CultureInfo.InvariantCulture),
                    Csv(r.ProductSn),
                    Csv(r.LoadConfirmedAt),
                    Csv(r.CuringStartedAt),
                    r.PlannedMinutes.ToString(CultureInfo.InvariantCulture),
                    Csv(r.LeftAt),
                    r.CuredMinutes < 0 ? "" : r.CuredMinutes.ToString(CultureInfo.InvariantCulture),
                    Csv(SlotTraceEndReasonText.ToText(r.EndReason)),
                    r.AlarmFlag ? "是" : "",
                    Csv(r.OperatorNote)));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return rows.Count;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                UiInvoke(() => StatusText = "导出失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误"));
                return;
            }
            int count = t.Result;
            UiInvoke(() => StatusText = $"已导出 {count} 条记录到 {path}");
        });
    }

    /// <summary>CSV 字段转义（引号/逗号/换行处理）。</summary>
    private static string Csv(string? value)
    {
        string v = value ?? "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }
}
