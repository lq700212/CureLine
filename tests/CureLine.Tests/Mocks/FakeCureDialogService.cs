// =========================================================================
// FakeCureDialogService：程序①（CureStation）IDialogService 测试桩
// 用法与主程序 FakeDialogService 同：调后查 Calls 断 VM 调了谁。
// =========================================================================

using CureLine.Core.Security;
using CureStation.ViewModels;

namespace CureLine.Tests.Mocks;

public class FakeCureDialogService : CureStation.ViewModels.IDialogService
{
    public List<string> Calls { get; } = new();

    /// <summary>ShowPointTable 的桩返回值（true=保存成功）。</summary>
    public bool PointTableResult { get; set; }
    /// <summary>ShowCuringPolicy 的桩返回值（true=保存成功）。</summary>
    public bool CuringPolicyResult { get; set; }
    public bool ConfirmResult { get; set; } = true;

    public void ShowMessage(string message, string title) =>
        Calls.Add($"Msg:{title}:{message}");

    public bool Confirm(string message, string title)
    {
        Calls.Add($"Confirm:{title}");
        return ConfirmResult;
    }

    public bool ShowPointTable()
    {
        Calls.Add("ShowPointTable");
        return PointTableResult;
    }

    public bool ShowCuringPolicy()
    {
        Calls.Add("ShowCuringPolicy");
        return CuringPolicyResult;
    }

    /// <summary>ShowLogin 的桩返回值（null=取消；有值=登录成功带出角色）。</summary>
    public (string User, OperatorRole Role)? LoginResult { get; set; }
    /// <summary>ShowWorkOrder 的桩返回值（true=保存成功）。</summary>
    public bool WorkOrderResult { get; set; }

    public (string User, OperatorRole Role)? ShowLogin()
    {
        Calls.Add("ShowLogin");
        return LoginResult;
    }

    /// <summary>ShowAccounts 收到的 actor（断言门禁传得对）。</summary>
    public OperatorRole? AccountActor { get; private set; }

    public void ShowAccounts(OperatorRole actor)
    {
        Calls.Add("ShowAccounts");
        AccountActor = actor;
    }

    public bool ShowWorkOrder(OperatorRole? currentRole)
    {
        Calls.Add($"ShowWorkOrder:{currentRole?.ToString() ?? "null"}");
        return WorkOrderResult;
    }

    /// <summary>ShowMesPostConfig 的桩返回值（true=保存成功）。</summary>
    public bool MesPostConfigResult { get; set; }

    public bool ShowMesPostConfig()
    {
        Calls.Add("ShowMesPostConfig");
        return MesPostConfigResult;
    }

    /// <summary>ShowLicenseActivation 的桩返回值（true=激活成功）。</summary>
    public bool LicenseActivationResult { get; set; }

    public bool ShowLicenseActivation()
    {
        Calls.Add("ShowLicenseActivation");
        return LicenseActivationResult;
    }

    /// <summary>ShowScannerConfig 的桩返回值（true=保存成功）。</summary>
    public bool ScannerConfigResult { get; set; }

    public bool ShowScannerConfig()
    {
        Calls.Add("ShowScannerConfig");
        return ScannerConfigResult;
    }

    public void ShowIoTest(CureLine.Core.Comms.IPointTableBus bus, CureLine.Core.Comms.PointTableConfig config)
        => Calls.Add("ShowIoTest");

    /// <summary>ShowLog 收到的快照（测试断言状态卡内容用）。</summary>
    public LogSystemSnapshot? LogSnapshot { get; private set; }

    public void ShowLog(LogSystemSnapshot snapshot)
    {
        Calls.Add("ShowLog");
        LogSnapshot = snapshot;
    }

    /// <summary>ShowGrillAlarmReset 收到的编排器（断言主窗把owner实例传对了）。</summary>
    public GrillAlarmResetCoordinator? AlarmPresenter { get; private set; }

    public void ShowGrillAlarmReset(GrillAlarmResetCoordinator presenter)
    {
        Calls.Add("ShowGrillAlarmReset");
        AlarmPresenter = presenter;
    }

    public void CloseGrillAlarmReset() => Calls.Add("CloseGrillAlarmReset");

    /// <summary>ShowTraceReport 收到的追溯库存取（断言主窗把 owner 实例传对了）。</summary>
    public CureLine.Core.Data.SlotProductTraceStore? TraceStore { get; private set; }

    /// <summary>ShowTraceReport 收到的库位格子分组（断言主窗把点表换算的分组传对了）。</summary>
    public System.Collections.Generic.IReadOnlyList<CureStation.ViewModels.TraceLocationGroup>? TraceLocationGroups { get; private set; }

    public void ShowTraceReport(CureLine.Core.Data.SlotProductTraceStore store, Func<string?>? askSavePath,
        System.Collections.Generic.IReadOnlyList<CureStation.ViewModels.TraceLocationGroup>? locationGroups)
    {
        Calls.Add("ShowTraceReport");
        TraceStore = store;
        TraceLocationGroups = locationGroups;
    }

    /// <summary>
    /// 扫码拦截提示窗的桩：记录标题+正文+指引（断言主窗把拦截原因传对了）。
    /// </summary>
    public List<(string Title, string Message, string Detail)> ScanNotices { get; } = new();

    public void ShowScanNotice(string title, string message, string detail)
    {
        Calls.Add($"ShowScanNotice:{title}");
        ScanNotices.Add((title, message, detail));
    }

    /// <summary>
    /// 放回口径确认窗的桩：返回 ReloadChoiceResult（缺省 Cancel=不放这个产品）。
    /// </summary>
    public CureLine.Core.Curing.SlotReloadChoice ReloadChoiceResult { get; set; }
        = CureLine.Core.Curing.SlotReloadChoice.Cancel;

    /// <summary>AskSlotReloadBasis 收到的 prompt（断言弹窗文案带对了数）。</summary>
    public CureLine.Core.Curing.SlotReloadPrompt? ReloadPrompt { get; private set; }

    public CureLine.Core.Curing.SlotReloadChoice AskSlotReloadBasis(
        CureLine.Core.Curing.SlotReloadPrompt prompt)
    {
        Calls.Add("AskSlotReloadBasis");
        ReloadPrompt = prompt;
        return ReloadChoiceResult;
    }

    public void Shutdown() => Calls.Add("Shutdown");
}
