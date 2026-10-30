// =========================================================================
// CimGatewayThemeTests：程序②双主题（CimGateway.Themes）
// —— ThemeManager.ParseTheme 纯函数 + Light/Dark 两字典键对齐锁。
//
// 与程序①（ThemeManagerTests/ThemeDictionaryTests）同构不同类：两程序的
// ThemeManager 同名（各自 Themes 命名空间），本文件用 using 别名区分；
// 字典键对齐锁走文本级断言（xUnit 无 Application 宿主，WPF 资源加载要 STA）。
// 锁什么事：ThemeManager 切字典换皮靠"同名异值按键取色"——任一键在另一边
// 缺失，切过去对应元素吃空值（视觉空白回退），现场肉眼难查。
// =========================================================================

using System.IO;
using System.Text.RegularExpressions;
using CureLine.Tests.Mocks;
using GatewayTheme = CimGateway.Themes.ThemeManager;
using Xunit;

namespace CureLine.Tests;

public class CimGatewayThemeTests
{
    // ---------- ParseTheme（纯函数，与程序①同口径） ----------

    [Theory(DisplayName = "网关ParseTheme：非法/缺省回 Light")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("black")]
    [InlineData("DARKNESS")]
    public void ParseTheme_UnknownOrEmpty_FallsBackToLight(string? raw)
    {
        Assert.Equal("Light", GatewayTheme.ParseTheme(raw));
    }

    [Theory(DisplayName = "网关ParseTheme：Dark 大小写空白均识别")]
    [InlineData("Dark")]
    [InlineData("dark")]
    [InlineData(" DARK ")]
    public void ParseTheme_DarkForms(string raw)
    {
        Assert.Equal("Dark", GatewayTheme.ParseTheme(raw));
    }

    [Fact(DisplayName = "网关ApplyTheme 无 Application 只记账 + ToggleTarget 翻转")]
    public void ApplyTheme_WithoutApp_BookkeepsAndToggles()
    {
        using var scope = new TestScope();
        string before = GatewayTheme.Current;
        try
        {
            // xUnit 元程序集 Application.Current 为 null——防御分支只换 Current
            GatewayTheme.ApplyTheme(before == "Dark" ? "Light" : "Dark");
            Assert.NotEqual(before, GatewayTheme.Current);
            // 切完后的 ToggleTarget = 再切一次回原主题（回程目标，与初始值相同）
            Assert.Equal(before, GatewayTheme.ToggleTarget);
        }
        finally
        {
            GatewayTheme.ApplyTheme(before); // 恢复全局静态，不污染其它用例
            Assert.Equal(before, GatewayTheme.Current);
        }
    }

    // ---------- 字典键对齐（文本级，不加载 WPF 资源） ----------

