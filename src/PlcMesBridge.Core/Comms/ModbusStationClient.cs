// =========================================================================
// ModbusStationClient：多机网关 Modbus TCP 通道（IPlcClient 的 Modbus 实现，
// Phase 2 删三菱后的统一真机底座）
//
// 干什么：对外维持 IPlcClient 品牌语法（"R13001"/"D30001"），对内包装子模块
//   kaleidoscope 的 ModbusTcpMasterClient（BeginConnect 手动超时/锁串行化/
//   断连标记释放坏 socket/重连节流自愈全套现成），零自造轮子。
//   StationGateway/MultiMachineManager 面向 IPlcClient 抽象编程，换底层
//   业务语义零改动（见 MultiMachineGateway 类头注释）。
//
// 地址语义（与网关 ShiftAddr 同款策略，改前必看 MesConfig 触发地址是 R 系）：
//   "剥 R/D 前缀取数字"= Modbus 协议地址（0x0000 起，保持寄存器区：FC03 读 /
//   FC06 单写 / FC10 多写）。R13001→13001、D30001→30001，无补零，大小写
//   不敏感；无前缀纯数字也认（剥字母自然落到数字段）。解析失败 / 数字部分
//   >65535 / 区间越界一律按"读写失败"处理——配错地址当场失败回写（网关
//   WriteFail 链），绝不误写寄存器 0。
//
// 字符串打包策略（编码策略集中点，现场校准就改这里）：
//   2 字节/寄存器、big-endian（网络序）：字符串字节流按地址递增两两装进
//   保持寄存器，高字节在先。PackAscii/UnpackAscii 纯函数单测锁定
//   （ModbusChannelTests）；写端按 length（字节数）截断 + 补 '\0' 到偶数
//   边界，读端只取前 length 字节——与老项目 Write(addr, str, 80) 语义对齐。
//   '\0' 原样上交不代清（网关 HandleApi 的 Replace("\0","") 原样沿用老项目）。
//   MES 文本约定 ASCII（单字节），非 ASCII 字符截高位不抛（真机联调时按
//   CIM 端约定再校准，编码策略只此一处——这正是 Pack/Unpack 收敛的意义）。
//
// 自愈语义（铁律 3，直接复用子模块，不再手写）：
//   读写抛 Socket/IO/超时异常 → master 置断连并关坏 socket + 关节流重连；
//   下次读写 master 内部 EnsureConnected 按 ReconnectIntervalMs=5s 尝试重建。
//   IsConnected 直接代理 master 状态（断连标记立即可见，与 Melsec 版同节奏）。
//
// 分帧（Modbus 协议硬约束，模拟器/三菱版没有）：FC03 单帧最多 125 寄存器、
//   FC10 最多 123 寄存器。网关 JSON 区 DataLength=799 字节 ≈ 400 寄存器，
//   必须分块循环读写（见 ReadRegs/WriteRegs）；中间块失败即整次失败——
//   老项目同步整串写同样存在半写窗口，行为口径一致，勿在此加事务。
//
// 线程：全部网络 IO 经子模块 master 内部 _syncRoot 串行化；网关在后台线程
//   轮询（铁律 2：UI 线程禁网络 IO——本类不感知线程，调用方负责）。
// =========================================================================

using System.Text;
using Kaleidoscope.Models;
using Kaleidoscope.Services;

namespace PlcMesBridge.Core.Comms;

public sealed class ModbusStationClient : IPlcClient
{
    /// <summary>断线重连节流间隔（铁律 3：5s，与原 Melsec 版同款节奏）。</summary>
    public const int ReconnectIntervalMs = 5000;

    /// <summary>FC03 读保持寄存器单帧上限（Modbus 协议规格，NModbus 按 125 校验）。</summary>
    private const int MaxReadRegs = 125;

    /// <summary>FC10 写多寄存器单帧上限（Modbus 协议规格，NModbus 按 123 校验）。</summary>
    private const int MaxWriteRegs = 123;

    private readonly ModbusTcpMasterClient _master = new();
    private bool _disposed;

    public string IpAddress { get; private set; } = "";
    public int Port { get; private set; }

    /// <summary>连接状态直读子模块（断连标记/节流重连全在 master 内部，包装层零缓存零陈旧）。</summary>
    public bool IsConnected => !_disposed && _master.IsConnected;

