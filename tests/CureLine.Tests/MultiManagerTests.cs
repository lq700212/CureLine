// =========================================================================
// 多机管理测试：ConnectAll 开关跳过 + 连接失败红灯事件
// 用模拟 PLC（PLCuse 全 1 时模拟永远连上；ip=fail 时连不上）。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.Core.Modes;

namespace CureLine.Tests;

public class MultiManagerTests
{
    public MultiManagerTests()
    {
        // AppConfig 静态初值不可靠（PlcIps 默认 null），测试显式初始化。
        for (int i = 0; i < 8; i++)
        {
            AppConfig.PlcUse[i] = "1";
            AppConfig.PlcIps[i] = "127.0.0.1";
        }
    }    [Fact(DisplayName = "PLCuse为0跳过不连接")]
    public void ConnectAll_Skips_Disabled()
    {
        for (int i = 1; i < 8; i++) AppConfig.PlcUse[i] = "0";
        try
        {
            using var mgr = new MultiMachineManager(_ => new SimulatedPlcClient());
            var logs = new List<(int, string)>();
            mgr.StationLog += (id, m) => logs.Add((id, m));
            var (ok, skipped) = mgr.ConnectAll();
            Assert.Equal(1, ok);
            Assert.Equal(7, skipped);
            Assert.Contains(logs, x => x.Item1 == 3); // 被跳过的机台有日志
            Assert.Single(mgr.Stations); // 只建了启用的
        }
        finally
        {
            for (int i = 0; i < 8; i++) AppConfig.PlcUse[i] = "1";
        }
    }

    [Fact(DisplayName = "连不上触发StationBroken")]
    public void ConnectAll_Fail_RaisesBroken()
    {
        for (int i = 1; i < 8; i++) AppConfig.PlcUse[i] = "0";
        AppConfig.PlcIps[0] = "fail";
        try
        {
            using var mgr = new MultiMachineManager(_ => new SimulatedPlcClient());
            int broken = -1;
            mgr.StationBroken += id => broken = id;
            var (ok, _) = mgr.ConnectAll();
            Assert.Equal(0, ok);
            Assert.Equal(0, broken);
        }
        finally
        {
            for (int i = 0; i < 8; i++) AppConfig.PlcUse[i] = "1";
            AppConfig.PlcIps[0] = "127.0.0.1";
        }
    }
}