    /// <summary>从测试 bin 上溯仓库根，定位程序②主题字典文件。</summary>
    private static string ThemeFilePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CimGateway")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "CimGateway", "Themes", fileName);
    }

    private static HashSet<string> KeysOf(string path)
    {
        var keys = new HashSet<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)"""))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>Light/Dark 两字典 x:Key 集合必须全等（同名异值是换皮机制的根）。</summary>
    [Fact(DisplayName = "网关两主题字典键集合全等")]
    public void LightDark_KeySets_AreIdentical()
    {
        var light = KeysOf(ThemeFilePath("Light.xaml"));
        var dark = KeysOf(ThemeFilePath("Dark.xaml"));
        Assert.NotEmpty(light);
        Assert.Equal(light, dark);
    }

    /// <summary>
    /// 弹窗键板 Fresh.* 全集在两字典都齐——App.xaml 样式 Setter 与各窗 XAML 的
    /// DynamicResource 引用全集。后续新增键，两字典 + 本清单三处同补；
    /// 删键先删引用再删这里。
    /// </summary>
    [Fact(DisplayName = "网关弹窗键板 Fresh 全集两字典齐备")]
    public void FreshKeys_PresentInBothThemes()
    {
        var light = KeysOf(ThemeFilePath("Light.xaml"));
        var dark = KeysOf(ThemeFilePath("Dark.xaml"));
        var required = new[]
        {
            // 基础件（卡片/提示/边/字/按钮四态/主蓝/危险/输入框）
            "Brush.Fresh.CardBg","Brush.Fresh.HintBg","Brush.Fresh.Border","Brush.Fresh.FgStrong",
            "Brush.Fresh.FgMid","Brush.Fresh.FgDim","Brush.Fresh.ButtonBg","Brush.Fresh.ButtonHoverBg",
            "Brush.Fresh.ButtonHoverBorder","Brush.Fresh.ButtonPressedBg","Brush.Fresh.Accent",
            "Brush.Fresh.AccentBorder","Brush.Fresh.AccentHover","Brush.Fresh.AccentPressed",
            "Brush.Fresh.OnAccent","Brush.Fresh.Danger","Brush.Fresh.DangerBorder",
            "Brush.Fresh.DangerHover","Brush.Fresh.DangerPressed","Brush.Fresh.InputBg",
            // 窗体骨架（窗底/关闭钮悬停红/分区底边/子卡只读框）
            "Brush.Fresh.WinBg","Brush.Fresh.CloseHoverBg","Brush.Fresh.PanelBorder",
            "Brush.Fresh.SubCardBg",
            // 提示条两族（错误红/成功绿：底-边-字）
            "Brush.Fresh.AlertErrBg","Brush.Fresh.AlertErrBorder","Brush.Fresh.AlertErrFg",
            "Brush.Fresh.AlertOkBg","Brush.Fresh.AlertOkBorder","Brush.Fresh.AlertOkFg",
            // 滚动条胶囊
            "Brush.Fresh.ScrollThumb","Brush.Fresh.ScrollThumbHover",
        };
        foreach (var key in required)
        {
            Assert.True(light.Contains(key), $"Light.xaml 缺弹窗键 {key}");
            Assert.True(dark.Contains(key), $"Dark.xaml 缺弹窗键 {key}");
        }
    }

    /// <summary>
    /// 主窗/机台卡主键板全集在两字典都齐——品牌顶栏/文字三阶/主色/面板/机台卡/
    /// 状态点/顶栏按钮组（Ghost 九键）的 DynamicResource 引用全集。
    /// </summary>
    [Fact(DisplayName = "网关主窗键板全集两字典齐备")]
    public void MainKeys_PresentInBothThemes()
    {
        var light = KeysOf(ThemeFilePath("Light.xaml"));
        var dark = KeysOf(ThemeFilePath("Dark.xaml"));
        var required = new[]
        {
            // 窗底/品牌顶栏
            "Brush.WindowBg","Brush.HeaderGrad","Brush.TopbarBorder","Brush.TopbarFg",
            "Brush.HeaderSubFg","Brush.HeaderChipBg",
            // 文字三阶 + 主色 + 面板
            "Brush.FgStrong","Brush.FgMid","Brush.FgDim","Brush.FgOnAccent",
            "Brush.Accent","Brush.AccentDeep","Brush.AccentSoft",
            "Brush.PanelBg","Brush.PanelBorder",
            // 机台卡三键
            "Brush.CardBg","Brush.CardBorder","Brush.CardBorderHover",
            // 状态点（通讯灯/连接灯）
            "Brush.StateIdle","Brush.StateRed","Brush.StateYellow","Brush.StateGreen",
            // 顶栏按钮组（Ghost 基础六键 + 高亮三键）
            "Brush.GhostBg","Brush.GhostBorder","Brush.GhostFg","Brush.GhostHoverBg",
            "Brush.GhostBorderHover","Brush.GhostPressedBg",
            "Brush.PrimaryGhostBg","Brush.PrimaryGhostBorder","Brush.PrimaryGhostFg",
        };
        foreach (var key in required)
        {
            Assert.True(light.Contains(key), $"Light.xaml 缺主窗键 {key}");
            Assert.True(dark.Contains(key), $"Dark.xaml 缺主窗键 {key}");
        }
    }
}
