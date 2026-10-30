// =========================================================================
// StationConfig：多机机台连接配置模型 + ini 落盘 + 校验（去现场零写代码的关键件）
//
// 为什么有这个文件：原来多机 IP/端口全写死在代码里，换机房/换寄存器点位
// 就得找开发改代码。收敛成配置对象后，现场只点鼠标：网关配置窗填表 →
// 测试连接变绿 → 保存重启，全程不碰代码不碰记事本。
//
// 三段式（纯逻辑进 Core 可测）：
// ① StationConfig：数据 + 缺省（IP 缺省 127.0.0.1 / 端口 6060 / 全启用；
//    缺省键=老 ini 无新键时行为与原来一致）；
// ② StationConfigStore：ini 自愈读写（缺键回填缺省，见 IniFile）；
// ③ StationConfigValidator：保存前校验（IP 或主机名 / 端口 1..65535 / 接口页
//    寄存器地址 ^[DR]\d+$），错在界面就拦，不进轮询。
//
// ini 落盘位置：[setting] 沿用老键（StationUse/StationIp1..10 跳号沿用）+ 新键
//   StationPorts（8 个逗号串，0..7 号机顺序）；[MES] 6 接口地址束沿用 AppConfig
//   现有键。Phase 2 说明（P2.1）：单机配置段与 ASCII 协议键
//   已随三菱链路删除——真机底座统一 Modbus TCP（见 ModbusStationClient.cs，
//   地址=剥 R/D 取数字=协议地址，无协议分叉故协议键不复存在）。
// =========================================================================

using System.Net;
using System.Text.RegularExpressions;

namespace CureLine.Core.Infrastructure;

public class StationConfig
{
    public bool Enabled { get; set; } = true;
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6060;

    /// <summary>
    /// 机台名（V0.0.32 配置化）：LoadStations 已回填当前生效名（ini 非空用 ini，
    /// 空回落 AppConfig.DefaultStationNames）——保存链直接写回即可，无需空值分支。
    /// 机台名会当日志文件名用（Logs\MESLOG\日期\机台名.txt），校验拦文件名非法字符。
    /// </summary>
    public string Name { get; set; } = "";

    public StationConfig Clone() => (StationConfig)MemberwiseClone();
}

/// <summary>校验结果：空=通过；否则每条 (字段中文键, 原因中文键)，UI 层 Tr 后显示。</summary>
public static class StationConfigValidator
{
    private static readonly Regex AddrRx =
        new(@"^[DR]\d{1,5}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HostRx =
        new(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$", RegexOptions.Compiled);

    /// <summary>机台寄存器字地址（只认 R/D 区：R=Modbus 保持寄存器口径，D 为老三菱遗产兼容；保存时直接拦）。</summary>
    public static bool IsStationAddress(string? s) =>
        AddrRx.IsMatch((s ?? "").Trim());

    public static string NormAddr(string s) => s.Trim().ToUpperInvariant();

    /// <summary>机台名禁止的文件名非法字符（机台名会当日志文件名用，
    /// Windows 文件名禁 \ / : * ? " &lt; &gt; |——写进日志路径会炸或越目录）。</summary>
    private static readonly char[] InvalidFileNameChars =
        { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    public static bool IsStationNameValid(string? s) =>
        !string.IsNullOrWhiteSpace(s) &&
        s.Length <= 50 &&
        s.IndexOfAny(InvalidFileNameChars) < 0;

    public static bool IsIpOrHost(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0)
            return false;
        return IPAddress.TryParse(v, out _) || HostRx.IsMatch(v);
    }

    public static List<(string Field, string Reason)> ValidateStations(
        StationConfig[] cs)
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
            // 机台名（V0.0.32 配置化）：非空 + ≤50 字 + 无文件名非法字符
            // （名会当日志文件名与机台面板标题用）。
            if (!IsStationNameValid(cs[i].Name))
                errs.Add((tag + "设备名", "设备名无效"));
        }
        return errs;
    }
}

public static class StationConfigStore
{
    /// <summary>多机 IP 跳号键（历史沿用，对应 0..7 号机，见 AppConfig）。</summary>
    internal static readonly string[] IpKeys =
        { "StationIp1", "StationIp2", "StationIp3", "StationIp4", "StationIp5", "StationIp6", "StationIp7", "StationIp8" };

    /// <summary>读 8 台配置（IP 键跳号沿用；启用/端口是新键，缺省全启用/6060）；
    /// 机台名走 [StationNames] 节键 1..8（V0.0.32），缺键/空值逐台回落缺省名。</summary>
    public static StationConfig[] LoadStations(string iniPath)
    {
        string use = IniFile.ReadStr(iniPath, "setting", "StationUse", "1,1,1,1,1,1,1,1");
        string[] useParts = use.Split(',');
        string ports = IniFile.ReadStr(iniPath, "setting", "StationPorts",
            "6060,6060,6060,6060,6060,6060,6060,6060");
        string[] portParts = ports.Split(',');
        var cs = new StationConfig[8];
        for (int i = 0; i < 8; i++)
        {
            string name = IniFile.ReadStr(iniPath, "StationNames", (i + 1).ToString(), "");
            cs[i] = new StationConfig
            {
                Enabled = (i < useParts.Length ? useParts[i].Trim() : "1") == "1",
                Ip = IniFile.ReadStr(iniPath, "setting", IpKeys[i], "127.0.0.1"),
                Port = int.TryParse(i < portParts.Length ? portParts[i].Trim() : "",
                    out int p) && p is >= 1 and <= 65535 ? p : 6060,
                Name = name.Trim().Length > 0 ? name.Trim() : AppConfig.DefaultStationNames[i],
            };
        }
        return cs;
    }

    /// <summary>存 8 台配置（IP 仍写跳号键，老 ini/老版本程序互认；机台名写
    /// [StationNames] 1..8——保存链给的永远是回填后的生效名，不产生空键）。</summary>
    public static void SaveStations(string iniPath, StationConfig[] cs)
    {
        IniFile.Write(iniPath, "setting", "StationUse",
            string.Join(",", cs.Select(c => c.Enabled ? "1" : "0")));
        for (int i = 0; i < 8 && i < cs.Length; i++)
        {
            IniFile.Write(iniPath, "setting", IpKeys[i], (cs[i].Ip ?? "").Trim());
            IniFile.Write(iniPath, "StationNames", (i + 1).ToString(), (cs[i].Name ?? "").Trim());
        }
        IniFile.Write(iniPath, "setting", "StationPorts",
            string.Join(",", cs.Select(c => c.Port.ToString())));
    }
}
