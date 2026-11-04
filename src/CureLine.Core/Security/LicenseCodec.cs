// =========================================================================
// LicenseCodec：软件授权激活码的编解码与验签核心（消费方 + 签发工具共用本文件）
//
// 【为什么需要】本项目原先只有 HSL 通讯组件授权（hsl.dat），那是"通讯库能不能跑"；
//   本文件管的是"CureLine 软件本身能不能跑"——两套授权互不相干，勿混。
//   方案与 HJVision 同源：ECDsa P-256 非对称签名。厂商持私钥签发，程序内置公钥
//   验签；反编译只能拿到公钥，无法离线伪造激活码。
//
// 【产品隔离（本文件最重要的扩展）】两个产品共用同一把密钥对，靠 payload
//   保留位 [14..15] 的产品 ID 区分：
//     HJVision = 0xFFFF（历史码原值，保持已发码继续有效）
//     CureLine = 0x434C（ASCII 'C''L'）
//   主程序验签后必须再比对产品 ID（LicenseGate 负责），拿 HJVision 的激活码
//   到 CureLine 上会落在"激活码不属于本软件"的拒绝分支，反之亦然。
//
// 【设备码格式】<前缀>-<指纹8hex>-<挑战8hex>-<校验4hex>（全部大写 hex）
//   CureLine 前缀 = CURE；HJVision 前缀 = HUAJI（历史口径）。
//   指纹   = 机器多因子指纹 SHA256 前 4 字节（采集见 MachineFingerprint）
//   挑战值 = 激活窗每次打开随机重生成的 4 字节（防重放：窃录的旧码对不上新挑战）
//   校验   = SHA256(前缀 + "1" + 指纹 + 挑战) 前 2 字节——前缀纳入哈希后
//            HUAJI 的结果与旧版完全一致（"HUAJI1" 原样），CureLine 用 "CURE1"。
//
// 【激活码格式】Base32Crockford(payload[16B] + ECDsa 签名[64B]) → 128 字符，
//   按 5 字符一组用 '-' 分隔展示（粘贴时自动忽略分隔符/空白/大小写）。
//     payload[0..3]   指纹 4 字节          —— 必须等于本机指纹
//     payload[4..7]   挑战值 4 字节        —— 必须等于激活窗当前挑战值
//     payload[8]      类型 0x50='P' 永久 / 0x54='T' 试用
//     payload[9]      试用天数 1..255（永久填 0）
//     payload[10..13] 签发起点的天数（自 2020-01-01 的 uint32 小端；到期=起点+天数）
//     payload[14..15] 产品 ID（大端：HJVision=0xFFFF / CureLine=0x434C）
//
// 【Base32 选 Crockford 变体】字母表去掉 I/L/O/U（易与 1/0/手抄混淆），解码端把
//   O→0、I/L→1 宽容还原——现场人员抄码/报码的最常见错误自动免疫。
//
// 【安全边界（诚实声明）】公钥与验签逻辑在源码可见；防护目标是"无法离线伪造
//   激活码"（需要私钥），不追求对抗"重打包绕过验签"级别攻击。
//
// 【怎么换密钥对】签发工具"生成密钥对"→ 把输出的公钥 Base64 串替换下方
//   PubKeyB64 常量并重编两个产品的主程序；私钥文件 LicenseSignKey.dat 留在
//   厂商签发工具目录（.gitignore 禁入库）。丢失私钥 = 已发激活码全部作废。
//
// 【与 HJVision 的关系】本文件是 CureLine 的单一事实源；签发工具
//   tools/LicenseIssuer 以 Compile Include 链接本文件，签出来的码格式永不漂移。
// =========================================================================

using System.Security.Cryptography;
using System.Text;

namespace CureLine.Core.Security;

public static class LicenseCodec
{
    /// <summary>
    /// 厂商签名公钥（CNG EccPublicBlob 的 Base64）。与 HJVision 共用同一把密钥对。
    /// 【换钥流程】签发工具重新生成密钥对 → 私钥 LicenseSignKey.dat 留厂商 →
    /// 本常量替换并重编两个产品；换钥后旧激活码全部失效（须重发）。
    /// </summary>
    public const string PubKeyB64 =
        "RUNTMSAAAAC3yK15WAS0/x4zoT3NM94LQKUXUxO5vURNnZqQGf04ADsYST7M/vzG/5bm9mf4IqCyGREKBPZAis1I5uEp93k4";

