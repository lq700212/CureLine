using CimGateway.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using PlcMesBridge.ViewModels;

namespace CimGateway;

/// <summary>
/// 日志窗（MVVM 的 View）。关窗时 Dispose VM（退订实时流）；
/// 新行到底部滚动是纯视图行为，留在这里。
/// </summary>
public partial class MesLogWindow : Window
{
    public MesLogViewModel ViewModel { get; }

    public MesLogWindow(int stationId = -1)
    {
        ViewModel = new MesLogViewModel(stationId);
        DataContext = ViewModel;
        InitializeComponent();
        ViewModel.Lines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add
                && BoxLogs.Items.Count > 0)
                BoxLogs.ScrollIntoView(BoxLogs.Items[^1]);
        };
        if (BoxLogs.Items.Count > 0)
            BoxLogs.ScrollIntoView(BoxLogs.Items[^1]);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
