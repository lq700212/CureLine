// =========================================================================
// MainWindow：固化程控主窗（程序①骨架）
//   View 只做装配：DataContext 已由 App 注入；这里只留退出按钮转发
//   + 小屏自适应的卡片高度实测（V0.0.85，见 HookSlotCardHeight）。
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CureStation.ViewModels;
using CureLine.Core.Infrastructure;

namespace CureStation;

public partial class MainWindow : Window
{
    /// <summary>是否已挂接 SN 卡高实测（首张卡只挂一次；30 卡 UniformGrid 均分同高）。</summary>
    private bool _snCardHeightHooked;

    public MainWindow()
    {
        InitializeComponent();
        // 无边框化（V0.0.28）：最大化时窗矩形比可视工作区四周大一个 resize 边框
        // （96dpi≈8px，SystemParameters 已含 DPI 修正），自绘窗没有 OS 非客户区帮
        // 我们裁掉——这里给根 Grid 垫回同厚度，防顶栏/工单卡被屏幕边裁切。
        StateChanged += (_, _) => ApplyRootMargin();
        Loaded += (_, _) =>
        {
            ApplyRootMargin(); // XAML 预设 Maximized 启动也要对齐一次
            HookSlotCardHeight();
        };
        // ContentRendered：ItemsControl 首轮模板化完成后的兜挂（Loaded 时可能
        // 还没生成第一张卡）。两个入口都幂等（HookSlotCardHeight 自防重复）。
        ContentRendered += (_, _) => HookSlotCardHeight();
        // 登录空闲自动退出（V0.0.93）：进程级输入打点——InputManager 收本进程
        // 全部窗口（主窗/弹窗/菜单）的鼠标键盘事件，任意操作都把空闲基线推到
        // 当下（NotifyActivity 纯内存赋值，高频零成本）。事件在 UI 线程，
        // DataContext 由 App 注入 ctor 时已就绪。
        if (DataContext is MainViewModel vm)
            InputManager.Current.PreProcessInput += (_, args) =>
            {
                if (args.StagingItem?.Input != null)
                    vm.IdleMonitor.NotifyActivity();
            };
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

    // ---------------------------------------------------------------------
    // SN 卡高实测（V0.0.85 小屏自适应，用户拍板"重叠风险下优先固化时间"）：
    // 量到卡渲染高 → MainViewModel.UpdateSnCardHeight 换算 SN 最大行数档
    // （1~4）统一下发 30 张卡。找不到实测点（结构异常）就静默不接管——
    // 档恒 4、行为与 V0.0.84 一致（fail-safe 到旧行为）。
    // 主窗 ✕ 关闭即进程退出（V0.0.39），退订无需显式收口。
    // ---------------------------------------------------------------------

    /// <summary>
    /// 幂等挂钩：视觉树下钻找第一张 SlotCard 卡根 Border（挂在 ContentPresenter
    /// 下的模板根）→ 订阅其 SizeChanged 并把当前实测值先喂一拍。调用时机
    /// = Loaded / ContentRendered（此时模板未生成则等后者兜挂）。
    /// </summary>
    private void HookSlotCardHeight()
    {
        if (_snCardHeightHooked || DataContext is not MainViewModel vm)
            return;

        var card = FindFirstSlotCard();
        if (card == null)
            return;   // 第一次 Loaded 时模板可能还没生成——ContentRendered 再兜一次

        _snCardHeightHooked = true;
        card.SizeChanged += OnSnCardHeightChanged;
        if (card.ActualHeight > 0)
            vm.UpdateSnCardHeight(card.ActualHeight);
    }

    private void OnSnCardHeightChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口 resize/换主题缩放都会改 UniformGrid 均分高度——这里是档位
        // 热降/热升的唯一入口；VM 内"档位变化才下发"自带防抖。
        if (DataContext is MainViewModel vm && e.NewSize.Height > 0)
            vm.UpdateSnCardHeight(e.NewSize.Height);
    }

    /// <summary>
    /// 视觉树下钻：从主内容 ItemsControl（RackGroupsHost）往下找第一个内容为
    /// RackSlotViewModel 的 ContentPresenter，其模板内容第一层视觉即卡根 Border
    /// （SlotCard 模板根）。下钻深度浅（ItemsControl→UniformGrid→Presenter×N）。
    /// </summary>
    private Border? FindFirstSlotCard()
    {
        Border? found = null;

        void Walk(DependencyObject node)
        {
            if (found != null)
                return;
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ContentPresenter { Content: RackSlotViewModel })
                {
                    // 直接子代即模板根（SlotCard 模板根就是 Border，非容器包裹）
                    var inner = VisualTreeHelper.GetChildrenCount(child);
                    for (var k = 0; k < inner && found == null; k++)
                        if (VisualTreeHelper.GetChild(child, k) is Border b)
                            found = b;
                }
                if (found == null)
                    Walk(child);   // 深度优先继续（TicksPresenter→UniformGrid 层）
            }
        }

        if (RackGroupsHost != null)
            Walk(RackGroupsHost);
        return found;
    }
}
