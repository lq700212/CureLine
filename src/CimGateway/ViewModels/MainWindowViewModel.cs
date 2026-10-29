// =========================================================================
// MainWindowViewModel：主窗全部状态 + 命令（MVVM，多机网关视图）。
//
// 绑什么：菜单文本+门禁（角色 gating）/ 时钟 / 多机视图筛选+扫描周期+
//   8 面板 / 连接设备按钮。单机视图与"运行模式"菜单已随单机链路删除
//   （P2.1，Phase 2 程序②= CimGateway 整线过账）。
// Model 层不动：MultiMachineManager / LocalLogService 照用（事件在后台
//   线程触发，VM 经 UiInvoke 更新）。
// 节拍说明（相对老项目的现代化改动，铁律：UI 线程禁网络 IO）：
//   老项目 Timer 跑 UI 线程（160 次 PLC 读 + 同步 POST 冻界面）；
//   新项目各机台网关线程跑后台（StationGateway 自带线程），VM 只收事件。
// 关窗：View.OnClosing 调 OnClosing（确认经服务；重启中跳过确认，
//   否则确认框卡住旧进程造成双开）；Dispose 释放网关组。
// =========================================================================

using PlcMesBridge.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Timers;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Infrastructure;
using PlcMesBridge.Core.Mes;
using PlcMesBridge.Core.Modes;
using PlcMesBridge.Core.Services;

namespace CimGateway.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IDialogService _dialogs;
    private MultiMachineManager? _multi;
    private readonly bool _useSim;
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

        _clock.Elapsed += (_, _) => UiInvoke(() =>
            ClockText = DateTime.Now.ToString("G"));
        _clock.Start();
        ClockText = DateTime.Now.ToString("G");

        ConnectMultiCommand = new RelayCommand(ConnectMulti, () => ConnectEnabled);
        ShowLogCommand = new RelayCommand(() => _dialogs.ShowMesLog(-1));
        ShowDebugCommand = new RelayCommand(() => _dialogs.ShowMesDebug());
        ShowPlcCommand = new RelayCommand(ShowPlc);
        ShowChangePwdCommand = new RelayCommand(ShowChangePwd);
        ShowShortcutCommand = new RelayCommand(ShowShortcut);
        SwitchLanguageCommand = new RelayCommand(SwitchLanguage);
        LoginCommand = new RelayCommand(LoginLogout);

        RefreshTexts();
        LocalLog(Tr("软件启动"), -1);
    }

    /// <summary>程序化重启中（关窗跳过"是否退出"确认，防双开）。</summary>
    public bool IsRestarting { get; private set; }

    // ================= 登录态 + 菜单 =================

    public string LoginHeader => _role == LoginRole.None
        ? Tr("登录") : $"{_loginUser} ({Tr("切换/退出")})";

    /// <summary>PLC设置项显隐（仅 dev 可见，XAML 经转换器绑 Visibility）。</summary>
    public bool PlcVisible => _role == LoginRole.Dev;

    public bool PlcEnabled => _role == LoginRole.Dev;
    public bool SettingsItemEnabled => RunModeSettings.IsLoggedIn(_role);

    public string TitleText => Tr("PLC通信");
    public string MiLogText => Tr("MES 交互实时日志");
    public string MiDebugText => Tr("MES 接口调试界面");
    public string MiSettingsText => Tr("设置");
    public string MiPlcText => Tr("PLC设置");
    public string MiChangePwdText => Tr("修改密码");
    public string MiShortcutText => Tr("创建桌面快捷方式");
    public string MiLangText =>
        LanguageService.CurrentLanguage == "CH" ? "English" : "中文";

    public RelayCommand ShowLogCommand { get; }
    public RelayCommand ShowDebugCommand { get; }
    public RelayCommand ShowPlcCommand { get; }
    public RelayCommand ShowChangePwdCommand { get; }
    public RelayCommand ShowShortcutCommand { get; }
    public RelayCommand SwitchLanguageCommand { get; }
    public RelayCommand LoginCommand { get; }

    public void RefreshTexts()
    {
        ConnectText = Tr("连接设备");
        RefreshAll();
    }

    private static string Tr(string zh) => LanguageService.Tr(zh);

    private void RefreshGating()
    {
        Raise(nameof(LoginHeader));
        Raise(nameof(PlcVisible));
        Raise(nameof(PlcEnabled));
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

    private void ShowPlc()
    {
        if (!RequireLogin())
            return;
        if (!RunModeSettings.IsDev(_role))
        {
            _dialogs.ShowMessage(Tr("需要 dev 权限"), Tr("设置"),
                DialogImage.Warning);
            return;
        }
        if (_dialogs.ShowPlcConfig())
            RestartApp();
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

    private void RestartApp()
    {
        IsRestarting = true;
        _dialogs.RestartApp();
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
        _multi = new MultiMachineManager(id =>
            _useSim ? new SimulatedPlcClient()
                    : new ModbusStationClient());
        _multi.StationLog += (id, m) => LocalLog(m, id);
        _multi.Heartbeat += (id, on) => UiInvoke(() =>
            Stations[id].SetLive(on, false));
        _multi.StationBroken += id => UiInvoke(() =>
            Stations[id].SetLive(false, true));
        _multi.ScanElapsed += (id, ms) => UiInvoke(() =>
            ScanCycleText = $"{Tr("扫描周期: ")}{ms} ms");
        _multi.AlarmRaised += (m, n) => UiInvoke(() => _dialogs.ShowAlarm(m, n));
    }

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
    /// 关窗确认（View.OnClosing 调）：重启中跳过确认；用户手动关才确认。
    /// 返回 true 表示放行关闭。
    /// </summary>
    public bool OnClosing()
    {
        if (IsRestarting)
            return true;
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
