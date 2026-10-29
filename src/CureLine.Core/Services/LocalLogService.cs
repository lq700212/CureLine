// =========================================================================
// LocalLogService：本机操作日志（复刻自单机版 Form1.MarkLog / 多机版 MarkLog）
//
// 单机：log\yyyy-MM-dd\log.txt；多机：log\yyyy-MM-dd\log-{机台号}.txt。
// 文件截 50 字（老项目 Substring(0,50) 原样），界面显示全量。
// WPF 层统一落盘：Core 只发事件不碰文件（保持 Core 无 UI、无落盘策略）。
// 线程安全：锁串行化，后台线程可直接调。
//
// 位置说明：纯逻辑无 WPF 依赖，故放 Core（可测，见 LocalLogServiceTests），
// WPF 层只调 Write 搬运返回值上界面。
//
// Written 事件流（2026-10-29 日志/控制台悬浮窗，双主题 G2/G1 实施）：
//   老模式"调用 Write 拿返回行自己上屏"要求每个调用点手动转发——程序①写日志
//   的点散在主 VM/策略窗/点表窗/状态机 log 回调等多处，各点转发必漏。现在
//   Write() 落盘后统一触发 Written（含时间戳的完整行），订阅方（日志悬浮窗）
//   只挂一处即可看到全工程日志。向后兼容：返回值语义不变，无人订阅时零副作用。
//   ⚠ 线程红线：Write 常在后台线程/状态机锁内被调——事件处理器禁止同步回调
//   重逻辑或 Dispatcher.Invoke 阻塞等待；订阅侧只做"无锁入队 + 定时批量搬运"
//   （LogWindow 用 DispatcherTimer），与 _sync 锁内轻量投递红线同款纪律。
// =========================================================================

using System.Text;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Services;

public static class LocalLogService
{
    private static readonly object LockObj = new();

    /// <summary>
    /// 日志汇聚事件：Write 落盘成功后触发，参数=与返回值同款的完整显示行（HH:mm:ss + 全文）。
    /// 触发线程 = 调用方线程（可能后台线程），订阅方负责封送 UI；锁内调用圆圆安全
    /// （处理器只做轻量操作时）——见类头"Written 事件流"红线注释。
    /// </summary>
    public static event Action<string>? Written;

    /// <summary>
    /// 写一条操作日志。stationId=-1 单机（log.txt），>=0 多机（log-{id}.txt）。
    /// 返回界面显示行（HH:mm:ss + 全文），调用方直接塞 ListBox。
    /// </summary>
    public static string Write(string message, int stationId = -1)
    {
        string dir = AppPaths.TodayLogDir();
        string file = stationId < 0
            ? Path.Combine(dir, "log.txt")
            : Path.Combine(dir, $"log-{stationId}.txt");
        string line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (LockObj)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string cut = message.Length < 50 ? message : message.Substring(0, 50);
                File.AppendAllText(file,
                    $"{DateTime.Now:G} {cut}\r\n", Encoding.UTF8);
            }
            catch
            {
                // 落盘失败不影响业务（老项目无保护，新项目加固但同样静默）。
            }
        }
        // 事件在锁外触发（锁内只包文件 IO；订阅方千变万化，不持锁更稳）。
        try { Written?.Invoke(line); }
        catch { /* 日志旁听者异常不影响业务主链 */ }
        return line;
    }
}
