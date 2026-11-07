// =========================================================================
// ScannerOptions：扫码枪配置（程序①专用，config.ini [Scanner] 节）
//
// 干什么：把扫码枪的串口参数与扫码会话超时集中到一处，供主窗启动装配。
// 为什么独立成类不塞 AppConfig：扫码枪是程序①的业务外设（程序②整线网关
//   不用），参数按"现场换枪/换口"的低频口径设计——界面入口在
//   "设置→扫码枪配置"（dev 开发者专属弹窗，保存即热生效），
//   也可直接改 config.ini（自愈语义与本类 Load/Save 同源）。
//
// 现场接线（SH-5000-GHD 扫码枪 + 网口转 USB 转接线）：
//   该枪为"网口输出"，因工控机网口不够用，经"网口转 USB 转接线"接入
//   工控机——系统里表现为**虚拟串口**（USB Serial，设备管理器"端口
//   (COM 和 LPT)"下多出的 COM 号）。上位机按串口读码，不是 TCP——
//   所以本配置只有串口参数。
//
// 串口定位两种方式（二选一，PortName 优先）：
//   ① PortName 填设备管理器里看到的 COM 号（如 "COM3"）——最直接，换 USB 口要改；
//   ② PortName 留空 + DeviceKeyword 填设备管理器设备名里的关键字（如 "CH340"/
//      "Prolific"/"USB-Serial"）→ 服务用 WMI 自动找口，换 USB 口不用改配置。
//   两者都空 = 未配置扫码枪：程序照常运行，只是不读码（日志提示一次）。
//
// 怎么改：新增配置项 = 属性 + Load/Save 两处 + config.sample.ini 同步；
//   ini 键名勿改（现场已按样例部署）。
// =========================================================================

using CureLine.Core.Infrastructure;

namespace CureLine.Core.Comms;

/// <summary>扫码枪串口配置（ini [Scanner] 节的内存映像）。</summary>
public sealed class ScannerOptions
{
    /// <summary>
    /// 是否启用扫码枪。缺省 false——现场没接枪时程序不尝试开串口，
    /// 避免"每次启动都报串口打不开"的噪声；接好枪后在 ini 改 1。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 固定串口名（如 "COM3"）。填了就固定用该端口；留空则按
    /// <see cref="DeviceKeyword"/> 用 WMI 自动识别（换 USB 口免改配置）。
    /// </summary>
    public string PortName { get; set; } = "";

    /// <summary>
    /// 设备识别关键词（PortName 留空时生效）：设备管理器里设备名称包含的
    /// 关键字（如 USB 转串口芯片 "CH340"/"Prolific"/"USB-Serial"）。
    /// 留空且 PortName 也空 = 未配置扫码枪，不开串口。
    /// </summary>
    public string DeviceKeyword { get; set; } = "";

    /// <summary>波特率（缺省 9600——扫码枪常见出厂值；按扫码枪手册核对）。</summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>数据位（缺省 8，扫码枪标准 8N1 口径）。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位字符串（项目约定 "1"/"15"/"2"，缺省 "1"）。</summary>
    public string StopBits { get; set; } = "1";

    /// <summary>校验位（标准枚举名 None/Odd/Even/Mark/Space，缺省 None）。</summary>
    public string Parity { get; set; } = "None";

    /// <summary>
    /// 扫码会话超时秒数（扫了库位码后等产品码的最长间隔，缺省 15）：
    /// 超时自动回到"请扫库位码"，防止界面挂在旧库位误导下一名操作员。
    /// 现场扫码节奏慢可调大，范围 5~600（过小会在慢慢找产品码时被打断）。
    /// </summary>
    public int SessionTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 心跳调试日志开关（排障用，缺省 false）：true 时子模块每个心跳周期
    /// 把 WMI 串口搜索结果打进操作日志，用于排查"插着枪却识别不到"。
    /// </summary>
    public bool DebugLog { get; set; }

