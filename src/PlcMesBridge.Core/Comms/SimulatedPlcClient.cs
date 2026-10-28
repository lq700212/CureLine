// =========================================================================
// SimulatedPlcClient：内存模拟 PLC（IPlcClient 模拟版，先做模拟联调的关键件）
//
// 干什么：用两个字典模拟 PLC 地址区——字区（short[]，D/R/其它一视同仁按名存）
//   与字符串区，语义锁定 IPlcClient 契约（含 WriteString 按 length 截断补齐）；
//   真机实现走 Modbus TCP（见 ModbusStationClient.cs，P2 删三菱后的统一底座）。
// 为什么：① 无真机跑通 UI + 业务全流程；② 单元测试注入，网关触发/装载/
//   回写全部可测；③ 联调脚本化：测试可预置 R13001=1 等触发业务。
// 注意：模拟器里字区与串区独立存储，互不干扰——够模拟用，别拿它验证
//   "字串混读"这种真机特性；真机字符串字节序按 ModbusStationClient.Pack 约定。
// =========================================================================

namespace PlcMesBridge.Core.Comms;

public sealed class SimulatedPlcClient : IPlcClient
{
    private readonly object _lock = new();
    private readonly Dictionary<string, short[]> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public string IpAddress { get; private set; } = "";
    public int Port { get; private set; }

    /// <summary>连不上模拟：ip 为空或 "fail" 时返回 false（测连接失败分支用）。</summary>
    public bool Connect(string ip, int port, int timeoutMs = 3000)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(ip) || ip == "fail")
            {
                IsConnected = false;
                return false;
            }
            IpAddress = ip;
            Port = port;
            IsConnected = true;
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
            IsConnected = false;
    }

    private bool Check() => IsConnected;

    /// <summary>预置单字（布触发字 D30001、完成字 D30011、心跳字等）。</summary>
    public void PresetWord(string address, short value)
    {
        lock (_lock)
            _words[address] = new[] { value };
    }

    /// <summary>预置字数组（固化时间 D30005、进入时间 D30032、PC 时间 D30014）。</summary>
    public void PresetWords(string address, short[] values)
    {
        lock (_lock)
            _words[address] = (short[])values.Clone();
    }

    public void PresetInt32(string address, int[] values)
    {
        lock (_lock)
        {
            var w = new short[values.Length * 2];
            Buffer.BlockCopy(values, 0, w, 0, w.Length * 2);
            _words[address] = w;
        }
    }

    /// <summary>预置字符串（库位 D30040、产品区 D3xxx、网关 JSON 区）。</summary>
    public void PresetString(string address, string value)
    {
        lock (_lock)
            _strings[address] = value;
    }

    /// <summary>取回写值断言用（网关 STATUS/MSG/DATA、完成位 D30011 等）。</summary>
    public string PeekString(string address)
    {
        lock (_lock)
            return _strings.TryGetValue(address, out string? v) ? v : "";
    }

    public short PeekWord(string address)
    {
        lock (_lock)
            return _words.TryGetValue(address, out short[]? v) && v.Length > 0 ? v[0] : (short)0;
    }

    public (bool Ok, short Value) ReadInt16(string address)
    {
        lock (_lock)
        {
            if (!Check())
                return (false, 0);
            if (_words.TryGetValue(address, out short[]? v) && v.Length > 0)
                return (true, v[0]);
            return (true, 0);
        }
    }

    public (bool Ok, short[]? Values) ReadInt16(string address, ushort length)
    {
        lock (_lock)
        {
            if (!Check())
                return (false, null);
            var ret = new short[length];
            if (_words.TryGetValue(address, out short[]? v))
                Array.Copy(v, ret, Math.Min(v.Length, ret.Length));
            return (true, ret);
        }
    }

    public (bool Ok, int[]? Values) ReadInt32(string address, ushort length)
    {
        lock (_lock)
        {
            if (!Check())
                return (false, null);
            var w = new short[length * 2];
            if (_words.TryGetValue(address, out short[]? v))
                Array.Copy(v, w, Math.Min(v.Length, w.Length));
            var ret = new int[length];
            Buffer.BlockCopy(w, 0, ret, 0, w.Length * 2);
            return (true, ret);
        }
    }

    public (bool Ok, string? Value) ReadString(string address, ushort length)
    {
        lock (_lock)
        {
            if (!Check())
                return (false, null);
            _strings.TryGetValue(address, out string? v);
            return (true, v ?? string.Empty);
        }
    }

    public bool WriteInt16(string address, short value)
    {
        lock (_lock)
        {
            if (!Check())
                return false;
            _words[address] = new[] { value };
            return true;
        }
    }

    public bool WriteInt16(string address, short[] values)
    {
        lock (_lock)
        {
            if (!Check())
                return false;
            _words[address] = (short[])values.Clone();
            return true;
        }
    }

    public bool WriteInt32(string address, int[] values)
    {
        lock (_lock)
        {
            if (!Check())
                return false;
            var w = new short[values.Length * 2];
            Buffer.BlockCopy(values, 0, w, 0, w.Length * 2);
            _words[address] = w;
            return true;
        }
    }

    public bool WriteString(string address, string value, int length)
    {
        lock (_lock)
        {
            if (!Check())
                return false;
            string v = value ?? string.Empty;
            _strings[address] = v.Length > length ? v.Substring(0, length) : v;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Disconnect();
    }
}
