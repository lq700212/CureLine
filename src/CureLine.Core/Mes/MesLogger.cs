// =========================================================================
// MesLogger：MES 日志（复刻自 MES_Core.vb，单机版 + 多机版融合）
//
// 干什么：三路同记——① 本地文件 Logs\MESLOG\MES_yyyy-MM-dd.txt（单机）或
//   yyyy-MM-dd\机台名.txt（多机）；② 内存 FIFO 缓存；③ 事件通知 UI 实时刷新。
// 为什么融合：单机版是全量 300 条一个缓存；多机版是按机台分 8 个缓存各 50 条。
//   融合版两者都支持：stationId = -1 走单机全量；>=0 走分机台。
// 线程：锁内只做"文件+缓存"，事件在锁外触发（防跨线程死锁，老项目原注）。
//
// 多机版另有 sShowLog（直写对应机台 ListBox），那是 WinForms 控件耦合，
// 不进本类，由 WPF 层订阅事件后自行分发（见 MainWindow）。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Core.Mes;

/// <summary>
/// 结构化日志条目（V0.0.51 主窗业务化新增，观测用）：日志窗表格化三列
/// （时间/事件级别/详情）的数据源。Formatted 与落盘格式逐字一致——
/// 历史缓存（字符串）经 TryParseEntry 还原成条目，实时流走 EntryAdded 事件。
/// </summary>
public sealed record MesLogEntry(DateTime Time, string Title, string Message, int StationId)
{
    /// <summary>落盘/缓存行的原文格式（与 WriteLog 写文件逐字一致，勿改）。</summary>
    public string Formatted => $"[{Time:yyyy-MM-dd HH:mm:ss.fff}] [{Title}] {Message}";
}

public static class MesLogger
{
    private static readonly object LockObj = new();
    private const int MaxSingleLogCount = 300;
    private const int MaxStationLogCount = 50;

    /// <summary>单机全量缓存（stationId=-1 时用）。</summary>
    private static readonly List<string> SingleCache = new();

    /// <summary>分机台缓存（多机模式，key=机台号 0..7）。</summary>
    private static readonly Dictionary<int, List<string>> StationCaches = new();

    /// <summary>
    /// 全局日志事件。stationId=-1 单机；>=0 多机某机台。
    /// 注意：在工作线程触发，UI 订阅方必须切回 UI 线程再操作控件。
    /// </summary>
    public static event Action<string, int>? LogAdded;

    /// <summary>
    /// 结构化日志事件（与 LogAdded 同点成对触发，同一行两路通知）：
    /// 日志窗表格化按条目渲染；工作线程触发，UI 订阅方必须封送。
    /// </summary>
    public static event Action<MesLogEntry>? EntryAdded;

    /// <summary>
    /// 8 台设备名（V0.0.32 起走配置：AppConfig.Load 从 ini [StationNames] 节读入，
    /// 缺键/空值回落 AppConfig.DefaultStationNames=老项目 Designer 原样）。
    /// 换产线/换客户在网关配置窗"机台连接"页直接改名保存即可，不再需要改代码。
    /// 消费方：机台面板名/主窗视图筛选下拉/GenLogName 日志文件名，三处同源。
    /// </summary>
    public static string[] StationNames => AppConfig.StationNames;

    /// <summary>机台日志名（复刻自多机版 MESLogger.GenLogName）。</summary>
    public static string GenLogName(int stationId) =>
        stationId >= 0 && stationId < StationNames.Length
            ? StationNames[stationId]
            : $"Station{stationId}";

    /// <summary>单机版入口（等价老项目 WriteLog(title, content)）。</summary>
    public static void WriteLog(string title, string content) =>
        WriteLog(title, content, -1);

