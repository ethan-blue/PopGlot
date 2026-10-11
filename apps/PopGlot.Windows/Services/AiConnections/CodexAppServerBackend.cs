using System.Diagnostics;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>Codex 0.150.1 app-server. Authentication remains inside Codex.</summary>
internal sealed class CodexAppServerBackend : ICliBackend
{
    private readonly CliRuntimeHost _host;
    private readonly CliConnectionSettings _connection;
    private JsonLineTransport? _transport;
    private readonly Dictionary<string, object?> _threadConfig = new();
    public CodexAppServerBackend(CliConnectionSettings connection)
    {
        _connection = connection;
        _host = new(CliDiscovery.CodexToolId, EngineExecutionKind.CodexAppServer, connection);
    }
    public long SpawnCount => _host.SpawnCount;
    public long PromptCount => _host.PromptCount;

    private async Task<JsonLineTransport> ConnectAsync(CancellationToken ct)
    {
        CliLauncher.RequirePolicy(EngineExecutionKind.CodexAppServer, _connection);
        if (_transport is { IsAlive: true }) return _transport;
        await CliLauncher.VerifyVersionAsync(CliDiscovery.CodexToolId, _connection, ct).ConfigureAwait(false);
        var transport = _host.EnsureTransport(BuildStartInfo);
        await RequestAsync(transport, "initialize", new
        {
            clientInfo = new { name = "popglot", title = "PopGlot", version = typeof(CodexAppServerBackend).Assembly.GetName().Version?.ToString() ?? "0" },
            capabilities = new { experimentalApi = true },
        }, ct).ConfigureAwait(false);
        await transport.NotifyAsync("{\"method\":\"initialized\"}", ct).ConfigureAwait(false);
        // Disable each configured MCP server without exposing its configuration or credentials.
        var config = await RequestAsync(transport, "config/read", new { includeLayers = false }, ct).ConfigureAwait(false);
        if (config.TryGetProperty("config", out var settings) && settings.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Object)
            foreach (var server in servers.EnumerateObject()) _threadConfig[$"mcp_servers.{server.Name}.enabled"] = false;
        _transport = transport;
        return transport;
    }

    internal static ProcessStartInfo BuildStartInfo(CliConnectionSettings connection)
    {
        var info = new ProcessStartInfo(CliLauncher.Resolve(CliDiscovery.CodexToolId, connection.ExecutablePath));
        info.ArgumentList.Add("app-server");
        foreach (var feature in new[] { "shell_tool", "unified_exec", "shell_snapshot", "apps", "plugins", "hooks", "multi_agent", "image_generation", "view_image", "tool_search", "tool_suggest", "token_budget", "current_time_reminder", "collaboration_modes", "deferred_executor", "remote_control", "code_mode", "code_mode_only", "js_repl", "browser_use", "computer_use", "memories" })
        { info.ArgumentList.Add("-c"); info.ArgumentList.Add($"features.{feature}=false"); }
        foreach (var config in new[] { "web_search=\"disabled\"", "project_doc_max_bytes=0", "skills.include_instructions=false", "history.persistence=\"none\"" })
        { info.ArgumentList.Add("-c"); info.ArgumentList.Add(config); }
        return info;
    }

    private static async Task<JsonElement> RequestAsync(JsonLineTransport transport, string method, object parameters, CancellationToken ct)
        => CliWire.Result(await transport.SendAsync(id => JsonSerializer.Serialize(new { id, method, @params = parameters }), line => CliWire.ResponseId(line), ct).ConfigureAwait(false));

    public async Task<CliAccountStatus> CheckAccountAsync(CancellationToken ct)
    {
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        var result = await RequestAsync(transport, "account/read", new { refreshToken = false }, ct).ConfigureAwait(false);
        var found = result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object;
        var requires = result.TryGetProperty("requiresOpenaiAuth", out var auth) && auth.GetBoolean();
        return new(found || !requires ? CliConnectionState.PendingRequestVerification : CliConnectionState.NeedsLogin,
            found && account.TryGetProperty("type", out var type) ? type.GetString() : null,
            found || !requires ? "Codex 账号状态已读取；选择模型后测试翻译。" : "Codex 尚未登录。请在 Codex 中登录后重新连接。");
    }

