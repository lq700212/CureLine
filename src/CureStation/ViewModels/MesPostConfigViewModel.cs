// =========================================================================
// MesPostConfigViewModel：单线过账配置窗编排（程序①，P3.2 后半）
//
// 干什么：过账的全部可调参数一处改——总开关/工位/固化时间单位/进站时机/
//   中途取件口径/静态参数（MAC_NO 等）/三个接口 URL。保存落 config.ini
//   （[CuringMes] 节 + [MES] Url 三键），主窗据此热更新过账服务（不重启）。
// 为什么界面化：W68 终版未定 → 现场联调"点点鼠标"改配置，代码零改动
//   （用户口径）；与 PLC 配置窗同款"编辑面板+保存"模式与 XAML 纪律。
// 门禁：窗入口 L4（主窗 Gate）；URL 属接口配置与点表同级。
// 怎么改：新增配置项 = CuringMesOptions 加属性 + Load/Save + 本 VM 三处
//   （字段/Sync/Flush）+ XAML 加控件；键名勿改（现场已按样例部署）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

public sealed class MesPostConfigViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;

    public MesPostConfigViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        SaveCommand = new RelayCommand(Save);
        SyncFromOptions(CuringMesOptions.Load());
    }

    public RelayCommand SaveCommand { get; }

    /// <summary>保存成功后置 true（DialogService 据此返回 true，主窗热更新过账服务）。</summary>
    public bool CloseDialog { get; private set; }

    // ---------- 工位下拉选项（ComboBox 绑定） ----------
    public string[] SideOptions { get; } = { "R", "L" };
    public string[] TimeUnitOptions { get; } = { "Seconds（秒，老项目口径）", "Minutes（分钟）" };
    public string[] InAtOptions { get; } = { "Arrive（托盘到位）", "Start（开计时）" };

    // ---------- 编辑区（打开回显；保存前 Flush 回对象） ----------

    private bool _editEnabled;
    public bool EditEnabled { get => _editEnabled; set => Set(ref _editEnabled, value); }

    private string _editSide = "R";
    public string EditSide { get => _editSide; set => Set(ref _editSide, value ?? "R"); }

    // 双架（V0.0.20）：架号→工位映射 + L 工位专用机台参数（缺省回落主值）
    private string _editRackSides = "";
    public string EditRackSides { get => _editRackSides; set => Set(ref _editRackSides, value ?? ""); }

    private string _editMacNoL = "";
    public string EditMacNoL { get => _editMacNoL; set => Set(ref _editMacNoL, value ?? ""); }

    private string _editMacLocNoL = "";
    public string EditMacLocNoL { get => _editMacLocNoL; set => Set(ref _editMacLocNoL, value ?? ""); }

    private int _editTimeUnitIndex;
    public int EditTimeUnitIndex { get => _editTimeUnitIndex; set => Set(ref _editTimeUnitIndex, value); }

    private int _editInAtIndex;
    public int EditInAtIndex { get => _editInAtIndex; set => Set(ref _editInAtIndex, value); }

    private bool _editPostAbortOut;
    public bool EditPostAbortOut { get => _editPostAbortOut; set => Set(ref _editPostAbortOut, value); }

    private string _editMacNo = "";
    public string EditMacNo { get => _editMacNo; set => Set(ref _editMacNo, value ?? ""); }

    private string _editMacLocNo = "";
    public string EditMacLocNo { get => _editMacLocNo; set => Set(ref _editMacLocNo, value ?? ""); }

    private string _editPlant = "";
    public string EditPlant { get => _editPlant; set => Set(ref _editPlant, value ?? ""); }

    private string _editPraCode = "";
    public string EditPraCode { get => _editPraCode; set => Set(ref _editPraCode, value ?? ""); }

    private string _editCheckResult = "";
    public string EditCheckResult { get => _editCheckResult; set => Set(ref _editCheckResult, value ?? ""); }

    private string _editUrl27 = "";
    public string EditUrl27 { get => _editUrl27; set => Set(ref _editUrl27, value ?? ""); }

    private string _editUrl30 = "";
    public string EditUrl30 { get => _editUrl30; set => Set(ref _editUrl30, value ?? ""); }

    private string _editUrl32 = "";
    public string EditUrl32 { get => _editUrl32; set => Set(ref _editUrl32, value ?? ""); }

    private void SyncFromOptions(CuringMesOptions o)
    {
        EditEnabled = o.Enabled;
        EditSide = CuringMesOptions.NormSide(o.Side);
        EditTimeUnitIndex = o.TimeUnit == CuringTimeUnit.Minutes ? 1 : 0;
        EditInAtIndex = o.InAt == PostInAt.Start ? 1 : 0;
        EditPostAbortOut = o.PostAbortOut;
        EditRackSides = o.RackSides;
        EditMacNo = o.MacNo;
        EditMacNoL = o.MacNo_L;
        EditMacLocNo = o.MacLocNo;
        EditMacLocNoL = o.MacLocNo_L;
        EditPlant = o.Plant;
        EditPraCode = o.PraCode;
        EditCheckResult = o.CheckResult;
        EditUrl27 = MesConfig.UrlApi0027;
        EditUrl30 = MesConfig.UrlApi0030;
        EditUrl32 = MesConfig.UrlApi0032;
    }

    private void Save()
    {
        var o = new CuringMesOptions
        {
            Enabled = EditEnabled,
            Side = CuringMesOptions.NormSide(EditSide),
            TimeUnit = EditTimeUnitIndex == 1 ? CuringTimeUnit.Minutes : CuringTimeUnit.Seconds,
            InAt = EditInAtIndex == 1 ? PostInAt.Start : PostInAt.Arrive,
            PostAbortOut = EditPostAbortOut,
            RackSides = EditRackSides.Trim(),
            MacNo = EditMacNo.Trim(),
            MacNo_L = EditMacNoL.Trim(),
            MacLocNo = EditMacLocNo.Trim(),
            MacLocNo_L = EditMacLocNoL.Trim(),
            Plant = EditPlant.Trim(),
            PraCode = EditPraCode.Trim(),
            CheckResult = EditCheckResult.Trim(),
        };

        // URL 校验：非空须合法 http/https（照 PLC 配置窗同款口径）
        var urlErrors = new List<(string Name, string Url)>
        {
            ("API0027 URL", EditUrl27.Trim()),
            ("API0030 URL", EditUrl30.Trim()),
            ("API0032 URL", EditUrl32.Trim()),
        }.Where(x => x.Url != "").Where(x =>
            !Uri.TryCreate(x.Url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            .Select(x => $"{x.Name} 格式无效（须 http/https）").ToList();
        if (urlErrors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", urlErrors), "保存失败（URL 校验）");
            return;
        }

        try
        {
            o.Save();
            // URL 写 [MES] 节（与主程序同键跨程序一致）+ 本进程 MesConfig 立即生效
            string ini = AppPaths.ConfigIni;
            IniFile.Write(ini, "MES", "Url_API0027", EditUrl27.Trim());
            IniFile.Write(ini, "MES", "Url_API0030", EditUrl30.Trim());
            IniFile.Write(ini, "MES", "Url_API0032", EditUrl32.Trim());
            MesConfig.UrlApi0027 = EditUrl27.Trim();
            MesConfig.UrlApi0030 = EditUrl30.Trim();
            MesConfig.UrlApi0032 = EditUrl32.Trim();
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage($"过账配置落盘失败：{ex.Message}", "保存失败");
            return;
        }
        CloseDialog = true;
        // 成功提示由主窗统一弹（与点表/策略窗同款）
    }
}
