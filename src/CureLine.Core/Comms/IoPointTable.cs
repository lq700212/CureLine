// =========================================================================
// IoPointTable：IO 耦合器点表模型（固化程控的"通讯地图"）
//
// 干什么：把现场 IO 表（GFNB-1A3A 扩展块布局）翻译成强类型配置——
//   每台耦合器 = N 个扩展块 × 每块 16 入 + 16 出；每个物理点（块+位）
//   绑定一个业务角色（光栅左/右、托盘有无、红黄绿灯）+ 仓位（架/层）。
//   现场人员在配置窗里checkBox + 下拉点点鼠标即可完成，无需改代码。
//
// 为什么这么设计：
//   ① 地址布局全部配置化（起始/步进/块数/每块点数）——不同品牌耦合器的
//      寄存器映射不同，现场实测后改配置即可适配，程序零改动（通用性目标）；
//   ② 业务只认"角色+仓位"语义，不认 Modbus 地址——换设备/换地址只改点表，
//      状态机代码不动（对齐 kaleidoscope"换客户只写 UI 与业务编排"的分层宗旨）；
//   ③ 绑定关系即校验输入——保存时查重复/越界/角色方向，错在配置窗当场拦。
//
// 地址布局（缺省值对应 苏州毅嘉 Audi cover module IO 表 2026-09-21 实测布局）：
//   块 K（1 起）：DI 寄存器地址 = DiStartAddress + (K-1)×BlockStrideRegisters
//                DO 寄存器地址 = DoStartAddress + (K-1)×BlockStrideRegisters
//     表上：块1 DI@0x1000 / DO@0x1010，块2 DI@0x1020 / DO@0x1030 …
//     每块步进 0x20、DO 比 DI 大 0x10（16 点打包 1 寄存器，bit 从低到高 0~15）。
//   块内位（缺省模板的生成规律，绑定可自由改）：
//     每 4 位一组表示 1 层：bit0~3=层1（0=左光栅OK 1=右光栅OK 2=托盘有无 3=预留）、
//     bit4~7=层2、bit8~11=层3、bit12~15=预留；输出侧同布局（组内 0=红 1=黄 2=绿）。
//
// 持久化：config/pointtable.json（Newtonsoft，UTF-8 缩进），见 IoPointTableStore。
// =========================================================================

using System.Collections.ObjectModel;

namespace CureLine.Core.Comms;

/// <summary>物理点绑定的业务角色（现场语义），Reserved=未用/预留。</summary>
public enum IoPointRole
{
    Reserved,
    RackLightL,
    RackLightR,
    TrayDetect,
    LampRed,
    LampYellow,
    LampGreen,
}

/// <summary>角色分类助手：输入侧角色 / 输出侧角色判断 + 中文文案。</summary>
public static class IoPointRoleInfo
{
    public static bool IsInput(IoPointRole role) => role is
        IoPointRole.RackLightL or IoPointRole.RackLightR or IoPointRole.TrayDetect;

    /// <summary>中文文案（界面与日志用；程序①先中文缺省，双语切换后续接入语言服务）。</summary>
    public static string ToText(IoPointRole role) => role switch
    {
        IoPointRole.Reserved => "预留",
        IoPointRole.RackLightL => "左侧光栅OK",
        IoPointRole.RackLightR => "右侧光栅OK",
        IoPointRole.TrayDetect => "托盘有无检测",
        IoPointRole.LampRed => "状态灯_红",
        IoPointRole.LampYellow => "状态灯_黄",
        IoPointRole.LampGreen => "状态灯_绿",
        _ => role.ToString(),
    };
}

/// <summary>
/// 单台 IO 耦合器配置（IP + 块状地址布局 + 点位绑定）。
/// 字段缺省值 = Audi cover module IO 表布局（10 块、DI@0x1000、DO@0x1010、步进 0x20）。
/// </summary>
public sealed class IoCouplerDevice
{
    public string Name { get; set; } = "IO耦合器1";
    public string Ip { get; set; } = "192.168.1.20";
    public int Port { get; set; } = 502;
    public byte UnitId { get; set; } = 1;

    /// <summary>DI 输入寄存器起始地址（功能码 0x04），缺省 0x1000。</summary>
    public ushort DiStartAddress { get; set; } = 0x1000;

    /// <summary>DO 输出寄存器起始地址（功能码 0x03 读 / 0x06 写），缺省 0x1010（= DI 起始 + 0x10）。</summary>
    public ushort DoStartAddress { get; set; } = 0x1010;

    /// <summary>扩展块数（每块占 BlockStrideRegisters 个寄存器地址空间）。</summary>
    public int BlockCount { get; set; } = 10;

    /// <summary>块间地址步进（寄存器个数）。缺省 0x20=32：每块 16DI + 16DO（各占 0x10）。</summary>
    public ushort BlockStrideRegisters { get; set; } = 0x20;

