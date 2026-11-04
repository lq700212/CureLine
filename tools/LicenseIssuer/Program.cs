// =========================================================================
// Program：签发工具入口（厂商内部，两种用法）
//
// 双击运行                       → WPF 图形界面（粘贴设备码 → 生成激活码）
// LicenseIssuer.exe --generate   → 生成密钥对：私钥落 exe 同目录
//                                  LicenseSignKey.dat，stdout 打印 PUBKEY=公钥
// LicenseIssuer.exe --issue 设备码 P|T 天数
//                                → 命令行签发：stdout 打印 CODE=激活码
//                                  （回归探针/厂商脚本化签发）
// 返回码：0=成功；2=私钥缺失/已存在拒绝覆盖；1=其它异常/参数错误。
//
// 为什么自己写 Main：WPF 自动生成的入口不读命令行参数；且 WinExe 默认没有
// 控制台，从 cmd 调 --issue 时用 AttachConsole 挂到父控制台才能看到输出。
// =========================================================================

using System.Windows;
using CureLine.Core.Security;

namespace LicenseIssuer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // 命令行模式：挂父控制台（双击场景无父控制台，AttachConsole 失败无害）
            if (args.Length > 0)
                NativeConsole.Attach();

            if (args.Length == 1 && Is(args[0], "--generate"))
            {
                string pub = SignCore.GenerateKeyPair();
                Console.WriteLine("PUBKEY=" + pub);
                return 0;
            }

            if (args.Length == 4 && Is(args[0], "--issue"))
            {
                if (!SignCore.HasPrivateKey())
                {
                    Console.Error.WriteLine("私钥缺失（LicenseSignKey.dat）");
                    return 2;
                }
                if (!LicenseCodec.TryParseDeviceCode(args[1], out ushort productId,
                        out string fp8, out string ch8))
                {
                    Console.Error.WriteLine("设备码格式不正确（CURE-… 或 HUAJI-…）");
                    return 1;
                }
                byte type = Is(args[2], "P")
                    ? LicenseCodec.TypePermanent : LicenseCodec.TypeTrial;
                if (!byte.TryParse(args[3], out byte days))
                {
                    Console.Error.WriteLine("天数非法");
                    return 1;
                }
                if (type == LicenseCodec.TypeTrial && (days < 1 || days > 255))
                {
                    Console.Error.WriteLine("试用天数必须是 1~255");
                    return 1;
                }
                byte[] payload = LicenseCodec.BuildPayload(
                    productId,
                    LicenseCodec.HexToBytes(fp8),
                    LicenseCodec.HexToBytes(ch8),
                    type, days, LicenseCodec.DateToDays(DateTime.Now));
                byte[] sig = SignCore.Sign(payload);
                Console.WriteLine("CODE=" + LicenseCodec.EncodeActivationCode(payload, sig));
                return 0;
            }

            if (args.Length > 0)
            {
                Console.WriteLine("用法：--generate | --issue 设备码 P|T 天数");
                return 1;
            }

            // 图形界面模式
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow());
            return 0;
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine("LicenseIssuer 异常：" + ex.Message); } catch { }
            return 1;
        }
    }

    private static bool Is(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>WinExe 挂父控制台（P/Invoke kernel32，失败静默——双击场景正常）。</summary>
    private static class NativeConsole
    {
        private const int AttachParentProcess = -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        public static void Attach()
        {
            try { _ = AttachConsole(AttachParentProcess); } catch { }
        }
    }
}
