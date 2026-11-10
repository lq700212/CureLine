// =========================================================================
// TraceReportWindow：固化追溯报表窗（V0.0.97，程序①）
//   View 只做装配：DataContext（编排在 TraceReportViewModel）+ 关窗转发——
//   业务零代码。窗为普通模态（ShowDialog）：报表是"看一眼就走"的功能，
//   与报警确认那种"必须现场处理的常驻弹窗"不同，不用单例常驻。
//   导出 CSV 的保存对话框由宿主（DialogService）在 UI 线程弹——VM 不碰 UI。
// =========================================================================

using System.Windows;
using CureStation.ViewModels;

namespace CureStation;

public partial class TraceReportWindow : Window
{
    public TraceReportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 装配并展示（宿主调）：换绑 VM 后开窗。VM 构造时已自带首查——
    /// 打开就能看到数据，不用操作员再点一次【查询】。
    /// </summary>
    public void Present(TraceReportViewModel vm)
    {
        DataContext = vm;
        ShowDialog();
    }

    /// <summary>标题条 ✕（无边框自绘标配）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
