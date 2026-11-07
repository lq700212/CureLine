// =========================================================================
// ScannerBarcodeService：扫码枪宿主封装（程序①专用，Serial/Tcp 双模式）
//
// 干什么：把 kaleidoscope 子模块的扫码枪服务包一层，面向主窗 VM 暴露最小
//   契约——启动/扫码事件/连接事件/连接名/释放。业务层（SlotBindingSession）
//   只认"扫到一串文本"，不关心底下是 [Scanner] Mode 键：
//   ① "Tcp"（缺省、现场现行）→ ScannerTcpService：上位机作 TCP 客户端连
//      枪侧 DTU（Eport E10 型，TCP-SERVER 8899，串口透传），按行收码；
//   ② "Serial" → ScannerService：System.IO.Ports 串口读码（WMI 找口）。
//
// 为什么 TCP 走子模块的 ScannerTcpService（勿在本类复制实现）：
//   ① 自带 BeginConnect+WaitOne 强制超时（2s）+ 3 秒节流重连 + TCP KeepAlive
//      （拔网线/断电这类静默断连不假死）+ 30s 节流的连接失败日志——对齐铁律 2/4；
//   ② ReadLoop 按行切分，CR / LF / CRLF 三种结束符兼容——现场 DTU 推
//      "条码文本 + CR（0x0D）" 直接落在此口径（2026-11-07 实测 4 条完整收码）；
//   ③ Roslyn 编译的 net472 程序集，net8 运行期加载已在 SubmoduleSmokeTests 锁定。
//
// **TriggerCommand 红线（现场踩过）**：子模块 ScanConfig.TriggerCommand 缺省
//   "LON" 是基恩士 SR 口径（连上后发触发开激光）；现场 SH-5000-GHD 经 DTU
//   透传且枪自动连续读码——任何指令都会被 DTU 当成串口数据写进枪（可能干扰
//   读码），**TCP 模式下触发指令必须取 ScannerOptions.NormTriggerCommand
//   （缺省空串），绝不能依赖缺省 "LON"**。将来接需要触发的 TCP 枪再在
//   配置窗填指令即可。
//
// 为什么把子模块日志桥接到 LocalLogService：默认 LogHelper 会另写
//   Logs\运行日志_yyyyMMdd.log，现场会有两套日志目录容易混淆；桥接后
//   扫码枪的"连上/断开/找不到口/连接失败(30s 节流)"全部进主窗操作日志
//   （铁律 4 只记边沿）。桥接只在本服务真实构造子模块实例时设置，
//   Dispose 恢复原值。
//
// 线程约定：Start/Dispose 由宿主在**后台线程**调（ScannerService.Open 内部
//   做 WMI 查询+串口打开；ScannerTcpService.Open 起后台连接线程——UI 线程
//   禁阻塞，铁律 2）；两个事件都在子模块后台线程触发，UI 订阅方必须自行封送。
//
// 怎么改：新增接入方式（如 EIP）= ScannerOptions 加定位参数 + 本类 Start
//   加分支构造对应子模块服务（ScanConfig.Mode 相应改），业务层零改动。
// =========================================================================

using CureLine.Core.Services;
using Kaleidoscope.Models;
using Kaleidoscope.Services;
using Kaleidoscope.Utils;

namespace CureLine.Core.Comms;

/// <summary>
/// 扫码枪宿主封装（Serial/Tcp 双模式，见文件头注释）。一个进程只建一个实例；
/// <see cref="Start"/> 失败不抛异常——扫码是增值功能，坏了不影响程控。
/// </summary>
public sealed class ScannerBarcodeService : IDisposable
{
    private readonly ScannerOptions _options;

    /// <summary>测试注入的扫码枪工厂（null=按配置造真实子模块服务：Serial=ScannerService / Tcp=ScannerTcpService）。</summary>
    private readonly Func<ScanConfig, IScanner>? _factory;

    /// <summary>Start/Dispose 串行化（Start 在后台任务、Dispose 在退出链）。</summary>
    private readonly object _gate = new();

    private IScanner? _scanner;
    private bool _disposed;

    /// <summary>是否由本类改写过 LogHelper.LogAction（Dispose 时据此决定是否恢复）。</summary>
    private bool _logBridged;

    /// <summary>桥接前的子模块日志出口（恢复用）。</summary>
    private Action<string, string>? _previousLogAction;

    /// <summary>扫到一条完整条码（子模块后台线程触发，UI 订阅方需封送）。</summary>
    public event EventHandler<string>? BarcodeScanned;

    /// <summary>连接状态边沿（true=已连接上枪；后台线程触发，UI 需封送）。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>配置项（只读快照；热改配置走宿主 dispose/重建本类实现）。</summary>
    public ScannerOptions Options => _options;

