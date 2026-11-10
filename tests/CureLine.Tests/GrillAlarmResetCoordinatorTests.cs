// =========================================================================
// GrillAlarmResetCoordinatorTests：光栅报警人工复位弹窗编排（V0.0.69）剧本锁
//
// 干什么：纯 VM 直驱（喂报警清单数组断言事件/行集合），锁住用户拍板的两项
//   行为契约——① 弹窗清单热更（报警期间窗开着，新库位加入/解除即删行，不重开）；
//   ② 弹窗开关决策（首帧报警必弹 / 用户 ✕ 后挂着的旧报警不连环重弹、
//   出现新库位才再弹 / 全解除收窗并清表态 / 切回自动收窗且清空）。
//   触发即同步事件（VM 无线程封送需求——Refresh 由宿主 UI 线程/测试线程调）。
// 怎么扩：弹窗决策细节变化改 Refresh 的 _shown/_dismissed 小账本后，
//   按时序在这里补剧本 t 序号用例。
// =========================================================================

using CureLine.Core.Curing;
using CureStation.ViewModels;

namespace CureLine.Tests;

public class GrillAlarmResetCoordinatorTests
{
    /// <summary>固定人工档（弹窗只属人工模式）。</summary>
    private static GrillAlarmResetCoordinator MakeManual() =>
        new(() => GrillAlarmResetMode.Manual);

    private static GrillAlarmResetCoordinator MakeAuto() =>
        new(() => GrillAlarmResetMode.Auto);

    // ---------- 自动档：不弹窗不清单（现行为零影响） ----------

    [Fact]
    public void AutoMode_FeedAlarms_DoesNotShowAndKeepsEmpty()
    {
        var c = MakeAuto();
        c.ShowRequested += () => Assert.Fail("自动档不该请求弹窗");

        c.Refresh([(1, 1), (2, 2)]);
        Assert.Empty(c.Items);
        Assert.False(c.HasItems);
        Assert.Equal("暂无正在报警的库位", c.AlarmCountText);
    }

    // ---------- 人工档：首帧弹窗 + 清单 ----------

    [Fact]
    public void ManualMode_FirstAlarm_ShowsAndLists()
    {
        var c = MakeManual();
        int shows = 0;
        c.ShowRequested += () => shows++;

        c.Refresh([(1, 1), (2, 2)]);

        Assert.Equal(1, shows);
        Assert.Equal(2, c.Items.Count);
        Assert.True(c.HasItems);
        Assert.Equal("2 个库位正在报警", c.AlarmCountText);
        // 行文案=库位编码（与主窗卡片标题同源）+ 架层号，操作员一眼对上现场
        var first = c.Items.Single(i => i.Rack == 1 && i.Level == 1);
        Assert.Contains("架1-层1", first.Caption);
        Assert.Equal(
            $"{SlotLocationCode.EncodeInRack(1, 1)} · 架1-层1",
            first.Caption);
    }

    [Fact]
    public void ManualMode_HotUpdateItemsWhileShown_NoRepop()
    {
        // 用户拍板重点：窗开着（已 Show）报警期间清单热更——新库位进来加行，
        // 不再连发 Show（窗已开着），解除即删行。
        var c = MakeManual();
        int shows = 0;
        c.ShowRequested += () => shows++;
        c.Refresh([(1, 1)]);
        Assert.Equal(1, shows);
        Assert.Single(c.Items);

        c.Refresh([(1, 1), (3, 2)]);   // 窗开着：新报警热进来（3 行不重复弹）
        Assert.Equal(1, shows);        // 无第二次 Show
        Assert.Equal(2, c.Items.Count);
        Assert.Contains(c.Items, i => i.Rack == 3 && i.Level == 2);

        c.Refresh([(3, 2)]);           // 1-1 解除（宿主复位完成回喂）——行即走
        Assert.Single(c.Items);
        Assert.DoesNotContain(c.Items, i => i.Rack == 1);

        c.Refresh([]);                 // 全解除：收窗 + 清单清空 + 表态作废
        Assert.Empty(c.Items);
        Assert.Equal(1, shows);        // 计数仍=首帧那一次（全解除只是收窗不弹）
        Assert.True(c.IsEmptyPromptVisible);

        // 全解除后 Close 收窗语义在下一用例专锁；这里只验清单自洽
    }

    [Fact]
    public void ManualMode_AllCleared_WindowClosesAndStateForgiven()
    {
        var c = MakeManual();
        int closes = 0;
        c.CloseRequested += () => closes++;
        c.Refresh([(1, 1)]);

        c.Refresh([]);
        Assert.Equal(1, closes);
        Assert.Empty(c.Items);
        Assert.True(c.IsEmptyPromptVisible); // 空态提示可见

        // 全解除后"用户表态"作废：下次报警重新弹第一枪
        int shows = 0;
        c.ShowRequested += () => shows++;
        c.Refresh([(1, 1)]);
        Assert.Equal(1, shows);
    }

