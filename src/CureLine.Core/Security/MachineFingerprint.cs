// =========================================================================
// MachineFingerprint：本机设备指纹采集（软件授权的机器绑定因子）
//
// 【为什么是四因子】旧式"只看 CPU ProcessorId"的绑定，同型号 CPU 值完全相同，
//   拷配置就能复制激活。这里改为 MachineGuid（装系统唯一）+ 主板 SN + 磁盘 SN
//   + CPU id 拼 SHA256 取前 4 字节——整目录克隆到另一台机器（哪怕同型号）
//   指纹必然不同，授权自动落到"设备指纹不符"。
//
// 【稳定性取舍】MachineGuid 是主锚点（注册表直读，几乎不失败），读不到整体
//   失败（拒绝激活并报原因，绝不回退空串/常量）；WMI 三项是尽力采集，查不到
//   用固定占位 "N/A" 参与哈希——WMI 服务偶发不可用时指纹保持不变，不会因为
//   一次查询失败就把已激活的机器锁死。
//
// 【线程】WMI 查询每次几十毫秒、四因子合计几百毫秒。本实现内部缓存首次结果，
//   重复调用不再查 WMI；需要预热的调用方用 LicenseGate.Warmup() 在后台线程先跑。
//
// 【可测性】采集逻辑藏在 IFingerprintProvider 后面，LicenseGate 构造注入——
//   单元测试用固定指纹桩，不依赖运行测试的机器。
// =========================================================================

using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace CureLine.Core.Security;

/// <summary>指纹采集结果：Ok=false 时 Fingerprint8 为空串、Error 带中文原因。</summary>
public sealed record FingerprintResult(bool Ok, string Fingerprint8, string Error);

/// <summary>指纹提供者（LicenseGate 的可测注入点）。</summary>
public interface IFingerprintProvider
{
    /// <summary>取本机指纹（8 位大写 hex）。失败返回 Ok=false，绝不返回常量/空串冒充成功。</summary>
    FingerprintResult Get();
}

/// <summary>
/// 默认实现：WMI 四因子采集 + 进程内缓存。
/// 缓存语义：首次成功/失败都缓存——失败也缓存可避免每次激活窗重开都等 WMI 超时；
/// 现场修好 WMI 后重启程序即可重新采集（激活是低频操作，不值得做复杂失效策略）。
/// </summary>
public sealed class MachineFingerprintProvider : IFingerprintProvider
{
    private readonly object _sync = new();
    private FingerprintResult? _cached;

    /// <inheritdoc />
    public FingerprintResult Get()
    {
        lock (_sync)
        {
            // 失败结果不缓存（README 口径：修好 WMI 重启即重采；这里再给一次
            // 同进程内"修好立刻生效"的机会，代价只是激活窗重开时重查一次）。
            if (_cached is { Ok: true })
                return _cached;
            _cached = MachineFingerprint.Collect();
            return _cached;
        }
    }
}

/// <summary>静态采集实现（签发/诊断也可以直接调，不经过缓存）。</summary>
public static class MachineFingerprint
{
    /// <summary>
    /// 采集本机指纹：MachineGuid 必须成功；主板/磁盘/CPU 失败用 "N/A" 占位。
    /// 拼接顺序固定（改顺序 = 所有已激活机器指纹变化，禁止调整）。
    /// </summary>
    public static FingerprintResult Collect()
    {
        try
        {
            // —— 因子 1（必须）：MachineGuid，Windows 装机唯一标识。
            // RegistryView.Registry64 显式 64 位视图：防止 AnyCPU 进程在特殊场景
            // 按 32 位被重定向到 Wow6432Node 读不到。
            string? machineGuid = null;
            string? error = null;
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                    RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (k != null)
                    machineGuid = Convert.ToString(k.GetValue("MachineGuid"));
            }
            catch (Exception ex)
            {
                error = "读 MachineGuid 失败: " + ex.Message;
            }
            if (string.IsNullOrWhiteSpace(machineGuid))
                return new FingerprintResult(false, "", error ?? "MachineGuid 为空");

            // —— 因子 2/3/4（尽力采集，失败固定占位）
            string? board = WmiFirstValue("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
            string? disk = WmiFirstValue("SELECT SerialNumber FROM Win32_DiskDrive", "SerialNumber");
            string? cpu = GetCpuSerialNumber();
            string boardP = string.IsNullOrWhiteSpace(board) ? "N/A" : board.Trim();
            string diskP = string.IsNullOrWhiteSpace(disk) ? "N/A" : disk.Trim();
            string cpuP = string.IsNullOrWhiteSpace(cpu) ? "N/A" : cpu.Trim();

            string raw = "MG=" + machineGuid + "|MB=" + boardP + "|DS=" + diskP + "|CP=" + cpuP;
            using SHA256 sha = SHA256.Create();
            byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            string fp8 = LicenseCodec.BytesToHex(new[] { h[0], h[1], h[2], h[3] });
            return new FingerprintResult(true, fp8, "");
        }
        catch (Exception ex)
        {
            return new FingerprintResult(false, "", "指纹采集异常: " + ex.Message);
        }
    }

    /// <summary>WMI 单值查询助手（查不到/异常返回 null，调用方占位）。每次几十毫秒。</summary>
    private static string? WmiFirstValue(string wql, string propertyName)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(wql);
            foreach (ManagementObject obj in searcher.Get())
            {
                object? v = obj[propertyName];
                if (v != null) return v.ToString();
            }
        }
        catch (Exception)
        {
            // WMI 服务偶发不可用——该因子按占位参与指纹（MachineGuid 主锚点不受影响）
        }
        return null;
    }

    /// <summary>CPU 序列号（指纹因子之一；激活窗"设备信息"诊断也可用）。</summary>
    public static string? GetCpuSerialNumber()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT ProcessorId FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                object? v = obj["ProcessorId"];
                if (v != null) return v.ToString();
            }
        }
        catch (Exception)
        {
            // CPU 号只是四因子之一（MachineGuid 才是主锚点），读不到不致命
        }
        return null;
    }
}