    /// <summary>
    /// 是否配置了可用的串口定位方式（PortName 或 DeviceKeyword 至少一个非空）。
    /// 未配置时主窗显示"扫码未配置"，不启动串口线程。
    /// </summary>
    public bool HasPortTarget =>
        !string.IsNullOrWhiteSpace(PortName) || !string.IsNullOrWhiteSpace(DeviceKeyword);

    /// <summary>规范化后的波特率（越界回缺省 9600——坏值不喂给串口驱动）。</summary>
    public int NormBaudRate => BaudRate is >= 2400 and <= 921600 ? BaudRate : 9600;

    /// <summary>规范化后的数据位（5~8，越界回 8）。</summary>
    public int NormDataBits => DataBits is >= 5 and <= 8 ? DataBits : 8;

    /// <summary>规范化后的超时秒数（5~600，越界回 15）。</summary>
    public int NormSessionTimeoutSeconds =>
        SessionTimeoutSeconds is >= 5 and <= 600 ? SessionTimeoutSeconds : 15;

    /// <summary>停止位规范化：只认 "1"/"15"/"2"，其它回 "1"（与子模块约定同口径）。</summary>
    public string NormStopBits => StopBits.Trim() switch
    {
        "15" => "15",
        "2" => "2",
        _ => "1",
    };

    /// <summary>校验位规范化：只认标准枚举名，其它回 None（与子模块约定同口径）。</summary>
    public string NormParity => Parity.Trim().ToLowerInvariant() switch
    {
        "even" => "Even",
        "odd" => "Odd",
        "mark" => "Mark",
        "space" => "Space",
        _ => "None",
    };

    // ---------------- ini 存取（缺键回填缺省，IniFile 自愈） ----------------

    private const string Section = "Scanner";

    /// <summary>从 config.ini 读（缺省值见各属性；非法数值在 Norm* 属性里兜底）。</summary>
    public static ScannerOptions Load(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        return new ScannerOptions
        {
            Enabled = IniFile.ReadStr(ini, Section, "Enabled", "0") == "1",
            PortName = IniFile.ReadStr(ini, Section, "PortName", "").Trim(),
            DeviceKeyword = IniFile.ReadStr(ini, Section, "DeviceKeyword", "").Trim(),
            BaudRate = IniFile.ReadInt(ini, Section, "BaudRate", 9600),
            DataBits = IniFile.ReadInt(ini, Section, "DataBits", 8),
            StopBits = IniFile.ReadStr(ini, Section, "StopBits", "1").Trim(),
            Parity = IniFile.ReadStr(ini, Section, "Parity", "None").Trim(),
            SessionTimeoutSeconds = IniFile.ReadInt(ini, Section, "SessionTimeoutSeconds", 15),
            DebugLog = IniFile.ReadStr(ini, Section, "DebugLog", "0") == "1",
        };
    }

    /// <summary>写回 config.ini（扫码枪配置窗保存的唯一落盘口；测试也走这里）。</summary>
    public void Save(string? iniPath = null)
    {
        string ini = iniPath ?? AppPaths.ConfigIni;
        IniFile.Write(ini, Section, "Enabled", Enabled ? "1" : "0");
        IniFile.Write(ini, Section, "PortName", PortName.Trim());
        IniFile.Write(ini, Section, "DeviceKeyword", DeviceKeyword.Trim());
        IniFile.Write(ini, Section, "BaudRate", NormBaudRate.ToString());
        IniFile.Write(ini, Section, "DataBits", NormDataBits.ToString());
        IniFile.Write(ini, Section, "StopBits", NormStopBits);
        IniFile.Write(ini, Section, "Parity", NormParity);
        IniFile.Write(ini, Section, "SessionTimeoutSeconds", NormSessionTimeoutSeconds.ToString());
        IniFile.Write(ini, Section, "DebugLog", DebugLog ? "1" : "0");
    }
}
