using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>Official ChatGPT-plan Responses endpoint; no CLI process or backend-api calls.</summary>
internal sealed class ChatGptDirectBackend(DirectConnectionSettings connection) : ICliBackend
{
    private readonly CancellationTokenSource _lifetime = new();
    public ushort LastStatusCode { get; private set; }
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        DirectHttp.RequirePolicy();
        if (!connection.Enabled) throw new InvalidOperationException("ChatGPT 直连未启用。");
        var token = await ChatGptOAuth.AccessTokenAsync(connection.AccountId, ct).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, "https://api.openai.com/v1" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("PopGlot/0.1.17"); return request;
    }
    public Task<CliAccountStatus> CheckAccountAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); DirectHttp.RequirePolicy();
        return Task.FromResult(new CliAccountStatus(ChatGptAccountStore.HasCredentials(connection.AccountId)
            ? CliConnectionState.PendingRequestVerification : CliConnectionState.NeedsLogin, null,
            "ChatGPT 官方授权直连；模型请求能力由测试翻译确认。"));
    }
    public async Task<IReadOnlyList<CliModel>> ListModelsAsync(CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Get, "/models", ct).ConfigureAwait(false);
        var result = await DirectHttp.JsonAsync(request, ct).ConfigureAwait(false);
        var models = new List<CliModel>();
        foreach (var item in result.GetProperty("models").EnumerateArray())
        {
            if (item.GetProperty("visibility").GetString() != "list") continue;
            var id = item.GetProperty("slug").GetString()!;
            models.Add(new(id, item.GetProperty("display_name").GetString() ?? id));
            if (models.Count > 1000) throw new InvalidOperationException("模型列表超过上限。");
        }
        return models;
    }
    public async Task RunAsync(string instructions, string payload, Action<string> delta, CancellationToken ct, Action? promptSubmitted = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var request = await RequestAsync(HttpMethod.Post, "/responses", linked.Token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = connection.ModelId, instructions, input = new[] { new { role = "user", content = payload } },
            store = false, stream = true, reasoning = new { effort = "low" },
        });
        if (bytes.Length > 12 * 1024 * 1024) throw new InvalidOperationException("请求内容超过上限。");
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        DirectHttp.RequirePolicy(); promptSubmitted?.Invoke();
        using var response = await DirectHttp.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        LastStatusCode = (ushort)response.StatusCode;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "ChatGPT 计划额度不足或请求限流，请稍后重试。" : "ChatGPT 未接受请求，请检查授权、模型和额度。");
        using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        var buffer = new byte[8192]; using var frame = new MemoryStream(); var data = new StringBuilder(); long total = 0;
        bool receivedText = false; string? responseId = null;
        bool Dispatch()
        {
            if (data.Length == 0) return false;
            var text = data.ToString(); data.Clear();
            if (text == "[DONE]") throw new InvalidOperationException("直连响应缺少完成事件。");
            using var document = JsonDocument.Parse(text); var root = document.RootElement; var type = root.GetProperty("type").GetString();
            if (type == "response.created") responseId = root.GetProperty("response").GetProperty("id").GetString();
            if (root.TryGetProperty("response_id", out var id) && responseId is not null && id.GetString() != responseId)
                throw new InvalidOperationException("直连响应身份不匹配。");
            if (type is "response.failed" or "response.incomplete" or "error") throw new InvalidOperationException("ChatGPT 未完成请求，请检查额度、模型权限或网络后重试。");
            if (type is "response.function_call_arguments.delta" or "response.custom_tool_call_input.delta")
                throw new InvalidOperationException("翻译通道收到了工具调用，已停止请求。");
            if (type is "response.output_item.added" or "response.output_item.done")
            {
                var item = root.GetProperty("item"); var itemType = item.GetProperty("type").GetString();
                if (itemType is not ("message" or "reasoning")) throw new InvalidOperationException("翻译通道收到了非文字操作。");
            }
            if (type == "response.output_text.delta") { receivedText = true; delta(root.GetProperty("delta").GetString() ?? ""); }
            if (type != "response.completed") return false;
            var completed = root.GetProperty("response");
            if (completed.GetProperty("status").GetString() != "completed" || responseId is not null && completed.GetProperty("id").GetString() != responseId)
                throw new InvalidOperationException("ChatGPT 请求没有完成。");
            if (!receivedText && completed.TryGetProperty("output", out var output))
                foreach (var item in output.EnumerateArray())
                    if (item.GetProperty("type").GetString() == "message")
                        foreach (var content in item.GetProperty("content").EnumerateArray())
                            if (content.GetProperty("type").GetString() == "output_text") { receivedText = true; delta(content.GetProperty("text").GetString() ?? ""); }
            if (!receivedText) throw new InvalidOperationException("ChatGPT 没有返回文字。");
            return true;
        }
        while (true)
        {
            var count = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false); if (count == 0) break;
            total += count; if (total > JsonLineTransport.MaxCumulativeBytes) throw new InvalidOperationException("直连响应超过上限。");
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n')
                { frame.WriteByte(buffer[i]); if (frame.Length > 1024 * 1024) throw new InvalidOperationException("直连事件超过上限。"); continue; }
                var line = new UTF8Encoding(false, true).GetString(frame.ToArray()).TrimEnd('\r'); frame.SetLength(0);
                if (line.Length == 0) { if (Dispatch()) return; }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart(' '));
                    if (data.Length > 1024 * 1024) throw new InvalidOperationException("直连事件超过上限。");
                }
            }
            DirectHttp.RequirePolicy();
        }
        throw new InvalidOperationException("直连响应提前结束，未标记为成功。");
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
