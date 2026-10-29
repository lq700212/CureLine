// =========================================================================
// WorkOrderStoreTests：工单信息存取（往返/自愈）——TestScope 隔离临时目录
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class WorkOrderStoreTests
{
    [Fact]
    public void Save_Load_RoundTrip()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var book = new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "R-ROUTE-001", PresetQty = 100, ProducedQty = 37 },
            Left = new WorkOrderSlot { RcNo = "L-ROUTE-002", PresetQty = 80, ProducedQty = 12 },
            EmpNo = "EMP001",
            BcNo = "BC-2026-1028-0001",
            PtNo1 = "PT-A01",
            PtNo2 = "PT-B02",
            UploadEnabled = true,
        };
        WorkOrderStore.Save(book);

        var loaded = WorkOrderStore.Load()!;
        Assert.Equal("R-ROUTE-001", loaded.Right.RcNo);
        Assert.Equal(100, loaded.Right.PresetQty);
        Assert.Equal(37, loaded.Right.ProducedQty);
        Assert.Equal("L-ROUTE-002", loaded.Left.RcNo);
        Assert.Equal(80, loaded.Left.PresetQty);
        Assert.Equal(12, loaded.Left.ProducedQty);
        Assert.Equal("EMP001", loaded.EmpNo);
        Assert.Equal("BC-2026-1028-0001", loaded.BcNo);
        Assert.Equal("PT-A01", loaded.PtNo1);
        Assert.Equal("PT-B02", loaded.PtNo2);
        Assert.True(loaded.UploadEnabled);
    }

    [Fact]
    public void LoadMissing_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var reasons = new List<string>();
        Assert.Null(WorkOrderStore.Load(reasons));
        Assert.NotEmpty(reasons);
    }

    [Fact]
    public void LoadCorrupted_ReturnsNullWithReason()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(WorkOrderStore.DefaultPath, "{ 不是 JSON");
        var reasons = new List<string>();
        Assert.Null(WorkOrderStore.Load(reasons));
        Assert.Contains(reasons, r => r.Contains("损坏"));
    }
}
