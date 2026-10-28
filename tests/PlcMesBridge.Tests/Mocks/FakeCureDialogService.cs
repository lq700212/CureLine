// =========================================================================
// FakeCureDialogService：程序①（CureStation）IDialogService 测试桩
// 用法与主程序 FakeDialogService 同：调后查 Calls 断 VM 调了谁。
// =========================================================================

using PlcMesBridge.Core.Security;

namespace PlcMesBridge.Tests.Mocks;

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

    /// <summary>ShowLogin 的桩返回值（null=取消；有值=登录成功带出级别）。</summary>
    public (string User, OperatorLevel Level)? LoginResult { get; set; }
    /// <summary>ShowWorkOrder 的桩返回值（true=保存成功）。</summary>
    public bool WorkOrderResult { get; set; }

    public (string User, OperatorLevel Level)? ShowLogin()
    {
        Calls.Add("ShowLogin");
        return LoginResult;
    }

    public void ShowOperators() => Calls.Add("ShowOperators");

    public bool ShowWorkOrder(OperatorLevel? currentLevel)
    {
        Calls.Add($"ShowWorkOrder:{currentLevel?.ToString() ?? "null"}");
        return WorkOrderResult;
    }

    public void Shutdown() => Calls.Add("Shutdown");
}
