// =========================================================================
// ScannerBarcodeService：串口扫码枪宿主封装（程序①专用）
//
// 干什么：把 kaleidoscope 子模块的串口扫码枪服务（ScannerService）包一层，
//   面向主窗 VM 暴露最小契约——启动/扫码事件/连接事件/连接名/释放。
//   业务层（SlotBindingSession）只认"扫到一串文本"，不关心串口细节。
//
// 为什么用子模块的串口实现：
//   ① 现场扫码枪是**网口输出经 USB 转接线接入**——系统里就是虚拟串口，
//      与 ScannerService 的 System.IO.Ports 实现天然匹配；
//   ② 该实现自带 WMI 按设备名自动找口 + 3 秒心跳断连检测 + 静默重连，
//      正好覆盖"USB 口松了/换口"这类现场故障，宿主不用自己写一套。
//   子模块是净 4.7.2 程序集，net8 运行期加载已在 SubmoduleSmokeTests 锁定。
//
// 为什么把子模块日志桥接到 LocalLogService：默认 LogHelper 会另写
//   Logs\运行日志_yyyyMMdd.log，现场会有两套日志目录容易混淆；桥接后
//   扫码枪的"连上/断开/找不到口"全部进主窗操作日志（铁律 4 只记边沿）。
//   桥接只在本服务真实构造 ScannerService 时设置，Dispose 恢复原值。
//
// 线程约定：Start/Dispose 由宿主在**后台线程**调（ScannerService.Open 内部
//   会做一次 WMI 查询+串口打开，UI 线程禁阻塞——铁律 2）；两个事件都在
//   子模块后台线程触发，UI 订阅方必须自行封送。
//
// 怎么改：换 TCP/EIP 扫码枪 = 工厂换成 ScannerTcpService/ScannerEipService
//   （ScanConfig.Mode 相应改），本类与业务层零改动。
// =========================================================================

using CureLine.Core.Services;
using Kaleidoscope.Models;
using Kaleidoscope.Services;
using Kaleidoscope.Utils;

namespace CureLine.Core.Comms;

/// <summary>
/// 串口扫码枪宿主封装（见文件头注释）。一个进程只建一个实例；
/// <see cref="Start"/> 失败不抛异常——扫码是增值功能，坏了不影响程控。
/// </summary>
public sealed class ScannerBarcodeService : IDisposable
{
    private readonly ScannerOptions _options;

    /// <summary>测试注入的扫码枪工厂（null=真实 ScannerService；测试传假实现）。</summary>
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

    /// <summary>连接状态边沿（true=串口已打开；后台线程触发，UI 需封送）。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>配置项（只读快照；热改配置需重启程序——串口参数是低频配置）。</summary>
    public ScannerOptions Options => _options;

    /// <summary>串口是否已打开（实际连上）。</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return _scanner is { IsOpen: true };
        }
    }

    /// <summary>实际连接名（如 "COM3"；未连上时回落"扫码枪"）——状态条/日志展示用。</summary>
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
    /// 启动扫码枪（宿主必须在后台线程调）。返回 true=已启动串口线程
    /// （不代表已连上——连上与否看 <see cref="IsConnected"/> 与连接事件）。
    /// 未启用/未配置端口/已释放一律返回 false 并记一行原因（边沿级，不刷屏）。
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (!_options.Enabled)
            {
                LocalLogService.Write("扫码枪：未启用（config.ini [Scanner] Enabled=0），跳过启动");
                return false;
            }
            if (!_options.HasPortTarget)
            {
                LocalLogService.Write("扫码枪：未配置串口（[Scanner] PortName 与 DeviceKeyword 均为空），跳过启动");
                return false;
            }
            if (_scanner != null)
                return true;

            var config = new ScanConfig
            {
                Enabled = true,
                Mode = "Serial",                 // 明确串口模式（缺省是 Tcp，必须写死）
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
                scanner = new ScannerService(config);
            }

            scanner.SerialNumberScanned += OnSerialNumberScanned;
            scanner.ConnectionChanged += OnConnectionChanged;
            _scanner = scanner;

            // Open 内部会做一次"WMI 找口 + 串口打开"（可能几百 ms）并启动心跳线程；
            // 本方法由宿主后台任务调用，不阻塞 UI。
            bool started = scanner.Open();
            if (!started)
            {
                LocalLogService.Write("扫码枪：串口启动失败（配置无效或设备未就绪），后台不再重试");
                DetachAndDisposeLocked();
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 释放：退订事件 + 关串口（子模块 Dispose 会等心跳线程退出，最坏 3 秒——
    /// 宿主在退出链用后台任务调，不要阻塞 UI）。
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
