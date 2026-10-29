// =========================================================================
// 模拟 PLC 与 HTTP 直测：SimulatedPlcClient 语义 + MesHttpClient 成功/失败
//
// SimulatedPlcClient 是全部业务测试的地基，先把它自己锁死：
// ① ip 为空/"fail" 连不上；② 断线后读写失败；③ 字区/串区独立；
// ④ WriteString 按 length 截断；⑤ 未预置读出 0/空串（不抛）。
// MesHttpClient：200→true；500→false 但错误体保留（老项目取错误流语义）；
// solemn 连不上→false。
// =========================================================================

using CureLine.Core.Comms;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class SimAndHttpTests : IDisposable
{
    private readonly TestScope _scope = new();

    public void Dispose() => _scope.Dispose();

    [Fact(DisplayName = "模拟器空ip与fail连不上")]
    public void Sim_BadIp_ConnectFails()
    {
        using var sim = new SimulatedPlcClient();
        Assert.False(sim.Connect("", 6060));
        Assert.False(sim.Connect("fail", 6060));
        Assert.False(sim.IsConnected);
    }

    [Fact(DisplayName = "模拟器断线后读写失败")]
    public void Sim_Disconnected_RwFails()
    {
        using var sim = new SimulatedPlcClient();
        sim.Connect("127.0.0.1", 6060);
        sim.Disconnect();
        Assert.False(sim.ReadInt16("D1").Ok);
        Assert.False(sim.WriteInt16("D1", 1));
        Assert.False(sim.WriteString("D1", "x", 10));
    }

    [Fact(DisplayName = "模拟器字串区独立读写")]
    public void Sim_Word_String_Independent()
    {
        using var sim = new SimulatedPlcClient();
        sim.Connect("127.0.0.1", 6060);
        sim.PresetWord("D30001", 7);
        sim.PresetString("D30001", "text");
        Assert.Equal(7, sim.ReadInt16("D30001").Value);
        Assert.Equal("text", sim.ReadString("D30001", 10).Value);
        sim.WriteInt16("D30001", 8);
        Assert.Equal("text", sim.PeekString("D30001")); // 写字不影响串区
    }

    [Fact(DisplayName = "模拟器写串按长截断")]
    public void Sim_WriteString_Truncates()
    {
        using var sim = new SimulatedPlcClient();
        sim.Connect("127.0.0.1", 6060);
        sim.WriteString("R17001", "ABCDEFG", 3);
        Assert.Equal("ABC", sim.PeekString("R17001"));
    }

    [Fact(DisplayName = "模拟器未预置读零空不抛")]
    public void Sim_Unset_ReadsZeroEmpty()
    {
        using var sim = new SimulatedPlcClient();
        sim.Connect("127.0.0.1", 6060);
        Assert.Equal(0, sim.ReadInt16("D99999").Value);
        Assert.Equal("", sim.ReadString("D99999", 10).Value);
        Assert.Equal(0, sim.PeekWord("D99999"));
        Assert.Equal("", sim.PeekString("D99999"));
    }

    [Fact(DisplayName = "HTTP200成功")]
    public void Http_Ok_True_Body()
    {
        using var mes = new MesStubServer();
        mes.EnqueueOk("fine");
        bool ok = MesHttpClient.PostJson(mes.Url, "{\"a\":1}", out string resp);
        Assert.True(ok);
        Assert.Contains("fine", resp);
        Assert.Single(mes.Bodies);
    }

    [Fact(DisplayName = "HTTP500失败但错误体保留")]
    public void Http_500_False_BodyKept()
    {
        using var mes = new MesStubServer();
        mes.EnqueueHttpError("boom");
        bool ok = MesHttpClient.PostJson(mes.Url, "{}", out string resp);
        Assert.False(ok);
        Assert.Contains("boom", resp);
    }

    [Fact(DisplayName = "连不上MES失败不抛")]
    public void Http_Unreachable_False_NoThrow()
    {
        var ex = Record.Exception(() =>
            Assert.False(MesHttpClient.PostJson("http://127.0.0.1:1/mes/", "{}", out _)));
        Assert.Null(ex);
    }
}
