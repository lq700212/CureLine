// =========================================================================
// IoPointTableStore：点表配置的读写与缺省模板（程序①的"点点鼠标"数据底座）
//
// 干什么：
//   ① Load/Save：config/pointtable.json 读写（Newtonsoft、UTF-8 缩进）；
//      文件不存在/损坏 → 回 null 并带出自愈原因（对齐 INI 自愈语义，
//      现场删了配置文件程序也不崩，靠配置窗重新配一遍即修复）。
//   ② BuildDefaultTemplate：一键生成"IO 表缺省模板"——现场拿到新线体只要
//      改 IP，或仓位结构变了在配置窗微调绑定，不用逐点手填。
//      布局规律来自 Audi cover module IO 表（块内 4 位一组 1 层、组内
//      左光栅/右光栅/托盘 + 预留；输出组内 红/黄/绿 + 预留）。
//
// 怎么改：缺省模板生成规律与 IoPointTable 顶注释一致；每块最多 4 层
//   （16 位 ÷ 每层 4 位），超过时调用方给一台设备分多块。
//   多台机台布局用 BuildMachineTemplate（一台耦合器管一列 3 层的现场口径）。
// =========================================================================

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using CureLine.Core.Infrastructure;

namespace CureLine.Core.Comms;

public static class IoPointTableStore
{
    /// <summary>点表配置文件路径：exe 同目录 config/pointtable.json。</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(AppPaths.ConfigDir, "pointtable.json");

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    /// <summary>
    /// 读点表；文件不存在/空/JSON 损坏一律回 null（调用方进配置窗或先建缺省模板），
    /// 原因文本写入 reasons 供日志（启动期不弹框打扰操作员）。
    /// </summary>
    public static PointTableConfig? Load(string? path = null, List<string>? reasons = null)
    {
        path ??= DefaultPath;
        reasons?.Clear();
        try
        {
            if (!File.Exists(path))
            {
                reasons?.Add("点表配置文件不存在（首次启动或被删除），使用空配置。");
                return null;
            }
            var config = JsonConvert.DeserializeObject<PointTableConfig>(
                File.ReadAllText(path), JsonSettings);
            if (config == null)
            {
                reasons?.Add("点表配置文件内容为空，使用空配置。");
                return null;
            }
            return config;
        }
        catch (Exception ex)
        {
            // 配置坏了不 crash 现场程序——回空表，现场在配置窗重新保存即修复。
            reasons?.Add($"点表配置文件损坏（{ex.Message}），使用空配置。");
            return null;
        }
    }

