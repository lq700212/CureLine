// =========================================================================
// MesDebugViewModel：调试窗的状态 + 命令（MVVM）。
//
// 绑什么：29 个参数字段行（16 记忆 + 13 个 0033 专有）/ 接口下拉 /
//   报文预览 / 返回信息 / 上传中（按钮灰态）+ 静态文本 + 保存上传语言命令。
// 字段值一改就重算预览（ParamFieldViewModel 回调父 VM，构造时传入）。
// 后台上传：Task 跑网络 IO（铁律：UI 线程禁网络 IO），回来经 UI 上下文
//   Post 更新；非 1/异常经 IDialogService 弹报警窗（VM 不 new Window）。
// 记忆语义原样：保存只落 16 记忆字段，0033 专有字段不进文件。
// =========================================================================

using PlcMesBridge.ViewModels;
using System.Collections.ObjectModel;
using Newtonsoft.Json;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;

namespace CimGateway.ViewModels;

/// <summary>单个参数字段行（字段名 + 中文说明 + 值 + 按接口显隐）。</summary>
public class ParamFieldViewModel : ViewModelBase
{
    private readonly Action _onValueChanged;
    private string _value = "";
    private bool _visible = true;

    public ParamFieldViewModel(string field, string desc, Action onValueChanged,
        bool isMemory = true)
    {
        Field = field;
        Desc = desc;
        _onValueChanged = onValueChanged;
        IsMemory = isMemory;
    }

    public string Field { get; }
    public string Desc { get; }

    /// <summary>是否 16 记忆字段（保存/上传前同步只动这些，0033 专有不落盘）。</summary>
    public bool IsMemory { get; }

    /// <summary>行标题（字段名 + 翻译后说明，语言切换重读）。</summary>
    public string Caption => $"{Field} ({LanguageService.Tr(Desc)})";

    /// <summary>语言切换后重读标题（Caption 含翻译，父 VM 调这个）。</summary>
    public void RefreshTexts() => RefreshAll();

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value ?? ""))
                _onValueChanged();
        }
    }

    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }
}

