// =========================================================================
// MesLogViewModel：日志窗的状态 + 命令（MVVM，V0.0.51 表格化重做）。
//
// 绑什么：LogRows（时间/事件/详情三列结构化行）+ 静态文本 + 清空/语言命令。
// 数据源：Core.MesLogger.EntryAdded（结构化条目，与落盘行同源同格式）；
//   打开窗时历史缓存字符串经 TryParseEntry 还原（解析失败的旧行按 Info
//   级别整行兜底显示，信息不丢）。事件在工作线程触发，经 UiInvoke 封送。
// 级别判定（Level 三态，View 映射点色）：Send FAIL / NG / 失败 / 异常 / 超时
//   → Err；上传成功 / 连接成功 → Ok；其余（触发/读数等）→ Info。
//   关键词双语言（中英词条同源 LanguageService，英文模式按英文词判）。
// 滚动到底部是纯视图行为，留给 View（订阅集合变化调 ScrollIntoView）。
// =========================================================================

using CureLine.ViewModels;
using System.Collections.ObjectModel;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;

namespace CimGateway.ViewModels;

/// <summary>日志表一行（只读展示模型）。Level="Ok"/"Err"/"Info"。</summary>
public class MesLogRow : ViewModelBase
{
    public MesLogRow(string time, string title, string message, string level)
    {
        _time = time;
        _title = title;
        _message = message;
        _level = level;
    }

    private string _time;
    public string Time { get => _time; private set => Set(ref _time, value); }

    private string _title;
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _message;
    public string Message { get => _message; private set => Set(ref _message, value); }

    private string _level;
    public string Level { get => _level; private set => Set(ref _level, value); }
}

public class MesLogViewModel : ViewModelBase, IDisposable
{
    private readonly int _stationId;
    private bool _disposed;

    public MesLogViewModel(int stationId = -1)
    {
        _stationId = stationId;
        // 全量窗（-1）取全部机台历史合并；分机台窗取本台历史。
        var history = stationId < 0
            ? MesLogger.GetAllCachedLogs()
            : MesLogger.GetCachedLogs(stationId);
        foreach (string line in history)
            AddParsed(MesLogger.TryParseEntry(line, stationId), line);
        MesLogger.EntryAdded += OnEntryAdded;
        ClearCommand = new RelayCommand(() => LogRows.Clear());
        SwitchLanguageCommand = new RelayCommand(SwitchLanguage);
    }

    /// <summary>结构化日志行（View 直接绑 ItemsSource；清空只清显示不动后台缓存）。</summary>
    public ObservableCollection<MesLogRow> LogRows { get; } = new();

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

    private void OnEntryAdded(MesLogEntry e)
    {
        // 分流（V0.0.51 修正口径）：全量窗（-1）收全部机台日志（"-1 全部机台"
        // 语义）；分机台窗只收本机台。旧口径全量窗只收无主日志（-1），与
        // 语义不符且全量窗恒空——已随表格化重做一并修正。
        if (_stationId >= 0 && e.StationId != _stationId)
            return;
        UiInvoke(() => AddParsed(e, e.Formatted));
    }

    /// <summary>条目→行（级别判定）；entry=null（旧格式/手改行）按 Info 整行兜底。</summary>
    private void AddParsed(MesLogEntry? entry, string rawLine)
    {
        if (_disposed)
            return;
        if (entry == null)
        {
            LogRows.Add(new MesLogRow("", "LOG", rawLine, "Info"));
            Trim();
            return;
        }
        LogRows.Add(new MesLogRow(
            entry.Time.ToString("HH:mm:ss"), entry.Title, entry.Message, LevelOf(entry)));
        Trim();
    }

    private void Trim()
    {
        if (LogRows.Count > 300)
            LogRows.RemoveAt(0);
    }

    /// <summary>级别三态判定（标题+正文关键词，双语言——见类头）。</summary>
    private static string LevelOf(MesLogEntry e)
    {
        bool failByTitle = e.Title == "Send FAIL" || e.Title == "Parse Exception";
        bool ok = e.Message.Contains(Tr("MES上传成功")) || e.Message.Contains("MES upload success")
            || e.Message.Contains(Tr("连接机台成功")) || e.Message.Contains("Connect Station Success");
        bool err = failByTitle
            || e.Message.Contains(Tr("MES返回NG")) || e.Message.Contains("MES returned NG")
            || e.Message.Contains(Tr("MES网络异常/超时")) || e.Message.Contains("MES network error/timeout")
            || e.Message.Contains(Tr("MES反序列化失败")) || e.Message.Contains("MES deserialize failed")
            || e.Message.Contains(Tr("MES返回数据解析为空")) || e.Message.Contains("MES response empty")
            || e.Message.Contains(Tr("MES返回数据解析异常")) || e.Message.Contains("MES response parse error")
            || e.Message.Contains(Tr("连接机台失败")) || e.Message.Contains("Connect Station Failed");
        return err ? "Err" : ok ? "Ok" : "Info";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MesLogger.EntryAdded -= OnEntryAdded;
    }
}
