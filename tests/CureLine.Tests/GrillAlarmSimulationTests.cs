// =========================================================================
// GrillAlarmSimulationTests（V0.0.64，用户拍板：现场交付前用 Mock 输入信号
// 把"光栅报警→复位"逻辑当真现场时序过一遍，锁进回归）：
//
// 干什么：按一台 IO 耦合器一路仓位的真实 DI 时序，用 ScriptedPointBus
//   模拟"托盘 + 双光栅"输入点流，驱动状态机走完整个生命周期——
//   托盘到位开计时 → 双光栅先后挡（红灯报警） → 双光栅先后让空
//   （边沿即时复位） → 原计划到点绿灯放行 → 取件灭灯回空闲。
// 为什么锚 ScriptedPointBus：它的 SetInput = 点位先翻转进快照、再发变化
//   边沿，与 V0.0.64 起真机/演示总线的轮询契约（先整拍换快照、再统一发
//   边沿）一致——剧本里每次边沿处理读到的快照与真机相同，复位判定链
//   不用 Pump 兜底也能即时成立（边沿错过的兜底另有专测）。
// 怎么扩：现场剧本变化（人手挪步、先取件等人等）就按 t 序号加 SetInput。
//   断言全部走灯写（bus.Writes）+ 日志 + 相位，黑盒不改状态机。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Curing;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class GrillAlarmSimulationTests
{
    // 虚拟时钟（xUnit 每用例新实例，字段即每用例独立；与真机 1s 心跳对位）
    private DateTimeOffset _now = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);

    // 模拟耦合器一路仓位的语义点（1-1 固定仓位，输入全 DI / 输出全 DO 三色灯）
    private static IoPointKey Tray(int rack, int level) =>
        new(true, IoPointRole.TrayDetect, rack, level);
    private static IoPointKey GrillL(int rack, int level) =>
        new(true, IoPointRole.RackLightL, rack, level);
    private static IoPointKey GrillR(int rack, int level) =>
        new(true, IoPointRole.RackLightR, rack, level);
    private static IoPointKey Lamp(IoPointRole role) =>
        new(false, role, 1, 1);
    /// <summary>机台总监塔灯键（V0.0.65）：机台级恒 (DO, role, 0, 0)。</summary>
    private static IoPointKey Beacon(IoPointRole role) =>
        new(false, role, 0, 0);

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

    [Fact]
    public void SimulatedCouplerDiFlow_AlarmThenEdgeReset_FullCycle()
    {
        // ---- 剧本（输入点流全部按耦合器 DI 真实时序编排）----
        var (bus, machine, logs) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            // t0：托盘 DI OFF→ON（摆放到位）——门控未开，立即开计时（黄灯）
            bus.SetInput(Tray(1, 1), true);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow), true));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen), false)); // 防残留绿
            Assert.Contains(logs, m => m.Contains("开始固化计时"));

            _now += TimeSpan.FromMinutes(10);  // 半程
            machine.Pump(_now);                // 心跳巡检（灯保持，稳态补写记账巡回）
            bus.Writes.Clear();

            // t1：光栅L DI OFF→ON（人手伸入，左挡）——红灯报警（黄绿让位），不停表
            bus.SetInput(GrillL(1, 1), true);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed), true));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow), false));
            // 机台塔灯同步（V0.0.65）：报警即全机红 + 蜂鸣长鸣
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconRed), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), true));
            Assert.Contains(logs, m => m.Contains("光栅被挡"));
            bus.Writes.Clear();

            // t2：光栅R DI OFF→ON（挪步、双侧全挡）——AlarmGrill 已报警，短路零写零日志
            bus.SetInput(GrillR(1, 1), true);
            Assert.Empty(bus.Writes);

            // t3/t4：双光栅 ON→OFF 连发（真机一帧 16 位逐位让空——两条 OFF
            // 边沿在同一轮询拍内接连到达，状态机逐条判断）
            // t3 左先让空：右侧仍挡（人手还在架区）→ 不复位、红灯保持
            bus.SetInput(GrillL(1, 1), false);
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            Assert.DoesNotContain(bus.Writes,
                w => w.Key == Lamp(IoPointRole.LampRed) && w.State == false);

            // t4 右让空：双全让空 → 复位即时生效（红灯灭/黄灯复亮），计时不动
            bus.SetInput(GrillR(1, 1), false);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed), false));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampYellow), true));
            // 机台塔灯同步（V0.0.65）：复位即红灭+蜂停；唯一仓位回计时中 → 塔灯黄
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconRed), false));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), false));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconYellow), true));
            Assert.Contains(logs, m => m.Contains("报警复位"));
            Assert.Equal(SlotPhase.Curing, machine.GetStatus(1, 1)!.Phase);
            bus.Writes.Clear();

            // t5：按原计划 t0+60 到点——报警/复位全程未打断计时
            _now += TimeSpan.FromMinutes(50);
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true)); // 塔灯放行绿
            // 复位已先行，到点放行不该再打"报警随放行解除"日志
            Assert.DoesNotContain(logs, m => m.Contains("报警随放行解除"));
            bus.Writes.Clear();

            // t6：托盘 ON→OFF（取件离开）——灭灯回空闲
            //（黄/红在到点放行拍已写灭记账，取件去重不再发写——只断言灭绿灯）
            bus.SetInput(Tray(1, 1), false);
            Assert.Null(machine.GetStatus(1, 1));
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen), false));
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampYellow));
            Assert.DoesNotContain(bus.Writes, w => w.Key == Lamp(IoPointRole.LampRed));
            Assert.Contains(logs, m => m.Contains("取件完成"));
        }
    }

    [Fact]
    public void SimulatedCouplerDiFlow_AlarmUntilRelease_AlarmClearedByRelease()
    {
        // 反剧本：人手伸入后一直没退（光栅保持 ON）——报警挂在计时全程，
        // 到点放行时"绿灯接替红灯"（报警随放行解除），既不丢复位也不漏放行。
        var (bus, machine, logs) = Make(
            new CuringPolicy { Name = "T", GlobalMinutes = 60 });
        using (machine)
        {
            bus.SetInput(Tray(1, 1), true);      // 到位 → 开计时
            bus.SetInput(GrillL(1, 1), true);    // 人手伸入（左挡）
            bus.Writes.Clear();

            _now += TimeSpan.FromMinutes(60);    // 到点（人手仍在——红灯一直亮）
            machine.Pump(_now);
            Assert.Equal(SlotPhase.Done, machine.GetStatus(1, 1)!.Phase);
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampGreen), true));  // 绿灯放行
            Assert.True(Wrote(bus.Writes, Lamp(IoPointRole.LampRed), false));   // 红灯让位
            // 机台塔灯同步（V0.0.65）：放行即全机绿 + 蜂鸣停（报警随放行解除）
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.BeaconGreen), true));
            Assert.True(Wrote(bus.Writes, Beacon(IoPointRole.Buzzer), false));
            Assert.Contains(logs, m => m.Contains("报警随放行解除"));
        }
    }
}
