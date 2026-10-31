// =========================================================================
// App：固化程控程序入口（程序①骨架）
//
// 干什么：OnStartup 确保 config/DataBase/log 目录（kernel32 不自动建目录，
//   老项目血泪）、挂全局异常兜底、装配 MainViewModel + MainWindow。
// 怎么改：新增窗体一律经 IDialogService 开（VM 不许 new Window）；
//   启动参数（点表/演示开关）走 config/pointtable.json + config.ini。
// =========================================================================

using System.IO;
using System.Windows;
using CureStation.Themes;
using CureStation.ViewModels;
using CureLine.Core.Infrastructure;
using CureLine.Core.Security;

namespace CureStation;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 目录确保（删库不怕：AppPaths 全面 FirstTouch 目录存在性由各服务自理，
        // 这里兜底最关键的 config 目录——点表 JSON 落它下面）
        Directory.CreateDirectory(AppPaths.ConfigDir);
        Directory.CreateDirectory(AppPaths.LogDir);

        // 数据库与账号（P3.2）：建表（cure_record/users 兜底）+ 播种三级预置账号
        // （空表才播；初始密码只在此一处，上线必须在用户管理窗改掉——包括 dev）。
        new SqliteHelper(AppPaths.DatabaseFile).EnsureDatabase();
        OperatorStore.Default.EnsureSeeded("123456", "123456", "dev123");

        // 启动配置（P3.2 后半）：读 config.ini——语言 + [MES] 6 接口 URL（过账用）
        // + MES 参数记忆（mes_params.json，静态参数的回退源）。ini 缺键自动回填。
        AppConfig.Load();

        // 双主题（2026-10-29）：按 [setting] Theme（Dark/Light，缺省 Light、非法回
        // Light）换主题字典——缺省值同时借 IniFile 自愈语义落盘（首次启动补键，
        // 铁律 8：config.sample.ini 同步了该键）。
        ThemeManager.ApplyTheme(IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Theme", ThemeManager.DefaultTheme));

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
    }
}
