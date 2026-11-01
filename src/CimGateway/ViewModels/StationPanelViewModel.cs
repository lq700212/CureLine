// =========================================================================
// StationPanelViewModel：单机台面板的状态 + 命令（MVVM，V0.0.51 业务化重做）。
//
// 绑什么（卡片=过账面板，一比一还原 docs/UI设计_CIM主窗业务化_方案C定稿.html §1）：
//   头行=机台名 + 心跳灯 + 今日三数徽标（触发/失败）；
//   ApiStats=6 接口状态微表（每行：接口码 chip / 语义短名 / 触发 / 成功 / 失败 /
//     最后结果点——绿=STATUS1、红=NG或失败、灰=未触发）；
//   LatestMessage=底部最新消息一行（点击开分机台日志窗，卡片日志 ListBox 已删）。
// 数据源：MainWindowViewModel 分发 Core 观测事件 ApiTriggered/ApiDone
//   （Core/Modes/MultiMachineGateway 的 ApiResult）；连接态沿旧事件。
// 灯语义（四态）：Red 断连 > Yellow 今日有失败 > Lime 在线 > Gray 未连/未启用。
//   Yellow 是观测增强（原三态基础上加），通讯语义零改动。
// 触发流：LogLines 保留（上限 100 条，老项目语义）——单机视图（S3）右侧
//   "最近触发流"绑它；SelectedLine 单击即全文（老项目语义，存图链仍走主窗）。
// 跨窗动作经回调由 MainWindowVM 注入（开日志窗 / 全文+存图链路），
//   本 VM 不认 IDialogService，保持面板独立可复用。
// =========================================================================

using CureLine.ViewModels;
using System.Collections.ObjectModel;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Core.Modes;

namespace CimGateway.ViewModels;

/// <summary>
/// 6 接口状态微表的一行（纯观测行，不改协议）：LastOk=null 表示该接口今日
/// 从未触发（灰点）；计数为进程内累计、重启清零——对账以 MES 日志为准。
/// </summary>
public class ApiStatRow : ViewModelBase
{
    private int _triggered;
    private int _ok;
    private int _fail;
    private bool? _lastOk;
    private string _lastTime = "—";
    private string _lastElapsed = "—";

    public ApiStatRow(string code, string name)
    {
        Code = code;   // "0027"（显示用短码，去 API 前缀）
        _name = name;  // 语义短名（Tr 后传入；语言切换经 UpdateName 重设）
    }

    public string Code { get; }

    private string _name;
    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    /// <summary>语言切换时重设语义短名（MainWindowVm.RefreshTexts 链）。</summary>
    public void UpdateName(string name) => Name = name;

    public int Triggered { get => _triggered; private set => Set(ref _triggered, value); }
    public int Ok { get => _ok; private set => Set(ref _ok, value); }
    public int Fail { get => _fail; private set => Set(ref _fail, value); }

    /// <summary>最后结果：true=成功(STATUS1) / false=NG或失败 / null=未触发（灰点）。</summary>
    public bool? LastOk
    {
        get => _lastOk;
        private set
        {
            if (Set(ref _lastOk, value))
            {
                Raise(nameof(LampKind));
                Raise(nameof(LastStatusText));
            }
        }
    }

    /// <summary>状态点键（XAML DataTrigger 映射主题 State* 点色）：
    /// "Ok"/"Fail"/"Idle" 三态。</summary>
    public string LampKind => LastOk == true ? "Ok" : LastOk == false ? "Fail" : "Idle";

    /// <summary>单机视图明细表"最后 STATUS"列：1 / 2 / —（未触发）。</summary>
    public string LastStatusText => LastOk == null ? "—" : (LastOk == true ? "1 / OK" : "2 / NG");

    /// <summary>单机视图明细表"最后触发"列（HH:mm:ss；未触发 —）。</summary>
    public string LastTime
    {
        get => _lastTime;
        private set => Set(ref _lastTime, value);
    }

    /// <summary>单机视图明细表"耗时"列（报文处理耗时；未触发 —，超时显示"超时"）。</summary>
    public string LastElapsed
    {
        get => _lastElapsed;
        private set => Set(ref _lastElapsed, value);
    }

    public void OnTriggered()
    {
        Triggered++;
    }

