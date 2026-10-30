// =========================================================================
// MultiMachineGateway：多机通用 MES 网关（复刻自多机版 Form1.vb ScanPLC1 后半）
//
// 干什么：每台机台独立后台线程死循环（200ms）：查 6 个触发字→命中一个→
//   触发复 0→后台 Task 读 JSON→按接口校验→POST MES→回写 STATUS/MSG/DATA→
//   完成=1→触发复 0。一次扫描只处理一个触发（防多 API 并发冲突，老项目原注）。
//
// 协议细节（MES 侧约定，改前必读 W68 文档 + 老项目源码）：
// ① JSON 校验：0027/0028/0033 外层是数组（List），0030/0031/0032 是对象；
// ② 回写 STATUS/MSG/DATA 三个串地址，空值写 " "（空格占位，防通讯库报空串错）；
// ③ API0030 的 DATA 是数组 [{OUT_RESULT,OUT_REMARK,OUT_NEXTOP}]，拆三段写
//    DATA/+30/+60（偏移可配 [MES] DataSegmentOffsets，缺省 30/60）；
//    API0031 的 DATA 拆 BC_NO/+30 CHECK_RESULT/+60 REMARK；
// ④ 分段地址算法：(含R?"R":"D") + (数字部分+偏移)，无补零（老项目原样）；
// ⑤ writeFail：STATUS=2、MSG=NG、DATA=" "、完成=1、触发复 0；
// ⑥ 每次触发成功与否都记 MESLogger（分机台），STATUS=1 记成功否则弹报警。
//
// 补齐说明（相对老项目的残缺）：
// ① 老项目 HslCommunication 无工程引用、靠 bin 残留 DLL——新项目走子模块
//    libs 正式引用 + IStationClient 抽象，真机/模拟可换；
// ② Form2/Form6 是无人 new 的死窗体——新项目不复刻两份重复窗体，改为
//    单一主窗 + 机台视图筛选（全部/单机台），8 机面板是同一 UserControl；
// ③ D22xx 旧业务全注释——不复刻注释代码，只保留通用网关（当前实际行为）。
// =========================================================================

using Newtonsoft.Json;
using CureLine.Core.Comms;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;

namespace CureLine.Core.Modes;

public class StationGateway : IDisposable
{
    private readonly int _stationId;
    private readonly IStationClient _station;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>分机台操作日志（UI 追加到对应机台面板 + 写 log-{id}.txt 由 UI/此类？此处发事件，落盘在 WPF 层统一做，保持 Core 无 UI 依赖）。</summary>
    public event Action<int, string>? StationLog;
    /// <summary>心跳灯翻转（UI 切灯色）。</summary>
    public event Action<int, bool>? Heartbeat;
    /// <summary>0 号机扫描周期上报（UI 显示"扫描周期: x ms"，老项目仅 id=0）。</summary>
    public event Action<int, long>? ScanElapsed;
    /// <summary>MES 报警（UI 弹报警窗）。</summary>
    public event Action<string, string>? AlarmRaised;

    /// <param name="stationFactory">按机台号建 IStationClient（真机 Modbus，模拟直接 new）。</param>
    public StationGateway(int stationId, Func<int, IStationClient> stationFactory)
    {
        _stationId = stationId;
        _station = stationFactory(stationId);
    }

    /// <summary>
    /// 连接本机台（老项目 Button1_Click 单台分支；StationUse!=1 由 Manager 跳过）。
    /// IP/端口来自配置（网关配置窗可改，改后点"连接设备"重连生效）。
    /// </summary>
    public bool Connect(out string message)
    {
        _station.Disconnect();
        string ip = AppConfig.StationIps[_stationId];
        int port = AppConfig.StationPorts[_stationId];
        if (_station.Connect(ip, port))
        {
            message = $"{LanguageService.Tr("连接机台成功")},{ip}";
            StationLog?.Invoke(_stationId, message);
            return true;
        }
        message = $"{LanguageService.Tr("连接机台失败")},{ip}";
        StationLog?.Invoke(_stationId, message);
        return false;
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _station.Disconnect();
    }

