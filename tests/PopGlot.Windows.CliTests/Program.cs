using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using PopGlot.Windows;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.CliTests;

internal static class Program
{
    private static int _passes;
    private static int _failures;
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Contains("--version"))
        {
            var version = Environment.GetEnvironmentVariable("POPGLOT_FAKE_VERSION");
            if (version == "unsupported-version-query") return 2;
            if (version == "hanging-version-query") { Thread.Sleep(30000); return 2; }
            Console.WriteLine(Environment.GetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL") == "omp" ? version ?? "18.2.11" : "codex-cli 0.150.1"); return 0;
        }
        if (args.Contains("app-server") || args.Contains("rpc")) return FakeServer(args.Contains("rpc"));
        var root = Path.Combine(Path.GetTempPath(), $"popglot-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        StoragePaths.RootOverride = root;
        CredentialStore.OverrideVault = new MemoryCredentialVault();
        Environment.SetEnvironmentVariable("POPGLOT_DATA_ROOT", root);
        CoreBridge.Initialize(root);
        var settings = CoreBridge.GetSettings();
        CoreBridge.SaveSettings(settings with { NetworkEnabled = true, SafeDevMode = false });
        try
        {
            Check("Codex protocol + FFI stream", () => StreamFake(EngineExecutionKind.CodexAppServer));
            Check("OMP protocol + FFI stream", () => StreamFake(EngineExecutionKind.OmpRpc));
            Check("OMP compatibility follows protocol, not exact version or saved pin", OmpVersionCompatibility);
            Check("OMP login browser/callback/final confirmation", OmpLoginSuccess);
            Check("OMP login failure/cancel/unsafe URL isolation", OmpLoginFailures);
            Check("late OMP prompt error is not success", () => FailureFake("late_error"));
            Check("OMP HTTP error shows status without leaking upstream text", OmpHttpFailure);
            Check("process crash completes turn", () => FailureFake("crash"));
            Check("no tool access accepted", () => FailureFake("tools"));
            Check("cancel kills owned process", CancellationFake);
            Check("offline gate: zero processes/prompts", Offline);
            Check("save, reload, switch, disable", Persistence);
            Check("main translation/history/summary/recognized-text route", CoordinatorRoutes);
            Check("disconnect cancels active saved connection", DisconnectActive);
            Check("immediate disconnect fences queued request", DisconnectQueued);
            Check("official OAuth/PKCE/callback/JWT/encrypted credentials", DirectTests.SignInAndValidation);
            Check("direct HTTP main route/model list/summary/quick switch", DirectTests.DirectRoutes);
            Check("direct late failure and EOF never become success", DirectTests.TerminalFailures);
            Check("direct offline and disconnect fence HTTP requests", DirectTests.OfflineAndDisconnect);
            Check("serialized refresh rotation and logout isolation", DirectTests.RefreshAndLogout);
            Check("OMP API-key import consent and provider/OAuth isolation", DirectTests.ImportBoundaries);
            DirectTests.Restore();
            if (args.Contains("--live-import")) Check("installed OMP selected API key -> native HTTP translation", DirectTests.LiveImportedNative);
            if (args.Contains("--live"))
            {
                Check("installed Codex login/models/translation", () => Live("codex"));
                Check("installed OMP zero tools/models/translation", () => Live("omp"));
            }
            if (args.Contains("--live-omp")) Check("installed OMP providers/models/translation", () => Live("omp"));
            if (args.Contains("--probe-omp")) Check("installed OMP RPC with stale saved version, without inference", ProbeOmp);
            if (args.Contains("--ui")) Check("WPF connection screen and buttons", () => { UiScenario.Run(root); return Task.CompletedTask; });
        }
        finally { DirectTests.Restore(); Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", null); Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null); }
        Console.WriteLine($"RESULT {_passes} passed / {_failures} failed; isolated data: {root}");
        return _failures == 0 ? 0 : 1;
    }
    private static void Check(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); _passes++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        // A failed test must not poison the next one: scenario cleanup lives
        // here, not in the test body, or a leaked "hang" makes every later
        // fake server sleep through its 3-minute deadline.
        finally { Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null); }
    }
    internal static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ProviderProfile FakeProfile(EngineExecutionKind kind)
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", kind == EngineExecutionKind.OmpRpc ? "omp" : "codex");
        return new() { Id = "fake-" + kind, Name = "fixture", ApiBaseUrl = "", CredentialTarget = "", SupportsVision = false,
            ExecutionKind = EngineExecutionKindNames.Format(kind), TextModel = "fixture",
            CliConnection = new() { ExecutablePath = Environment.ProcessPath!, ExpectedVersion = CliLauncher.ReferenceToolVersion(kind), ModelId = "fixture", UpstreamProviderId = "fixture-provider", Enabled = true } };
    }
    private static async Task StreamFake(EngineExecutionKind kind)
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
        var profile = FakeProfile(kind);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var backend = CliTranslationExecutor.CreateBackend(kind, profile.CliConnection!);
        var account = await backend.CheckAccountAsync(timeout.Token);
        var models = await backend.ListModelsAsync(timeout.Token);
        Assert(account.State == CliConnectionState.PendingRequestVerification && models.Count == 1, "status/models must be real protocol results");
        var stream = CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, timeout.Token);
        var response = await stream.Completion;
        Assert(response.Result.TranslatedText == "你好，世界", "translation must use visible deltas");
        Assert(response.Diagnostics is null && response.CliDiagnostics?.ToolReportedCompletion == true, "no fabricated HTTP status");
        Assert(response.CliDiagnostics?.RequestPreparationMs is not null && response.CliDiagnostics.FirstProtocolDeltaMs >= response.CliDiagnostics.RequestPreparationMs,
            "CLI timing must separate preparation from the first protocol text delta");
        Assert(stream.Buffer.GetAccumulatedText() == "你好，世界", "stream matches result");
    }
    private static async Task FailureFake(string scenario)
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", scenario);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failed = false;
        try { await CliTranslationExecutor.Stream(profile, "Hello", "en", "zh", null, timeout.Token).Completion; }
        catch (InvalidOperationException) { failed = true; }
        Assert(failed, "must fail without reporting completed");
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
    }
    private static async Task OmpLoginSuccess()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "login_input");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new OmpRpcBackend(profile.CliConnection!);
        var providers = await backend.ListLoginProvidersAsync(timeout.Token);
        Assert(providers.Count == 2 && providers[0].Authenticated && !providers[1].Available, "provider state comes from OMP; disallowed provider filtered");
        Assert(providers[0].ToString() == "Google Antigravity · 已登录", "selection displays a provider label, never record internals");
        int browser = 0, input = 0;
        await backend.LoginAsync("google-antigravity", (uri, instructions, ct) => { browser++; Assert(uri.Host == "accounts.google.com" && instructions == "fixture device instructions", "browser URL and device instructions"); return Task.CompletedTask; },
            (_, ct) => { input++; return Task.FromResult("fixture-oauth-code"); }, timeout.Token);
        Assert(browser == 1 && input == 1, "must handle browser and matching callback input");
        Assert((await backend.ListModelsAsync(timeout.Token)).Count == 1, "models refresh after final login response on a fresh process");
    }
    private static async Task OmpVersionCompatibility()
    {
        try
        {
            foreach (var version in new[] { "omp/18.2.12", "omp/20.0.0-beta.1", "custom omp build", "unsupported-version-query", "hanging-version-query" })
            {
                var profile = FakeProfile(EngineExecutionKind.OmpRpc);
                profile.CliConnection!.ExpectedVersion = "old-saved-version";
                Assert(profile.ExecutionFacts.TextCapable, "saved OMP version cannot disable a configured route");
                Environment.SetEnvironmentVariable("POPGLOT_FAKE_VERSION", version);
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, ct.Token).Completion;
                Assert(response.Result.TranslatedText == "你好，世界", "compatible RPC must work regardless of reported version");
                var expected = version.StartsWith("omp/") ? version[4..] : "unknown";
                Assert(response.CliDiagnostics?.ToolVersion == expected, "diagnostics record the observed version, not a hardcoded pin");
            }
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "incompatible_state");
            using var backend = new OmpRpcBackend(FakeProfile(EngineExecutionKind.OmpRpc).CliConnection!);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool refused = false;
            try { await backend.RunAsync("translate", "Hello", _ => { }, timeout.Token); }
            catch (InvalidOperationException ex) { refused = ex.Message.Contains("RPC") && ex.Message.Contains("工具状态"); }
            Assert(refused && backend.PromptCount == 0, "missing isolation proof must reject before sending source text");
        }
        finally { Environment.SetEnvironmentVariable("POPGLOT_FAKE_VERSION", null); Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null); }
    }
    private static async Task OmpHttpFailure()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "http_error");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await CliTranslationExecutor.Stream(profile, "Hello", "en", "zh", null, timeout.Token).Completion; }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("429") && !ex.Message.Contains("private-upstream-text"), "only HTTP status may be displayed");
            return;
        }
        throw new InvalidOperationException("HTTP failure must not complete a translation");
    }
    private static async Task OmpLoginFailures()
    {
        foreach (var scenario in new[] { "login_error", "login_unsafe", "login_hang", "login_preinput", "login_policy" })
        {
            var profile = FakeProfile(EngineExecutionKind.OmpRpc);
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", scenario);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(scenario == "login_hang" ? 1200 : 10000));
            using var backend = new OmpRpcBackend(profile.CliConnection!);
            bool failed = false; int browser = 0;
            var settings = CoreBridge.GetSettings();
            try { await backend.LoginAsync("google-antigravity", (_, instructions, ct) => {
                browser++;
                if (scenario == "login_policy") CoreBridge.SaveSettings(settings with { NetworkEnabled = false });
                return Task.CompletedTask;
            }, (_, ct) => Task.FromResult("fixture-oauth-code"), timeout.Token); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { failed = true; }
            finally { CoreBridge.SaveSettings(settings); }
            Assert(failed && (scenario != "login_unsafe" || browser == 0), "failed/cancelled/unsafe login cannot become success");
        }
    }
    private static async Task CancellationFake()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "hang");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew(); var cancelled = false;
        try { await CliTranslationExecutor.Stream(profile, "hello", "en", "zh", null, cancel.Token).Completion; }
        catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && watch.Elapsed < TimeSpan.FromSeconds(5), "cancellation must finish promptly");
            Assert(!Directory.EnumerateDirectories(Path.Combine(StoragePaths.CoreConfigDirectory, "cli-run")).Any(), "owned work directories reclaimed");
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
    }
    private static async Task Offline()
    {
        var profile = FakeProfile(EngineExecutionKind.CodexAppServer);
        var settings = CoreBridge.GetSettings();
        CoreBridge.SaveSettings(settings with { NetworkEnabled = false });
        try
        {
            using var backend = new CodexAppServerBackend(profile.CliConnection!);
            var denied = false;
            try { await backend.CheckAccountAsync(CancellationToken.None); } catch (InvalidOperationException) { denied = true; }
            Assert(denied && backend.SpawnCount == 0 && backend.PromptCount == 0, "offline must refuse before any spawn");
        }
        finally { CoreBridge.SaveSettings(settings); }
    }
    private static Task Persistence()
    {
        var profile = FakeProfile(EngineExecutionKind.CodexAppServer);
        ProfileManager.SaveCliProfile(profile, null, true);
        var active = ProfileManager.Load().TryGetActiveProfile();
        Assert(active?.ExecutionFacts.TextCapable == true && active.Id == profile.Id, "saved CLI is usable");
        Assert(CoreBridge.GetSettings().TextModel == "", "native API route cleared");
        Assert(ProfileManager.TrySwitchActiveProfile(profile.Id, out _), "quick switch accepts CLI without API key");
        ProfileManager.DisableCliProfile(profile.Id);
        Assert(ProfileManager.Load().TryGetActiveProfile()?.ExecutionFacts.TextCapable == false, "disconnect disables saved engine");
        return Task.CompletedTask;
    }
    private static async Task CoordinatorRoutes()
    {
        var profile = FakeProfile(EngineExecutionKind.CodexAppServer);
        ProfileManager.SaveCliProfile(profile, profile.Id, true);
        Assert(TranslationStyleMenu.ProbeNextTextRoute() == TranslationStyleSupport.Supported, "CLI style probe must not require an API credential");
        var history = new HistoryStore(); var before = history.Load().Count;
        var coordinator = new TranslationCoordinator(history);
        var session = await coordinator.TranslateTextAsync("Hello world", "en", "zh", TranslationInputSource.Manual);
        Assert(session.Stage == TranslationSessionStage.Completed, "main coordinator failed: " + session.Error?.Message);
        Assert(session.TextExecutor == TranslationTextExecutor.CodexAppServer && session.TranslatedText == "你好，世界", "must dispatch CLI instead of native/free provider");
        Assert(session.HistoryCommitted && history.Load().Count == before + 1, "one completed session persists once");
        Assert(coordinator.EvaluateSummaryCapability().State == RouteCapabilityState.Available, "CLI summary is advertised only when executable");
        var summary = await coordinator.RunTextTaskAsync("Hello world", "en", "zh", TextTaskKind.Summarize);
        Assert(summary.CliDiagnostics is not null && summary.Diagnostics is null, "summary must dispatch CLI");
        var recognizedSession = new TranslationSession { TextEngineProfile = profile.Clone() };
        var method = typeof(TranslationCoordinator).GetMethod("TranslateRecognizedTextAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var settings = CoreBridge.GetSettings();
        var task = (Task<TranslationResponse>)method.Invoke(coordinator, new object?[] { recognizedSession, "Hello world", "en", "zh", settings,
            profile.ToProviderSettings(settings), null, 0L, null, null, CancellationToken.None })!;
        var recognized = await task;
        Assert(recognized.CliDiagnostics is not null && recognizedSession.TextExecutor == TranslationTextExecutor.CodexAppServer, "recognized OCR text must dispatch CLI");
    }
    private static async Task DisconnectActive()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        ProfileManager.SaveCliProfile(profile, null, true);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "hang");
        var stream = CliTranslationExecutor.Stream(profile, "hello", "en", "zh", null, CancellationToken.None);
        await Task.Delay(500);
        ProfileManager.DisableCliProfile(profile.Id);
        var failed = false;
        try { await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { failed = true; }
        Assert(failed, "disconnect must terminate ongoing CLI jobs");
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
    }
    private static async Task DisconnectQueued()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        ProfileManager.SaveCliProfile(profile, null, true);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "hang");
        var stream = CliTranslationExecutor.Stream(profile, "hello", "en", "zh", null, CancellationToken.None);
        ProfileManager.DisableCliProfile(profile.Id);
        var failed = false;
        try { await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { failed = true; }
        Assert(failed, "disconnect must fence a request even before its worker starts");
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
    }
    private static async Task Live(string tool)
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", null);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
        var entry = CliDiscovery.Discover(tool).First();
        var kind = tool == "codex" ? EngineExecutionKind.CodexAppServer : EngineExecutionKind.OmpRpc;
        var connection = new CliConnectionSettings { ExecutablePath = entry.ExecutablePath, Enabled = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var backend = CliTranslationExecutor.CreateBackend(kind, connection);
        var status = await backend.CheckAccountAsync(timeout.Token);
        var models = await backend.ListModelsAsync(timeout.Token);
        Console.WriteLine($"LIVE {tool}: state={status.State}, models={models.Count}, version={connection.ExpectedVersion}");
        if (backend is OmpRpcBackend ompBackend)
        {
            var providers = await ompBackend.ListLoginProvidersAsync(timeout.Token);
            Assert(providers.Count > 0, "installed OMP must return login providers");
            Console.WriteLine($"LIVE OMP: login providers={providers.Count}; authenticated={providers.Count(p => p.Authenticated)}; Antigravity listed={providers.Any(p => p.Id == "google-antigravity")}");
        }
        Assert(models.Count > 0, "no available models");
        var model = tool == "codex" ? models.FirstOrDefault(m => m.IsDefault) ?? models[0] :
            models.FirstOrDefault(m => m.Provider == "zhipu-coding-plan" && m.Id == "glm-4.7") ?? models[0];
        connection.ModelId = model.Id; connection.UpstreamProviderId = model.Provider;
        var profile = new ProviderProfile { Id = "live-" + tool, Name = tool, ExecutionKind = EngineExecutionKindNames.Format(kind),
            CliConnection = connection, TextModel = model.Id, SupportsVision = false };
        var response = await CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, timeout.Token).Completion;
        Assert(!response.Result.IsPartial && response.Result.Warnings.Count == 0 && !string.IsNullOrWhiteSpace(response.Result.TranslatedText), "real translation must complete");
        Console.WriteLine($"LIVE {tool}: {response.Result.TranslatedText} ({response.ElapsedMs} ms)");
        Console.WriteLine($"LIVE {tool}: preparation={response.CliDiagnostics?.RequestPreparationMs} ms; submit-to-first-text={response.CliDiagnostics?.FirstProtocolDeltaMs - response.CliDiagnostics?.RequestPreparationMs} ms");
    }
    private static async Task ProbeOmp()
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", null);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_VERSION", null);
        var entry = CliDiscovery.Discover(CliDiscovery.OmpToolId).First();
        var connection = new CliConnectionSettings { ExecutablePath = entry.ExecutablePath, ExpectedVersion = "stale-saved-version", Enabled = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var backend = new OmpRpcBackend(connection);
        var providers = await backend.ListLoginProvidersAsync(timeout.Token);
        var models = await backend.ListModelsAsync(timeout.Token);
        Assert(providers.Count > 0 && models.Count > 0 && connection.ExpectedVersion != "stale-saved-version", "actual RPC handshake and catalogs must work with old saved metadata");
        Assert(backend.PromptCount == 0, "probe never sends text for inference");
        Console.WriteLine($"OMP PROBE: observed={connection.ExpectedVersion}; providers={providers.Count}; models={models.Count}; inference prompts=0");
    }
    private static int FakeServer(bool omp)
    {
        var scenario = Environment.GetEnvironmentVariable("POPGLOT_FAKE_SCENARIO");
        var instructions = "";
        if (omp)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "--system-prompt");
            if (index >= 0) instructions = File.ReadAllText(args[index + 1]);
        }
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var root = JsonDocument.Parse(line).RootElement;
            var id = root.TryGetProperty("id", out var i) ? i.Clone() : default;
            var method = root.TryGetProperty(omp ? "type" : "method", out var m) ? m.GetString() : "";
            if (id.ValueKind == JsonValueKind.Undefined) continue;
            void Reply(object? data) => Emit(omp ? new { id, type = "response", command = method, success = true, data } : (object)new { id, result = data });
            switch (method)
            {
                case "initialize": Reply(new { userAgent = "fixture" }); break;
                case "config/read": Reply(new { config = new { mcp_servers = new { } } }); break;
                case "account/read": Reply(new { account = new { type = "chatgpt" }, requiresOpenaiAuth = true }); break;
                case "model/list": Reply(new { data = new[] { new { model = "fixture", displayName = "fixture", isDefault = true } } }); break;
                case "get_available_models": Reply(new { models = new[] { new { id = "fixture", name = "fixture", provider = "fixture-provider" } } }); break;
                case "get_login_providers": Reply(new { providers = new[] {
                    new { id = "google-antigravity", name = "Google Antigravity", available = true, authenticated = true },
                    new { id = "unavailable", name = "Unavailable", available = false, authenticated = false },
                    new { id = "anthropic", name = "Anthropic", available = true, authenticated = false } } }); break;
                case "login":
                    if (scenario == "login_preinput") { Emit(new { type = "extension_ui_request", id = "auth-ui", method = "input", title = "Account" }); break; }
                    Emit(new { type = "extension_ui_request", id = "auth-url", method = "open_url",
                        url = scenario == "login_unsafe" ? "https://accounts.google.com.evil.test/oauth" : "https://accounts.google.com/o/oauth2/v2/auth?state=fixture", instructions = "fixture device instructions" });
                    if (scenario is "login_hang" or "login_policy") { Thread.Sleep(30000); break; }
                    if (scenario == "login_input")
                    {
                        Emit(new { type = "extension_ui_request", id = "auth-ui", method = "input", title = "Paste callback" });
                        using var answer = JsonDocument.Parse(Console.ReadLine()!);
                        Assert(answer.RootElement.GetProperty("type").GetString() == "extension_ui_response" &&
                            answer.RootElement.GetProperty("id").GetString() == "auth-ui" &&
                            answer.RootElement.GetProperty("value").GetString() == "fixture-oauth-code", "OAuth callback matches UI request id");
                    }
                    if (scenario == "login_error") Emit(new { id, type = "response", command = "login", success = false, error = "fixture auth failure" });
                    else Reply(new { providerId = "google-antigravity" });
                    break;
                case "get_state": Reply(scenario == "incompatible_state" ? new { unsupported = true } : (object)new { dumpTools = scenario == "tools" ? new[] { new { name = "bash" } } : Array.Empty<object>() }); break;
                case "set_model": case "set_thinking_level": Reply(null); break;
                case "thread/start":
                    instructions = root.GetProperty("params").GetProperty("baseInstructions").GetString()!;
                    Assert(root.GetProperty("params").GetProperty("environments").GetArrayLength() == 0, "no environment");
                    Reply(new { thread = new { id = "thread" } }); break;
                case "turn/start": case "prompt":
                    if (scenario == "crash") return 3;
                    Reply(omp ? null : new { turn = new { id = "turn" } });
                    if (scenario == "http_error")
                    {
                        Emit(new { type = "message_end", message = new { stopReason = "error", errorMessage = "HTTP 429 private-upstream-text" } });
                        Emit(new { type = "agent_end", isTerminal = true }); break;
                    }
                    if (scenario == "hang") { Thread.Sleep(30000); break; }
                    if (scenario == "late_error") { Emit(new { id, type = "response", command = "prompt", success = false, error = "fixture" }); break; }
                    var marker = System.Text.RegularExpressions.Regex.Match(instructions, @"PGMETA-[a-f0-9-]+").Value;
                    var metadata = "\n" + marker + "\n{\"explanation\":\"\",\"warnings\":[]}";
                    if (omp)
                    {
                        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", delta = "你好，" } });
                        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", delta = "世界" } });
                        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", delta = metadata } });
                        Emit(new { type = "message_end", message = new { stopReason = "stop" } });
                        Emit(new { type = "agent_end", isTerminal = true });
                    }
                    else
                    {
                        Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "thread", turnId = "turn", itemId = "answer", delta = "你好，" } });
                        Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "thread", turnId = "turn", itemId = "answer", delta = "世界" } });
                        Emit(new { method = "item/agentMessage/delta", @params = new { threadId = "thread", turnId = "turn", itemId = "answer", delta = metadata } });
                        Emit(new { method = "turn/completed", @params = new { threadId = "thread", turn = new { id = "turn", status = "completed" } } });
                    }
                    break;
                default: Reply(null); break;
            }
        }
        return 0;
    }
    private static void Emit(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
}
