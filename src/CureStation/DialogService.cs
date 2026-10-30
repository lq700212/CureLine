// =========================================================================
// IDialogService：程序①自己的对话框出口（VM 开窗/提示唯一出口，MVVM 可测性关键）
//
// 干什么：VM 不许直接 new Window/MessageBox（那样 VM 就测不了）——
//   一律走本服务；测试用 FakeDialogService 桩（tests 工程）断言调用。
// 为什么与主程序 CimGateway（原 PlcMesBridge，程序②）的 IDialogService 平行而不下沉共享：
//   窗集合是程序的私有资产——程序①的窗集（点表配置/工单/权限）与主程序
//   （登录/网关配置/调试）完全不同，共用接口会把两个程序的窗耦合死。
// 怎么改：新增窗体在这里加专用方法（生产实现 + Fake 桩 + VM 测试三处同步）。
// =========================================================================

using System.Windows;
using CureLine.Core.Curing;
using CureLine.Core.Security;

namespace CureStation.ViewModels;

/// <summary>日志悬浮窗"系统状态卡"一行（标签 + 值）。</summary>
public sealed record SystemStatusRow(string Label, string Value);

/// <summary>
/// 日志悬浮窗的系统状态卡快照（打开一刻定格，不随主窗 500ms 续推——
/// 状态卡看的是"开窗时系统在什么状态"，日志流才是实时件）。
/// </summary>
public sealed record LogSystemSnapshot(string GeneratedAtText, IReadOnlyList<SystemStatusRow> Rows);

public interface IDialogService
{
    /// <summary>提示框（确定）。前阻塞，纯通知。</summary>
    void ShowMessage(string message, string title);

    /// <summary>是否框（是/否）。返回 true=是。</summary>
    bool Confirm(string message, string title);

    /// <summary>打开点表配置窗（模态）。返回 true 表示保存了点表（调用方可重建总线）。</summary>
    bool ShowPointTable();

    /// <summary>打开工艺策略窗（模态）。返回 true 表示保存了策略（调用方可刷新激活策略）。</summary>
    bool ShowCuringPolicy();

    /// <summary>
    /// 登录窗（模态）。返回 null=取消；成功返回（用户名, 级别）——
    /// 级别是工单页/策略窗/点表/权限管理门禁的依据。
    /// </summary>
    (string User, OperatorLevel Level)? ShowLogin();

    /// <summary>打开权限管理窗（模态）。仅 LEVEL.4 可开（门禁在主窗，VM 不重复查）。</summary>
    void ShowOperators();

    /// <summary>
    /// 打开工单信息窗（模态，传当前操作员级别做门禁——级别不够的编辑区禁用）。
    /// 返回 true 表示保存了工单。
    /// </summary>
    bool ShowWorkOrder(OperatorLevel? currentLevel);

    /// <summary>打开过账配置窗（模态）。返回 true 表示保存了配置（调用方热更新过账服务）。</summary>
    bool ShowMesPostConfig();

    /// <summary>
    /// 打开 IO 通讯测试窗（模态）。bus=主窗已启动的共享总线（测试窗不建 TCP），
    /// config=点表配置（通道清单来源——通用化核心：点表怎么配测试窗就怎么显示）。
    /// </summary>
    void ShowIoTest(CureLine.Core.Comms.IPointTableBus bus, CureLine.Core.Comms.PointTableConfig config);

    ///
    /// <summary>打开日志/控制台悬浮窗（非模态、单例——已开则只刷新快照并置前）。</summary>
    void ShowLog(LogSystemSnapshot snapshot);

    /// <summary>程序化关闭主进程（退出）。</summary>
    void Shutdown();
}

/// <summary>生产实现：真 WPF 窗 + MessageBox。</summary>
public sealed class DialogService : IDialogService
{
    public void ShowMessage(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool ShowPointTable()
    {
        // 生产装配：VM 在服务里 new（测试绕过此窗直接实例化 VM 用 Fake 桩）。
        var win = new PointTableWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new PointTableViewModel(new DialogService()),
        };
        return win.ShowDialog() == true;
    }

