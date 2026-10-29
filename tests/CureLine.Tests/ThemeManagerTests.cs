// =========================================================================
// ThemeManagerTests：双主题切换中枢（ParseTheme 纯函数 + 状态记账）
// 口径：ini [setting] Theme 非法/缺省回 Light（现场改坏不至于黑屏）；
//   "Dark"（忽略大小写/首尾空白）→ Dark。ApplyTheme 在无 Application 的
//   测试态只记账不碰资源（见 ThemeManager 注释防御）。
// =========================================================================

using CureLine.Tests.Mocks;
using CureStation.Themes;
using Xunit;

namespace CureLine.Tests;

public class ThemeManagerTests
{
    [Theory(DisplayName = "ParseTheme：非法/缺省回 Light")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("black")]
    [InlineData("DARKNESS")]
    public void ParseTheme_UnknownOrEmpty_FallsBackToLight(string? raw)
    {
        Assert.Equal("Light", ThemeManager.ParseTheme(raw));
    }

    [Theory(DisplayName = "ParseTheme：Dark 大小写空白均识别")]
    [InlineData("Dark")]
    [InlineData("dark")]
    [InlineData(" DARK ")]
    public void ParseTheme_DarkForms(string raw)
    {
        Assert.Equal("Dark", ThemeManager.ParseTheme(raw));
    }

    [Fact(DisplayName = "ApplyTheme 无 Application 只记账 + ToggleTarget 翻转")]
    public void ApplyTheme_WithoutApp_BookkeepsAndToggles()
    {
        using var scope = new TestScope();
        string before = ThemeManager.Current;
        try
        {
            // xUnit 元程序集 Application.Current 为 null——防御分支只换 Current
            ThemeManager.ApplyTheme(before == "Dark" ? "Light" : "Dark");
            Assert.NotEqual(before, ThemeManager.Current);
            // 切完后的 ToggleTarget = 再切一次回原主题（回程目标，与初始值相同）
            Assert.Equal(before, ThemeManager.ToggleTarget);
        }
        finally
        {
            ThemeManager.ApplyTheme(before); // 恢复全局静态，不污染其它用例
            Assert.Equal(before, ThemeManager.Current);
        }
    }
}