    public static void WriteLog(string title, string content, int stationId)
    {
        DateTime now = DateTime.Now;
        string logTime = now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string formatted = $"[{logTime}] [{title}] {content}";

        lock (LockObj)
        {
            try
            {
                string logFile;
                if (stationId < 0)
                {
                    Directory.CreateDirectory(AppPaths.MesLogDir);
                    logFile = Path.Combine(AppPaths.MesLogDir, $"MES_{DateTime.Now:yyyy-MM-dd}.txt");
                }
                else
                {
                    string dir = Path.Combine(AppPaths.MesLogDir, DateTime.Now.ToString("yyyy-MM-dd"));
                    Directory.CreateDirectory(dir);
                    logFile = Path.Combine(dir, $"{GenLogName(stationId)}.txt");
                }
                File.AppendAllText(logFile,
                    formatted + "\r\n" + new string('-', 50) + "\r\n",
                    System.Text.Encoding.UTF8);

                if (stationId < 0)
                {
                    SingleCache.Add(formatted);
                    if (SingleCache.Count > MaxSingleLogCount)
                        SingleCache.RemoveAt(0);
                }
                else
                {
                    if (!StationCaches.TryGetValue(stationId, out var list))
                        StationCaches[stationId] = list = new List<string>();
                    list.Add(formatted);
                    if (list.Count > MaxStationLogCount)
                        list.RemoveAt(0);
                }
            }
            catch
            {
                // 忽略文件被占用等写入异常，避免软件崩溃（老项目原注）。
            }
        }

        LogAdded?.Invoke(formatted, stationId);
        EntryAdded?.Invoke(new MesLogEntry(now, title, content, stationId));
    }

    /// <summary>
    /// 缓存行原文 → 结构化条目（日志窗打开时批量还原历史）。
    /// 格式固定为 "[yyyy-MM-dd HH:mm:ss.fff] [title] message"；解析失败
    /// （手改文件/旧格式）返回 null，调用方按纯文本行兜底显示。
    /// </summary>
    public static MesLogEntry? TryParseEntry(string formatted, int stationId = -1)
    {
        if (string.IsNullOrEmpty(formatted) || formatted[0] != '[')
            return null;
        int closeTime = formatted.IndexOf(']', 1);
        if (closeTime < 0)
            return null;
        if (!DateTime.TryParseExact(formatted.Substring(1, closeTime - 1),
                "yyyy-MM-dd HH:mm:ss.fff", null,
                System.Globalization.DateTimeStyles.None, out DateTime time))
            return null;
        // 第二段 "[title]"（title 本体按约定不含 ']'——WriteLog 侧保证）
        int openTitle = formatted.IndexOf('[', closeTime);
        int closeTitle = formatted.IndexOf(']', openTitle + 1);
        if (openTitle < 0 || closeTitle < 0)
            return null;
        string title = formatted.Substring(openTitle + 1, closeTitle - openTitle - 1);
        string message = closeTitle + 1 < formatted.Length
            ? formatted.Substring(closeTitle + 1).TrimStart()
            : string.Empty;
        return new MesLogEntry(time, title, message, stationId);
    }

    /// <summary>取单机历史缓存副本（日志窗打开时一次性加载）。</summary>
    public static List<string> GetCachedLogs()
    {
        lock (LockObj)
            return new List<string>(SingleCache);
    }

    /// <summary>
    /// 取全量历史（单机缓存 + 全部机台缓存按行首时间戳排序合并）——
    /// 全量日志窗（stationId=-1）用：机台日志落分机台缓存，"-1 全部机台"
    /// 语义要看到全部。行格式 "[yyyy-MM-dd HH:mm:ss.fff] ..." 固定，字符串序
    /// 即时间序；解析不出时间戳的残行排最后保原序。
    /// </summary>
    public static List<string> GetAllCachedLogs()
    {
        lock (LockObj)
        {
            var all = new List<string>(SingleCache);
            foreach (var kv in StationCaches)
                all.AddRange(kv.Value);
            all.Sort((a, b) =>
            {
                string ka = a.Length > 24 && a[0] == '[' ? a.Substring(1, 23) : "9999-12-31 23:59:59.999";
                string kb = b.Length > 24 && b[0] == '[' ? b.Substring(1, 23) : "9999-12-31 23:59:59.999";
                return string.CompareOrdinal(ka, kb);
            });
            return all;
        }
    }

    /// <summary>取某机台历史缓存副本（多机日志窗用）。</summary>
    public static List<string> GetCachedLogs(int stationId)
    {
        lock (LockObj)
            return StationCaches.TryGetValue(stationId, out var list)
                ? new List<string>(list)
                : new List<string>();
    }

    /// <summary>
    /// 清空内存缓存（仅测试用：静态缓存跨用例常驻，不清会导致用例互相污染。
    /// 生产代码不要调——日志窗打开时要读历史）。
    /// </summary>
    public static void ClearForTests()
    {
        lock (LockObj)
        {
            SingleCache.Clear();
            StationCaches.Clear();
        }
    }
}
