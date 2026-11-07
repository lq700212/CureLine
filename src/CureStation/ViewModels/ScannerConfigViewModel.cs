// =========================================================================
// ScannerConfigViewModel：扫码枪配置窗编排（程序①，dev 开发者专属）
//
// 干什么：把 [Scanner] 节的全部可调参数收进一个弹窗——启用开关、连接方式
//   （Serial 串口 / Tcp 以太网）、串口定位（固定 COM 口 / 设备名关键字自动
//   找口）与串口参数、TCP 目标（IP / 端口 / 触发指令）、扫码会话超时。保存
//   落 config.ini（ScannerOptions.Save），主窗收到 true 后按新参数重建扫码枪
//   （保存即生效，不重启程序）。
// 为什么界面化：现场扫码枪接入方式会演进（V0.0.77=串口「网口输出+网口转
//   USB 转接线→虚拟串口」；V0.0.78 起=TCP「枪侧接 Eport E10 型 DTU 串口转
//   网口，DTU 配 TCP-SERVER 8899，上位机作客户端连入收码」），换 USB 口/
//   换接入方式都让现场在界面里直接改，不用手翻 ini。入口只对 dev 开放
//   （扫码枪属于设备接入参数，与点表配置同级），admin/operator/未登录在
//   设置下拉里直接没有这一项。
// 怎么改：新增配置项 = ScannerOptions 加属性 + Load/Save + 本 VM 三处
//   （字段/Sync/Save）+ XAML 加控件；ini 键名勿改（现场已按样例部署）。
// =========================================================================

using System.IO.Ports;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

