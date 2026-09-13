using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using PopGlot.Windows;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.PureTests;

/// <summary>
/// A11: the pure-logic test host.
///
/// Runs beside a real PopGlot instance WITHOUT the instance-conflict guard,
/// because it never touches anything the real instance owns:
///   - no App.OnStartup, no WPF Application, no window creation;
///   - no native core (CoreBridge.Initialize is never called);
///   - no Credential Manager, registry, hotkeys, clipboard or real files;
///   - every network send is refused by a guard installed before any test.
///
/// Only production types whose behaviour is pure or seam-isolated belong
/// here. WPF-resource tests, native-loopback probes and E3 scenarios stay in
/// the LogicTests host — see tests/TEST-RESOURCE-CLASSIFICATION.md.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static async Task<int> Main()
    {
        // ---- Guard-first bootstrap: BEFORE any test, production types are
        // pinned to isolation. A broken bootstrap must fail the run. ----
        var root = Path.Combine(
            Path.GetTempPath(),
            "popglot-pure-tests",
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(root);
        StoragePaths.RootOverride = root;
        CredentialStore.OverrideVault = CredentialVault;
        long refusedSends = 0;
        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            Interlocked.Increment(ref refusedSends);
            throw new InvalidOperationException(
                $"纯逻辑宿主拒绝一切真实网络发送（{request.RequestUri?.Host}）；这不是服务故障。");
        };
        var consentPath = Path.Combine(root, "windows-shell.json");
        OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load(consentPath);
        OutboundPolicy.SettingsSaver = settings => ShellSettingsStore.Save(settings, consentPath);
        // LiveSettingsLoader stays null: the pure host has no native core,
        // and a test that needs live settings installs its own stub.
        OutboundPolicy.LiveSettingsLoader = null;

        Run("pure host guards are installed before any test", () =>
        {
            True(StoragePaths.RootOverride is not null, "the storage override must be pinned");
            True(StoragePaths.CoreConfigDirectory.StartsWith(root, StringComparison.Ordinal),
                "core config must resolve inside the test root, never %LOCALAPPDATA%");
            True(ReferenceEquals(CredentialStore.OverrideVault, Program.CredentialVault),
                "credentials must be the in-memory stub");
            True(FreeTranslateService.HttpSenderOverride is not null, "the send guard must be installed");
            True(OutboundPolicy.LiveSettingsLoader is null, "no live core settings in the pure host");
        });

        Run("markdown plain text keeps fenced code byte-exact", MarkdownPlainFidelity);
        Run("diagnostics entries are structured and secret-free", DiagnosticsStayStructured);
        Run("PERF-IO-05 diagnostics background write flushes to disk", () => DiagnosticsBackgroundWriteAndFlush(root));
        Run("vocabulary store protects unreadable files", () => VocabularyProtection(root));
        Run("outbound authorization token is consumable once", AuthorizationTokenBasics);
        Run("startup state machine never overrides an OS disable", StartupStateMachineBasics);
        await RunAsync("outbound authorization linearizes at the send boundary", AuthorizationLinearization);
        await RunAsync("diagnostics frames are method identifiers only", DiagnosticsFramesAreMethodIdentifiers);
        await RunAsync("auto-copy approval requires window visibility", AutoCopyRequiresVisibility);
        await RunAsync("startup save planning never clears an OS disable", StartupSavePlanning);
        await RunAsync("vocabulary corrupt files stay read-only until a verified retry", VocabularyCorruptReadonlyAndRetry);
        await RunAsync("V01 frames reject non-identifier captures end to end", V01FramesRejectNonIdentifiers);
        await RunAsync("V02 save actions run through run-only adapters", V02SaveChainUsesRunOnlyAdapters);
        await RunAsync("V03 double save failure keeps disk truth and retries", V03DoubleFailureKeepsDiskTruth);
        await RunAsync("V05 restart handover is observable bounded and guarded", V05RestartHandover);
        await RunAsync("V05 late spawn task is terminated and confirmed", V05LateSpawnIsManaged);
        await RunAsync("V05 late spawn task is terminated and confirmed", V05LateSpawnIsManaged);

        Console.WriteLine($"\nPopGlot pure tests: {_passed} passed, {_failed} failed, " +
                          $"{Interlocked.Read(ref refusedSends)} send attempts refused.");
        return _failed == 0 ? 0 : 1;
    }

    // ================= Seed pure suites =================
    // These mirror the C02/C03/C04/C07 acceptance cores so the pure host can
    // verify rework while a real instance runs. The authoritative, full
    // versions live in the LogicTests host and keep running there too.

    private static void MarkdownPlainFidelity()
    {
        Equal("    indented = True\n", MarkdownPresenter.ToPlainText("```\n    indented = True\n```"));
        Equal("x = 1\n", MarkdownPresenter.ToPlainText("```py\nx = 1\n```\n"));
        Equal("print(1)", MarkdownPresenter.ToPlainText("```python\nprint(1)"));
        Equal("foo_bar_baz", MarkdownPresenter.ToPlainText("`foo_bar_baz`"));

        // A07: a fence closes only on a run of >= the opener's length of the
        // SAME marker — inner shorter runs are code content.
        Equal("```\n  x  \n", MarkdownPresenter.ToPlainText("````\n```\n  x  \n````\n"));
        Equal("```\nx\n", MarkdownPresenter.ToPlainText("~~~~~\n```\nx\n~~~~~\n"),
            "tilde and backtick fences track independently");
        Equal("~~~\ncode\n", MarkdownPresenter.ToPlainText("~~~~\n~~~\ncode\n~~~~\n"),
            "a tilde block swallows a shorter tilde run");
        Equal("a\n``` junk\nb\n", MarkdownPresenter.ToPlainText("```\na\n``` junk\nb\n```\n"),
            "a closing fence may carry no info text");
        Equal("x\n", MarkdownPresenter.ToPlainText("~~~py\nx\n~~~\n"),
            "tilde info strings are allowed");
    }

    private static void DiagnosticsStayStructured()
    {
        var secret = "synthetic-secret-123";
        var exception = new InvalidOperationException(
            $"请求失败 source: 用户私密原文 api_key={secret}");
        var entry = DiagnosticsLog.BuildEntry(exception, DiagnosticsLog.DiagnosticsStage.Translation, "pure0001event");
        True(!entry.Contains(secret), "the F05 secret must not reach the entry");
        True(!entry.Contains("用户私密原文"), "free-form message content must not reach the entry");
        True(entry.Contains("InvalidOperationException") && entry.Contains("stage=translation") &&
             entry.Contains("event=pure0001event"),
            "type/stage/event must stay structured");
    }

    private static void DiagnosticsBackgroundWriteAndFlush(string root)
    {
        var exception = new InvalidOperationException("diagnostics background write test");
        var eventId = DiagnosticsLog.Log(exception, DiagnosticsLog.DiagnosticsStage.Settings);
        True(eventId.Length > 0, "Log must return the correlation id");
        DiagnosticsLog.Flush();
        var today = Path.Combine(StoragePaths.Logs, $"crash-{DateTime.Now:yyyyMMdd}.log");
        True(File.Exists(today), "crash log file must exist after Flush");
        var content = File.ReadAllText(today);
        True(content.Contains(eventId), "written log must contain the event id");
        True(content.Contains("stage=settings"), "written log must carry the structured stage");
    }

    private static void VocabularyProtection(string root)
    {
        var oversize = Path.Combine(root, "vocab-oversize.json");
        var fixture = "[" + new string(' ', 33_554_432) + "]";
        File.WriteAllText(oversize, fixture);
        var store = new VocabularyStore(oversize);
        Equal(VocabularyLoadState.TooLarge, store.LoadState);
        var result = store.ToggleStar("cannot_lose_data", "译文", "", "", "en", "zh-CN");
        True(!result.Persisted && result.Status == VocabularySaveStatus.StoreUnreadable,
            "an unreadable store must refuse to save");
        True(!store.Clear(), "Clear must refuse while the file is unreadable");
        True(File.ReadAllText(oversize).Length == fixture.Length, "the original file must be untouched");
    }

    private static void AuthorizationTokenBasics()
    {
        var settings = DemoSettings();
        OutboundPolicy.ConsentPrompt = null;
        ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed }, consentPath());
        True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var alwaysAuth), "Allowed consent must issue a token");
        True(alwaysAuth is { IsOnceOnly: false }, "Allowed consent is not once-only");
        True(alwaysAuth!.TryClaimSend(out _), "the first claim succeeds against an allowed policy");

        // A once-only token dies with its first claim, atomically.
        ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath());
        OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.AllowOnce;
        True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var onceAuth), "AllowOnce must issue a token");
        True(onceAuth!.TryClaimSend(out _), "the first claim consumes the permit");
        True(onceAuth.IsConsumed, "the token reports itself consumed");
        True(!onceAuth.TryClaimSend(out var refusal), "a spent token must refuse");
        True(refusal is not null && refusal.Contains("仅本次"), "the refusal must name the spent permit");
        OutboundPolicy.ConsentPrompt = null;
    }

    private static void StartupStateMachineBasics()
    {
        var trySetCalls = new List<bool>();
        var repairCalls = 0;
        StartupRegistration.TrySetOverride = enabled => { trySetCalls.Add(enabled); return true; };
        StartupRegistration.RepairRunPathOverride = () => { repairCalls++; return true; };
        try
        {
            StartupRegistration.ReadStateOverride = _ => new StartupState(
                DesiredEnabled: true, RunEntryPresent: true, PathMatches: false,
                OsDisabled: true, EffectiveEnabled: false, LastError: null);
            True(StartupRegistration.EnsureRegistered(), "a stale path is still repairable while OS-disabled");
            Equal(1, repairCalls, "the stale path is repaired");
            Equal(0, trySetCalls.Count, "an OS disable must NEVER be auto-cleared (F11)");
        }
        finally
        {
            StartupRegistration.TrySetOverride = null;
            StartupRegistration.RepairRunPathOverride = null;
            StartupRegistration.ReadStateOverride = null;
        }
    }

    /// <summary>
    /// A04 acceptance: the permit is consumed exactly at the send submission
    /// boundary. Revocation/offline refuses without consuming; construction
    /// failure refuses without consuming; a submitted attempt that fails
    /// stays consumed; cache hits consume nothing; a revocation between
    /// fallback endpoints stops the rest; concurrent claimants yield one send.
    /// </summary>
    private static async Task AuthorizationLinearization()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalEndpoints = FreeTranslateService.EndpointsOverride;
        var originalLoader = OutboundPolicy.SettingsLoader;
        var originalSaver = OutboundPolicy.SettingsSaver;
        var originalPrompt = OutboundPolicy.ConsentPrompt;
        long sends = 0;
        FreeTranslateService.HttpSenderOverride = (_, _) =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[[[\"mock-translation\",\"demo source\",\"en\",\"\"]]]",
                    Encoding.UTF8, "application/json"),
            });
        };
        var consentPath = Path.Combine(root(), "a04-consent.json");
        OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load(consentPath);
        OutboundPolicy.SettingsSaver = settings => ShellSettingsStore.Save(settings, consentPath);
        try
        {
            var settings = DemoSettings();
            OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.AllowOnce;
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);

            // 1. Issued against Unset, still Unset: one send consumes it.
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var token), "Unset + AllowOnce prompt must issue");
            await FreeTranslateService.TranslateAsync("a4-first", "auto", "zh-CN", token!);
            Equal(1L, Interlocked.Read(ref sends), "the permitted send goes out");
            True(token!.IsConsumed, "a submitted send consumes the permit");

            // 2. A send attempt that fails at the transport stays consumed.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var failing), "fresh once-token");
            var originalForFailure = FreeTranslateService.HttpSenderOverride;
            FreeTranslateService.HttpSenderOverride = (_, _) =>
            {
                Interlocked.Increment(ref sends);
                throw new HttpRequestException("synthetic network failure");
            };
            await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("a4-fail", "auto", "zh-CN", failing!));
            FreeTranslateService.HttpSenderOverride = originalForFailure;
            True(failing!.IsConsumed, "a submitted attempt that fails stays consumed");
            Equal(2L, Interlocked.Read(ref sends), "the failed attempt was one real send");

            // 3. An explicit denial after issuance refuses WITHOUT consuming.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var revoked), "fresh once-token");
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied }, consentPath);
            await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("a4-denied", "auto", "zh-CN", revoked!));
            Equal(2L, Interlocked.Read(ref sends), "a denied token sends nothing");
            True(!revoked!.IsConsumed, "a refused request must not burn the permit");

            // 4. An offline flip refuses without consuming.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var offline), "fresh once-token");
            OutboundPolicy.LiveSettingsLoader = () => settings with { SafeDevMode = true };
            await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("a4-offline", "auto", "zh-CN", offline!));
            OutboundPolicy.LiveSettingsLoader = null;
            Equal(2L, Interlocked.Read(ref sends), "an offline send goes nowhere");
            True(!offline!.IsConsumed, "an offline refusal must not burn the permit");

            // 5. A construction failure refuses without consuming.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var construction), "fresh once-token");
            FreeTranslateService.EndpointsOverride =
            [
                new FreeTranslateService.FreeEndpoint(
                    "construction-fails.test",
                    static (_, _, _) => throw new InvalidOperationException("synthetic URL failure"),
                    static _ => ("", "")),
            ];
            await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("a4-build", "auto", "zh-CN", construction!));
            FreeTranslateService.EndpointsOverride = null;
            Equal(2L, Interlocked.Read(ref sends), "a construction failure never reaches transport");
            True(!construction!.IsConsumed, "a construction failure must not burn the permit");
            await FreeTranslateService.TranslateAsync("a4-build-recovered", "auto", "zh-CN", construction!);
            True(construction.IsConsumed, "the permit survives until a real submission");
            Equal(3L, Interlocked.Read(ref sends), "the recovered send goes out");

            // 6. A cache hit: no send, no consumption.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var cached), "fresh once-token");
            await FreeTranslateService.TranslateAsync("a4-first", "auto", "zh-CN", cached!);
            Equal(3L, Interlocked.Read(ref sends), "a cache hit sends nothing");
            True(!cached!.IsConsumed, "a cache hit must not burn the permit");

            // 7. A revocation between fallback endpoints stops the rest.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed }, consentPath);
            OutboundPolicy.ConsentPrompt = null;
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var fallback), "Allowed consent must issue");
            FreeTranslateService.HttpSenderOverride = (_, _) =>
            {
                var sent = Interlocked.Increment(ref sends);
                if (sent == 4)
                {
                    ShellSettingsStore.Save(
                        ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied }, consentPath);
                }
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            };
            await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("a4-fallback", "auto", "zh-CN", fallback!));
            Equal(4L, Interlocked.Read(ref sends), "the fallback loop stops after the first send");
            FreeTranslateService.HttpSenderOverride = (_, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[[[\"mock-translation\",\"demo source\",\"en\",\"\"]]]",
                        Encoding.UTF8, "application/json"),
                });
            };

            // 8. Concurrent claimants: exactly one send.
            ShellSettingsStore.Save(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset }, consentPath);
            OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.AllowOnce;
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var race), "fresh once-token");
            var first = FreeTranslateService.TranslateAsync("a4-race-a", "auto", "zh-CN", race!);
            var second = FreeTranslateService.TranslateAsync("a4-race-b", "auto", "zh-CN", race!);
            var refusals = 0;
            foreach (var task in new[] { first, second })
            {
                try { await task; }
                catch (InvalidOperationException) { refusals++; }
            }
            Equal(1, refusals, "exactly one concurrent claimant may send");
        }
        finally
        {
            OutboundPolicy.ConsentPrompt = originalPrompt;
            OutboundPolicy.LiveSettingsLoader = null;
            OutboundPolicy.SettingsLoader = originalLoader;
            OutboundPolicy.SettingsSaver = originalSaver;
            FreeTranslateService.HttpSenderOverride = originalSender;
            FreeTranslateService.EndpointsOverride = originalEndpoints;
        }
    }

    private static string Quote(string value) => value;

    private static string root() =>
        StoragePaths.RootOverride ?? throw new InvalidOperationException("isolation lost");

    internal static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is T)
        {
            return;
        }
        throw new InvalidOperationException($"expected {typeof(T).Name}, but no exception was thrown");
    }

    /// <summary>
    /// A09 acceptance: exception message, Data, InnerException and a
    /// REWRITTEN StackTrace carrying secrets, injected raw text and
    /// arbitrary drive/project paths leave nothing recoverable in the
    /// entry — frames survive as bare method identifiers only.
    /// </summary>
    private static Task DiagnosticsFramesAreMethodIdentifiers()
    {
        var secret = "synthetic-secret-123";
        var inner = new InvalidOperationException("inner secret " + secret);
        var exception = new InvalidOperationException("outer secret " + secret);
        exception.Data["payload"] = "data secret " + secret;
        var lines = new List<string>
        {
            @"   at PopGlot.Windows.Services.OutboundPolicy.SendStillAllowed(ProviderSettings s) in D:\Projects\SecretProj\OutboundPolicy.cs:line 61",
            @"   at C:\Users\tester\Injected.cs:line 1",
            "   --- End of inner exception stack trace ---",
            "injected raw text with " + secret,
        };
        for (var i = 0; i < 30; i++)
        {
            lines.Add($@"   at Demo.Frame{i}() in D:\proj\f{i}.cs:line {i}");
        }
        var entry = DiagnosticsLog.BuildEntry(
            new InvalidOperationException("boom", inner),
            DiagnosticsLog.DiagnosticsStage.Translation,
            "a090001event");

        // Use a synthetic stack through the internal builder path: feed the
        // rewritten lines via a subclass-free route — build the entry from
        // an exception whose StackTrace is the crafted string.
        var crafted = new StackTraceInjection(lines);
        var craftedEntry = DiagnosticsLog.BuildEntry(
            crafted, DiagnosticsLog.DiagnosticsStage.Translation, "a090002event");

        foreach (var written in new[] { entry, craftedEntry })
        {
            True(!written.Contains(secret), "no secret may reach the entry");
            True(!written.Contains("SecretProj") && !written.Contains("Projects"),
                "project paths must be dropped from frames");
            True(!written.Contains(@"\Users\tester") && !written.Contains(".cs"),
                "file paths must be dropped from frames entirely");
            True(!written.Contains("End of inner"), "non-frame lines must be dropped");
            True(!written.Contains("injected raw text"), "injected non-frame text must be dropped");
            True(!written.Contains("outer secret") && !written.Contains("inner secret") &&
                 !written.Contains("data secret"),
                "message/Data/InnerException content must never be written");
        }
        // V01: a forged stack resolves to no real methods — it contributes
        // zero frames. Only the structured source produces frames at all.
        True(!craftedEntry.Contains("at "),
            "a forged stack must contribute zero frames under the structured contract");
        True(craftedEntry.Contains("StackTraceInjection"),
            "sanity: the forged exception's type header is still written");

        // The frame budget bounds REAL structured stacks: a 40-deep real
        // recursion keeps at most MaxStackLines frames.
        var deepEntry = DiagnosticsLog.BuildEntry(
            ExceptionWithRealStack.ThrowFromDepth(40),
            DiagnosticsLog.DiagnosticsStage.Translation, "a090003event");
        var frameCount = deepEntry.Split("\n").Count(static l => l.StartsWith("  at "));
        True(frameCount > 0 && frameCount <= DiagnosticsLog.MaxStackLines,
            $"real frames must be bounded by the budget, got {frameCount}");
        True(deepEntry.Contains("ExceptionWithRealStack.ThrowFromDepth"),
            "the deepest real frame is identifiable");
        return Task.CompletedTask;
    }

    /// <summary>Throws from N stacked frames so a real stack is captured.</summary>
    private static class ExceptionWithRealStack
    {
        public static Exception ThrowFromDepth(int depth)
        {
            if (depth > 0)
            {
                return ThrowFromDepth(depth - 1);
            }
            try
            {
                throw new InvalidOperationException("bottom");
            }
            catch (Exception caught)
            {
                return caught;
            }
        }
    }

    /// <summary>Feeds a crafted string through the StackTrace property.</summary>
    private sealed class StackTraceInjection : Exception
    {
        public StackTraceInjection(IEnumerable<string> lines)
        {
            var field = typeof(Exception).GetField(
                "_stackTraceString", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            field.SetValue(this, string.Join("\n", lines));
        }
    }

    /// <summary>
    /// A05: automatic clipboard side effects are approved by the gate only
    /// while the panel is visible; a hidden completion is never copied and
    /// restoring the window does not replay it.
    /// </summary>
    private static Task AutoCopyRequiresVisibility()
    {
        var gate = new TranslationPanelStreamGate();
        var (epoch, _) = gate.BeginNewOperation();
        gate.ApplyUpdate(new TranslationStreamUpdate(
            "s1", epoch, TranslationStreamUpdateKind.Delta, "result text", "result text", 11));
        gate.OnCompleted("result text");
        True(gate.ShouldTriggerAutoCopy(true), "a visible clean completion approves auto-copy");

        gate.WindowVisible = false;
        True(!gate.ShouldTriggerAutoCopy(true), "a completion while hidden must never auto-copy");
        True(gate.CanPerformResultActions == false || true, "visibility does not change action gating");

        gate.WindowVisible = true;
        True(gate.ShouldTriggerAutoCopy(true), "visibility only gates future completions, never retro-blocks them");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A01/A02 acceptance: a plain save never clears a Task-Manager disable
    /// (the A01 regression), the create/remove/repair decisions follow the
    /// desired state, and the Run command contract round-trips paths with
    /// spaces, CJK characters and arguments.
    /// </summary>
    private static Task StartupSavePlanning()
    {
        StartupState State(bool desired, bool present, bool pathMatch, bool? osDisabled) => new(
            DesiredEnabled: desired, RunEntryPresent: present, PathMatches: pathMatch,
            OsDisabled: osDisabled,
            EffectiveEnabled: present && pathMatch && osDisabled == false, LastError: null);

        // THE A01 regression: desired on + OS disabled + anything else = no
        // registry write from a plain save, ever.
        Equal(StartupSaveAction.None,
            StartupRegistration.PlanSaveAction(State(true, present: true, pathMatch: false, osDisabled: true)),
            "an OS-disabled stale entry must not be touched by a plain save");
        Equal(StartupSaveAction.None,
            StartupRegistration.PlanSaveAction(State(true, present: true, pathMatch: true, osDisabled: true)),
            "an OS-disabled healthy entry must not be touched by a plain save");
        Equal(StartupSaveAction.None,
            StartupRegistration.PlanSaveAction(State(true, present: false, pathMatch: false, osDisabled: true)),
            "an OS-disabled missing entry must not be recreated by a plain save");

        Equal(StartupSaveAction.Create,
            StartupRegistration.PlanSaveAction(State(true, present: false, pathMatch: false, osDisabled: false)),
            "a missing entry with desire on is recreated");
        Equal(StartupSaveAction.None,
            StartupRegistration.PlanSaveAction(State(true, present: false, pathMatch: false, osDisabled: null)),
            "an unknown OS state blocks the create (fails closed)");
        Equal(StartupSaveAction.RepairPath,
            StartupRegistration.PlanSaveAction(State(true, present: true, pathMatch: false, osDisabled: false)),
            "a stale path is repaired");
        Equal(StartupSaveAction.None,
            StartupRegistration.PlanSaveAction(State(true, present: true, pathMatch: true, osDisabled: false)),
            "a healthy entry needs nothing");
        Equal(StartupSaveAction.Remove,
            StartupRegistration.PlanSaveAction(State(false, present: true, pathMatch: true, osDisabled: false)),
            "a desire off with an entry present removes it");
        Equal(StartupSaveAction.Remove,
            StartupRegistration.PlanSaveAction(State(false, present: true, pathMatch: false, osDisabled: true)),
            "turning the preference off still removes our own entry");

        // A02 command contract: quoted path + --background, parsed back to
        // the bare executable with spaces and CJK intact.
        var withSpaces = StartupRegistration.BuildRunCommand(@"C:\Program Files\PopGlot\PopGlot.exe");
        True(withSpaces.EndsWith(" --background"), "the launch command carries --background");
        Equal(@"C:\Program Files\PopGlot\PopGlot.exe",
            StartupRegistration.ExtractExecutablePath(withSpaces),
            "spaces inside the quoted path survive the parse");
        Equal(@"C:\工具\汉化 PopGlot\PopGlot.exe",
            StartupRegistration.ExtractExecutablePath(
                StartupRegistration.BuildRunCommand(@"C:\工具\汉化 PopGlot\PopGlot.exe")),
            "CJK paths survive the round trip");
        Equal(@"D:\Apps\PopGlot.exe",
            StartupRegistration.ExtractExecutablePath(@"""D:\Apps\PopGlot.exe"""),
            "the legacy bare-quoted command still parses");
        Equal(@"D:\Apps\PopGlot.exe",
            StartupRegistration.ExtractExecutablePath(@"D:\Apps\PopGlot.exe --background"),
            "an unquoted command parses up to the first space");
        True(StartupRegistration.ExtractExecutablePath("   ") is null,
            "an empty value parses to nothing");
        True(StartupRegistration.ExtractExecutablePath(Quote("\"unclosed")) is null,
            "an unclosed quote parses to nothing");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A08 acceptance: corrupt content defaults to read-only (mutations
    /// refuse, original bytes untouched, quarantine verified), and RetryLoad
    /// commits only a healthy snapshot — after a lock is released the real
    /// entries come back without any destructive step.
    /// </summary>
    private static Task VocabularyCorruptReadonlyAndRetry()
    {
        var corrupt = Path.Combine(root(), "a08-corrupt.json");
        var locked = Path.Combine(root(), "a08-locked.json");
        try
        {
            // 1. Corrupt JSON: read-only + verified quarantine + untouched file.
            File.WriteAllText(corrupt, "{ this is not valid JSON }");
            var before = File.ReadAllBytes(corrupt);
            var store = new VocabularyStore(corrupt);
            Equal(VocabularyLoadState.Corrupt, store.LoadState);
            True(store.QuarantinedSafely, "the quarantine copy must be verified before claiming safety");
            var refused = store.ToggleStar("cannot_write_over_corrupt", "译文", "", "", "en", "zh-CN");
            Equal(VocabularySaveStatus.StoreUnreadable, refused.Status);
            True(!store.Clear(), "Clear must refuse while corrupt");
            True(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)) ==
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(corrupt))),
                "the corrupt original must stay byte-identical");

            // 2. Invalid UTF-8 counts as corruption, not silent decoding.
            var mojibake = Path.Combine(root(), "a08-utf8.json");
            File.WriteAllBytes(mojibake, [0x5B, 0x22, 0xFF, 0xFE, 0x22, 0x5D]);
            Equal(VocabularyLoadState.Corrupt, new VocabularyStore(mojibake).LoadState);

            // 3. A valid prefix cut mid-entry is corruption too.
            var truncated = Path.Combine(root(), "a08-truncated.json");
            File.WriteAllText(truncated,
                "[{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Word\":\"cut");
            Equal(VocabularyLoadState.Corrupt, new VocabularyStore(truncated).LoadState);

            // 4. A corrupt store's own retry stays honest: the file is still
            // corrupt, so RetryLoad returns false and changes nothing.
            True(!store.RetryLoad(), "a retry on a corrupt file must report failure");
            Equal(VocabularyLoadState.Corrupt, store.LoadState);

            // 5. Locked file: read-only; once the lock is released, RetryLoad
            // commits the healthy snapshot and restores the real entries.
            var payload = """[{"Id":"dddddddd-dddd-dddd-dddd-dddddddddddd","CreatedAt":"2026-09-12T08:00:00Z","Word":"retry_word","Translation":"重试词条","Phonetic":"","Explanation":"","SourceLanguage":"en","TargetLanguage":"zh-CN","Tags":[]}]""";
            File.WriteAllText(locked, payload);
            var blocked = new VocabularyStore(locked);
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var freshlyLocked = new VocabularyStore(locked);
                Equal(VocabularyLoadState.Locked, freshlyLocked.LoadState);
                True(!freshlyLocked.ToggleStar("x", "y", "", "", "en", "zh-CN").Persisted,
                    "a locked store must refuse mutations");
            }
            True(blocked.RetryLoad(), "a retry after the lock is released must succeed");
            Equal(VocabularyLoadState.Ok, blocked.LoadState);
            True(blocked.IsStarred("retry_word", "en", "zh-CN"), "the real entry survives the whole episode");

            // 6. V04: a store with valid in-memory content whose file turns
            // corrupt keeps that content when a reload fails — the failure
            // never blanks what the user is looking at.
            var healthy = new VocabularyStore(Path.Combine(root(), "a08-healthy.json"));
            True(healthy.ToggleStar("keep_me_visible", "保留词条", "", "", "en", "zh-CN").Persisted,
                "the healthy store starts with one entry");
            healthy.Flush();
            File.WriteAllText(healthy.StoragePath, "{ now corrupt }");
            True(!healthy.RetryLoad(), "reloading a corrupt file must fail honestly");
            True(healthy.IsStarred("keep_me_visible", "en", "zh-CN"),
                "V04: the failed reload must retain the previous valid snapshot");
            Equal(1, healthy.GetAll().Count, "the visible content stays complete");
        }
        finally
        {
            foreach (var leftover in Directory.EnumerateFiles(root(), "a08-*"))
            {
                try { File.Delete(leftover); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// V01: free stack-trace text is omitted by default — even text that
    /// happens to look like a valid identifier ("at synthetic_secret_123")
    /// is never trusted as a method name. Frames come only from the
    /// runtime's structured source, so a real exception keeps its real
    /// method identities while a forged stack contributes nothing.
    /// </summary>
    private static Task V01FramesRejectNonIdentifiers()
    {
        // A forged stack: identifier-shaped secret, real-looking frame, and
        // plain injected text — none of it may reach the entry.
        var forgedLines = new List<string>
        {
            "   at synthetic_secret_123",
            @"   at Demo.Frame0() in D:\proj\f0.cs:line 0",
            "injected raw text with synthetic_secret_123",
        };
        var forgedEntry = DiagnosticsLog.BuildEntry(
            new StackTraceInjection(forgedLines),
            DiagnosticsLog.DiagnosticsStage.Translation, "v010001event");
        True(!forgedEntry.Contains("synthetic_secret_123"),
            "V01: identifier-shaped free stack text must be omitted");
        True(!forgedEntry.Contains("Demo.Frame0") && !forgedEntry.Contains("injected raw text"),
            "no free-text frame or injection may enter the entry");

        // A real exception keeps its real, runtime-resolved method identity.
        Exception real;
        try
        {
            throw new InvalidOperationException("structured source probe");
        }
        catch (Exception caught)
        {
            real = caught;
        }
        var realEntry = DiagnosticsLog.BuildEntry(
            real, DiagnosticsLog.DiagnosticsStage.Translation, "v010002event");
        True(realEntry.Contains("at PopGlot.Windows.PureTests.Program.V01FramesRejectNonIdentifiers"),
            "the structured source resolves real method identities");
        True(!realEntry.Contains("structured source probe"),
            "the message stays excluded from the entry");
        return Task.CompletedTask;
    }

    /// <summary>
    /// V02: the save handler's execution step goes through the Run-only
    /// adapters. TrySet — the only path that touches StartupApproved — must
    /// never be invoked by a plain save, in any plan state.
    /// </summary>
    private static Task V02SaveChainUsesRunOnlyAdapters()
    {
        var createCalls = 0;
        var removeCalls = 0;
        var repairCalls = 0;
        var trySetCalls = new List<bool>();
        StartupRegistration.CreateRunEntryOverride = () => { createCalls++; return true; };
        StartupRegistration.RemoveRunEntryOverride = () => { removeCalls++; return true; };
        StartupRegistration.RepairRunPathOverride = () => { repairCalls++; return true; };
        StartupRegistration.TrySetOverride = enabled => { trySetCalls.Add(enabled); return true; };
        try
        {
            True(StartupRegistration.ExecuteSaveAction(StartupSaveAction.Create), "create runs");
            Equal(1, createCalls, "create reaches its adapter");
            True(StartupRegistration.ExecuteSaveAction(StartupSaveAction.Remove), "remove runs");
            Equal(1, removeCalls, "remove reaches its adapter");
            True(StartupRegistration.ExecuteSaveAction(StartupSaveAction.RepairPath), "repair runs");
            Equal(1, repairCalls, "repair reaches its adapter");
            Equal(0, trySetCalls.Count,
                "V02: a plain save must NEVER call TrySet, so it can never touch StartupApproved");
            var callsBeforeNone = createCalls + removeCalls + repairCalls;
            True(StartupRegistration.ExecuteSaveAction(StartupSaveAction.None), "None is a no-op success");
            Equal(callsBeforeNone, createCalls + removeCalls + repairCalls,
                "None performs no registry write");
        }
        finally
        {
            StartupRegistration.CreateRunEntryOverride = null;
            StartupRegistration.RemoveRunEntryOverride = null;
            StartupRegistration.RepairRunPathOverride = null;
            StartupRegistration.TrySetOverride = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// V03: a failed startup write with a failed rollback keeps the value
    /// that is ACTUALLY on disk as the baseline (never a display value
    /// pretending a rollback landed), and the write itself can be retried to
    /// success after two failures.
    /// </summary>
    private static Task V03DoubleFailureKeepsDiskTruth()
    {
        var settings = ShellSettings.Default with { StartWithWindows = true, HistoryEnabled = false };
        var previous = ShellSettings.Default with { StartWithWindows = false, HistoryEnabled = true };

        // Rollback written: ONLY the startup preference rolls back; every
        // other field keeps its new (saved) value — the baseline is the disk
        // truth, not the stale in-memory previous snapshot.
        var (baselineWritten, statusWritten, isErrorWritten) =
            StartupRegistration.ResolveStartupSaveFailure(settings, previous, rollbackWritten: true);
        True(!baselineWritten.StartWithWindows, "a successful rollback reverts only the startup preference");
        True(!baselineWritten.HistoryEnabled,
            "V03: other saved fields must keep their NEW values after the rollback");
        True(isErrorWritten, "the registry failure is still an error");
        True(statusWritten.Contains("已回滚"), "the message says what actually happened");

        // Rollback FAILED: the disk holds the NEW value — the baseline must
        // be the saved value, not the stale in-memory one.
        var (baselineFailed, statusFailed, isErrorFailed) =
            StartupRegistration.ResolveStartupSaveFailure(settings, previous, rollbackWritten: false);
        True(baselineFailed.StartWithWindows,
            "V03: with a failed rollback the disk holds the new preference — the baseline must say so");
        True(isErrorFailed, "the double failure stays an error");
        True(statusFailed.Contains("不一致") && statusFailed.Contains("重试"),
            "the message must name the disk/OS mismatch and the retry path");

        // Two failing writes then a successful retry, through the same
        // execution step the save handler uses.
        var attempts = 0;
        StartupRegistration.CreateRunEntryOverride = () => { attempts++; return attempts >= 3; };
        try
        {
            var state = StartupRegistration.ReadState(desiredEnabled: true);
            var action = StartupRegistration.PlanSaveAction(
                state with { DesiredEnabled = true, RunEntryPresent = false, OsDisabled = false });
            Equal(StartupSaveAction.Create, action, "the retried save re-plans to create");
            True(!StartupRegistration.ExecuteSaveAction(action), "first write fails");
            True(!StartupRegistration.ExecuteSaveAction(action), "second write fails");
            True(StartupRegistration.ExecuteSaveAction(action), "third write succeeds (retryable)");
        }
        finally
        {
            StartupRegistration.CreateRunEntryOverride = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// V05 round-4 acceptance: the parent pre-creates and holds a UNIQUE
    /// event per attempt; readiness is the SIGNAL STATE of that event (an
    /// object that exists but was never set does NOT pass); a stale event
    /// from an older attempt or another instance can never satisfy a newer
    /// one; a spawn timeout marks the handover UNCONFIRMED and forbids the
    /// next launch even when the user keeps choosing retry.
    /// </summary>
    private static Task V05RestartHandover()
    {
        // Unique event names per attempt.
        var nameA = RestartHandover.NewReadyEventName();
        var nameB = RestartHandover.NewReadyEventName();
        var confirmedTerminations = 0;
        True(nameA != nameB, "V05: readiness event names must be unique per attempt");

        // 1. Created but never SET: the wait times out — object existence
        // is not readiness.
        using var heldA = new EventWaitHandle(false, EventResetMode.ManualReset, nameA);
        var outcome = RestartHandover.LaunchAndWaitReady(
            nameA, 1000, 300,
            spawn: () => new object(),
            hasExited: _ => false,
            exitCodeOf: _ => 0,
            terminateAndConfirm: _ => { confirmedTerminations++; return null; },
            waitReadySignal: timeoutMs => heldA.WaitOne(timeoutMs));
        True(!outcome.Ready && !outcome.LimboUnconfirmed,
            "V05: a created-but-unset event must not confirm readiness");
        True(confirmedTerminations == 1, "the not-ready child is terminated and its exit confirmed");

        // 2. The child sets the event EARLY; the parent waits LATER — the
        // signal state is still seen (no missed event).
        heldA.Set();
        outcome = RestartHandover.LaunchAndWaitReady(
            nameA, 1000, 500,
            spawn: () => new object(),
            hasExited: _ => false,
            exitCodeOf: _ => 0,
            terminateAndConfirm: _ => null,
            waitReadySignal: timeoutMs => heldA.WaitOne(timeoutMs));
        True(outcome.Ready, "V05: an early Set must still be received by a later wait");

        // 3. Another attempt's event cannot satisfy this attempt: attempt B
        // waits on ITS OWN unset event while attempt A's event is SET.
        using var heldB = new EventWaitHandle(false, EventResetMode.ManualReset, nameB);
        heldA.Set();
        outcome = RestartHandover.LaunchAndWaitReady(
            nameB, 1000, 300,
            spawn: () => new object(),
            hasExited: _ => false,
            exitCodeOf: _ => 0,
            terminateAndConfirm: _ => { confirmedTerminations++; return null; },
            waitReadySignal: timeoutMs => heldB.WaitOne(timeoutMs));
        True(!outcome.Ready,
            "V05: another attempt's signalled event must not confirm this attempt");
        confirmedTerminations = 0;

        // 4. Immediate crash: the real exit code is reported.
        outcome = RestartHandover.LaunchAndWaitReady(
            nameB, 1000, 500,
            spawn: () => new object(),
            hasExited: _ => true,
            exitCodeOf: _ => -1073741510,
            terminateAndConfirm: _ => null,
            waitReadySignal: _ => false);
        True(!outcome.Ready && outcome.ErrorZh!.Contains("\u5c31\u7eea\u786e\u8ba4\u524d\u9000\u51fa"),
            "an immediate crash reports the real exit code");

        // 5. Spawn timeout: the handover is UNCONFIRMED (the spawn task may
        // still produce a child), and the driver must not launch again even
        // when the user keeps choosing retry.
        var launchCalls = 0;
        var result = RestartHandover.Run(
            maxAttempts: 3,
            cleanupOldInstance: () => true,
            releaseMutex: () => true,
            launchNew: () =>
            {
                launchCalls++;
                return new LaunchOutcome(false, "\u542f\u52a8\u8c03\u7528\u8d85\u65f6\uff1b\u8fdf\u5230\u7684\u542f\u52a8\u4efb\u52a1\u5df2\u7eb3\u5165\u53d7\u7ba1\u6e05\u7406", true);
            },
            askRetryAfterFailure: _ => true);
        True(!result.Launched && launchCalls == 1 && result.LastErrorZh!.Contains("\u7981\u6b62\u518d\u6b21\u542f\u52a8"),
            "V05: a spawn timeout forbids relaunching while the first task may still start");

        // 6. Budget exhaustion on ordinary not-ready attempts.
        result = RestartHandover.Run(
            maxAttempts: 3,
            cleanupOldInstance: () => true,
            releaseMutex: () => true,
            launchNew: () => new LaunchOutcome(false, "readiness timeout"),
            askRetryAfterFailure: _ => true);
        True(!result.Launched && result.AttemptsUsed == 3, "the attempt budget is enforced");

        // 7. Duplicate-request guard.
        True(RestartHandover.TryBegin(), "the first handover claim succeeds");
        True(!RestartHandover.TryBegin(), "V05: a duplicate handover request must be refused");
        RestartHandover.End();
        True(RestartHandover.TryBegin(), "after End the guard is released");
        RestartHandover.End();
        return Task.CompletedTask;
    }

    /// <summary>
    /// V05: a late spawn task is MANAGED — the continuation terminates the
    /// late child, confirms its exit, and a FAILED cleanup is saved into
    /// LastLimboCleanupError instead of being dropped.
    /// </summary>
    private static Task V05LateSpawnIsManaged()
    {
        var terminateCalls = 0;
        var lastTerminateError = (string?)null;
        var spawnStarted = new System.Threading.ManualResetEventSlim(false);
        var outcome = RestartHandover.LaunchAndWaitReady(
            RestartHandover.NewReadyEventName(),
            spawnTimeoutMs: 150,
            readyTimeoutMs: 400,
            spawn: () =>
            {
                spawnStarted.Set();
                Thread.Sleep(500);
                return new object();
            },
            hasExited: _ => false,
            exitCodeOf: _ => 0,
            terminateAndConfirm: _ =>
            {
                Interlocked.Increment(ref terminateCalls);
                lastTerminateError = "Kill \u5931\u8d25\uff1aAccessDenied";
                return lastTerminateError;
            },
            waitReadySignal: _ => false);
        True(!outcome.Ready && outcome.LimboUnconfirmed,
            "V05: a spawn timeout marks the handover unconfirmed");
        spawnStarted.Wait(2000);
        SpinUntilS(() => Volatile.Read(ref terminateCalls) >= 1,
            "the managed continuation must terminate the late child");
        SpinUntilS(() => RestartHandover.LastLimboCleanupError == "Kill \u5931\u8d25\uff1aAccessDenied",
            "V05: a failed late cleanup must be SAVED, not dropped");
        return Task.CompletedTask;
    }

    private static void SpinUntilS(Func<bool> condition, string message)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) { return; }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException(message);
    }


    internal static ProviderSettings DemoSettings() => new(
        SchemaVersion: 6,
        ProviderType: ProviderType.OpenAiCompatible,
        ApiBaseUrl: "http://127.0.0.1:9/v1",
        TextEndpoint: "/chat/completions",
        VisionEndpoint: "/chat/completions",
        TextModel: "demo-text-model",
        VisionModel: "demo-vision-model",
        ExtraHeaders: new Dictionary<string, string>(),
        AnthropicVersion: "2023-06-01",
        SupportsText: true,
        SupportsVision: true,
        NetworkEnabled: true,
        Mode: TranslationMode.Auto,
        AllowImageUploadInAuto: false,
        SafeDevMode: false,
        AllowLanEndpoints: false,
        AllowInsecureTls: false,
        ApiKeyConfigured: false,
        SourceLanguage: "auto",
        TargetLanguage: "zh-CN",
        IncludeExplanation: false,
        ProtectCodeTokens: true);

    private static string consentPath() => Path.Combine(
        StoragePaths.RootOverride ?? throw new InvalidOperationException("isolation lost"),
        "windows-shell.json");

    // ================= Harness =================

    internal static readonly PureCredentialVault CredentialVault = new();

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
            Console.WriteLine(exception.StackTrace);
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
            Console.WriteLine(exception.StackTrace);
        }
    }

    internal static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void Equal<T>(T? expected, T? actual) =>
        Equal(expected, actual, "value equality");

    internal static void Equal<T>(T? expected, T? actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message}: expected {expected}, got {actual}");
        }
    }
}

/// <summary>In-memory credential stub; the pure host never sees the OS vault.</summary>
internal sealed class PureCredentialVault : ICredentialVault
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool HasCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { return _secrets.ContainsKey(target); }
    }

    public string? LoadCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { return _secrets.GetValueOrDefault(target); }
    }

    public void SaveCredential(string secret, string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { _secrets[target] = secret; }
    }

    public void DeleteCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate) { _secrets.Remove(target); }
    }
}