    /// <summary>过账完成：累计 + 记最后结果（ok 带耗时，失败按状态词给"超时/NG"）。</summary>
    public void OnDone(bool ok, string status, string message, long elapsedMs)
    {
        if (ok) Ok++; else Fail++;
        LastOk = ok;
        LastTime = DateTime.Now.ToString("HH:mm:ss");
        LastElapsed = ok ? $"{elapsedMs} ms"
            : message.Contains("超时") || message.Contains("timeout") ? "超时"
            : $"{elapsedMs} ms";
    }
}

public class StationPanelViewModel : ViewModelBase
{
    private readonly Action<int> _openLog;
    private readonly Action<string> _showDetail;
    private string _lampColor = "Gray";
    private string? _selectedLine;
    private bool _visible = true;
    private bool? _enabled;          // null=未知（尚未连接尝试）
    private bool _hasFailToday;
    private string _latestMessage = "";
    private string _latestKind = "Info";   // Ok / Warn / Info（消息行三态色）

    /// <summary>6 接口语义短名（与客户手册 §3 接口清单同源，Tr 词条在 LanguageService）。</summary>
    private static readonly (string Code, string Key)[] ApiTable =
    {
        ("0027", "固化时间"), ("0028", "报警异常"), ("0030", "进出站"),
        ("0031", "物料绑定"), ("0032", "物料上传"), ("0033", "测试记录"),
    };

    public StationPanelViewModel(int stationId,
        Action<int> openLog, Action<string> showDetail)
    {
        StationId = stationId;
        _openLog = openLog;
        _showDetail = showDetail;
        Name = MesLogger.StationNames[stationId];
        OpenLogCommand = new RelayCommand(() => _openLog(StationId));
        foreach (var (code, key) in ApiTable)
            ApiStats.Add(new ApiStatRow(code, Tr(key)));
    }

    private static string Tr(string zh) => LanguageService.Tr(zh);

    public int StationId { get; }

    public string Name { get; }

    /// <summary>心跳灯色（Gray 未连/未启用 / Green 常亮 / Lime 闪 / Red 断连 /
    /// Yellow 今日有失败——四态优先级见类头）。</summary>
    public string LampColor
    {
        get => _lampColor;
        private set => Set(ref _lampColor, value);
    }

    /// <summary>启用态（null=未知）：收到"已配置为不连接本工站"→false（灰点"未启用"），
    /// 连接成功/失败→true。未启用台无网关线程，永不参与统计。</summary>
    public bool? Enabled
    {
        get => _enabled;
        private set
        {
            if (Set(ref _enabled, value))
            {
                Raise(nameof(HeadBadge));
                RecomputeLamp();
            }
        }
    }

    /// <summary>6 接口状态微表（固定 6 行，ctor 建好；行内计数见 ApiStatRow）。</summary>
    public ObservableCollection<ApiStatRow> ApiStats { get; } = new();

    /// <summary>触发流行（单机视图右侧"最近触发流"；头插，上限 100，超了清空重记）。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>今日触发数（6 接口合计，头行徽标）。</summary>
    public int TotalTriggered { get; private set; }

    /// <summary>今日失败数（含 NG 与网络失败，头行徽标红字）。</summary>
    public int TotalFail { get; private set; }

    /// <summary>头行今日徽标文案："触发 N · 失败 M"（未启用台显示"未启用"）。</summary>
    public string HeadBadge => Enabled == false
        ? Tr("未启用")
        : $"{Tr("触发")} {TotalTriggered} · {Tr("失败")} {TotalFail}";

    /// <summary>底部最新消息一行（空=尚无消息；点击开分机台日志窗）。</summary>
    public string LatestMessage
    {
        get => _latestMessage;
        private set => Set(ref _latestMessage, value);
    }

    /// <summary>最新消息三态键："Ok" 绿 / "Warn" 红 / "Info" 灰（XAML 触发器映射）。</summary>
    public string LatestKind
    {
        get => _latestKind;
        private set => Set(ref _latestKind, value);
    }

    /// <summary>选中行（单击即全文，老项目 SelectedIndexChanged 语义——存图开图链在此）。</summary>
    public string? SelectedLine
    {
        get => _selectedLine;
        set
        {
            if (Set(ref _selectedLine, value) && value != null)
                _showDetail(value);
        }
    }

    /// <summary>面板显隐（主窗视图筛选"只看某几台"用，XAML 绑 Visibility）。</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    public RelayCommand OpenLogCommand { get; }

