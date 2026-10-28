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
using CureStation.ViewModels;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Security;

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

        // 数据库与账号（P3.2）：建表（PLCtable/users 兜底）+ 播种四级操作员的
        // 初始管理员（空表才播；初始密码只在此一处，上线前必须在权限管理改掉）。
        new SqliteHelper(AppPaths.DatabaseFile).EnsureDatabase();
        OperatorStore.Default.EnsureSeeded("123456");

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
