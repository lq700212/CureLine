// =========================================================================
// 网关扩展测试：0031 拆包/HTTP层失败/空字段占位/单次只处理一个触发
//
// 覆盖老 MultiGatewayTests 没碰到的分支：
// ① API0031 DATA 拆三段（BC_NO/+30 CHECK_RESULT/+60 REMARK）；
// ② HTTP 500 → WriteFail（STATUS=2/MSG=NG）；③ URL 未配 → WriteFail；
// ④ STATUS/MSG 空 → " " 占位；⑤ 双触发同时置1 → 一次只处理第一个；
// ⑥ ValidateJson 全 6 接口正/反例（用 PlcScenarios 标准报文）。
// 桩 MES + 内存 PLC（TestScope 隔离全局），不碰真机真网。
// =========================================================================

using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;
using PlcMesBridge.Core.Modes;
using PlcMesBridge.Tests.Mocks;

namespace PlcMesBridge.Tests;

public class GatewayExtendedTests : IDisposable
{
    private readonly TestScope _scope;
    private readonly SimulatedPlcClient _plc;

    public GatewayExtendedTests()
    {
        _scope = new TestScope();
        _plc = new SimulatedPlcClient();
        _plc.Connect("127.0.0.1", 6060);

        // 6 触发 + 6 地址束全部显式配置（不依赖全局残留）。
        string[] triggers = { "R13000", "R16000", "R17000", "R18000", "R19000", "R12000" };
        string[] datas = { "R13900", "R16900", "R17900", "R18900", "R19900", "R12900" };
        MesConfig.TriggerApi0027 = triggers[0]; MesConfig.TriggerApi0028 = triggers[1];
        MesConfig.TriggerApi0030 = triggers[2]; MesConfig.TriggerApi0031 = triggers[3];
        MesConfig.TriggerApi0032 = triggers[4]; MesConfig.TriggerApi0033 = triggers[5];
        for (int i = 0; i < 6; i++)
        {
            // 地址束约定：DATA=Rxx900，STATUS=Rxx801，MSG=Rxx810，完成=Rxx800。
            var s = MesConfig.ApiAddresses[i];
            s.MesStatus = datas[i].Replace("900", "801");
            s.MesMsg = datas[i].Replace("900", "810");
            s.MesData = datas[i];
            s.MesComplete = datas[i].Replace("900", "800");
        }
        MesConfig.ApiAddresses[0].PlcData = "R13001";
        MesConfig.ApiAddresses[1].PlcData = "R16001";
        MesConfig.ApiAddresses[2].PlcData = "R17001";
        MesConfig.ApiAddresses[3].PlcData = "R18001";
        MesConfig.ApiAddresses[4].PlcData = "R19001";
        MesConfig.ApiAddresses[5].PlcData = "R12001";
        MesConfig.DataLength = 799;
        MesConfig.DataSegmentOffsets = new[] { 30, 60 }; // 防其它用例改偏移的静态残留
    }

    public void Dispose()
    {
        _plc.Dispose();
        _scope.Dispose();
    }

    private StationGateway Gw() => new(0, _ => _plc);

    private void WaitComplete(string completeAddr)
    {
        for (int i = 0; i < 100 && _plc.PeekWord(completeAddr) != 1; i++)
            Thread.Sleep(100);
        Assert.Equal(1, _plc.PeekWord(completeAddr));
    }

    [Fact(DisplayName = "0031 DATA拆三段")]
    public void Data31_Split3()
    {
        using var mes = new MesStubServer();
        mes.EnqueueOk(dataJson: PlcScenarios.Data31("B1", "1", "rm31"));
        MesConfig.UrlApi0031 = mes.Url;
        _plc.PresetString("R18001", PlcScenarios.Api0031Json());
        _plc.PresetWord("R18000", 1);

        using var gw = Gw();
        gw.PollTriggers();
        WaitComplete("R18800");

        Assert.Equal("B1", _plc.PeekString("R18900"));
        Assert.Equal("1", _plc.PeekString("R18930"));
        Assert.Equal("rm31", _plc.PeekString("R18960"));
        Assert.Equal("1", _plc.PeekString("R18801"));
    }

    [Fact(DisplayName = "HTTP500走失败回写STATUS2")]
    public void Http500_WriteFail_Status2()
    {
        using var mes = new MesStubServer();
        mes.EnqueueHttpError();
        MesConfig.UrlApi0030 = mes.Url;
        _plc.PresetString("R17001", PlcScenarios.Api0030Json());
        _plc.PresetWord("R17000", 1);

        using var gw = Gw();
        gw.PollTriggers();
        WaitComplete("R17800");

        Assert.Equal("2", _plc.PeekString("R17801"));
        Assert.Equal("NG", _plc.PeekString("R17810"));
        Assert.Equal(" ", _plc.PeekString("R17900"));
        Assert.Equal(0, _plc.PeekWord("R17000"));
    }

    [Fact(DisplayName = "URL未配走失败回写")]
    public void EmptyUrl_WriteFail()
    {
        MesConfig.UrlApi0032 = "";
        _plc.PresetString("R19001", PlcScenarios.Api0032Json());
        _plc.PresetWord("R19000", 1);

        using var gw = Gw();
        gw.PollTriggers();
        WaitComplete("R19800");

        Assert.Equal("2", _plc.PeekString("R19801"));
    }

