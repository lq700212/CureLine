// =========================================================================
// LicenseActivationViewModel：软件激活窗 VM（两个程序共用，View 各自一份）
//
// 干什么：打开时向 LicenseGate 换新挑战值 → 展示设备码；用户粘贴厂商返回的
//   激活码 → 调 TryActivate → 成功/失败文案；成功后通知 View 关窗。
// 为什么共用：程序①（固化程控）与程序②（CIM 通信网关）授权逻辑完全一致，
//   只有窗标题/品牌文案不同——控件差异放各自 XAML，编排逻辑收在 Core。
// 怎么改：门禁规则改动一律改 LicenseGate（本类只是薄编排）；新增按钮走
//   RelayCommand，不直接碰 Window/MessageBox（RequestClose 事件交给 View）。
// =========================================================================

using CureLine.Core.Security;

namespace CureLine.ViewModels;

public sealed class LicenseActivationViewModel : ViewModelBase
{
    private readonly LicenseGate _gate;

    private string _deviceCode = "";
    private string _licenseCode = "";
    private string _statusText = "";
    private string _detailText = "";
    private string _tip = "";

    /// <summary>构造：appName=程序展示名（"固化程控" / "CIM 通信网关"）。</summary>
    public LicenseActivationViewModel(LicenseGate gate, string appName)
    {
        _gate = gate;
        AppName = appName;

        // 每次打开换新挑战值（防重放）——设备码随之刷新，旧码失效。
        _ = _gate.NewChallenge();
        if (_gate.TryBuildDeviceCode(out string code, out string error))
        {
            DeviceCode = code;
        }
        else
        {
            DeviceCode = "";
            Tip = "无法采集本机设备指纹：" + error;
        }

        StatusText = _gate.StateText();
        DetailText = _gate.DetailText();

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
        private set => Set(ref _statusText, value);
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
        string? error = _gate.TryActivate(LicenseCode.Trim());
        StatusText = _gate.StateText();
        DetailText = _gate.DetailText();
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
