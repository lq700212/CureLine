// =========================================================================
// MesConfig：MES 全局配置（复刻自 MES_Core.vb 的 MESConfig Module）
//
// 两部分：① 6 个接口 URL（config.ini [MES] Url_API00xx，缺省 192.168.120.129:5012）；
// ② 多机网关地址束（触发/数据/状态/MSG/DATA/完成，R 系地址，可配）。
// 单机模式只用 URL；多机模式还要地址束。默认值 = 老项目硬编码初值，1:1。
// =========================================================================

namespace CureLine.Core.Mes;

public static class MesConfig
{
    public static string UrlApi0027 { get; set; } = "";
    public static string UrlApi0028 { get; set; } = "";
    public static string UrlApi0030 { get; set; } = "";
    public static string UrlApi0031 { get; set; } = "";
    public static string UrlApi0032 { get; set; } = "";
    public static string UrlApi0033 { get; set; } = "";

    public static string TriggerApi0027 { get; set; } = "";
    public static string TriggerApi0028 { get; set; } = "";
    public static string TriggerApi0030 { get; set; } = "";
    public static string TriggerApi0031 { get; set; } = "";
    public static string TriggerApi0032 { get; set; } = "";
    public static string TriggerApi0033 { get; set; } = "";

    /// <summary>机台寄存器里 JSON 报文最大字长（老项目 DataLength=799）。</summary>
    public static int DataLength { get; set; } = 799;

    /// <summary>
    /// API0030/0031 返回 DATA 三段拆写的偏移（base+off1=第2段、base+off2=第3段；
    /// 老项目硬编码 30/60，V0.0.22 配置化——W68 终版偏移不同改 ini 即可零代码适配）。
    /// AppConfig.Load 保证恒有 ≥2 个合法值（非法配置整体回缺省 30/60）。
    /// </summary>
    public static int[] DataSegmentOffsets { get; set; } = { 30, 60 };

    /// <summary>
    /// 单个接口的机台寄存器地址束（复刻自老项目 Addressinfo.vb 的 5 字段）。
    /// StationData=机台准备好的 JSON；status/msg/data=MES 返回回写；complete=完成标志。
    /// </summary>
    public class ApiAddressSet
    {
        public string StationData { get; set; } = "";
        public string MesMsg { get; set; } = "";
        public string MesData { get; set; } = "";
        public string MesStatus { get; set; } = "";
        public string MesComplete { get; set; } = "";
    }

    /// <summary>6 个接口的地址束，按 API0027/0028/0030/0031/0032/0033 顺序排。</summary>
    public static ApiAddressSet[] ApiAddresses { get; } = new ApiAddressSet[6];

    static MesConfig()
    {
        for (int i = 0; i < ApiAddresses.Length; i++)
            ApiAddresses[i] = new ApiAddressSet();
    }

    /// <summary>按接口码取 URL（调试窗/网关共用）。未知返回空串。</summary>
    public static string GetUrl(string apiCode) => apiCode switch
    {
        "API0027" => UrlApi0027,
        "API0028" => UrlApi0028,
        "API0030" => UrlApi0030,
        "API0031" => UrlApi0031,
        "API0032" => UrlApi0032,
        "API0033" => UrlApi0033,
        _ => string.Empty,
    };

    /// <summary>按接口码取触发地址。未知返回空串。</summary>
    public static string GetTrigger(string apiCode) => apiCode switch
    {
        "API0027" => TriggerApi0027,
        "API0028" => TriggerApi0028,
        "API0030" => TriggerApi0030,
        "API0031" => TriggerApi0031,
        "API0032" => TriggerApi0032,
        "API0033" => TriggerApi0033,
        _ => string.Empty,
    };

    /// <summary>6 接口码常量（顺序与 ApiAddresses 下标一致：0027=0 … 0033=5）。</summary>
    public static readonly string[] ApiCodes =
        { "API0027", "API0028", "API0030", "API0031", "API0032", "API0033" };
}
