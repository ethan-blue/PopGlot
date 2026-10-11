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
        { Console.WriteLine(Environment.GetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL") == "omp" ? "18.2.11" : "codex-cli 0.150.1"); return 0; }
        if (args.Contains("app-server") || args.Contains("rpc")) return FakeServer(args.Contains("rpc"));
        var root = Path.Combine(Path.GetTempPath(), $"popglot-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        StoragePaths.RootOverride = root;
        Environment.SetEnvironmentVariable("POPGLOT_DATA_ROOT", root);
        CoreBridge.Initialize(root);
        var settings = CoreBridge.GetSettings();
        CoreBridge.SaveSettings(settings with { NetworkEnabled = true, SafeDevMode = false });
        try
        {
            Check("Codex protocol + FFI stream", () => StreamFake(EngineExecutionKind.CodexAppServer));
            Check("OMP protocol + FFI stream", () => StreamFake(EngineExecutionKind.OmpRpc));
            Check("late OMP prompt error is not success", () => FailureFake("late_error"));
            Check("process crash completes turn", () => FailureFake("crash"));
            Check("no tool access accepted", () => FailureFake("tools"));
            Check("cancel kills owned process", CancellationFake);
            Check("offline gate: zero processes/prompts", Offline);
            Check("save, reload, switch, disable", Persistence);
            Check("main translation/history/summary/recognized-text route", CoordinatorRoutes);
            Check("disconnect cancels active saved connection", DisconnectActive);
            Check("immediate disconnect fences queued request", DisconnectQueued);
            if (args.Contains("--live"))
            {
                Check("installed Codex login/models/translation", () => Live("codex"));
                Check("installed OMP zero tools/models/translation", () => Live("omp"));
            }
            if (args.Contains("--ui")) Check("WPF connection screen and buttons", () => { UiScenario.Run(root); return Task.CompletedTask; });
        }
        finally { Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", null); Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null); }
        Console.WriteLine($"RESULT {_passes} passed / {_failures} failed; isolated data: {root}");
        return _failures == 0 ? 0 : 1;
    }
    private static void Check(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); _passes++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
    }
    internal static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ProviderProfile FakeProfile(EngineExecutionKind kind)
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", kind == EngineExecutionKind.OmpRpc ? "omp" : "codex");
        return new() { Id = "fake-" + kind, Name = "fixture", ApiBaseUrl = "", CredentialTarget = "", SupportsVision = false,
            ExecutionKind = EngineExecutionKindNames.Format(kind), TextModel = "fixture",
            CliConnection = new() { ExecutablePath = Environment.ProcessPath!, ExpectedVersion = CliLauncher.ProtocolVersion(kind), ModelId = "fixture", UpstreamProviderId = "fixture-provider", Enabled = true } };
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
                case "get_state": Reply(new { dumpTools = scenario == "tools" ? new[] { new { name = "bash" } } : Array.Empty<object>() }); break;
                case "set_model": case "set_thinking_level": Reply(null); break;
                case "thread/start":
                    instructions = root.GetProperty("params").GetProperty("baseInstructions").GetString()!;
                    Assert(root.GetProperty("params").GetProperty("environments").GetArrayLength() == 0, "no environment");
                    Reply(new { thread = new { id = "thread" } }); break;
                case "turn/start": case "prompt":
                    if (scenario == "crash") return 3;
                    Reply(omp ? null : new { turn = new { id = "turn" } });
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
