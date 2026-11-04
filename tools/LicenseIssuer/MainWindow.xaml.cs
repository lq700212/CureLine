// =========================================================================
// MainWindow：签发工具界面（厂商内部）
//
// 干什么：粘贴客户设备码 → 自动识别产品（CURE/HUAJI）→ 选永久/试用天数 →
//   用私钥签发 → 展示激活码供整串复制。
// 为什么产品识别在这里做：两个产品共用一把密钥对，设备码前缀是产品归属的
//   唯一凭据；payload 产品 ID 必须与之一致（LicenseCodec.BuildPayload 同源），
//   签错产品会被对方主程序的产品隔离闸门拒绝。
// 怎么改：签发规则改 LicenseCodec（主程序与工具链接同一份）；本文件只做输入校验
//   与提示，不复制任何 payload 布局逻辑。
// =========================================================================

using System.Windows;
using System.Windows.Media;
using CureLine.Core.Security;

namespace LicenseIssuer;

public partial class MainWindow : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x7D, 0x3C));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x54, 0x00));
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x90));

    public MainWindow()
    {
        InitializeComponent();
        UpdateKeyState();
        UpdateProductHint();
        UpdateExpiryHint();
    }

    // ================= 输入联动 =================

    /// <summary>设备码变化：实时识别产品（合法设备码才显示产品名，否则提示格式）。</summary>
    private void OnDeviceChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateProductHint();

    private void UpdateProductHint()
    {
        if (string.IsNullOrWhiteSpace(DeviceBox.Text))
        {
            ProductText.Text = "识别产品：等待输入设备码";
            ProductText.Foreground = HintBrush;
            return;
        }
        if (LicenseCodec.TryParseDeviceCode(DeviceBox.Text, out ushort productId,
                out _, out _))
        {
            ProductText.Text = "识别产品：" + LicenseCodec.GetProductName(productId)
                + $"（产品 ID 0x{productId:X4}，设备码前缀 "
                + LicenseCodec.GetDeviceCodePrefix(productId) + "）";
            ProductText.Foreground = OkBrush;
        }
        else
        {
            ProductText.Text = "识别产品：设备码格式不正确或校验位不符"
                + "（让客户在激活窗整串复制，勿手抄）";
            ProductText.Foreground = WarnBrush;
        }
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e) => UpdateExpiryHint();

    private void OnDaysChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateExpiryHint();

    private void UpdateExpiryHint()
    {
        if (ExpiryText == null || DaysBox == null || TrialRadio == null)
            return;
        if (TrialRadio.IsChecked != true)
        {
            ExpiryText.Text = "永久授权不受到期日限制";
            return;
        }
        if (int.TryParse((DaysBox.Text ?? "").Trim(), out int days) && days >= 1 && days <= 255)
            ExpiryText.Text = "到期日：" + DateTime.Now.Date.AddDays(days).ToString("yyyy-MM-dd")
                + "（到期当天 24 点前仍可用）";
        else
            ExpiryText.Text = "试用天数须为 1~255 的整数";
    }

    // ================= 业务动作 =================

    private void OnPasteDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                SetStatus(WarnBrush, "剪贴板里没有文本，先让客户把设备码发过来。");
                return;
            }
            DeviceBox.Text = Clipboard.GetText().Trim();
            SetStatus(HintBrush, "设备码已从剪贴板粘贴，请确认产品识别结果后点【生成激活码】。");
        }
        catch (Exception ex)
        {
            SetStatus(WarnBrush, "读取剪贴板失败：" + ex.Message);
        }
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(CodeBox.Text))
            {
                SetStatus(WarnBrush, "还没有激活码可复制，请先点【生成激活码】。");
                return;
            }
            Clipboard.SetText(CodeBox.Text);
            SetStatus(OkBrush, "激活码已复制到剪贴板，整串发给客户即可。");
        }
        catch (Exception ex)
        {
            SetStatus(WarnBrush, "复制失败：" + ex.Message);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        DeviceBox.Clear();
        CodeBox.Clear();
        DaysBox.Text = "30";
        PermRadio.IsChecked = true;
        DeviceBox.Focus();
        SetStatus(HintBrush, "已清空，可粘贴下一台设备的码。");
    }

    /// <summary>
    /// 生成激活码：校验设备码（含校验位）→ 产品识别 → payload（productId 与
    /// 设备码前缀同源）→ 私钥签名 → Base32 分组编码。
    /// 私钥缺失不禁用主按钮，点下去给明确原因（禁用态按钮容易被误认为程序卡住）。
    /// </summary>
    private void OnIssue(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SignCore.HasPrivateKey())
            {
                SetStatus(WarnBrush, "未找到签名私钥（LicenseSignKey.dat），"
                    + "请点【生成密钥对】创建，或从厂商备份恢复。");
                return;
            }
            if (!LicenseCodec.TryParseDeviceCode(DeviceBox.Text, out ushort productId,
                    out string fp8, out string ch8))
            {
                SetStatus(WarnBrush, "设备码格式不正确或校验位不符"
                    + "（让客户在激活窗整串复制，勿手抄）。");
                return;
            }

            byte type = PermRadio.IsChecked == true
                ? LicenseCodec.TypePermanent : LicenseCodec.TypeTrial;
            byte days = 0;
            if (type == LicenseCodec.TypeTrial)
            {
                if (!int.TryParse((DaysBox.Text ?? "").Trim(), out int d) || d < 1 || d > 255)
                {
                    SetStatus(WarnBrush, "试用天数必须是 1~255 的整数（当前填的是「"
                        + DaysBox.Text + "」）。");
                    return;
                }
                days = (byte)d;
            }

            byte[] payload = LicenseCodec.BuildPayload(
                productId,
                LicenseCodec.HexToBytes(fp8),
                LicenseCodec.HexToBytes(ch8),
                type, days, LicenseCodec.DateToDays(DateTime.Now));
            byte[] sig = SignCore.Sign(payload);
            CodeBox.Text = LicenseCodec.EncodeActivationCode(payload, sig);
            CodeBox.ScrollToHome();

            string productName = LicenseCodec.GetProductName(productId);
            SetStatus(OkBrush, type == LicenseCodec.TypePermanent
                ? $"已签发 {productName} 永久授权码（仅对本设备码的挑战值有效）。"
                : $"已签发 {productName} 试用 {days} 天授权码（自今天起算，仅对本设备码的挑战值有效）。");
        }
        catch (Exception ex)
        {
            SetStatus(WarnBrush, "签发失败：" + ex.Message);
        }
    }

    /// <summary>生成密钥对：私钥落盘 + 公钥复制剪贴板（替换两产品 PubKeyB64 后重编）。</summary>
    private void OnGenKey(object sender, RoutedEventArgs e)
    {
        try
        {
            string pub = SignCore.GenerateKeyPair();
            UpdateKeyState();
            try { Clipboard.SetText(pub); } catch { /* 剪贴板被占不致命，弹窗里也有全文 */ }
            MessageBox.Show(
                "密钥对已生成。\n\n私钥已保存：" + SignCore.KeyFilePath
                + "\n（.gitignore 已排除，禁止入库/外发，请立即多处离线备份）\n\n"
                + "公钥串已复制到剪贴板（如下），请替换两个产品 "
                + "LicenseCodec.PubKeyB64 常量并重新编译：\n\n" + pub,
                "生成密钥对", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus(OkBrush, "密钥对已生成，公钥已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            SetStatus(WarnBrush, "生成密钥对失败：" + ex.Message);
        }
    }

    // ================= 状态 =================

    private void UpdateKeyState()
    {
        bool has = SignCore.HasPrivateKey();
        GenKeyButton.IsEnabled = !has;
        if (has)
            SetStatus(OkBrush, "签名私钥就绪：" + SignCore.KeyFilePath);
        else
            SetStatus(WarnBrush, "未找到签名私钥：点【生成密钥对】创建，"
                + "并把输出的公钥串填回两个产品 LicenseCodec.PubKeyB64 后重编。");
    }

    private void SetStatus(Brush brush, string text)
    {
        StatusText.Foreground = brush;
        StatusText.Text = text;
    }
}