    public async Task<IReadOnlyList<CliModel>> ListModelsAsync(CancellationToken ct)
    {
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        var models = new List<CliModel>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync(transport, "model/list", new { cursor, limit = 100 }, ct).ConfigureAwait(false);
            foreach (var model in result.GetProperty("data").EnumerateArray())
            {
                if (model.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean()) continue;
                var id = model.GetProperty("model").GetString()!;
                models.Add(new(id, model.TryGetProperty("displayName", out var name) ? name.GetString() ?? id : id,
                    IsDefault: model.TryGetProperty("isDefault", out var defaultValue) && defaultValue.GetBoolean()));
            }
            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (models.Count > 1000) throw new InvalidOperationException("工具返回的模型列表超过上限。");
        } while (cursor is not null);
        return models;
    }

    public async Task RunAsync(string instructions, string payload, Action<string> delta, CancellationToken ct, Action? promptSubmitted = null)
    {
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        CliLauncher.RequirePolicy(EngineExecutionKind.CodexAppServer, _connection);
        using var events = transport.Subscribe();
        // Empty environments disables filesystem/shell/image access in this installed protocol.
        // New ephemeral thread per job prevents prior translations leaking into context.
        var thread = await RequestAsync(transport, "thread/start", new
        {
            model = _connection.ModelId, baseInstructions = instructions, developerInstructions = "",
            ephemeral = true, environments = Array.Empty<object>(), selectedCapabilityRoots = Array.Empty<object>(),
            dynamicTools = Array.Empty<object>(), sandbox = "read-only", approvalPolicy = "never", config = _threadConfig,
        }, ct).ConfigureAwait(false);
        var threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
        CliLauncher.RequirePolicy(EngineExecutionKind.CodexAppServer, _connection);
        _host.RecordPromptSubmission();
        promptSubmitted?.Invoke();
        var turn = await RequestAsync(transport, "turn/start", new
        {
            threadId, input = new[] { new { type = "text", text = payload, text_elements = Array.Empty<object>() } },
            effort = "low",
        }, ct).ConfigureAwait(false);
        var turnId = turn.GetProperty("turn").GetProperty("id").GetString();
        var received = new HashSet<string>();
        await foreach (var line in events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var p)) continue;
            if (root.TryGetProperty("id", out _)) throw new InvalidOperationException("翻译通道收到工具执行或交互请求，已停止连接。");
            if (p.TryGetProperty("threadId", out var t) && t.GetString() != threadId) continue;
            if (p.TryGetProperty("turnId", out var v) && v.GetString() != turnId) continue;
            if (method.GetString() == "item/agentMessage/delta")
            { received.Add(p.GetProperty("itemId").GetString()!); delta(p.GetProperty("delta").GetString() ?? ""); }
            if ((method.GetString() is "item/started" or "item/completed") && p.TryGetProperty("item", out var item))
            {
                var type = item.GetProperty("type").GetString();
                if (type is not ("agentMessage" or "userMessage" or "reasoning"))
                    throw new InvalidOperationException("工具尝试执行翻译以外的操作，已停止连接。");
                if (method.GetString() == "item/completed" && type == "agentMessage" && received.Add(item.GetProperty("id").GetString()!))
                    delta(item.GetProperty("text").GetString() ?? "");
            }
            if (method.GetString() == "turn/completed" && p.GetProperty("turn").GetProperty("id").GetString() == turnId)
            {
                if (p.GetProperty("turn").GetProperty("status").GetString() != "completed")
                    throw new InvalidOperationException("Codex 未完成翻译。请检查账号额度、模型权限及网络后重试。");
                return;
            }
        }
        throw new InvalidOperationException("Codex 连接提前关闭，翻译未完成。");
    }

    public void Dispose() => _host.Dispose();
}