    /// <summary>payload 长度（字节）。</summary>
    public const int PayloadLength = 16;

    /// <summary>ECDsa P-256 签名长度（r|s 各 32 字节）。</summary>
    public const int SignatureLength = 64;

    /// <summary>产品 ID：CureLine（ASCII "CL"，本项目）。</summary>
    public const ushort ProductCureLine = 0x434C;

    /// <summary>产品 ID：HJVision（历史 0xFFFF，保持已签发激活码继续有效）。</summary>
    public const ushort ProductHuaJiVision = 0xFFFF;

    /// <summary>CureLine 设备码前缀。</summary>
    public const string DeviceCodePrefixCureLine = "CURE";

    /// <summary>HJVision 设备码前缀（历史口径）。</summary>
    public const string DeviceCodePrefixHuaJiVision = "HUAJI";

    /// <summary>类型：永久授权。</summary>
    public const byte TypePermanent = 0x50;   // 'P'

    /// <summary>类型：试用授权（天数见 payload[9]）。</summary>
    public const byte TypeTrial = 0x54;       // 'T'

    /// <summary>Crockford Base32 字母表（无 I/L/O/U）。</summary>
    private const string B32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // ════════════════ 产品标识 ════════════════

    /// <summary>产品 ID → 设备码前缀。未知产品返回 null（调用方按非法处理）。</summary>
    public static string? GetDeviceCodePrefix(ushort productId) => productId switch
    {
        ProductCureLine => DeviceCodePrefixCureLine,
        ProductHuaJiVision => DeviceCodePrefixHuaJiVision,
        _ => null,
    };

    /// <summary>产品 ID → 展示名（签发工具/激活窗文案用）。未知产品返回 "未知产品"。</summary>
    public static string GetProductName(ushort productId) => productId switch
    {
        ProductCureLine => "CureLine",
        ProductHuaJiVision => "HJVision",
        _ => "未知产品",
    };

    // ════════════════ Crockford Base32 ════════════════

    /// <summary>字节数组 → Crockford Base32 大写字符串（无填充；输入长度任意）。</summary>
    public static string Base32Encode(byte[] data)
    {
        if (data == null || data.Length == 0) return "";
        // 每字符 5 bit：总输出 = ceil(bits/5)；先把整块按大端位流逐 5bit 取出
        long bitLen = (long)data.Length * 8;
        int outLen = (int)((bitLen + 4) / 5);
        char[] outBuf = new char[outLen];
        int bitPos = 0;
        for (int i = 0; i < outLen; i++)
        {
            // 拼当前 5bit（可能跨字节：从 bitPos 起连续取 5 位）
            int v = 0;
            for (int b = 0; b < 5; b++)
            {
                int byteIdx = (bitPos + b) >> 3;
                int bitInByte = 7 - ((bitPos + b) & 7);
                // 末尾不足 5bit 时按零位补齐（编码端补的 0，解码端会丢弃余数位）
                int bitSet = byteIdx < data.Length ? (data[byteIdx] >> bitInByte) & 1 : 0;
                v = (v << 1) | bitSet;
            }
            outBuf[i] = B32Alphabet[v];
            bitPos += 5;
        }
        return new string(outBuf);
    }

