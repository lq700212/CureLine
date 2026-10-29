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
// =========================================================================

using System.Text;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Services;

public static class LocalLogService
{
    private static readonly object LockObj = new();

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
        return $"{DateTime.Now:HH:mm:ss} {message}";
    }
}
