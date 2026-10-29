// =========================================================================
// MesLogger 测试：三路同记（文件/缓存/事件）+ 容量上限 + 分流语义
//
// 锁定的口径（与 MesLogWindow 分流逻辑配套，改一处另一处必跟）：
// ① stationId=-1 → 单机文件 MES_yyyy-MM-dd.txt + 单机缓存（上限 300）；
// ② >=0 → 分机台文件 yyyy-MM-dd\机台名.txt + 分机台缓存（上限 50）；
// ③ LogAdded 事件在锁外触发，带 stationId 原样透传；
// ④ GenLogName 越界回退 "Station{id}"（不抛）。
// 落盘全进 TestScope 临时目录，不污染仓库。
// =========================================================================

using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class MesLoggerTests
{
    [Fact(DisplayName = "单机日志落文件进缓存发事件")]
    public void Single_WritesFile_Caches_FiresEvent()
    {
        using var scope = new TestScope();
        string? evt = null;
        int evtStation = -99;
        void Handler(string m, int s) { evt = m; evtStation = s; }
        MesLogger.LogAdded += Handler;
        try
        {
            MesLogger.WriteLog("UT", "hello-single");
            Assert.NotNull(evt);
            Assert.Contains("hello-single", evt!);
            Assert.Equal(-1, evtStation);
            Assert.Contains(MesLogger.GetCachedLogs(), l => l.Contains("hello-single"));

            string file = Path.Combine(scope.TempDir, "Logs", "MESLOG",
                $"MES_{DateTime.Now:yyyy-MM-dd}.txt");
            Assert.True(File.Exists(file), "单机日志文件未生成");
            Assert.Contains("hello-single", File.ReadAllText(file));
        }
        finally
        {
            MesLogger.LogAdded -= Handler;
        }
    }

    [Fact(DisplayName = "分机台日志分文件分缓存")]
    public void Station_SplitsFile_AndCache()
    {
        using var scope = new TestScope();
        var got = new List<(string, int)>();
        void Handler(string m, int s) => got.Add((m, s));
        MesLogger.LogAdded += Handler;
        try
        {
            MesLogger.WriteLog("UT", "hello-s3", 3);
            Assert.Contains(got, x => x.Item2 == 3 && x.Item1.Contains("hello-s3"));
            Assert.Contains(MesLogger.GetCachedLogs(3), l => l.Contains("hello-s3"));
            Assert.DoesNotContain(MesLogger.GetCachedLogs(), l => l.Contains("hello-s3"));

            string file = Path.Combine(scope.TempDir, "Logs", "MESLOG",
                DateTime.Now.ToString("yyyy-MM-dd"),
                $"{MesLogger.GenLogName(3)}.txt");
            Assert.True(File.Exists(file), "机台日志文件未生成");
        }
        finally
        {
            MesLogger.LogAdded -= Handler;
        }
    }

    [Fact(DisplayName = "单机缓存只留300")]
    public void SingleCache_Caps300()
    {
        using var scope = new TestScope();
        for (int i = 0; i < 310; i++)
            MesLogger.WriteLog("UT", $"s{i:000}");
        var cached = MesLogger.GetCachedLogs();
        Assert.Equal(300, cached.Count);
        Assert.DoesNotContain(cached, l => l.Contains("s000"));
        Assert.Contains(cached, l => l.Contains("s309"));
    }

    [Fact(DisplayName = "机台缓存只留50")]
    public void StationCache_Caps50()
    {
        using var scope = new TestScope();
        for (int i = 0; i < 60; i++)
            MesLogger.WriteLog("UT", $"t{i:00}", 1);
        var cached = MesLogger.GetCachedLogs(1);
        Assert.Equal(50, cached.Count);
        Assert.Contains(cached, l => l.Contains("t59"));
        Assert.Empty(MesLogger.GetCachedLogs(2)); // 别台不受影响
    }

    [Fact(DisplayName = "GenLogName越界回退")]
    public void GenLogName_OutOfRange_FallsBack()
    {
        Assert.Equal("橡胶装配PCB设备", MesLogger.GenLogName(0));
        Assert.Equal(8, MesLogger.StationNames.Length);
        Assert.Equal("Station99", MesLogger.GenLogName(99));
        Assert.Equal("Station-1", MesLogger.GenLogName(-1));
    }
}
