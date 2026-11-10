// =========================================================================
// CuringDisplayTests：展示层纯逻辑（核心数值不碰 UI 就能锁口径）
// ① SlotStatistics 五态计数 / ⚡剩余最短；② RackSlotViewModel.Kind 映射
//   （状态机精确值 / hasTable=false 未配置）；③ 工单带纯映射 SummarizeWorkOrder；
// ④ G 卡口径卡尾/层名（TrayFoot 三态 + LayerName）；⑤ 工单数拆分转换器。
// =========================================================================

using System.Globalization;
using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureStation;
using CureStation.ViewModels;
using CureLine.Tests.Mocks;
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

    [Fact(DisplayName = "NextClose：无计时 → null（文案在主 VM 拼）")]
    public void NextClose_NoCuring()
    {
        var items = new[] { new SlotStatItem("Idle", 0, "A01"), new SlotStatItem("Done", 0, "A02") };
        Assert.Null(SlotStatistics.NextClose(items));
    }

    [Fact(DisplayName = "NextClose：取剩余最短仓位（纯数据，文本随显示单位在 UI 拼）")]
    public void NextClose_PicksMinimum()
    {
        var items = new[]
        {
            new SlotStatItem("Curing", 45, "A03"),
            new SlotStatItem("Curing", 12, "B07"), // 最小
            new SlotStatItem("Curing", 88, "C02"),
            new SlotStatItem("Idle", 0, "C03"),
        };
        var best = SlotStatistics.NextClose(items);
        Assert.NotNull(best);
        Assert.Equal("B07", best.Code);
        Assert.Equal(12, best.RemainMin);
    }

    // ---------- DurationUnit 显示单位（V0.0.56 策略窗切时/分主窗同步） ----------

    /// <summary>临时目录下补建 config/（WritePrivateProfileString 不自动建目录）。</summary>
    private static void EnsureConfigDir() =>
        Directory.CreateDirectory(AppPaths.ConfigDir);

    [Fact(DisplayName = "DurationUnit：分钟口径=旧显示（N分整串；大数字纯数字+尾'分'）")]
    public void DurationUnit_Minutes_KeepsLegacyFormat()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        Assert.Equal("45分", DurationUnit.Text(45));
        Assert.Equal("120分", DurationUnit.Text(120));
        Assert.Equal(("45", "分", "", ""), DurationUnit.SplitBig(45));   // 数字26号+尾'分'11号
        Assert.Equal(("120", "分", "", ""), DurationUnit.SplitBig(120));
    }

    [Fact(DisplayName = "DurationUnit：小时口径=智能省略零段 + 大数字四段交替（数字26/单位字11）")]
    public void DurationUnit_Hours_SmartOmitZeroSegment()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Hours);
        Assert.Equal("2小时", DurationUnit.Text(120));      // 整小时省"0分"
        Assert.Equal("1小时28分", DurationUnit.Text(88));   // 88 分 = 1 时 28 分
        Assert.Equal("47分", DurationUnit.Text(47));        // 不足 1 小时省"0小时"
        Assert.Equal("0分", DurationUnit.Text(0));          // 不变量兜底
        // 大数字四段（数字/单位字交替；空段=空串，显隐由第二组非空推导）
        Assert.Equal(("1", "小时", "28", "分"), DurationUnit.SplitBig(88)); // 1|小时|28|分
        Assert.Equal(("2", "小时", "", ""), DurationUnit.SplitBig(120));   // 整小时无第二段
        Assert.Equal(("47", "分", "", ""), DurationUnit.SplitBig(47));     // 不足 1 小时=两段
    }

    [Fact(DisplayName = "DurationUnit.LogText：日志口径（分钟=“X 分钟”老字样；小时=同 Text 智能省略）")]
    public void DurationUnit_LogText_MinutesLegacy_HoursSmart()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        Assert.Equal("120 分钟", DurationUnit.LogText(120));  // 历史日志观感零变化（非"120分"）
        Assert.Equal("47 分钟", DurationUnit.LogText(47));

        DurationUnit.RecallForTest(PolicyTimeUnit.Hours);
        Assert.Equal("2小时", DurationUnit.LogText(120));     // 整小时省"0分"
        Assert.Equal("24小时", DurationUnit.LogText(1440));   // 现场日记"1440 分钟"→"24小时"
        Assert.Equal("1小时28分", DurationUnit.LogText(88));
        Assert.Equal("47分", DurationUnit.LogText(47));       // 不足 1 小时省"0小时"

        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);   // 收尾还原（TestScope Dispose 双保险）
    }

    [Fact(DisplayName = "DurationUnit：缺键/非法/旧 Seconds 一律回缺省 Hours（2026-11-09 拍板：默认单位=时）")]
    public void DurationUnit_ReadIniDefault_MissingAndInvalidFallsBackToHours()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        // 缺键（出厂态）→ 缺省 Hours：开箱即小时口径
        DurationUnit.LoadFromIni();
        Assert.Equal(PolicyTimeUnit.Hours, DurationUnit.Current);

        // 非法值 / 旧版存量 "Seconds"（档已删）→ 同回缺省 Hours（自愈同源）
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Bogus");
        DurationUnit.LoadFromIni();
        Assert.Equal(PolicyTimeUnit.Hours, DurationUnit.Current);

        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Seconds");
        DurationUnit.LoadFromIni();
        Assert.Equal(PolicyTimeUnit.Hours, DurationUnit.Current);

        // 显式 Minutes 在盘中不受缺省影响（读盘为准）
        IniFile.Write(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", "Minutes");
        DurationUnit.LoadFromIni();
        Assert.Equal(PolicyTimeUnit.Minutes, DurationUnit.Current);
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);   // 收尾还原（TestScope Dispose 双保险）
    }

    [Fact(DisplayName = "DurationUnit：Change 落盘 ini 记忆 + 触发广播")]
    public void DurationUnit_Change_PersistsAndRaises()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        int raised = 0;
        void OnChanged() => raised++;
        DurationUnit.Changed += OnChanged;
        try
        {
            DurationUnit.Change(PolicyTimeUnit.Hours);
            Assert.Equal(PolicyTimeUnit.Hours, DurationUnit.Current);
            Assert.Equal("Hours", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", ""));
            Assert.Equal(1, raised);

            DurationUnit.Change(PolicyTimeUnit.Hours);   // 同值不触发（幂等）
            Assert.Equal(1, raised);
        }
        finally
        {
            DurationUnit.Changed -= OnChanged;
            DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        }
    }

    // ---------- 卡片时长文本（随显示单位） ----------

    [Fact(DisplayName = "卡片文本：分钟口径保持旧显示（'47'+尾'分'两段、副文'120 分'）")]
    public void CardText_Minutes_Legacy()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 47));

        Assert.Equal("47", slot.RemainNum1);
        Assert.Equal("分", slot.RemainUnit1);
        Assert.Equal("", slot.RemainNum2);      // 无第二段（26/11 两段即旧版视觉）
        Assert.False(slot.RemainHasTail);
        Assert.Equal("120 分", slot.TotalDisplay);
        Assert.Equal("计时中 剩47分", slot.SlotStateText);
    }

    [Fact(DisplayName = "卡片文本：小时口径四段交替+第二段显隐+状态词'剩1小时15分'（只换字面）")]
    public void CardText_Hours_SyncsOnUnitNameOnly()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 75));

        // 切小时（模拟策略窗广播）→ 立即重刷：数值不动、字面全换
        Assert.Equal(120, slot.TotalMin);   // 前置锚点
        DurationUnit.RecallForTest(PolicyTimeUnit.Hours);
        slot.RefreshUnitText();

        Assert.Equal("1", slot.RemainNum1);        // 75 分 = 1 时 15 分
        Assert.Equal("小时", slot.RemainUnit1);    // 单位字 11 号（与"分"后缀同字号）
        Assert.Equal("15", slot.RemainNum2);
        Assert.Equal("分", slot.RemainUnit2);
        Assert.True(slot.RemainHasTail);           // 第二段组（"15分"）显示
        Assert.Equal("2小时", slot.TotalDisplay);
        Assert.Equal("计时中 剩1小时15分", slot.SlotStateText);   // 缓存快照重拼
        Assert.Equal(120, slot.TotalMin);   // 数值未动
        Assert.Equal(38, slot.ProgressPct); // 进度未动 ((120-75)/120=37.5% → 38 AwayFromZero)

        // 整小时形态：第二段组收起（"2小时"两段完事）
        slot.RefreshUnitText();   // 数据不变；借 SplitBig 直接验证整小时态
        Assert.Equal(("2", "小时", "", ""), DurationUnit.SplitBig(120));

        // 回分钟无损：退回数字+尾"分"两段
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        slot.RefreshUnitText();
        Assert.Equal("75", slot.RemainNum1);
        Assert.Equal("分", slot.RemainUnit1);
        Assert.Equal("", slot.RemainNum2);
        Assert.False(slot.RemainHasTail);
        Assert.Equal("120 分", slot.TotalDisplay);
    }

    [Fact(DisplayName = "主窗联动：策略窗 Change(时) → 卡片/统计条文本立即同步 + ini 落盘")]
    public void MainVm_DisplayUnitChange_ImmediatelyResyncesTexts()
    {
        using var scope = new TestScope();
        EnsureConfigDir();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        var fake = new FakeCureDialogService();
        using var vm = new MainViewModel(fake);

        // 手动点亮一张卡（生产链路是 500ms 拍走同方法 Refresh，此处直调同口径）
        var slot = vm.Slots.First(s => s.Rack == 1 && s.Level == 1);
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 75));
        Assert.Equal("75", slot.RemainNum1);   // 前置锚点：分钟口径（纯数字 26 号）

        // 策略窗唯一入口 Change=写 ini 记忆 + 广播 → 主窗订阅立即重刷（不等 500ms）
        DurationUnit.Change(PolicyTimeUnit.Hours);

        Assert.Equal("1", slot.RemainNum1);       // 75 分 = 1 时 15 分（四段交替）
        Assert.Equal("小时", slot.RemainUnit1);
        Assert.Equal("15", slot.RemainNum2);
        Assert.Equal("分", slot.RemainUnit2);
        Assert.True(slot.RemainHasTail);
        Assert.Contains("剩1小时15分", vm.NextCloseText);  // 统计条同口（⚡码·剩X）
        Assert.Equal("Hours", IniFile.ReadStr(AppPaths.ConfigIni, "setting", "PolicyTimeUnit", ""));

        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);  // 收尾还原（TestScope Dispose 双保险）
    }

    [Fact(DisplayName = "卡片文本：空闲计划预览（仅 Idle 有值、单位切换重拼、非空闲清空/策略未知='计划 —'）")]
    public void CardText_PlanText_IdleOnly()
    {
        using var scope = new TestScope();
        DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        try
        {
            var slot = new RackSlotViewModel { Rack = 1, Level = 1 };

            // 空闲拍：计划=激活策略三级解析值（1440 分，小时口径="24小时"）
            DurationUnit.RecallForTest(PolicyTimeUnit.Hours);
            slot.Refresh(PointTableSnapshot.Empty, true, null, 1440);
            Assert.Equal("Idle", slot.Kind);
            Assert.Equal("计划 24小时", slot.PlanText);

            // 单位切换：RefreshUnitText 一拍内重拼（缓存分钟数值，不等 500ms 拍）
            DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
            slot.RefreshUnitText();
            Assert.Equal("计划 1440分", slot.PlanText);

            // 非空闲拍（状态机 Curing 精确值接管）：计划预览清空（XAML 以 Kind=Idle 控显隐）
            slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 47), 1440);
            Assert.Equal("Curing", slot.Kind);
            Assert.Equal("", slot.PlanText);

            // 策略未知（planMinutes=0）：显示"计划 —"占位（防空文本行）
            var slot2 = new RackSlotViewModel { Rack = 1, Level = 2 };
            slot2.Refresh(PointTableSnapshot.Empty, true, null, 0);
            Assert.Equal("计划 —", slot2.PlanText);
        }
        finally
        {
            DurationUnit.RecallForTest(PolicyTimeUnit.Minutes);
        }
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
        Assert.Equal(63, slot.ProgressPct);  // (120-45)/120=62.5% → 63
        Assert.Equal(0.625, slot.ProgressRatio, 6); // 同源进度比（卡内进度条 ScaleTransform 直绑）
    }

    [Fact(DisplayName = "Kind：计时中光栅报警→Alarm 优先显示（V0.0.67 卡牌报警显出）")]
    public void Kind_CuringAlarmGrill_ShowsAlarmCarryingMinutes()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        // 报警快照：Phase 仍 Curing（客户口径"计时不变"），报警标志优先 → Alarm 口径
        // （红卡+脉冲+胶囊"占用"——此前 Phase=Curing 分支抢先、卡牌报警显示不出的根因修复）
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 45, AlarmGrill: true));
        Assert.Equal("Alarm", slot.Kind);
        Assert.Equal("占用", slot.SlotStateText);
        Assert.Equal(45, slot.RemainMin);   // 数值照传（不变量保持，复位后自愈回 Curing 视图）
        Assert.Equal(120, slot.TotalMin);
        Assert.Equal(63, slot.ProgressPct);

        // 复位后快照（AlarmGrill=false）→ 自愈回计时口径
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Curing, 120, 45));
        Assert.Equal("Curing", slot.Kind);
        Assert.Contains("计时中", slot.SlotStateText);
    }

    [Fact(DisplayName = "Kind：未计时（Total=0）→ Ratio/Pct 双零不变量")]
    public void Kind_ZeroTotal_RatioAndPctClampedZero()
    {
        var slot = new RackSlotViewModel { Rack = 3, Level = 2 };
        slot.Refresh(PointTableSnapshot.Empty, true, new SlotStatus(SlotPhase.Done, 60, 0));
        Assert.Equal(0, slot.TotalMin);
        Assert.Equal(0, slot.ProgressPct);
        Assert.Equal(0.0, slot.ProgressRatio, 6);
        Assert.False(slot.HasDetail);
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

    // ---------- 绑定件数展示（V0.0.97：卡片不再显示产品 SN，只显示已绑件数） ----------
    // 一个库位可能放十几个产品，卡片上显示单个 SN 根本显示不下——原
    // V0.0.84~V0.0.86 的动态行数区/摘要/小屏降档一整套随之退役（连带
    // SnZoneHeight / SnZoneCompensation / SnMaxRows / SnMaxRowsForCardHeight
    // 等属性整体删除）。现在只显示单行"已绑 N 件"，mid 补偿两档
    // （行可见=20 / 行隐藏=0），保证大数字锚点不漂。

    [Fact(DisplayName = "绑定件数：未绑定 → 文案'未绑定'（红字触发器吃 BindingMissing），行可见、补偿 20")]
    public void BindingCount_Missing_ShowsMissingText()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, null, 0, 0, bindingMissing: true);
        Assert.Equal("未绑定", slot.BindingText);
        Assert.True(slot.BindingMissing);
        Assert.True(slot.HasBindingText);
        Assert.Equal(new System.Windows.Thickness(0, 0, 0, 20), slot.BindingRowMargin);
    }

    [Fact(DisplayName = "绑定件数：空位（无绑定且非缺失）→ 文案空、整行收起、补偿清零")]
    public void BindingCount_Empty_CollapsedRow()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, null, 0, 0);
        Assert.Equal("", slot.BindingText);
        Assert.False(slot.HasBindingText);
        Assert.False(slot.BindingMissing);
        Assert.Equal(new System.Windows.Thickness(0), slot.BindingRowMargin);
    }

    [Fact(DisplayName = "绑定件数：已绑 N 件 → 文案'已绑 N 件'，行可见、补偿 20")]
    public void BindingCount_Bound_ShowsCount()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, null, 0, 3);
        Assert.Equal("已绑 3 件", slot.BindingText);
        Assert.Equal(3, slot.BoundProductCount);
        Assert.False(slot.BindingMissing);
        Assert.True(slot.HasBindingText);
        Assert.Equal(new System.Windows.Thickness(0, 0, 0, 20), slot.BindingRowMargin);
    }

    [Fact(DisplayName = "绑定件数：有绑定件时不再判'未绑定'（件数优先于缺失标志）")]
    public void BindingCount_BoundWinsOverMissing()
    {
        var slot = new RackSlotViewModel { Rack = 1, Level = 1 };
        slot.Refresh(PointTableSnapshot.Empty, true, null, 0, 2, bindingMissing: true);
        Assert.Equal("已绑 2 件", slot.BindingText);
        Assert.False(slot.BindingMissing);
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

    // ---------- G 卡卡尾/层名（2026-10-29 一比一复刻口径） ----------

    /// <summary>构造带托盘检测点的快照（其余点缺省 null，走"—"兜底）。</summary>
    private static PointTableSnapshot SnapWithTray(int rack, int level, bool tray) =>
        new(new Dictionary<IoPointKey, bool>
        {
            [new IoPointKey(true, IoPointRole.TrayDetect, rack, level)] = tray,
        }, DateTimeOffset.Now);

    [Fact(DisplayName = "TrayFoot/LayerName：托盘三态与层名映射")]
    public void CardFoot_TraysAndLayerNames()
    {
        var trayOn = new RackSlotViewModel { Rack = 1, Level = 1 };
        trayOn.Refresh(SnapWithTray(1, 1, true), true);
        Assert.Equal("◈ 托盘在", trayOn.TrayFoot);
        Assert.Equal("上层", trayOn.LayerName);

        var trayOff = new RackSlotViewModel { Rack = 1, Level = 1 };
        trayOff.Refresh(SnapWithTray(1, 1, false), true);
        Assert.Equal("无托盘", trayOff.TrayFoot);

        var noPoint = new RackSlotViewModel { Rack = 2, Level = 3 };
        noPoint.Refresh(PointTableSnapshot.Empty, true);
        Assert.Equal("—", noPoint.TrayFoot);
        Assert.Equal("下层", noPoint.LayerName);

        var mid = new RackSlotViewModel { Rack = 1, Level = 2 };
        mid.Refresh(PointTableSnapshot.Empty, true);
        Assert.Equal("中层", mid.LayerName);
    }

    [Fact(DisplayName = "WoQtySplitConverter：产出/总量拆分与兜底")]
    public void WoQtySplit_ReadsBothHalvesOrPassThrough()
    {
        var conv = new WoQtySplitConverter();
        // 参数 0=产出段 / 1=总量段；缺省参数同 0（XAML 只显式给）
        Assert.Equal("37", conv.Convert("37/120", typeof(object), "0", CultureInfo.CurrentCulture));
        Assert.Equal("120", conv.Convert("37/120", typeof(object), "1", CultureInfo.CurrentCulture));
        Assert.Equal("0", conv.Convert("0/0", typeof(object), "0", CultureInfo.CurrentCulture)); // 空工单兜底 "0/0"→"0"
        Assert.Equal("—", conv.Convert("—", typeof(object), "0", CultureInfo.CurrentCulture));   // 拆不动原样返回
    }

    [Fact(DisplayName = "SummarizeWorkOrder：预设为 0 → 0% (免除零)")]
    public void SummarizeWorkOrder_ZeroPreset()
    {
        var book = new WorkOrderBook(); // 全空工单
        var (rcR, qtyR, pctR, rcL, qtyL, pctL) = MainViewModel.SummarizeWorkOrder(book);
        Assert.Equal(("—", "0/0", 0, "—", "0/0", 0), (rcR, qtyR, pctR, rcL, qtyL, pctL));
    }
}