public sealed class ScannerConfigViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;

    public ScannerConfigViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        SaveCommand = new RelayCommand(Save);
        DetectPortsCommand = new RelayCommand(DetectPorts);
        var loaded = ScannerOptions.Load();
        SyncFromOptions(loaded);

        // 现场预置（V0.0.79）：**缺省值本身=现场 TCP 口径**（ScannerOptions 缺省
        // 出厂即 Mode=Tcp + 枪侧 DTU IP FieldTcpIp + 8899 + 指令留空）——首次打开
        // 回显就是完整现场形态，这里只把【启用】勾上作提示："从未配过枪"的出厂
        // 形态=未启用 + 串口定位全空 + TCP 目标仍是出厂预置 IP。用户改过任何定位、
        // 或明确清空/改过 TcpIp、或确认过 Enabled=0，一律原样回显不打扰。
        // 只预填不落盘：点"保存配置"才算确认；串口关键字/COM 口不再自动预填
        // （现场已换 TCP 接入，串口是替代路径，由现场切到串口方式后自行填写）。
        if (!EditEnabled && !loaded.HasSerialTarget &&
            string.Equals(loaded.TcpIp?.Trim(), ScannerOptions.FieldTcpIp,
                          StringComparison.OrdinalIgnoreCase))
        {
            EditEnabled = true;
        }
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>检测本机串口（枚举系统 COM 号，填进候选下拉供现场选择）。</summary>
    public RelayCommand DetectPortsCommand { get; }

    /// <summary>保存成功后置 true（测试断言用；View 经 RequestClose 关窗）。</summary>
    public bool CloseDialog { get; private set; }

    /// <summary>保存成功→true / 取消关闭→false（View 转成 DialogResult 关窗）。</summary>
    public event Action<bool>? RequestClose;

    /// <summary>配置文件实际路径（界面显示，方便现场核对/备份）。</summary>
    public string IniPath => AppPaths.ConfigIni;

    /// <summary>最近一次"检测串口"枚举到的本机 COM 号（选择即回填 PortName）。</summary>
    public ObservableCollection<string> DetectedPorts { get; } = new();

    private string? _selectedDetectedPort;
    /// <summary>检测结果下拉选中项：选中即把该 COM 号填进 PortName（固定口优先）。</summary>
    public string? SelectedDetectedPort
    {
        get => _selectedDetectedPort;
        set
        {
            if (!Set(ref _selectedDetectedPort, value) || string.IsNullOrWhiteSpace(value))
                return;
            EditPortName = value;
        }
    }

    // ---------- 下拉候选（现场常见值；波特率可手输任意合法值） ----------
    public int[] BaudRateOptions { get; } = { 4800, 9600, 19200, 38400, 57600, 115200 };
    public int[] DataBitsOptions { get; } = { 5, 6, 7, 8 };

    /// <summary>停止位显示值（保存时 "1.5" 转项目约定的 "15"）。</summary>
    public string[] StopBitsOptions { get; } = { "1", "1.5", "2" };

    /// <summary>校验位（与 System.IO.Ports.Parity 枚举名一致）。</summary>
    public string[] ParityOptions { get; } = { "None", "Odd", "Even", "Mark", "Space" };

    // ---------- 编辑区（打开回显；保存前校验并落盘） ----------

    private bool _editEnabled;
    /// <summary>启用扫码枪（未接枪时保持不勾，程序不会尝试建立连接）。</summary>
    public bool EditEnabled
    {
        get => _editEnabled;
        set => Set(ref _editEnabled, value);
    }

    private bool _editUseTcp;
    /// <summary>
    /// 连接方式=Tcp（勾选=以太网 TCP 读码；不勾=串口读码）。
    /// 对应 ini [Scanner] Mode 键（Tcp/Serial）；切换即刷新上下两个参数区块的显隐。
    /// </summary>
    public bool EditUseTcp
    {
        get => _editUseTcp;
        set
        {
            if (Set(ref _editUseTcp, value))
            {
                // 分区显隐派生属性联动——XAML DataTrigger 按这两个 bool 切区块。
                Raise(nameof(ShowSerialSection));
                Raise(nameof(ShowTcpSection));
            }
        }
    }

    /// <summary>串口区块是否可见（未勾 TCP 时显示；缺省值给 XAML 触发器兜底）。</summary>
    public bool ShowSerialSection => !EditUseTcp;

    /// <summary>TCP 区块是否可见（勾 TCP 时显示）。</summary>
    public bool ShowTcpSection => EditUseTcp;

    private string _editTcpIp = "";
    /// <summary>TCP 目标 IP（枪侧 DTU/枪自身的地址，如 169.254.173.207）。</summary>
    public string EditTcpIp
    {
        get => _editTcpIp;
        set => Set(ref _editTcpIp, value ?? "");
    }

    private string _editTcpPort = "8899";
    /// <summary>TCP 目标端口（字符串编辑，保存时校验 1~65535；现场 DTU TCP-SERVER 8899）。</summary>
    public string EditTcpPort
    {
        get => _editTcpPort;
        set => Set(ref _editTcpPort, value ?? "");
    }

    private string _editTriggerCommand = "";
    /// <summary>TCP 连接成功后的触发指令（缺省空=不发；DTU 透传的自动连续读码枪必须留空，
    /// 发了会被当串口数据写进枪——红线见 ScannerBarcodeService 头注释）。</summary>
    public string EditTriggerCommand
    {
        get => _editTriggerCommand;
        set => Set(ref _editTriggerCommand, value ?? "");
    }

    private string _editPortName = "";
    /// <summary>固定 COM 口（如 COM3）；填了就固定用它，换 USB 口要改。</summary>
    public string EditPortName
    {
        get => _editPortName;
        set => Set(ref _editPortName, value ?? "");
    }

    private string _editDeviceKeyword = "";
    /// <summary>设备名关键字（如 CH340）；PortName 留空时 WMI 自动找口，换 USB 口免改。</summary>
    public string EditDeviceKeyword
    {
        get => _editDeviceKeyword;
        set => Set(ref _editDeviceKeyword, value ?? "");
    }

    private string _editBaudRate = "9600";
    /// <summary>波特率（字符串编辑，保存时校验 2400~921600；常见 9600/115200）。</summary>
    public string EditBaudRate
    {
        get => _editBaudRate;
        set => Set(ref _editBaudRate, value ?? "");
    }

    private int _editDataBits = 8;
    public int EditDataBits
    {
        get => _editDataBits;
        set => Set(ref _editDataBits, value);
    }

    private string _editStopBits = "1";
    /// <summary>停止位显示值（"1"/"1.5"/"2"）。</summary>
    public string EditStopBits
    {
        get => _editStopBits;
        set => Set(ref _editStopBits, value ?? "1");
    }

    private string _editParity = "None";
    public string EditParity
    {
        get => _editParity;
        set => Set(ref _editParity, value ?? "None");
    }

    private string _editSessionTimeout = "15";
    /// <summary>扫码会话超时秒数（5~600；扫了库位码后等产品码的最长间隔）。</summary>
    public string EditSessionTimeout
    {
        get => _editSessionTimeout;
        set => Set(ref _editSessionTimeout, value ?? "");
    }

    private bool _editDebugLog;
    /// <summary>心跳调试日志（排障用：WMI 找口过程写操作日志；正常生产保持不勾）。</summary>
    public bool EditDebugLog
    {
        get => _editDebugLog;
        set => Set(ref _editDebugLog, value);
    }

    /// <summary>
    /// 枚举本机串口（System.IO.Ports 读注册表，毫秒级，UI 线程可调）。
    /// 唯一一个口时自动选中回填 PortName；没有串口给提示（多半是转接线没插/
    /// 驱动没装）。设备管理器里拔插一次看 COM 号变化，是确认哪把枪最稳的办法。
    /// </summary>
    private void DetectPorts()
    {
        string[] ports;
        try
        {
            ports = SerialPort.GetPortNames()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage($"枚举串口失败：{ex.Message}", "检测串口");
            return;
        }

        DetectedPorts.Clear();
        foreach (string port in ports)
            DetectedPorts.Add(port);

        if (ports.Length == 0)
        {
            SelectedDetectedPort = null;
            _dialogs.ShowMessage(
                "本机没有检测到任何串口。\n" +
                "请确认：① 扫码枪网口转 USB 转接线已插好；② 设备管理器" +
                "『端口(COM 和 LPT)』下有对应 COM 号（没有则先装转接线驱动）。",
                "检测串口");
            return;
        }

        // 只有一个口：直接选中回填（现场多数情况就是它）
        if (ports.Length == 1)
            SelectedDetectedPort = ports[0];
    }

    // ---------- 回显 / 保存 ----------

    private void SyncFromOptions(ScannerOptions o)
    {
        EditEnabled = o.Enabled;
        EditUseTcp = o.NormMode == "Tcp";          // 原文非法值按规范化回显（与运行口径一致）
        EditTcpIp = o.TcpIp;
        EditTcpPort = o.NormTcpPort.ToString();
        EditTriggerCommand = o.TriggerCommand ?? "";  // 原文回显（可空），不挡空值
        EditPortName = o.PortName;
        EditDeviceKeyword = o.DeviceKeyword;
        EditBaudRate = o.NormBaudRate.ToString();
        EditDataBits = o.NormDataBits;
        EditStopBits = o.NormStopBits == "15" ? "1.5" : o.NormStopBits;
        EditParity = o.NormParity;
        EditSessionTimeout = o.NormSessionTimeoutSeconds.ToString();
        EditDebugLog = o.DebugLog;
    }

    /// <summary>
    /// 校验 + 落盘（保存唯一入口）。**按连接方式分桶校验**：Tcp 方式只校验 TCP
    /// 三件（目标 IP / 端口 / 触发指令），Serial 方式只校验串口五件——非当前
    /// 方式的参数原样落 ini 不拦错（切回方式时保留各自上次配的值，数值坏也有
    /// Norm* 兜底）；扫码会话超时两方式共用。Enabled 勾了却没有任何当前方式
    /// 的接入目标=无效配置，拦下。校验口径与 ScannerOptions 的 Norm* 一致。
    /// </summary>
    private void Save()
    {
        var errors = new List<string>();
        bool useTcp = EditUseTcp;

        // ---- 两方式共用：扫码会话超时 ----
        if (!int.TryParse(EditSessionTimeout.Trim(), out int timeoutSeconds) ||
            timeoutSeconds is < 5 or > 600)
            errors.Add("扫码会话超时须为 5~600 秒（缺省 15，扫库位后等产品码的最长间隔）");

        // TCP 三件先取值（两种方式都落盘；非法端口按缺省 8899 兜底同 NormTcpPort）。
        string tcpIp = EditTcpIp.Trim();
        bool tcpPortOk = int.TryParse(EditTcpPort.Trim(), out int parsedPort) &&
                         parsedPort is >= 1 and <= 65535;
        int tcpPort = tcpPortOk ? parsedPort : 8899;

        // 共用换算先取值（两种方式都落盘，切回方式时保留上次配置）：
        // 停止位下拉恒为 "1"/"1.5"/"2" 之一；异常文本回 "" 由 NormStopBits 兜底 "1"。
        string stopBitsValue = EditStopBits switch
        {
            "1.5" => "15",
            "1" => "1",
            "2" => "2",
            _ => "",
        };

        if (useTcp)
        {
            // ---- TCP 方式专属：必须配出可连入的目标 ----
            if (tcpIp.Length == 0)
                errors.Add("已勾选启用，但未填 TCP 目标 IP：请先在枪侧 DTU（如 Eport E10）" +
                           "的 Web 配置台确认 IP 与 TCP 服务端口，再回填这里" +
                           "（现场 DTU 已配 TCP-SERVER 8899）");

            if (!tcpPortOk)
                errors.Add("TCP 端口须为 1~65535 的整数（现场 DTU TCP-SERVER 8899）");

            // 触发指令可空（=连上不发——自动连续读码枪的口径，现场 SH-5000-GHD）；
            // 填了不许嵌换行：发送时自动补 CRLF，嵌换行会被枪当多条指令收。
            if (EditTriggerCommand.Contains('\r') || EditTriggerCommand.Contains('\n'))
                errors.Add("触发指令不应包含换行（发送时会自动补 CRLF，嵌换行会被枪当多条指令）");
        }
        else
        {
            // ---- 串口方式专属：原有五件套校验 ----
            string port = EditPortName.Trim();
            if (port.Length > 0 && !Regex.IsMatch(port, "^COM\\d+$", RegexOptions.IgnoreCase))
                errors.Add($"PortName「{port}」格式无效：应为 COM 号（如 COM3），" +
                           "在设备管理器『端口(COM 和 LPT)』下查看");

            if (!int.TryParse(EditBaudRate.Trim(), out int baudRate) ||
                baudRate is < 2400 or > 921600)
                errors.Add("波特率须为 2400~921600 的整数（常见 9600 / 115200，按扫码枪手册核对）");

            if (EditDataBits is < 5 or > 8)
                errors.Add("数据位须在 5~8 之间（常见 8）");

            if (stopBitsValue.Length == 0)
                errors.Add("停止位只支持 1 / 1.5 / 2");

            if (!ParityOptions.Contains(EditParity))
                errors.Add("校验位只支持 None / Odd / Even / Mark / Space");

            if (EditEnabled && port.Length == 0 && EditDeviceKeyword.Trim().Length == 0)
                errors.Add("已勾选启用，但未填串口定位：请填 PortName（如 COM3）" +
                           "或 DeviceKeyword（如 CH340）；两者都空时扫码枪不会启动");
        }

        if (errors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", errors), "保存失败（配置校验）");
            return;
        }

        // 波特率在串口分桶外解析（TCP 方式下该区隐藏、值保持 ini 原样=合法；
        // 解析失败=0，Save 内部 NormBaudRate 兜底 9600，坏值当场治愈）。
        int.TryParse(EditBaudRate.Trim(), out int baud);

        var options = new ScannerOptions
        {
            Enabled = EditEnabled,
            Mode = useTcp ? "Tcp" : "Serial",   // Save 内部落规范化值
            TcpIp = tcpIp,                      // TCP 三件恒落盘——切方式时保留上次配置
            TcpPort = tcpPort,
            TriggerCommand = EditTriggerCommand.Trim(),
            PortName = EditPortName.Trim(),     // 串口定位同理恒落盘
            DeviceKeyword = EditDeviceKeyword.Trim(),
            BaudRate = baud,
            DataBits = EditDataBits,
            StopBits = stopBitsValue,
            Parity = ParityOptions.Contains(EditParity) ? EditParity : "None", // 坏值兜底
            SessionTimeoutSeconds = timeoutSeconds,
            DebugLog = EditDebugLog,
        };

        try
        {
            options.Save();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage($"扫码枪配置落盘失败：{ex.Message}", "保存失败");
            return;
        }

        CloseDialog = true;
        // 成功提示由主窗统一弹（与点表/策略窗同款）；此处只负责关窗请求。
        RequestClose?.Invoke(true);
    }
}
