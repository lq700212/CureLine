// =========================================================================
// TraceReportWindow：固化追溯报表窗（V0.0.97，程序①）
//   View 只做装配：DataContext（编排在 TraceReportViewModel）+ 关窗转发——
//   业务零代码。窗为普通模态（ShowDialog）：报表是"看一眼就走"的功能，
//   与报警确认那种"必须现场处理的常驻弹窗"不同，不用单例常驻。
//   导出 CSV 的保存对话框由宿主（DialogService）在 UI 线程弹——VM 不碰 UI。
// =========================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CureStation.ViewModels;

namespace CureStation;

public partial class TraceReportWindow : Window
{
    public TraceReportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 装配并展示（宿主调）：换绑 VM 后开窗。开窗即首查——打开就能看到数据，
    /// 不用操作员再点一次【查询】（V0.0.100 修：此前注释宣称"构造时已自带
    /// 首查"但实际没人调，开窗恒是 0 卡空表；首查收口在装配这一步，VM 构造
    /// 保持零副作用——测试可直驱实例化）。
    /// </summary>
    public void Present(TraceReportViewModel vm)
    {
        DataContext = vm;
        vm.QueryAsync();
        ShowDialog();
    }

    /// <summary>标题条 ✕（无边框自绘标配）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 库位格子/【全部】行点选后收面板（V0.0.100）：命令已把选中码写进 VM，
    /// 这里只负责收下拉（点选即关闭——选择完一目了然，不用再点别处）。
    /// </summary>
    private void OnLocationPicked(object sender, RoutedEventArgs e)
    {
        LocToggle.IsChecked = false;
    }

    /// <summary>
    /// 日期整框"点哪儿都弹日历"（V0.0.101 修用户实测：原生 DatePicker 只认
    /// PART_Button 图标钮点击——图标区才 13px，点框主体恒无反应）。模板根
    /// PART_Root 铺 Transparent 接住整框命中后在这里转发开日历；点图标钮时
    /// ButtonBase 自己吃掉 MouseLeftButtonDown（Handled=true，冒泡到不了根），
    /// 不会与图标钮的开/关 toggle 打架。纯点选口径无手输，键盘行为不动。
    /// （V0.0.102 补：IsDropDownOpen 转发一直有效，日历弹不出是模板件名错——
    /// 弹出 Popup 必须命名 PART_Popup，见 BareDatePicker 模板注释。）
    /// </summary>
    private void OnDatePickerShellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.TemplatedParent is DatePicker dp &&
            !dp.IsDropDownOpen)
        {
            dp.IsDropDownOpen = true;
            e.Handled = true;
        }
    }
}
