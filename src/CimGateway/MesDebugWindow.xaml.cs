using CimGateway.ViewModels;
using System.Windows;
using CureLine.ViewModels;

namespace CimGateway;

/// <summary>
/// 调试窗（MVVM 的 View：只设 DataContext）。
/// 表单/预览/上传全在 MesDebugViewModel。
/// </summary>
public partial class MesDebugWindow : Window
{
    public MesDebugViewModel ViewModel { get; }

    public MesDebugWindow()
    {
        ViewModel = new MesDebugViewModel();
        DataContext = ViewModel;
        InitializeComponent();
    }

    /// <summary>标题条关闭钮。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