    /// <summary>保存点表（UTF-8 缩进 JSON）。目录不存在自动建（kernel32 不自动建目录同款前拦）。</summary>
    public static void Save(PointTableConfig config, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonConvert.SerializeObject(config, JsonSettings));
    }

    /// <summary>
    /// 一键生成"IO 表缺省模板"：1 台耦合器，每块承载一架的 1~4 层（块 K = 架 rackStart+K-1）。
    /// 块内每 4 位一组 = 1 层，组内输入 0=左光栅OK 1=右光栅OK 2=托盘有无；输出 0=红 1=黄 2=绿。
    /// 层数 >4 时本方法抛异常（调用方应把多余层分到另一台设备）。
    /// rackStart=起始全局架号（缺省 1）：双架部署第二台耦合器传 11，
    /// 生成架 11~rackStart+rackCount-1（块号仍从 1 起算设备内地址，地址换算不受影响）。
    /// </summary>
    public static PointTableConfig BuildDefaultTemplate(
        string deviceName, string ip, int rackCount, int levelsPerRack, int rackStart = 1)
    {
        const int bitsPerLevel = 4, pointsPerLevel = 3;
        int maxLevels = 16 / bitsPerLevel;
        if (rackCount < 1)
            throw new ArgumentOutOfRangeException(nameof(rackCount), "架数 ≥1");
        if (rackStart < 1)
            throw new ArgumentOutOfRangeException(nameof(rackStart), "起始架号 ≥1");
        if (levelsPerRack < 1 || levelsPerRack > maxLevels)
            throw new ArgumentOutOfRangeException(nameof(levelsPerRack),
                $"每架层数只能在 [1, {maxLevels}]（每层占 4 位）；更多层请拆多台设备。");

        var device = new IoCouplerDevice
        {
            Name = deviceName,
            Ip = ip,
            BlockCount = rackCount,
        };

        var bindings = new List<IoPointBinding>(rackCount * levelsPerRack * pointsPerLevel * 2);
        for (int block = 1; block <= rackCount; block++)
        {
            int rack = rackStart + block - 1;   // 块 K ↔ 全局架号（第二台 rackStart=11 → 块1=架11）
            for (int level = 1; level <= levelsPerRack; level++)
            {
                int bitBase = (level - 1) * bitsPerLevel;
                var levelName = LevelName(level);
                // 输入组内：0=左光栅OK 1=右光栅OK 2=托盘有无（bit3 预留不生成，配置窗可手工补）
                bindings.Add(Make(block, bitBase + 0, true, IoPointRole.RackLightL, rack, level, levelName));
                bindings.Add(Make(block, bitBase + 1, true, IoPointRole.RackLightR, rack, level, levelName));
                bindings.Add(Make(block, bitBase + 2, true, IoPointRole.TrayDetect, rack, level, levelName));
                // 输出组内：0=红 1=黄 2=绿（bit3 预留同上）
                bindings.Add(Make(block, bitBase + 0, false, IoPointRole.LampRed, rack, level, levelName));
                bindings.Add(Make(block, bitBase + 1, false, IoPointRole.LampYellow, rack, level, levelName));
                bindings.Add(Make(block, bitBase + 2, false, IoPointRole.LampGreen, rack, level, levelName));
            }
        }

        var config = new PointTableConfig { Devices = { device } };
        device.Bindings = bindings;
        var check = PointTableValidator.Validate(config);
        if (!check.Ok)
            throw new InvalidOperationException($"缺省模板生成异常：{check}");
        return config;
    }

    /// <summary>
    /// 一键生成"多台机台耦合器"模板（V0.0.41 现场口径：一台机台 10 列 × 上中下
    /// 3 层 = 30 仓位，每列 1 台町洋 NEMO GFNB-1A3A，一台管一列的 3 层）。
    ///
    /// 干什么：循环 <paramref name="deviceCount"/> 次调 <see cref="BuildDefaultTemplate"/>
    /// （每台 rackCount=1、起始架号=列号连续递增），IP 按首址+步长自动递增
    /// （现场实值 192.168.1.20~.29 即缺省参数），合成一个多设备 PointTableConfig
    /// 后整体过校验（跨设备架号查重）。现场拿到软件点一下"批量生成"，IP 都不用改。
    ///
    /// 为什么：单台模板一次只出一台，配 10 台要手工循环 10 轮"添加设备→改名→
    /// 改 IP→生成"——批量入口是"点点鼠标配完 10 台"的核心。
    ///
    /// 怎么改：GFNB 寄存器起始地址（DI 0x1000 / DO 0x2000）按 iO-GRID 家族惯例
    /// 占位，现场实测后在配置窗"设备高级参数"里改（配置键，代码零改动）；
    /// 若实测不同，改这里的缺省参数即可。
    /// </summary>
    /// <param name="deviceCount">耦合器台数（=列数，缺省 10）。</param>
    /// <param name="rackStart">起始全局架号（缺省 1；每台架号依次 +1，一台=一列=一架）。</param>
    /// <param name="levelsPerRack">每架层数（缺省 3：上/中/下层）。</param>
    /// <param name="ipFirst">首台 IP（缺省 192.168.1.20 = 现场 1 号耦合器现值）。</param>
    /// <param name="ipStep">IP 末段递增步长（缺省 1；0 仅允许单台，否则 IP 撞车抛异常）。</param>
    /// <param name="port">Modbus TCP 端口（缺省 502）。</param>
    /// <param name="diStartAddress">DI 输入寄存器起始地址占位（FC04，缺省 0x1000）。</param>
    /// <param name="doStartAddress">DO 输出寄存器起始地址占位（FC03 读/FC06 写，缺省 0x2000）。</param>
    public static PointTableConfig BuildMachineTemplate(
        int deviceCount = 10,
        int rackStart = 1,
        int levelsPerRack = 3,
        string ipFirst = "192.168.1.20",
        int ipStep = 1,
        int port = 502,
        ushort diStartAddress = 0x1000,
        ushort doStartAddress = 0x2000)
    {
        if (deviceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(deviceCount), "台数 ≥1");
        if (ipStep < 0)
            throw new ArgumentOutOfRangeException(nameof(ipStep), "IP 步长 ≥0");
        if (deviceCount > 1 && ipStep == 0)
            throw new ArgumentOutOfRangeException(nameof(ipStep),
                "多台设备 IP 步长不能为 0（IP 会全部相同，现场连不上）。");
        if (!System.Net.IPAddress.TryParse(ipFirst, out var first))
            throw new ArgumentException($"IP 首址非法：'{ipFirst}'", nameof(ipFirst));

        var config = new PointTableConfig();
        for (int i = 0; i < deviceCount; i++)
        {
            // 每台 1 块承载 1 架（GFNB-1A3A = 16DI+16DO 正好一块），架号=列号连续排。
            var single = BuildDefaultTemplate(
                $"IO耦合器{i + 1}", NextIp(first, i * ipStep), 1, levelsPerRack, rackStart + i);
            var device = single.Devices[0];
            device.Port = port;
            device.BlockCount = 1;                  // 一台 = 1 块（16DI+16DO）
            device.DiStartAddress = diStartAddress; // GFNB 占位口径，现场实测后配置窗改
            device.DoStartAddress = doStartAddress;
            // 超时 300ms：多台部署轮询线程逐台串行建连，3s/台会让 10 台首轮
            // 建连拖 30s（不可达 IP 同步超时）；现场同网段 LAN 建连 <10ms，
            // 300ms 超时既保快速失败/快重连，也保轮询节拍（配置窗可改）。
            device.TimeoutMs = 300;
            config.Devices.Add(device);
        }

        var check = PointTableValidator.Validate(config);
        if (!check.Ok)
            throw new InvalidOperationException($"机台模板生成异常：{check}");
        return config;
    }

    /// <summary>IP 末段递增：把首址按四字节整数算 offset 再转回点分十进制（1.20+10=1.30，跨段进位也正确）。</summary>
    private static string NextIp(System.Net.IPAddress first, int offset)
    {
        byte[] b = first.GetAddressBytes();
        if (b.Length != 4)
            throw new ArgumentException("仅支持 IPv4 地址", nameof(first));
        uint value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        value += (uint)offset;
        return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }

    private static IoPointBinding Make(int block, int bit, bool isInput,
        IoPointRole role, int rack, int level, string levelName)
    {
        // 功能名抄 IO 表文本（架/层/角色拼装），现场对照纸质表核对更直观。
        return new IoPointBinding
        {
            Block = block,
            Bit = bit,
            IsInput = isInput,
            Role = role,
            Rack = rack,
            Level = level,
            FunctionName = $"固化架{rack}{levelName}{IoPointRoleInfo.ToText(role)}",
        };
    }

    private static string LevelName(int level) => level switch
    {
        1 => "上层",
        2 => "中层",
        _ => "下层",
    };
}
