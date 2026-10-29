// =========================================================================
// DocShot.cs —— PlcMesBridge 培训文档截图 harness（WPF・net8 版）
//
// 干什么：按"生产装配链"逐窗 new 两程序全部关键窗体，注入演示数据/演示
//   配置（R1：数据走真实生产路径——MockPLC/缺省自愈/登录记忆回填，绝不
//   P 图），逐窗截图到 docs/images/*.png，每张打非空校验指标（R3）日志。
// 为什么独立 harness：产品代码零改动；只改"种子数据与截图清单"即可重拍。
// 怎么跑：tools/DocShot/run_docshot.ps1（build 两程序 → build 本 harness →
//   组装 runner 依赖目录 → 起 exe；全程无人值守）。
// 命名约定：输出文件名前缀 cs_ = 程序①CureStation；gw_ = 程序②CimGateway。
//
// R1 种子（写进 seed 目录的 config，两程序各自独立）：
//   程序① [setting] Simulate=1 → MockPointTableBus 演示流转（真实状态机
//   驱动三色灯——拍"空闲 + 流转中"两态全真）；[CuringMes] Enabled=1 且 URL
//   空（门槛双保险 → 不发任何网络请求，顶栏显示"过账：已启用"，演示态不
//   惹麻烦）；登录窗用 Downstream RunModeSettings.SaveLogin 预填记忆
//   （真实"用户上次记住密码"语义，不是改窗口状态）。
//   程序② [setting] Simulate=1 → 8 机台模拟 PLC 全流程。
//
// R2 截图手段（WPF 按窗口类别）：
//   默认 PrintWindow(PW_RENDERFULLCONTENT)（含标题栏）；输出指标明显
//   异常（真空白判据）时回落 RenderTargetBitmap（纯 WPF 视觉树，必定
//   有内容），两版都存盘供人眼复核。
//
// R3 非空校验：内容区（跳标题栏 60px）每 7px 采 distinct/深色像素，
//   阈值 distinct>=6 && dark>=8 为参考口径（探针跑新项目时重校准——
//   本项目窗体密集，实测见 logs/DocShot.metrics.txt）。
//
// 线程模型：单 STA 线程顺序拍；消息泵用 PushFrame 保证 WPF 渲染完成
//   再截（与 skill"sleep 等首帧"同义，Pump 内部在 Dispatcher 处理）。
// 防卡死：所有弹窗走两程序专用的 SilentDialogs（IDialogService 全静默，
//   MessageBox 绝不出现），无需看门狗线程；窗只 Hide+Close，不依赖
//   真实"退出确认"链（fake Confirm 恒 true 放行关窗）。
// =========================================================================

using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using PlcMesBridge.Core.Infrastructure;

// ───────── 别名（两程序同类型名——全程带命名空间区分） ─────────
using CwMain = CureStation.MainWindow;
using CwLogin = CureStation.LoginWindow;
using CwWorkOrder = CureStation.WorkOrderWindow;
using CwPolicy = CureStation.CuringPolicyWindow;
using CwIoTest = CureStation.IoTestWindow;
using CwPointTable = CureStation.PointTableWindow;
using CwOperators = CureStation.OperatorWindow;
using CwMesPost = CureStation.MesPostConfigWindow;
using CureDialogs = CureStation.ViewModels.IDialogService;
using CureMainVm = CureStation.ViewModels.MainViewModel;
using CureLoginVm = CureStation.ViewModels.LoginViewModel;
using CureWorkOrderVm = CureStation.ViewModels.WorkOrderViewModel;
using CurePolicyVm = CureStation.ViewModels.CuringPolicyViewModel;
using CureIoTestVm = CureStation.ViewModels.IoTestViewModel;
using CurePointTableVm = CureStation.ViewModels.PointTableViewModel;
using CureOperatorVm = CureStation.ViewModels.OperatorViewModel;
using CureMesPostVm = CureStation.ViewModels.MesPostConfigViewModel;

using GwMain = CimGateway.MainWindow;
using GwLogin = CimGateway.LoginWindow;
using GwLog = CimGateway.MesLogWindow;
using GwDebug = CimGateway.MesDebugWindow;
using GwAlarm = CimGateway.MesAlarmWindow;
using GwChangePwd = CimGateway.ChangePasswordWindow;
using GwPlcConfig = CimGateway.PlcConfigWindow;
using GwDialogs = CimGateway.ViewModels.IDialogService;
using GwDialogImage = CimGateway.ViewModels.DialogImage;
using GwMainVm = CimGateway.ViewModels.MainWindowViewModel;

