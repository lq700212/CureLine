// =========================================================================
// ModalForegroundGuard：模态弹窗"切后台再切回自动找回"护栏
// （WPF 版，移植自 HJVision V4.4.384 第三版方案——WM_ACTIVATEAPP 广播监听）
//
// 干什么：主界面开着模态弹窗（ShowDialog）时把软件切到后台再切回来，
//   Windows 会把前台激活权落到被模态禁用的主窗上——主窗"看得见点不动"，
//   真正的弹窗压在后面，用户点哪都没反应，像死机（HJVision 称"幽灵模态
//   拦截"）。本护栏在应用回到主窗的一刻自动把"还活着的最上层模态弹窗"
//   Activate 回前台，用户免于任何找回动作。用户实测复现路径（V0.0.92 补）：
//   开工单窗 → Win+D 显示桌面 → 任务视图点主窗缩略图切回 → 主界面点哪
//   都不响应，弹窗最小化看不到——正是本护栏要兜的场景。
//
// 机制（为什么这样修）：
//   1. 信号源两条（V0.0.92 起双保险）：
//      ① WM_ACTIVATEAPP(wParam=TRUE)：应用整体回前台时系统向本线程顶层窗
//         广播此消息——覆盖"从别的程序切回"（桌面/别的软件 → 本应用）；
//      ② 主窗 Activated：同进程内窗口切换不发 WM_ACTIVATEAPP（应用前台
//         没变，比如 Alt+Tab 在主窗与弹窗间切到被模态禁用的主窗），同样
//         造成幽灵拦截——主窗重新获得激活也走一遍找回。
//      两条都经 Dispatcher.BeginInvoke 延迟一拍再动作（系统激活序列还在
//      进行，不能在 WndProc/激活回调深处动窗口——HJVision 第二版起的
//      经验：收尾不进 WndProc 深处）。
//   2. 找谁 = WPF 的 ShowDialog 会禁用本应用全部其它窗口（比 WinForms 只禁
//      owner 更齐整）。由此得到两个可靠判据：
//      ① 存在"可见且被禁"的窗口 ⇔ 当前有 ShowDialog 在跑（无模态时护栏
//         整体不动作，日志/报警/报警复位窗等非模态窗零影响；项目也无任何
//         窗口级 IsEnabled 手动赋值，不会被误触发）。V0.0.92 从"主窗被禁"
//         放宽为"任一窗被禁"——判定不再锚在"哪个窗是主窗"上（原实现用
//         Application.MainWindow 判主窗，该属性随激活漂移：模态弹窗打开后
//         它指向弹窗，跨应用切回主窗的瞬间还没簿记回来——用户实测路径下
//         判据整体失灵、护栏不动作，即本轮修的缺口）；
//      ② 非主窗的可见窗里，唯一还 IsEnabled 的那个 = 最上层模态弹窗
//        （下层弹窗与主窗都被上层的 ShowDialog 禁了）。
//      窗口集合 Application.Current.Windows 的顺序 = 打开顺序，所以"取
//      enabled 可见非主窗的最后一个"即最上层。多层弹窗（用户管理窗上再
//      开确认框）自动取到真正接客的那层。
//   3. 动作 = 最小化则先恢复 WindowState，再 Activate() 拿回前台焦点。
//      延迟一拍（Dispatcher.BeginInvoke）才做——广播到达时系统激活序列
//      还在进行，立刻动窗口会与系统的激活收序打架。
//
// 怎么改：接线只有一行——两程序 App.xaml.cs 在主窗 Show() 之后调
//   ModalForegroundGuard.Install(win)。新增弹窗零接线（ShowDialog 打开
//   的窗自动受保护）。MessageBox 是 Win32 原生对话框：不进 Application.Windows、
//   也不禁 WPF 窗口的 IsEnabled，本判据覆盖不到——其切回体验靠 OS 的
//   owner 关系兜底（ShowDialog 打开瞬间它挂当前激活窗为 owner，任务视图
//   里能单独选回），若现场反馈再升级为自绘确认窗。选窗判据抽在纯函数
//   PickRestoreIndex（快照 record，不依赖 WPF 窗口实例），改判据先补
//   ModalForegroundGuardTests 用例。
//
// 参考：E:\Project\HJVision\GYZVision\SubClass\ModalMinimizeGuard.cs
//   （WinForms 版第一~三版全程：第一版"禁最小化"被用户实测否决的教训、
//   第二版"最小化瞬间临时解放 owner 抢前台"的时机经验、第三版本方案）。
//   CureLine 弹窗统一骨架无最小化钮，第二版修的"弹窗最小化瞬间丢前台"
//   场景不存在，只移植第三版找回链。
// =========================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace CureLine.Core.Infrastructure;

public static class ModalForegroundGuard
{
    /// <summary>WM_ACTIVATEAPP：应用获得/失去前台时系统广播给该线程全部顶层窗；wParam 非 0 = 刚回到前台。</summary>
    private const int WM_ACTIVATEAPP = 0x001C;

    private static bool _installed;

