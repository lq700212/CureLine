// =========================================================================
// MainWindow：固化程控主窗（程序①骨架）
//   View 只做装配：DataContext 已由 App 注入；这里只留退出按钮转发。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;
using PlcMesBridge.Core.Infrastructure;

namespace CureStation;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>退出去掉自带确认（骨架期一键关；状态机接入后加确认）。</summary>
    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Dispose();
        Application.Current.Shutdown();
    }
}
