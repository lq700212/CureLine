// =========================================================================
// KaleidoscopeUiContractTests：kaleidoscope.UI 库与宿主键板的键名契约
// （纯文本级，不加载 WPF 资源——与 ThemeDictionaryTests 同款手法）
//
// 锁什么事：库控件/样式全 DynamicResource 引语义键（Brush.* / Brush.Fresh.*），
// DynamicResource 缺键 = 静默空白（不报错，现场"某处突然没了颜色"肉眼难查）。
// 库样式沉到子模块后，键名漂移无人察觉是最大风险——本组测试把契约锁死：
//   ① 库字典引用的语义键 ⊆ 两程序键板键集（宿主必须供得起库引用的每个键）；
//   ② 库 keyed 样式键集合锁清单（宿主 App.xaml 合并后 StaticResource 可解析，
//     删键/改名必须本清单同批改——改动=影响所有宿主）。
// =========================================================================

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CureLine.Tests;

public class KaleidoscopeUiContractTests
{
    // ---------- 工具：从测试 bin 上溯仓库根 ----------

    /// <summary>仓库根定位（测试输出在 tests/CureLine.Tests/bin/...，逐级上溯）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CureStation")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static HashSet<string> KeysOf(string path)
    {
        var keys = new HashSet<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"x:Key=""([^""]+)"""))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    /// <summary>抓字典文本里所有 DynamicResource 引用的键（{DynamicResource 键名}）。</summary>
    private static HashSet<string> DynRefsOf(string path)
    {
        var refs = new HashSet<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(path), @"\{DynamicResource\s+([^}]+?)\s*\}"))
            refs.Add(m.Groups[1].Value);
        return refs;
    }

    // ---------- ① 库引用的语义键 ⊆ 宿主键板 ----------

    /// <summary>
    /// 库字典（ChromeStyles + FreshStyles）引用的每个语义键，两程序键板
    /// （Light+Dark 各 143/70 键）都必须有同名键——缺键即该程序对应元素吃空值。
    /// 库缺省键板 Default 键本身不算数（CureLine 不合并缺省键板，走宿主键板）。
    /// </summary>
    [Fact]
    public void LibraryStyleRefs_AreCoveredByBothHostPalettes()
    {
        string root = RepoRoot();
        string libDir = Path.Combine(root, "extern", "kaleidoscope", "Kaleidoscope.UI", "Themes");
        Assert.True(Directory.Exists(libDir), "kaleidoscope.UI Themes 目录不存在——子模块未拉取？");

        var libRefs = new HashSet<string>();
        foreach (var f in new[] { "ChromeStyles.xaml", "FreshStyles.xaml", "ShellStyles.xaml" })
            libRefs.UnionWith(DynRefsOf(Path.Combine(libDir, f)));
        // 库样式内部引用的库自身资源键（Fx.WinShadow/Fx.ShellMenuShadow 影效键、
        // WinTitleBar 族/样式 BasedOn）不是语义键——排除后剩下的全是宿主必须供值的
        // 语义键（含 Fn.ShellRadius 等 CornerRadius 键——宿主键板必须提供）
        libRefs.RemoveWhere(k => k.StartsWith("Fx.") || k.StartsWith("Win"));

        var csLight = KeysOf(ThemeFilePathOf("CureStation", "Light.xaml"));
        var csDark = KeysOf(ThemeFilePathOf("CureStation", "Dark.xaml"));
        var cgLight = KeysOf(ThemeFilePathOf("CimGateway", "Light.xaml"));
        var cgDark = KeysOf(ThemeFilePathOf("CimGateway", "Dark.xaml"));

        foreach (var key in libRefs)
        {
            Assert.True(csLight.Contains(key) && csDark.Contains(key),
                $"程序①键板缺库引用键 {key}");
            Assert.True(cgLight.Contains(key) && cgDark.Contains(key),
                $"程序②键板缺库引用键 {key}");
        }
        Assert.True(libRefs.Count >= 30, "库引用键数量异常（正则失配？）——先修 KeysOf/DynRefsOf 再说");
    }

    /// <summary>库缺省键板 Light/Dark 键集合全等（与宿主 ThemeDictionaryTests 同构的自洽锁）。</summary>
    [Fact]
    public void LibraryDefaultPalettes_LightDark_KeySetsIdentical()
    {
        string libDir = Path.Combine(RepoRoot(), "extern", "kaleidoscope", "Kaleidoscope.UI", "Themes");
        var light = KeysOf(Path.Combine(libDir, "DefaultLight.xaml"));
        var dark = KeysOf(Path.Combine(libDir, "DefaultDark.xaml"));
        Assert.NotEmpty(light);
        Assert.Equal(light, dark);
    }

    // ---------- ② 库 keyed 样式键清单锁 ----------

    /// <summary>
    /// 库 keyed 样式键清单（宿主 App.xaml 合并后 StaticResource 直接引用）。
    /// 改键名/删键 = 所有宿主编译期不报错、运行期资源悬空——必须本清单同批改。
    /// </summary>
    [Fact]
    public void LibraryStyleKeys_AreLocked()
    {
        string libDir = Path.Combine(RepoRoot(), "extern", "kaleidoscope", "Kaleidoscope.UI", "Themes");
        var chrome = KeysOf(Path.Combine(libDir, "ChromeStyles.xaml"));
        var fresh = KeysOf(Path.Combine(libDir, "FreshStyles.xaml"));
        var shell = KeysOf(Path.Combine(libDir, "ShellStyles.xaml"));

        var expectedChrome = new[]
        {
            "Fx.WinShadow", "WinTitleBar", "WinTitleText", "WinTitleClose", "WinSysBtn", "WinSysClose",
        };
        var expectedFresh = new[]
        {
            "GhostButton", "PrimaryGhostButton", "FreshButton", "FreshPrimaryButton",
            "FreshDangerButton", "FreshSuccessButton", "FreshInput", "FreshPwdInput",
            "FreshCombo", "FreshComboItem",
        };
        var expectedShell = new[]
        {
            "CapsuleRadiusConverter",
            "KxPrimaryButton", "KxOkButton", "KxSecondaryButton", "KxDangerButton",
            "KxWarnButton", "KxSuccessButton",
            "StatusChip", "StatusChipText",
            "KxTitleCloseButton", "KxTitleMinButton", "KxTitleMaxButton",
            "KxTextBox", "KxPasswordBox", "KxDatePicker", "KxComboBox", "KxSwitch",
            "BrandTabControl", "BrandTabItem",
            "BrandGridHeader", "BrandGridCell", "BrandGridRow",
            "WinContextMenu", "WinMenuItem", "WinMenuItemSub", "WinMenuSeparator",
            "KxCheckBox", "KxRadioButton", "KxGridCheckBox", "KxGroupBoxImplicit",
            "ChromeDialogRootBorder", "ChromeDialogHeadBorder",
            "FormLabel118", "FormLabel86", "RequiredMark", "HintText", "HintWarnText", "SectionTitle",
            "KpiCard", "KpiLabel", "KpiValue", "KpiSub", "StatusDotPulse",
            "KxDialogButtonImplicit", "KxDialogComboImplicit", "KxDialogTextBoxImplicit",
            "KxDialogPasswordImplicit", "KxDialogGridImplicit", "KxDialogListImplicit",
        };
        foreach (var k in expectedChrome)
            Assert.True(chrome.Contains(k), $"ChromeStyles 缺键 {k}——改名/删键须同步本测试与全宿主");
        foreach (var k in expectedFresh)
            Assert.True(fresh.Contains(k), $"FreshStyles 缺键 {k}——改名/删键须同步本测试与全宿主");
        foreach (var k in expectedShell)
            Assert.True(shell.Contains(k), $"ShellStyles 缺键 {k}——改名/删键须同步本测试与全宿主");
        Assert.Equal(expectedChrome.Length, chrome.Count);
        Assert.Equal(expectedFresh.Length, fresh.Count);
        Assert.Equal(expectedShell.Length, shell.Count);
    }

    private static string ThemeFilePathOf(string program, string fileName) =>
        Path.Combine(RepoRoot(), "src", program, "Themes", fileName);
}
