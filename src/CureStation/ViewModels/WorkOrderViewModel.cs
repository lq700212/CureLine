// =========================================================================
// WorkOrderViewModel：工单信息窗编排（R/L 双工位 + 基础信息 + 批次物料 + 制程）
//
// 干什么：对照参考 HMI"工单信息"页——R/L 各 { 途程单号/预设数量/已生产数量 }、
//   员工工号、物料条码、制程代码×2、物料上传使能；计数清零；保存落盘
//   config/workorder.json。门禁（构造传操作员级别）：途程/数量/清零 L2+，
//   制程代码 L3+，员工工号/条码 L1+（登录即可）——不够级别的编辑区禁用。
// 编辑模式：与策略窗同款——打开回显，保存前 Flush；数字解析失败当场弹框拦下。
//
// 工位布局开关（[CuringMes] DualSide，构造传 postOptions；null=Load 现读盘）：
//   ● 双工位（true）：R/L 双面板全显、文案带工位字样（"工单信息（R）"等）、
//     上传 R/L 两枚——老口径不变；
//   ● 单工位（缺省 false，现场就一套工位）：只显 Side 对应哪侧的面板
//     （Side=L 用 Left 槽——视图↔槽映射恒定：R↔Right、L↔Left，Sync/Flush
//     零改动，隐藏侧数据原样往返=盘上等值落回）；标题/按钮文案全去 R/L 字样，
//     上传按钮带 Side 侧（过账报文口径与界面一致）。
//
// 怎么改：新增字段 = WorkOrderBook 加属性 + 窗加控件 + Sync/Flush 两处；
//   与 MesParamStore 字段名对齐的（RC_NO 等）勿改名（过账映射靠它）。
// =========================================================================

using CureLine.Core.Curing;
using CureLine.Core.Security;
using CureLine.ViewModels;

namespace CureStation.ViewModels;