    /// <summary>语言切换：6 行语义短名重设 + 全属性重读（HeadBadge/LatestMessage 等）。</summary>
    public void RefreshTexts()
    {
        for (int i = 0; i < ApiStats.Count && i < ApiTable.Length; i++)
            ApiStats[i].UpdateName(Tr(ApiTable[i].Key));
        RefreshAll();
    }

    /// <summary>心跳灯（连接事件驱动：正常 Green/Lime 闪，断连红；黄=今日有失败，
    /// 灰=未启用/未连——重算集中一处，防各事件写灯互相覆盖）。</summary>
    public void SetLive(bool on, bool broken)
    {
        _liveOn = on;
        _liveBroken = broken;
        if (broken)
            Enabled = true;      // 连接过（失败）=启用台
        UiInvoke(RecomputeLamp);
    }

    private bool _liveOn;
    private bool _liveBroken;

    /// <summary>灯色重算（唯一出口）：Red > Gray(未启用) > Yellow(今日有失败) >
    /// Lime(在线闪) > Green(待机)。</summary>
    private void RecomputeLamp() =>
        LampColor = _liveBroken ? "Red"
            : Enabled == false ? "Gray"
            : _hasFailToday ? "Yellow"
            : _liveOn ? "Lime" : "Green";

    /// <summary>Core 观测：接口触发命中（主窗 VM 从 MultiMachineManager 分发来）。</summary>
    public void OnApiTriggered(string apiCode)
    {
        if (FindRow(apiCode) is not { } row)
            return;
        row.OnTriggered();
        TotalTriggered++;
        Raise(nameof(HeadBadge));
    }

    /// <summary>Core 观测：接口过账完成（成功/NG/失败三出口）。</summary>
    public void OnApiDone(ApiResult r)
    {
        if (FindRow(r.ApiCode) is not { } row)
            return;
        row.OnDone(r.Ok, r.Status, r.Message, r.ElapsedMs);
        if (!r.Ok && !_hasFailToday)
        {
            _hasFailToday = true;
            RecomputeLamp();
        }
        TotalFail = ApiStats.Sum(s => s.Fail);
        Raise(nameof(HeadBadge));
    }

    private ApiStatRow? FindRow(string apiCode)
    {
        // apiCode="API0030" → 微表 Code="0030"（尾 4 位匹配，防前缀口径变化）
        string shortCode = apiCode.Length >= 4 ? apiCode[^4..] : apiCode;
        return ApiStats.FirstOrDefault(s => s.Code == shortCode);
    }

    /// <summary>
    /// 追加触发流行（头插，上限 100）+ 底部最新消息派生：
    /// 关键词三态（中英双词表——语言切换后历史行不再重判，只影响新行）：
    /// 成功=MES上传成功 / MES upload success；警告=NG/失败/异常/超时/不存在；
    /// 其余 Info。另有"已配置为不连接本工站"→未启用态（Enabled=false）。
    /// </summary>
    public void AddLog(string line) => UiInvoke(() =>
    {
        if (LogLines.Count > 100)
            LogLines.Clear();
        LogLines.Insert(0, line);

        if (line.Contains(Tr("已配置为不连接本工站")) || line.Contains("not connect"))
        {
            Enabled = false;
            LatestMessage = line;
            LatestKind = "Info";
            return;
        }
        bool ok = line.Contains(Tr("MES上传成功")) || line.Contains("MES upload success")
            || line.Contains(Tr("连接机台成功")) || line.Contains("Connect Station Success");
        bool warn = line.Contains(Tr("MES返回NG")) || line.Contains("MES returned NG")
            || line.Contains(Tr("MES网络异常/超时")) || line.Contains("MES network error/timeout")
            || line.Contains(Tr("MES反序列化失败")) || line.Contains("MES deserialize failed")
            || line.Contains(Tr("MES返回数据解析为空")) || line.Contains("MES response empty")
            || line.Contains(Tr("MES返回数据解析异常")) || line.Contains("MES response parse error")
            || line.Contains(Tr("连接机台失败")) || line.Contains("Connect Station Failed");
        LatestMessage = line;
        LatestKind = ok ? "Ok" : warn ? "Warn" : "Info";
        if (ok || warn)
            Enabled = true;      // 有业务消息 = 启用台（防御：无连接日志也亮）
    });
}
