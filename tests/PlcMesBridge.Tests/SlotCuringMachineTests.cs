// =========================================================================
// SlotCuringMachineTests：仓位状态机全流程（虚拟时钟手动 Pump，确定性验证）
// 流程口径：托盘到位→（门控开则等光栅让空）→黄灯计时→到点绿灯→托盘离开灭灯。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Tests.Mocks;

namespace PlcMesBridge.Tests;

public class SlotCuringMachineTests
{
    // 虚拟时钟（xUnit 每用例新实例，字段即每用例独立）
    private DateTimeOffset _now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    private static IoPointKey Tray(int rack, int level) =>
        new(true, IoPointRole.TrayDetect, rack, level);
    private static IoPointKey GrillL(int rack, int level) =>
        new(true, IoPointRole.RackLightL, rack, level);
    private static IoPointKey Lamp(IoPointRole role, int rack, int level) =>
        new(false, role, rack, level);

    private (ScriptedPointBus Bus, SlotCuringMachine Machine, List<string> Logs) Make(
        CuringPolicy? policy = null)
    {
        var bus = new ScriptedPointBus();
        var logs = new List<string>();
        var machine = new SlotCuringMachine(bus, () => policy,
            clock: () => _now, log: logs.Add);
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
