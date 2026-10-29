// =========================================================================
// AppConfig：启动配置加载（多机网关口径，集中一处）
//
// 多机：PLCuse 开关串 / PLCIP2..10 跳号 IP / PLCPorts 端口串 / 存图目录 /
//   6 URL + 6 触发地址 + DataLength + 6 组地址束（xxxPLCDataAddr/MESMSGAddr/
//   MESDataAddr/MESSTATUSAddr/MESCompleteAddr，缺省 R 系，见 MultiMachineGateway
//   注释）。
// Phase 2 说明（P2.1）：单机模式已随三菱链路整链删除——RunMode 枚举 /
//   [setting] Mode 键 / [SinglePLC] 段 / PLCAscii 协议键一并撤下（git 历史可
//   找回）。现场 ini 里残留旧键无害：本类只读本文件列出的键，多余键不解析
//   也不清理（kernel32 ini 自愈按缺省回填本类用到的键）。
// =========================================================================

using PlcMesBridge.Core.Mes;

namespace PlcMesBridge.Core.Infrastructure;

public static class AppConfig
{
    public static string[] PlcUse { get; private set; } =
        new[] { "1", "1", "1", "1", "1", "1", "1", "1" };

    /// <summary>8 台 PLC IP（键 PLCIP2/3/4/5/6/7/9/10，老项目跳号命名原样）。</summary>
    public static string[] PlcIps { get; private set; } = new string[8];

    /// <summary>8 台端口（新键 [setting] PLCPorts，缺省全 6060；现场按 Modbus 从站改）。</summary>
    public static int[] StationPorts { get; private set; } =
        new[] { 6060, 6060, 6060, 6060, 6060, 6060, 6060, 6060 };

    /// <summary>
    /// 网关触发扫描周期 ms（键 [setting] ScanIntervalMs，缺省 200=老项目口径；
    /// V0.0.22 配置化，钳制 50~5000——下限防空转打爆 PLC，上限防触发响应过钝）。
    /// 网关循环每次 Sleep 读本值，配置窗保存即热生效（无需重连）。
    /// setter 为 public 仅供 TestScope 快照恢复（与 MesConfig.DataLength 同风格）；
    /// 产品代码只有 AppConfig.Load 应写本值。
    /// </summary>
    public static int ScanIntervalMs { get; set; } = 200;

    private static readonly string[] IpKeys = PlcConfigStore.IpKeys;

    private static readonly string[] DefaultUrls =
    {
        "http://192.168.120.129:5012/MacProduction/MacParameters",
        "http://192.168.120.129:5012/MacProduction/InsMachineDAQ",
        "http://192.168.120.129:5012/MacTraceability/BoardInOut",
        "http://192.168.120.129:5012/MacTraceability/Prelogcheck",
        "http://192.168.120.129:5012/MacTraceability/UploadMaterial",
        "http://192.168.120.129:5012/MacTraceability/UploadBadDetail",
    };

    private static readonly string[] DefaultTriggers =
        { "R13000", "R16000", "R17000", "R18000", "R19000", "R12000" };

    private static readonly string[] DefaultPlcData =
        { "R13001", "R16001", "R17001", "R18001", "R19001", "R12001" };
    private static readonly string[] DefaultMsgs =
        { "R13810", "R16810", "R17810", "R18810", "R19810", "R12810" };
    private static readonly string[] DefaultDatas =
        { "R13900", "R16900", "R17900", "R18900", "R19900", "R12900" };
    private static readonly string[] DefaultStatuses =
        { "R13801", "R16801", "R17801", "R18801", "R19801", "R12801" };
    private static readonly string[] DefaultCompletes =
        { "R13800", "R16800", "R17800", "R18800", "R19800", "R12800" };

