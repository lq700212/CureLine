// =========================================================================
// TestScope：静态全局快照/恢复 + 临时目录（一处改，全用例隔离生效）
//
// 为什么需要：Core 大量静态全局（MesConfig 6 URL+地址束、MesParamStore 16 字段、
// LanguageService、AppConfig.PlcUse/PlcIps、AppPaths.BaseDir），
// 用例改了不恢复就会污染下一个（全工程已串行，见 AssemblyInfo，
// 但执行顺序仍不保证，顺序一变就飘红——必须每个用例自清）。
//
// 用法：
//   using var scope = new TestScope();   // 构造时快照 + BaseDir 指临时目录
//   MesConfig.UrlApi0030 = mes.Url;      // 随便改
//   // Dispose 时自动恢复 + 删临时目录（MesLogger/LocalLogService 落盘全进临时目录，
//   // 不污染仓库；MesLogger 静态缓存也一并清空）。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;

namespace PlcMesBridge.Tests.Mocks;

public sealed class TestScope : IDisposable
{
    private readonly string[] _urls = new string[6];
    private readonly string[] _triggers = new string[6];
    private readonly string[,] _addrSets = new string[6, 5];
    private readonly int _dataLength;
    private readonly Dictionary<string, string> _params = new();
    private readonly string _lang;
    private readonly string _baseDir;
    private readonly string[] _plcUse = new string[8];
    private readonly string?[] _plcIps = new string?[8];
    private readonly int[] _stationPorts = new int[8];
    private bool _disposed;

    /// <summary>本用例专属临时目录（ini/db/日志全放这里）。</summary>
    public string TempDir { get; }

    public TestScope()
    {
        // ---- 快照 ----
        _urls[0] = MesConfig.UrlApi0027; _urls[1] = MesConfig.UrlApi0028;
        _urls[2] = MesConfig.UrlApi0030; _urls[3] = MesConfig.UrlApi0031;
        _urls[4] = MesConfig.UrlApi0032; _urls[5] = MesConfig.UrlApi0033;
        _triggers[0] = MesConfig.TriggerApi0027; _triggers[1] = MesConfig.TriggerApi0028;
        _triggers[2] = MesConfig.TriggerApi0030; _triggers[3] = MesConfig.TriggerApi0031;
        _triggers[4] = MesConfig.TriggerApi0032; _triggers[5] = MesConfig.TriggerApi0033;
        for (int i = 0; i < 6; i++)
        {
            var s = MesConfig.ApiAddresses[i];
            _addrSets[i, 0] = s.PlcData; _addrSets[i, 1] = s.MesMsg;
            _addrSets[i, 2] = s.MesData; _addrSets[i, 3] = s.MesStatus;
            _addrSets[i, 4] = s.MesComplete;
        }
        _dataLength = MesConfig.DataLength;
        foreach (var (field, _) in MesParamStore.AllFields)
            _params[field] = MesParamStore.Get(field);
        _lang = LanguageService.CurrentLanguage;
        _baseDir = AppPaths.BaseDir;
        Array.Copy(AppConfig.PlcUse, _plcUse, 8);
        Array.Copy(AppConfig.PlcIps, _plcIps, 8);
        Array.Copy(AppConfig.StationPorts, _stationPorts, 8);

        // ---- 隔离：落盘全部重定向到临时目录 ----
        TempDir = Path.Combine(Path.GetTempPath(),
            "PlcBridge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
        AppPaths.BaseDir = TempDir;
        MesLogger.ClearForTests();
    }

    /// <summary>临时 ini 全路径（文件名自取，缺省 c.ini）。</summary>
    public string IniPath(string name = "c.ini") => Path.Combine(TempDir, name);

    /// <summary>临时 db 全路径。</summary>
    public string DbPath(string name = "t.db3") => Path.Combine(TempDir, name);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // ---- 恢复（顺序无关，纯赋值无 IO，除了 BaseDir 切回）----
        MesConfig.UrlApi0027 = _urls[0]; MesConfig.UrlApi0028 = _urls[1];
        MesConfig.UrlApi0030 = _urls[2]; MesConfig.UrlApi0031 = _urls[3];
        MesConfig.UrlApi0032 = _urls[4]; MesConfig.UrlApi0033 = _urls[5];
        MesConfig.TriggerApi0027 = _triggers[0]; MesConfig.TriggerApi0028 = _triggers[1];
        MesConfig.TriggerApi0030 = _triggers[2]; MesConfig.TriggerApi0031 = _triggers[3];
        MesConfig.TriggerApi0032 = _triggers[4]; MesConfig.TriggerApi0033 = _triggers[5];
        for (int i = 0; i < 6; i++)
        {
            var s = MesConfig.ApiAddresses[i];
            s.PlcData = _addrSets[i, 0]; s.MesMsg = _addrSets[i, 1];
            s.MesData = _addrSets[i, 2]; s.MesStatus = _addrSets[i, 3];
            s.MesComplete = _addrSets[i, 4];
        }
        MesConfig.DataLength = _dataLength;
        foreach (var kv in _params)
            MesParamStore.Set(kv.Key, kv.Value);
        LanguageService.CurrentLanguage = _lang;
        AppConfig.PlcUse[0] = _plcUse[0]; AppConfig.PlcUse[1] = _plcUse[1];
        AppConfig.PlcUse[2] = _plcUse[2]; AppConfig.PlcUse[3] = _plcUse[3];
        AppConfig.PlcUse[4] = _plcUse[4]; AppConfig.PlcUse[5] = _plcUse[5];
        AppConfig.PlcUse[6] = _plcUse[6]; AppConfig.PlcUse[7] = _plcUse[7];
        for (int i = 0; i < 8; i++)
            AppConfig.PlcIps[i] = _plcIps[i]!;
        Array.Copy(_stationPorts, AppConfig.StationPorts, 8);
        AppPaths.BaseDir = _baseDir;
        MesLogger.ClearForTests();
        try { Directory.Delete(TempDir, true); } catch { }
    }
}
