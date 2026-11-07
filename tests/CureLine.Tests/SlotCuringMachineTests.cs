// =========================================================================
// SlotCuringMachineTests：仓位状态机全流程（虚拟时钟手动 Pump，确定性验证）
// 流程口径：托盘到位→（门控开则等光栅让空）→黄灯计时→到点绿灯→托盘离开灭灯。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class SlotCuringMachineTests
{
    // 虚拟时钟（xUnit 每用例新实例，字段即每用例独立）
    private DateTimeOffset _now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    private static IoPointKey Tray(int rack, int level) =>
        new(true, IoPointRole.TrayDetect, rack, level);
    private static IoPointKey GrillL(int rack, int level) =>
        new(true, IoPointRole.RackLightL, rack, level);
    private static IoPointKey GrillR(int rack, int level) =>
        new(true, IoPointRole.RackLightR, rack, level);
    private static IoPointKey Lamp(IoPointRole role, int rack, int level) =>
        new(false, role, rack, level);
    /// <summary>机台总监塔灯键（V0.0.65）：机台级恒 (DO, role, 0, 0)。</summary>
    private static IoPointKey Beacon(IoPointRole role) =>
        new(false, role, 0, 0);

    private (ScriptedPointBus Bus, SlotCuringMachine Machine, List<string> Logs) Make(
        CuringPolicy? policy = null, Func<int, string>? durationText = null,
        Func<GrillAlarmResetMode>? resetModeProvider = null)
    {
        var bus = new ScriptedPointBus();
        var logs = new List<string>();
        var machine = new SlotCuringMachine(bus, () => policy,
            clock: () => _now, log: logs.Add, durationText: durationText,
            resetModeProvider: resetModeProvider);
        // 宿主转发边沿（生产由 MainViewModel 做——machine 不直接订阅总线）
        bus.PointChanged += (_, e) => machine.OnPointChanged(e);
        return (bus, machine, logs);
    }

    private static bool Wrote(List<(IoPointKey Key, bool State)> writes,
        IoPointKey key, bool state) =>
        writes.Any(w => w.Key == key && w.State == state);

    // ---------- 基本流程 ----------

    [Fact]
    public void TrayOn_GateOff_StartsCuringWritesYellow()
    {
        var (bus, machine, logs) = Make();
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);

            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
            Assert.Contains(logs, m => m.Contains("架1层1 开始固化计时"));
        }
    }

    // ---------- 机台总监塔灯（V0.0.65 全机聚合；V0.0.67 口径：红 > 绿 > 黄——
    //            没有来料在固化=黄灯待料，有来料固化/等门控=绿灯运转中） ----------

    [Fact]
    public void MachineBeacon_CrossSlots_RedPriorityThenGreenThenYellow()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            // t0：1-1 开计时 → 塔灯绿（运转中；黄灭防残留；红/蜂不进“常态绿”态）
            bus.SetInput(Tray(1, 1), true);
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconYellow), false));
            Assert.DoesNotContain(bus.Writes,
                w => w.Key == Beacon(IoPointRole.BeaconRed) && w.State); // 红未亮（常态绿态不写红亮）

            // t1：2-3 再开计时 → 全机仍绿，塔灯零新写（聚合去重=稳态零网络写）
            bus.Writes.Clear();
            bus.SetInput(Tray(2, 3), true);
            Assert.DoesNotContain(bus.Writes,
                w => !w.Key.IsInput && IoPointRoleInfo.IsMachineLevel(w.Key.Role));

            // t2：1-1 光栅被挡 → 报警压过一切：塔灯红 + 蜂鸣长鸣（绿让位红）
            bus.Writes.Clear();
            bus.SetInput(GrillL(1, 1), true);
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconRed), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), false));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), true));

            // t3：光栅让空复位（2-3 仍在计时）→ 塔灯回绿：红灭/蜂停/绿复亮
            bus.Writes.Clear();
            bus.SetInput(GrillL(1, 1), false);
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconRed), false));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), false));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true));

            // t4：两仓都到点放行（Done 不算在固化）→ 全机回黄=待料（用户口径，Pump 尾聚合兜住）
            bus.Writes.Clear();
            _now += TimeSpan.FromMinutes(60);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(2, 3)!.Phase);
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconYellow), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), false));

            // t5：逐仓取件回空闲 → 塔灯黄保持（聚合去重零写）
            bus.Writes.Clear();
            bus.SetInput(Tray(1, 1), false);
            bus.SetInput(Tray(2, 3), false);
            Assert.DoesNotContain(bus.Writes,
                w => !w.Key.IsInput && IoPointRoleInfo.IsMachineLevel(w.Key.Role));

            // t6：稳态巡回继续零写（Pump 尾塔灯聚合恒幂等）
            machine.Pump(_now.AddMinutes(1));
            Assert.DoesNotContain(bus.Writes,
                w => !w.Key.IsInput && IoPointRoleInfo.IsMachineLevel(w.Key.Role));
        }
    }

    [Fact]
    public void MachineBeacon_WaitingGateCountsAsBusy_ThenStarts()
    {
        var policy = new CuringPolicy { Name = "T", GlobalMinutes = 60, GrillGate = true };
        var (bus, machine, _) = Make(policy);
        using (machine)
        {
            // 门控开 + 人手在架（光栅挡）→ 托盘到位只进等门控（还没开计时）
            bus.SetInput(GrillL(1, 1), true);
            bus.SetInput(Tray(1, 1), true);
            Assert.Equal(SlotPhase.WaitingGate, machine.GetStatus(1, 1)!.Phase);
            // 等门控也算"在固化任务中"→ 塔灯绿（运转中，全机有未完成固化位即绿）
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true));

            // 光栅让空 → 立即开计时；相位换但聚合未变 → 塔灯绿保持零写
            bus.Writes.Clear();
            bus.SetInput(GrillL(1, 1), false);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.DoesNotContain(bus.Writes,
                w => !w.Key.IsInput && IoPointRoleInfo.IsMachineLevel(w.Key.Role));
        }
    }

    [Fact]
    public void MachineBeacon_EmptySlots_YellowOnFirstPump()
    {
        var (bus, machine, _) = Make();
        using (machine)
        {
            // 刚启动零事件：全机空闲（没有来料在固化）→ 首次 Pump 塔灯黄常亮=待料
            //（用户口径 V0.0.67；绿不亮、蜂不响、红不亮）
            machine.Pump(_now);
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconYellow), true));
            Assert.False(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true));
            Assert.False(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), true));
            Assert.False(Wrote(bus.Writes, Beacon(IoPointRole.BeaconRed), true));
        }
    }

    // ---------- 日志时长文本（durationText 注入跟随显示单位，V0.0.61） ----------

    [Fact]
    public void Logs_Default_KeepsMinutesFormat()
    {
        var (bus, machine, logs) = Make();   // policy=null → 兜底 FallbackMinutes=120
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            // 缺省“X 分钟”老口径逐字锁定（没注入 durationText 时历史观感不变）
            Assert.Contains(logs, m => m.Contains("开始固化计时 120 分钟（黄灯）"));
        }
    }

    [Fact]
    public void Logs_DurationTextInjected_AllDurationTextsFollow()
    {
        // 宿主注入的时长文本（生产=DurationUnit.LogText 跟随显示单位；此处纯函数代言）
        var (bus, machine, logs) = Make(durationText: m => $"{m / 60}小时");
        using (machine)
        {
            bus.SetInput(Tray(2, 3), true);
            Assert.Contains(logs, m => m.Contains("开始固化计时 2小时（黄灯）"));

            machine.Pump(_now.AddMinutes(120));
            Assert.Contains(logs, m => m.Contains("固化到点放行（绿灯，共 2小时）"));
        }
    }

    [Fact]
    public void PumpAfterDuration_WritesGreenAndLogs()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(2, 3), true);
            bus.Writes.Clear();

            _now += TimeSpan.FromMinutes(59);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(2, 3)!.Phase); // 差 1 分钟不放行

            _now += TimeSpan.FromMinutes(1);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(2, 3)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 2, 3), false));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen, 2, 3), true));
            Assert.Contains(logs, m => m.Contains("固化到点放行"));
        }
    }

    [Fact]
    public void TrayOffDuringCuring_ClearsLampsAndLogs()
    {
        var (bus, machine, logs) = Make();
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);   // 开计时（黄灯已写）
            bus.Writes.Clear();
            bus.SetInput(Tray(1, 1), false);  // 计时中取件

            Assert.Null(machine.GetStatus(1, 1)); // 回空闲（GetStatus 对空闲回 null）
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), false));
            // 绿灯从未点亮（开计时时的"防残留绿灯"写与灭灯写都被去重拦掉——
            // 稳态零冗余网络写正是去重语义），断言"没写过 true"而非"写过 false"
            Assert.DoesNotContain(bus.Writes,
                w => w.Key == Lamp(IoPointRole.LampGreen, 1, 1) && w.State);
            Assert.Contains(logs, m => m.Contains("计时中取件，计时作废"));
        }
    }

    [Fact]
    public void TrayOffWhenDone_ClearsGreenAndLogs()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 10 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(10);
            machine.Pump(_now);               // 放行（绿灯）
            bus.Writes.Clear();
            bus.SetInput(Tray(1, 1), false);  // 取件

            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen, 1, 1), false));
            Assert.Contains(logs, m => m.Contains("取件完成"));
        }
    }

    [Fact]
    public void DuplicateTrayEdge_DoesNotResetTimer()
    {
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(30);
            machine.Pump(_now);
            bus.SetInput(Tray(1, 1), true);   // 传感器抖动重复到位边沿

            _now += TimeSpan.FromMinutes(30); // 累计 60：若被重置则才 30 分，不该放行
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
        }
    }

    [Fact]
    public void PumpIdempotent_NoDuplicateWrites()
    {
        var (bus, machine, _) = Make();
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);   // 黄灯写一次
            machine.Pump(_now);
            machine.Pump(_now);

            Assert.Single(bus.Writes, w => w.Key == Lamp(IoPointRole.LampYellow, 1, 1));
        }
    }

    // ---------- 光栅门控 ----------

    [Fact]
    public void GateOn_WaitsWhenGrillBlocked_ThenGrillEdgeStarts()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GrillGate = true });
        using (machine)
        {
            bus.SetInput(GrillL(1, 1), true);  // 人手在架内（左光栅亮）
            bus.SetInput(Tray(1, 1), true);    // 托盘到位

            var status = machine.GetStatus(1, 1)!;
            Assert.Equal(SlotPhase.WaitingGate, status.Phase);
            Assert.False(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
            Assert.Contains(logs, m => m.Contains("等光栅让空"));

            bus.SetInput(GrillL(1, 1), false); // 人手离开（右光栅没绑=null 视为让空）
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
        }
    }

    [Fact]
    public void GateOn_WaitingSlot_PumpFallbackStarts()
    {
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GrillGate = true });
        using (machine)
        {
            bus.SetInput(GrillL(1, 1), true);
            bus.SetInput(Tray(1, 1), true);
            Assert.Equal(SlotPhase.WaitingGate, machine.GetStatus(1, 1)!.Phase);

            // 光栅让空边沿错过（如断连期间）——快照已灭，Pump 兜底推进
            bus.SetSnapshotSilently(GrillL(1, 1), false);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
        }
    }

    [Fact]
    public void GateOn_NoGrillBound_StartsImmediately()
    {
        // 没绑光栅的仓位门控自动放行（快照取不到=视为让空，不卡流程）
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GrillGate = true });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
        }
    }

    // ---------- 计时中光栅报警（V0.0.63 客户口径：计时不变，报警复位） ----------

    [Fact]
    public void GrillDuringCuring_RaisesAlarm_TimerUntouched()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(30);
            machine.Pump(_now);               // 半程
            bus.Writes.Clear();

            bus.SetInput(GrillL(1, 1), true); // 计时中人手伸入
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase); // 相位不动（不停表）
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), true));      // 报警红灯
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), false));  // 黄灯让位
            Assert.Contains(logs, m => m.Contains("报警"));

            bus.Writes.Clear();
            bus.SetInput(GrillL(1, 1), true); // 冗余重复边沿：AlarmGrill 状态去重不出声
            Assert.Empty(bus.Writes);
        }
    }

    [Fact]
    public void GrillClearAfterAlarm_ResetsLampsAndKeepsPlannedEnd()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);    // t0 开计时
            _now += TimeSpan.FromMinutes(10);
            machine.Pump(_now);                // t0+10
            bus.SetInput(GrillL(1, 1), true);  // 报警（人手伸入）
            bus.SetInput(GrillL(1, 1), false); // 复位（人手离开）
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));     // 红灯灭
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));   // 黄灯复亮
            Assert.Contains(logs, m => m.Contains("报警复位"));

            _now += TimeSpan.FromMinutes(50);  // 仍按原计划 t0+60 到点
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase); // 报亮/复位均未动表
        }
    }

    [Fact]
    public void GrillAlarmWhenExpires_DoneTurnsGreenAndClearsRed()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);  // 人手一直在架内（计时照走）
            bus.Writes.Clear();

            _now += TimeSpan.FromMinutes(60);
            machine.Pump(_now);                // 报警中到点：绿灯放行，红灯让位

            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen, 1, 1), true));
            Assert.Contains(logs, m => m.Contains("报警随放行解除"));
        }
    }

    [Fact]
    public void GrillAlarmWhenTrayLeaves_IdleClearsRed()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);  // 报警
            bus.Writes.Clear();

            bus.SetInput(Tray(1, 1), false);   // 人没走直接端走托盘：计时作废，红灯随取件灭
            Assert.Null(machine.GetStatus(1, 1)); // 回空闲
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));
            Assert.Contains(logs, m => m.Contains("计时中取件，计时作废"));
        }
    }

    [Fact]
    public void GrillOnInWaitingOrDone_NoAlarm()
    {
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 10, GrillGate = true });
        using (machine)
        {
            bus.SetInput(GrillL(1, 1), true);  // 人手先在架内
            bus.SetInput(Tray(1, 1), true);    // 到位 → 门控等待（本来就在等，不算中途报警）
            Assert.Equal(SlotPhase.WaitingGate, machine.GetStatus(1, 1)!.Phase);
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampRed, 1, 1) && w.State);

            bus.SetInput(GrillL(1, 1), false); // 让空 → 开计时
            _now += TimeSpan.FromMinutes(10);
            machine.Pump(_now);                // 放行 → Done
            bus.Writes.Clear();
            bus.SetInput(GrillL(1, 1), true);  // 取件伸入（Done 是正常取件动作，不报警）
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampRed, 1, 1) && w.State);
        }
    }

    // ---------- 报警复位模式（V0.0.69：Auto=现行为让空即复位 / Manual=
    //            人工确认解除；队列边界：人工档让空不复位、Pump 兜底也不复位，
    //            唯一解除口=ManualResetGrillAlarm；到点放行/取件不见滞留） ----------

    [Fact]
    public void ManualMode_GrillClearEdges_PumpFallback_KeepsAlarm()
    {
        // 人工档双保险都不自动解除：让空边沿不复位（日志"待确认"），快照兜底
        // （SetSnapshotSilently+Pump，边沿错过的路径）同样不复位。
        var mode = new Boxed<GrillAlarmResetMode>(GrillAlarmResetMode.Manual);
        var (bus, machine, logs) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 },
            resetModeProvider: () => mode.Value);
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);  // 报警（红灯）
            bus.Writes.Clear();

            bus.SetInput(GrillL(1, 1), false); // 让空边沿：人工档不复位
            Assert.True(machine.GetStatus(1, 1)!.AlarmGrill);   // 报警态保持
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampRed, 1, 1) && !w.State);
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampYellow, 1, 1) && w.State);
            Assert.Contains(logs, m => m.Contains("人工复位模式待确认"));

            // Pump 快照兜底路径：人工档也不自动复位（else 门槛在 Auto）
            machine.Pump(_now.AddSeconds(1));
            Assert.True(machine.GetStatus(1, 1)!.AlarmGrill);

            // 自动档瞬间切回（不重建状态机，provider 热切语义）：下一拍兜底回归自动
            mode.Value = GrillAlarmResetMode.Auto;
            machine.Pump(_now.AddSeconds(1));
            Assert.False(machine.GetStatus(1, 1)!.AlarmGrill);  // 自动档让空即复位
        }
    }

    [Fact]
    public void ManualReset_ClearsAlarm_TimingUnaffected()
    {
        // 唯一解除口：人工确认→红灭黄复亮+蜂停（塔灯回绿），计时不动，原计划到点放行。
        var (bus, machine, logs) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 },
            resetModeProvider: () => GrillAlarmResetMode.Manual);
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);   // 报警
            bus.SetInput(GrillL(1, 1), false);  // 让空不复位（人工档）
            bus.Writes.Clear();

            Assert.True(machine.ManualResetGrillAlarm(1, 1));
            Assert.False(machine.GetStatus(1, 1)!.AlarmGrill);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), false)); // 蜂鸣停
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true)); // 回转绿
            Assert.Contains(logs, m => m.Contains("人工复位确认"));

            // 计时不动：开计时那一刻锁定的 60 分钟原样走完 → (1,1) 正常 Done
            bus.Writes.Clear();
            _now += TimeSpan.FromMinutes(60);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen, 1, 1), true));
        }
    }

    [Fact]
    public void ManualReset_NotInAlarm_ReturnsFalse()
    {
        // 幂等安全：非报警态/未知仓位一律 false（重复点按钮无害）。
        var (bus, machine, _) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 },
            resetModeProvider: () => GrillAlarmResetMode.Manual);
        using (machine)
        {
            Assert.False(machine.ManualResetGrillAlarm(1, 1)); // 未知仓位
            bus.SetInput(Tray(1, 1), true);                    // 计时中未报警
            Assert.False(machine.ManualResetGrillAlarm(1, 1)); // 非报警态（幂等 no-op）
            bus.SetInput(GrillL(1, 1), true);                  // 报警
            Assert.True(machine.ManualResetGrillAlarm(1, 1));  // 解除
            Assert.False(machine.ManualResetGrillAlarm(1, 1)); // 已解除→幂等 false
        }
    }

    [Fact]
    public void GetAlarmSlots_SortedByLevelThenRack()
    {
        // 弹窗清单编排的取数契约：报警清单按层→架升序（Level 主键，同层架升序）。
        var (bus, machine, _) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 },
            resetModeProvider: () => GrillAlarmResetMode.Manual);
        using (machine)
        {
            bus.SetInput(Tray(1, 2), true); bus.SetInput(GrillL(1, 2), true);
            bus.SetInput(Tray(2, 1), true); bus.SetInput(GrillL(2, 1), true);
            bus.SetInput(Tray(1, 1), true); bus.SetInput(GrillL(1, 1), true);
            bus.SetInput(Tray(2, 2), true); bus.SetInput(GrillL(2, 2), true);

            var alarms = machine.GetAlarmSlots().Select(a => (a.Rack, a.Level)).ToList();
            // 排序契约（Coordinator 弹窗同源）：层号主序 → 同层架号升序
            Assert.Equal([(1, 1), (2, 1), (1, 2), (2, 2)], alarms);
        }
    }

    [Fact]
    public void AlignGrillBlocked_RestartsCuringWithAlarm()
    {
        // 重启对齐路径（StartCuring 复核）：托盘在架 + 人手还在（光栅挡）→ 开计时即报警
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetSnapshotSilently(Tray(1, 1), true);
            bus.SetSnapshotSilently(GrillL(1, 1), true);
            machine.AlignFromSnapshot();

            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), true));
            Assert.Contains(logs, m => m.Contains("报警"));
        }
    }

    [Fact]
    public void RestoreRecord_GrillStillBlocked_ReraisesAlarm()
    {
        // 断电续算路径（RestoreFromRecord 复核）：重启恢复计时 + 人手仍在架内 → 重新报警
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var (bus, machine1, _) = MakePersisted(persist: true,
            new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = true });
        using (machine1)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(10);  // 计了 10 分断电（落盘 Curing 记录）
        }

        var policy = new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = true };
        var logs2 = new List<string>();
        var machine2 = new SlotCuringMachine(bus, () => policy, clock: () => _now, log: logs2.Add);
        using (machine2)
        {
            bus.SetSnapshotSilently(GrillL(1, 1), true); // 重启时人手还在架内
            machine2.AlignFromSnapshot();

            Assert.Equal(SlotPhase.Curing, machine2.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), true));
            Assert.Contains(logs2, m => m.Contains("报警"));
        }
    }

    [Fact]
    public void GrillClearAfterAlarm_ResetsOnlyWhenBothSidesClear()
    {
        // 双光栅组合口径：一侧让空另一侧仍挡=人手还在架区，不复位；
        // 全让空才复位（与门控放行同源 GrillClear 检查）。
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);  // 左挡 → 报警
            bus.SetInput(GrillR(1, 1), true);  // 右也挡（已报警态短路，无新写）
            bus.Writes.Clear();

            bus.SetInput(GrillL(1, 1), false); // 左让空、右仍挡 → 不复位
            Assert.DoesNotContain(bus.Writes,
                w => w.Key == Lamp(IoPointRole.LampRed, 1, 1) && w.State == false);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);

            bus.SetInput(GrillR(1, 1), false); // 双侧让空 → 复位
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
        }
    }

    [Fact]
    public void GrillClearAlarm_SnapshotFallbackResetsWhenEdgeMissed()
    {
        // 快照兜底复位（V0.0.64）：真机让空边沿可能错过（让空与其他变化同拍/
        // 掉线期间）——用 SetSnapshotSilently 模拟"快照已双让空但无 OFF 边沿"，
        // Pump 心跳必须按快照复位，红灯不再挂到到点/取件。复位不动计时。
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            bus.SetInput(GrillL(1, 1), true);  // 报警（人手伸入）
            bus.SetInput(GrillR(1, 1), true);
            bus.Writes.Clear();

            bus.SetSnapshotSilently(GrillL(1, 1), false); // 快照直改：边沿错过的让空
            bus.SetSnapshotSilently(GrillR(1, 1), false);
            machine.Pump(_now);                // 心跳兜底 → 复位

            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase); // 相位不动
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed, 1, 1), false));  // 红灯灭
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true)); // 黄灯复亮
            Assert.Contains(logs, m => m.Contains("报警复位"));
        }
    }

    [Fact]
    public void SteadyCuringPump_ZeroNetworkWrites()
    {
        // 稳态零网络写（铁律 4 的状态机侧锁定）：Curing 稳态（含红灯补灭记账）
        // 去重后，心跳 Pump 不再产生任何灯写——防未来有人把补写改成每拍必写。
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);    // 开计时（记录黄 true/绿 false）
            machine.Pump(_now);                // 首拍：红灯补灭等记账巡回
            bus.Writes.Clear();

            machine.Pump(_now);                // 次拍起：稳态零网络写
            Assert.Empty(bus.Writes);
        }
    }

    // ---------- 自愈 ----------

    [Fact]
    public void WriteFailure_RetriedNextPump()
    {
        var (bus, machine, _) = Make();
        using (machine)
        {
            bus.WriteSucceeds = false;
            bus.SetInput(Tray(1, 1), true);   // 黄灯写失败（不记账）
            Assert.Empty(bus.Writes);

            bus.WriteSucceeds = true;
            machine.Pump(_now);               // 心跳补写
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 1, 1), true));
        }
    }

    [Fact]
    public void UnboundLamp_WriteFails_NoCrash()
    {
        // 总线一直写失败（点未绑定/断连）：状态机照常推进，不炸不卡
        var (bus, machine, _) = Make();
        using (machine)
        {
            bus.WriteSucceeds = false;
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(120);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
        }
    }

    [Fact]
    public void AlignSnapshot_TrayPresent_RestartsCuring()
    {
        var (bus, machine, logs) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetSnapshotSilently(Tray(3, 2), true); // 重启场景：只给快照（无边沿）
            machine.AlignFromSnapshot();

            Assert.Equal(SlotPhase.Curing, machine.GetStatus(3, 2)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow, 3, 2), true));
            Assert.Contains(logs, m => m.Contains("重启对齐"));

            // 幂等：重复对齐不动已在流程中的仓位
            _now += TimeSpan.FromMinutes(10);
            machine.AlignFromSnapshot();
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(3, 2)!.Phase);
        }
    }

    // ---------- 策略热切换 ----------

    [Fact]
    public void PolicyHotSwap_RunningSlotKeepsOldMinutes()
    {
        // 可变策略源：模拟"策略窗激活新策略"的热切换
        var bus = new ScriptedPointBus();
        var provider = new Boxed<CuringPolicy?>(new CuringPolicy { Name = "A", GlobalMinutes = 60 });
        var machine = new SlotCuringMachine(bus, () => provider.Value, clock: () => _now);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e); // 宿主转发
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);   // 按 A（60 分）开计时
            provider.Value = new CuringPolicy { Name = "B", GlobalMinutes = 1 }; // 热切 B

            _now += TimeSpan.FromMinutes(1);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase); // 在制品不受新策略影响

            _now += TimeSpan.FromMinutes(59);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);   // 仍按 A 的 60 分走完
        }
    }

    [Fact]
    public void PolicyHotChange_ExtendsInFlightCuring()
    {
        // 宿主收口（策略窗保存后 ApplyPolicyHotChange）：在制单向上调——
        // 旧锁定 60 分已过 30 分，热更后新解析 1440 分生效（不放行、锁定值上调）
        var bus = new ScriptedPointBus();
        var logs = new List<string>();
        var provider = new Boxed<CuringPolicy?>(new CuringPolicy { Name = "A", GlobalMinutes = 60 });
        var machine = new SlotCuringMachine(bus, () => provider.Value,
            clock: () => _now, log: logs.Add);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e); // 宿主转发
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);   // 按 60 分锁定开计时
            _now += TimeSpan.FromMinutes(30);
            machine.Pump(_now);

            provider.Value = new CuringPolicy { Name = "B", GlobalMinutes = 1440 }; // 24 小时
            machine.ApplyPolicyHotChange();
            Assert.Contains(logs, m => m.Contains("策略热更"));

            _now += TimeSpan.FromMinutes(30); // 旧锁定时长到点时刻——被延长顶掉，不放行
            machine.Pump(_now);
            var st = machine.GetStatus(1, 1)!;
            Assert.Equal(SlotPhase.Curing, st.Phase);
            Assert.Equal(1440, st.TotalMinutes);  // 锁定值已上调
            Assert.Equal(1380, st.RemainMinutes); // 剩 1440-30=1380

            _now += TimeSpan.FromMinutes(1380);   // 新时长到点才放行
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
        }
    }

    [Fact]
    public void PolicyHotChange_Shorten_Ignored()
    {
        // 工艺红线：在制调小一律不动（调小立即生效=把没固化满的货提前放行）
        var bus = new ScriptedPointBus();
        var provider = new Boxed<CuringPolicy?>(new CuringPolicy { Name = "A", GlobalMinutes = 60 });
        var machine = new SlotCuringMachine(bus, () => provider.Value, clock: () => _now);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e);
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(30);
            machine.Pump(_now);

            provider.Value = new CuringPolicy { Name = "B", GlobalMinutes = 5 }; // 调小
            machine.ApplyPolicyHotChange();   // 静默忽略（无延长日志，零动作）

            _now += TimeSpan.FromMinutes(5);  // 调小后的"到点"时刻——必须不放行
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.Equal(60, machine.GetStatus(1, 1)!.TotalMinutes); // 仍锁定 60

            _now += TimeSpan.FromMinutes(25); // 旧 60 分走完才放行
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
        }
    }

    [Fact]
    public void PolicyHotChange_PersistsExtendedMinutes()
    {
        // 延长同步重写持久化记录：断电恢复按延长后的时长续算（不回退旧值）
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var bus = new ScriptedPointBus();
        var provider = new Boxed<CuringPolicy?>(
            new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = true });
        var machine = new SlotCuringMachine(bus, () => provider.Value, clock: () => _now);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e);
        using (machine)
        {
            bus.SetInput(Tray(2, 1), true);   // 落盘 60 分
            provider.Value = new CuringPolicy { Name = "T2", GlobalMinutes = 1440, PersistMinutes = true };
            machine.ApplyPolicyHotChange();

            var rec = CuringStateStore.Load()!.Slots.Single(r => r.Rack == 2 && r.Level == 1);
            Assert.Equal(1440, rec.Minutes);  // 记录已是延长后的值
        }
    }

    [Fact]
    public void PolicyHotChange_NonCuringSlots_Unaffected()
    {
        // 边界：Done（已放行）与 WaitingGate（没开计时）不做在制热更——
        // Gate 开计时那一刻会重新按当前策略解析（热更后开计时自然是新时长）
        var bus = new ScriptedPointBus();
        var provider = new Boxed<CuringPolicy?>(new CuringPolicy { Name = "A", GlobalMinutes = 60 });
        var machine = new SlotCuringMachine(bus, () => provider.Value, clock: () => _now);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e);
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);          // 1-1：60 分到点放行（Done）
            _now += TimeSpan.FromMinutes(60);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);

            provider.Value = new CuringPolicy { Name = "B", GlobalMinutes = 60, GrillGate = true };
            bus.SetInput(GrillL(3, 1), true);        // 3-1：先亮光栅（托盘边沿时才会进门控等待）
            bus.SetInput(Tray(3, 1), true);
            Assert.Equal(SlotPhase.WaitingGate, machine.GetStatus(3, 1)!.Phase);

            provider.Value = new CuringPolicy { Name = "C", GlobalMinutes = 1440, GrillGate = true };
            machine.ApplyPolicyHotChange();          // Done/Gate 都不动
            Assert.Equal(60, machine.GetStatus(1, 1)!.TotalMinutes);  // Done 保持旧锁定

            bus.SetInput(GrillL(3, 1), false);       // 光栅让空 → 开计时现取新策略 1440
            machine.Pump(_now);
            var opened = machine.GetStatus(3, 1)!;
            Assert.Equal(SlotPhase.Curing, opened.Phase);
            Assert.Equal(1440, opened.TotalMinutes);
        }
    }

    // ---------- 计时状态持久化（PersistMinutes 开关，断电续算） ----------

    /// <summary>持久化用例的公共装配：TestScope 隔离落盘目录 + 开关策略 + 宿主转发。</summary>
    private (ScriptedPointBus Bus, SlotCuringMachine Machine, List<string> Logs) MakePersisted(
        bool persist, CuringPolicy? policy = null)
    {
        var bus = new ScriptedPointBus();
        var logs = new List<string>();
        var p = policy ?? new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = persist };
        var machine = new SlotCuringMachine(bus, () => p, clock: () => _now, log: logs.Add);
        bus.PointChanged += (_, e) => machine.OnPointChanged(e); // 宿主转发
        return (bus, machine, logs);
    }

    [Fact]
    public void Persist_StartAndDone_WritesStateFile()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var (bus, machine, _) = MakePersisted(persist: true,
            new CuringPolicy { Name = "T", GlobalMinutes = 10, PersistMinutes = true });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true); // 开计时 → 落盘 Curing
            var book1 = CuringStateStore.Load()!;
            Assert.Single(book1.Slots, r => r.Rack == 1 && r.Level == 1 &&
                r.Phase == "Curing" && r.Minutes == 10 && r.StartedAt == _now);

            _now += TimeSpan.FromMinutes(10);
            machine.Pump(_now);             // 到点放行 → 落盘 Done
            var book2 = CuringStateStore.Load()!;
            Assert.Single(book2.Slots, r => r.Phase == "Done" && r.Minutes == 10);
        }
    }

    [Fact]
    public void Persist_Restart_RestoresRemainingMinutes()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var (bus, machine1, _) = MakePersisted(persist: true);
        var policy = new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = true };
        using (machine1)
        {
            bus.SetInput(Tray(1, 1), true);   // 8:00 开计时（落盘 StartedAt=8:00, 60 分）
        }
        _now += TimeSpan.FromMinutes(30);     // 8:30：模拟断电 30 分钟（时间流逝）

        // "重启"：全新状态机（内存全丢），对齐时从文件续算
        var machine2 = new SlotCuringMachine(bus, () => policy, clock: () => _now);
        using (machine2)
        {
            machine2.AlignFromSnapshot();
            var status = machine2.GetStatus(1, 1)!;
            Assert.Equal(SlotPhase.Curing, status.Phase);
            Assert.Equal(30, status.RemainMinutes); // 断电 30 分钟已计入，剩 30
        }
    }

    [Fact]
    public void Persist_DoneRestart_RestoresGreen()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var (bus, machine1, _) = MakePersisted(persist: true,
            new CuringPolicy { Name = "T", GlobalMinutes = 10, PersistMinutes = true });
        using (machine1)
        {
            bus.SetInput(Tray(2, 1), true);
            _now += TimeSpan.FromMinutes(10);
            machine1.Pump(_now);              // 到点放行（落盘 Done）
        }
        bus.Writes.Clear();

        var policy = new CuringPolicy { Name = "T", GlobalMinutes = 10, PersistMinutes = true };
        var machine2 = new SlotCuringMachine(bus, () => policy, clock: () => _now);
        using (machine2)
        {
            machine2.AlignFromSnapshot();     // 重启恢复：托盘还在架 → 保持放行
            Assert.Equal(SlotPhase.Done, machine2.GetStatus(2, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen, 2, 1), true)); // 绿灯重写
        }
    }

    [Fact]
    public void Persist_TrayLeft_RemovesRecord()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var (bus, machine, _) = MakePersisted(persist: true);
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);   // 开计时（落盘）
            bus.SetInput(Tray(1, 1), false);  // 取件（清记录）
            var book = CuringStateStore.Load()!;
            Assert.DoesNotContain(book.Slots, r => r.Rack == 1 && r.Level == 1);
        }
    }

    [Fact]
    public void PersistOff_NoWriteAndIgnoresOldRecords()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        // 预置旧记录（模拟上次开过开关留下的），本次策略关 → 不得恢复（宁可重计）
        CuringStateStore.Save(new CuringStateBook
        {
            Slots = { new CuringSlotRecord
                { Rack = 1, Level = 1, Phase = "Curing", StartedAt = _now.AddMinutes(-50), Minutes = 60 } },
        });
        var (bus, machine, logs) = MakePersisted(persist: false,
            new CuringPolicy { Name = "T", GlobalMinutes = 60 }); // PersistMinutes=false
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            // 开关关：不重写文件——内容仍是预置的旧记录（StartedAt 未变）
            var book = CuringStateStore.Load()!;
            Assert.Single(book.Slots);
            Assert.Equal(_now.AddMinutes(-50), book.Slots[0].StartedAt);
        }

        var machine2 = new SlotCuringMachine(bus,
            () => new CuringPolicy { Name = "T", GlobalMinutes = 60 }, clock: () => _now, log: logs.Add);
        using (machine2)
        {
            machine2.AlignFromSnapshot();
            Assert.Contains(logs, m => m.Contains("重新开始计时")); // 旧记录被无视，走重计
        }
    }

    [Fact]
    public void Persist_CorruptFile_FallsBackToRecount()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(CuringStateStore.DefaultPath, "{ 不是 JSON");
        var (bus, machine, logs) = MakePersisted(persist: true);
        bus.SetSnapshotSilently(Tray(1, 1), true); // 预置托盘在架（不发边沿，不触发落盘覆盖坏文件）

        var machine2 = new SlotCuringMachine(bus,
            () => new CuringPolicy { Name = "T", GlobalMinutes = 60, PersistMinutes = true },
            clock: () => _now, log: logs.Add);
        using (machine)
        using (machine2)
        {
            machine2.AlignFromSnapshot();
            Assert.Contains(logs, m => m.Contains("损坏"));         // 坏文件原因进日志
            Assert.Contains(logs, m => m.Contains("重新开始计时")); // 兜底重计，不卡流程
        }
    }

    /// <summary>可变策略源（热切换测试用的小盒子）。</summary>
    private sealed class Boxed<T>
    {
        public Boxed(T value) => Value = value;
        public T Value { get; set; }
    }

    // ---------- 过账挂点事件（P3.2 后半：真实边沿发 / 重启对齐不发） ----------

    [Fact]
    public void TrayArrivedEvent_FiresOnRealEdge_NotOnRestartAlign()
    {
        var (bus, machine, _) = Make();
        var arrived = new List<(int Rack, int Level)>();
        machine.TrayArrived += (r, l) => arrived.Add((r, l));
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true); // 真实边沿
            Assert.Single(arrived);
            Assert.Equal((1, 1), arrived[0]);

            // 重启对齐：托盘在架但状态机空闲（模拟断电重启）——不发进站
            var bus2 = new ScriptedPointBus();
            bus2.SetSnapshotSilently(Tray(2, 2), true);
            var machine2 = new SlotCuringMachine(bus2, () => (CuringPolicy?)null,
                clock: () => _now, log: _ => { });
            machine2.TrayArrived += (r, l) => arrived.Add((r, l));
            using (machine2)
            {
                machine2.AlignFromSnapshot();
                Assert.Single(arrived); // 对齐不发——防 MES 重复过账
            }
        }
    }

    [Fact]
    public void CuringStartedEvent_FiresOnGateRelease()
    {
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GrillGate = true });
        var started = new List<(int Rack, int Level)>();
        machine.CuringStarted += (r, l) => started.Add((r, l));
        using (machine)
        {
            bus.SetInput(GrillL(1, 1), true);  // 先占住光栅（人手在架内）
            bus.SetInput(Tray(1, 1), true);    // 到位 → 等放行
            Assert.Empty(started);             // 还没开计时
            bus.SetInput(GrillL(1, 1), false); // 光栅让空 → 开计时
            Assert.Single(started);
        }
    }

    [Fact]
    public void TrayLeftEvent_CarriesPhaseAndCuredSeconds()
    {
        var (bus, machine, _) = Make(new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        var lefts = new List<(int Rack, int Level, SlotPhase Phase, int Seconds)>();
        machine.TrayLeft += (r, l, p, s) => lefts.Add((r, l, p, s));
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);
            _now += TimeSpan.FromMinutes(30);
            bus.SetInput(Tray(1, 1), false); // 计时中取件：实际经过 30 分钟
            Assert.Single(lefts);
            Assert.Equal((1, 1, SlotPhase.Curing, 1800), lefts[0]);

            // 到点放行后取件：带计划时长（60 分钟 = 3600 秒）
            bus.SetInput(Tray(2, 1), true);
            _now += TimeSpan.FromMinutes(60);
            machine.Pump(_now);              // 到点放行
            bus.SetInput(Tray(2, 1), false); // 取件
            Assert.Equal(2, lefts.Count);
            Assert.Equal((2, 1, SlotPhase.Done, 3600), lefts[1]);
        }
    }
}
