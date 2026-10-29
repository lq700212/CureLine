using System.Windows.Controls;

namespace CimGateway.Controls;

/// <summary>
/// 单机台面板（MVVM 的 View：纯绑定，DataContext 由父 ItemsControl 项继承）。
/// VM 由 MainWindowVM 建好（StationPanelViewModel），本文件无逻辑。
/// </summary>
public partial class StationPanel : UserControl
{
    public StationPanel()
    {
        InitializeComponent();
    }
}