    /// <summary>是否已连接上枪（实际连上）。</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _scanner is { IsOpen: true };
        }
    }

    /// <summary>实际连接名（串口="COM3"、TCP="ip:端口"；未连上时回落"扫码枪"）——状态条/日志展示用。</summary>
    public string Name
    {
        get
        {
            lock (_gate)
                return _scanner?.Name is { Length: > 0 } name ? name : "扫码枪";
        }
    }

    /// <summary>是否已启动（含"启动了但还没连上"——子模块在后台静默重连）。</summary>
    public bool Started
    {
        get
        {
            lock (_gate)
                return _scanner != null;
        }
    }

    public ScannerBarcodeService(ScannerOptions options, Func<ScanConfig, IScanner>? factory = null)
    {
        _options = options;
        _factory = factory;
    }

    /// <summary>
    /// 启动扫码枪（宿主必须在后台线程调）。返回 true=已启动后台收码线程
    /// （不代表已连上——连上与否看 <see cref="IsConnected"/> 与连接事件）。
    /// 未启用/未配置接入目标/已释放一律返回 false 并记一行原因（边沿级，不刷屏）。
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            // 未启用/未配目标——边沿级一行说明原因后跳过（文案与宿主启动装配
            // InitScanner 跳过分支同源：ScannerOptions.SkipReasonText 唯一文案源）。
            if (_options.SkipReasonText() is { } skip)
            {
                LocalLogService.Write(skip);
                return false;
            }
            if (_scanner != null)
                return true;

            // 按配置方式建对应子模块配置。ScanConfig 是三方式共存的单模型（Tcp/Serial/
            // Eip 字段同池），各方式只消费自己的字段——本类只产 Tcp/Serial 两种。
            bool tcpMode = _options.NormMode == "Tcp";
            var config = tcpMode
                ? new ScanConfig
                {
                    Enabled = true,
                    Mode = "Tcp",
                    IpAddress = _options.TcpIp.Trim(),
                    Port = _options.NormTcpPort,
                    // 触发指令红线：子模块 ScanConfig 缺省 "LON" 是基恩士 SR 口径——
                    // 必须显式取本配置的规范化值（缺省空串=连上不发任何指令，DTU
                    // 透传的自动连续读码枪绝不能收到 LON），绝不能依赖子模块缺省。
                    TriggerCommand = _options.NormTriggerCommand,
                }
                : new ScanConfig
                {
                    Enabled = true,
                    Mode = "Serial",             // 明确串口模式（子模块缺省是 Tcp，必须写死）
                    PortName = _options.PortName,
                    DeviceKeyword = _options.DeviceKeyword,
                    BaudRate = _options.NormBaudRate,
                    DataBits = _options.NormDataBits,
                    StopBits = _options.NormStopBits,
                    Parity = _options.NormParity,
                    DebugLog = _options.DebugLog,
                };

            IScanner scanner;
            if (_factory != null)
            {
                scanner = _factory(config);
            }
            else
            {
                // 子模块日志统一进主窗操作日志（只设置一次，Dispose 恢复）。
                _previousLogAction = LogHelper.LogAction;
                _logBridged = true;
                LogHelper.LogAction = (level, message) =>
                    LocalLogService.Write($"扫码枪：{message}");
                // 真实服务按方式选型（串口=ScannerService、TCP=ScannerTcpService，
                // 选型理由见文件头注释）；假工厂不受影响——由测试方决定回什么实现。
                scanner = tcpMode ? (IScanner)new ScannerTcpService(config) : new ScannerService(config);
            }

            scanner.SerialNumberScanned += OnSerialNumberScanned;
            scanner.ConnectionChanged += OnConnectionChanged;
            _scanner = scanner;

            // Open 内部会起后台连接线程（TCP=立即返回、后台异步建连+断线节流重连；
            // 串口=WMI 找口+串口打开后起心跳线程）；本方法由宿主后台任务调用，不阻塞 UI。
            bool started = scanner.Open();
            if (!started)
            {
                LocalLogService.Write(tcpMode
                    ? "扫码枪：TCP 启动失败（配置无效），后台不再重试"
                    : "扫码枪：串口启动失败（配置无效或设备未就绪），后台不再重试");
                DetachAndDisposeLocked();
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 释放：退订事件 + 关连接（串口=关串口、TCP=关 socket；子模块 Dispose 会等
    /// 心跳/收码线程退出，最坏 3 秒——宿主在退出链用后台任务调，不要阻塞 UI）。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        IScanner? scanner;
        Action<string, string>? previous;
        bool restoreLog;
        lock (_gate)
        {
            scanner = _scanner;
            _scanner = null;
            previous = _previousLogAction;
            restoreLog = _logBridged;
            _logBridged = false;
            _previousLogAction = null;
        }

        if (scanner != null)
        {
            try
            {
                scanner.SerialNumberScanned -= OnSerialNumberScanned;
                scanner.ConnectionChanged -= OnConnectionChanged;
                scanner.Dispose();
            }
            catch
            {
                // 释放在退出链——任何异常都吞掉，不能让关窗流程崩。
            }
        }

        if (restoreLog)
            LogHelper.LogAction = previous;
    }

    /// <summary>读码转发（子模块后台线程 → 宿主订阅方自行封送）。</summary>
    private void OnSerialNumberScanned(object? sender, string code) =>
        BarcodeScanned?.Invoke(this, code);

    /// <summary>连接边沿转发（子模块后台线程 → 宿主订阅方自行封送）。</summary>
    private void OnConnectionChanged(object? sender, bool connected) =>
        ConnectionChanged?.Invoke(this, connected);

    /// <summary>启动失败时清理已接线的实例（须在 _gate 内调）。</summary>
    private void DetachAndDisposeLocked()
    {
        if (_scanner == null)
            return;
        var scanner = _scanner;
        _scanner = null;
        try
        {
            scanner.SerialNumberScanned -= OnSerialNumberScanned;
            scanner.ConnectionChanged -= OnConnectionChanged;
            scanner.Dispose();
        }
        catch
        {
            // 同上：启动失败路径也吞异常。
        }
    }
}
