// =========================================================================
// MesAlarmViewModel：报警窗展示（MVVM，纯显示 + 关闭命令）。
// =========================================================================

using PlcMesBridge.ViewModels;
using PlcMesBridge.Core.Infrastructure;

namespace CimGateway.ViewModels;

public class MesAlarmViewModel : ViewModelBase
{
    public MesAlarmViewModel(string message, string name)
    {
        Message = message;
        MachineName = name;
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(true));
    }

    public event Action<bool?>? RequestClose;

    public string Message { get; }
    public string MachineName { get; }

    public string TitleText => LanguageService.Tr("MES 报警信息");
    public string CloseText => LanguageService.Tr("关闭");

    public RelayCommand CloseCommand { get; }

    public void RefreshTexts() => RefreshAll();
}