    /// <summary>加载全部配置（启动时调一次；ini 缺键自动回填缺省，见 IniFile）。</summary>
    public static void Load(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;

        LanguageService.CurrentLanguage = IniFile.ReadStr(ini, "setting", "Language", "CH");

        // 字符串字节序（V0.0.20 配置化，W68 联调预备）：缺省 BigEndian=老项目
        // 口径；CIM 侧按 little-endian 组包时配 SwapBytes（ModbusStationClient
        // 读写串按字节对交换），现场校准零改代码。非法值按缺省 BigEndian。
        Comms.ModbusStationClient.SwapStringBytes =
            IniFile.ReadStr(ini, "setting", "StringByteOrder", "BigEndian")
                .Equals("SwapBytes", StringComparison.OrdinalIgnoreCase);

        // 多机 8 台走 PlcConfigStore（自愈缺省，见 PlcConfig.cs）：启用开关/IP/端口一并带出。
        StationPlcConfig[] stations = PlcConfigStore.LoadStations(ini);
        for (int i = 0; i < 8; i++)
        {
            PlcUse[i] = stations[i].Enabled ? "1" : "0";
            PlcIps[i] = stations[i].Ip;
            StationPorts[i] = stations[i].Port;
        }

        MesConfig.UrlApi0027 = IniFile.ReadStr(ini, "MES", "Url_API0027", DefaultUrls[0]);
        MesConfig.UrlApi0028 = IniFile.ReadStr(ini, "MES", "Url_API0028", DefaultUrls[1]);
        MesConfig.UrlApi0030 = IniFile.ReadStr(ini, "MES", "Url_API0030", DefaultUrls[2]);
        MesConfig.UrlApi0031 = IniFile.ReadStr(ini, "MES", "Url_API0031", DefaultUrls[3]);
        MesConfig.UrlApi0032 = IniFile.ReadStr(ini, "MES", "Url_API0032", DefaultUrls[4]);
        MesConfig.UrlApi0033 = IniFile.ReadStr(ini, "MES", "Url_API0033", DefaultUrls[5]);

        MesConfig.TriggerApi0027 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0027", DefaultTriggers[0]);
        MesConfig.TriggerApi0028 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0028", DefaultTriggers[1]);
        MesConfig.TriggerApi0030 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0030", DefaultTriggers[2]);
        MesConfig.TriggerApi0031 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0031", DefaultTriggers[3]);
        MesConfig.TriggerApi0032 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0032", DefaultTriggers[4]);
        MesConfig.TriggerApi0033 = IniFile.ReadStr(ini, "MES", "TriggerAddrAPI0033", DefaultTriggers[5]);
        // DataLength 必须容错：现场手改 ini 写错一个字符不能让启动崩（老项目无此键，
        // 新项目新增；非法值回填缺省 799，保证网关读 JSON 长度永远可用）。
        string dataLenRaw = IniFile.ReadStr(ini, "MES", "DataLength", "799");
        if (int.TryParse(dataLenRaw, out int dataLen) && dataLen > 0)
        {
            MesConfig.DataLength = dataLen;
        }
        else
        {
            // 非法/非正数：回填缺省再取缺省（与 IniFile 自愈同一思想，见类头）。
            IniFile.Write(ini, "MES", "DataLength", "799");
            MesConfig.DataLength = 799;
        }

        // DATA 分段偏移（V0.0.22 配置化，W68 联调预备）：格式"30,60"逗号分隔，
        // 要求 2~8 段、每段 1~65535（65535 上限=Modbus 协议地址边界，防溢出误写）。
        // 任一非法整体回缺省 30,60 并回写 ini（同 DataLength 自愈口径），保证
        // WriteDataSegment 取 [0]/[1] 永不越界。
        string segRaw = IniFile.ReadStr(ini, "MES", "DataSegmentOffsets", "30,60");
        int[]? segs = ParseSegmentOffsets(segRaw);
        if (segs != null)
        {
            MesConfig.DataSegmentOffsets = segs;
        }
        else
        {
            IniFile.Write(ini, "MES", "DataSegmentOffsets", "30,60");
            MesConfig.DataSegmentOffsets = new[] { 30, 60 };
        }

        // 扫描周期（V0.0.22 配置化）：钳制 50~5000ms，越界回写钳制值（自愈语义），
        // 网关循环每次 Sleep 读静态值，配置窗保存 AppConfig.Load 后即热生效。
        string scanRaw = IniFile.ReadStr(ini, "setting", "ScanIntervalMs", "200");
        if (int.TryParse(scanRaw, out int scan) && scan is >= 50 and <= 5000)
        {
            ScanIntervalMs = scan;
        }
        else
        {
            IniFile.Write(ini, "setting", "ScanIntervalMs", "200");
            ScanIntervalMs = 200;
        }

        string[] names = { "API0027", "API0028", "API0030", "API0031", "API0032", "API0033" };
        for (int i = 0; i < 6; i++)
        {
            var set = MesConfig.ApiAddresses[i];
            set.PlcData = IniFile.ReadStr(ini, "MES", $"{names[i]}PLCDataAddr", DefaultPlcData[i]);
            set.MesMsg = IniFile.ReadStr(ini, "MES", $"{names[i]}MESMSGAddr", DefaultMsgs[i]);
            set.MesData = IniFile.ReadStr(ini, "MES", $"{names[i]}MESDataAddr", DefaultDatas[i]);
            set.MesStatus = IniFile.ReadStr(ini, "MES", $"{names[i]}MESSTATUSAddr", DefaultStatuses[i]);
            set.MesComplete = IniFile.ReadStr(ini, "MES", $"{names[i]}MESCompleteAddr", DefaultCompletes[i]);
        }

        MesParamStore.LoadParams();
    }

    /// <summary>
    /// 解析 DATA 分段偏移串（"30,60"）：2~8 段、每段 1~65535，全合法返回数组；
    /// 任一非法返回 null（调用方整体回缺省——不做部分生效，防现场配一半）。
    /// </summary>
    private static int[]? ParseSegmentOffsets(string raw)
    {
        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 8)
            return null;
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int v) || v is < 1 or > 65535)
                return null;
            values[i] = v;
        }
        return values;
    }
}
