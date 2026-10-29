// =========================================================================
// PlcConfigViewModel：PLC 配置窗两页的状态 + 命令（MVVM）。
//
// 绑什么：多机 8 行 / 接口 6 组×7 行 + 测试结果 + 静态文本 +
//   测试/恢复/保存取消命令。单机页与协议下拉已随三菱链路删除（P2.1：
//   真机底座统一 Modbus TCP，无二进制/ASCII 分叉，协议键不再存在）。
// 语义与原来逐字一致：测试拿表单值直测（不用先保存）；保存先校验，
//   错当场拦（经 IDialogService 弹）；落盘后 AppConfig.Load 刷新内存，
//   置 PlcChanged 由主窗重启。
// "恢复缺省"语义原样保留：多机=ini 存盘值（撤销本次未保存修改）。
// 异步测试跑后台（铁律），回来经 UiInvoke 更新；按钮灰态经 Testing 属性。
// =========================================================================

using PlcMesBridge.ViewModels;
using System.Collections.ObjectModel;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;

namespace CimGateway.ViewModels;

/// <summary>接口页通用文本行（取值键 + 显示标签 + 值 + 框宽）。</summary>
public class ConfigTextRow : ViewModelBase
{
    private string _value;

    /// <param name="key">取值键（接口页=trg:API0027 类复合键）。</param>
    /// <param name="displayLabelKey">显示标签键（走翻译，如"触发地址"）。</param>
    public ConfigTextRow(string key, string displayLabelKey, string value, int boxWidth = 170)
    {
        LabelKey = key;
        DisplayLabelKey = displayLabelKey;
        _value = value;
        BoxWidth = boxWidth;
    }

    public string LabelKey { get; }
    public string DisplayLabelKey { get; }
    public int BoxWidth { get; }

    public string Caption => LanguageService.Tr(DisplayLabelKey);

    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? "");
    }

    public void RefreshTexts() => RefreshAll();
}

/// <summary>多机单行（启用/IP/端口/测试按钮/结果）。</summary>
public class StationRowViewModel : ViewModelBase
{
    private bool _enabled;
    private string _ip = "";
    private string _port = "";
    private string _resultText = "";
    private string _resultForeground = "Black";
    private string? _resultTip;
    private bool _testing;

    public StationRowViewModel(int id)
    {
        Id = id;
    }

    public int Id { get; }

    public string TitleText => $"{Id + 1}号{LanguageService.Tr("启用")}";

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public string Ip
    {
        get => _ip;
        set => Set(ref _ip, value ?? "");
    }

    public string PortText
    {
        get => _port;
        set => Set(ref _port, value ?? "");
    }

    public string ResultText
    {
        get => _resultText;
        set => Set(ref _resultText, value);
    }

    public string ResultForeground
    {
        get => _resultForeground;
        set => Set(ref _resultForeground, value);
    }

    public string? ResultTip
    {
        get => _resultTip;
        set => Set(ref _resultTip, value);
    }

    public bool Testing
    {
        get => _testing;
        set => Set(ref _testing, value);
    }

    public void RefreshTexts() => RefreshAll();
}

/// <summary>接口一组（API0027 等 + 7 行：触发/URL/数据/MSG/DATA/状态/完成）。</summary>
public class ApiSectionViewModel : ViewModelBase
{
    public ApiSectionViewModel(string apiCode)
    {
        ApiCode = apiCode;
    }

    public string ApiCode { get; }

    public ObservableCollection<ConfigTextRow> Rows { get; } = new();

    public void RefreshTexts()
    {
        RefreshAll();
        foreach (ConfigTextRow r in Rows)
            r.RefreshTexts();
    }
}

