// =========================================================================
// MockMesServer 测试：内置模拟 MES（V0.0.52，Simulate=1 演示链路的应答端）。
//
// 覆盖：幂等启动同址 / 六接口成功应答协议合法（0030/0031 带 DATA）/ NG 规则
//   （RC_NO=NG → STATUS=2）/ 占位 URL 补桩规则（空与样例地址替换，自配透传）。
//   真实网关消化桩应答的全链路在 ViewModelTests 的模拟模式用例里串。
// =========================================================================

using System.Net.Http;
using System.Text;
using CureLine.Core.Infrastructure;
using CureLine.Core.Mes;
using CureLine.Tests.Mocks;

namespace CureLine.Tests;

public class MockMesServerTests : IDisposable
{
    private readonly TestScope _scope = new();
    private readonly HttpClient _http = new();

    public MockMesServerTests()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
    }

    public void Dispose() => _scope.Dispose();

    [Fact(DisplayName = "内置桩幂等启动同址且绑本地环回")]
    public void EnsureStarted_Idempotent_LoopbackOnly()
    {
        string? first = MockMesServer.EnsureStarted();
        string? second = MockMesServer.EnsureStarted();
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.StartsWith("http://127.0.0.1:", first);
    }

    [Fact(DisplayName = "内置桩六接口成功应答协议合法")]
    public async Task OkResponses_AllApis_ProtocolValid()
    {
        string baseUrl = MockMesServer.EnsureStarted()!;
        foreach (string code in MesConfig.ApiCodes)
        {
            string body = (code == "API0027" || code == "API0028" || code == "API0033")
                ? "[{\"RC_NO\":\"DEMO\"}]"
                : "{\"RC_NO\":\"DEMO\"}";
            var resp = await _http.PostAsync(baseUrl + code,
                new StringContent(body, Encoding.UTF8, "application/json"));
            string text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("\"STATUS\":\"1\"", text);
            Assert.Contains("\"MSG\":\"OK\"", text);
            if (code == "API0030")
                Assert.Contains("OUT_RESULT", text);   // 数组 DATA → 网关走三段拆写
            if (code == "API0031")
                Assert.Contains("BC_NO", text);
        }
    }

    [Fact(DisplayName = "内置桩NG规则RC_NO=NG回STATUS2")]
    public async Task NgRule_RcNoNg_ReturnsStatus2()
    {
        string baseUrl = MockMesServer.EnsureStarted()!;
        // 两种空格风格都要命中
        foreach (string body in new[] { "{\"RC_NO\":\"NG\"}", "{ \"RC_NO\": \"NG\" }" })
        {
            var resp = await _http.PostAsync(baseUrl + "API0030",
                new StringContent(body, Encoding.UTF8, "application/json"));
            string text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("\"STATUS\":\"2\"", text);
            Assert.Contains("\"MSG\":\"NG\"", text);
        }
        // RC_NO 非 NG（如 NGDEMO）不误伤
        var ok = await _http.PostAsync(baseUrl + "API0030",
            new StringContent("{\"RC_NO\":\"NGDEMO\"}", Encoding.UTF8, "application/json"));
        Assert.Contains("\"STATUS\":\"1\"", await ok.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "占位URL补桩：空与样例地址替换自配透传")]
    public void FillPlaceholderUrls_OnlyEmptyAndSampleReplaced()
    {
        string baseUrl = MockMesServer.EnsureStarted()!;
        MesConfig.UrlApi0027 = "";                                   // 空 → 补
        MesConfig.UrlApi0028 = $"http://{MockMesServer.PlaceholderHost}:5012/x"; // 样例占位 → 补
        MesConfig.UrlApi0030 = "http://10.1.2.3:9000/myapi";         // 自配 → 透传不动
        MockMesServer.FillPlaceholderUrls();
        Assert.Equal(baseUrl + "API0027", MesConfig.UrlApi0027);
        Assert.Equal(baseUrl + "API0028", MesConfig.UrlApi0028);
        Assert.Equal("http://10.1.2.3:9000/myapi", MesConfig.UrlApi0030);
    }
}
