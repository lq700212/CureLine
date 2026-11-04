// =========================================================================
// SignCore：签名私钥管理（厂商内部工具专用）
//
// 干什么：生成 ECDsa P-256 密钥对；加载私钥对 payload 签名。
// 为什么单独存在：主程序只有公钥（LicenseCodec.VerifyActivation），私钥永远
//   不进入产品程序集——这是"客户无法离线伪造激活码"的全部根基。
// 怎么改：私钥文件固定 exe 同目录 LicenseSignKey.dat（Base64 CNG EccPrivateBlob）；
//   换密钥对 = 删旧文件 → 重新生成 → 把公钥串替换两个产品 LicenseCodec.PubKeyB64。
//
// 【红线】LicenseSignKey.dat 禁止入库/外发/随部署包分发；丢失 = 已发激活码
//   全部作废（必须换钥重发）。请多处离线备份。
// =========================================================================

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LicenseIssuer;

/// <summary>签名私钥管理（与 HJVision 签发工具同款 CNG 口径）。</summary>
public static class SignCore
{
    /// <summary>私钥文件完整路径（与签发工具 exe 同目录）。</summary>
    public static string KeyFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LicenseSignKey.dat");

    /// <summary>私钥是否就绪（缺文件=先"生成密钥对"或从厂商备份恢复）。</summary>
    public static bool HasPrivateKey()
    {
        try { return File.Exists(KeyFilePath); }
        catch { return false; }
    }

    /// <summary>
    /// 生成 P-256 密钥对：私钥落盘 LicenseSignKey.dat，返回公钥 Base64
    /// （替换主工程 LicenseCodec.PubKeyB64）。已有私钥时拒绝覆盖——换钥是重决策，
    /// 先手工删除旧文件并确认已发激活码作废影响。
    /// </summary>
    public static string GenerateKeyPair()
    {
        if (HasPrivateKey())
            throw new InvalidOperationException(
                "私钥文件已存在（" + KeyFilePath + "），换密钥对须先手工删除旧文件，"
                + "并确认已发激活码将全部作废。");
        // CNG 临时密钥：默认 Software KSP + null 名 + 显式允许明文导出
        //（HJVision 探针实锤：不设导出策略会报"不支持请求的操作"）。
        var cp = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport,
        };
        using CngKey k = CngKey.Create(CngAlgorithm.ECDsaP256, null, cp);
        byte[] privBlob = k.Export(CngKeyBlobFormat.EccPrivateBlob);
        byte[] pubBlob = k.Export(CngKeyBlobFormat.EccPublicBlob);
        File.WriteAllText(KeyFilePath, Convert.ToBase64String(privBlob), new UTF8Encoding(false));
        return Convert.ToBase64String(pubBlob);
    }

    /// <summary>加载私钥签名 payload（签发唯一入口）。返回 64 字节 ECDsa P-256 签名。</summary>
    public static byte[] Sign(byte[] payload)
    {
        string b64 = File.ReadAllText(KeyFilePath).Trim();
        using CngKey k = CngKey.Import(Convert.FromBase64String(b64), CngKeyBlobFormat.EccPrivateBlob);
        using ECDsaCng signer = new(k);
        signer.HashAlgorithm = CngAlgorithm.Sha256;
        return signer.SignData(payload);
    }
}