using PlcMesBridge.Core.Security;
using PlcMesBridge.Core.Curing;
using PlcMesBridge.Core.Comms;
using PlcMesBridge.Core.Mes;

namespace DocShot;

public static class Program
{
    // ---------- 输出与日志路径 ----------
    private static string _imgs = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "docs", "images");
    private static string _metrics = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "DocShot", "logs", "DocShot.metrics.txt");
    private static readonly List<string> _shots = new();
    private static Window? _ownerSource; // 子窗的 Owner 供 Prep 复刻
    private static int _failCount;

    // ───────────────────────── 主流程 ─────────────────────────
    [STAThread]
    public static void Main()
    {
        // WinExe 无控制台句柄——不设 Console.OutputEncoding（设了抛 IOException）。
        string exeDir = AppContext.BaseDirectory;
        _imgs = Path.GetFullPath(_imgs);
        _metrics = Path.GetFullPath(_metrics);
        Directory.CreateDirectory(Path.GetDirectoryName(_metrics)!);
        Directory.CreateDirectory(_imgs);

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        try
        {
            ShotCureStation(app, exeDir);
            ShotGateway(app, exeDir);
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            _failCount++;
        }

        if (_failCount > 0)
            Log($"DONE with {_failCount} shot failures — SEE metrics file");
        else
            Log("DONE: ALL PASS");

        // 铁律：不 app.Shutdown()——WPF 关窗链会触发主窗 OnClosing 的
        //   "是否退出？"MessageBox（程序②直弹不走 IDialogService，Fake 拦不住，
        //   2026-10 现场卡死实证）。R1 种子/截图全部完成于此前，直接退最干净。
        Environment.Exit(_failCount == 0 ? 0 : 1);
    }

    // ───────────────────── 程序① CureStation 截图 ─────────────────────
    private static void ShotCureStation(Application app, string exeDir)
    {
        // 资源装填：解析 CureStation App.xaml 的 <Application.Resources> 内嵌段
        // （FreshButton/FreshInput 等——harness 不走 Application.Run，用
        //   XamlReader 解析同款资源装进本进程，与运行时视觉一致）。
        try
        {
            // 编译进 harness 程序集的资源字典（run_docshot.ps1 自动从
            // CureStation/App.xaml 摘取生成）——与运行时视觉同源。
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/DocShot;component/Cure.App.Resources.xaml"),
            });
        }
        catch (Exception ex)
        {
            Log($"WARN 程序① 资源装填失败（截图视觉可能与运行时有差）：{ex.Message}");
        }

        // 种子目录（R1：走真实生产路径——自愈/记忆/模拟开关全在 seed 里，
        //   产品代码零改动，现场目录不被污染）。
        AppPaths.BaseDir = Path.Combine(exeDir, "seed", "CureStation");
        Directory.CreateDirectory(AppPaths.ConfigDir);
        Directory.CreateDirectory(AppPaths.BaseDir + "\\DataBase");
        WriteCureSeedIni(AppPaths.ConfigIni);
        // 登录记忆（真实生产语义：登录窗按 SavedUser 预填 + 按账号跟出记住密码）
        RunModeSettings.SaveLogin(AppPaths.ConfigIni, true, "admin", "123456");
        try
        {
            var store = new OperatorStore(
                new PlcMesBridge.Core.Infrastructure.SqliteHelper(AppPaths.DatabaseFile));
            store.EnsureSchema();
            store.EnsureSeeded("123456"); // 与产品初始一致，现场上线必改
        }
        catch (Exception ex) { Log($"WARN 操作员播种失败（权限窗可能空表）：{ex.Message}"); }

        // 演示数据（登录态记忆以上走真实链；寓意字段走工单种子对象——
        //   不写盘 workorder.json，避免污染读盘口径，符合 R1"演示数据"注记要求）
        var book = new WorkOrderBook
        {
            Right = new WorkOrderSlot { RcNo = "2405A2301", PresetQty = 120, ProducedQty = 37 },
            Left = new WorkOrderSlot { RcNo = "2405B1180", PresetQty = 100, ProducedQty = 12 },
            EmpNo = "E0055",
            BcNo = "BC00077",
            PtNo1 = "PT-001",
            PtNo2 = "PT-002",
            UploadEnabled = true,
        };

        var fake = new SilentCureDialogs();

        // ---------- 主窗（Mock 总线直启 → 状态机演示流转） ----------
        var main = new CwMain
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60, Top = 60,
            DataContext = new CureMainVm(fake),
        };
        main.Show();
        _ownerSource = main;
        Pump(1200);
        ShotFrame(main, "cs-main-idle.png");   // 空闲态（30 卡首屏，不关窗——主窗两态都走 ShotFrame）
        // Mock 推进（每约 250ms 一拍，rack 每 40 拍推进一阶——
        //   12s 后多仓位处于黄灯计时/绿灯放行各阶段，画面有"活"的流转。
        Pump(12000);
        ShotFrame(main, "cs-main-flow.png");   // 流转中（黄灯/绿灯/托盘混合）

        // ---------- 登录窗（登录面板 + 改密面板） ----------
        var lv = new CureLoginVm();
        lv.Password = "123456";                // R1 例外允许的"模拟打字"
        var lw = new CwLogin { DataContext = lv };
        ShotFrame(lw, "cs-login.png", 800);    // 两态连拍（Shot 会关窗，改密面板复用同窗）
        lv.ChangePwdCommand.Execute(null);
        Pump(250);
        ShotFrame(lw, "cs-login-pwd.png");
        lw.Hide(); lw.Close();

        // ---------- 工单窗（L1 只读态 + L3 全可编辑态） ----------
        var wo1 = new CwWorkOrder
        {
            DataContext = new CureWorkOrderVm(fake, OperatorLevel.Level1, book),
        };
        Shot(wo1, "cs-workorder-l1.png");
        var wo3 = new CwWorkOrder
        {
            DataContext = new CureWorkOrderVm(fake, OperatorLevel.Level3, book),
        };
        Shot(wo3, "cs-workorder-l3.png");

        // ---------- 点表配置窗 ----------
        var ptw = new CwPointTable { DataContext = new CurePointTableVm(fake) };
        Shot(ptw, "cs-pointtable.png", 900);

        // ---------- 工艺策略窗（可视化画布：点表口径） ----------
        var cfg = IoPointTableStore.Load() ?? IoPointTableStore.BuildDefaultTemplate(
            "A线OP30固化架IO", "192.168.1.20", 10, 3);
        int racks = Math.Max(SlotLocationCode.RacksPerRow,
            cfg.Devices.SelectMany(d => d.Bindings).Where(b => b.Rack > 0)
                .Select(b => b.Rack).DefaultIfEmpty(SlotLocationCode.RacksPerRow).Max());
        int levels = Math.Max(SlotLocationCode.Levels,
            cfg.Devices.SelectMany(d => d.Bindings).Where(b => b.Level > 0)
                .Select(b => b.Level).DefaultIfEmpty(SlotLocationCode.Levels).Max());
        var pol = new CwPolicy
        {
            DataContext = new CurePolicyVm(fake, racks, levels),
        };
        Shot(pol, "cs-policy.png", 1000);

        // ---------- IO 通讯测试窗（独立演示总线：确定性推进 DI 快照） ----------
        var testBus = new MockPointTableBus();
        testBus.Start(cfg);
        Pump(2500); // 等 Mock 推进两拍（DI 有读数）
        var iot = new CwIoTest { DataContext = new CureIoTestVm(testBus, cfg) };
        Shot(iot, "cs-iotest.png", 900);
        testBus.Dispose();

        // ---------- 权限管理窗（L4） ----------
        var opw = new CwOperators { DataContext = new CureOperatorVm(fake) };
        Shot(opw, "cs-operators.png", 900);

        // ---------- 过账配置窗（L4） ----------
        var mp = new CwMesPost { DataContext = new CureMesPostVm(fake) };
        Shot(mp, "cs-mespost.png", 900);

        // ---------- 主窗流转后期（更多仓位走过一轮；避开 Mock 演示掉线窗口） ----------
        Pump(6000);
        ShotFrame(main, "cs-main-flow2.png");

        // 主窗不 Close（关窗链有确认框风险）；进程收尾统一 Exit。
        main.Hide();
        try { (main.DataContext as CureMainVm)?.Dispose(); } catch { }
        _ownerSource = null;
    }

    // ───────────────────── 程序② CimGateway 截图 ─────────────────────
    private static void ShotGateway(Application app, string exeDir)
    {
        // 资源换装（FreshBlue 是 CimGateway 程序集编译的样式包 + 全局转换器）
        try
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/CimGateway;component/Styles/FreshBlue.xaml"),
            });
            app.Resources["BoolToVisibilityConverter"] = new System.Windows.Controls.BooleanToVisibilityConverter();
        }
        catch (Exception ex)
        {
            Log($"WARN 程序② 资源装填失败：{ex.Message}");
        }

        // 种子目录（独立于程序①——两程序分机部署的目录隔离习惯延续）
        AppPaths.BaseDir = Path.Combine(exeDir, "seed", "CimGateway");
        Directory.CreateDirectory(AppPaths.ConfigDir);
        Directory.CreateDirectory(AppPaths.BaseDir + "\\DataBase");
        WriteGatewaySeedIni(AppPaths.ConfigIni);
        UserAccountStore.Default.EnsureSeeded("123456", "123456");
        RunModeSettings.SaveLogin(AppPaths.ConfigIni, true, "admin", "123456");

        // 调试窗参数记忆（真实生产链：mes_params.json 全部静态参数）
        MesParamStore.RC_NO = "2405A2301";
        MesParamStore.PT_NO = "OP30";
        MesParamStore.MAC_NO = "CIM01";
        MesParamStore.MAC_LOC_NO = "L02";
        MesParamStore.PLANT = "TEST";
        MesParamStore.PRA_CODE = "PC01";
        MesParamStore.PRA_Value = "120";
        MesParamStore.Collect_Datetime = "2026-10-29 10:12:00";
        MesParamStore.ACTION = "I";
        MesParamStore.BC_NO = "BC00077";
        MesParamStore.SP_MEMO = "";
        MesParamStore.EMP_NO = "E0055";
        MesParamStore.TL_EL_NO = "";
        MesParamStore.IN_DATETIME = "2026-10-29 10:12:00";
        MesParamStore.CHECK_RESULT = "0";
        MesParamStore.BO_NUMBER = "BO00012";
        MesParamStore.SaveParams();

        var fake = new SilentGatewayDialogs();

        // ---------- 主窗（未连接 → 连接设备 → 8 机台跑起来） ----------
        var main = new GwMain
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 60, Top = 60,
            DataContext = new GwMainVm(fake),
        };
        main.Show();
        _ownerSource = main;
        Pump(1000);
        ShotFrame(main, "gw-main-idle.png");      // 待机态（8 空面板 + 时钟）

        // 连接设备：Simulate=1 → 8 机台模拟 PLC 全流程（真实生产链）
        ((GwMainVm)main.DataContext).ConnectMultiCommand.Execute(null);
        Pump(8000);                                // 网关拉起 + 日志滚动一波
        ShotFrame(main, "gw-main.png");

        // ---------- 登录窗 ----------
        var login = new GwLogin();                 // VM 由窗内装配（生产同链）
        Shot(login, "gw-login.png", 800);

        // ---------- MES 交互实时日志窗（-1 全部机台） ----------
        var log1 = new GwLog(-1);
        Shot(log1, "gw-log-all.png", 900);

        // ---------- MES 接口调试窗（API0027 表单 + JSON 预览） ----------
        var dbg = new GwDebug();
        Shot(dbg, "gw-debug.png", 900);

        // ---------- 报警窗（演示报文） ----------
        var alarm = new GwAlarm(
            "上传失败：API0028 返回非 1（STATUS=2，MSG=设备号不存在）\r\n" +
            "{\"RC_NO\":\"2405A2301\",\"PT_NO\":\"OP30\",\"MAC_NO\":\"CIM01\"}",
            "点胶固化收料机");
        Shot(alarm, "gw-alarm.png", 800);

        // ---------- 修改密码窗 ----------
        var cpw = new GwChangePwd("admin", LoginRole.Admin);
        Shot(cpw, "gw-changepwd.png", 800);

        // ---------- 网关设置窗（dev 专属——仅内部版，客户两版零出现） ----------
        var cfgW = new GwPlcConfig();
        Shot(cfgW, "gw-plcconfig.png", 1000);

        // 主窗不 Close（OnClosing 有"是否退出？"直弹 MessageBox，见 Main 收尾注释）
        main.Hide();
        try { (main.DataContext as GwMainVm)?.Dispose(); } catch { }
        _ownerSource = null;
    }

    // ═══════════════════ 通用工具区 ═══════════════════

    private static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(_metrics, line + Environment.NewLine, new UTF8Encoding(false)); }
        catch { }
    }

    /// <summary>
    /// 消息泵：等待 ms 毫秒且持续处理 WPF 消息（STA 主线程不能 Sleep 干等，
    /// 否则渲染/布局停摆——skill"sleep 等首帧"的 WPF 版）。
    /// </summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { Thread.Sleep(Math.Max(0, ms)); }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>子窗截图生命周期：Show → Pump → 截 → Close（无模态）。</summary>
    private static void Shot(Window w, string file, int pumpMs = 700)
    {
        ShotFrame(w, file, pumpMs);
        w.Hide();
        try { w.Close(); } catch { }
    }

    /// <summary>只截不关（主窗等复用窗口用）。</summary>
    private static void ShotFrame(Window w, string file, int pumpMs = 700)
    {
        // Prep 复刻：子窗挂主窗 Owner（生产 DialogService 同款语义）
        if (_ownerSource != null && !ReferenceEquals(w, _ownerSource))
        {
            try { if (w.Owner != _ownerSource) w.Owner = _ownerSource; } catch { }
        }
        // 统一截图尺寸（XAML 里 Maximized 的主窗在屏幕上是全屏；
        //   截图按设计稿 1280×760 / 1230×640 的理解态处理）。
        try { w.WindowState = WindowState.Normal; } catch { }
        if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("file 必填");
        try
        {
            w.Show();
        }
        catch (Exception ex)
        {
            Log($"FAIL {file} Show 异常：{ex.Message}");
            _failCount++;
            return;
        }
        Pump(pumpMs);
        Capture(w, file);
    }

    // ---------- 截图实现（R2：PrintWindow 含标题栏 + RTB 兜底） ----------

    private static void Capture(Window w, string file)
    {
        string path = Path.Combine(_imgs, file);
        BitmapSource? print = null;
        (int distinct, int dark) mPrint = (0, 0);
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(w);
            var hwnd = helper.EnsureHandle();
            print = ShotPrintWindow(hwnd);
            if (print != null)
            {
                mPrint = Metrics(print, out var samples);
                SavePng(print, path);
            }
        }
        catch (Exception ex) { Log($"WARN PrintWindow {file}：{ex.Message}"); }

        // RTB 兜底（WPF 视觉树必渲染；print 指标差时另存 .rtb.png 供人眼挑）
        BitmapSource? rtb = null;
        (int distinct, int dark) mRtb = (0, 0);
        try
        {
            rtb = ShotRtb(w);
            mRtb = Metrics(rtb, out _);
            if (mPrint != (0, 0) && !Healthy(mPrint) && Healthy(mRtb))
                SavePng(rtb, Path.ChangeExtension(path, ".rtb.png").Replace(".png.p", ".png"));
        }
        catch (Exception ex) { Log($"WARN RTB {file}：{ex.Message}"); }

        bool ok = (print != null && Healthy(mPrint));
        if (!ok) { _failCount++; }
        Log($"SHOOT {file} print={(print != null ? "1" : "0")} " +
            $"p(Func{mPrint.distinct}/{mPrint.dark}) rtb(Func{mRtb.distinct}/{mRtb.dark}) " +
            $"{(ok ? "PASS" : "WARN(plc pass to human)")}");
    }

    private static bool Healthy((int distinct, int dark) m) => m.distinct >= 6 && m.dark >= 8;

    private static void SavePng(BitmapSource src, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>PrintWindow 含标题栏版（PW_RENDERFULLCONTENT=2，DWM 复合窗可用）。</summary>
    private static BitmapSource? ShotPrintWindow(IntPtr hwnd)
    {
        if (wndRect(hwnd, out var rect) == 0) return null;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return null;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(hdcScreen);
        var bi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1,
                biBitCount = 32, biCompression = 0,
            },
        };
        IntPtr dib = CreateDIBSection(hdcScreen, ref bi, 0, out _, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return null;
            var old = SelectObject(mem, dib);
            bool ok = PrintWindow(hwnd, mem, 2);
            SelectObject(mem, old);
            if (!ok) return null;
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                dib, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>RenderTargetBitmap 纯 WPF 视觉树版（不含 OS 标题栏）。</summary>
    private static BitmapSource ShotRtb(Window w)
    {
        int px = Math.Max(1, (int)Math.Ceiling(w.ActualWidth));
        int py = Math.Max(1, (int)Math.Ceiling(w.ActualHeight));
        var rtb = new RenderTargetBitmap(px, py, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        return rtb;
    }

    /// <summary>R3 非空校验：内容区（跳标题区）每 7px 采 distinct/深色像素数。</summary>
    private static (int distinct, int dark) Metrics(BitmapSource bmp, out long samples)
    {
        samples = 0;
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        int skipTop = Math.Min(60, h / 10);
        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[Math.Max(16, w * h * 4)];
        converted.CopyPixels(pixels, Math.Max(1, w * 4), 0);
        var colors = new HashSet<int>();
        int dark = 0;
        for (int y = skipTop; y < h; y += 7)
        {
            for (int x = 0; x < w; x += 7)
            {
                int idx = (y * w + x) * 4;
                byte b = pixels[idx], g = pixels[idx + 1], r = pixels[idx + 2];
                samples++;
                colors.Add(((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4));
                if (r + g + b < 240) dark++;
            }
        }
        return (colors.Count, dark);
    }

    // ---------- 种子 ini（模拟开关只写 [setting]） ----------

    private static void WriteCureSeedIni(string path)
    {
        var lines = new StringBuilder();
        lines.AppendLine("[setting]");
        lines.AppendLine("Language=CH");
        lines.AppendLine("Simulate=1");               // Mock 总线演示流转
        lines.AppendLine("[MES]");
        lines.AppendLine("Url_API0027=");            // URL 空 → 门槛双保险不发请求
        lines.AppendLine("Url_API0030=");
        lines.AppendLine("Url_API0032=");
        lines.AppendLine("[CuringMes]");
        lines.AppendLine("Enabled=1");               // 顶栏显示"过账：已启用"
        lines.AppendLine("Side=R");
        lines.AppendLine("TimeUnit=Seconds");
        lines.AppendLine("PostInAt=Arrive");
        lines.AppendLine("PostAbortOut=1");
        lines.AppendLine("RackSides=");
        lines.AppendLine("MacNo=OP30");
        lines.AppendLine("MacLocNo=BD01");
        lines.AppendLine("Plant=TEST");
        lines.AppendLine("PraCode=P01");
        lines.AppendLine("CheckResult=0");
        File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
        Log($"SEED cure ini -> {path}");
    }

    private static void WriteGatewaySeedIni(string path)
    {
        var lines = new StringBuilder();
        lines.AppendLine("[setting]");
        lines.AppendLine("Language=CH");
        lines.AppendLine("Simulate=1");              // 8 机台模拟 PLC 全流程
        lines.AppendLine("[MES]");
        foreach (var k in new[] { "0027", "0028", "0030", "0031", "0032", "0033" })
            lines.AppendLine($"Url_API{k}=");
        File.WriteAllText(path, lines.ToString(), new UTF8Encoding(false));
        Log($"SEED gateway ini -> {path}");
    }

    // ---------- 静默对话框（防强调框卡截图的绝对护栏） ----------

    private sealed class SilentCureDialogs : CureDialogs
    {
        public void ShowMessage(string message, string title) { }
        public bool Confirm(string message, string title) => true;   // 放行关窗链
        public bool ShowPointTable() => false;
        public bool ShowCuringPolicy() => false;
        public (string User, OperatorLevel Level)? ShowLogin() => null;
        public void ShowOperators() { }
        public bool ShowWorkOrder(OperatorLevel? currentLevel) => false;
        public bool ShowMesPostConfig() => false;
        public void ShowIoTest(IPointTableBus bus, PointTableConfig config) { }
        public void Shutdown() { }                                    // harness 不退出
    }

    private sealed class SilentGatewayDialogs : GwDialogs
    {
        public void ShowMessage(string message, string title, GwDialogImage image = GwDialogImage.Info) { }
        public bool Confirm(string message, string title) => true;
        public bool? ConfirmOrCancel(string message, string title) => true;
        public (bool Ok, LoginRole Role, string User) ShowLogin() => (false, LoginRole.None, "");
        public void ShowChangePassword(string currentUser, LoginRole role) { }
        public bool ShowPlcConfig() => false;
        public void ShowAlarm(string message, string name) { }
        public void ShowImage(string imagePath) { }
        public void ShowMesLog(int station) { }
        public void ShowMesDebug() { }
        public void RestartApp() { }
        public void CreateDesktopShortcut() { }
    }

    // ---------- Win32（PrintWindow 截图三件套） ----------

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    private static int wndRect(IntPtr hwnd, out RECT r) { var ok = GetWindowRect(hwnd, out r); return ok ? 1 : 0; }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags); // flags=2=FULLCONTENT
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc,
        ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public short biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
