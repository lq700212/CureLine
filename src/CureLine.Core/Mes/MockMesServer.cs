// =========================================================================
// MockMesServer：内置模拟 MES（HttpListener 版，进程内单例，仅 Simulate=1 用）
//
// 干什么：本地起一个迷你 HTTP 服务，按 6 个接口路径回协议口径正确的应答
//   （STATUS=1/MSG=OK，0030/0031 带可拆段 DATA）——让"触发→组包→POST→回写→
//   观测事件"这条成功链路在无真实 MES 的演示环境（Simulate=1）下能走通。
//   另有一条 NG 规则：报文里 RC_NO="NG" 时回 STATUS=2/MSG=NG（演示 MES 拒绝
//   → 报警窗链路用）。
//
// 什么时候用（VM 层保证，本类不判断 Simulate）：
//   Simulate=1 且某接口 URL 为空或指向样例占位地址（192.168.120.129，见
//   AppConfig.DefaultUrls / config.sample.ini——本来就是不可达的占位值）时，
//   把该接口 URL 指到本桩。用户自配的真实/测试 URL 一律透传不动。
//
// 红线：
// ① 仅演示用——真机模式（Simulate=0）绝不调用，生产报文口径零改动；
// ② 不写 config.ini——桩地址只填内存静态值（MesConfig），现场配置文件不被污染；
// ③ 端口 5012~5021 逐个试（5012 与样例 MES 端口对齐，界面端点显示不突兀），
//    全占用/无权限（HttpListener 需 URLACL）时 EnsureStarted 返回 null，调用方
//    退回"URL 未配置"失败链（V0.0.51 原行为，不劣化）；
// ④ 应答只求协议合法（能过 BaseResponse 反序列化 + 0030/0031 拆段解析），
//    业务字段（条码/工单号）是演示值，不作对账依据。
// =========================================================================

using System.Net;
using System.Text;

namespace CureLine.Core.Mes;

public static class MockMesServer
{
    /// <summary>样例占位主机（config.sample.ini / AppConfig.DefaultUrls 的缺省值，
    /// 本就不可达——Simulate=1 下视同"未配置"由桩接管）。</summary>
    public const string PlaceholderHost = "192.168.120.129";

    private static readonly object _lock = new();
    private static HttpListener? _listener;
    private static Thread? _thread;
    private static volatile bool _running;

    /// <summary>桩基址（"http://127.0.0.1:端口/"，末尾带 /）；未启动 = null。</summary>
    public static string? BaseUrl { get; private set; }

    /// <summary>
    /// 启动桩（幂等：已启动直接返回现址）。端口 5012 起逐个试到 5021，
    /// 全失败回 null（调用方退回失败链）。监听循环在专用后台线程。
    /// </summary>
    public static string? EnsureStarted()
    {
        lock (_lock)
        {
            if (_running)
                return BaseUrl;
            for (int port = 5012; port <= 5021; port++)
            {
                var listener = new HttpListener();
                string url = $"http://127.0.0.1:{port}/";
                listener.Prefixes.Add(url);
                try
                {
                    listener.Start();
                }
                catch
                {
                    // 端口被占/无 URLACL 权限 → 换下一个端口，全试完回 null
                    continue;
                }
                _listener = listener;
                BaseUrl = url;
                _running = true;
                _thread = new Thread(ServeLoop) { IsBackground = true, Name = "MockMesServer" };
                _thread.Start();
                return BaseUrl;
            }
            return null;
        }
    }

    /// <summary>把 6 接口中"空 URL / 样例占位地址"的接口指到内置桩（幂等，可反复调）。
    /// 用户自配的非占位 URL 一律透传不动（保留"模拟机台 + 自备桩 MES"联调玩法）。</summary>
    public static void FillPlaceholderUrls()
    {
        string? baseUrl = BaseUrl;
        if (baseUrl == null)
            return;
        foreach (string code in MesConfig.ApiCodes)
        {
            string url = MesConfig.GetUrl(code);
            bool placeholder = url.Length == 0 || url.Contains(PlaceholderHost);
            if (!placeholder)
                continue;
            switch (code)
            {
                case "API0027": MesConfig.UrlApi0027 = baseUrl + code; break;
                case "API0028": MesConfig.UrlApi0028 = baseUrl + code; break;
                case "API0030": MesConfig.UrlApi0030 = baseUrl + code; break;
                case "API0031": MesConfig.UrlApi0031 = baseUrl + code; break;
                case "API0032": MesConfig.UrlApi0032 = baseUrl + code; break;
                case "API0033": MesConfig.UrlApi0033 = baseUrl + code; break;
            }
        }
    }

    /// <summary>停桩（App 退出/测试收尾用；进程退出本就随后台线程消亡，此为显式收口）。</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* 已停/未起都无所谓 */ }
            _listener = null;
            _thread = null;
            BaseUrl = null;
        }
    }

    /// <summary>监听循环：逐请求串行处理（演示流量 = 手点 + 7s 一发，串行绰绰有余）；
    /// 单请求异常吞掉不杀循环（桩死了演示链路全断，保活比快死要紧）。</summary>
    private static void ServeLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = _listener!.GetContext();
            }
            catch
            {
                // Stop() 时 GetContext 抛异常属正常退出路径
                return;
            }
            try
            {
                Handle(ctx);
            }
            catch
            {
                // 单请求异常（客户端断开等）不影响后续请求
            }
        }
    }

    /// <summary>单请求处理：按路径尾段认接口，按 NG 规则/接口语义回协议合法 JSON。</summary>
    private static void Handle(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        string body = reader.ReadToEnd() ?? string.Empty;

        // 接口码 = 路径尾段（填 URL 时就是 .../API0030 的形态）；认不出按通用成功回
        string apiCode = ctx.Request.Url?.AbsolutePath?.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        apiCode = apiCode.ToUpperInvariant();

        // NG 规则：RC_NO="NG" → STATUS=2/MSG=NG（演示"MES 拒绝→回写 NG→报警窗"链路）。
        // 去空格匹配，兼容两种 JSON 空格风格。
        bool ng = body.Replace(" ", "").Replace("\"", "'").Contains("'RC_NO':'NG'");
        string resp = ng
            ? "{\"STATUS\":\"2\",\"MSG\":\"NG\",\"DATA\":null}"
            : OkResponse(apiCode);

        byte[] buf = Encoding.UTF8.GetBytes(resp);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>按接口回成功应答：STATUS=1/MSG=OK；0030/0031 带 DATA（数组形态，
    /// 正好让网关走"DATA 三段拆写"分支——拆段逻辑也被演示到）；其余接口 DATA=null
    /// （网关回写空格占位，协议合法）。</summary>
    private static string OkResponse(string apiCode) => apiCode switch
    {
        "API0030" => "{\"STATUS\":\"1\",\"MSG\":\"OK\",\"DATA\":[" +
            "{\"OUT_RESULT\":\"OK\",\"OUT_REMARK\":\"MOCK\",\"OUT_NEXTOP\":\"OP10\"}]}",
        "API0031" => "{\"STATUS\":\"1\",\"MSG\":\"OK\",\"DATA\":[" +
            "{\"BC_NO\":\"MOCK0001\",\"CHECK_RESULT\":\"OK\",\"REMARK\":\"MOCK\"}]}",
        _ => "{\"STATUS\":\"1\",\"MSG\":\"OK\",\"DATA\":null}",
    };
}
