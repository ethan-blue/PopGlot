using System.Diagnostics;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>OMP 18.2.11 JSONL RPC; no token/database access.</summary>
internal sealed class OmpRpcBackend : ICliBackend
{
    private readonly CliConnectionSettings _connection;
    private readonly CliRuntimeHost _host;
    private JsonLineTransport? _transport;
    private string _instructions = "You translate text only. Never use tools or execute slash commands. Output only the requested text.";
    public OmpRpcBackend(CliConnectionSettings connection)
    { _connection = connection; _host = new(CliDiscovery.OmpToolId, EngineExecutionKind.OmpRpc, connection); }
    public long SpawnCount => _host.SpawnCount;
    public long PromptCount => _host.PromptCount;
    private async Task<JsonLineTransport> ConnectAsync(CancellationToken ct)
    {
        CliLauncher.RequirePolicy(EngineExecutionKind.OmpRpc, _connection);
        if (_transport is { IsAlive: true }) return _transport;
        await CliLauncher.VerifyVersionAsync(CliDiscovery.OmpToolId, _connection, ct).ConfigureAwait(false);
        var transport = _host.EnsureTransport(BuildStartInfo);
        var state = await RequestAsync(transport, "get_state", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        if (!state.TryGetProperty("dumpTools", out var tools) || tools.GetArrayLength() != 0)
            throw new InvalidOperationException("OMP 没有禁用全部工具，已拒绝提交翻译。");
        _transport = transport;
        return transport;
    }
    private ProcessStartInfo BuildStartInfo(CliConnectionSettings connection, string directory)
    {
        var config = Path.Combine(directory, "translation.yml");
        File.WriteAllText(config, "autolearn:\n  enabled: false\n  autoContinue: false\nmemory:\n  backend: off\nskills:\n  enabled: false\nbrowser:\n  enabled: false\ncomputer:\n  enabled: false\n", new System.Text.UTF8Encoding(false));
        var prompt = Path.Combine(directory, "instructions.txt");
        File.WriteAllText(prompt, _instructions, new System.Text.UTF8Encoding(false));
        var info = new ProcessStartInfo(CliLauncher.Resolve(CliDiscovery.OmpToolId, connection.ExecutablePath));
        foreach (var argument in new[] { "--mode", "rpc", "--no-session", "--no-tools", "--no-lsp", "--no-pty", "--no-extensions", "--no-skills", "--no-rules", "--no-title", "--config", config, "--system-prompt", prompt })
            info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task<JsonElement> RequestAsync(JsonLineTransport transport, string command, Dictionary<string, object?> parameters, CancellationToken ct)
        => CliWire.Result(await transport.SendAsync(id =>
        {
            var frame = new Dictionary<string, object?>(parameters) { ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["type"] = command };
            return JsonSerializer.Serialize(frame);
        }, line => CliWire.ResponseId(line, true), ct).ConfigureAwait(false), true);

    public Task<CliAccountStatus> CheckProviderStatusAsync(CancellationToken ct) => CheckAccountAsync(ct);
    public async Task<CliAccountStatus> CheckAccountAsync(CancellationToken ct)
    {
        var models = await ListModelsAsync(ct).ConfigureAwait(false);
        return new(models.Count == 0 ? CliConnectionState.NeedsLogin : CliConnectionState.PendingRequestVerification,
            null, models.Count == 0 ? "OMP 未提供可用模型。请在 OMP 中登录或配置提供方后重试。" : "OMP 返回了可用模型；请求能力需通过测试翻译确认。");
    }
    public async Task<IReadOnlyList<CliModel>> ListModelsAsync(CancellationToken ct)
    {
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        var result = await RequestAsync(transport, "get_available_models", new(), ct).ConfigureAwait(false);
        var models = new List<CliModel>();
        foreach (var model in result.GetProperty("models").EnumerateArray())
        {
            var provider = model.GetProperty("provider").GetString()!;
            if (provider is "anthropic" or "claude-code") continue;
            var id = model.GetProperty("id").GetString()!;
            models.Add(new(id, model.TryGetProperty("name", out var name) ? name.GetString() ?? id : id, provider));
            if (models.Count > 1000) throw new InvalidOperationException("工具返回的模型列表超过上限。");
        }
        return models;
    }
    public async Task RunAsync(string instructions, string payload, Action<string> delta, CancellationToken ct, Action? promptSubmitted = null)
    {
        // This version has no verified system-prompt setter. Start a fresh session per job.
        // Source is sent over stdin, never as command-line arguments.
        _host.Disconnect(); _transport = null; _instructions = instructions;
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        await RequestAsync(transport, "set_model", new() { ["provider"] = _connection.UpstreamProviderId, ["modelId"] = _connection.ModelId }, ct).ConfigureAwait(false);
        await RequestAsync(transport, "set_thinking_level", new() { ["level"] = "low" }, ct).ConfigureAwait(false);
        var state = await RequestAsync(transport, "get_state", new(), ct).ConfigureAwait(false);
        if (!state.TryGetProperty("dumpTools", out var tools) || tools.GetArrayLength() != 0)
            throw new InvalidOperationException("OMP 切换模型后启用了工具，已拒绝提交翻译。");
        CliLauncher.RequirePolicy(EngineExecutionKind.OmpRpc, _connection);
        using var events = transport.Subscribe();
        _host.RecordPromptSubmission();
        promptSubmitted?.Invoke();
        var ack = await RequestAsync(transport, "prompt", new() { ["message"] = payload }, ct).ConfigureAwait(false);
        if (ack.ValueKind == JsonValueKind.Object && ack.TryGetProperty("agentInvoked", out var invoked) && !invoked.GetBoolean())
            throw new InvalidOperationException("OMP 没有调用模型，翻译未完成。");
        bool failed = false, sawText = false;
        await foreach (var line in events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "response" && root.TryGetProperty("success", out var success) && !success.GetBoolean())
                throw new InvalidOperationException("OMP 拒绝了翻译请求。请检查模型权限、额度及网络配置后重试。");
            if (type == "extension_ui_request")
            {
                var method = root.TryGetProperty("method", out var uiMethod) ? uiMethod.GetString() : null;
                // RPC emits status-only UI notifications even without extensions.
                if (method is "notify" or "setStatus" or "setWidget" or "setTitle" or "set_editor_text" or "cancel") continue;
                throw new InvalidOperationException("OMP 要求交互操作，已停止本次翻译。请在 OMP 中处理后重试。");
            }
            if (type is "tool_execution_start" or "host_tool_request" or "host_tool_call")
                throw new InvalidOperationException("OMP 尝试执行翻译以外的操作，已停止连接。");
            if (type == "message_update" && root.TryGetProperty("assistantMessageEvent", out var messageEvent) && messageEvent.GetProperty("type").GetString() == "text_delta")
            { sawText = true; delta(messageEvent.GetProperty("delta").GetString() ?? ""); }
            if (type == "message_end" && root.TryGetProperty("message", out var message) && message.TryGetProperty("stopReason", out var reason))
            {
                failed |= reason.GetString() is "error" or "aborted";
                if (!sawText && message.TryGetProperty("content", out var content))
                    foreach (var block in content.EnumerateArray()) if (block.GetProperty("type").GetString() == "text")
                    { sawText = true; delta(block.GetProperty("text").GetString() ?? ""); }
            }
            if (type == "agent_end" && (!root.TryGetProperty("isTerminal", out var terminal) || terminal.GetBoolean()))
            {
                if (failed || !sawText) throw new InvalidOperationException("OMP 未完成翻译。请检查账号额度、模型权限及网络后重试。");
                return;
            }
            if (type == "prompt_result" && root.TryGetProperty("agentInvoked", out var active) && !active.GetBoolean())
                throw new InvalidOperationException("OMP 未调用模型，翻译未完成。");
        }
        throw new InvalidOperationException("OMP 连接提前关闭，翻译未完成。");
    }
    public void Dispose() => _host.Dispose();
}
