// =========================================================================
// ScanNoticeWindow：扫码提示弹窗（V0.0.99，程序①）
//   View 只做装配：DataContext（编排在 ScanNoticeViewModel）+ 关窗转发——
//   业务零代码。模态（ShowDialog）：扫码拦截必须让操作员停下来看清楚，
//   不能像状态条浮层那样一闪而过。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class ScanNoticeWindow : Window
{
    public ScanNoticeWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 装配并展示（宿主调）：换绑 VM 后开窗。
    /// </summary>
    public void Present(ScanNoticeViewModel vm)
    {
        DataContext = vm;
        ShowDialog();
    }

    /// <summary>标题条 ✕（无边框自绘标配）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
