// =========================================================================
// MainWindow：固化程控主窗（程序①骨架）
//   View 只做装配：DataContext 已由 App 注入；这里只留退出按钮转发。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;
using CureLine.Core.Infrastructure;

namespace CureStation;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // 无边框化（V0.0.28）：最大化时窗矩形比可视工作区四周大一个 resize 边框
        // （96dpi≈8px，SystemParameters 已含 DPI 修正），自绘窗没有 OS 非客户区帮
        // 我们裁掉——这里给根 Grid 垫回同厚度，防顶栏/工单卡被屏幕边裁切。
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

    /// <summary>系统钮：关闭 ✕ = 退出（V0.0.39 顶栏精简后"退出"钮已删，✕ 是唯一退出入口，
    ///     与 VM.Dispose 同链，无二次确认弹窗）。</summary>
    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// "设置"下拉菜单项统一收合：命令执行由 XAML Command 绑定转给 VM，
    /// 这里只把触发钮 IsChecked 复位 false（Popup IsOpen 两向绑它）——
    /// StaysOpen=False 只管"点外部自关"，点菜单项不会自动关，必须在此收口。
    /// 程序①顶栏所有 SettingsMenuBtn 均挂本处理器。
    /// </summary>
    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        SettingsToggle.IsChecked = false;
    }
}
