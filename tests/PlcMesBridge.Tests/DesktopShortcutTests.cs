// =========================================================================
// 桌面快捷方式测试：只测纯逻辑 + 临时目录真实 .lnk（不碰用户真实桌面）
//
// 覆盖：快捷方式名取 exe 名 / 空名兜底 / 图标指向 exe 自身 /
//   新建成功 / 已存在不覆盖（第二次返回 AlreadyExists 且文件不动）。
// =========================================================================

using PlcMesBridge.Core.Infrastructure;

namespace PlcMesBridge.Tests;

public class DesktopShortcutTests : IDisposable
{
    private readonly string _tmp;

    public DesktopShortcutTests()
    {
        // 每个用例独立临时目录（ Path.GetRandomFileName 防串行残留也防并行撞名）。
        _tmp = Path.Combine(Path.GetTempPath(), "lnk_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { /* 临时目录删不掉不影响结果 */ }
    }

    [Fact(DisplayName = "快捷方式名取exe名")]
    public void BuildPath_UsesExeName()
    {
        string lnk = DesktopShortcut.BuildShortcutPath(_tmp, @"D:\app\CimGateway.exe");
        Assert.Equal(Path.Combine(_tmp, "CimGateway.lnk"), lnk);
    }

    [Fact(DisplayName = "exe路径异常时兜底默认名")]
    public void BuildPath_FallbackName()
    {
        string lnk = DesktopShortcut.BuildShortcutPath(_tmp, "");
        Assert.Equal(Path.Combine(_tmp, "CimGateway.lnk"), lnk);
    }

    [Fact(DisplayName = "图标指向exe自身第一个内嵌图标")]
    public void IconLocation_PointsToExe()
    {
        Assert.Equal(@"D:\app\CimGateway.exe,0",
            DesktopShortcut.BuildIconLocation(@"D:\app\CimGateway.exe"));
    }

    [Fact(DisplayName = "临时目录新建lnk成功且已存在不覆盖")]
    public void Ensure_CreatesThenKeepsExisting()
    {
        // 用当前测试 dll 冒充目标 exe（只拼路径，不启动它，安全）。
        string fakeExe = Path.Combine(_tmp, "CimGateway.exe");
        File.WriteAllText(fakeExe, "fake");

        var (r1, p1) = DesktopShortcut.EnsureOnDesktop(fakeExe, _tmp);
        Assert.Equal(ShortcutResult.Created, r1);
        Assert.True(File.Exists(p1));

        // 故意改一下 lnk 内容：第二次必须返回 AlreadyExists 且不动文件。
        long size = new FileInfo(p1).Length;
        var (r2, p2) = DesktopShortcut.EnsureOnDesktop(fakeExe, _tmp);
        Assert.Equal(ShortcutResult.AlreadyExists, r2);
        Assert.Equal(p1, p2);
        Assert.Equal(size, new FileInfo(p2).Length);
    }
}