    /// <summary>
    /// 连接（timeoutMs 内连不上返回 false，不抛——界面弹框由调用方做）。
    /// 重连节流 PollIntervalMs=0：网关自带 200ms 触发轮询线程，不启子模块后台轮询。
    /// </summary>
    public bool Connect(string ip, int port, int timeoutMs = 3000)
    {
        if (_disposed)
            return false;
        var cfg = new PlcMasterConfig
        {
            IpAddress = ip,
            Port = port,
            UnitId = 1,
            TimeoutMs = timeoutMs,
            ReconnectIntervalMs = ReconnectIntervalMs,
            PollIntervalMs = 0,
        };
        bool ok = _master.Connect(cfg);
        if (ok)
        {
            IpAddress = ip;
            Port = port;
        }
        return ok;
    }

    public void Disconnect() => _master.Disconnect();

    // ==================== 读 ====================

    public (bool Ok, short Value) ReadInt16(string address)
    {
        if (!TryParseAddr(address, out ushort reg))
            return (false, (short)0);
        ushort[]? regs = _master.ReadHoldingRegisters(reg, 1);
        if (regs == null || regs.Length == 0)
            return (false, (short)0);
        return (true, unchecked((short)regs[0]));
    }

    /// <summary>读 length 个字寄存器（D 区一个字 = 1 个 Modbus 寄存器，length=寄存器个数）。</summary>
    public (bool Ok, short[]? Values) ReadInt16(string address, ushort length)
    {
        if (length == 0 || !TryParseAddr(address, out ushort reg))
            return (false, null);
        ushort[]? regs = ReadRegs(reg, length);
        if (regs == null)
            return (false, null);
        var values = new short[length];
        for (int i = 0; i < length; i++)
            values[i] = unchecked((short)regs[i]);
        return (true, values);
    }

    /// <summary>
    /// 读 int32 数组（每值 2 寄存器、低 16 位在低地址——与模拟器
    /// SimulatedPlcClient 的 BlockCopy 内存序语义一致）。接口对称保留：
    /// 多机网关不用（只有旧单机固化时间用过），P3 单线过账再决定是否真用。
    /// </summary>
    public (bool Ok, int[]? Values) ReadInt32(string address, ushort length)
    {
        if (length == 0 || !TryParseAddr(address, out ushort reg))
            return (false, null);
        if (length > MaxReadRegs / 2)
            return (false, null);   // 单次 int32 组也受单帧寄存器上限约束（防数组越界）
        ushort[]? regs = ReadRegs(reg, length * 2);
        if (regs == null)
            return (false, null);
        var values = new int[length];
        for (int i = 0; i < length; i++)
        {
            int lo = regs[i * 2] & 0xFFFF;
            int hi = regs[i * 2 + 1] << 16;
            values[i] = hi | lo;
        }
        return (true, values);
    }

    /// <summary>
    /// 读字符串（length=字节数，Hsl/Melsec 版同语义）：向上取整读寄存器，
    /// 解包后只取前 length 字节（奇数边界补的填充不计入）。'\0' 原样上交。
    /// </summary>
    public (bool Ok, string? Value) ReadString(string address, ushort length)
    {
        if (length == 0 || !TryParseAddr(address, out ushort reg))
            return (false, null);
        int count = (length + 1) / 2;               // 2 字节/寄存器，向上取整
        if (count > MaxReadRegs)
            return (false, null);                   // 分帧交给 ReadRegs（count 会进来）
        ushort[]? regs = ReadRegs(reg, count);
        if (regs == null)
            return (false, null);
        byte[] bytes = UnpackAscii(regs);
        return (true, Encoding.ASCII.GetString(bytes, 0, length));
    }

    // ==================== 写 ====================

    /// <summary>写单字（FC06 单写；触发字清 0、完成位置 1 走这里）。</summary>
    public bool WriteInt16(string address, short value)
    {
        if (!TryParseAddr(address, out ushort reg))
            return false;
        return _master.WriteSingleRegister(reg, unchecked((ushort)value));
    }

    /// <summary>写字数组（FC10 多写，分帧在 WriteRegs 内部处理）。</summary>
    public bool WriteInt16(string address, short[]? values)
    {
        if (values == null || values.Length == 0 || !TryParseAddr(address, out ushort reg))
            return false;
        var regs = new ushort[values.Length];
        for (int i = 0; i < values.Length; i++)
            regs[i] = unchecked((ushort)values[i]);
        return WriteRegs(reg, regs);
    }

    /// <summary>写 int32 数组（每值 2 寄存器、低 16 位在低地址，与 ReadInt32 对称）。</summary>
    public bool WriteInt32(string address, int[]? values)
    {
        if (values == null || values.Length == 0 || !TryParseAddr(address, out ushort reg))
            return false;
        if (values.Length > MaxWriteRegs / 2)
            return false;
        var regs = new ushort[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            regs[i * 2] = unchecked((ushort)(values[i] & 0xFFFF));      // 低字在低地址
            regs[i * 2 + 1] = unchecked((ushort)(values[i] >> 16));     // 高字在次地址
        }
        return WriteRegs(reg, regs);
    }