    public bool ShowCuringPolicy()
    {
        // 生产装配：同点表窗模式（VM 在服务里 new，测试用 Fake 桩）。
        // 架数/层数与主窗仓位网格同口径（点表最大架/层号，垫底 10×3）——
        // 可视化预览画布按它铺格子，双架点表时预览铺 20×3。
        var config = CureLine.Core.Comms.IoPointTableStore.Load();
        int racks = Math.Max(CureLine.Core.Curing.SlotLocationCode.RacksPerRow,
            config?.Devices.SelectMany(d => d.Bindings)
                .Where(b => b.Rack > 0).Select(b => b.Rack).DefaultIfEmpty(1).Max() ?? 10);
        int levels = Math.Max(CureLine.Core.Curing.SlotLocationCode.Levels,
            config?.Devices.SelectMany(d => d.Bindings)
                .Where(b => b.Level > 0).Select(b => b.Level).DefaultIfEmpty(1).Max() ?? 3);
        var win = new CuringPolicyWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new CuringPolicyViewModel(new DialogService(), racks, levels),
        };
        return win.ShowDialog() == true;
    }

    public void ShowIoTest(CureLine.Core.Comms.IPointTableBus bus, CureLine.Core.Comms.PointTableConfig config)
    {
        // 生产装配：测试窗共享主窗总线（不自建 TCP——对齐 AgingTestSystem V1.23）。
        var win = new IoTestWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new IoTestViewModel(bus, config),
        };
        win.ShowDialog();
    }

    public (string User, OperatorLevel Level)? ShowLogin()
    {
        // 生产装配：VM 在服务里 new（测试用 Fake 桩带出预置结果）。
        var vm = new LoginViewModel();
        var win = new LoginWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = vm,
        };
        return win.ShowDialog() == true ? (vm.LoginName, vm.Level!.Value) : null;
    }

    public void ShowOperators()
    {
        // 生产装配：权限管理窗（LEVEL.4 门禁在主窗）。
        var win = new OperatorWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new OperatorViewModel(new DialogService()),
        };
        win.ShowDialog();
    }

    public bool ShowWorkOrder(OperatorLevel? currentLevel)
    {
        // 生产装配：工单窗（门禁在 VM——级别不够的编辑区禁用）。
        // 物料上传动作接单线过账服务（Poster 现读盘上工单发 API0032）。
        // 工位布局开关（单/双工位面板）随开着窗那一刻的 [CuringMes] 配置。
        var win = new WorkOrderWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new WorkOrderViewModel(new DialogService(), currentLevel,
                uploadMaterial: side => CuringMesPosterHolder.Default?.UploadMaterial(side),
                postOptions: CuringMesOptions.Load()),
        };
        return win.ShowDialog() == true;
    }

    public bool ShowMesPostConfig()
    {
        // 生产装配：过账配置窗（L4 门禁在主窗；VM 在服务里 new，测试用 Fake 桩）。
        var win = new MesPostConfigWindow
        {
            Owner = Application.Current?.MainWindow,
            DataContext = new MesPostConfigViewModel(new DialogService()),
        };
        return win.ShowDialog() == true;
    }

    // 日志悬浮窗单例（static：各窗 new DialogService() 装配，单例要跨实例共享）。
    // 关窗（含 Owner 主窗退出联动关闭）即清引用；下次 ShowLog 重建重订日志流。
    private static LogWindow? _logWindow;

    public void ShowLog(LogSystemSnapshot snapshot)
    {
        // 非模态单例：已开只换系统状态卡快照 + 置前激活（防连点堆窗）；
        // 初开时右下角定位（LogWindow.Present 内部处理）。
        if (_logWindow == null)
        {
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        _logWindow.Owner = Application.Current?.MainWindow;
        _logWindow.Present(snapshot);
        _logWindow.Activate();
    }

    public void Shutdown() => Application.Current?.Shutdown();
}

/// <summary>
/// 过账服务的全局访问点（工单窗上传动作用）：MainViewModel 装配时赋值。
/// 静态桥接是无奈之举——工单窗 VM 在 DialogService 里 new，构造链拿不到
/// 主窗的 Poster 实例；与"对话框接口不下沉"同理，桥接点收在程序①内。
/// </summary>
public static class CuringMesPosterHolder
{
    public static CuringMesPoster? Default { get; set; }
}
