// =========================================================================
// MesLogViewModel：日志窗的状态 + 命令（MVVM）。
//
// 绑什么：日志行集合（ListBox 直接绑）+ 静态文本 + 清空/语言命令。
// 实时流：订阅 Core.MesLogger.LogAdded，后台线程触发时经 UI 上下文
//   Post 进集合（ViewModelBase 捕获的 _ui 在此用；集合更新走 Post，
//   不走属性通知，WPF 线程安全）。关窗退订经 Dispose（View.OnClosed 调）。
// 滚动到底部是纯视图行为，留给 View（订阅集合变化调 ScrollIntoView）。
// =========================================================================

using CureLine.ViewModels;
using System.Collections.ObjectModel;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;

namespace CimGateway.ViewModels;

public class MesLogViewModel : ViewModelBase, IDisposable
{
    private readonly int _stationId;
    private bool _disposed;

    public MesLogViewModel(int stationId = -1)
    {
        _stationId = stationId;
        var history = stationId < 0
            ? MesLogger.GetCachedLogs()
            : MesLogger.GetCachedLogs(stationId);
        foreach (string line in history)
            Lines.Add(line);
        MesLogger.LogAdded += OnLogAdded;
        ClearCommand = new RelayCommand(() => Lines.Clear());
        SwitchLanguageCommand = new RelayCommand(SwitchLanguage);
    }

    /// <summary>日志行（View 直接绑 ItemsSource；只清显示不动后台缓存）。</summary>
    public ObservableCollection<string> Lines { get; } = new();

    public string TitleText => Tr("MES 交互实时日志");
    public string ClearText => Tr("清空当前显示");
    public string LangText => LanguageService.CurrentLanguage == "CH" ? "English" : "中文";

    public RelayCommand ClearCommand { get; }
    public RelayCommand SwitchLanguageCommand { get; }

    public void RefreshTexts() => RefreshAll();

    private static string Tr(string zh) => LanguageService.Tr(zh);

    private void SwitchLanguage()
    {
        LanguageService.CurrentLanguage =
            LanguageService.CurrentLanguage == "CH" ? "EN" : "CH";
        IniFile.Write(AppPaths.ConfigIni, "setting", "Language",
            LanguageService.CurrentLanguage);
        RefreshTexts();
    }

    private void OnLogAdded(string message, int stationId)
    {
        // 分流：单机窗只收 -1，多机窗只收本机台（老项目 index 过滤语义）。
        if (_stationId < 0 ? stationId >= 0 : stationId != _stationId)
            return;
        UiInvoke(() => AddLine(message));
    }

    private void AddLine(string message)
    {
        if (_disposed)
            return;
        Lines.Add(message);
        if (Lines.Count > 300)
            Lines.RemoveAt(0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MesLogger.LogAdded -= OnLogAdded;
    }
}
