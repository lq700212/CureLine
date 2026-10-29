// =========================================================================
// RememberMeProtector：记住密码的加密（Windows DPAPI，CurrentUser 范围）
//
// 干什么："记住密码"必须能解回明文回填密码框，单向哈希做不到，所以用
//   可逆加密；密钥不落地（DPAPI 密钥由 Windows 管，本机本用户才能解），
//   ini 里只存 Base64 密文，拷走 ini 也解不开。
// 为什么 P/Invoke 不引包：crypt32 CryptProtectData/UnprotectData 十几行封好，
//   不给现场加 NuGet 包（离线装机最省心）；非 Windows（Linux 调试）
//   直接返回 null（本次不记住），不抛到 UI。
// 怎么改：
//   - Entropy 是应用标识（换 salt 语义：别的程序拿 DPAPI 也解不开本 token）。
//   - Protect/Unprotect 失败一律 null（换用户/换机器/ini 手改坏/非 Windows），
//     调用方按"无记忆"处理；绝不吞了异常还写半截（要么整串要么没有）。
// =========================================================================

using System.Runtime.InteropServices;

namespace CureLine.Core.Security;

public static class RememberMeProtector
{
    /// <summary>
    /// DPAPI 附加熵（应用标识，防跨程序解密）。字面量含旧项目名 "PlcMesBridge"
    /// 刻意不随 V0.0.24 更名修改：熵值一变，已存的"记住密码"密文就解不开。
    /// </summary>
    private static readonly byte[] Entropy =
        System.Text.Encoding.UTF8.GetBytes("PlcMesBridge.RememberMe.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref Blob pDataIn, string? szDataDescr, ref Blob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref Blob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref Blob pDataIn, IntPtr ppszDataDescr, ref Blob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref Blob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密（记住勾选时调）。成功返回 Base64 密文，失败返回 null。</summary>
    public static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return null;
        try
        {
            byte[] plain = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] cipher = ProtectBytes(plain);
            CryptographicClear(plain);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            // DllNotFound（非 Windows）/ 拒绝访问等：本次不记住，不惊动 UI。
            return null;
        }
    }

    /// <summary>解密（登录窗预填时调）。坏串/换机/换用户一律 null（按无记忆）。</summary>
    public static string? Unprotect(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        try
        {
            byte[] cipher = Convert.FromBase64String(token.Trim());
            byte[] plain = UnprotectBytes(cipher);
            string password = System.Text.Encoding.UTF8.GetString(plain);
            CryptographicClear(plain);
            return password;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ProtectBytes(byte[] plain)
    {
        Blob dataIn = ToBlob(plain);
        Blob entropy = ToBlob(Entropy);
        Blob dataOut = default;
        try
        {
            if (!CryptProtectData(ref dataIn, null, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 0, ref dataOut))
                throw new InvalidOperationException("CryptProtectData failed");
            return FromBlob(dataOut);
        }
        finally
        {
            FreeInput(ref dataIn);
            FreeInput(ref entropy);
            FreeOutput(ref dataOut);
        }
    }

    private static byte[] UnprotectBytes(byte[] cipher)
    {
        Blob dataIn = ToBlob(cipher);
        Blob entropy = ToBlob(Entropy);
        Blob dataOut = default;
        try
        {
            if (!CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, 0, ref dataOut))
                throw new InvalidOperationException("CryptUnprotectData failed");
            return FromBlob(dataOut);
        }
        finally
        {
            FreeInput(ref dataIn);
            FreeInput(ref entropy);
            FreeOutput(ref dataOut);
        }
    }

    private static Blob ToBlob(byte[] bytes)
    {
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new Blob { cbData = bytes.Length, pbData = ptr };
    }

    private static byte[] FromBlob(Blob blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero)
            return Array.Empty<byte>();
        byte[] bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
        return bytes;
    }

    /// <summary>释放入参（AllocHGlobal 配 FreeHGlobal）。</summary>
    private static void FreeInput(ref Blob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
        }
        blob.cbData = 0;
    }

    /// <summary>释放 DPAPI 出参（系统用 LocalAlloc 配 LocalFree，别混）。</summary>
    private static void FreeOutput(ref Blob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            LocalFree(blob.pbData);
            blob.pbData = IntPtr.Zero;
        }
        blob.cbData = 0;
    }

    private static void CryptographicClear(byte[] bytes)
    {
        Array.Clear(bytes, 0, bytes.Length);
    }
}
