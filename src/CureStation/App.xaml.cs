// =========================================================================
// App：固化程控程序入口（程序①）
//
// 干什么：OnStartup 确保 config/DataBase/log 目录（kernel32 不自动建目录，
//   老项目血泪）、挂全局异常兜底、软件授权门禁（未激活先弹激活窗）、
//   装配 MainViewModel + MainWindow，并起每小时授权巡检。
// 怎么改：新增窗体一律经 IDialogService 开（VM 不许 new Window）；
//   启动参数（点表/演示开关）走 config/pointtable.json + config.ini。
// 授权说明：V0.0.62 起授权真源收口在 kaleidoscope 子模块
//   Kaleidoscope.Security（设备码+era 挑战值+试用/永久+防回拨三存储），与
//   HJVision 同一套下沉代码；产品 ID 用 LicenseHost.Setup 注入为 CureLine
//   （0x434C），HJVision 的激活码在这里被产品隔离拒绝。
//   演示模式（config.ini [setting] Simulate=1）免激活放行（与 Mock 总线同口径）。
// =========================================================================

using System.IO;
using System.Windows;
using System.Windows.Threading;
using CureStation.Themes;
using CureStation.ViewModels;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;
using CureLine.Core.Services;
using CureLine.ViewModels;

namespace CureStation;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动期可能先弹独立的激活窗（还没有主窗）——显式关闭模式防止
        // "激活窗一关应用就自动退出"；主窗建好后再恢复 OnLastWindowClose。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 目录确保（删库不怕：AppPaths 全面 FirstTouch 目录存在性由各服务自理，
        // 这里兜底最关键的 config 目录——点表 JSON 落它下面）
        Directory.CreateDirectory(AppPaths.ConfigDir);
        Directory.CreateDirectory(AppPaths.LogDir);

        // 数据库与账号（P3.2）：建表（cure_record/users/app_meta 兜底）+ 播种三级预置账号
        // （空表才播；初始密码只在此一处，上线必须在用户管理窗改掉——包括 dev）。
        new SqliteHelper(AppPaths.DatabaseFile).EnsureDatabase();
        OperatorStore.Default.EnsureSeeded("123456", "123456", "dev123");

        // 启动配置（P3.2 后半）：读 config.ini——语言 + [MES] 6 接口 URL（过账用）
        // + MES 参数记忆（mes_params.json，静态参数的回退源）。ini 缺键自动回填。
        AppConfig.Load();

        // 双主题（2026-10-29）：按 [setting] Theme（Dark/Light，缺省 Light、非法回
        // Light）换主题字典——缺省值同时借 IniFile 自愈语义落盘（首次启动补键，
        // 铁律 8：config.sample.ini 同步了该键）。放在授权窗之前：激活窗也用主题键板。
        ThemeManager.ApplyTheme(IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Theme", ThemeManager.DefaultTheme));

        // ── 软件授权门禁（启动）：演示模式免激活；未授权先弹激活窗，仍未授权则退出 ──
        // V0.0.62 起真源在 kaleidoscope 子模块（LicenseHost.Setup 注入产品/桥/注册表
        // 覆盖点——必须在首次 Refresh 之前，否则本产品码被按别家产品拒绝）。
        LicenseHost.Setup();
        LicenseHost.Refresh();
        if (!LicenseHost.IsProductionAllowed)
        {
            var licenseVm = new LicenseActivationViewModel("固化程控");
            new LicenseActivationWindow { DataContext = licenseVm }.ShowDialog();
            LicenseHost.Refresh();
            if (!LicenseHost.IsProductionAllowed)
            {
                Shutdown();
                return;
            }
        }
        LocalLogService.Write("软件授权：" + LicenseHost.ShortStatusText());

        // 全局异常兜底：骨架期避免现场闪退无任何提示（后续接 LocalLogService）
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"程序遇到意外错误：\n{args.Exception.Message}",
                "固化程控 - 错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var vm = new MainViewModel(new DialogService());
        var win = new MainWindow { DataContext = vm };
        MainWindow = win;
        win.Show();

        // 主窗已建好：恢复"最后一个窗口关闭即退出"的常规语义
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // 每小时巡检授权：试用到期/记录被改成无效码/指纹变化——正常生产中途
        // 失效必须立刻停程序（授权只许一处判定，见 kaleidoscope LicenseGate /
        // 宿主接线 LicenseHost）。
        StartLicensePatrol();
    }

    /// <summary>
    /// 每小时重算授权状态；一旦失效（过期/指纹不符/码无效）提示并退出程序。
    /// 定时器声明周期随 Application（DispatcherTimer 由 Dispatcher 持有），
    /// 不需要字段引用；巡检只在 UI 线程跑（Refresh 的线程约定）。
    /// </summary>
    private static void StartLicensePatrol()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        timer.Tick += (_, _) =>
        {
            LicenseHost.Refresh();
            if (LicenseHost.IsProductionAllowed)
                return;
            timer.Stop();
            MessageBox.Show(
                "软件授权已失效，程序将退出。\n\n" + LicenseHost.StateText()
                + (string.IsNullOrEmpty(LicenseHost.LastReason) ? "" : "\n" + LicenseHost.LastReason)
                + "\n\n请联系厂商重新激活。",
                "软件授权", MessageBoxButton.OK, MessageBoxImage.Warning);
            Current?.Shutdown();
        };
        timer.Start();
    }
}
