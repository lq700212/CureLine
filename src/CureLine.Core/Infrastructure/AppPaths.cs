// =========================================================================
// AppPaths：全部落盘路径集中地（对应老项目散落的
//   My.Application.Info.DirectoryPath & "\config\config.ini" 等写法）
//
// 为什么集中：老项目路径拼在各窗体里，改目录要翻 5 个文件；新项目只改这里。
// 基址 = 程序所在目录（复刻老项目语义：exe 同目录 config/log/DataBase）。
// =========================================================================

namespace CureLine.Core.Infrastructure;

public static class AppPaths
{
    /// <summary>程序所在目录（老项目 My.Application.Info.DirectoryPath 对应）。</summary>
    public static string BaseDir { get; set; } =
        AppDomain.CurrentDomain.BaseDirectory;

    public static string ConfigDir => Path.Combine(BaseDir, "config");
    public static string ConfigIni => Path.Combine(ConfigDir, "config.ini");
    public static string MesParamsJson => Path.Combine(ConfigDir, "mes_params.json");
    public static string DatabaseFile => Path.Combine(BaseDir, "DataBase", "PLCdatabase.db3");
    public static string LogDir => Path.Combine(BaseDir, "log");
    public static string MesLogDir => Path.Combine(BaseDir, "Logs", "MESLOG");

    /// <summary>当日操作日志目录 log\yyyy-MM-dd（老项目 MarkLog 语义）。</summary>
    public static string TodayLogDir(string? date = null) =>
        Path.Combine(LogDir, date ?? DateTime.Now.ToString("yyyy-MM-dd"));
}
