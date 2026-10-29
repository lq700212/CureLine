// =========================================================================
// SlotStatistics：主窗顶部统计条聚合（方案 D · 紧凑网格+下部信息带，2026-10-29）
//
// 干什么：把 30/60 张仓位卡的 Kind（Idle/Gate/Curing/Done/Alarm）与剩余分钟
//   聚合成顶部统计条需要的东西——五态计数 + "⚡剩余最短"提示（计时中最早
//   快到点的库位，一眼锁定下一个要取件的口）。
// 为什么抽纯静态函数：聚合逻辑是纯映射（不吃 UI/总线/状态机），测试不用起
//   总线/定时器就能锁口径（空集合 / 全计时 / 最小值比选），见 SlotStatisticsTests。
// 怎么改：新增统计列（如"未配置数"）在 Kind switch 加 case + 顶部条 XAML
//   加 pill；Kind 字段与 RackSlotViewModel.Kind 同名（改动两边同步）。
// =========================================================================

namespace CureLine.Core.Curing;

/// <summary>
/// 统计输入一条：Kind=卡片状态键（Idle/Gate/Curing/Done/Alarm，
/// 与 RackSlotViewModel.Kind 同名同口径）、RemainMin=剩余分钟（非计时=0）、
/// Code=客户库位编码（NextClose 显示用，如 "A03"）。
/// </summary>
public sealed record SlotStatItem(string Kind, int RemainMin, string Code);

public static class SlotStatistics
{
    /// <summary>
    /// 五态计数（Kind 仅认五态键，NotConfigured/未知值不计——未配置态只在
    /// 点表加载瞬间存在，统计条不展示它）。
    /// </summary>
    public static (int Idle, int Curing, int Gate, int Done, int Alarm) Count(
        IEnumerable<SlotStatItem> items)
    {
        int idle = 0, curing = 0, gate = 0, done = 0, alarm = 0;
        foreach (var it in items)
        {
            switch (it.Kind)
            {
                case "Idle": idle++; break;
                case "Curing": curing++; break;
                case "Gate": gate++; break;
                case "Done": done++; break;
                case "Alarm": alarm++; break;
            }
        }
        return (idle, curing, gate, done, alarm);
    }

    /// <summary>
    /// "⚡剩余最短"提示：计时中最小 RemainMin 的仓位（RemainMin>0 才算——
    /// 剩 0 分即该放行了属 Done 由状态机推进）。无计时回"暂无计时"。
    /// </summary>
    public static string NextClose(IEnumerable<SlotStatItem> items, string prefix = "⚡")
    {
        SlotStatItem? best = null;
        foreach (var it in items)
        {
            if (it.Kind != "Curing" || it.RemainMin <= 0)
                continue;
            if (best == null || it.RemainMin < best.RemainMin)
                best = it;
        }
        return best == null ? "暂无计时" : $"{prefix}{best.Code}·剩{best.RemainMin}分";
    }
}
