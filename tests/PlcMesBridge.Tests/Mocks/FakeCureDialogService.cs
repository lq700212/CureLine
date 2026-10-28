// =========================================================================
// FakeCureDialogService：程序①（CureStation）IDialogService 测试桩
// 用法与主程序 FakeDialogService 同：调后查 Calls 断 VM 调了谁。
// =========================================================================

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

    public void Shutdown() => Calls.Add("Shutdown");
}
