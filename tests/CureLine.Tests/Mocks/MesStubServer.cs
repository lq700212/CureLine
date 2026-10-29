// =========================================================================
// MesStubServer：本地桩 MES（HttpListener 版，可复用测试基建）
//
// 干什么：替掉真实 MES（192.168.120.129:5012），让"POST→回写"全链路可测。
// 用法：
//   using var mes = new MesStubServer();            // 随机端口起监听
//   mes.EnqueueOk();                                // 预置一次成功应答
//   MesConfig.UrlApi0030 = mes.Url;                 // 指过去（用完记得恢复，见 TestScope）
//   ... 触发业务 ...
//   mes.WaitForRequests(1);                         // 等请求到达（带超时断言）
//   Assert.Contains("B1", mes.Bodies[0]);           // 断言发了什么
//
// 为什么不用 WebApplication：HttpListener 零依赖、单文件、与老测试
// （MultiGatewayTests.ServeMes）同一技术，用法照抄即可。
// 注意：Enqueue 的应答按"先入先出"消费；队列空了默认回 STATUS=1 成功。
// =========================================================================

using System.Net;
using System.Text;

namespace CureLine.Tests.Mocks;

public sealed class MesStubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Queue<(int Status, string Body)> _queue = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>收到的请求体（按到达顺序）。</summary>
    public readonly List<string> Bodies = new();

    /// <summary>收到的请求 URL（与 Bodies 一一对应）。</summary>
    public readonly List<string> Urls = new();

    /// <summary>桩地址（末尾带 /，可直接赋给 MesConfig.UrlApi00xx）。</summary>
    public string Url { get; }

    public MesStubServer()
    {
        int port = 18100 + Random.Shared.Next(0, 800);
        Url = $"http://127.0.0.1:{port}/mes/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        Task.Run(Serve);
    }

    /// <summary>预置一次应答（HTTP 状态码 + JSON 体）。</summary>
    public void Enqueue(int httpStatus, string body)
    {
        lock (_lock)
            _queue.Enqueue((httpStatus, body));
    }

    /// <summary>预置一次业务成功（STATUS=1）。</summary>
    public void EnqueueOk(string msg = "ok", string? dataJson = null) =>
        Enqueue(200, "{\"STATUS\":\"1\",\"MSG\":\"" + msg + "\",\"DATA\":" +
            (dataJson ?? "null") + "}");

    /// <summary>预置一次业务拒绝（STATUS=0，网关回写 STATUS=0 + 弹报警）。</summary>
    public void EnqueueReject(string msg = "no such barcode") =>
        Enqueue(200, "{\"STATUS\":\"0\",\"MSG\":\"" + msg + "\",\"DATA\":null}");

    /// <summary>预置一次 HTTP 层失败（500，MesHttpClient 返回 false，走 STATUS=2）。</summary>
    public void EnqueueHttpError(string body = "server error") =>
        Enqueue(500, body);

    /// <summary>等到收到 n 个请求（超时抛，超时信息带已收到的数量方便排障）。</summary>
    public void WaitForRequests(int n, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_lock)
            {
                if (Bodies.Count >= n)
                    return;
            }
            Thread.Sleep(50);
        }
        int got;
        lock (_lock)
            got = Bodies.Count;
        throw new Xunit.Sdk.XunitException(
            $"桩 MES 超时：期望 {n} 个请求，实际收到 {got} 个（{timeoutMs}ms）。");
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            try
            {
                string body;
                using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await sr.ReadToEndAsync();
                (int status, string resp) next;
                lock (_lock)
                {
                    Bodies.Add(body);
                    Urls.Add(ctx.Request.Url?.ToString() ?? "");
                    next = _queue.Count > 0
                        ? _queue.Dequeue()
                        : (200, "{\"STATUS\":\"1\",\"MSG\":\"ok\",\"DATA\":null}");
                }
                byte[] b = Encoding.UTF8.GetBytes(next.resp);
                ctx.Response.StatusCode = next.status;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(b, 0, b.Length);
                ctx.Response.Close();
            }
            catch
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