    [Fact]
    public void ManualMode_UserClosed_OldAlarmStaysSilent_NewAlarmPopsAgain()
    {
        var c = MakeManual();
        int shows = 0;
        c.ShowRequested += () => shows++;
        c.Refresh([(1, 1)]);
        Assert.Equal(1, shows);

        c.NotifyWindowClosed();          // 用户 ✕ 关窗（记表态）
        c.Refresh([(1, 1)]);             // 挂着的旧报警：不连环重弹
        Assert.Equal(1, shows);
        Assert.Single(c.Items);          // 清单仍热更维护着（行在）
        Assert.True(c.Items[0].Rack == 1 && c.Items[0].Level == 1);

        c.Refresh([(1, 1), (4, 3)]);     // 新库位报警：再弹一枪带名单回眼前
        Assert.Equal(2, shows);
        Assert.Equal(2, c.Items.Count);
    }

    [Fact]
    public void ManualMode_SwitchToAuto_ClosesAndClears_SwitchBackReplays()
    {
        var mode = new BoxedMode(GrillAlarmResetMode.Manual);
        var c = new GrillAlarmResetCoordinator(() => mode.Value);
        int shows = 0, closes = 0;
        c.ShowRequested += () => shows++;
        c.CloseRequested += () => closes++;

        c.Refresh([(1, 1)]);
        Assert.Equal(1, shows);

        mode.Value = GrillAlarmResetMode.Auto;  // 策略窗切回自动（中枢广播后宿主照喂）
        c.Refresh([]);
        Assert.Equal(1, closes);
        Assert.Empty(c.Items);

        mode.Value = GrillAlarmResetMode.Manual; // 再切回人工：挂新报警重新弹
        c.Refresh([(2, 1)]);
        Assert.Equal(2, shows);
    }

    // ---------- 复位命令派发 ----------

    [Fact]
    public void ItemResetCommand_RaisesRequestedWithRackLevel()
    {
        var c = MakeManual();
        var requested = new List<(int Rack, int Level)>();
        c.ResetRequested += (r, l) => requested.Add((r, l));
        c.Refresh([(2, 3)]);

        c.Items[0].Reset.Execute(null);
        Assert.Equal([(2, 3)], requested);
    }

    [Fact]
    public void ResetAllCommand_EnabledOnlyWithItems()
    {
        var c = MakeManual();
        Assert.False(c.ResetAllCommand.CanExecute(null)); // 无报警（自动档清场后）
        int allRequests = 0;
        c.ResetAllRequested += () => allRequests++;

        c.Refresh([(1, 1), (2, 1)]);
        Assert.True(c.ResetAllCommand.CanExecute(null));  // 有报警可用
        c.ResetAllCommand.Execute(null);
        Assert.Equal(1, allRequests);
    }

    /// <summary>可变模式源（模式热切用例的小盒子——照状态机 Boxed<T> 先例）。</summary>
    private sealed class BoxedMode(GrillAlarmResetMode value)
    {
        public GrillAlarmResetMode Value { get; set; } = value;
    }

    // ---------- 三步现场确认流程（V0.0.97） ----------
    // 清单 → 【有产品取出】→ 补扫取出的产品 SN → 计时处置（继续/终止）→ 回清单。

    [Fact(DisplayName = "报警确认：点【有产品取出】进入补扫步骤并抛确认事件")]
    public void TakeOut_EntersScanOutStep()
    {
        var c = MakeManual();
        var confirmed = new List<(int Rack, int Level)>();
        c.TakeOutConfirmed += (r, l) => confirmed.Add((r, l));
        c.Refresh([(2, 3)]);

        c.Items[0].TakeOut.Execute(null);

        Assert.Equal(GrillAlarmDialogStep.ScanOut, c.Step);
        Assert.Equal([(2, 3)], confirmed);
        Assert.Equal(2, c.CurrentRack);
        Assert.Equal(3, c.CurrentLevel);
        Assert.Equal("B04", c.CurrentLocationCode);   // 架2层3 → 客户编码 B04
        Assert.Empty(c.OutScans);
    }

