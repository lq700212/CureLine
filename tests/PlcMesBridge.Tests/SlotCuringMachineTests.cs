// =========================================================================
// SlotCuringMachineTests：仓位状态机全流程（虚拟时钟手动 Pump，确定性验证）
// 流程口径：托盘到位→（门控开则等光栅让空）→黄灯计时→到点绿灯→托盘离开灭灯。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Curing;
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

    /// <summary>可变策略源（热切换测试用的小盒子）。</summary>
    private sealed class Boxed<T>
    {
        public Boxed(T value) => Value = value;
        public T Value { get; set; }
    }
}