public sealed class WorkOrderViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly OperatorLevel? _level;
    private readonly List<string> _flushErrors = new();
    private readonly Action<string>? _uploadMaterial; // 物料上传动作（过账服务；null=不可用）
    private readonly string _mainSide; // 单工位模式所用工位（"R"/"L"）；双工位下不参与显隐判断

    public WorkOrderViewModel(IDialogService dialogs, OperatorLevel? level,
        WorkOrderBook? book = null, Action<string>? uploadMaterial = null,
        CuringMesOptions? postOptions = null)
    {
        _dialogs = dialogs;
        _level = level;
        _uploadMaterial = uploadMaterial;
        // 工位布局开关（缺省 null=现读 [CuringMes] ini；测试可注入固定 options）：
        // 双工位=两面板全显；单工位只显 Side 侧（Side=L 时主面板用 L 槽），文案去工位字样
        var options = postOptions ?? CuringMesOptions.Load();
        IsDualSide = options.DualSide;
        _mainSide = CuringMesOptions.NormSide(options.Side);
        ShowRightPanel = IsDualSide || _mainSide == "R";
        ShowLeftPanel = IsDualSide || _mainSide == "L";
        SyncFromBook(book ?? WorkOrderStore.Load() ?? WorkOrderStore.BuildDefault());
        ClearCountRtCommand = new RelayCommand(() => EditRtProduced = "0", () => CanEditRoute);
        ClearCountLfCommand = new RelayCommand(() => EditLfProduced = "0", () => CanEditRoute);
        SaveCommand = new RelayCommand(Save);
        UploadRMaterialCommand = new RelayCommand(() => UploadMaterial("R"), () => CanUpload);
        UploadLMaterialCommand = new RelayCommand(() => UploadMaterial("L"), () => CanUpload);
    }

    // ---------- 工位布局（构造定死不随运行时变；窗开一次读一次） ----------

    /// <summary>双工位开关（[CuringMes] DualSide）：true=R/L 两面板全显带工位字样。</summary>
    public bool IsDualSide { get; }

    /// <summary>R 侧面板显隐：双工位恒显；单工位=工位是 R 才显（XAML Content 也随文案属性切换）。</summary>
    public bool ShowRightPanel { get; }

    /// <summary>L 侧面板显隐：双工位恒显；单工位=工位是 L 才显。</summary>
    public bool ShowLeftPanel { get; }

    /// <summary>窗标题/标题条文案：双工位带（R/L 双工位）字样，单工位不带。</summary>
    public string WindowTitle => IsDualSide ? "工单信息（R/L 双工位）" : "工单信息";

    /// <summary>R 侧面板标题（单工位=就是唯一那套，不带工位字样）。</summary>
    public string RightPanelTitle => IsDualSide ? "工单信息（R）" : "工单信息";

    /// <summary>L 侧面板标题（双工位专用；单工位 L 口径下同款去字样）。</summary>
    public string LeftPanelTitle => IsDualSide ? "工单信息（L）" : "工单信息";

    /// <summary>R 侧清零钮文案。</summary>
    public string ClearRText => IsDualSide ? "R 计数清零" : "计数清零";

    /// <summary>L 侧清零钮文案。</summary>
    public string ClearLText => IsDualSide ? "L 计数清零" : "计数清零";

    /// <summary>R 侧上传钮文案（报本侧工位）。</summary>
    public string UploadRText => IsDualSide ? "上传 R 物料" : "物料上传";

    /// <summary>L 侧上传钮文案（报本侧工位）。</summary>
    public string UploadLText => IsDualSide ? "上传 L 物料" : "物料上传";

    // ---------- 门禁（级别不够的编辑区在 XAML 里禁用） ----------

    /// <summary>途程单号/预设数量/已生产数量/计数清零：LEVEL.2+（班长及以上）。</summary>
    public bool CanEditRoute => OperatorLevelInfo.AtLeast(_level, OperatorLevel.Level2);

    /// <summary>制程代码（PT_NO）：LEVEL.3+（工程师及以上）。</summary>
    public bool CanEditProcess => OperatorLevelInfo.AtLeast(_level, OperatorLevel.Level3);

    /// <summary>门禁说明（窗内提示行）。</summary>
    public string GateText => _level == null
        ? "未登录——只读查看"
        : $"{OperatorLevelInfo.DisplayName(_level.Value)} 登录：" +
          (CanEditProcess ? "可编辑全部"
            : CanEditRoute ? "可编辑途程/数量，制程代码需工程师"
            : "仅可录员工工号/条码");

    // ---------- 编辑区（打开回显；保存前 Flush 回对象） ----------

    private string _editRtRcNo = "";
    public string EditRtRcNo { get => _editRtRcNo; set => Set(ref _editRtRcNo, value ?? ""); }
    private string _editRtPreset = "";
    public string EditRtPreset { get => _editRtPreset; set => Set(ref _editRtPreset, value ?? ""); }
    private string _editRtProduced = "";
    public string EditRtProduced { get => _editRtProduced; set => Set(ref _editRtProduced, value ?? ""); }
    private string _editLfRcNo = "";
    public string EditLfRcNo { get => _editLfRcNo; set => Set(ref _editLfRcNo, value ?? ""); }
    private string _editLfPreset = "";
    public string EditLfPreset { get => _editLfPreset; set => Set(ref _editLfPreset, value ?? ""); }
    private string _editLfProduced = "";
    public string EditLfProduced { get => _editLfProduced; set => Set(ref _editLfProduced, value ?? ""); }
    private string _editEmpNo = "";
    public string EditEmpNo { get => _editEmpNo; set => Set(ref _editEmpNo, value ?? ""); }
    private string _editBcNo = "";
    public string EditBcNo { get => _editBcNo; set => Set(ref _editBcNo, value ?? ""); }
    private string _editPtNo1 = "";
    public string EditPtNo1 { get => _editPtNo1; set => Set(ref _editPtNo1, value ?? ""); }
    private string _editPtNo2 = "";
    public string EditPtNo2 { get => _editPtNo2; set => Set(ref _editPtNo2, value ?? ""); }
    private bool _editUploadEnabled;
    public bool EditUploadEnabled { get => _editUploadEnabled; set => Set(ref _editUploadEnabled, value); }

    public RelayCommand ClearCountRtCommand { get; }
    public RelayCommand ClearCountLfCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand UploadRMaterialCommand { get; }
    public RelayCommand UploadLMaterialCommand { get; }

    /// <summary>
    /// 物料上传按钮可用：过账服务已装配（未装配=程序未启用过账，按钮置灰）。
    /// 使能校验在过账服务里再查一道（读盘上工单 UploadEnabled，双保险）。
    /// </summary>
    public bool CanUpload => _uploadMaterial != null;

    /// <summary>上传提交后的提示文本（结果看主窗顶栏过账状态与 MES 日志）。</summary>
    private string _uploadStatusText = "";
    public string UploadStatusText
    {
        get => _uploadStatusText;
        private set => Set(ref _uploadStatusText, value);
    }

    /// <summary>保存成功后置 true（DialogService 据此返回 true，主窗提示）。</summary>
    public bool CloseDialog { get; private set; }

    private void SyncFromBook(WorkOrderBook b)
    {
        EditRtRcNo = b.Right.RcNo;
        EditRtPreset = b.Right.PresetQty.ToString();
        EditRtProduced = b.Right.ProducedQty.ToString();
        EditLfRcNo = b.Left.RcNo;
        EditLfPreset = b.Left.PresetQty.ToString();
        EditLfProduced = b.Left.ProducedQty.ToString();
        EditEmpNo = b.EmpNo;
        EditBcNo = b.BcNo;
        EditPtNo1 = b.PtNo1;
        EditPtNo2 = b.PtNo2;
        EditUploadEnabled = b.UploadEnabled;
    }

    private void Save()
    {
        if (!SaveCore())
            return;
        CloseDialog = true;
        // 成功提示由主窗统一弹（与点表/策略窗同款）
    }

    /// <summary>
    /// 物料上传（对照 HMI"批次物料上报"：R/L 选工位 + 上传动作）：
    /// 先保存工单（把刚录的条码/工号落盘——过账服务现读盘上工单），
    /// 再触发后台 API0032。保存失败（数字格式/落盘错）不上传。
    /// </summary>
    private void UploadMaterial(string side)
    {
        if (_uploadMaterial == null)
            return;
        if (!SaveCore())
        {
            UploadStatusText = "保存失败，未上传（见弹框提示）";
            return;
        }
        _uploadMaterial(side);
        // 双工位带工位字样（区分两套工单）；单工位不带（与界面去工位字样的口径一致）
        UploadStatusText = IsDualSide
            ? $"{DateTime.Now:HH:mm:ss} 已提交 {side} 工位物料上传（结果见主窗顶栏过账状态）"
            : $"{DateTime.Now:HH:mm:ss} 已提交物料上传（结果见主窗顶栏过账状态）";
    }

    /// <summary>保存公共体（Save 与上传共用）：Flush+校验+落盘。成功 true。</summary>
    private bool SaveCore()
    {
        FlushToBook();
        if (_flushErrors.Count > 0)
        {
            _dialogs.ShowMessage(string.Join("\r\n", _flushErrors), "保存失败（数字格式）");
            return false;
        }
        var book = new WorkOrderBook();
        FlushToBook(book);
        try
        {
            WorkOrderStore.Save(book);
        }
        catch (Exception ex)
        {
            _dialogs.ShowMessage($"工单落盘失败：{ex.Message}", "保存失败");
            return false;
        }
        return true;
    }

    /// <summary>编辑区 → 工单对象（保存前调；数字解析失败进 _flushErrors）。</summary>
    private void FlushToBook(WorkOrderBook? target = null)
    {
        _flushErrors.Clear();
        var b = target ?? new WorkOrderBook();
        // 门禁兜底：级别不够的字段不落编辑区值（防绕过界面直改）——回读原值
        var src = WorkOrderStore.Load() ?? WorkOrderStore.BuildDefault();
        b.Right.RcNo = CanEditRoute ? EditRtRcNo.Trim() : src.Right.RcNo;
        b.Left.RcNo = CanEditRoute ? EditLfRcNo.Trim() : src.Left.RcNo;
        b.Right.PresetQty = CanEditRoute ? ParseOrError(EditRtPreset, src.Right.PresetQty, "R 预设数量") : src.Right.PresetQty;
        b.Left.PresetQty = CanEditRoute ? ParseOrError(EditLfPreset, src.Left.PresetQty, "L 预设数量") : src.Left.PresetQty;
        b.Right.ProducedQty = CanEditRoute ? ParseOrError(EditRtProduced, src.Right.ProducedQty, "R 已生产数量") : src.Right.ProducedQty;
        b.Left.ProducedQty = CanEditRoute ? ParseOrError(EditLfProduced, src.Left.ProducedQty, "L 已生产数量") : src.Left.ProducedQty;
        b.EmpNo = EditEmpNo.Trim();          // L1+（登录即可录）
        b.BcNo = EditBcNo.Trim();            // L1+
        b.PtNo1 = CanEditProcess ? EditPtNo1.Trim() : src.PtNo1;
        b.PtNo2 = CanEditProcess ? EditPtNo2.Trim() : src.PtNo2;
        b.UploadEnabled = CanEditProcess && EditUploadEnabled; // 使能按工程师级
    }

    private int ParseOrError(string text, int fallback, string what)
    {
        if (int.TryParse(text?.Trim(), out var v) && v >= 0)
            return v;
        _flushErrors.Add($"{what} \"{text}\" 不是非负整数。");
        return fallback;
    }
}
