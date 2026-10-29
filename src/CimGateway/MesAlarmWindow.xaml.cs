using CimGateway.ViewModels;
using System.Windows;
using PlcMesBridge.ViewModels;

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
}