    /// <summary>每块点数（≤16，恒打包 1 个寄存器；缺省 16）。</summary>
    public int PointsPerBlock { get; set; } = 16;

    /// <summary>输入逻辑取反：true=bit=0 为 ON（现场线路低有效时实测后勾选）。</summary>
    public bool InvertInputs { get; set; }

    /// <summary>输出逻辑取反，语义同输入取反。</summary>
    public bool InvertOutputs { get; set; }

    /// <summary>连接/收发超时（毫秒），缺省 3000。</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>轮询周期（毫秒），缺省 250。全部块 DI+DO 一轮 2×块数 帧，250ms 下带宽占用 ≈20%，安全。</summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>点位绑定表：完整覆盖所有块×位（Reserved 也生成，总数 = 块数×点数×2）。</summary>
    public List<IoPointBinding> Bindings { get; set; } = new();

    /// <summary>块 K 的 DI 寄存器地址（K 从 1 起）。</summary>
    public ushort DiAddressOf(int block)
    {
        CheckBlock(block);
        return (ushort)(DiStartAddress + (block - 1) * BlockStrideRegisters);
    }

    /// <summary>块 K 的 DO 寄存器地址（K 从 1 起）。</summary>
    public ushort DoAddressOf(int block)
    {
        CheckBlock(block);
        return (ushort)(DoStartAddress + (block - 1) * BlockStrideRegisters);
    }

    public int DiPointCount => BlockCount * Math.Max(0, PointsPerBlock);
    public int DoPointCount => DiPointCount;

    private void CheckBlock(int block)
    {
        if (block < 1 || block > BlockCount)
            throw new ArgumentOutOfRangeException(nameof(block),
                $"块号 {block} 超出 [1, {BlockCount}]");
    }
}

/// <summary>
/// 一个物理点的业务绑定。IsInput=true 表示 DI（读），false 表示 DO（写）。
/// Role=Reserved 时 Rack/Level 必须为 0；业务角色必须 1~99 架 / 1~3 层。
/// </summary>
public sealed class IoPointBinding
{
    public int Block { get; set; }
    public int Bit { get; set; }
    public bool IsInput { get; set; }
    public IoPointRole Role { get; set; } = IoPointRole.Reserved;
    public int Rack { get; set; }
    public int Level { get; set; }

    /// <summary>功能名（IO 表的中文文本，如"固化架1上层左侧光栅OK信号"），缺省模板自动填。</summary>
    public string FunctionName { get; set; } = "";

    /// <summary>业务键（角色+架+层+方向），做唯一性与快照索引。</summary>
    public IoPointKey Key => new(IsInput, Role, Rack, Level);
}

/// <summary>语义点定位键：方向 + 角色 + 仓位。业务代码只按这个键取点。</summary>
public readonly record struct IoPointKey(bool IsInput, IoPointRole Role, int Rack, int Level);

/// <summary>整张点表（可多台耦合器；A 线 OP30 一台带 10 块；B/C 双线可两台）。</summary>
public sealed class PointTableConfig
{
    public List<IoCouplerDevice> Devices { get; set; } = new();
}

/// <summary>校验结果：Ok=false 时 Errors 非空，界面弹框展示。</summary>
public sealed record PointTableValidation(bool Ok, ReadOnlyCollection<string> Errors)
{
    public override string ToString() => Ok ? "校验通过" : string.Join("\r\n", Errors);
}

/// <summary>点表校验：结构完整性 + 绑定重复/越界/角色方向，保存前必跑。</summary>
public static class PointTableValidator
{
    public static PointTableValidation Validate(PointTableConfig config)
    {
        var errors = new List<string>();

        if (config.Devices.Count == 0)
            errors.Add("至少需要配置一台 IO 耦合器。");

        var hasConcreteRole = config.Devices
            .SelectMany(d => d.Bindings)
            .Any(b => b.Role != IoPointRole.Reserved);
        if (config.Devices.Count > 0 && !hasConcreteRole)
            errors.Add("没有任何点绑定业务角色（全是预留），无法开跑。");

        // 语义键全场唯一闸（跨设备）：快照把多台设备按语义键合并进同一字典，
        // 两台耦合器绑同架同层会同键互相覆盖（后扫的盖先扫的）——双架部署
        // 第二台必须绑独立架号段（如 11~20），全场查重在保存前拦下撞号。
        var allSemantic = new HashSet<IoPointKey>();

        for (int di = 0; di < config.Devices.Count; di++)
        {
            var device = config.Devices[di];
            var where = $"设备[{device.Name}]";

            if (string.IsNullOrWhiteSpace(device.Name))
                errors.Add($"第 {di + 1} 台设备名称为空。");
            if (string.IsNullOrWhiteSpace(device.Ip) ||
                !System.Net.IPAddress.TryParse(device.Ip, out _))
                errors.Add($"{where} IP 非法：'{device.Ip}'。");
            if (device.Port is < 1 or > 65535)
                errors.Add($"{where} 端口非法：{device.Port}。");
            if (device.BlockCount < 1)
                errors.Add($"{where} 扩展块数必须 ≥1。");
            if (device.PointsPerBlock is < 1 or > 16)
                errors.Add($"{where} 每块点数必须 1~16（Modbus 寄存器 16 位）。");
            if (device.PollIntervalMs < 50)
                errors.Add($"{where} 轮询周期太小（<50ms），会打爆设备。");
            if (device.TimeoutMs < 100)
                errors.Add($"{where} 超时太小（<100ms）。");

            foreach (var b in device.Bindings)
                ValidateBinding(device, b, where, errors);

            CheckDuplicates(device, where, allSemantic, errors);
        }

        return new PointTableValidation(errors.Count == 0, errors.AsReadOnly());
    }

