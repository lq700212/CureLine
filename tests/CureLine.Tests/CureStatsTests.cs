// =========================================================================
// 统计口径测试：CureRecordStore.ComputeStats（老项目 UpdateStats 内核）
//
// 锁定的口径（改其中任何一条即与老项目行为分叉，必须同步更新注释）：
// ① 完成 = now >= 进入+固化秒；② L/R 按第4字符；③ 明日=Day相等且Hour<=8；
// ④ 空产品跳过不计数；⑤ 短 ID 归 R（老项目会抛，新项目归 R，差异已声明）。
// =========================================================================

using CureLine.Core.Data;

namespace CureLine.Tests;

public class CureStatsTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 10, 0, 0);

    [Fact(DisplayName = "完成与静置分区计数")]
    public void Stats_Done_Resting_Split()
    {
        // 固化 100s：8:00 进入的已完成（L/R 各1），9:59 进入的静置中。
        var rows = new[]
        {
            ("2026-09-21 08:00:00", "ABCL001⚫ABCR002"),
            ("2026-09-21 09:59:00", "ABCL003"),
        };
        var s = CureRecordStore.ComputeStats(rows, 100, Now);
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.DoneL);
        Assert.Equal(1, s.DoneR);
        Assert.Equal(1, s.RestingL);
        Assert.Equal(0, s.RestingR);
    }

    [Fact(DisplayName = "明日可完成只计次日8点前")]
    public void Stats_Tomorrow_Before8am()
    {
        // 固化到次日 7:30 → 计入明日；次日 9:00 → 不计。
        var rows = new[]
        {
            ("2026-09-21 10:00:00", "ABCL001"), // +21.5h=次日7:30
            ("2026-09-21 10:00:00", "ABCR002"), // +23h=次日9:00
        };
        var s1 = CureRecordStore.ComputeStats(new[] { rows[0] }, 21 * 3600 + 1800, Now);
        Assert.Equal(1, s1.TomorrowL);
        var s2 = CureRecordStore.ComputeStats(new[] { rows[1] }, 23 * 3600, Now);
        Assert.Equal(0, s2.TomorrowR);
    }

    [Fact(DisplayName = "空产品与坏时间跳过")]
    public void Stats_Skips_Empty_BadTime()
    {
        var rows = new[]
        {
            ("not-a-time", "ABCL001"),
            ("2026-09-21 08:00:00", "⚫⚫"),
            ("2026-09-21 08:00:00", "AB"), // 短 ID 归 R
        };
        var s = CureRecordStore.ComputeStats(rows, 100, Now);
        Assert.Equal(1, s.Total);
        Assert.Equal(1, s.DoneR);
    }

    [Fact(DisplayName = "L分区只看第4字符")]
    public void Stats_Left_Is4thChar()
    {
        var rows = new[] { ("2026-09-21 08:00:00", "XXXL1⚫XXXR1⚫XXXX1") };
        var s = CureRecordStore.ComputeStats(rows, 100, Now);
        Assert.Equal(1, s.DoneL);
        Assert.Equal(2, s.DoneR);
    }
}
