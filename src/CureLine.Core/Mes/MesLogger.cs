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

    /// <summary>8 台设备名（老项目 Designer 原样，GenLogName/分文件共用）。</summary>
    public static readonly string[] StationNames =
    {
        "橡胶装配PCB设备", "外壳穿线半自动机构", "焊锡_2D检测_涂胶半自动机构",
        "外壳组件保压机构", "外壳组件点胶_打标二维码设备", "点胶固化收料机",
        "测试线体-激光-基板焊接_打标_贴圆片体机", "测试线体-光效测试压力测试设备",
    };

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
        string logTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
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
    }

    /// <summary>取单机历史缓存副本（日志窗打开时一次性加载）。</summary>
    public static List<string> GetCachedLogs()
    {
        lock (LockObj)
            return new List<string>(SingleCache);
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
