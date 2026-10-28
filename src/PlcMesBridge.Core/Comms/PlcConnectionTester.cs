// =========================================================================
// PlcConnectionTester：PLC 连通测试（配置窗"测试连接"按钮的后台实现）
//
// 干什么：拿表单上填的值直测（不用先保存）：连上→读一个字→
//   返回（成败， 说明， 耗时ms）。现场流程：填 IP/地址 → 点测试 →
//   绿了再保存，错了当场改，全程不重启。
//
// 为什么放 Core：纯逻辑 + 可测（传 SimulatedPlcClient 工厂测成功链，
//   传真机工厂 + 不可达地址测失败链，CI 无真机也能全绿）。
// 线程：调用方在后台 Task 跑（Connect 自带超时，不冻界面由调用方保证，
//   见配置窗 BtnTest_Click 的 Task.Run 包裹）。
// =========================================================================

using System.Diagnostics;
using PlcMesBridge.Core.Comms;

namespace PlcMesBridge.Core.Comms;

public static class PlcConnectionTester
{
    /// <summary>
    /// 测一次。factory：按需构造（真机 ModbusStationClient / 模拟 SimulatedPlcClient）。
    /// readAddr：网关传 0027 触发字——连通 + 可读一次验证。
    /// </summary>
    public static async Task<(bool Ok, string Message, long Ms)> TestAsync(
        Func<IPlcClient> factory, string ip, int port, string readAddr,
        int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        using IPlcClient plc = factory();
        bool conn = await Task.Run(() => plc.Connect(ip, port, timeoutMs))
            .ConfigureAwait(false);
        if (!conn)
        {
            sw.Stop();
            return (false, $"连接失败：{ip}:{port}", sw.ElapsedMilliseconds);
        }
        var (ok, val) = await Task.Run(() => plc.ReadInt16(readAddr))
            .ConfigureAwait(false);
        sw.Stop();
        if (ok)
            return (true, $"连接成功：{ip}:{port}，{readAddr}={val}（{sw.ElapsedMilliseconds}ms）",
                sw.ElapsedMilliseconds);
        return (false, $"已连上但读取 {readAddr} 失败", sw.ElapsedMilliseconds);
    }
}
