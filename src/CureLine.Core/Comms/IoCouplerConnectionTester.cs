// =========================================================================
// IoCouplerConnectionTester：IO 耦合器连通测试（点表配置窗"测试连接"按钮的底气）
//
// 干什么：拿一台设备配置直测 Modbus TCP 可达性 + 读回一块 DI/DO 寄存器，
//   返回 (Ok, 文案)，配置窗后台 Task 调用不冻界面。
// 为什么单独成件：与三菱的 PlcConnectionTester 平行——目的可测、给"先测再存"
//   的配置纪律用；不依赖点表/总线（设备还没保存前就能测）。
// 改动注意：连接必须在后台线程（UI 线程禁网络 IO 铁律）——本类只做纯逻辑，
//   调 correlation by Task.Run 由调用方负责。
// =========================================================================

using Kaleidoscope.Models;
using Kaleidoscope.Services;

namespace CureLine.Core.Comms;

public static class IoCouplerConnectionTester
{
    /// <summary>
    /// 测试单台耦合器连通：BeginConnect 带超时连接 → 读第一块 DI/DO 各 1 寄存器。
    /// 三态文案：连通/连接不上/连上但读不通（硬件换协议/DI 区地址不对）。
    /// </summary>
    public static (bool Ok, string Message) Test(IoCouplerDevice device)
    {
        if (string.IsNullOrWhiteSpace(device.Ip))
            return (false, "IP 为空，先填 IP。");

        var controller = new ModbusTcpIoController();
        try
        {
            var cfg = ToKaleidoConfig(device);
            if (!controller.Connect(cfg))
                return (false, $"连接不上 {device.Ip}:{device.Port}" +
                    $"（超时 {device.TimeoutMs}ms，检查网线/IP/端口）。");

            // 读回验证：连上不等于能读——DI 区起始地址/功能码与设备不符时此处暴露。
            var di = controller.ReadInputRegisters(device.DiStartAddress, 1);
            var doR = controller.ReadHoldingRegisters(device.DoStartAddress, 1);
            if (di == null || doR == null)
                return (false, $"TCP 已连上但读寄存器失败" +
                    $"（DI@0x{device.DiStartAddress:X4}/DO@0x{device.DoStartAddress:X4}），" +
                    "起始地址或功能码与设备不符，现场用 Modbus Poll 校准点表布局。");

            return (true, $"连通 OK：{device.Ip}:{device.Port}" +
                $"，DI@0x{device.DiStartAddress:X4}=0x{di[0]:X4}，" +
                $"DO@0x{device.DoStartAddress:X4}=0x{doR[0]:X4}");
        }
        finally
        {
            controller.Dispose(); // 测试专用连接测完即断（不留半开挂死耦合器会话槽）
        }
    }

    private static IoConfig ToKaleidoConfig(IoCouplerDevice d) => new()
    {
        PlcAddress = d.Ip,
        PlcPort = d.Port,
        IoUnitId = d.UnitId,
        IoInputRegisterStartAddress = d.DiStartAddress,
        IoOutputRegisterStartAddress = d.DoStartAddress,
        InvertInputs = d.InvertInputs,
        InvertOutputs = d.InvertOutputs,
        TcpSendTimeoutMs = d.TimeoutMs,
        TcpReceiveTimeoutMs = d.TimeoutMs,
    };
}
