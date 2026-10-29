// =========================================================================
// LogWindowViewModel：日志/控制台悬浮窗编排（操作日志流 + 系统状态卡）
//
// 干什么：订阅 LocalLogService.Written（全工程日志单一汇聚口）→ 操作日志
//   流（最新在上、封顶 200 条）+ 打开一刻的系统状态卡快照（见
//   MainViewModel.BuildSystemSnapshot）。
//
// 【线程红线落地，与状态机锁内轻量投递红线同款纪律】：
//   Written 在后台线程甚至状态机 _sync 锁内触发——处理器只做无锁入队
//   （ConcurrentQueue.Enqueue），绝不同步碰 UI 集合、绝不 Dispatcher.Invoke
//   阻塞等待；DispatcherTimer 100ms 一拍批量搬运（一次 CollectionChanged 少量
//   触发，ListBox 虚拟化从容）。
//
// 生命周期：Attach/Detach 幂等——窗口 Closed 必须 Detach()（退订 + 停定时器，
//   否则悬挂订阅=内存泄漏+幽灵窗口重复入队）。
// =========================================================================

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CureLine.Core.Services;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

public sealed class LogWindowViewModel : ViewModelBase
{
    /// <summary>日志显示行封顶（最新在上；超限从尾部挤掉最旧，防长跑内存涨）。</summary>
    private const int MaxLines = 200;

    /// <summary>操作日志流（最新在上；完整显示行 HH:mm:ss + 全文，不截 50 字——截断只限文件）。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>系统状态卡（打开一刻定格，不随主窗续推）。</summary>
    public ObservableCollection<SystemStatusRow> StatusRows { get; }

    /// <summary>状态卡生成时刻（HH:mm:ss，一眼分清新旧快照）。</summary>
    public string GeneratedAtText { get; }

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _drainTimer;
    private bool _attached;

    public LogWindowViewModel(LogSystemSnapshot snapshot)
    {
        GeneratedAtText = snapshot.GeneratedAtText;
        StatusRows = new ObservableCollection<SystemStatusRow>(snapshot.Rows);
        _drainTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _drainTimer.Tick += (_, _) => Drain();
        Attach();
    }

    /// <summary>订阅 Written + 起搬运定时器（幂等；Present 重开窗时复用同一逻辑）。</summary>
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        LocalLogService.Written += OnWritten;
        _drainTimer.Start();
    }

    /// <summary>退订 + 停定时器（窗口 Closed 必调；悬挂订阅会重复入队=刷屏+泄漏）。</summary>
    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        LocalLogService.Written -= OnWritten;
        _drainTimer.Stop();
    }

    /// <summary>Written 处理器：只入队（后台线程/锁内触发，绝不同步碰 UI 集合）。</summary>
    private void OnWritten(string line) => _pending.Enqueue(line);

    /// <summary>100ms 一拍批量搬运：把队内全部出尽，按原时间序插到表头（最新在上）、封顶挤尾。</summary>
    private void Drain()
    {
        if (_pending.IsEmpty)
            return;
        var batch = new List<string>();
        while (_pending.TryDequeue(out var line))
            batch.Add(line);
        // 按时间序逐条插到 i 位（batch[0] 最旧、batch[^1] 最新 → 最新始终在表头）
        for (int i = 0; i < batch.Count; i++)
            LogLines.Insert(i, batch[i]);
        while (LogLines.Count > MaxLines)
            LogLines.RemoveAt(LogLines.Count - 1);
    }
}
