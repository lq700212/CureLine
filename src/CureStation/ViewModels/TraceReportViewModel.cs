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

/// <summary>
/// 库位格子选择的一组数据（一台固化架一组，与主窗"N号固化架"分组同源）。
/// Codes 是**行优先**排布：第 1 行=全部站的上层、第 2 行=中层、第 3 行=下层
/// ——与主窗仓位卡网格（先层后架）完全对应，操作员在主页看到什么排布，
/// 在下拉里就是什么排布，点格子选库位一目了然。
/// </summary>
public sealed class TraceLocationGroup
{
    /// <summary>构造（组标题/列数/该组库位码集合——行优先）。</summary>
    public TraceLocationGroup(string title, int columns, IReadOnlyList<string> codes)
    {
        Title = title;
        Columns = columns;
        Codes = codes;
    }

    /// <summary>组标题（"1号固化架"——与主窗分组标题同源）。</summary>
    public string Title { get; }

    /// <summary>格子列数（本组架内站数，恒 ≤10；UniformGrid 按它铺列）。</summary>
    public int Columns { get; }

    /// <summary>本组库位码（行优先：先层后站，与主窗卡片网格同序）。</summary>
    public IReadOnlyList<string> Codes { get; }
}

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

    /// <summary>
    /// 构造（注入追溯库；askSavePath 可空=不支持导出；locationGroups=库位格子
    /// 选择数据——宿主按当前点表换算传入，空=下拉里只有"全部"）。
    /// 注意：构造**不查询**（测试可直驱实例化、不会惊动后台线程）——开窗首查
    /// 由宿主装配（TraceReportWindow.Present）触发。
    /// </summary>
    public TraceReportViewModel(SlotProductTraceStore store, Func<string?>? askSavePath = null,
        IReadOnlyList<TraceLocationGroup>? locationGroups = null)
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
        // 库位格子选择：宿主按当前点表换算（一台固化架一组，行优先=先层后站）。
        LocationGroups = locationGroups ?? [];

        QueryCommand = new RelayCommand(() => QueryAsync(resetPage: true));
        ResetCommand = new RelayCommand(() => ResetFilters());
        PrevPageCommand = new RelayCommand(() => QueryAsync(resetPage: false), () => Page > 1);
        NextPageCommand = new RelayCommand(() => QueryAsync(resetPage: false), () => Page < PageCount);
        ExportCommand = new RelayCommand(() => ExportCsv());
        // 库位格子点选（参数=库位码或"全部"）：格子/全部行共用一个命令。
        SelectLocationCommand = new RelayCommand(code => LocationText = (string)code!);
        // 页签命令（V0.0.97：页签钮用 Command 切视图，不用 Radio 的 IsChecked
        // 双向绑定——Radio 未勾选时 IsChecked=null，双向绑 bool 属性会把 null
        // 写回 false，开窗瞬间视图被误切走。Command 链无此坑，勾选态仍由
        // Radio 的 GroupName 互斥自动管）。
        ShowDetailCommand = new RelayCommand(() => ShowDetail = true);
        ShowSummaryCommand = new RelayCommand(() => ShowDetail = false);
    }

    // ---------------- 筛选条件 ----------------

    /// <summary>起始缺省时刻（当天 00:00）。</summary>
    public const string StartDefaultTime = "00:00";

    /// <summary>结束缺省时刻（23:59=当天收尾——下拉里单列此项，选 23:30
    /// 会丢掉最后半小时的记录）。</summary>
    public const string EndDefaultTime = "23:59";

    /// <summary>库位下拉的"不限"项（首项恒为此值）。</summary>
    public const string LocationAll = "全部";

    /// <summary>
    /// 时刻下拉选项（30 分钟步进 + 23:59 收尾）。静态列表只建一次，
    /// 起止两个下拉共用同一份。
    /// </summary>
    public IReadOnlyList<string> TimeOptions { get; } = BuildTimeOptions();

    private static List<string> BuildTimeOptions()
    {
        var list = new List<string>(49);
        for (int h = 0; h < 24; h++)
        for (int m = 0; m <= 30; m += 30)
            list.Add($"{h:00}:{m:00}");
        list.Add(EndDefaultTime);   // 23:59 收尾（当天最后一分钟的记录也能筛到）
        return list;
    }

    private DateTime? _fromDate;
    /// <summary>绑定时刻起（日期；空=不限）——DatePicker 点选。</summary>
    public DateTime? FromDate
    {
        get => _fromDate;
        set => Set(ref _fromDate, value);
    }

    private DateTime? _toDate;
    /// <summary>绑定时刻止（日期；空=不限）——DatePicker 点选。</summary>
    public DateTime? ToDate
    {
        get => _toDate;
        set => Set(ref _toDate, value);
    }

    private string _fromTime = StartDefaultTime;
    /// <summary>起始时刻（HH:mm 下拉点选；缺省 00:00）。</summary>
    public string FromTime
    {
        get => _fromTime;
        set => Set(ref _fromTime, value);
    }

    private string _toTime = EndDefaultTime;
    /// <summary>结束时刻（HH:mm 下拉点选；缺省 23:59）。</summary>
    public string ToTime
    {
        get => _toTime;
        set => Set(ref _toTime, value);
    }

    private string _locationText = LocationAll;
    /// <summary>库位码（格子点选；"全部"=不限）。</summary>
    public string LocationText
    {
        get => _locationText;
        set => Set(ref _locationText, value);
    }

    /// <summary>
    /// 库位格子选择数据（一台固化架一组；宿主按当前点表换算传入）。
    /// 下拉面板按它铺格子——与主窗仓位卡排布一一对应，点格子选库位。
    /// </summary>
    public IReadOnlyList<TraceLocationGroup> LocationGroups { get; }

    /// <summary>
    /// 是否显示组标题条（多台固化架才显示"N号固化架"；单架只有一排格子，
    /// 标题多余——与主窗"组数>1 才显示标题条"同口径）。
    /// </summary>
    public bool ShowGroupHeaders => LocationGroups.Count > 1;

    /// <summary>库位格子点选（参数=库位码或"全部"——格子/全部行共用）。</summary>
    public RelayCommand SelectLocationCommand { get; }

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
            {
                Raise(nameof(ShowSummary));
                // 切页签=按当前筛选重查该视图（页码归一回第一页）——两个视图
                // 条数口径不同（件数 vs 库位数），共用一个页码会"翻页没反应"
                // （明细第 3 页翻页，汇总还是那几行，现场以为按钮坏了）。
                QueryAsync(resetPage: true);
            }
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
    /// 查哪个视图按当前页签定：明细=件数分页，汇总=库位数分页（两套条数口径，
    /// 切页签时各自重查归一）。
    /// </summary>
    public void QueryAsync(bool resetPage = true)
    {
        if (resetPage)
            Page = 1;
        var filter = BuildFilter();
        // 时间口径守门：起始晚于结束=条件写反（硬查只会得到空表，现场会误以为
        // "没数据"）。From/To 是固定格式的完整时间串，字典序=时间序。
        if (filter.From != null && filter.To != null &&
            string.CompareOrdinal(filter.From, filter.To) > 0)
        {
            StatusText = "起始时间晚于结束时间，请检查筛选条件";
            return;
        }
        bool detailView = ShowDetail;   // 查询发起时的页签（回填跟随同一次决定）
        int page = Page;
        int skip = (page - 1) * PageSize;
        Task.Run(() =>
        {
            var stats = _store.Stats(filter);
            int total = detailView ? _store.Count(filter) : _store.CountSummary(filter);
            var rows = detailView ? _store.Query(filter, skip, PageSize) : null;
            var summary = detailView ? null : _store.QuerySummary(filter, skip, PageSize);
            return (stats, rows, summary, total);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                UiInvoke(() =>
                    StatusText = "查询失败：" + (t.Exception?.GetBaseException().Message ?? "未知错误"));
                return;
            }
            var (stats, rows, summary, total) = t.Result;
            UiInvoke(() =>
            {
                int pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (page > pageCount)
                {
                    // 页码越界（筛选收窄后原页超界）：本次取回的是越界空页，
                    // 直接填会让"第 3/1 页挂着空表格"——收敛页码并按新页重查
                    // （只发生在越界这一次，收敛后的页码必 ≤ 总页数）。
                    PageCount = pageCount;
                    Page = pageCount;
                    QueryAsync(resetPage: false);
                    return;
                }
                Rows.Clear();
                if (rows != null)
                    foreach (var r in rows)
                        Rows.Add(r);
                SummaryRows.Clear();
                if (summary != null)
                    foreach (var r in summary)
                        SummaryRows.Add(r);
                TotalText = stats.Total.ToString("N0");
                FinishedText = stats.Finished.ToString("N0");
                AbnormalText = stats.Abnormal.ToString("N0");
                ActiveText = stats.Active.ToString("N0");
                SlotCountText = stats.SlotCount.ToString("N0");
                PageCount = pageCount;
                PageText = $"第 {Page}/{PageCount} 页 · 共 {total} 条";
                StatusText = detailView
                    ? $"共 {total} 条记录 · 本页 {rows?.Count ?? 0} 条"
                    : $"共 {total} 个库位 · 本页 {summary?.Count ?? 0} 行";
            });
        });
    }

    /// <summary>重置筛选条件并重查（日期清空、时刻回缺省、库位回"全部"）。</summary>
    public void ResetFilters()
    {
        FromDate = null;
        ToDate = null;
        FromTime = StartDefaultTime;
        ToTime = EndDefaultTime;
        LocationText = LocationAll;
        SnText = "";
        ReasonText = "全部";
        QueryAsync(resetPage: true);
    }

    /// <summary>VM 属性 → Core 筛选对象（日期+时刻合成完整时间点；日期空=不限）。</summary>
    private SlotTraceFilter BuildFilter()
    {
        return new SlotTraceFilter
        {
            From = CombineTime(FromDate, FromTime, StartDefaultTime),
            To = CombineTime(ToDate, ToTime, EndDefaultTime),
            LocationCode = string.IsNullOrWhiteSpace(LocationText) || LocationText == LocationAll
                ? null : LocationText.Trim(),
            ProductSn = string.IsNullOrWhiteSpace(SnText) ? null : SnText.Trim(),
            EndReason = ReasonText == "全部"
                ? SlotTraceEndReason.None
                : SlotTraceEndReasonText.FromText(ReasonText),
        };
    }

    /// <summary>日期+时刻 → 库里的时间字符串（yyyy-MM-dd HH:mm:ss）；日期空=不限。</summary>
    private static string? CombineTime(DateTime? date, string? timeText, string fallbackTime)
    {
        if (date == null)
            return null;
        return new DateTime(date.Value.Year, date.Value.Month, date.Value.Day)
            .AddMinutes(ParseMinutes(timeText, fallbackTime))
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>"HH:mm" → 当日分钟数（解析失败回缺省——下拉只产合法文本，
    /// 兜底只为坏数据不崩；缺省时刻是常量恒可解析）。</summary>
    private static int ParseMinutes(string? text, string fallbackTime)
    {
        string t = (text ?? "").Trim();
        if (TimeSpan.TryParseExact(t, @"hh\:mm", CultureInfo.InvariantCulture, out var span)
            && span >= TimeSpan.Zero && span < TimeSpan.FromDays(1))
            return (int)span.TotalMinutes;
        TimeSpan.TryParseExact(fallbackTime, @"hh\:mm", CultureInfo.InvariantCulture, out var fb);
        return (int)fb.TotalMinutes;
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