    /// <summary>
    /// 重复绑定两道闸：
    /// ① 同一物理点（方向+块+位）只能有一行——按设备独立（不同耦合器的
    ///   物理点本来就各自独立）；
    /// ② 同一语义键（方向+角色+架+层）只能绑一个物理点——业务唯一性，
    ///   <paramref name="allSemantic"/> 由 Validate 传入、跨设备共享
    ///   （快照按键建字典合并多台设备，跨设备同键会让状态机拿到二义值）。
    /// </summary>
    private static void CheckDuplicates(IoCouplerDevice device, string where,
        HashSet<IoPointKey> allSemantic, List<string> errors)
    {
        var physical = new HashSet<(bool, int, int)>();
        foreach (var b in device.Bindings)
        {
            if (!physical.Add((b.IsInput, b.Block, b.Bit)))
                errors.Add($"{where} 块{b.Block}位{b.Bit}重复绑定（同一物理点只能一行）。");
            if (b.Role != IoPointRole.Reserved && !allSemantic.Add(b.Key))
                errors.Add($"{where} 仓位（架{b.Rack}/层{b.Level}）的" +
                    $"{IoPointRoleInfo.ToText(b.Role)} 重复绑定多个物理点" +
                    "（含其它设备已绑同架同层——架号必须全局唯一）。");
        }
    }

    private static void ValidateBinding(IoCouplerDevice device, IoPointBinding b,
        string where, List<string> errors)
    {
        if (b.Block < 1 || b.Block > device.BlockCount)
            errors.Add($"{where} 绑定块号 {b.Block} 超出 [1, {device.BlockCount}]。");
        if (b.Bit < 0 || b.Bit >= device.PointsPerBlock)
            errors.Add($"{where} 绑定 Bit {b.Bit} 超出 [0, {device.PointsPerBlock - 1}]。");
        if (b.Role == IoPointRole.Reserved)
        {
            if (b.Rack != 0 || b.Level != 0)
                errors.Add($"{where} 块{b.Block}位{b.Bit}是预留点，仓位必须清零。");
            return;
        }

        if (IoPointRoleInfo.IsInput(b.Role) != b.IsInput)
            errors.Add($"{where} 块{b.Block}位{b.Bit}角色与点方向不符：" +
                $"{IoPointRoleInfo.ToText(b.Role)} 应是 {(IoPointRoleInfo.IsInput(b.Role) ? "输入" : "输出")}点。");
        if (b.Rack is < 1 or > 99)
            errors.Add($"{where} 块{b.Block}位{b.Bit} 架号 {b.Rack} 应在 1~99。");
        if (b.Level is < 1 or > 3)
            errors.Add($"{where} 块{b.Block}位{b.Bit} 层号 {b.Level} 应在 1~3。");
    }
}

/// <summary>点表快照：一次轮询周期的全部点状态（不可变引用 + 原子换引用读）。</summary>
public sealed class PointTableSnapshot
{
    public static readonly PointTableSnapshot Empty = new(
        ImmutableBuild(), DateTimeOffset.MinValue);

    private static ReadOnlyDictionary<IoPointKey, bool> ImmutableBuild() =>
        new(new Dictionary<IoPointKey, bool>());

    public DateTimeOffset Timestamp { get; }
    public IReadOnlyDictionary<IoPointKey, bool> States { get; }

    public PointTableSnapshot(IReadOnlyDictionary<IoPointKey, bool> states, DateTimeOffset timestamp)
    {
        States = states;
        Timestamp = timestamp;
    }

    /// <summary>按语义键取点状态；没绑定/未读到返回 null（调用方区分"没配"与"断开"）。</summary>
    public bool? Get(IoPointKey key) =>
        States.TryGetValue(key, out var value) ? value : null;
}

/// <summary>点变化边沿事件参数（轮询线程触发，UI 订阅方自行封送）。</summary>
public sealed record PointChangedEventArgs(IoPointKey Key, bool State);
