// =========================================================================
// ThemeDictionaryTests：双主题字典键对齐（纯文本级，不加载 WPF 资源）
//
// 为什么测文件文本而不是真加载 ResourceDictionary：xUnit 测试态没有
// Application 宿主，WPF 资源加载要 STA 线程的基建；而"键集合一致的静态
// 不变量"文本级就能锁死（x:Key 正则抽取）——零 WPF 依赖、不脆弱。
//
// 锁什么事：ThemeManager 切字典换皮靠"同名异值按键取色"——Light/Dark 两
// 字典只要有一个键在另一边缺失，切过去对应元素就吃空值（视觉空白回退），
// 这类回归现场肉眼难查（新主题下"某处突然没了颜色"）。静态文本断言把
// 漂移挡在最便宜的位置。
// =========================================================================

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CureLine.Tests;

public class ThemeDictionaryTests
{
    // ---------- 工具：从测试 bin 上溯仓库根，定位主题字典文件 ----------

    private static string ThemeFilePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CureStation")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "CureStation", "Themes", fileName);
    }

    private static HashSet<string> KeysOf(string path)
    {
        var keys = new HashSet<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)"""))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    // ---------- 断言 ----------

    /// <summary>Light/Dark 两字典 x:Key 集合必须全等（同名异值是换皮机制的根）。</summary>
    [Fact]
    public void LightDark_KeySets_AreIdentical()
    {
        var light = KeysOf(ThemeFilePath("Light.xaml"));
        var dark = KeysOf(ThemeFilePath("Dark.xaml"));
        Assert.NotEmpty(light);
        Assert.Equal(light, dark);
    }

    /// <summary>
    /// 弹窗键板 Fresh.* 全集在两字典都齐——App.xaml 样式 Setter 与八窗 XAML 的
    /// DynamicResource 引用全集（对应"同键双值、缺一键即对应主题下吃空值"）。
    /// 沉淀成清单：后续新增键，两字典 + 本清单三处同补；删键先删引用再删这里。
    /// </summary>
    [Fact]
    public void FreshKeys_PresentInBothThemes()
    {
        var light = KeysOf(ThemeFilePath("Light.xaml"));
        var dark = KeysOf(ThemeFilePath("Dark.xaml"));
        var required = new[]
        {
            // 基础件（卡片/提示/边/字/按钮四态/主蓝/危险/成功/输入框）
            "Brush.Fresh.CardBg","Brush.Fresh.HintBg","Brush.Fresh.Border","Brush.Fresh.FgStrong",
            "Brush.Fresh.FgMid","Brush.Fresh.FgDim","Brush.Fresh.ButtonBg","Brush.Fresh.ButtonHoverBg",
            "Brush.Fresh.ButtonHoverBorder","Brush.Fresh.ButtonPressedBg","Brush.Fresh.Accent",
            "Brush.Fresh.AccentBorder","Brush.Fresh.AccentHover","Brush.Fresh.AccentPressed",
            "Brush.Fresh.OnAccent","Brush.Fresh.Danger","Brush.Fresh.DangerBorder",
            "Brush.Fresh.DangerHover","Brush.Fresh.DangerPressed","Brush.Fresh.Success",
            "Brush.Fresh.SuccessBorder","Brush.Fresh.SuccessHover","Brush.Fresh.SuccessPressed",
            "Brush.Fresh.InputBg",
            // 窗体骨架（窗底/关闭钮悬停红/分区底边/卡内子区/只读框）
            "Brush.Fresh.WinBg","Brush.Fresh.CloseHoverBg","Brush.Fresh.PanelBorder",
            "Brush.Fresh.SubCardBg","Brush.Fresh.LockedBg",
            // 提示条三族（警示黄/错误红/成功绿：底-边-字）
            "Brush.Fresh.WarnBg","Brush.Fresh.WarnBorder","Brush.Fresh.WarnFg",
            "Brush.Fresh.AlertErrBg","Brush.Fresh.AlertErrBorder","Brush.Fresh.AlertErrFg",
            "Brush.Fresh.AlertOkBg","Brush.Fresh.AlertOkBorder","Brush.Fresh.AlertOkFg",
            // 策略可视化画布（底 + 分档四色 + 块上字两阶）
            "Brush.Fresh.CanvasBg","Brush.Fresh.TierGreen","Brush.Fresh.TierYellow",
            "Brush.Fresh.TierOrange","Brush.Fresh.TierRed","Brush.Fresh.TierFg","Brush.Fresh.TierFgSub",
            // IO 测试灯珠（ON=Success/边=SuccessBorder 复用；OFF/未读到/边框三键）
            "Brush.Fresh.DotOff","Brush.Fresh.DotNoRead","Brush.Fresh.DotBorder",
            // DataGrid（列头底/选中底/网线；字色走 FgStrong/FgMid 复用）
            "Brush.Fresh.GridHeaderBg","Brush.Fresh.GridSelBg","Brush.Fresh.GridLine",
            // 滚动条胶囊
            "Brush.Fresh.ScrollThumb","Brush.Fresh.ScrollThumbHover",
        };
        foreach (var key in required)
        {
            Assert.True(light.Contains(key), $"Light.xaml 缺弹窗键 {key}");
            Assert.True(dark.Contains(key), $"Dark.xaml 缺弹窗键 {key}");
        }
    }
}
