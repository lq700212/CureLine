// =========================================================================
// CuringDisplayTests：方案 D 展示层纯逻辑（核心数值不碰 UI 就能锁口径）
// ① SlotStatistics 五态计数 / ⚡剩余最短；② RackSlotViewModel.Kind 映射
//   （状态机精确值 / hasTable=false 未配置）；③ 工单带纯映射 SummarizeWorkOrder。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureStation.ViewModels;
using Xunit;

namespace CureLine.Tests;

public class CuringDisplayTests
{
    // ---------- SlotStatistics.Count ----------

    [Fact(DisplayName = "Count：空集合全 0")]
    public void Count_Empty_AllZero()
    {
        var (idle, curing, gate, done, alarm) = SlotStatistics.Count(Array.Empty<SlotStatItem>());
        Assert.Equal((0, 0, 0, 0, 0), (idle, curing, gate, done, alarm));
    }

    [Fact(DisplayName = "Count：五态各计其数（未知键不计）")]
    public void Count_FiveKinds_UnknownIgnored()
    {
        var items = new[]
        {
            new SlotStatItem("Idle", 0, "A01"),
            new SlotStatItem("Idle", 0, "A02"),
            new SlotStatItem("Curing", 30, "A03"),
            new SlotStatItem("Gate", 0, "A04"),
            new SlotStatItem("Done", 0, "A05"),
            new SlotStatItem("Alarm", 0, "A06"),
            new SlotStatItem("NotConfigured", 0, "A07"), // 不计入五态
        };
        var (idle, curing, gate, done, alarm) = SlotStatistics.Count(items);
        Assert.Equal((2, 1, 1, 1, 1), (idle, curing, gate, done, alarm));
    }

    [Fact(DisplayName = "NextClose：无计时提示文本")]
    public void NextClose_NoCuring()
    {
        var items = new[] { new SlotStatItem("Idle", 0, "A01"), new SlotStatItem("Done", 0, "A02") };
        Assert.Equal("暂无计时", SlotStatistics.NextClose(items));
    }

    [Fact(DisplayName = "NextClose：取剩余最短（纯文本带⚡与库位码）")]
    public void NextClose_PicksMinimum()
    {
        var items = new[]
        {
            new SlotStatItem("Curing", 45, "A03"),
            new SlotStatItem("Curing", 12, "B07"), // 最小
            new SlotStatItem("Curing", 88, "C02"),
            new SlotStatItem("Idle", 0, "C03"),
        };
        Assert.Equal("⚡B07·剩12分", SlotStatistics.NextClose(items));
    }

    // ---------- RackSlotViewModel.Kind 映射 ----------

    [Fact(DisplayName = "Kind：状态机计时中带精确剩余与进度")]
    public void Kind_Curing_CarriesRemainAndProgress()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 45));
        Assert.Equal("Curing", slot.Kind);
        Assert.True(slot.HasDetail);
        Assert.Equal(45, slot.RemainMin);
        Assert.Equal(120, slot.TotalMin);
        Assert.Equal(63, slot.ProgressPct); // (120-45)/120=62.5% → 63
    }

    [Fact(DisplayName = "Kind：放行/待门控/未配置")]
    public void Kind_DoneGateNotConfigured()
    {
        var done = new RackSlotViewModel { Rack = 1, Level = 1 };
        done.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Done, 60, 0));
        Assert.Equal("Done", done.Kind);
        Assert.False(done.HasDetail);

        var gate = new RackSlotViewModel { Rack = 1, Level = 1 };
        gate.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.WaitingGate, 60, 60));
        Assert.Equal("Gate", gate.Kind);

        var nc = new RackSlotViewModel { Rack = 1, Level = 1 };
        nc.Refresh(PointTableSnapshot.Empty, false, null);
        Assert.Equal("NotConfigured", nc.Kind);
        Assert.Equal("未配置", nc.SlotStateText);
    }

    [Fact(DisplayName = "Kind：状态机空闲快照全空 → Idle")]
    public void Kind_IdleWhenAllQuiet()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Idle, 0, 0));
        Assert.Equal("Idle", slot.Kind);
        Assert.Equal(0, slot.ProgressPct);
        Assert.Equal("空闲", slot.SlotStateText);
    }

    // ---------- 工单带纯映射 ----------

    [Fact(DisplayName = "SummarizeWorkOrder：空单号→—、产出比与百分比")]
    public void SummarizeWorkOrder_MapsBothSides()
    {
        var book = new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "", ProducedQty = 12, PresetQty = 50 },   // 24%
            Left = new WorkOrderSlot { RcNo = "RC-L9", ProducedQty = 99, PresetQty = 40 }, // 超产钳 100%
        };
        var (rcR, qtyR, pctR, rcL, qtyL, pctL) = MainViewModel.SummarizeWorkOrder(book);
        Assert.Equal("—", rcR);
        Assert.Equal("12/50", qtyR);
        Assert.Equal(24, pctR);
        Assert.Equal("RC-L9", rcL);
        Assert.Equal("99/40", qtyL);
        Assert.Equal(100, pctL);
    }

    [Fact(DisplayName = "SummarizeWorkOrder：预设为 0 → 0% (免除零)")]
    public void SummarizeWorkOrder_ZeroPreset()
    {
        var book = new WorkOrderBook(); // 全空工单
        var (rcR, qtyR, pctR, rcL, qtyL, pctL) = MainViewModel.SummarizeWorkOrder(book);
        Assert.Equal(("—", "0/0", 0, "—", "0/0", 0), (rcR, qtyR, pctR, rcL, qtyL, pctL));
    }
}
