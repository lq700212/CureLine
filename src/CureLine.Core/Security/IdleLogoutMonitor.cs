// =========================================================================
// IdleLogoutMonitor：登录空闲超时监视器（Core 纯逻辑可测）
//
// 干什么：记录"最近一次用户活动"时刻，按可配置的空闲上限判定超时——
//   超时即发一次 LogoutDue（宿主执行退出登录）。防现场离岗后账号被
//   他人顺手使用（工位共享工控机的常见安全口）。
//
// 宿主接线三件（程序①主窗）：
//   ① 打点——View 层挂 WPF InputManager.Current.PreProcessInput（进程级，
//      主窗/全部弹窗/菜单的鼠标键盘都算活动）调 NotifyActivity()；
//   ② 驱动——主窗 VM 刷新拍（500ms DispatcherTimer）顺带 Tick()；
//   ③ 消费——订阅 LogoutDue 执行登出（未登录时宿主自行忽略）。
//
// 判定口径：
//   · 分钟数经 Func<int> 每拍现取（先例 resetModeProvider/grillPolarityProvider），
//     <=0 = 禁用（不触发；基线跟随当下，重新启用从零计不翻旧账）；
//   · 空闲时长 >= 上限即触发（恰好到点算超时）；
//   · 触发后基线重置——下一次触发需再次积满上限，防连续刷事件；
//   · 时钟经 Func<DateTime> 注入（缺省 DateTime.Now），测试用假时钟推进。
//
// 线程纪律：打点/Tick/事件全在 UI 线程（输入事件与 DispatcherTimer 同线程），
//   无锁；LogoutDue 处理器禁止再弹模态框（登出是静默动作，日志+顶栏可见即可）。
// =========================================================================

namespace CureLine.Core.Security;

public sealed class IdleLogoutMonitor
{
    private readonly Func<int> _minutesProvider;
    private readonly Func<DateTime> _clock;
    private DateTime _lastActivity;

    /// <summary>空闲超时触发（参数=触发时生效的空闲上限分钟数，供日志引用）。
    /// UI 线程触发。</summary>
    public event Action<int>? LogoutDue;

    public IdleLogoutMonitor(Func<int> minutesProvider, Func<DateTime>? clock = null)
    {
        _minutesProvider = minutesProvider;
        _clock = clock ?? (() => DateTime.Now);
        _lastActivity = _clock();
    }

    /// <summary>活动打点：任意用户输入（鼠标/键盘）都调——基线推到当下。</summary>
    public void NotifyActivity() => _lastActivity = _clock();

    /// <summary>周期判定（宿主刷新拍调，500ms 一次）：超时发一次 LogoutDue。</summary>
    public void Tick()
    {
        var now = _clock();
        var minutes = _minutesProvider();
        if (minutes <= 0)
        {
            _lastActivity = now; // 禁用期基线跟随：重新启用从零计，不翻旧账
            return;
        }
        if (now - _lastActivity >= TimeSpan.FromMinutes(minutes))
        {
            _lastActivity = now; // 防连发：下一轮从头积
            LogoutDue?.Invoke(minutes);
        }
    }
}
