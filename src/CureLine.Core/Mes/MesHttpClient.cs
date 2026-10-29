// =========================================================================
// MesHttpClient：MES 网络请求（复刻自 MES_Core.vb 的 MESHttpHelper.PostJson）
//
// 行为 1:1（调用方无感替换，老项目 HttpWebRequest → 新项目 HttpClient）：
// ① 先记 "Http Request" 日志（URL + BODY）；② POST application/json UTF8；
// ③ 成功记 "Http Response" 返回 true；④ 失败把"服务端返回的错误流/异常信息"
//    交给调用方并记 "Http Error"，返回 false（老项目专取 WebException.Response
//    流的语义：MES 报错时 HTTP 状态码非 200，但 body 里有 STATUS/MSG 可用）。
// 自签证书放行：老项目全局回调始终返回 true，原样保留（内网 MES 常见自签）。
// 超时：老项目无显式超时（默认 100s）；新项目显式 100s，保持一致。
// =========================================================================

using System.Net;
using System.Text;

namespace CureLine.Core.Mes;

public static class MesHttpClient
{
    private static readonly HttpClient Http;

    static MesHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // 复刻老项目：绕过自签名证书验证（如需 HTTPS）。
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
    }

    /// <summary>
    /// POST JSON。成功 true；失败 false 且 responseMsg 为错误信息/错误流。
    /// stationId 透传给日志（多机分机台记，便于按机台排障）。
    /// </summary>
    public static async Task<(bool Success, string Response)> PostJsonAsync(
        string url, string jsonBody, int stationId = -1)
    {
        MesLogger.WriteLog("Http Request", $"URL: {url}\r\nBODY: {jsonBody}", stationId);
        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content).ConfigureAwait(false);
            string msg = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                MesLogger.WriteLog("Http Response", msg, stationId);
                return (true, msg);
            }
            // 非 200：body 照样有用（MES 的 STATUS/MSG），按"可解析的失败"处理：
            // 老项目此时走 WebException 分支返回 false 但 responseMsg=错误流，语义相同。
            MesLogger.WriteLog("Http Error", msg, stationId);
            return (false, msg);
        }
        catch (HttpRequestException ex)
        {
            string msg = ex.Message;
            MesLogger.WriteLog("Http Error", msg, stationId);
            return (false, msg);
        }
        catch (TaskCanceledException ex)
        {
            // 超时（HttpClient 超时抛 TaskCanceledException，老项目对应 WebException 超时）。
            string msg = "请求超时: " + ex.Message;
            MesLogger.WriteLog("Http Error", msg, stationId);
            return (false, msg);
        }
        catch (Exception ex)
        {
            MesLogger.WriteLog("Http Error", ex.Message, stationId);
            return (false, ex.Message);
        }
    }

    /// <summary>同步版（老项目 PostJson 是同步签名，存量调用过渡用，新代码请用异步）。</summary>
    public static bool PostJson(string url, string jsonBody, out string responseMsg, int stationId = -1)
    {
        var (ok, msg) = PostJsonAsync(url, jsonBody, stationId).GetAwaiter().GetResult();
        responseMsg = msg;
        return ok;
    }
}
