// =========================================================================
// IStationClient：机台寄存器读写抽象（kaleidoscope 范式在本土落地）
//
// 为什么抽象：① 老项目直捏 MelsecMcNet，换 ASCII 协议要改三处（A版现状）；
//   ② "先做模拟联调"——业务代码对接口编程，真机/模拟一键切换；
//   ③ 单元测试可注入模拟实现，全流程可测。
// 返回值用 (Ok, Value)：复刻老项目 OperateResult.IsSuccess/Content 语义——
//   失败时 Value 取 default，调用方必须判 Ok（老项目判 Content null 对应）。
// 地址：品牌语法字符串（"R13000"=Modbus 保持寄存器口径，"D30001"为老三菱遗产兼容）。
// 线程：实现类内部保证线程安全；业务轮询在后台线程跑（UI 线程禁网络 IO）。
// =========================================================================

namespace CureLine.Core.Comms;

public interface IStationClient : IDisposable
{
    bool IsConnected { get; }
    string IpAddress { get; }
    int Port { get; }

    /// <summary>连接。timeoutMs 内连不上返回 false（不抛，界面弹框由调用方做）。</summary>
    bool Connect(string ip, int port, int timeoutMs = 3000);

    void Disconnect();

    (bool Ok, short Value) ReadInt16(string address);
    (bool Ok, short[]? Values) ReadInt16(string address, ushort length);
    (bool Ok, int[]? Values) ReadInt32(string address, ushort length);

    /// <summary>
    /// 读字符串。三菱 D 区一个字存 2 字节 ASCII；返回已去 '\0'/截断处理？
    /// 不——原样返回，去 '\0' 由调用方做（老项目 .Replace(vbNullChar,"") 在业务层）。
    /// </summary>
    (bool Ok, string? Value) ReadString(string address, ushort length);

    bool WriteInt16(string address, short value);
    bool WriteInt16(string address, short[] values);
    bool WriteInt32(string address, int[] values);

    /// <summary>写字符串（自动按 length 补 '\0'/截断，老项目 Write(addr,str,80) 语义）。</summary>
    bool WriteString(string address, string value, int length);
}
