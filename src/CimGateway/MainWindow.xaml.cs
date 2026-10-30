// =========================================================================
// MainWindow：CIM 通信网关主窗（MVVM 的 View：装配 + 系统钮转发 + 关窗确认）。
// 多机视图 = 多机版 Form1（单机视图与"运行模式"已随单机链路删除，P2.1）。
// 全部状态/命令在 MainWindowViewModel，Model 层（MultiMachineManager）不动。
// =========================================================================

using System.ComponentModel;
using System.Windows;
using CimGateway.ViewModels;
using CureLine.ViewModels;

namespace CimGateway;

public partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainWindowViewModel();
        DataContext = ViewModel;
        InitializeComponent();
        // 无边框化（对齐程序①口径）：最大化时窗矩形比可视工作区四周大一个 resize
        // 边框（SystemParameters 已含 DPI 修正），自绘窗没有 OS 非客户区帮我们裁掉
        // ——这里给根 Grid 垫回同厚度，防顶栏/机台卡被屏幕边裁切。
        StateChanged += (_, _) => ApplyRootMargin();
        Loaded += (_, _) => ApplyRootMargin(); // XAML 预设 Maximized 启动也要对齐一次
    }

    /// <summary>根元素边距随窗口态同步：最大化垫回 resize 边框，还原/最小化归零。</summary>
    private void ApplyRootMargin()
    {
        if (RootGrid == null) return; // 构造早期 StateChanged 可能先到
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);
    }

    /// <summary>系统钮：最小化（自绘窗没有 OS 按钮了，顶栏三钮顶替）。</summary>
    private void OnMinClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>系统钮：最大化 ↔ 还原切换（双击顶栏走 WindowChrome 原生行为）。</summary>
    private void OnMaxClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>系统钮关闭：走 OnClosing 确认链（与 VM"是否退出?"同口径）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
