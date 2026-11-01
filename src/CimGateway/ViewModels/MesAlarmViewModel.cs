// =========================================================================
// MesAlarmViewModel：报警窗展示（MVVM，纯显示 + 关闭命令）。
// V0.0.51 观感对齐设计稿（红左缘 + 图标 + 报文代码块）：补报警时间戳
// （AlarmTime，窗打开时刻——报警即弹即看，取当下即事件时刻）。
// =========================================================================

using CureLine.ViewModels;
using CureLine.Core.Infrastructure;

namespace CimGateway.ViewModels;

public class MesAlarmViewModel : ViewModelBase
{
    public MesAlarmViewModel(string message, string name)
    {
        Message = message;
        MachineName = name;
        AlarmTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(true));
    }

    public event Action<bool?>? RequestClose;

    public string Message { get; }
    public string MachineName { get; }
    public string AlarmTime { get; }

    public string TitleText => LanguageService.Tr("MES 报警信息");
    public string CloseText => LanguageService.Tr("关闭");

    public RelayCommand CloseCommand { get; }

    public void RefreshTexts() => RefreshAll();
}
