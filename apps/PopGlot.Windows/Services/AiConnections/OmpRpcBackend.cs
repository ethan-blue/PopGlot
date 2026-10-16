using System.Diagnostics;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>OMP JSONL RPC; compatibility is checked through actual responses, not version equality.</summary>
internal sealed class OmpRpcBackend : ICliBackend
{
    internal sealed record LoginProvider(string Id, string Name, bool Available, bool Authenticated)
    {
        public string DisplayName => $"{Name} · {(Authenticated ? "已登录" : Available ? "未登录" : "暂不可用")}";
        public override string ToString() => DisplayName;
    }

    public async Task<IReadOnlyList<LoginProvider>> ListLoginProvidersAsync(CancellationToken ct)
    {
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        var result = await RequestAsync(transport, "get_login_providers", new(), ct).ConfigureAwait(false);
        var providers = new List<LoginProvider>();
        foreach (var item in result.GetProperty("providers").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            if (id is "anthropic" or "claude-code") continue;
            providers.Add(new(id, item.GetProperty("name").GetString() ?? id,
                item.GetProperty("available").GetBoolean(), item.GetProperty("authenticated").GetBoolean()));
            if (providers.Count > 100) throw new InvalidOperationException("OMP 返回的登录提供方超过上限。");
        }
        return providers;
    }