    /// <summary>
    /// 写字符串（自动按 length 补 '\0'/截断，Length=字节数——老项目
    /// Write(addr, str, 80) 语义）：ASCII 单字节装寄存器（big-endian 两字节
    /// 组装见 PackAscii）。空值业务层传 " "（网关约定），此处照写不代转。
    /// </summary>
    public bool WriteString(string address, string value, int length)
    {
        if (length <= 0 || !TryParseAddr(address, out ushort reg))
            return false;
        string s = value ?? "";
        var bytes = new byte[length];
        int n = Math.Min(s.Length, length);
        for (int i = 0; i < n; i++)
            bytes[i] = (byte)s[i];
        return WriteRegs(reg, PackAscii(bytes));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _master.Dispose();
    }

    // ==================== 地址/打包纯函数（internal 供单元测试锁定） ====================

    /// <summary>
    /// 地址解析（ShiftAddr 同款策略）：剥字母前缀取数字段 = 协议地址。
    /// 有数字 + 全段数字 + ≤65535 判过；否则 false（配错地址当场失败，不误写）。
    /// </summary>
    internal static bool TryParseAddr(string? address, out ushort reg)
    {
        reg = 0;
        string s = (address ?? "").Trim();
        int i = 0;
        while (i < s.Length && !char.IsDigit(s[i]))
            i++;
        return i < s.Length && ushort.TryParse(s.Substring(i), out reg);
    }

    /// <summary>
    /// 字节流 → 寄存器（编码策略集中点）：2 字节/寄存器 big-endian——
    /// 寄存器高字节 = 偶数字节、低字节 = 奇数字节；奇数字节数末尾补 '\0'。
    /// </summary>
    internal static ushort[] PackAscii(byte[] bytes)
    {
        ushort[] regs = new ushort[(bytes.Length + 1) / 2];
        for (int i = 0; i < regs.Length; i++)
        {
            int hi = i * 2 < bytes.Length ? bytes[i * 2] : 0;
            int lo = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : 0;
            regs[i] = (ushort)((hi << 8) | lo);
        }
        return regs;
    }

    /// <summary>寄存器 → 字节流（PackAscii 的逆，读字符串前半段用）。</summary>
    internal static byte[] UnpackAscii(ushort[] regs)
    {
        byte[] bytes = new byte[regs.Length * 2];
        for (int i = 0; i < regs.Length; i++)
        {
            bytes[i * 2] = (byte)(regs[i] >> 8);
            bytes[i * 2 + 1] = (byte)(regs[i] & 0xFF);
        }
        return bytes;
    }

    // ==================== 分帧（Modbus 单帧上限硬约束） ====================

    /// <summary>
    /// 分块读寄存器：FC03 单帧 ≤125 个，count 超限循环分块一次会话内连续读。
    /// 任一块失败整次失败（返回 null——半读数据不外泄，调用方走失败链）。
    /// </summary>
    private ushort[]? ReadRegs(ushort startReg, int count)
    {
        if (startReg + count - 1 > ushort.MaxValue)
            return null;    // 地址区间越过寄存器上限：整次失败，不让溢出地址误读
        var acc = new ushort[count];
        int done = 0;
        while (done < count)
        {
            int take = Math.Min(MaxReadRegs, count - done);
            ushort[]? part = _master.ReadHoldingRegisters((ushort)(startReg + done), (ushort)take);
            if (part == null || part.Length < take)
                return null;
            Array.Copy(part, 0, acc, done, take);
            done += take;
        }
        return acc;
    }

    /// <summary>分块写寄存器：FC10 单帧 ≤123 个，分块在连接状态有效（节流自愈在前一台已建链的条件内重用）。</summary>
    private bool WriteRegs(ushort startReg, ushort[] values)
    {
        if (startReg + values.Length - 1 > ushort.MaxValue)
            return false;   // 区间越界整次失败（同 ReadRegs，防溢出地址误写）
        int done = 0;
        while (done < values.Length)
        {
            int take = Math.Min(MaxWriteRegs, values.Length - done);
            var part = new ushort[take];
            Array.Copy(values, done, part, 0, take);
            if (!_master.WriteMultipleRegisters((ushort)(startReg + done), part))
                return false;
            done += take;
        }
        return true;
    }
}
