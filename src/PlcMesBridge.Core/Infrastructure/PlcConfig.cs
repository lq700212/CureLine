// =========================================================================
// PlcConfig：多机 PLC 配置模型 + ini 落盘 + 校验（去现场零写代码的关键件）
//
// 为什么有这个文件：原来多机 IP/端口/协议全写死在代码里，换机房/换 PLC 点位
// 就得找开发改代码。收敛成配置对象后，现场只点鼠标：PLC配置窗填表 →
// 测试连接变绿 → 保存重启，全程不碰代码不碰记事本。
//
// 三段式（纯逻辑进 Core 可测）：
// ① StationPlcConfig：数据 + 缺省（IP 缺省 127.0.0.1 / 端口 6060 / 全启用；
//    缺省键=老 ini 无新键时行为与原来一致）；
// ② PlcConfigStore：ini 自愈读写（缺键回填缺省，见 IniFile）；
// ③ PlcConfigValidator：保存前校验（IP 或主机名 / 端口 1..65535 / 接口页
//    PLC 地址 ^[DR]\d+$），错在界面就拦，不进轮询。
//
// ini 落盘位置：[setting] 沿用老键（PLCuse/PLCIP2..10 跳号沿用）+ 新键
//   PLCPorts（8 个逗号串，0..7 号机顺序）；[MES] 6 接口地址束沿用 AppConfig
//   现有键。Phase 2 说明（P2.1）：单机 [SinglePLC] 段与 ASCII 协议键（PLCAscii）
//   已随三菱链路删除——真机底座统一 Modbus TCP（见 ModbusStationClient.cs，
//   地址=剥 R/D 取数字=协议地址，无协议分叉故协议键不复存在）。
// =========================================================================

using System.Net;
using System.Text.RegularExpressions;

namespace PlcMesBridge.Core.Infrastructure;

public class StationPlcConfig
{
    public bool Enabled { get; set; } = true;
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6060;

    public StationPlcConfig Clone() => (StationPlcConfig)MemberwiseClone();
}

/// <summary>校验结果：空=通过；否则每条 (字段中文键, 原因中文键)，UI 层 Tr 后显示。</summary>
public static class PlcConfigValidator
{
    private static readonly Regex AddrRx =
        new(@"^[DR]\d{1,5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HostRx =
        new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    /// <summary>PLC 字地址（只认 D/R 区：X/Y 是位区，读字会失败，保存时直接拦）。</summary>
    public static bool IsPlcAddress(string? s) =>
        AddrRx.IsMatch((s ?? "").Trim());

    public static string NormAddr(string s) => s.Trim().ToUpperInvariant();

    public static bool IsIpOrHost(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0)
            return false;
        return IPAddress.TryParse(v, out _) || HostRx.IsMatch(v);
    }

    public static List<(string Field, string Reason)> ValidateStations(
        StationPlcConfig[] cs)
    {
        var errs = new List<(string, string)>();
        for (int i = 0; i < cs.Length; i++)
        {
            if (!cs[i].Enabled)
                continue; // 不启用的机台不校验（省得现场为备用机填假 IP 还被拦）。
            string tag = $"{i + 1}号机";
            if (!IsIpOrHost(cs[i].Ip))
                errs.Add((tag + "IP地址", "IP地址无效"));
            if (cs[i].Port is < 1 or > 65535)
                errs.Add((tag + "端口", "端口范围1-65535"));
        }
        return errs;
    }
}

public static class PlcConfigStore
{
    /// <summary>多机 IP 跳号键（历史沿用，对应 0..7 号机，见 AppConfig）。</summary>
    internal static readonly string[] IpKeys =
        { "PLCIP2", "PLCIP3", "PLCIP4", "PLCIP5", "PLCIP6", "PLCIP7", "PLCIP9", "PLCIP10" };

    /// <summary>读 8 台配置（IP 键跳号沿用；启用/端口是新键，缺省全启用/6060）。</summary>
    public static StationPlcConfig[] LoadStations(string iniPath)
    {
        string use = IniFile.ReadStr(iniPath, "setting", "PLCuse", "1,1,1,1,1,1,1,1");
        string[] useParts = use.Split(',');
        string ports = IniFile.ReadStr(iniPath, "setting", "PLCPorts",
            "6060,6060,6060,6060,6060,6060,6060,6060");
        string[] portParts = ports.Split(',');
        var cs = new StationPlcConfig[8];
        for (int i = 0; i < 8; i++)
        {
            cs[i] = new StationPlcConfig
            {
                Enabled = (i < useParts.Length ? useParts[i].Trim() : "1") == "1",
                Ip = IniFile.ReadStr(iniPath, "setting", IpKeys[i], "127.0.0.1"),
                Port = int.TryParse(i < portParts.Length ? portParts[i].Trim() : "",
                    out int p) && p is >= 1 and <= 65535 ? p : 6060,
            };
        }
        return cs;
    }

    /// <summary>存 8 台配置（IP 仍写跳号键，老 ini/老版本程序互认）。</summary>
    public static void SaveStations(string iniPath, StationPlcConfig[] cs)
    {
        IniFile.Write(iniPath, "setting", "PLCuse",
            string.Join(",", cs.Select(c => c.Enabled ? "1" : "0")));
        for (int i = 0; i < 8 && i < cs.Length; i++)
            IniFile.Write(iniPath, "setting", IpKeys[i], (cs[i].Ip ?? "").Trim());
        IniFile.Write(iniPath, "setting", "PLCPorts",
            string.Join(",", cs.Select(c => c.Port.ToString())));
    }
}