    private void Loop()
    {
        bool lamp = false;
        while (_running)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                lamp = !lamp;
                Heartbeat?.Invoke(_stationId, lamp);
                PollTriggers();
            }
            catch
            {
                // 轮询单次异常不杀线程（老项目外层无 try，新项目加固：后台线程
                // 崩了整台机无声停扫，比弹框更糟，故吞单次异常保活）。
            }
            sw.Stop();
            if (_stationId == 0)
                ScanElapsed?.Invoke(_stationId, sw.ElapsedMilliseconds);
            // 周期读静态值（V0.0.22 配置化 [setting] ScanIntervalMs，缺省 200；
            // Math.Max 兜底：配置窗保存钳制 50~5000，此处再兜一层防 0 空转）。
            Thread.Sleep(Math.Max(AppConfig.ScanIntervalMs, 20));
        }
    }

    /// <summary>触发轮询（老项目 ScanPLC1 通用网关段原样，一次只处理一个）。</summary>
    internal void PollTriggers()
    {
        string activeApi = "";
        string activeTrg = "";
        string activeUrl = "";
        int activeIndex = -1;

        for (int i = 0; i < MesConfig.ApiCodes.Length; i++)
        {
            string trg = MesConfig.GetTrigger(MesConfig.ApiCodes[i]);
            if (string.IsNullOrEmpty(trg))
                continue;
            var (ok, v) = _station.ReadInt16(trg);
            if (ok && v == 1)
            {
                activeApi = MesConfig.ApiCodes[i];
                activeTrg = trg;
                activeUrl = MesConfig.GetUrl(activeApi);
                activeIndex = i;
                _station.WriteInt16(trg, 0); // 清触发
                break;
            }
        }

        if (activeIndex < 0)
            return;

        var addrs = MesConfig.ApiAddresses[activeIndex];
        // 后台处理，不堵扫描（老项目 Task.Run 原注）。
        Task.Run(() => HandleApi(activeApi, activeTrg, activeUrl, addrs));
    }

    internal void HandleApi(
        string apiCode, string trgAddr, string url, MesConfig.ApiAddressSet addrs)
    {
        MesLogger.WriteLog("Station Trigger",
            $"检测到 MES 触发, 机台 ID: {_stationId}, API: {apiCode}", _stationId);

        var (ok, raw) = _station.ReadString(addrs.StationData, (ushort)MesConfig.DataLength);
        string json = ok ? (raw ?? string.Empty).Replace("\0", string.Empty).Trim() : string.Empty;
        MesLogger.WriteLog("Read Data", $"API: {apiCode}, JSON: {json}", _stationId);

        bool parsed = ValidateJson(apiCode, json);
        if (!parsed)
        {
            WriteFail($"{LanguageService.Tr("MES反序列化失败")}: {apiCode}",
                trgAddr, addrs, "JSON 校验失败");
            return;
        }
        if (string.IsNullOrEmpty(url))
        {
            WriteFail($"URL 未配置: {apiCode}", trgAddr, addrs, "URL 未配置");
            return;
        }

        bool httpOk = MesHttpClient.PostJson(url, json, out string resp, _stationId);
        if (!httpOk)
        {
            WriteFail($"{LanguageService.Tr("MES网络异常/超时")}: {apiCode}",
                trgAddr, addrs, resp);
            return;
        }

        try
        {
            var respObj = JsonConvert.DeserializeObject<MesModels.BaseResponse>(resp);
            if (respObj == null)
            {
                WriteFail($"{LanguageService.Tr("MES返回数据解析为空")}: {apiCode}",
                    trgAddr, addrs, resp);
                return;
            }
            string status = string.IsNullOrEmpty(respObj.STATUS) ? " " : respObj.STATUS;
            string msg = string.IsNullOrEmpty(respObj.MSG) ? " " : respObj.MSG;
            string data = " ";
            if (respObj.DATA != null)
            {
                data = respObj.DATA is string s ? s : JsonConvert.SerializeObject(respObj.DATA);
                if (string.IsNullOrEmpty(data))
                    data = " ";
            }

            _station.WriteString(addrs.MesStatus, status, Math.Max(status.Length, 1));
            _station.WriteString(addrs.MesMsg, msg, Math.Max(msg.Length, 1));

            if (apiCode == "API0030" && !string.IsNullOrWhiteSpace(data))
            {
                var list = TryParseList<MesModels.DataResponse30>(data);
                if (list?.Count > 0)
                {
                    WriteDataSegment(addrs.MesData,
                        list[0].OUT_RESULT, list[0].OUT_REMARK, list[0].OUT_NEXTOP);
                }
                else
                {
                    _station.WriteString(addrs.MesData, data, Math.Max(data.Length, 1));
                }
            }
            else if (apiCode == "API0031" && !string.IsNullOrWhiteSpace(data))
            {
                var list = TryParseList<MesModels.DataResponse31>(data);
                if (list?.Count > 0)
                {
                    WriteDataSegment(addrs.MesData,
                        list[0].BC_NO, list[0].CHECK_RESULT, list[0].REMARK);
                }
                else
                {
                    _station.WriteString(addrs.MesData, data, Math.Max(data.Length, 1));
                }
            }
            else
            {
                _station.WriteString(addrs.MesData, data, Math.Max(data.Length, 1));
            }

            _station.WriteInt16(trgAddr, 0);
            _station.WriteInt16(addrs.MesComplete, 1);

            if (status == "1")
            {
                StationLog?.Invoke(_stationId,
                    $"{LanguageService.Tr("MES上传成功")}: {apiCode}");
            }
            else
            {
                StationLog?.Invoke(_stationId,
                    $"{LanguageService.Tr("MES返回NG")}: {apiCode}");
                AlarmRaised?.Invoke(resp, MesLogger.GenLogName(_stationId));
            }
        }
        catch (Exception ex)
        {
            WriteFail($"{LanguageService.Tr("MES返回数据解析异常")}: {ex.Message}",
                trgAddr, addrs, resp);
        }
    }

    /// <summary>按接口校验 JSON 可解析（老项目 Select Case 反序列化段原样）。</summary>
    internal static bool ValidateJson(string apiCode, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            object? obj = apiCode switch
            {
                "API0027" => JsonConvert.DeserializeObject<List<MesModels.Api0027Request>>(json),
                "API0028" => JsonConvert.DeserializeObject<List<MesModels.Api0028Request>>(json),
                "API0030" => JsonConvert.DeserializeObject<MesModels.Api0030Request>(json),
                "API0031" => JsonConvert.DeserializeObject<MesModels.Api0031Request>(json),
                "API0032" => JsonConvert.DeserializeObject<MesModels.Api0032Request>(json),
                "API0033" => JsonConvert.DeserializeObject<List<MesModels.Api0033Request>>(json),
                _ => null,
            };
            return obj != null;
        }
        catch
        {
            MesLogger.WriteLog("Parse Exception", $"解析异常", -1);
            return false;
        }
    }

    private static List<T>? TryParseList<T>(string data)
    {
        try { return JsonConvert.DeserializeObject<List<T>>(data); }
        catch { return null; }
    }

    /// <summary>
    /// DATA 三段写（DATA/+off1/+off2；偏移可配 [MES] DataSegmentOffsets，缺省
    /// 30/60=老项目口径，AppConfig.Load 保证恒有合法值）。分段地址算法老项目原样：
    /// 有R用R否则D，数字+偏移，无补零。空值写 " " 占位（防空串写机台报错，
    /// 老项目 IIf 原注）。
    /// </summary>
    internal void WriteDataSegment(string baseAddr, string? v0, string? v1, string? v2)
    {
        int off1 = MesConfig.DataSegmentOffsets.Length > 0 ? MesConfig.DataSegmentOffsets[0] : 30;
        int off2 = MesConfig.DataSegmentOffsets.Length > 1 ? MesConfig.DataSegmentOffsets[1] : 60;
        _station.WriteString(baseAddr, string.IsNullOrEmpty(v0) ? " " : v0,
            Math.Max(v0?.Length ?? 0, 1));
        _station.WriteString(ShiftAddr(baseAddr, off1), string.IsNullOrEmpty(v1) ? " " : v1,
            Math.Max(v1?.Length ?? 0, 1));
        _station.WriteString(ShiftAddr(baseAddr, off2), string.IsNullOrEmpty(v2) ? " " : v2,
            Math.Max(v2?.Length ?? 0, 1));
    }

    /// <summary>
    /// 分段地址算法（老项目原样：有R用R否则D，数字部分+偏移，无补零）。
    /// 加固两处（heritage 行为不变，合法输入结果与老项目逐字一致）：
    /// ① 大小写不敏感（"r17900" 也认 R）；② 数字部分解析失败直接返回原地址，
    /// 不抛（配错地址走 WriteFail 回写，不炸后台线程）。
    /// </summary>
    internal static string ShiftAddr(string addr, int offset)
    {
        string prefix = addr.Contains('R', StringComparison.OrdinalIgnoreCase) ? "R" : "D";
        string num = addr.Replace("R", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("D", string.Empty, StringComparison.OrdinalIgnoreCase);
        return int.TryParse(num, out int n) ? prefix + (n + offset) : addr;
    }

    /// <summary>异常回写（老项目 writeFail lambda 原样：STATUS=2/MSG=NG/DATA=" "/完成=1/触发复0）。</summary>
    internal void WriteFail(string logMsg, string trgAddr, MesConfig.ApiAddressSet addrs, string respEcho)
    {
        _station.WriteString(addrs.MesStatus, "2", 1);
        _station.WriteString(addrs.MesMsg, "NG", 2);
        _station.WriteString(addrs.MesData, " ", 1);
        _station.WriteInt16(addrs.MesComplete, 1);
        _station.WriteInt16(trgAddr, 0);
        MesLogger.WriteLog("Send FAIL", $"{logMsg} | {respEcho}", _stationId);
        StationLog?.Invoke(_stationId, logMsg);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _station.Dispose();
    }
}

