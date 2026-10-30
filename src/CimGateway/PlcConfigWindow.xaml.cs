using CimGateway.ViewModels;
using System.Windows;
using CureLine.ViewModels;

namespace CimGateway;

/// <summary>
/// PLC 配置窗（MVVM 的 View：只设 DataContext + 响应关窗请求）。
/// 结果由 ViewModel.PlcChanged 带出（DialogService.ShowPlcConfig 读它）。
/// </summary>
public partial class PlcConfigWindow : Window
{
    public PlcConfigViewModel ViewModel { get; }

    public PlcConfigWindow()
    {
        ViewModel = new PlcConfigViewModel();
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.RequestClose += result =>
        {
            DialogResult = result;
            Close();
        };
    }

    /// <summary>标题条关闭钮：不设 DialogResult（等价取消，配置不落盘）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
