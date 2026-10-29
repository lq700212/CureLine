// =========================================================================
// 网关端到端测试：本地 HttpListener 桩 MES + 内存模拟 PLC
// 跑通"触发→读JSON→POST→回写→完成"全链路，含 0030 拆包与失败回写分支。
// =========================================================================

using System.Net;
using System.Text;
using Newtonsoft.Json;
using CureLine.Core.Comms;
using CureLine.Core.Mes;
using CureLine.Core.Modes;

namespace CureLine.Tests;

public class MultiGatewayTests : IDisposable
{
    private readonly SimulatedPlcClient _plc = new();
    private readonly HttpListener _mes = new();
    private readonly string _url;
    private string _nextResp = "{\"STATUS\":\"1\",\"MSG\":\"ok\",\"DATA\":null}";
    private readonly int _port;

    public MultiGatewayTests()
    {
        _port = 18080 + Random.Shared.Next(0, 500);
        _url = $"http://127.0.0.1:{_port}/mes/";
        _mes.Prefixes.Add(_url);
        _mes.Start();
        Task.Run(ServeMes);
        _plc.Connect("127.0.0.1", 6060);

        MesConfig.UrlApi0030 = _url;
        MesConfig.TriggerApi0030 = "R17000";
        MesConfig.ApiAddresses[2].PlcData = "R17001";
        MesConfig.ApiAddresses[2].MesStatus = "R17801";
        MesConfig.ApiAddresses[2].MesMsg = "R17810";
        MesConfig.ApiAddresses[2].MesData = "R17900";
        MesConfig.ApiAddresses[2].MesComplete = "R17800";
        MesConfig.DataLength = 799;
    }

    private async Task ServeMes()
    {
        while (_mes.IsListening)
        {
            try
            {
                var ctx = await _mes.GetContextAsync();
                byte[] b = Encoding.UTF8.GetBytes(_nextResp);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length);
                ctx.Response.Close();
            }
            catch { break; }
        }
    }

    public void Dispose()
    {
        try { _mes.Stop(); } catch { }
        _plc.Dispose();
    }

    private StationGateway Gw() => new(0, _ => _plc);

    private static void WaitFor(Func<bool> cond, string what)
    {
        for (int i = 0; i < 100 && !cond(); i++)
            Thread.Sleep(100);
        Assert.True(cond(), what);
    }

    [Fact(DisplayName = "网关全链路：触发→回写STATUS1→完成位")]
    public void Gateway_FullLoop_WritesBack()
    {
        string json = JsonConvert.SerializeObject(new MesModels.Api0030Request
        {
            RC_NO = "R1", ACTION = "I", BC_NO = "B1;",
            IN_DATETIME = "2026-09-21 10:00:00.000", CHECK_RESULT = "0",
        });
        _plc.PresetString("R17001", json);
        _plc.PresetWord("R17000", 1);

        using var gw = Gw();
        gw.PollTriggers();

        WaitFor(() => _plc.PeekWord("R17800") == 1, "完成位未置1");
        Assert.Equal("1", _plc.PeekString("R17801"));
        Assert.Equal("ok", _plc.PeekString("R17810"));
        Assert.Equal(0, _plc.PeekWord("R17000"));
    }

    [Fact(DisplayName = "0030 DATA拆三段")]
    public void Gateway_Data30_Split3()
    {
        _nextResp = "{\"STATUS\":\"1\",\"MSG\":\"ok\"," +
            "\"DATA\":[{\"OUT_RESULT\":\"PASS\",\"OUT_REMARK\":\"rm\",\"OUT_NEXTOP\":\"next\"}]}";
        string json = JsonConvert.SerializeObject(new MesModels.Api0030Request
        {
            RC_NO = "R1", ACTION = "O", BC_NO = "B1;",
            IN_DATETIME = "2026-09-21 10:00:00.000", CHECK_RESULT = "0",
        });
        _plc.PresetString("R17001", json);
        _plc.PresetWord("R17000", 1);

        using var gw = Gw();
        gw.PollTriggers();

        WaitFor(() => _plc.PeekWord("R17800") == 1, "完成位未置1");
        Assert.Equal("PASS", _plc.PeekString("R17900"));
        Assert.Equal("rm", _plc.PeekString("R17930"));
        Assert.Equal("next", _plc.PeekString("R17960"));
    }

    [Fact(DisplayName = "MES拒绝弹报警+回写STATUS0")]
    public void Gateway_MesReject_Alarm_Status0()
    {
        _nextResp = "{\"STATUS\":\"0\",\"MSG\":\"no such barcode\",\"DATA\":null}";
        string json = JsonConvert.SerializeObject(new MesModels.Api0030Request
        {
            RC_NO = "R1", ACTION = "I", BC_NO = "B9;",
            IN_DATETIME = "2026-09-21 10:00:00.000", CHECK_RESULT = "0",
        });
        _plc.PresetString("R17001", json);
        _plc.PresetWord("R17000", 1);
        string? alarm = null;
        using var gw = Gw();
        gw.AlarmRaised += (m, n) => alarm = m;

        gw.PollTriggers();

        WaitFor(() => _plc.PeekWord("R17800") == 1, "完成位未置1");
        Assert.Equal("0", _plc.PeekString("R17801"));
        WaitFor(() => alarm != null, "报警未触发");
    }

    [Fact(DisplayName = "坏JSON走失败回写STATUS2")]
    public void Gateway_BadJson_WriteFail()
    {
        _plc.PresetString("R17001", "{not json");
        _plc.PresetWord("R17000", 1);
        using var gw = Gw();
        gw.PollTriggers();

        WaitFor(() => _plc.PeekWord("R17800") == 1, "完成位未置1");
        Assert.Equal("2", _plc.PeekString("R17801"));
        Assert.Equal("NG", _plc.PeekString("R17810"));
        Assert.Equal(" ", _plc.PeekString("R17900"));
    }
}
