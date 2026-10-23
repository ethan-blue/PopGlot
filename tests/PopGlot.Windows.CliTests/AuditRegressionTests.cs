using PopGlot.Windows;
using PopGlot.Windows.Services.AiConnections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.CliTests;

internal static partial class Program
{
    private static async Task OmpPromptTerminals()
    {
        foreach (var scenario in new[] { "terminal_error", "terminal_aborted", "terminal_wrong_id", "terminal_eof", "terminal_length" })
            await FailureFake(scenario);
        foreach (var scenario in new[] { "multi_run", "terminal_only", "legacy_terminal" })
        {
            var profile = FakeProfile(EngineExecutionKind.OmpRpc);
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", scenario);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh-CN", null, timeout.Token).Completion;
            Assert(result.Result.TranslatedText == "你好，世界", "only the matching successful prompt terminal may complete");
        }
    }

    private static async Task WrongLanguageFinal()
    {
        var profile = FakeProfile(EngineExecutionKind.OmpRpc);
        ProfileManager.SaveCliProfile(profile,
            ProfileManager.Load().Profiles.Any(p => p.Id == profile.Id) ? profile.Id : null, true);
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "wrong_language");
        var history = new HistoryStore();
        var before = history.Load().Count;
        var coordinator = new TranslationCoordinator(history);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = await coordinator.TranslateTextAsync(
            "Please restart the application before changing the translation settings again.",
            "auto", "zh-CN", TranslationInputSource.Manual, deadline.Token);
        // 0.1.27 契约：协议有效、内容完整的“错误语言”结果是 Completed——
        // 语言启发式警告保持建议性（展示在说明区），不再把整场会话降级为
        // “内容不完整”，也不再没收历史与操作资格。
        Assert(session.Stage == TranslationSessionStage.Completed && session.IsCleanCompletion,
            "a valid protocol final with complete text is clean completion; the wrong-language warning stays advisory");
        Assert(session.TranslatedText.StartsWith("Please restart") && session.Warnings.Any(w => w.Contains("所选中文")),
            "preserves text and exposes an actionable language warning");
        Assert(session.HistoryCommitted && history.Load().Count == before + 1,
            "an advisory quality warning no longer fences history");
        var quick = new QuickSearchState();
        quick.StartNewSearch(session.SourceText);
        quick.OnSessionCompleted(session, quick.CurrentEpoch, session.SourceText);
        Assert(quick.Explanation?.Contains("所选中文") == true,
            "quick-search renders the quality warning in the explanation area");
        Assert(quick.CanSpeak && quick.CanStar, "completed content keeps the full action set");
    }

    private static async Task TokenIntegrityFinal()
    {
        var original = CoreBridge.GetSettings();
        CoreBridge.SaveSettings(original with { ProtectCodeTokens = true });
        try
        {
            var profile = FakeProfile(EngineExecutionKind.OmpRpc);
            ProfileManager.SaveCliProfile(profile,
                ProfileManager.Load().Profiles.Any(p => p.Id == profile.Id) ? profile.Id : null, true);
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "");
            var history = new HistoryStore();
            var before = history.Load().Count;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var session = await new TranslationCoordinator(history).TranslateTextAsync(
                "Call `customer_id` before retrying the operation.", "en", "zh-CN",
                TranslationInputSource.Manual, deadline.Token);
            Assert(session.Stage == TranslationSessionStage.Partial && !session.IsCleanCompletion,
                "an executor completion cannot hide protected-token loss");
            Assert(session.Warnings.Any(warning => warning.Contains("保护词")) &&
                !session.TranslatedText.Contains("customer_id"),
                "retained text exposes the genuine token loss");
            Assert(!session.HistoryCommitted && history.Load().Count == before,
                "integrity loss cannot enter history as a successful translation");
        }
        finally { CoreBridge.SaveSettings(original); }
    }

    private static async Task SettingsFieldUpdates()
    {
        var original = CoreBridge.GetSettings();
        var notified = 0;
        EventHandler handler = (_, _) => Interlocked.Increment(ref notified);
        CoreBridge.LanguagePairChanged += handler;
        try
        {
            // Block the first merge INSIDE the queue so both calls are pending
            // before either can finish. Each patch must see its predecessor.
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var first = CoreBridge.UpdateSettingsAsync(settings =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("test queue timed out");
                return settings with { SourceLanguage = "ja", TargetLanguage = "en" };
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "first update entered queue");
            var second = CoreBridge.UpdateSettingsAsync(settings => settings with { ProtectCodeTokens = !original.ProtectCodeTokens });
            release.Set();
            await Task.WhenAll(first, second);
            var saved = CoreBridge.GetSettings();
            Assert(saved.SourceLanguage == "ja" && saved.TargetLanguage == "en", "field patch retains latest language pair");
            Assert(saved.ProtectCodeTokens == !original.ProtectCodeTokens, "second patch is saved");
            Assert(notified == 1, "only a successful actual pair change notifies");
            await CoreBridge.UpdateSettingsAsync(settings => settings with { TargetLanguage = "zh-CN" });
            Assert(CoreBridge.GetSettings().SourceLanguage == "ja" && notified == 2, "language patch preserves other latest fields");
            var policy = original with { NetworkEnabled = false };
            await CoreBridge.UpdateSettingsAsync(current => SettingsWindow.MergePolicySettings(current, policy));
            Assert(CoreBridge.GetSettings().SourceLanguage == "ja" && !CoreBridge.GetSettings().NetworkEnabled,
                "saving an older policy form cannot overwrite the new language pair");
            await CoreBridge.UpdateSettingsAsync(current => SettingsWindow.MergePolicySettings(current, original));
            Assert(CoreBridge.GetSettings().SourceLanguage == "ja", "policy rollback also preserves the latest pair");
        }
        finally
        {
            CoreBridge.LanguagePairChanged -= handler;
            CoreBridge.SaveSettings(original);
        }
    }
    private static Task ProfileCommitIsolation()
    {
        var original = ProfileManager.Load();
        try
        {
            var first = new ProviderProfile { Id = "commit-first", Name = "first", ApiBaseUrl = "http://127.0.0.1:11434/v1", TextModel = "first" };
            ProfileManager.SaveApiProfile(first, null);
            var staleEdit = ProfileManager.Load().Profiles.Single(p => p.Id == first.Id).Clone();
            var second = new ProviderProfile { Id = "commit-second", Name = "second", ApiBaseUrl = "http://127.0.0.1:1234/v1", TextModel = "second" };
            ProfileManager.SaveApiProfile(second, null);
            Assert(ProfileManager.TrySwitchActiveProfile(second.Id, out _), "second profile can be selected without a health test");
            staleEdit.Name = "edited first";
            var merged = ProfileManager.SaveApiProfile(staleEdit, staleEdit.Id);
            Assert(merged.Profiles.Any(p => p.Id == second.Id) && merged.ActiveProfileId == second.Id,
                "an older editor commit preserves a newer profile and default-engine switch");
            Assert(merged.Profiles.Count(p => p.Id == first.Id) == 1 && merged.Profiles.Single(p => p.Id == first.Id).Name == "edited first",
                "editing replaces exactly one profile");
            merged.PreferFreeEngine = true;
            ProfileManager.Save(merged);
            var stillFree = ProfileManager.SaveApiProfile(second, second.Id);
            Assert(stillFree.PreferFreeEngine, "editing the remembered user engine does not silently replace the active free route");
            var cli = FakeProfile(EngineExecutionKind.OmpRpc);
            ProfileManager.SaveCliProfile(cli, null, false);
            ProfileManager.SaveCliProfile(cli, null, false);
            Assert(ProfileManager.Load().Profiles.Count(p => p.Id == cli.Id) == 1, "same CLI identity cannot be appended twice");
        }
        finally { ProfileManager.Save(original); ProfileManager.ApplyActiveToCore(original); }
        return Task.CompletedTask;
    }
}