    [Fact(DisplayName = "报警确认：补扫只登记不写库（未点确认不入库）")]
    public void OutScan_RecordsOnlyInList_NoDbWrite()
    {
        var c = MakeManual();
        c.Refresh([(1, 1)]);
        c.Items[0].TakeOut.Execute(null);

        // V0.0.99 用户现场实测纠正：扫了码没点确认按钮就不许入数据库——
        // 补扫阶段只进弹窗清单，不抛任何写库事件。
        Assert.True(c.TryHandleOutScan("SN-1"));
        Assert.True(c.TryHandleOutScan("SN-2"));
        Assert.Equal(2, c.OutScans.Count);
        Assert.Contains("已扫 2 件", c.ScanOutHintText);
    }

    [Fact(DisplayName = "报警确认：补扫通道只认产品码（库位码忽略/重复SN不重复记）")]
    public void OutScan_IgnoresLocationCodeAndDuplicates()
    {
        var c = MakeManual();
        c.Refresh([(1, 1)]);
        c.Items[0].TakeOut.Execute(null);

        Assert.True(c.TryHandleOutScan("A01"));      // 库位码：被通道消费但不算产品
        Assert.True(c.TryHandleOutScan("SN-1"));
        Assert.True(c.TryHandleOutScan("SN-1"));      // 重复：不算新件
        Assert.Single(c.OutScans);
        Assert.Equal("SN-1", c.OutScans[0].Sn);
    }

    [Fact(DisplayName = "报警确认：非补扫阶段不消费扫码（普通绑定通道不受影响）")]
    public void OutScan_NotInScanOutStep_ReturnsFalse()
    {
        var c = MakeManual();
        c.Refresh([(1, 1)]);
        Assert.False(c.TryHandleOutScan("SN-1"));    // 步骤一：交回普通通道
        Assert.Equal(GrillAlarmDialogStep.List, c.Step);
    }

    [Fact(DisplayName = "报警确认：【扫完了】进入计时处置，【继续计时】派发不终止")]
    public void TimingDecision_KeepTiming_KeepsCuring()
    {
        var c = MakeManual();
        var decisions = new List<(int Rack, int Level, IReadOnlyList<string> Sns, bool Terminate)>();
        c.TakeOutDecisionConfirmed += (r, l, sns, t) => decisions.Add((r, l, sns, t));
        c.Refresh([(1, 1)]);
        c.Items[0].TakeOut.Execute(null);
        c.TryHandleOutScan("SN-1");

        c.CommandFinishScan.Execute(null);
        Assert.Equal(GrillAlarmDialogStep.TimingDecision, c.Step);

        c.CommandKeepTiming.Execute(null);
        // 点确认按钮这一刻才把补扫清单交出去入库
        Assert.Single(decisions);
        Assert.Equal((1, 1), (decisions[0].Rack, decisions[0].Level));
        Assert.Equal(["SN-1"], decisions[0].Sns.ToArray());
        Assert.False(decisions[0].Terminate);
        Assert.Equal(GrillAlarmDialogStep.List, c.Step);   // 处置完回清单
        Assert.Equal(0, c.CurrentRack);
    }

    [Fact(DisplayName = "报警确认：【终止计时】派发终止（整个库位产品停表）")]
    public void TimingDecision_StopTiming_DispatchesTerminate()
    {
        var c = MakeManual();
        var decisions = new List<(int Rack, int Level, IReadOnlyList<string> Sns, bool Terminate)>();
        c.TakeOutDecisionConfirmed += (r, l, sns, t) => decisions.Add((r, l, sns, t));
        c.Refresh([(4, 2)]);
        c.Items[0].TakeOut.Execute(null);
        c.CommandFinishScan.Execute(null);

        c.CommandStopTiming.Execute(null);
        Assert.Single(decisions);
        Assert.Equal(4, decisions[0].Rack);
        Assert.Equal(2, decisions[0].Level);
        Assert.True(decisions[0].Terminate);
        Assert.Empty(decisions[0].Sns);
        Assert.Equal(GrillAlarmDialogStep.List, c.Step);
    }

    [Fact(DisplayName = "报警确认：【返回】放弃补扫回清单（不入库、不派发任何处置）")]
    public void BackToList_AbandonsScanWithoutDecision()
    {
        var c = MakeManual();
        var decisions = new List<(int Rack, int Level, IReadOnlyList<string> Sns, bool Terminate)>();
        c.TakeOutDecisionConfirmed += (r, l, sns, t) => decisions.Add((r, l, sns, t));
        c.Refresh([(1, 1)]);
        c.Items[0].TakeOut.Execute(null);
        c.TryHandleOutScan("SN-1");

        c.CommandBackToList.Execute(null);
        Assert.Equal(GrillAlarmDialogStep.List, c.Step);
        Assert.Empty(decisions);      // 放弃 = 库里不留任何痕迹
        Assert.Empty(c.OutScans);
    }
}
