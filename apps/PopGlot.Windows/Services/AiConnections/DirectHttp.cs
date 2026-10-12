using System.Net.Http;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

internal static class DirectHttp
{
    internal static Func<HttpClient>? TestClientFactory;
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15) })
    { Timeout = Timeout.InfiniteTimeSpan };
    public static HttpClient Client => TestClientFactory?.Invoke() ?? Shared;
    public static void RequirePolicy()
    {
        if (!RuntimeGate.NewWorkAllowed) throw new InvalidOperationException(RuntimeGate.RefusalZh);
        var settings = CoreBridge.GetSettings();
        if (settings.SafeDevMode || !settings.NetworkEnabled)
            throw new InvalidOperationException("网络翻译已关闭或处于安全离线模式，未发送直连请求。");
    }
    public static async Task<JsonElement> JsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequirePolicy();
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (request.RequestUri?.Host == "auth.openai.com" && request.RequestUri.AbsolutePath.EndsWith("oauth/token", StringComparison.Ordinal) && (int)response.StatusCode == 400)
                throw new InvalidOperationException("官方授权已过期、被撤销或未完成，请重新登录 ChatGPT。");
            throw new InvalidOperationException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "账号授权已失效或没有此权限，请重新登录或检查额度。" : "官方服务暂时无法完成请求，请稍后重试。");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { if (output.Length + count > 1024 * 1024) throw new InvalidOperationException("服务响应超过上限。"); output.Write(buffer, 0, count); }
        using var document = JsonDocument.Parse(output.ToArray()); return document.RootElement.Clone();
    }
}
