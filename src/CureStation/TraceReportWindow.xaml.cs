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
    /// <summary>
    /// 日期框按下时记录"本次点击落在哪个日期框"（V0.0.105 整框统一点击重做）。
    /// 日历已打开时点框内属于"点外部关闭"（StaysOpen=False 由弹层自己收），
    /// 不记——否则抬起会被误判成一次新的打开。
    /// </summary>
    private DatePicker? _pressPicker;

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
    /// 日期整框"点哪儿都弹日历"——按下阶段只做资格登记，真正打开在抬起
    /// （见 <see cref="OnDatePickerShellPreviewMouseUp"/>）。
    /// 判据三件：命中在日期框内（鼠标在控件上）、日历当前未打开、按下与抬起
    /// 是同一个日期框（防止按下 A 框、拖到 B 框松开时 B 框被误开）。
    /// </summary>
    private void OnDatePickerShellPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var dp = (sender as FrameworkElement)?.TemplatedParent as DatePicker;
        _pressPicker = dp != null && !dp.IsDropDownOpen && dp.IsMouseOver ? dp : null;
    }

    /// <summary>
    /// 抬起时打开日历（★必须是 MouseUp，不能用 MouseDown——V0.0.105 用户实测
    /// 的根因）：DatePicker 的弹出层是 StaysOpen=False，在 MouseDown 里置
    /// IsDropDownOpen=true 后，紧接着的本次 MouseUp 会被弹层判定为"框外抬起"
    /// 而立即关闭（临时日志实测：Popup.Opened → 214ms 后 Popup.Closed，肉眼
    /// 就是"点了没反应"）。抬起时打开=本次点击已结束，与原生图标钮的 Click
    /// 打开同序，日历才留得住。
    /// 鼠标按下后拖出框外再松开（IsMouseOver=false）视为取消，不打开。
    /// </summary>
    private void OnDatePickerShellPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        var dp = (sender as FrameworkElement)?.TemplatedParent as DatePicker;
        bool pressedWithin = dp != null && ReferenceEquals(dp, _pressPicker);
        _pressPicker = null;
        if (dp != null && pressedWithin && dp.IsMouseOver && !dp.IsDropDownOpen)
        {
            dp.IsDropDownOpen = true;
            e.Handled = true;
        }
    }
}