    /// <summary>
    /// Base32 字符串 → 字节数组。宽容解码：忽略空白/连字符、大小写不分、O→0、I/L→1。
    /// 非法字符返回 null（调用方按"激活码无效"处理）。
    /// </summary>
    public static byte[]? Base32Decode(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        // 规范化：去空白与连字符、大写、混淆字符还原
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
            if (c == 'O') c = '0';
            else if (c == 'I' || c == 'L') c = '1';
            int idx = B32Alphabet.IndexOf(c);
            if (idx < 0) return null;   // 字母表外的字符（如 U）= 非法输入
            sb.Append(c);
        }
        string norm = sb.ToString();
        if (norm.Length == 0) return null;
        int bitLen = norm.Length * 5;
        int byteLen = bitLen / 8;          // 丢弃末尾不满 8bit 的余数（编码端补零位）
        if (byteLen == 0) return null;
        byte[] outBuf = new byte[byteLen];
        int bitPos = 0;
        for (int i = 0; i < norm.Length; i++)
        {
            int v = B32Alphabet.IndexOf(norm[i]);
            for (int b = 0; b < 5; b++)
            {
                int bit = (v >> (4 - b)) & 1;
                int byteIdx = bitPos >> 3;
                if (byteIdx >= byteLen) break;   // 余数位（编码时补的 0）丢弃
                int bitInByte = 7 - (bitPos & 7);
                if (bit != 0) outBuf[byteIdx] |= (byte)(1 << bitInByte);
                bitPos++;
            }
        }
        return outBuf;
    }

    // ════════════════ hex 小工具 ════════════════

    /// <summary>字节数组 → 大写 hex 字符串。</summary>
    public static string BytesToHex(byte[] b)
    {
        if (b == null) return "";
        StringBuilder sb = new StringBuilder(b.Length * 2);
        for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2"));
        return sb.ToString();
    }

    /// <summary>hex 字符串 → 字节数组（大小写均可；奇数长度/非法字符返回 null）。</summary>
    public static byte[]? HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
        byte[] b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            int hi = HexVal(hex[i * 2]), lo = HexVal(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return null;
            b[i] = (byte)((hi << 4) | lo);
        }
        return b;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        return -1;
    }

    // ════════════════ 日期（自 2020-01-01 的天数） ════════════════

    private static readonly DateTime DayEpoch = new(2020, 1, 1);

    /// <summary>日期 → 自 2020-01-01 的天数（uint32，2100 年前无忧）。</summary>
    public static uint DateToDays(DateTime d)
    {
        TimeSpan sp = d.Date - DayEpoch;
        if (sp.Ticks < 0) return 0;
        return (uint)sp.Days;
    }

    /// <summary>自 2020-01-01 的天数 → 日期（0 点）。</summary>
    public static DateTime DaysToDate(uint days) => DayEpoch.AddDays(days);

    // ════════════════ 设备码 ════════════════

    /// <summary>
    /// 拼装设备码：&lt;前缀&gt;-&lt;指纹8hex&gt;-&lt;挑战8hex&gt;-&lt;校验4hex&gt;。
    /// productId 必须是已知产品（未知产品抛 ArgumentException——签发工具侧会先校验）。
    /// fp8/ch8 必须是 8 位 hex（MachineFingerprint / LicenseGate 生成，本方法只做拼装）。
    /// </summary>
    public static string BuildDeviceCode(ushort productId, string fp8Hex, string challenge8Hex)
    {
        string prefix = GetDeviceCodePrefix(productId)
            ?? throw new ArgumentException($"未知产品 ID：0x{productId:X4}", nameof(productId));
        // 先归一化成非空局部变量：既避免可空警告，也保证拼装与校验位用同一份文本
        string fp = (fp8Hex ?? "").ToUpperInvariant();
        string ch = (challenge8Hex ?? "").ToUpperInvariant();
        return prefix + "-" + fp + "-" + ch + "-" + DeviceChecksum(productId, fp, ch);
    }

    /// <summary>
    /// 设备码校验位：SHA256(前缀 + "1" + fp + ch) 前 2 字节大写 hex。
    /// 前缀纳入哈希保证两类设备码互不冒用；HUAJI 的结果与旧版逐字节一致
    /// （旧版硬编码 "HUAJI1"），历史设备码/激活码继续有效。
    /// </summary>
    public static string DeviceChecksum(ushort productId, string fp8Hex, string challenge8Hex)
    {
        string prefix = GetDeviceCodePrefix(productId)
            ?? throw new ArgumentException($"未知产品 ID：0x{productId:X4}", nameof(productId));
        using SHA256 sha = SHA256.Create();
        byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(prefix + "1"
            + (fp8Hex ?? "").ToUpperInvariant()
            + (challenge8Hex ?? "").ToUpperInvariant()));
        return BytesToHex(new byte[] { h[0], h[1] });
    }

    /// <summary>
    /// 解析设备码（宽容处理大小写/内部空白/手抄混淆字符：O→0、I/L→1）。
    /// 自动识别 CURE / HUAJI 两种前缀，productId 带回识别结果（签发工具用）。
    /// 返回 false = 格式错误或校验位不对（明显不是本体系的设备码 / 抄错了）。
    /// </summary>
    public static bool TryParseDeviceCode(
        string? text, out ushort productId, out string fp8Hex, out string challenge8Hex)
    {
        productId = 0;
        fp8Hex = challenge8Hex = "";
        if (string.IsNullOrEmpty(text)) return false;

        // 规范化第一层：大写 + 去空白（前缀里的 I/O 不能动）
        StringBuilder sb = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            char c = raw;
            if (c is ' ' or '\t' or '\r' or '\n') continue;
            if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
            sb.Append(c);
        }
        string s = sb.ToString();

        // 前缀识别（HUAJI 必须排在 CURE 前逐一精确匹配，互不前缀包含，顺序无妨；
        // 这里显式列出两产品，新增产品时在这里加一行即可）。
        ushort[] products = { ProductCureLine, ProductHuaJiVision };
        ushort matched = 0;
        string? prefix = null;
        foreach (ushort pid in products)
        {
            string p = GetDeviceCodePrefix(pid)!;
            if (s.StartsWith(p + "-", StringComparison.Ordinal))
            {
                matched = pid;
                prefix = p;
                break;
            }
        }
        if (prefix == null) return false;

        string body = s.Substring(prefix.Length + 1);
        // 规范化第二层：body 内手抄混淆还原（hex 本无 I/L/O，还原安全）
        StringBuilder b2 = new StringBuilder(body.Length);
        foreach (char raw in body)
        {
            char c = raw;
            if (c == 'O') c = '0';
            else if (c == 'I' || c == 'L') c = '1';
            b2.Append(c);
        }
        string[] parts = b2.ToString().Split(
            new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0].Length != 8 || parts[1].Length != 8 || parts[2].Length != 4)
            return false;
        if (HexToBytes(parts[0]) == null || HexToBytes(parts[1]) == null || HexToBytes(parts[2]) == null)
            return false;
        if (DeviceChecksum(matched, parts[0], parts[1]) != parts[2])
            return false;   // 抄错一位立刻拦下

        productId = matched;
        fp8Hex = parts[0];
        challenge8Hex = parts[1];
        return true;
    }

    // ════════════════ 激活码 payload ════════════════

    /// <summary>
    /// 拼装 16 字节激活码 payload（签发工具用；字段布局见类头注释）。
    /// fp4/ch4 各 4 字节；type=TypePermanent/TypeTrial；days=试用天数（永久填 0）；
    /// startDays=签发日期的 DateToDays 值（到期=DaysToDate(startDays)+days 天）。
    /// </summary>
    public static byte[] BuildPayload(
        ushort productId, byte[]? fp4, byte[]? ch4, byte type, byte days, uint startDays)
    {
        if (GetDeviceCodePrefix(productId) == null)
            throw new ArgumentException($"未知产品 ID：0x{productId:X4}", nameof(productId));
        byte[] p = new byte[PayloadLength];
        if (fp4 != null && fp4.Length >= 4) Buffer.BlockCopy(fp4, 0, p, 0, 4);
        if (ch4 != null && ch4.Length >= 4) Buffer.BlockCopy(ch4, 0, p, 4, 4);
        p[8] = type;
        p[9] = days;
        p[10] = (byte)(startDays & 0xFF);
        p[11] = (byte)((startDays >> 8) & 0xFF);
        p[12] = (byte)((startDays >> 16) & 0xFF);
        p[13] = (byte)((startDays >> 24) & 0xFF);
        p[14] = (byte)(productId >> 8);        // 产品 ID 高字节（大端）
        p[15] = (byte)(productId & 0xFF);      // 产品 ID 低字节
        return p;
    }

    /// <summary>
    /// 解析 16 字节 payload；返回 false = 长度不对/类型未知。
    /// 注意：本方法**不限制产品 ID**（签发工具要解析两个产品），
    /// 产品归属由调用方（LicenseGate / 签发工具）比对——
    /// 主程序侧必须显式检查 productId == 本产品，这是产品隔离的关键闸门。
    /// </summary>
    public static bool TryParsePayload(
        byte[]? payload, out ushort productId, out byte[] fp4, out byte[] ch4,
        out byte type, out byte days, out uint startDays)
    {
        productId = 0;
        fp4 = ch4 = Array.Empty<byte>();
        type = 0;
        days = 0;
        startDays = 0;
        if (payload == null || payload.Length != PayloadLength) return false;
        if (payload[8] != TypePermanent && payload[8] != TypeTrial) return false;
        productId = (ushort)((payload[14] << 8) | payload[15]);
        fp4 = new byte[] { payload[0], payload[1], payload[2], payload[3] };
        ch4 = new byte[] { payload[4], payload[5], payload[6], payload[7] };
        type = payload[8];
        days = payload[9];
        startDays = (uint)(payload[10] | (payload[11] << 8)
            | (payload[12] << 16) | (payload[13] << 24));
        return true;
    }

    // ════════════════ 激活码整体编解码 + 验签 ════════════════

    /// <summary>payload(16B)+签名(64B) → 分组排版激活码（5 字符一组 '-' 分隔）。</summary>
    public static string EncodeActivationCode(byte[] payload, byte[] signature)
    {
        byte[] all = new byte[PayloadLength + SignatureLength];
        Buffer.BlockCopy(payload, 0, all, 0, PayloadLength);
        Buffer.BlockCopy(signature, 0, all, PayloadLength, SignatureLength);
        string b32 = Base32Encode(all);
        // 5 字符一组 '-' 分隔（128 字符 → 26 段；粘贴端自动忽略 '-'）
        StringBuilder sb = new StringBuilder(b32.Length + b32.Length / 5 + 1);
        for (int i = 0; i < b32.Length; i++)
        {
            if (i > 0 && i % 5 == 0) sb.Append('-');
            sb.Append(b32[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析激活码文本（宽容：忽略空白/'-'/大小写/手抄混淆字符），拆出 payload+签名。
    /// 返回 false = 字符非法或长度不对（不是本体系激活码）——注意这步**不验签**，
    /// 验签必须另调 VerifyActivation。
    /// </summary>
    public static bool TryDecodeActivationCode(
        string? text, out byte[] payload, out byte[] signature)
    {
        payload = signature = Array.Empty<byte>();
        byte[]? all = Base32Decode(text ?? "");
        if (all == null || all.Length != PayloadLength + SignatureLength) return false;
        payload = new byte[PayloadLength];
        signature = new byte[SignatureLength];
        Buffer.BlockCopy(all, 0, payload, 0, PayloadLength);
        Buffer.BlockCopy(all, PayloadLength, signature, 0, SignatureLength);
        return true;
    }

    /// <summary>
    /// 用内置公钥验签（主程序侧唯一验签口）。
    /// 公钥未回填（还是占位串）时直接返回 false——只发生在开发机未生成密钥对时。
    /// </summary>
    public static bool VerifyActivation(byte[] payload, byte[] signature)
    {
        if (payload == null || signature == null) return false;
        if (signature.Length != SignatureLength) return false;
        if (string.IsNullOrEmpty(PubKeyB64) || PubKeyB64 == "REPLACE_WITH_PUBKEY") return false;
        try
        {
            byte[] pubBlob = Convert.FromBase64String(PubKeyB64);
            using CngKey k = CngKey.Import(pubBlob, CngKeyBlobFormat.EccPublicBlob);
            using ECDsaCng verifier = new(k);
            verifier.HashAlgorithm = CngAlgorithm.Sha256;
            return verifier.VerifyData(payload, signature);
        }
        catch (Exception)
        {
            // 公钥串被改坏/CNG 环境异常——按"验证不通过"处理（拒绝激活），不吞判断
            return false;
        }
    }
}