    // Only explicit login consumes interactive UI events. Translation remains non-interactive.
    // OMP owns all credentials, refresh and persistence; none are returned to PopGlot.
    public async Task LoginAsync(string providerId, Func<Uri, string?, CancellationToken, Task> openBrowser,
        Func<string, CancellationToken, Task<string>> requestInput, CancellationToken ct)
    {
        var providers = await ListLoginProvidersAsync(ct).ConfigureAwait(false);
        if (!providers.Any(p => p.Id == providerId && p.Available))
            throw new InvalidOperationException("此提供方暂不支持 OMP 登录。");
        var transport = await ConnectAsync(ct).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var events = transport.Subscribe();
        var authorizedUrl = false;
        async Task MonitorPolicyAsync()
        {
            while (true)
            {
                await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                CliLauncher.RequirePolicy(EngineExecutionKind.OmpRpc, _connection);
            }
        }
        async Task ConsumeAsync()
        {
            await foreach (var line in events.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is "tool_execution_start" or "host_tool_request" or "host_tool_call")
                    throw new InvalidOperationException("OMP 登录尝试执行额外工具，已停止。");
                if (type != "extension_ui_request") continue;
                var method = root.GetProperty("method").GetString();
                if (method == "open_url")
                {
                    var url = root.TryGetProperty("launchUrl", out var launch) && launch.ValueKind == JsonValueKind.String
                        ? launch.GetString() : root.GetProperty("url").GetString();
                    var uri = ValidateLoginUrl(url);
                    var instructions = root.TryGetProperty("instructions", out var hint) && hint.ValueKind == JsonValueKind.String ? hint.GetString() : null;
                    if (instructions?.Length > 4096) throw new InvalidOperationException("OMP 授权说明超过上限。");
                    await openBrowser(uri, instructions, lifetime.Token).ConfigureAwait(false);
                    authorizedUrl = true;
                }
                else if (method == "input" && authorizedUrl)
                {
                    var value = await requestInput(root.TryGetProperty("title", out var title) ? title.GetString() ?? "授权回调" : "授权回调", lifetime.Token).ConfigureAwait(false);
                    if (value.Length > 16384 || value.Contains('\n') || value.Contains('\r'))
                        throw new InvalidOperationException("授权回调格式无效。");
                    await transport.NotifyAsync(JsonSerializer.Serialize(new { type = "extension_ui_response", id = root.GetProperty("id").GetString(), value }), lifetime.Token).ConfigureAwait(false);
                }
                else if (method is not ("notify" or "setStatus" or "setWidget" or "setTitle" or "set_editor_text" or "cancel"))
                    throw new InvalidOperationException("此登录需要 OMP 终端交互，请在 OMP 中完成后刷新账号。");
            }
            throw new InvalidOperationException("OMP 登录连接提前关闭。");
        }
        var consume = ConsumeAsync();
        var policy = MonitorPolicyAsync();
        var login = RequestAsync(transport, "login", new() { ["providerId"] = providerId }, lifetime.Token);
        try
        {
            var first = await Task.WhenAny(login, consume, policy).ConfigureAwait(false);
            if (first == consume) await consume.ConfigureAwait(false);
            if (first == policy) await policy.ConfigureAwait(false);
            await login.ConfigureAwait(false); // URL opening is not login success.
            ct.ThrowIfCancellationRequested();
            CliLauncher.RequirePolicy(EngineExecutionKind.OmpRpc, _connection);
        }
        finally
        {
            lifetime.Cancel();
            _host.Disconnect(); _transport = null;
            try { await consume.ConfigureAwait(false); } catch { /* Primary failure is propagated above. */ }
            try { await policy.ConfigureAwait(false); } catch { /* Policy failure is propagated above. */ }
            // Observe the pending request even when the UI/event handler failed.
            if (!login.IsCompletedSuccessfully) { try { await login.ConfigureAwait(false); } catch { } }
        }
    }

    internal static Uri ValidateLoginUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            uri.Host is not ("accounts.google.com" or "auth.openai.com" or "github.com" or "login.microsoftonline.com" or "login.live.com"))
            throw new InvalidOperationException("OMP 返回了不支持的授权地址，请在 OMP 中完成登录。");
        return uri;
    }
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
        RequireToolIsolation(state);
        _transport = transport;
        return transport;
    }
    private ProcessStartInfo BuildStartInfo(CliConnectionSettings connection, string directory)
    {
        var config = Path.Combine(directory, "translation.yml");
        File.WriteAllText(config, "autolearn:\n  enabled: false\n  autoContinue: false\nmemory:\n  backend: off\nskills:\n  enabled: false\nbrowser:\n  enabled: false\ncomputer:\n  enabled: false\n", new System.Text.UTF8Encoding(false));
        var prompt = Path.Combine(directory, "instructions.txt");
        File.WriteAllText(prompt, _instructions, new System.Text.UTF8Encoding(false));
        var info = CliLauncher.CreateStartInfo(CliDiscovery.OmpToolId, connection.ExecutablePath);
        foreach (var argument in new[] { "--mode", "rpc", "--no-session", "--no-tools", "--no-lsp", "--no-pty", "--no-extensions", "--no-skills", "--no-rules", "--no-title", "--config", config, "--system-prompt", prompt })
            info.ArgumentList.Add(argument);
        return info;
    }
    private static async Task<JsonElement> RequestAsync(JsonLineTransport transport, string command, Dictionary<string, object?> parameters, CancellationToken ct,
        Action<string>? onRequestId = null)
        => CliWire.Result(await transport.SendAsync(id =>
        {
            var requestId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            onRequestId?.Invoke(requestId);
            var frame = new Dictionary<string, object?>(parameters) { ["id"] = requestId, ["type"] = command };
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
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OMP RPC 未返回可解析的模型列表，请检查该工具的 RPC 支持。");
        foreach (var model in entries.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("provider", out var providerValue) ||
                providerValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(providerValue.GetString()) ||
                !model.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idValue.GetString()))
                throw new InvalidOperationException("OMP RPC 返回了无效的提供方或模型标识，请检查该工具的 RPC 支持。");
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
        RequireToolIsolation(state);
        CliLauncher.RequirePolicy(EngineExecutionKind.OmpRpc, _connection);
        using var events = transport.Subscribe();
        _host.RecordPromptSubmission();
        promptSubmitted?.Invoke();
        string? promptId = null;
        var ack = await RequestAsync(transport, "prompt", new() { ["message"] = payload }, ct,
            id => promptId = id).ConfigureAwait(false);
        if (ack.ValueKind == JsonValueKind.Object && ack.TryGetProperty("agentInvoked", out var invoked) && !invoked.GetBoolean())
            throw new InvalidOperationException("OMP 没有调用模型，翻译未完成。");
        bool failed = false, sawText = false;
        string? providerFailure = null;
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
                if (reason.GetString() == "error" && message.TryGetProperty("errorMessage", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
                {
                    // Expose only an HTTP code, never upstream text (which can contain credentials).
                    var error = errorMessage.GetString() ?? "";
                    var status = System.Text.RegularExpressions.Regex.Match(error[..Math.Min(error.Length, 2000)], @"(?:^|\bHTTP(?:/\d(?:\.\d)?)?\s+|\bstatus(?:\s+code)?[=: ]+)(400|401|403|404|408|409|429|500|502|503|504)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    providerFailure = status.Success ? $"OMP 提供方请求失败（状态 {status.Groups[1].Value}）。请检查账号权限、额度和网络后重试。" : "OMP 提供方返回了请求错误。请检查账号权限、额度和网络后重试。";
                }
                if (!sawText && message.TryGetProperty("content", out var content))
                    foreach (var block in content.EnumerateArray()) if (block.GetProperty("type").GetString() == "text")
                    { sawText = true; delta(block.GetProperty("text").GetString() ?? ""); }
            }
            // Current OMP settles each prompt with prompt_result. The audited
            // v18.2.11 source emits prompt_result only for local/no-agent work,
            // so retain its terminal agent_end contract for that exact known
            // release. Unknown/new versions must prove prompt completion.
            if (type == "agent_end")
            {
                if (failed) throw new InvalidOperationException(providerFailure ?? "OMP 未完成翻译。请检查账号额度、模型权限及网络后重试。");
                if (_connection.ExpectedVersion == "18.2.11" &&
                    root.TryGetProperty("isTerminal", out var terminal) && terminal.ValueKind == JsonValueKind.True)
                {
                    if (!sawText) throw new InvalidOperationException("OMP 未返回翻译内容。");
                    return;
                }
            }
            if (type == "prompt_result")
            {
                if (!root.TryGetProperty("id", out var resultId) || resultId.ToString() != promptId) continue;
                if (!root.TryGetProperty("agentInvoked", out var active) || active.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("OMP 未调用模型，翻译未完成。");
                var status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
                if (status != "completed" || failed || !sawText)
                {
                    // Only a numeric status is safe to expose, never the raw
                    // provider message (which may echo source or credentials).
                    var code = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("httpStatus", out var http) && http.ValueKind == JsonValueKind.Number &&
                        http.TryGetInt32(out var value) && value is >= 400 and <= 599
                        ? $"（HTTP {value}）" : "";
                    throw new InvalidOperationException(providerFailure ?? $"OMP 翻译未完成{code}，请检查连接后重试。");
                }
                return;
            }
        }
        throw new InvalidOperationException("OMP 连接提前关闭，翻译未完成。");
    }
    public void Dispose() => _host.Dispose();
    private static void RequireToolIsolation(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("dumpTools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OMP RPC 没有提供可验证的工具状态，未提交翻译；请检查该工具的 RPC 支持。");
        if (tools.GetArrayLength() != 0)
            throw new InvalidOperationException("OMP 没有禁用全部工具，已拒绝提交翻译。");
    }
}