public class PlcConfigViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly bool _useSim;

    public PlcConfigViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Instance;
        _useSim = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";
        // ---- 多机页（IP 键跳号沿用 + 新键 PLCPorts，见 PlcConfig.cs）----
        StationPlcConfig[] stations = PlcConfigStore.LoadStations(AppPaths.ConfigIni);
        for (int i = 0; i < 8; i++)
        {
            Stations.Add(new StationRowViewModel(i)
            {
                Enabled = stations[i].Enabled,
                Ip = stations[i].Ip,
                PortText = stations[i].Port.ToString(),
            });
        }
        // ---- 接口页 ----
        DataLengthText = MesConfig.DataLength.ToString();
        DataSegmentOffsetsText = string.Join(",", MesConfig.DataSegmentOffsets);
        ScanIntervalText = AppConfig.ScanIntervalMs.ToString();
        foreach (string api in MesConfig.ApiCodes)
        {
            var sec = new ApiSectionViewModel(api);
            sec.Rows.Add(new ConfigTextRow($"trg:{api}", "触发地址",
                MesConfig.GetTrigger(api)));
            int idx = Array.IndexOf(MesConfig.ApiCodes, api);
            var set = MesConfig.ApiAddresses[idx];
            sec.Rows.Add(new ConfigTextRow($"url:{api}", "URL", MesConfig.GetUrl(api), 340));
            sec.Rows.Add(new ConfigTextRow($"dat:{api}", "数据地址", set.PlcData));
            sec.Rows.Add(new ConfigTextRow($"msg:{api}", "MSG地址", set.MesMsg));
            sec.Rows.Add(new ConfigTextRow($"data:{api}", "DATA地址", set.MesData));
            sec.Rows.Add(new ConfigTextRow($"st:{api}", "状态地址", set.MesStatus));
            sec.Rows.Add(new ConfigTextRow($"done:{api}", "完成地址", set.MesComplete));
            ApiSections.Add(sec);
        }

        TestStationCommand = new RelayCommand<int?>(async id =>
        {
            if (id.HasValue)
                await TestStationAsync(id.Value);
        });
        TestAllStationsCommand = new RelayCommand(
            async () => await TestAllStationsAsync(), () => !AnyStationTesting);
        RestoreStationsCommand = new RelayCommand(RestoreStationsFromIni);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
    }

    /// <summary>本次保存是否改了配置（主窗据此决定是否自动重启）。</summary>
    public bool PlcChanged { get; private set; }

    public event Action<bool?>? RequestClose;

    public ObservableCollection<StationRowViewModel> Stations { get; } = new();
    public ObservableCollection<ApiSectionViewModel> ApiSections { get; } = new();

    private string _dataLengthText = "799";
    public string DataLengthText
    {
        get => _dataLengthText;
        set => Set(ref _dataLengthText, value ?? "");
    }

    /// <summary>DATA 三段拆写偏移（"30,60"逗号分隔，V0.0.22 配置化，接口页可编辑）。</summary>
    private string _dataSegmentOffsetsText = "30,60";
    public string DataSegmentOffsetsText
    {
        get => _dataSegmentOffsetsText;
        set => Set(ref _dataSegmentOffsetsText, value ?? "");
    }

    /// <summary>网关触发扫描周期 ms（50~5000，V0.0.22 配置化，多机页可编辑）。</summary>
    private string _scanIntervalText = "200";
    public string ScanIntervalText
    {
        get => _scanIntervalText;
        set => Set(ref _scanIntervalText, value ?? "");
    }

    public string TitleText => Tr("网关配置");
    public string TabMultiText => Tr("机台连接");
    public string TabApiText => Tr("接口地址");
    public string SaveText => Tr("保存并重启");
    public string CancelText => Tr("取消");
    public string TestText => Tr("测试连接");
    public string TestAllText => Tr("全部测试");
    public string RestoreText => Tr("恢复缺省");
    public string HintText => Tr("测试用表单值直测保存后重启生效")
        + (_useSim ? "（" + Tr("模拟模式下配置仅对真机有效") + "）" : "");
    public string DataLengthLabel => Tr("数据长度");
    public string DataSegmentOffsetsLabel => Tr("分段偏移");
    public string ScanIntervalLabel => Tr("扫描周期ms");

    public RelayCommand<int?> TestStationCommand { get; }
    public RelayCommand TestAllStationsCommand { get; }
    public RelayCommand RestoreStationsCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool AnyStationTesting
    {
        get
        {
            foreach (StationRowViewModel s in Stations)
            {
                if (s.Testing)
                    return true;
            }
            return false;
        }
    }

    public void RefreshTexts()
    {
        RefreshAll();
        foreach (StationRowViewModel s in Stations)
            s.RefreshTexts();
        foreach (ApiSectionViewModel s in ApiSections)
            s.RefreshTexts();
    }

    private static string Tr(string zh) => LanguageService.Tr(zh);

    // ================= 多机 =================

    private void RestoreStationsFromIni()
    {
        // 语义原样：恢复到 ini 存盘值（撤销本次未保存修改），不是出厂缺省。
        StationPlcConfig[] cs = PlcConfigStore.LoadStations(AppPaths.ConfigIni);
        for (int i = 0; i < 8; i++)
        {
            Stations[i].Enabled = cs[i].Enabled;
            Stations[i].Ip = cs[i].Ip;
            Stations[i].PortText = cs[i].Port.ToString();
        }
    }

    private StationPlcConfig[] GatherStations()
    {
        var cs = new StationPlcConfig[8];
        for (int i = 0; i < 8; i++)
        {
            cs[i] = new StationPlcConfig
            {
                Enabled = Stations[i].Enabled,
                Ip = Stations[i].Ip,
                Port = ParseInt(Stations[i].PortText),
            };
        }
        return cs;
    }

    private string Trigger0027()
    {
        // 读 0027 触发字验证可读（触发地址是全局配置，读当前接口页表单值）。
        foreach (ApiSectionViewModel sec in ApiSections)
        {
            if (sec.ApiCode != "API0027")
                continue;
            foreach (ConfigTextRow r in sec.Rows)
            {
                if (r.LabelKey == "trg:API0027")
                    return r.Value;
            }
        }
        return MesConfig.TriggerApi0027;
    }

    private async Task TestOneStationAsync(int id, StationPlcConfig[] cs, string trg)
    {
        StationRowViewModel row = Stations[id];
        row.Testing = true;
        TestAllStationsCommand.RaiseCanExecuteChanged();
        try
        {
            var (ok, msg, _) = await PlcConnectionTester.TestAsync(
                () => new ModbusStationClient(), cs[id].Ip.Trim(), cs[id].Port,
                PlcConfigValidator.NormAddr(trg)).ConfigureAwait(false);
            UiInvoke(() =>
            {
                row.ResultText = ok ? Tr("连接成功") : Tr("连接失败");
                row.ResultForeground = ok ? "DarkGreen" : "Red";
                row.ResultTip = msg;
            });
        }
        finally
        {
            UiInvoke(() =>
            {
                row.Testing = false;
                TestAllStationsCommand.RaiseCanExecuteChanged();
            });
        }
    }

    private async Task TestStationAsync(int id)
    {
        StationPlcConfig[] cs = GatherStations();
        await TestOneStationAsync(id, cs, Trigger0027()).ConfigureAwait(false);
    }

    private async Task TestAllStationsAsync()
    {
        StationPlcConfig[] cs = GatherStations();
        string trg = Trigger0027();
        foreach (StationRowViewModel row in Stations)
            row.Testing = true;
        TestAllStationsCommand.RaiseCanExecuteChanged();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                if (!cs[i].Enabled)
                {
                    Stations[i].ResultText = "—";
                    continue;
                }
                Stations[i].ResultText = Tr("测试中...");
                await TestOneStationAsync(i, cs, trg).ConfigureAwait(false);
            }
        }
        finally
        {
            UiInvoke(() =>
            {
                foreach (StationRowViewModel row in Stations)
                    row.Testing = false;
                TestAllStationsCommand.RaiseCanExecuteChanged();
            });
        }
    }

    // ================= 保存 =================

    private void Save()
    {
        StationPlcConfig[] stations = GatherStations();
        var errs = PlcConfigValidator.ValidateStations(stations);
        errs.AddRange(ValidateApi());

        if (errs.Count > 0)
        {
            string lines = string.Join("\r\n",
                errs.Select(x => $"{Tr(x.Field)}：{Tr(x.Reason)}"));
            _dialogs.ShowMessage($"{Tr("以下配置有误")}：\r\n{lines}",
                Tr("网关配置"), DialogImage.Warning);
            return;
        }

        string ini = AppPaths.ConfigIni;
        PlcConfigStore.SaveStations(ini, stations);
        SaveApiSection(ini);
        AppConfig.Load(); // 刷新内存（重启后会再读一次，此处保证本进程内一致）
        PlcChanged = true;
        RequestClose?.Invoke(true);
    }

    private List<(string Field, string Reason)> ValidateApi()
    {
        var errs = new List<(string, string)>();
        if (!int.TryParse(DataLengthText.Trim(), out int dl) || dl is < 1 or > 10000)
            errs.Add(("数据长度", "范围1-10000"));
        // 分段偏移（V0.0.22）：2~8 段、每段 1~65535，与 AppConfig.Load 同口径
        // （配置窗拦一道，ini 手改还有 Load 兜底自愈，双保险）。
        string[] segParts = DataSegmentOffsetsText.Trim()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segParts.Length is < 2 or > 8)
            errs.Add(("分段偏移", "须2-8个逗号分隔数字"));
        else
        {
            foreach (string p in segParts)
            {
                if (!int.TryParse(p, out int v) || v is < 1 or > 65535)
                {
                    errs.Add(("分段偏移", "每段范围1-65535"));
                    break;
                }
            }
        }
        // 扫描周期（V0.0.22）：50~5000ms，与 AppConfig.Load 钳制口径一致。
        if (!int.TryParse(ScanIntervalText.Trim(), out int si) || si is < 50 or > 5000)
            errs.Add(("扫描周期ms", "范围50-5000"));
        foreach (ApiSectionViewModel sec in ApiSections)
        {
            string api = sec.ApiCode;
            foreach (ConfigTextRow r in sec.Rows)
            {
                if (r.LabelKey.StartsWith("url:", StringComparison.Ordinal))
                {
                    string url = r.Value.Trim();
                    if (!string.IsNullOrEmpty(url) &&
                        (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
                         (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
                        errs.Add(($"{api} URL", "IP地址无效"));
                }
                else if (!PlcConfigValidator.IsPlcAddress(r.Value))
                {
                    errs.Add(($"{api} {r.LabelKey.Split(':')[0]}", "PLC地址格式须为D/R+数字"));
                }
            }
        }
        return errs;
    }

    private string ApiValue(string key)
    {
        foreach (ApiSectionViewModel sec in ApiSections)
        {
            foreach (ConfigTextRow r in sec.Rows)
            {
                if (r.LabelKey == key)
                    return r.Value;
            }
        }
        return "";
    }

    private void SaveApiSection(string ini)
    {
        string[] names = { "API0027", "API0028", "API0030", "API0031", "API0032", "API0033" };
        string[] urlKeys = { "Url_API0027", "Url_API0028", "Url_API0030", "Url_API0031", "Url_API0032", "Url_API0033" };
        string[] trgKeys = { "TriggerAddrAPI0027", "TriggerAddrAPI0028", "TriggerAddrAPI0030", "TriggerAddrAPI0031", "TriggerAddrAPI0032", "TriggerAddrAPI0033" };
        for (int i = 0; i < 6; i++)
        {
            IniFile.Write(ini, "MES", urlKeys[i], ApiValue($"url:{names[i]}").Trim());
            IniFile.Write(ini, "MES", trgKeys[i],
                PlcConfigValidator.NormAddr(ApiValue($"trg:{names[i]}")));
            IniFile.Write(ini, "MES", $"{names[i]}PLCDataAddr",
                PlcConfigValidator.NormAddr(ApiValue($"dat:{names[i]}")));
            IniFile.Write(ini, "MES", $"{names[i]}MESMSGAddr",
                PlcConfigValidator.NormAddr(ApiValue($"msg:{names[i]}")));
            IniFile.Write(ini, "MES", $"{names[i]}MESDataAddr",
                PlcConfigValidator.NormAddr(ApiValue($"data:{names[i]}")));
            IniFile.Write(ini, "MES", $"{names[i]}MESSTATUSAddr",
                PlcConfigValidator.NormAddr(ApiValue($"st:{names[i]}")));
            IniFile.Write(ini, "MES", $"{names[i]}MESCompleteAddr",
                PlcConfigValidator.NormAddr(ApiValue($"done:{names[i]}")));
        }
        IniFile.Write(ini, "MES", "DataLength", DataLengthText.Trim());
        // 分段偏移/扫描周期（V0.0.22）：校验已拦非法，此处原样落盘；
        // 保存末尾 AppConfig.Load() 会把它们刷进静态值——扫描周期即时热生效，
        // 无需重启重连（偏移在下一笔触发回写即生效）。
        IniFile.Write(ini, "MES", "DataSegmentOffsets", DataSegmentOffsetsText.Trim());
        IniFile.Write(ini, "setting", "ScanIntervalMs", ScanIntervalText.Trim());
    }

    private static int ParseInt(string s) =>
        int.TryParse((s ?? "").Trim(), out int v) ? v : 0;
}
