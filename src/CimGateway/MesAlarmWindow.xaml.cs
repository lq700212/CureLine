using CimGateway.ViewModels;
using System.Windows;
using CureLine.ViewModels;

namespace CimGateway;

/// <summary>报警窗（MVVM 的 View）。用法：new MesAlarmWindow(报文, 机台名).Show()。</summary>
public partial class MesAlarmWindow : Window
{
    public MesAlarmViewModel ViewModel { get; }

    public MesAlarmWindow(string message, string name)
    {
        ViewModel = new MesAlarmViewModel(message, name);
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.RequestClose += _ => Close();
    }

    /// <summary>标题条关闭钮（与 VM CloseCommand 同链）。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
