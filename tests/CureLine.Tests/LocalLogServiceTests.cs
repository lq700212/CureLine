// =========================================================================
// LocalLogService 测试：落盘截 50 字 + 界面返回全量 + 单机/分机台分文件
//
// 锁定的口径（复刻老项目 MarkLog，改即行为分叉）：
// ① 文件只存前 50 字（长报警串不撑爆 log.txt）；② 返回给界面的行是全量
//   （"HH:mm:ss + 全文"）；③ stationId=-1 → log.txt，>=0 → log-{id}.txt。
// 落盘全进 TestScope 临时目录。
// =========================================================================

using CureLine.Core.Services;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class LocalLogServiceTests
{
    [Fact(DisplayName = "文件截50字界面给全量")]
    public void Write_LongMsg_FileCut50_UiFull()
    {
        using var scope = new TestScope();
        string longMsg = new('X', 100);
        string line = LocalLogService.Write(longMsg, -1);
        Assert.Contains(longMsg, line); // 界面全量

        string file = Path.Combine(scope.TempDir, "log",
            DateTime.Now.ToString("yyyy-MM-dd"), "log.txt");
        string content = File.ReadAllText(file);
        Assert.Contains(new string('X', 50), content);
        Assert.DoesNotContain(new string('X', 51), content);
    }

    [Fact(DisplayName = "短消息原文落盘")]
    public void Write_ShortMsg_AsIs()
    {
        using var scope = new TestScope();
        LocalLogService.Write("短", -1);
        string file = Path.Combine(scope.TempDir, "log",
            DateTime.Now.ToString("yyyy-MM-dd"), "log.txt");
        Assert.Contains("短", File.ReadAllText(file));
    }

    [Fact(DisplayName = "分机台分文件")]
    public void Write_Station_SplitFiles()
    {
        using var scope = new TestScope();
        LocalLogService.Write("s2-msg", 2);
        string dir = Path.Combine(scope.TempDir, "log", DateTime.Now.ToString("yyyy-MM-dd"));
        Assert.True(File.Exists(Path.Combine(dir, "log-2.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "log.txt")));
    }

    [Fact(DisplayName = "返回行带时间前缀")]
    public void Write_Returns_TimePrefixed()
    {
        using var scope = new TestScope();
        string line = LocalLogService.Write("abc", -1);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} abc$", line);
    }

    [Fact(DisplayName = "Written 事件首发完整行")]
    public void Written_Invoked_WithTimePrefixedLine()
    {
        // 日志悬浮窗的单一数据源：Write 落盘后统一发自含时间戳的完整行
        // （与返回值同款）。无人订阅零副作用（返回值语义不变）。
        using var scope = new TestScope();
        string? received = null;
        void Handler(string line) => received = line;
        LocalLogService.Written += Handler;
        try
        {
            LocalLogService.Write("evt-msg", -1);
            Assert.NotNull(received);
            Assert.Matches(@"^\d{2}:\d{2}:\d{2} evt-msg$", received);
        }
        finally
        {
            LocalLogService.Written -= Handler;
        }
    }

    [Fact(DisplayName = "Written 退订后不再推送")]
    public void Written_Unsubscribed_NoPush()
    {
        using var scope = new TestScope();
        int count = 0;
        void Handler(string line) => count++;
        LocalLogService.Written += Handler;
        LocalLogService.Write("z1", -1);   // 订阅期间 → 推送 1 次
        LocalLogService.Written -= Handler;
        LocalLogService.Write("z2", -1);   // 退订后 → 静默
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "最近缓存按写入顺序返回（最新在尾）")]
    public void GetRecentLines_ReturnsWritten_NewestLast()
    {
        // 日志窗打开时预填历史的数据源：Write 同批维护进程内缓存。
        using var scope = new TestScope();
        LocalLogService.ClearRecentForTests();
        LocalLogService.Write("cache-a", -1);
        LocalLogService.Write("cache-b", -1);

        var lines = LocalLogService.GetRecentLines();
        int a = lines.FindIndex(l => l.EndsWith("cache-a"));
        int b = lines.FindIndex(l => l.EndsWith("cache-b"));
        Assert.True(a >= 0 && b >= 0);
        Assert.True(a < b);   // 后写的排在后面（缓存按写入顺序）
    }

    [Fact(DisplayName = "最近缓存封顶 300 条丢最旧")]
    public void GetRecentLines_Capped()
    {
        using var scope = new TestScope();
        LocalLogService.ClearRecentForTests();
        for (int i = 0; i < 310; i++)
            LocalLogService.Write("cap-" + i, -1);

        var lines = LocalLogService.GetRecentLines();
        Assert.Equal(300, lines.Count);
        Assert.DoesNotContain(lines, l => l.EndsWith("cap-0"));
        Assert.Contains(lines, l => l.EndsWith("cap-309"));
    }
}
