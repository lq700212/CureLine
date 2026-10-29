// =========================================================================
// MES 层测试：参数记忆 / 配置映射 / 网关 JSON 校验 / 分段地址算法
// 不碰网络（MesHttpClient 端到端另起本地桩，见 MultiGatewayTests）。
// =========================================================================

using Newtonsoft.Json;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Core.Modes;

namespace CureLine.Tests;

public class MesLayerTests : IDisposable
{
    private readonly string _tmp;

    public MesLayerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "PlcRdMes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    [Fact(DisplayName = "MES参数保存加载往返")]
    public void Params_SaveLoad_RoundTrip()
    {
        string f = Path.Combine(_tmp, "mes_params.json");
        MesParamStore.RC_NO = "RC123";
        MesParamStore.CHECK_RESULT = "1";
        MesParamStore.SaveParams(f);
        MesParamStore.RC_NO = "";
        MesParamStore.CHECK_RESULT = "0";
        MesParamStore.LoadParams(f);
        Assert.Equal("RC123", MesParamStore.RC_NO);
        Assert.Equal("1", MesParamStore.CHECK_RESULT);
        MesParamStore.RC_NO = "";
        MesParamStore.CHECK_RESULT = "0";
    }

    [Fact(DisplayName = "各接口字段表完整")]
    public void RequiredFields_Cover_AllApis()
    {
        Assert.Equal(5, MesParamStore.RequiredFields.Count);
        Assert.Contains("RC_NO", MesParamStore.RequiredFields["API0027"]);
        Assert.Contains("ACTION", MesParamStore.RequiredFields["API0030"]);
        Assert.Contains("BO_NUMBER", MesParamStore.RequiredFields["API0031"]);
        // 16 字段全在 AllFields 登记
        Assert.Equal(16, MesParamStore.AllFields.Length);
    }

    [Fact(DisplayName = "配置URL与触发映射")]
    public void Config_Url_Trigger_Map()
    {
        MesConfig.UrlApi0030 = "http://x/30";
        MesConfig.TriggerApi0033 = "R12000";
        Assert.Equal("http://x/30", MesConfig.GetUrl("API0030"));
        Assert.Equal("R12000", MesConfig.GetTrigger("API0033"));
        Assert.Equal(string.Empty, MesConfig.GetUrl("API9999"));
    }

    [Fact(DisplayName = "网关JSON校验：数组与对象区分")]
    public void ValidateJson_ArrayVsObject()
    {
        string arr27 = JsonConvert.SerializeObject(new[]
            { new MesModels.Api0027Request { RC_NO = "R" } });
        string obj30 = JsonConvert.SerializeObject(
            new MesModels.Api0030Request { RC_NO = "R", ACTION = "I" });
        Assert.True(StationGateway.ValidateJson("API0027", arr27));
        Assert.True(StationGateway.ValidateJson("API0030", obj30));
        // 类型错配：对象送数组接口→反序列化抛→false
        Assert.False(StationGateway.ValidateJson("API0027", obj30));
        Assert.False(StationGateway.ValidateJson("API0030", ""));
        Assert.False(StationGateway.ValidateJson("APIX", "{}"));
    }

    [Fact(DisplayName = "分段地址算法无补零")]
    public void ShiftAddr_NoPadding()
    {
        Assert.Equal("R17930", StationGateway.ShiftAddr("R17900", 30));
        Assert.Equal("R17960", StationGateway.ShiftAddr("R17900", 60));
        Assert.Equal("D2910", StationGateway.ShiftAddr("D2880", 30));
    }

    [Fact(DisplayName = "BaseResponse STATUS字符串语义")]
    public void BaseResponse_Status_String()
    {
        var ok = JsonConvert.DeserializeObject<MesModels.BaseResponse>(
            "{\"STATUS\":\"1\",\"MSG\":\"ok\",\"DATA\":null}");
        Assert.Equal("1", ok!.STATUS);
        var ng = JsonConvert.DeserializeObject<MesModels.BaseResponse>(
            "{\"STATUS\":\"0\",\"MSG\":\"bad\",\"DATA\":{\"x\":1}}");
        Assert.Equal("0", ng!.STATUS);
    }
}
