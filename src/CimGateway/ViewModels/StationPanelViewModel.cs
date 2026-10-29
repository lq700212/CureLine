// =========================================================================
// StationPanelViewModel：单机台面板的状态 + 命令（MVVM）。
//
// 绑什么：机台名 / 心跳灯色 / 日志行（头插，上限 100 条，老项目语义）/
//   选中行（单击即全文）+ 双击开日志窗命令。
// 跨窗动作经回调由 MainWindowVM 注入（开日志窗 / 全文+存图链路），
//   本 VM 不认 IDialogService，保持面板独立可复用。
// =========================================================================

using CureLine.ViewModels;
using System.Collections.ObjectModel;
using CureLine.Core.Mes;

namespace CimGateway.ViewModels;

public class StationPanelViewModel : ViewModelBase
{
    private readonly Action<int> _openLog;
    private readonly Action<string> _showDetail;
    private string _lampColor = "Gray";
    private string? _selectedLine;
    private bool _visible = true;

    public StationPanelViewModel(int stationId,
        Action<int> openLog, Action<string> showDetail)
    {
        StationId = stationId;
        _openLog = openLog;
        _showDetail = showDetail;
        Name = MesLogger.StationNames[stationId];
        OpenLogCommand = new RelayCommand(() => _openLog(StationId));
    }

    public int StationId { get; }

    public string Name { get; }

    /// <summary>心跳灯色（Gray 未连 / Green 常亮 / Lime 闪 / Red 断连）。</summary>
    public string LampColor
    {
        get => _lampColor;
        private set => Set(ref _lampColor, value);
    }

    /// <summary>日志行（头插，上限 100，超了清空重记，老项目语义）。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>选中行（单击即全文，老项目 SelectedIndexChanged 语义）。</summary>
    public string? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (Set(ref _selectedLine, value) && value != null)
                _showDetail(value);
        }
    }

    /// <summary>面板显隐（主窗视图筛选"只看某几台"用，XAML 绑 Visibility）。</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    public RelayCommand OpenLogCommand { get; }

    /// <summary>心跳灯（复刻：正常 Green/Lime 闪，断连红）。</summary>
    public void SetLive(bool on, bool broken) =>
        UiInvoke(() => LampColor = broken ? "Red" : on ? "Lime" : "Green");

    public void AddLog(string line) => UiInvoke(() =>
    {
        if (LogLines.Count > 100)
            LogLines.Clear();
        LogLines.Insert(0, line);
    });
}
