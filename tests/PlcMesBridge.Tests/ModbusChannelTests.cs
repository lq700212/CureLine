// =========================================================================
// ModbusChannelTests：Modbus 通道纯函数锁定（P2 新增，真机语义的地基）
//
// 无真机可测：本类只锁 ModbusStationClient 的纯函数层——
// ① 地址解析（R/D 剥前缀取数字，溢出/残缺拒收——配错地址当场失败，不误写）；
// ② 字符串打包/解包（2 字节/寄存器 big-endian，现场校准集中点，改一处全过）。
// 连接/读写的真机链路等 P2 中段真机联调补：连接语义在子模块
// ModbusTcpMasterClient（Subview 面向其 152 例回归已覆盖），本类不重复造例。
// =========================================================================

using System.Text;
using PlcMesBridge.Core.Comms;

namespace PlcMesBridge.Tests;

public class ModbusChannelTests
{
    // ---- 地址解析（网关触发地址是 R 系，产品地址是 D 系，大小写不当回事）----

    [Theory(DisplayName = "D/R 剥前缀取数字=协议地址")]
    [InlineData("R13001", 13001)]
    [InlineData("D30001", 30001)]
    [InlineData("r100", 100)]
    [InlineData("d20", 20)]
    [InlineData("  D50001  ", 50001)]
    [InlineData("65001", 65001)]        // 无前缀纯数字也认（配置窗校验器已在入口拦格式）
    public void TryParseAddr_Valid(string addr, int expected)
    {
        Assert.True(ModbusStationClient.TryParseAddr(addr, out ushort reg));
        Assert.Equal(expected, reg);
    }

    [Theory(DisplayName = "残缺/溢出地址拒收（配错当场失败，不误写寄存器 0）")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]          // 有前缀无数字
    [InlineData("R130 01")]    // 数字中断段
    [InlineData("abc")]        // 无数字
    [InlineData("R70000")]     // 超 ushort 上限
    [InlineData("83001")]      // 无前缀纯数字但超 65535，同样拒收
    public void TryParseAddr_Invalid(string addr)
    {
        Assert.False(ModbusStationClient.TryParseAddr(addr, out ushort reg));
        Assert.Equal(0, reg);
    }

    // ---- 字符串打包（编码策略集中点：2 字节/寄存器 big-endian）----

    [Fact(DisplayName = "打包：奇数字节个位补 0 到字节序偶边界")]
    public void PackAscii_OddBytes_Padded()
    {
        ushort[] regs = ModbusStationClient.PackAscii(new byte[] { 0x41 });
        Assert.Single(regs);
        Assert.Equal(0x4100, regs[0]);      // 首字节在高位，奇数字节末尾低字节补 0
    }

    [Fact(DisplayName = "打包：两字节=一寄存器（高字节在先）")]
    public void PackAscii_TwoBytes_HiFirst()
    {
        ushort[] regs = ModbusStationClient.PackAscii(
            new byte[] { 0x41, 0x42 });
        Assert.Single(regs);
        Assert.Equal(0x4142, regs[0]);
    }

    [Fact(DisplayName = "打包：三字节 → 两寄存器（末尾补 0）")]
    public void PackAscii_ThreeBytes()
    {
        ushort[] regs = ModbusStationClient.PackAscii(
            new byte[] { 0x41, 0x42, 0x43 });
        Assert.Equal(2, regs.Length);
        Assert.Equal(0x4142, regs[0]);
        Assert.Equal(0x4300, regs[1]);
    }

    [Fact(DisplayName = "打包：空字节流 → 空寄存器（不以 null 代之）")]
    public void PackAscii_Empty()
    {
        ushort[] regs = ModbusStationClient.PackAscii(Array.Empty<byte>());
        Assert.Empty(regs);
    }

    // ---- 解包 ----

    [Fact(DisplayName = "解包：寄存器 → 高字节在先字节流")]
    public void UnpackAscii_HiFirst()
    {
        byte[] bytes = ModbusStationClient.UnpackAscii(
            new ushort[] { 0x4142, 0x4300 });
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43, 0x00 }, bytes);
    }

    [Fact(DisplayName = "打包↔解包互逆（读写对称的自洽性；奇长度回程补 1 个填充位）")]
    public void PackUnpack_Roundtrip()
    {
        var payload = "ABC-JSON-{|}\"799*填充\0\0"u8.ToArray();
        byte[] back = ModbusStationClient.UnpackAscii(
            ModbusStationClient.PackAscii(payload));
        // 回程长度 = 寄存器数×2 = payload 上取偶（奇长度末尾多 1 个补 0 字节）。
        Assert.Equal(payload.Length + payload.Length % 2, back.Length);
        for (int i = 0; i < payload.Length; i++)
            Assert.Equal(payload[i], back[i]);
    }

    [Theory(DisplayName = "ASCII 字符串端到端口径（网关 JSON 场景抽查）")]
    [InlineData("NG", new ushort[] { 0x4E47 })]
    [InlineData("", new ushort[] { })]               // 空串：业务层永远传 " " 占位，此处兜底
    [InlineData(" ", new ushort[] { 0x2000 })]       // 网关空值占位（单空格在高位，末尾补 0）
    [InlineData("2", new ushort[] { 0x3200 })]
    public void PackAscii_SampleValues(string text, ushort[] expected)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        ushort[] regs = ModbusStationClient.PackAscii(bytes);
        Assert.Equal(expected, regs);
    }

    // ---- 字节序翻转（V0.0.20 配置化：StringByteOrder=SwapBytes） ----

    [Fact(DisplayName = "SwapPairs 字节对两两交换（CIM 侧 little-endian 组包的翻转实现）")]
    public void SwapPairs_SwapsAdjacentBytes()
    {
        // "NG"=4E 47 → 47 4E：PackAscii 后寄存器 0x4E47 变 0x474E（字节序反转口径）
        byte[] swapped = ModbusStationClient.SwapPairs(new byte[] { 0x4E, 0x47 });
        Assert.Equal(new byte[] { 0x47, 0x4E }, swapped);
        // 串交换后经 PackAscii 得 little-endian 寄存器
        ushort[] regs = ModbusStationClient.PackAscii(swapped);
        Assert.Equal(new ushort[] { 0x474E }, regs);
    }

    [Fact(DisplayName = "SwapPairs 奇长度末尾孤字节不动（补 \\0 后长度恒偶，此处兜底）")]
    public void SwapPairs_OddLength_KeepsLastByte()
    {
        byte[] swapped = ModbusStationClient.SwapPairs(new byte[] { 0x41, 0x42, 0x43 });
        Assert.Equal(new byte[] { 0x42, 0x41, 0x43 }, swapped);
    }

    [Fact(DisplayName = "SwapPairs 自逆：交换两次还原原字节流")]
    public void SwapPairs_Involution()
    {
        var payload = "NG-A01-JSON"u8.ToArray();
        byte[] twice = ModbusStationClient.SwapPairs(
            ModbusStationClient.SwapPairs(payload));
        Assert.Equal(payload, twice);
    }

    // ---- 节流常量与自愈铁律一致 ----

    [Fact(DisplayName = "重连节流与铁律 3 同节奏（5s）")]
    public void ReconnectInterval_FixedAt5s() =>
        Assert.Equal(5000, ModbusStationClient.ReconnectIntervalMs);
}