    [Fact(DisplayName = "空STATUS空MSG写空格占位")]
    public void EmptyStatusMsg_BlankPlaceholder()
    {
        using var mes = new MesStubServer();
        mes.Enqueue(200, "{\"STATUS\":\"\",\"MSG\":\"\",\"DATA\":null}");
        MesConfig.UrlApi0030 = mes.Url;
        _plc.PresetString("R17001", PlcScenarios.Api0030Json());
        _plc.PresetWord("R17000", 1);

        using var gw = Gw();
        gw.PollTriggers();
        WaitComplete("R17800");

        Assert.Equal(" ", _plc.PeekString("R17801"));
        Assert.Equal(" ", _plc.PeekString("R17810"));
        Assert.Equal(" ", _plc.PeekString("R17900"));
    }

    [Fact(DisplayName = "双触发一次只处理第一个")]
    public void TwoTriggers_HandlesFirstOnly()
    {
        MesConfig.UrlApi0027 = ""; // 首个走 WriteFail（快，不碰网）
        _plc.PresetString("R13001", PlcScenarios.Api0027Json());
        _plc.PresetWord("R13000", 1);
        _plc.PresetString("R17001", PlcScenarios.Api0030Json());
        _plc.PresetWord("R17000", 1);

        using var gw = Gw();
        gw.PollTriggers();
        WaitComplete("R13800"); // 0027 的完成位

        Assert.Equal(0, _plc.PeekWord("R13000")); // 0027 已清
        Assert.Equal(1, _plc.PeekWord("R17000")); // 0030 留到下次扫描
        Assert.Equal(0, _plc.PeekWord("R17800"));
    }

    [Fact(DisplayName = "无触发直接返回")]
    public void NoTrigger_NoWrite()
    {
        using var gw = Gw();
        gw.PollTriggers();
        Thread.Sleep(300); // Task 不应被创建：完成位全 0
        foreach (string c in new[] { "R13800", "R16800", "R17800", "R18800", "R19800", "R12800" })
            Assert.Equal(0, _plc.PeekWord(c));
    }

    [Fact(DisplayName = "ValidateJson全接口正反例")]
    public void ValidateJson_AllApis()
    {
        Assert.True(StationGateway.ValidateJson("API0027", PlcScenarios.Api0027Json()));
        Assert.True(StationGateway.ValidateJson("API0028", PlcScenarios.Api0028Json()));
        Assert.True(StationGateway.ValidateJson("API0030", PlcScenarios.Api0030Json()));
        Assert.True(StationGateway.ValidateJson("API0031", PlcScenarios.Api0031Json()));
        Assert.True(StationGateway.ValidateJson("API0032", PlcScenarios.Api0032Json()));
        Assert.True(StationGateway.ValidateJson("API0033", PlcScenarios.Api0033Json()));
        // 错配：数组送对象接口 / 对象送数组接口
        Assert.False(StationGateway.ValidateJson("API0027", PlcScenarios.Api0030Json()));
        Assert.False(StationGateway.ValidateJson("API0030", PlcScenarios.Api0027Json()));
        Assert.False(StationGateway.ValidateJson("API0033", PlcScenarios.Api0032Json()));
        Assert.False(StationGateway.ValidateJson("API0031", "{bad"));
    }

    [Fact(DisplayName = "WriteDataSegment空值占位")]
    public void WriteDataSegment_Blank_OnEmpty()
    {
        using var gw = Gw();
        gw.WriteDataSegment("R17900", null, "", null);
        Assert.Equal(" ", _plc.PeekString("R17900"));
        Assert.Equal(" ", _plc.PeekString("R17930"));
        Assert.Equal(" ", _plc.PeekString("R17960"));
    }

    [Fact(DisplayName = "WriteDataSegment自定义偏移")]
    public void WriteDataSegment_CustomOffsets()
    {
        // V0.0.22 分段偏移可配（W68 终版预备）：改 {45,90} → 拆写走 DATA/+45/+90，
        // 缺省 30/60 的旧地址不该被碰；finally 恢复缺省防污染其它用例。
        try
        {
            MesConfig.DataSegmentOffsets = new[] { 45, 90 };
            using var gw = Gw();
            gw.WriteDataSegment("R17900", "A", "B", "C");
            Assert.Equal("A", _plc.PeekString("R17900"));
            Assert.Equal("B", _plc.PeekString("R17945"));
            Assert.Equal("C", _plc.PeekString("R17990"));
            Assert.Equal("", _plc.PeekString("R17930")); // 旧偏移地址没写过（模拟器未写=空串）
        }
        finally
        {
            MesConfig.DataSegmentOffsets = new[] { 30, 60 };
        }
    }

    [Fact(DisplayName = "RequiredFields为5接口0033字段归调试窗")]
    public void RequiredFields_Five_DebugOwns0033()
    {
        // 说明：Core 只记 16 记忆字段的 5 接口；API0033 另有 13 个专有字段
        // （BOARD_SN 等，不在 mes_params.json 记忆范围），全字段表在
        // MesDebugWindow.Required0033。此处锁定分工，防两边各改各的分叉。
        Assert.Equal(5, MesParamStore.RequiredFields.Count);
        Assert.False(MesParamStore.RequiredFields.ContainsKey("API0033"));
    }

    [Fact(DisplayName = "失败网关不进Stations且已释放")]
    public void ConnectAll_Fail_NotInStations()
    {
        for (int i = 1; i < 8; i++) AppConfig.PlcUse[i] = "0";
        AppConfig.PlcIps[0] = "fail";
        using var mgr = new MultiMachineManager(_ => new SimulatedPlcClient());
        int broken = -1;
        mgr.StationBroken += id => broken = id;
        var (ok, skipped) = mgr.ConnectAll();
        Assert.Equal(0, ok);
        Assert.Equal(7, skipped);
        Assert.Equal(0, broken);
        Assert.Empty(mgr.Stations); // 走查 B4：失败的不进列表
    }
}
