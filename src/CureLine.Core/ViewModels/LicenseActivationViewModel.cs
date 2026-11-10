// =========================================================================
// LicenseActivationViewModel：软件激活窗 VM（两个程序共用，View 各自一份）
//
// 干什么：打开时向 LicenseHost 要设备码（真源在 kaleidoscope 子模块
//   LicenseGate，宿主接线层 LicenseHost 是序列里的应用侧唯一出口）→ 展示
//   设备码；用户粘贴厂商返回的激活码 → 调 LicenseHost.TryActivate →
//   成功/失败文案；成功后通知 View 关窗。
// 为什么共用：程序①（固化程控）与程序②（CIM 通信网关）授权逻辑完全一致，
//   只有窗标题/品牌文案不同——控件差异放各自 XAML，编排逻辑收在 Core。
// 怎么改：门禁规则改动一律改 kaleidoscope 子模块 LicenseGate（本类只是薄
//   编排，宿主口径在 LicenseHost）；新增按钮走 RelayCommand，不直接碰
//   Window/MessageBox（RequestClose 事件交给 View）。
// =========================================================================

using System.Text.RegularExpressions;
using CureLine.Core.Security;

namespace CureLine.ViewModels;

/// <summary>
/// 激活窗状态胶囊语义档（纯 UI 档位，不参与业务判断）：由 StatusText 文案关键词
/// 解析而来（ResolveStatusKind，口径对齐 LicenseHost.StateText 六档文案）。
/// </summary>
public enum ActivationStatusKind
{
    /// <summary>中性蓝档：未激活 / 演示模式（免激活）。</summary>
    Info,
    /// <summary>绿档：永久使用。</summary>
    Ok,
    /// <summary>琥珀档：试用中（剩余 N 天）。</summary>
    Warn,
    /// <summary>红档：软件已过期 / 设备指纹不符。</summary>
    Err,
}

public sealed class LicenseActivationViewModel : ViewModelBase
{
    private string _deviceCode = "";
    private string _licenseCode = "";
    private string _statusText = "";
    private string _detailText = "";
    private string _tip = "";
    private string _statusShort = "";
    private ActivationStatusKind _statusKind = ActivationStatusKind.Info;

    /// <summary>构造：appName=程序展示名（"固化程控" / "CIM通信网关"）。</summary>
    public LicenseActivationViewModel(string appName)
    {
        AppName = appName;

        // 每次打开刷新设备码（era 挑战值口径：30 天档内恒定——重开窗不必重签；
        // 跨档自动换新，旧签发码随之失效，防重放语义保留在挑战值比对里）。
        if (LicenseHost.TryBuildDeviceCode(out string code, out string error))
        {
            DeviceCode = code;
        }
        else
        {
            DeviceCode = "";
            Tip = "无法采集本机设备指纹：" + error;
        }

        StatusText = LicenseHost.StateText();
        DetailText = LicenseHost.DetailText();

        ActivateCommand = new RelayCommand(Activate, CanActivate);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    /// <summary>程序展示名（窗口副标题/标题用）。</summary>
    public string AppName { get; }

    /// <summary>设备码（只读展示，厂商签发凭据）。</summary>
    public string DeviceCode
    {
        get => _deviceCode;
        private set => Set(ref _deviceCode, value);
    }

    /// <summary>用户粘贴的激活码（变化时刷新激活按钮可用性）。</summary>
    public string LicenseCode
    {
        get => _licenseCode;
        set
        {
            if (Set(ref _licenseCode, value))
                ActivateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>当前授权状态行（"激活状态：永久使用" 等）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
            {
                StatusKind = ResolveStatusKind(value);
                StatusShort = ResolveStatusShort(value);
            }
        }
    }

    /// <summary>
    /// 状态胶囊短词（"永久使用"/"剩余 N 天"/"未激活"…）：胶囊空间小，只放档位词；
    /// 数值细节（到期日等）留在 StatusText/DetailText 与 ToolTip。随 StatusText 联动。
    /// </summary>
    public string StatusShort
    {
        get => _statusShort;
        private set => Set(ref _statusShort, value);
    }

    /// <summary>
    /// StatusText 文案 → 胶囊短词（纯函数，测试直调）：取档位词，试用档提取
    /// "剩余 N 天"数字（正则取不到回"试用中"兜底）。
    /// </summary>
    public static string ResolveStatusShort(string statusText)
    {
        string t = statusText ?? "";
        if (t.Contains("永久")) return "永久使用";
        if (t.Contains("过期")) return "已过期";
        if (t.Contains("指纹不符")) return "指纹不符";
        if (t.Contains("演示")) return "演示模式";
        if (t.Contains("试用"))
        {
            Match m = Regex.Match(t, @"剩余 \d+ 天");
            return m.Success ? m.Value : "试用中";
        }
        return "未激活";
    }

    /// <summary>
    /// 状态胶囊语义档（View 据此切胶囊底/点/字三色，四档 DataTrigger）。
    /// 随 StatusText 联动刷新，外部只读。
    /// </summary>
    public ActivationStatusKind StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    /// <summary>
    /// StatusText 文案 → 胶囊档位（纯函数，测试直调）：关键词口径与
    /// LicenseHost.StateText 六档文案对齐——"永久"绿 / "过期""指纹不符"红 /
    /// "试用"琥珀 / 其余（未激活、演示模式）中性蓝。
    /// </summary>
    public static ActivationStatusKind ResolveStatusKind(string statusText)
    {
        string t = statusText ?? "";
        if (t.Contains("永久")) return ActivationStatusKind.Ok;
        if (t.Contains("过期") || t.Contains("指纹不符")) return ActivationStatusKind.Err;
        if (t.Contains("试用")) return ActivationStatusKind.Warn;
        return ActivationStatusKind.Info;
    }

    /// <summary>状态补充说明（无记录时引导操作；失败时给原因）。</summary>
    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    /// <summary>操作提示（错误红字/成功绿字，View 按前缀选色或统一红字均可）。</summary>
    public string Tip
    {
        get => _tip;
        private set => Set(ref _tip, value);
    }

    public RelayCommand ActivateCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>请求 View 关窗：true=激活成功，false=用户取消/关闭。</summary>
    public event Action<bool>? RequestClose;

    private bool CanActivate() => !string.IsNullOrWhiteSpace(LicenseCode);

    private void Activate()
    {
        if (string.IsNullOrWhiteSpace(LicenseCode))
        {
            Tip = "请先粘贴激活码";
            return;
        }
        string? error = LicenseHost.TryActivate(LicenseCode.Trim());
        StatusText = LicenseHost.StateText();
        DetailText = LicenseHost.DetailText();
        if (error == null)
        {
            Tip = "激活成功";
            LicenseCode = "";
            RequestClose?.Invoke(true);
        }
        else
        {
            Tip = error;
        }
    }
}
