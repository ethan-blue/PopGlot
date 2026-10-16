using PopGlot.Windows;
using PopGlot.Windows.Services.AiConnections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.CliTests;

internal static partial class Program
{
    private static async Task OmpPromptTerminals()
    {
        foreach (var scenario in new[] { "terminal_error", "terminal_aborted", "terminal_wrong_id", "terminal_eof" })
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
        Assert(session.Stage == TranslationSessionStage.Partial && !session.IsCleanCompletion,
            "valid protocol in the wrong language is not clean completion");
        Assert(session.TranslatedText.StartsWith("Please restart") && session.Warnings.Any(w => w.Contains("所选中文")),
            "preserves text and exposes an actionable language warning");
        Assert(!session.HistoryCommitted && history.Load().Count == before,
            "quality warning fences history and clean-final side effects");
        var quick = new QuickSearchState();
        quick.StartNewSearch(session.SourceText);
        quick.OnSessionCompleted(session, quick.CurrentEpoch, session.SourceText);
        Assert(quick.Explanation?.Contains("所选中文") == true && quick.StatusText.Contains("所选中文"),
            "quick-search renders the quality warning, not just a generic incomplete status");
        Assert(!quick.CanSpeak && !quick.CanStar, "warning preserves the partial action gate");
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
}
