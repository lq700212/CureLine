// =========================================================================
// 连接测试器测试：成功链（模拟工厂）+ 失败链（连不上/不可达）
//
// 无真机可测：成功走 SimulatedPlcClient 工厂；失败走 ip=fail 与
// 真机工厂 +127.0.0.1:1（拒绝 fast，不会卡 3s 超时；顺带覆盖
// ModbusStationClient 连接失败链——connect 拒绝→master false→不抛）。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class ConnectionTesterTests : IDisposable
{
    private readonly TestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact(DisplayName = "模拟工厂成功并读回值")]
    public async Task SimFactory_Ok_WithValue()
    {
        var sim = new SimulatedPlcClient();
        sim.Connect("127.0.0.1", 6060);
        sim.PresetWord("D50001", 42);
        var (ok, msg, ms) = await PlcConnectionTester.TestAsync(
            () => sim, "127.0.0.1", 6060, "D50001");
        Assert.True(ok);
        Assert.Contains("42", msg);
        Assert.True(ms >= 0);
        sim.Dispose();
    }

    [Fact(DisplayName = "连不上返回失败不抛")]
    public async Task FailIp_False_NoThrow()
    {
        var (ok, msg, _) = await PlcConnectionTester.TestAsync(
            () => new SimulatedPlcClient(), "fail", 6060, "D30001");
        Assert.False(ok);
        Assert.Contains("连接失败", msg);
    }

    [Fact(DisplayName = "真机工厂不可达失败")]
    public async Task RealFactory_Unreachable_False()
    {
        var (ok, msg, _) = await PlcConnectionTester.TestAsync(
            () => new ModbusStationClient(), "127.0.0.1", 1, "R13001", 2000);
        Assert.False(ok);
        Assert.Contains("连接失败", msg);
    }
}
