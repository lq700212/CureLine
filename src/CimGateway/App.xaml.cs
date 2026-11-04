// =========================================================================
// App：程序入口装配（对应老项目 Form1_Load 的前半：读配置→建库→种子账号→
//   软件授权门禁→起主窗，并起每小时授权巡检）。
//
// 顺序：定基址 → 读 config.ini（语言/主题/机台连接/MES）→ 应用主题 →
//   建 SQLite 库表（含 users）→ 播种预置账号（空表才播，已改密码永不覆盖）→
//   清扫 ini 旧明文记忆键 → 授权门禁（未激活先弹激活窗）→ 手工建主窗。
// config 缺键自动回填缺省（IniFile 自愈），现场删配置不怕。
// 初始密码说明：只在此一处出现（播种用），上线前必须登录"设置→修改密码"改掉，
// 文档不记录明文；users 表存 PBKDF2 哈希，拷走 db 也反推不出。
//
// 授权说明：与 HJVision / 程序① 同一套 ECDsa 激活方案（设备码+挑战值+试用/
//   永久+防回拨），但产品 ID 是 CureLine（0x434C）——HJVision 的激活码在
//   这里会被明确拒绝（产品隔离在 LicenseGate 一处判定）。
//   演示模式（config.ini [setting] Simulate=1）免激活放行（与模拟机台同口径）。
//   注意：原 StartupUri="MainWindow.xaml" 已删——自动建窗会绕过门禁，
//   现在与程序① 同款手工建窗，主窗建好后再恢复常规关闭语义。
// =========================================================================

using System.IO;
using System.Windows;
using System.Windows.Threading;
using CimGateway.Themes;
using CimGateway.ViewModels;
using CureLine.Core.Data;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Core.Services;
using CureLine.ViewModels;

namespace CimGateway;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动期可能先弹独立的激活窗（还没有主窗）——显式关闭模式防止
        // "激活窗一关应用就自动退出"；主窗建好后再恢复 OnLastWindowClose。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppPaths.BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        // kernel32 写 INI 不会自动建目录（老项目靠现场预置 config/），此处确保。
        Directory.CreateDirectory(AppPaths.ConfigDir);
        AppConfig.Load();

        // 双主题（2026-10-30 UI 对齐程序①）：按 [setting] Theme（Dark/Light，缺省
        // Light、非法回 Light）换主题字典——缺省值同时借 IniFile 自愈语义落盘
        // （首次启动补键，铁律 8：config.sample.ini 同步了该键）。
        ThemeManager.ApplyTheme(IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Theme",
            ThemeManager.DefaultTheme));

        MesParamStoreLoad();
        new SqliteHelper(AppPaths.DatabaseFile).EnsureDatabase();
        // 预置账号播种（admin 普通管理，dev 最高权限；初始密码见上注释）。
        UserAccountStore.Default.EnsureSeeded("123456", "dev123");
        // 旧版本 ini 明文记忆键清扫（RememberLogin/SavedPwd 系，防明文影子）。
        RunModeSettings.PurgeLegacySecrets(AppPaths.ConfigIni);

        // ── 软件授权门禁（启动）：演示模式免激活；未授权先弹激活窗，仍未授权则退出 ──
        var license = LicenseGate.Default;
        license.Refresh();
        if (!license.IsProductionAllowed())
        {
            var licenseVm = new LicenseActivationViewModel(license, "CIM通信网关");
            new LicenseActivationWindow { DataContext = licenseVm }.ShowDialog();
            license.Refresh();
            if (!license.IsProductionAllowed())
            {
                Shutdown();
                return;
            }
        }
        LocalLogService.Write("软件授权：" + license.ShortStatusText());

        // 主窗（替代原 StartupUri）：构造函数内建 MainWindowViewModel 并设 DataContext。
        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        // 主窗已建好：恢复"最后一个窗口关闭即退出"的常规语义
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // 每小时巡检授权：试用到期/配置被改成无效码/指纹变化——正常生产中途
        // 失效必须立刻停程序（授权只许这一处判定，见 LicenseGate）。
        StartLicensePatrol(license);
    }

    /// <summary>
    /// 每小时重算授权状态；一旦失效（过期/指纹不符/码无效）提示并退出程序。
    /// 定时器声明周期随 Application（DispatcherTimer 由 Dispatcher 持有），
    /// 不需要字段引用；巡检只在 UI 线程跑（Refresh 的线程约定）。
    /// </summary>
    private static void StartLicensePatrol(LicenseGate license)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) =>
        {
            license.Refresh();
            if (license.IsProductionAllowed())
                return;
            timer.Stop();
            MessageBox.Show(
                "软件授权已失效，程序将退出。\n\n" + license.StateText()
                + (string.IsNullOrEmpty(license.LastReason) ? "" : "\n" + license.LastReason)
                + "\n\n请联系厂商重新激活。",
                "软件授权", MessageBoxButton.OK, MessageBoxImage.Warning);
            Current?.Shutdown();
        };
        timer.Start();
    }

    private static void MesParamStoreLoad()
    {
        // AppConfig.Load 内已调 MesParamStore.LoadParams，此处占位说明装配链完整。
    }
}