public class MesDebugViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;

    /// <summary>0033 专有字段（字段名, 中文说明）。记忆参数里没有，仅本窗使用。</summary>
    private static readonly (string Field, string Desc)[] Extra0033 =
    {
        ("BOARD_SN", "板号SN"), ("TOP_BTM", "顶底层"), ("LINE_NO", "线别"),
        ("S_TEST_TIME", "测试开始时间"), ("E_TEST_TIME", "测试结束时间"),
        ("CHK_STATUS", "检测状态"), ("DOT_LOC", "点位"),
        ("BIN_LOC", "料仓位"), ("IMG_LOC", "图片位置"),
        ("ERR_NO", "错误码"), ("SCR_ACTUAL", "实测值"),
        ("SCR_STD", "标准值"), ("SCR_MAX", "上限"),
    };

    private static readonly Dictionary<string, string[]> Required0033 = new()
    {
        ["API0033"] = new[]
        {
            "RC_NO", "BOARD_SN", "TOP_BTM", "LINE_NO", "PT_NO",
            "S_TEST_TIME", "E_TEST_TIME", "CHK_STATUS", "DOT_LOC", "BIN_LOC",
            "IMG_LOC", "ERR_NO", "SCR_ACTUAL", "SCR_STD", "SCR_MAX", "PLANT",
        },
    };

    private string _selectedApi = "API0027";
    private string _preview = "";
    private string _response = "";
    private bool _uploading;

    public MesDebugViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Instance;
        MesParamStore.LoadParams();
        foreach (var (field, desc) in MesParamStore.AllFields)
            Fields.Add(new ParamFieldViewModel(field, desc, RefreshPreview, true));
        foreach (var (field, desc) in Extra0033)
            Fields.Add(new ParamFieldViewModel(field, desc, RefreshPreview, false));
        foreach (ParamFieldViewModel f in Fields)
            f.Value = MesParamStore.Get(f.Field);
        ApplyVisibility();
        SaveCommand = new RelayCommand(Save);
        UploadCommand = new RelayCommand(Upload, () => !Uploading);
        SwitchLanguageCommand = new RelayCommand(SwitchLanguage);
    }

    public ObservableCollection<ParamFieldViewModel> Fields { get; } = new();

    public string[] Apis { get; } =
        { "API0027", "API0028", "API0030", "API0031", "API0032", "API0033" };

    public string SelectedApi
    {
        get => _selectedApi;
        set
        {
            if (Set(ref _selectedApi, value ?? "API0027"))
                ApplyVisibility();
        }
    }

    public string Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public string Response
    {
        get => _response;
        private set => Set(ref _response, value);
    }

    public bool Uploading
    {
        get => _uploading;
        private set
        {
            if (Set(ref _uploading, value))
            {
                Raise(nameof(UploadEnabled));
                UploadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool UploadEnabled => !Uploading;

    public string TitleText => Tr("MES 接口调试界面");
    public string ApiLabel => Tr("接口类型:");
    public string SaveText => Tr("保存参数到本地");
    public string UploadText => Tr("手动上传到MES");
    public string PreviewLabel => Tr("发送报文预览:");
    public string RespLabel => Tr("MES 返回信息:");
    public string LangText => LanguageService.CurrentLanguage == "CH" ? "English" : "中文";

    public RelayCommand SaveCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand SwitchLanguageCommand { get; }

    public void RefreshTexts()
    {
        RefreshAll();
        foreach (ParamFieldViewModel f in Fields)
            f.RefreshTexts();
    }

    private static string Tr(string zh) => LanguageService.Tr(zh);

    /// <summary>按接口显隐字段（老项目 cmbApiType_SelectedIndexChanged 语义）。</summary>
    private void ApplyVisibility()
    {
        string[] required = _selectedApi == "API0033"
            ? Required0033[_selectedApi]
            : MesParamStore.RequiredFields.TryGetValue(_selectedApi, out var r)
                ? r : Array.Empty<string>();
        foreach (ParamFieldViewModel f in Fields)
            f.Visible = required.Contains(f.Field);
        RefreshPreview();
    }

    private string Get(string field)
    {
        foreach (ParamFieldViewModel f in Fields)
        {
            if (f.Field == field)
                return f.Value;
        }
        return "";
    }

    private void RefreshPreview() => Preview = BuildJson();

    /// <summary>组报文（老项目 BuildJsonString 语义：0027/0028/0033 外层数组，其余对象）。</summary>
    private string BuildJson() => _selectedApi switch
    {
        "API0027" => JsonConvert.SerializeObject(new List<MesModels.Api0027Request>
            { New0027() }, Formatting.None),
        "API0028" => JsonConvert.SerializeObject(new List<MesModels.Api0028Request>
            { New0028() }, Formatting.None),
        "API0030" => JsonConvert.SerializeObject(New0030(), Formatting.None),
        "API0031" => JsonConvert.SerializeObject(New0031(), Formatting.None),
        "API0032" => JsonConvert.SerializeObject(New0032(), Formatting.None),
        "API0033" => JsonConvert.SerializeObject(new List<MesModels.Api0033Request>
            { New0033() }, Formatting.None),
        _ => "",
    };

    private MesModels.Api0027Request New0027() => new()
    {
        RC_NO = Get("RC_NO"), PT_NO = Get("PT_NO"), MAC_NO = Get("MAC_NO"),
        MAC_LOC_NO = Get("MAC_LOC_NO"), PRA_CODE = Get("PRA_CODE"),
        PRA_Value = Get("PRA_Value"), Collect_Datetime = Get("Collect_Datetime"),
        PLANT = Get("PLANT"),
    };

    private MesModels.Api0028Request New0028() => new()
    {
        RC_NO = Get("RC_NO"), PT_NO = Get("PT_NO"), MAC_NO = Get("MAC_NO"),
        MAC_LOC_NO = Get("MAC_LOC_NO"), PRA_CODE = Get("PRA_CODE"),
        PRA_Value = Get("PRA_Value"), PLANT = Get("PLANT"),
    };

    private MesModels.Api0030Request New0030() => new()
    {
        RC_NO = Get("RC_NO"), ACTION = Get("ACTION"), BC_NO = Get("BC_NO"),
        PT_NO = Get("PT_NO"), SP_MEMO = Get("SP_MEMO"), MAC_NO = Get("MAC_NO"),
        MAC_LOC_NO = Get("MAC_LOC_NO"), TL_EL_NO = Get("TL_EL_NO"),
        IN_DATETIME = Get("IN_DATETIME"), CHECK_RESULT = Get("CHECK_RESULT"),
        PLANT = Get("PLANT"),
    };

    private MesModels.Api0031Request New0031() => new()
    {
        RC_NO = Get("RC_NO"), BC_NO = Get("BC_NO"), PT_NO = Get("PT_NO"),
        EMP_NO = Get("EMP_NO"), MAC_NO = Get("MAC_NO"),
        MAC_LOC_NO = Get("MAC_LOC_NO"), BO_NUMBER = Get("BO_NUMBER"),
        PLANT = Get("PLANT"),
    };

    private MesModels.Api0032Request New0032() => new()
    {
        RC_NO = Get("RC_NO"), BC_NO = Get("BC_NO"), PT_NO = Get("PT_NO"),
        EMP_NO = Get("EMP_NO"), MAC_NO = Get("MAC_NO"),
        MAC_LOC_NO = Get("MAC_LOC_NO"), IN_DATETIME = Get("IN_DATETIME"),
        PLANT = Get("PLANT"),
    };

    private MesModels.Api0033Request New0033() => new()
    {
        RC_NO = Get("RC_NO"), BOARD_SN = Get("BOARD_SN"), TOP_BTM = Get("TOP_BTM"),
        LINE_NO = Get("LINE_NO"), PT_NO = Get("PT_NO"),
        S_TEST_TIME = Get("S_TEST_TIME"), E_TEST_TIME = Get("E_TEST_TIME"),
        CHK_STATUS = Get("CHK_STATUS"), DOT_LOC = Get("DOT_LOC"),
        BIN_LOC = Get("BIN_LOC"), IMG_LOC = Get("IMG_LOC"),
        ERR_NO = Get("ERR_NO"), SCR_ACTUAL = Get("SCR_ACTUAL"),
        SCR_STD = Get("SCR_STD"), SCR_MAX = Get("SCR_MAX"),
    };

    /// <summary>记忆字段同步回全局（保存/上传前调；0033 专有不进文件）。</summary>
    private void SyncMemoryToStore()
    {
        foreach (ParamFieldViewModel f in Fields)
        {
            if (f.IsMemory)
                MesParamStore.Set(f.Field, f.Value);
        }
        MesParamStore.SaveParams();
    }

    private void Save()
    {
        SyncMemoryToStore();
        _dialogs.ShowMessage(Tr("参数已成功保存到本地并生效全局!"), TitleText);
    }

    /// <summary>手动上传（老项目 btnUpload_Click：先保存→后台 POST→回 UI 显示→非 1 弹报警）。</summary>
    private void Upload()
    {
        SyncMemoryToStore();
        string url = MesConfig.GetUrl(_selectedApi);
        if (string.IsNullOrEmpty(url))
        {
            _dialogs.ShowMessage(Tr("发生错误:") + " URL 未在 MESConfig 中配置",
                TitleText, DialogImage.Error);
            return;
        }
        string json = BuildJson();
        Uploading = true;
        Response = $"{Tr("请求中...")} URL: {url}\r\nJSON: {json}";
        Task.Run(async () =>
        {
            var (ok, resp) = await MesHttpClient.PostJsonAsync(url, json).ConfigureAwait(false);
            UiInvoke(() =>
            {
                Response = resp;
                Uploading = false;
                try
                {
                    var obj = JsonConvert.DeserializeObject<MesModels.BaseResponse>(resp);
                    if (!ok || (obj != null && obj.STATUS != "1"))
                        _dialogs.ShowAlarm(resp, "MES Debug Return");
                }
                catch
                {
                    _dialogs.ShowAlarm(Tr("发生错误:") + "\r\n" + resp, "Parse Error");
                }
            });
        });
    }

    private void SwitchLanguage()
    {
        LanguageService.CurrentLanguage =
            LanguageService.CurrentLanguage == "CH" ? "EN" : "CH";
        IniFile.Write(AppPaths.ConfigIni, "setting", "Language",
            LanguageService.CurrentLanguage);
        RefreshTexts();
    }
}