/// <summary>8 机台管理器（老项目 Button1_Click 循环 + FormClosing 释放对应）。</summary>
public class MultiMachineManager : IDisposable
{
    private readonly List<StationGateway> _stations = new();
    private readonly Func<int, IStationClient> _factory;
    private bool _disposed;

    public IReadOnlyList<StationGateway> Stations => _stations;

    public event Action<int, string>? StationLog;
    public event Action<int, bool>? Heartbeat;
    public event Action<int, long>? ScanElapsed;
    public event Action<string, string>? AlarmRaised;
    /// <summary>某机台连接失败（UI 将其心跳灯置红，老项目 LabelPLClive=Red 语义）。</summary>
    public event Action<int>? StationBroken;

    public MultiMachineManager(Func<int, IStationClient> plcFactory)
    {
        _factory = plcFactory;
    }

    /// <summary>
    /// 连接全部启用的机台并起线程（老项目 Button1_Click：StationUse=1 才连，
    /// 否则记"已配置为不连接本工站"）。返回 (成功数, 跳过数)。
    /// 约定：Stations 只收"连上且线程已起"的网关；连失败的当场释放、不进列表
    /// （语义清晰 + 不占机台客户端到 Manager.Dispose）。
    /// </summary>
    public (int Ok, int Skipped) ConnectAll()
    {
        int ok = 0, skipped = 0;
        for (int i = 0; i < 8; i++)
        {
            if (AppConfig.StationUse[i] != "1")
            {
                StationLog?.Invoke(i, LanguageService.Tr("已配置为不连接本工站"));
                skipped++;
                continue;
            }
            var gw = new StationGateway(i, _factory);
            gw.StationLog += (id, m) => StationLog?.Invoke(id, m);
            gw.Heartbeat += (id, b) => Heartbeat?.Invoke(id, b);
            gw.ScanElapsed += (id, ms) => ScanElapsed?.Invoke(id, ms);
            gw.AlarmRaised += (m, n) => AlarmRaised?.Invoke(m, n);
            if (gw.Connect(out _))
            {
                gw.Start();
                _stations.Add(gw);
                ok++;
            }
            else
            {
                StationBroken?.Invoke(i);
                gw.Dispose();
            }
        }
        return (ok, skipped);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var s in _stations)
            s.Dispose();
        _stations.Clear();
    }
}