    /// <summary>
    /// Install 时锚定的真主窗引用。判定"谁是不是主窗"必须用它——
    /// Application.MainWindow 会随激活漂移（模态弹窗打开后指向弹窗，
    /// 切回主窗的瞬间还没簿记回来，V0.0.92 用户实测缺口），不可靠。
    /// </summary>
    private static Window? _mainWin;

    /// <summary>
    /// 在主窗上安装"应用回前台找回模态弹窗"钩子。必须在主窗 Show() 之后调用
    /// （窗口句柄此时已建）；幂等，重复调用无副作用。钩子随主窗 HwndSource 生命周期，
    /// 无需摘除（主窗关闭即程序退出）。
    /// </summary>
    public static void Install(Window mainWindow)
    {
        if (_installed || mainWindow == null) return;
        var src = HwndSource.FromHwnd(new WindowInteropHelper(mainWindow).Handle);
        if (src == null) return;
        src.AddHook(WndProc);
        // 双保险第二条（V0.0.92）：进程内窗口切换不发 WM_ACTIVATEAPP（应用前台
        // 没变）——Alt+Tab/任务视图选中被模态禁用的主窗同样造成幽灵拦截，
        // 主窗重新获得激活时也走一遍找回（无模态时判据直接早退，零开销；
        // 弹窗正常关闭触发的主窗重激活在判据处自然短路，不会误动作）。
        mainWindow.Activated += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(new Action(RestoreTopModal));
        _mainWin = mainWindow;
        _installed = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 只关心"应用回到前台"这一刻；全部消息原样透传（旁观不拦，与 HJVision 同姿势）
        if (msg == WM_ACTIVATEAPP && wParam != IntPtr.Zero)
        {
            // 延迟一拍：此刻系统激活序列还在进行，不能在 WndProc 深处动窗口
            Application.Current?.Dispatcher.BeginInvoke(new Action(RestoreTopModal));
        }
        return IntPtr.Zero;
    }

    /// <summary>选窗判据的快照（与 WPF Window 解耦，纯数据可测）。</summary>
    public sealed record WinSnapshot(bool IsVisible, bool IsEnabled);

    /// <summary>
    /// 从窗口快照里挑出该拉回前台的模态弹窗索引。返回 -1 = 不动作。
    /// 【判据】存在"可见且被禁"的窗口 ⇔ 有 ShowDialog 在跑（否则一律不动作，
    /// 非模态窗零误伤）；在可见、enabled 的窗口里取最后一个（Application.Windows
    /// 顺序 = 打开顺序，最后打开的 = 最上层模态弹窗）。
    /// </summary>
    public static int PickRestoreIndex(IReadOnlyList<WinSnapshot> wins)
    {
        // 有模态在跑 ⇔ 存在被 ShowDialog 禁用的可见窗口（ShowDialog 禁本线程
        // 全部其它窗口，主窗必然在其中）。V0.0.92 从"主窗被禁"放宽为"任一窗
        // 被禁"且判据不再锚定"哪个窗是主窗"——Application.MainWindow 随激活
        // 漂移（模态弹窗打开后指向弹窗），跨应用切回主窗的瞬间该属性还没簿记
        // 回来，靠它判主窗存在时序盲区（用户实测 Win+D→任务视图点主窗复现）。
        bool anyDisabled = false;
        for (int i = 0; i < wins.Count; i++)
        {
            if (wins[i].IsVisible && !wins[i].IsEnabled)
            {
                anyDisabled = true;
                break;
            }
        }
        if (!anyDisabled) return -1;

        int pick = -1;
        for (int i = 0; i < wins.Count; i++)
        {
            var w = wins[i];
            if (!w.IsVisible || !w.IsEnabled) continue;   // 被禁/隐藏窗天然出局（模态中主窗必被禁，无需再判身份）
            pick = i;   // 集合序 = 打开序，恒取最后 = 最上层
        }
        return pick;
    }

    /// <summary>应用回前台后的找回动作：把最上层模态弹窗 Restore + Activate（异常静默，护栏不反噬正常窗口行为）。</summary>
    internal static void RestoreTopModal()
    {
        try
        {
            var app = Application.Current;
            // 主窗判定用 Install 锚定的引用；护栏未接线时回退运行时属性（尽力而为）
            var main = _mainWin ?? app?.MainWindow;
            if (app == null || main == null || !main.IsVisible) return;

            // 快照（Application.Windows 顺序 = 打开顺序）
            var wins = new List<Window>();
            foreach (Window w in app.Windows) wins.Add(w);
            var snaps = new List<WinSnapshot>(wins.Count);
            foreach (var w in wins)
                snaps.Add(new WinSnapshot(IsVisible: w.IsVisible,
                                          IsEnabled: w.IsEnabled));

            var idx = PickRestoreIndex(snaps);
            if (idx < 0) return;

            var target = wins[idx];
            // 最小化弹窗先恢复屏幕原位（HJVision Restore 同语义；启用窗口 Activate 才有意义）
            if (target.WindowState == WindowState.Minimized)
                target.WindowState = WindowState.Normal;
            target.Activate();   // 此刻本应用正在获得前台，激活自己被系统允许
        }
        catch { /* 护栏是体验加固：找回失败退化为旧状态（用户仍可 Alt+Tab 手动找），绝不反噬 */ }
    }
}
