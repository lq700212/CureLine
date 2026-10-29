// =========================================================================
// PasswordHasher：密码哈希（PBKDF2-SHA256，账号体系的锁芯）
//
// 干什么：密码→存库只存"盐 + 哈希"，登录时重算比对；库文件被拷走也反推不出密码。
// 为什么 PBKDF2：单向 + 可调迭代次数，拖库后暴力破解成本极高；
//   .NET 内置 Rfc2898DeriveBytes，无第三方依赖，跨平台可单元测试。
//   另混全局 Pepper（代码常量，纵深防御：只拿到 db 文件的人连弱口令都秒破不了；
//   注意 Pepper 防反编译不防拖库，真威胁模型靠"独立盐 + 高迭代"，注释说透）。
// 怎么改：
//   - 参数只升不降（Iterations SaltSize HashSize 改大后，老哈希照常验，
//     下次改密码时自动按新参数重算，见 UserAccountStore）。
//   - 新密码规则收敛在 ValidateNewPassword（长度下限 + 不得与旧相同），
//     UI 与测试都调它，别各写一套。
// =========================================================================

using System.Security.Cryptography;
using System.Text;

namespace CureLine.Core.Security;

public static class PasswordHasher
{
    /// <summary>PBKDF2 迭代次数（登录低频操作，10 万次约 100ms，可接受）。</summary>
    public const int Iterations = 120_000;

    /// <summary>每用户独立盐长度（16 字节，CSPRNG 生成，防彩虹表）。</summary>
    public const int SaltSize = 16;

    /// <summary>哈希输出长度（SHA256 取 32 字节全长）。</summary>
    public const int HashSize = 32;

    /// <summary>新密码最短长度（工控现场键盘输，6 位是可用与安全的折中）。</summary>
    public const int MinPasswordLength = 6;

    /// <summary>
    /// 全局 Pepper（应用级纵深：哈希输入 = Pepper + 密码；只拷走 db 文件
    /// 的人无法用通用工具爆破。本常量反编译可见，不算机密，威胁模型见类头）。
    /// 字面量含旧项目名 "PlcMesBridge" 刻意不随 V0.0.24 更名修改：盐值内部
    /// 一致性优先，换字面量=现网 users/operators 全部哈希复核失败，全体账号锁死。
    /// </summary>
    private const string Pepper = "PlcMesBridge.v1::field-panel";

    /// <summary>建新哈希（注册/改密码时调）。返回 (哈希, 盐)，调用方存 users 表。</summary>
    public static (byte[] Hash, byte[] Salt) CreateHash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        return (Compute(password, salt, Iterations), salt);
    }

    /// <summary>
    /// 校验（登录时调）。哈希比对用恒定时间比较，防时序侧信道；
    /// 输入空/长度不对直接 false，不抛（登录失败就是 false，日志只记边沿）。
    /// </summary>
    public static bool Verify(string? password, byte[]? hash, byte[]? salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || hash == null || salt == null)
            return false;
        if (hash.Length != HashSize || salt.Length == 0 || iterations <= 0)
            return false;
        byte[] calc;
        try
        {
            calc = Compute(password, salt, iterations);
        }
        catch
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(calc, hash);
    }

    /// <summary>
    /// 新密码规则（改密码统一入口）：非空、≥6 位、不得与旧密码相同。
    /// 返回 null 表示通过，否则返回中文原因（UI 直接展示）。
    /// </summary>
    public static string? ValidateNewPassword(string? newPassword, string? oldPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
            return "新密码不能为空";
        if (newPassword.Length < MinPasswordLength)
            return $"新密码至少 {MinPasswordLength} 位";
        if (string.Equals(newPassword, oldPassword, StringComparison.Ordinal))
            return "新密码不能与旧密码相同";
        return null;
    }

    private static byte[] Compute(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(Pepper + password),
            salt, iterations, HashAlgorithmName.SHA256, HashSize);
}
