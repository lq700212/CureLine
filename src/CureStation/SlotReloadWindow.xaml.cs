// =========================================================================
// SlotReloadWindow：放回再加工产品时的"固化时长口径"确认弹窗（V0.0.99，程序①）
//   View 只做装配：DataContext（编排在 SlotReloadViewModel）+ 关窗转发——
//   业务零代码。模态（ShowDialog）：这个选择决定整盘的固化时长，操作员必须
//   停下来看清楚再决定。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class SlotReloadWindow : Window
{
    public SlotReloadWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 装配并展示（宿主调）：换绑 VM 后开窗。窗关掉（✕/Alt+F4）时 VM 的
    /// Choice 仍是 Cancel——等同"取消放入"，不改变会话阶段。
    /// </summary>
    public void Present(SlotReloadViewModel vm)
    {
        DataContext = vm;
        ShowDialog();
    }

    /// <summary>标题条 ✕（无边框自绘标配；等同取消）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
