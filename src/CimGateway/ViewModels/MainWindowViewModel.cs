// =========================================================================
// MainWindowViewModel：主窗全部状态 + 命令（MVVM，多机网关视图）。
//
// 绑什么：菜单文本+门禁（角色 gating）/ 时钟 / 多机视图筛选+扫描周期+
//   8 面板 / 连接设备按钮。单机视图与"运行模式"菜单已随单机链路删除
//   （P2.1，Phase 2 程序②= CimGateway 整线过账）。
// Model 层不动：MultiMachineManager / LocalLogService 照用（事件在后台
//   线程触发，VM 经 UiInvoke 更新）。
// 节拍说明（相对老项目的现代化改动，铁律：UI 线程禁网络 IO）：
//   老项目 Timer 跑 UI 线程（160 次机台读 + 同步 POST 冻界面）；
//   新项目各机台网关线程跑后台（StationGateway 自带线程），VM 只收事件。
// 关窗：View.OnClosing 调 OnClosing（确认经服务；重启中跳过确认，
//   否则确认框卡住旧进程造成双开）；Dispose 释放网关组。
// =========================================================================

using CureLine.ViewModels;
using CimGateway.Themes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Timers;
using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Core.Modes;
using CureLine.Core.Services;

namespace CimGateway.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private MultiMachineManager? _multi;
    private readonly bool _useSim;
    private readonly object _simLock = new();
    private readonly List<SimulatedStationClient> _simClients = new();  // 演示触发器用
    private bool _disposed;
    private readonly System.Timers.Timer _clock = new(1000);

    // ---- 登录态 ----
    private LoginRole _role = LoginRole.None;
    private string _loginUser = "";

    // ---- 多机状态 ----
    private int _selectedViewIndex;
    private int _gridColumns = 4;
    private string _scanCycleText = "";
    private string _clockText = "";
    private string _connectText = "";
    private bool _connectEnabled = true;

    // ---- 整线汇总（S1，观测口径：进程内累计、重启清零，对账以 MES 日志为准）----
    private readonly long[] _scanMs = new long[8];   // 各机台实测扫描周期（单机视图用）
    private int _todayTriggered;
    private int _todayOk;
    private int _todayFail;
    private bool? _mesLastOk;                        // 最近一次报文结果（null=从未触发）
    private string _mesLastTime = "";

    public MainWindowViewModel(IDialogService? dialogs = null)
    {
        _dialogs = dialogs ?? DialogService.Instance;
        // 模拟开关：config.ini [setting] Simulate=1 则全用内存模拟。
        _useSim = IniFile.ReadStr(AppPaths.ConfigIni, "setting", "Simulate", "0") == "1";

        for (int i = 0; i < 8; i++)
        {
            int id = i;
            Stations.Add(new StationPanelViewModel(id,
                st => _dialogs.ShowMesLog(st),
                line => ShowStationDetail(line)));
        }
        ViewOptions.Add(Tr("全部机台"));
        for (int i = 0; i < 8; i++)
            ViewOptions.Add($"{i + 1}号-{MesLogger.StationNames[i]}");

        InitMulti();
        RefreshSummary();   // 汇总条初始态（配置值/端点/零计数）

        _clock.Elapsed += (_, _) => UiInvoke(() =>
            ClockText = DateTime.Now.ToString("G"));
        _clock.Start();
        ClockText = DateTime.Now.ToString("G");

        ConnectMultiCommand = new RelayCommand(ConnectMulti, () => ConnectEnabled);
        ShowLogCommand = new RelayCommand(() => _dialogs.ShowMesLog(-1));
        ShowDebugCommand = new RelayCommand(() => _dialogs.ShowMesDebug());
        ShowGatewayCommand = new RelayCommand(ShowGateway);
        ShowChangePwdCommand = new RelayCommand(ShowChangePwd);
        ShowShortcutCommand = new RelayCommand(ShowShortcut);
        SwitchLanguageCommand = new RelayCommand(SwitchLanguage);
        LoginCommand = new RelayCommand(LoginLogout);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        // 主题按钮文案对齐 App.OnStartup 已应用的当前主题（测试态无 App：ThemeManager
        // 只记账不碰资源，Current 保持缺省 Light → 按钮显示目标"深色"）
        ToggleThemeText = ThemeManager.Current == "Dark" ? "浅色" : "深色";

        RefreshTexts();
        LocalLog(Tr("软件启动"), -1);
    }

    // ================= 登录态 + 菜单 =================

    public string LoginHeader => _role == LoginRole.None
        ? Tr("登录") : $"{_loginUser} ({Tr("切换/退出")})";

    /// <summary>顶栏登录钮短文案（按钮空间小：未登录"登录"，已登录只显示账号——
    /// 点击仍是 LoginLogout 同链：已登录再点弹确认退出/切账号）。</summary>
    public string BtnLoginText => _role == LoginRole.None ? Tr("登录") : _loginUser;

    /// <summary>网关设置钮显隐（仅 dev 可见，XAML 经转换器绑 Visibility）。</summary>
    public bool GatewayVisible => _role == LoginRole.Dev;

    public bool GatewayEnabled => _role == LoginRole.Dev;
    public bool SettingsItemEnabled => RunModeSettings.IsLoggedIn(_role);

    public string TitleText => Tr("CIM通信网关");
    public string ViewLabel => Tr("视图:");
    public string BtnLogText => Tr("日志");
    public string BtnDebugText => Tr("调试");
    public string BtnGatewayText => Tr("网关设置");
    public string BtnChangePwdText => Tr("修改密码");
    public string BtnShortcutText => Tr("快捷方式");
    public string BtnLangText =>
        LanguageService.CurrentLanguage == "CH" ? "English" : "中文";

    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ShowDebugCommand { get; }
    public RelayCommand ShowGatewayCommand { get; }
    public RelayCommand ShowChangePwdCommand { get; }
    public RelayCommand ShowShortcutCommand { get; }
    public RelayCommand SwitchLanguageCommand { get; }
    public RelayCommand LoginCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }

    // ---- 双主题（Light=G2 浅色 / Dark=G1 深空，与程序①同款交互） ----

    private string _toggleThemeText = "深色";

    /// <summary>主题按钮文案=切换目标（当前浅色显示"深色"，反之"浅色"）。</summary>
    public string ToggleThemeText
    {
        get => _toggleThemeText;
        private set => Set(ref _toggleThemeText, value);
    }

    /// <summary>
    /// 主题切换：目标名 → ThemeManager 换字典（UI 线程，DynamicResource 全窗生效）
    /// → 写回 ini（现场记住，下次启动同主题；写盘失败只丢记忆不回切）→
    /// 按钮文案翻新目标。与程序① MainViewModel.ToggleTheme 同款口径。
    /// </summary>
    private void ToggleTheme()
    {
        var target = ThemeManager.ToggleTarget;
        ThemeManager.ApplyTheme(target);
        try
        {
            IniFile.Write(AppPaths.ConfigIni, "setting", "Theme", target);
        }
        catch
        {
            // 写盘失败不回切——界面已换，只是没记住（下次启动回旧主题）
        }
        ToggleThemeText = target == "Dark" ? "浅色" : "深色";
        LocalLog(Tr("主题切换：") + (target == "Dark" ? "深色（G1）" : "浅色（G2）"), -1);
    }

    public void RefreshTexts()
    {
        ConnectText = Tr("连接设备");
        RefreshAll();
        foreach (var s in Stations)
            s.RefreshTexts();   // 卡片接口短名/徽标随语言重设
        RefreshSummary();
    }

    private static string Tr(string zh) => LanguageService.Tr(zh);

    private void RefreshGating()
    {
        Raise(nameof(LoginHeader));
        Raise(nameof(BtnLoginText));
        Raise(nameof(GatewayVisible));
        Raise(nameof(GatewayEnabled));
        Raise(nameof(SettingsItemEnabled));
    }

    /// <summary>
    /// 登录按钮：未登录弹登录窗，成功记角色并刷新门禁；
    /// 已登录再点确认是否退出（是→清登录态；否→弹窗切换账号）。
    /// </summary>
    private void LoginLogout()
    {
        if (_role != LoginRole.None)
        {
            bool? ask = _dialogs.ConfirmOrCancel(
                $"{Tr("当前已登录")}: {_loginUser}\n" +
                Tr("是否退出登录？（点否则切换账号）"), Tr("登录"));
            if (ask == null)
                return;
            if (ask == true)
            {
                _role = LoginRole.None;
                _loginUser = "";
                RefreshGating();
                return;
            }
        }
        var (ok, role, user) = _dialogs.ShowLogin();
        if (!ok)
            return;
        _role = role;
        _loginUser = user;
        RefreshGating();
    }

    /// <summary>未登录拦一刀（设置子项共用）。</summary>
    private bool RequireLogin()
    {
        if (RunModeSettings.IsLoggedIn(_role))
            return true;
        _dialogs.ShowMessage(Tr("请先登录"), Tr("登录"));
        return false;
    }

    private void ShowGateway()
    {
        if (!RequireLogin())
            return;
        if (!RunModeSettings.IsDev(_role))
        {
            _dialogs.ShowMessage(Tr("需要 dev 权限"), Tr("设置"),
                DialogImage.Warning);
            return;
        }
        if (_dialogs.ShowGatewayConfig())
            RestartConnections();
    }

    /// <summary>
    /// 按新配置热重建全部机台连接（网关配置窗保存后调，V0.0.41 起**不再重启程序**）：
    /// 后台 Dispose 旧 Manager（内部停各机台网关线程）→ InitMulti 重建
    /// （factory 即时读新 AppConfig 静态值：启用/IP/端口/机台名）→ 复用
    /// ConnectMulti 的"连接中..."口径后台重连。热更语义：保存即生效，
    /// 顶栏连接钮/心跳灯随新配置刷新；日志只记一条重建边沿。
    /// </summary>
    public void RestartConnections()
    {
        var old = _multi;
        if (old == null)
            return;
        SetConnectUi(false, Tr("重连中..."));
        Task.Run(() =>
        {
            old.Dispose();      // 停旧网关线程 + 关旧 TCP（新连接另起，不共 socket）
            InitMulti();        // 新 Manager：factory 读已刷新的 AppConfig
            ConnectMulti();     // 后台逐台连（阻塞网络 IO 在 Task 内）
            UiInvoke(() =>
            {
                RefreshSummary();   // 端点/配置值可能已改，汇总条即时对齐
                LocalLog(Tr("连接已按新配置重建（保存即生效）"), -1);
            });
        });
    }

    private void ShowChangePwd()
    {
        if (!RequireLogin())
            return;
        _dialogs.ShowChangePassword(_loginUser, _role);
    }

    private void ShowShortcut()
    {
        if (!RequireLogin())
            return;
        _dialogs.CreateDesktopShortcut();
    }

    private void SwitchLanguage()
    {
        LanguageService.CurrentLanguage =
            LanguageService.CurrentLanguage == "CH" ? "EN" : "CH";
        IniFile.Write(AppPaths.ConfigIni, "setting", "Language",
            LanguageService.CurrentLanguage);
        RefreshTexts();
    }

    // ================= 通用：时钟 + 全局日志 =================

    public string ClockText
    {
        get => _clockText;
        private set => Set(ref _clockText, value);
    }

    public string ConnectText
    {
        get => _connectText;
        private set => Set(ref _connectText, value);
    }

    public bool ConnectEnabled
    {
        get => _connectEnabled;
        private set
        {
            if (Set(ref _connectEnabled, value))
                ConnectMultiCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ConnectMultiCommand { get; }

    /// <summary>
    /// 本机操作日志：落盘 LocalLogService 全量；机台消息上对应面板，
    /// 全局消息（station=-1）不占 UI（多机视图无全量列表，MES 日志窗可查）。
    /// </summary>
    private void LocalLog(string message, int station)
    {
        string line = LocalLogService.Write(message, station);
        if (station >= 0 && station < 8)
            Stations[station].AddLog(line);
    }

    // ================= 多机 =================

    public ObservableCollection<StationPanelViewModel> Stations { get; } = new();
    public ObservableCollection<string> ViewOptions { get; } = new();

    public int SelectedViewIndex
    {
        get => _selectedViewIndex;
        set
        {
            if (!Set(ref _selectedViewIndex, value))
                return;
            ApplyViewFilter();
        }
    }

    public int GridColumns
    {
        get => _gridColumns;
        private set => Set(ref _gridColumns, value);
    }

    public string ScanCycleText
    {
        get => _scanCycleText;
        private set => Set(ref _scanCycleText, value);
    }

    private void InitMulti()
    {
        lock (_simLock)
            _simClients.Clear();
        _multi = new MultiMachineManager(id =>
        {
            if (!_useSim)
                return new ModbusStationClient();
            var sim = new SimulatedStationClient();
            lock (_simLock)
                _simClients.Add(sim);
            return sim;
        });
        _multi.StationLog += (id, m) => LocalLog(m, id);
        _multi.Heartbeat += (id, on) => UiInvoke(() =>
            Stations[id].SetLive(on, false));
        _multi.StationBroken += id => UiInvoke(() =>
            Stations[id].SetLive(false, true));
        // 实测扫描周期按台记账（V0.0.51 起全台上报）：单机视图显示本台实测，
        // 汇总条显示配置值——老项目只显示 0 号机实测的口径由 UI 取数方式取代。
        _multi.ScanElapsed += (id, ms) => UiInvoke(() =>
        {
            if (id >= 0 && id < 8)
                _scanMs[id] = ms;
            if (!IsAllView)
                Raise(nameof(SingleScanText));
        });
        _multi.AlarmRaised += (m, n) => UiInvoke(() => _dialogs.ShowAlarm(m, n));
        // 观测事件分发（Core 后台线程触发，先更新面板模型再统一刷新汇总）：
        _multi.ApiTriggered += (id, api) => UiInvoke(() =>
        {
            if (id < 0 || id >= 8)
                return;
            Stations[id].OnApiTriggered(api);
            _todayTriggered++;
            RefreshSummary();
        });
        _multi.ApiDone += r => UiInvoke(() =>
        {
            if (r.StationId < 0 || r.StationId >= 8)
                return;
            Stations[r.StationId].OnApiDone(r);
            if (r.Ok) _todayOk++; else _todayFail++;
            _mesLastOk = r.Ok;
            _mesLastTime = DateTime.Now.ToString("HH:mm:ss");
            RefreshSummary();
        });
        StartDemoTriggers();
    }

    // ================= 演示触发器（仅 Simulate=1，真机零影响） =================
    //
    // 模拟客户端是纯内存被动件，触发字不会自己置 1——演示模式（Simulate=1）
    // 下后台每 7s 随机挑一台、随机挑一个接口，预置最小合法 JSON 到 DATA 区并把
    // 触发字置 1，走与真机完全相同的"扫触发→组包→POST→回写→观测事件"链路
    // （手册"模拟 8 机台网关含触发回写全链路"的实装）。预置 JSON 只求能通过
    // ValidateJson 反序列化（字段缺省 null 即可），MES 语义字段由现场对账看。

    private void StartDemoTriggers()
    {
        if (!_useSim)
            return;
        Task.Run(async () =>
        {
            var rnd = new Random();
            while (!_disposed)
            {
                await Task.Delay(7000);
                SimulatedStationClient? sim;
                lock (_simLock)
                {
                    if (_simClients.Count == 0)
                        continue;
                    sim = _simClients[rnd.Next(_simClients.Count)];
                }
                int idx = rnd.Next(MesConfig.ApiCodes.Length);
                string trg = MesConfig.GetTrigger(MesConfig.ApiCodes[idx]);
                if (string.IsNullOrEmpty(trg))
                    continue;
                sim.PresetString(MesConfig.ApiAddresses[idx].StationData, DemoJson(idx));
                sim.PresetWord(trg, 1);
            }
        });
    }

    /// <summary>演示用最小 JSON：数组接口（0027/28/33）单元素数组，对象接口单对象。</summary>
    private static string DemoJson(int apiIndex) =>
        (apiIndex == 0 || apiIndex == 1 || apiIndex == 5)
            ? "[{\"RC_NO\":\"DEMO\"}]"
            : "{\"RC_NO\":\"DEMO\"}";

    /// <summary>
    /// 连接全部机台（老项目 Button1_Click 循环语义；逐台 Connect 是阻塞
    /// 网络 IO，必须后台跑；按钮文案"连接中..."代替老项目 ProgressBar）。
    /// </summary>
    private void ConnectMulti()
    {
        if (_multi == null)
            return;
        SetConnectUi(false, "连接中...");
        Task.Run(() =>
        {
            var (ok, skipped) = _multi.ConnectAll();
            UiInvoke(() =>
            {
                LocalLog($"连接完成：成功 {ok} 台，跳过 {skipped} 台", -1);
                SetConnectUi(false, Tr("连接设备"));
                RefreshSummary();   // 在线/启用数在连接完成后定形
            });
        });
    }

    private void SetConnectUi(bool enabled, string text) =>
        UiInvoke(() =>
        {
            ConnectEnabled = enabled;
            ConnectText = text;
        });

    private void ApplyViewFilter()
    {
        for (int i = 0; i < 8; i++)
            Stations[i].Visible = _selectedViewIndex == 0 || _selectedViewIndex - 1 == i;
        GridColumns = _selectedViewIndex == 0 ? 4 : 1;
        Raise(nameof(IsAllView));
        Raise(nameof(IsSingleView));
        Raise(nameof(SinglePanel));
        RefreshSummary();
    }

    // ================= 整线汇总条（S1）+ 单机视图（S3） =================
    //
    // 设计稿：docs/UI设计_CIM主窗业务化_方案C定稿.html §1/§2。口径红线：
    // ① 统计=进程内观测累计，重启清零，对账以 MES 日志窗/日志文件为准；
    // ② MES 端点状态点只反映最近一次报文结果，不做心跳 ping（无触发时的
    //    "连通"是假象）；③ 扫描周期：汇总条=配置值，单机视图=该台实测。

    /// <summary>当前是"全部机台"视图（8 卡网格）——单机视图的显隐开关。</summary>
    public bool IsAllView => _selectedViewIndex == 0;

    /// <summary>当前是单机视图（XAML 切 Visibility）。</summary>
    public bool IsSingleView => _selectedViewIndex != 0;

    /// <summary>单机视图选中的面板（未选=0 号占位，视图不渲染其内容）。</summary>
    public StationPanelViewModel SinglePanel =>
        Stations[Math.Clamp(_selectedViewIndex - 1, 0, 7)];

    public string ScanIntervalText => $"{AppConfig.ScanIntervalMs} ms";

    /// <summary>扫描周期数值（汇总条 Run 绑数字部分，单位在 XAML）。</summary>
    public string ScanIntervalMsText => AppConfig.ScanIntervalMs.ToString();

    // ---- 汇总条/单机视图文案（Tr 派生属性：RefreshAll 时自动重读换语言） ----
    public string TrOnline => $"{Tr("在线")} / {Tr("启用")}";
    public string TrToday => Tr("今日过账");
    public string TrOk => $" {Tr("成功")}";
    public string TrFail => $" {Tr("失败")}";
    public string TrTrig => Tr("触发");
    public string TrLastPosting => $"{Tr("最近报文")} ";
    public string TrScan => Tr("扫描周期");
    public string TrConfigured => $"{Tr("配置值")} · {Tr("单机视图见实测")}";
    public string TrAllLogs => Tr("查看全部日志");
    public string TrMesDebug => Tr("MES 调试");
    public string TrApiDetails => Tr("接口明细");
    public string TrDetailHint => Tr("最后 STATUS / 最后触发 / 耗时");
    public string TrTriggerStream => Tr("最近触发流");
    public string TrLive => Tr("实时");
    public string TrOps => Tr("操作");

    /// <summary>今日过账副行："触发 N 次 · 进程内累计，重启清零"（观测口径）。</summary>
    public string TodayDetail =>
        $"{Tr("触发")} {_todayTriggered} {Tr("次")} · {Tr("进程内累计，重启清零")}";

    /// <summary>启用机台数（"已配置为不连接"的面板剔除；未知态按启用计）。</summary>
    public int EnabledCount => Stations.Count(s => s.Enabled != false);

    /// <summary>在线机台数（启用且未断连——灯非 Red 的启用台）。</summary>
    public int OnlineCount =>
        Stations.Count(s => s.Enabled != false && s.LampColor != "Red");

    public int TodayTriggered => _todayTriggered;
    public int TodayOk => _todayOk;
    public int TodayFail => _todayFail;

    /// <summary>MES 端点 host:port（取 API0030 主接口 URL 解析；未配置显示 —）。</summary>
    public string MesEndpoint
    {
        get
        {
            string url = MesConfig.GetUrl("API0030");
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                return $"{u.Host}:{u.Port}";
            return url.Length > 0 ? url : "—";
        }
    }

    /// <summary>最近报文时间（HH:mm:ss；从未触发显示"未触发"）。</summary>
    public string MesLastText => _mesLastOk == null
        ? Tr("未触发") : $"{_mesLastTime}";

    /// <summary>MES 状态点键：Ok / Fail / Idle（XAML 触发器映射 State* 点色）。</summary>
    public string MesLampKind => _mesLastOk == null ? "Idle" : _mesLastOk == true ? "Ok" : "Fail";

    /// <summary>单机视图：机台标题（"3号-机台3"，与视图筛选下拉同口径）。</summary>
    public string SingleTitle => IsAllView ? "" : ViewOptions[_selectedViewIndex];

    /// <summary>单机视图：该台 IP（配置值）。</summary>
    public string SingleIp => AppConfig.StationIps[Math.Clamp(_selectedViewIndex - 1, 0, 7)];

    /// <summary>单机视图：该台实测扫描周期（尚无实测显示 —）。</summary>
    public string SingleScanText
    {
        get
        {
            long ms = _scanMs[Math.Clamp(_selectedViewIndex - 1, 0, 7)];
            return ms > 0 ? $"{ms} ms" : "—";
        }
    }

    /// <summary>单机视图：该台今日成功/失败合计（取面板模型，事件驱动已更新）。</summary>
    public int SingleTodayOk => SinglePanel.ApiStats.Sum(s => s.Ok);
    public int SingleTodayFail => SinglePanel.ApiStats.Sum(s => s.Fail);
    public int SingleTodayTriggered => SinglePanel.TotalTriggered;

    /// <summary>汇总条与单机视图的派生文本统一刷新（观测事件/视图切换后调）。</summary>
    private void RefreshSummary()
    {
        ScanCycleText = $"{Tr("扫描周期: ")}{AppConfig.ScanIntervalMs} ms（{Tr("配置值")}）";
        Raise(nameof(EnabledCount));
        Raise(nameof(OnlineCount));
        Raise(nameof(TodayTriggered));
        Raise(nameof(TodayOk));
        Raise(nameof(TodayFail));
        Raise(nameof(MesEndpoint));
        Raise(nameof(MesLastText));
        Raise(nameof(MesLampKind));
        Raise(nameof(SingleTitle));
        Raise(nameof(SingleIp));
        Raise(nameof(SingleScanText));
        Raise(nameof(SingleTodayOk));
        Raise(nameof(SingleTodayFail));
        Raise(nameof(SingleTodayTriggered));
        Raise(nameof(ScanIntervalText));
    }

    /// <summary>
    /// 机台日志单击（复刻老项目 ListBox1_SelectedIndexChanged 全逻辑）：
    /// 先 MsgBox 全文；若含"存图上报"+"图片路径"，按⚫取第二段，文件存在开图，
    /// 不存在/格式不对给对应提示（老项目 MsgBox 原样）。
    /// </summary>
    private void ShowStationDetail(string line)
    {
        UiInvoke(() =>
        {
            _dialogs.ShowMessage(line, TitleText);
            string imgTag = Tr("存图上报");
            string pathTag = Tr("图片路径");
            if (line.Contains(imgTag) && line.Contains(pathTag))
            {
                string[] parts = line.Split('⚫');
                if (parts.Length >= 3)
                {
                    string path = parts[1];
                    if (File.Exists(path))
                        _dialogs.ShowImage(path);
                    else
                        _dialogs.ShowMessage(Tr("图片文件不存在") + "\r\n" + path,
                            TitleText);
                }
                else
                {
                    _dialogs.ShowMessage(Tr("图片路径不存在,请查看详细日志"), TitleText);
                }
            }
        });
    }

    // ================= 关窗 =================

    /// <summary>
    /// 关窗确认（View.OnClosing 调）：用户手动关才确认。
    /// 返回 true 表示放行关闭。
    /// </summary>
    public bool OnClosing()
    {
        return _dialogs.Confirm(Tr("是否退出?"), TitleText);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _clock.Stop();
        _clock.Dispose();
        _multi?.Dispose();
    }
}
