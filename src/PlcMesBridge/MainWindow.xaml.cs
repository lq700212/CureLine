using System.ComponentModel;
using System.Windows;
using PlcMesBridge.ViewModels;

namespace PlcMesBridge;

/// <summary>
/// 主窗（MVVM 的 View：只设 DataContext + 转发关窗事件）。
/// 多机视图 = 多机版 Form1（单机视图与"运行模式"已随单机链路删除，P2.1）。
/// 全部状态/命令在 MainWindowViewModel，Model 层（MultiMachineManager）不动。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.OnClosing())
        {
            e.Cancel = true;
            return;
        }
        ViewModel.Dispose();
        base.OnClosing(e);
    }
}
