using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static void RuntimeQuickSearchTerminalsRejectLateCallbacks()
    {
        foreach (var terminal in new[]
        {
            TranslationSessionStage.Completed, TranslationSessionStage.Partial,
            TranslationSessionStage.Failed, TranslationSessionStage.Cancelled,
        })
        {
            var state = new QuickSearchState();
            state.StartNewSearch("query");
            var epoch = state.CurrentEpoch;
            state.OnStreamUpdate(new TranslationStreamUpdate("s", epoch,
                TranslationStreamUpdateKind.Delta, "kept", "kept", 4), "query");
            True(state.OnSessionCompleted(new TranslationSession
            {
                Stage = terminal,
                TranslatedText = "kept",
            }, epoch, "query"), "the first terminal must be accepted");
            var stage = state.Stage;
            var status = state.StatusText;
            True(!state.OnStreamUpdate(new TranslationStreamUpdate("s", epoch,
                TranslationStreamUpdateKind.Delta, "late", "late", 4), "query"),
                "same-epoch delta must not reopen a terminal");
            True(!state.OnStageChanged(TranslationSessionStage.Finalizing, epoch, "query"),
                "same-epoch finalizing must not reopen a terminal");
            True(!state.OnException(new InvalidOperationException("late"), epoch, "query"),
                "a queued failure must not overwrite a terminal");
            True(!state.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                TranslatedText = "late",
            }, epoch, "query"), "duplicate terminal must be rejected");
            Equal(stage, state.Stage);
            Equal(status, state.StatusText);
            Equal("kept", state.AccumulatedText);
        }
    }

    private static async Task RuntimeCancelledPumpIsBounded()
    {
        using var cts = new CancellationTokenSource();
        var completion = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        TranslationStreamBuffer? buffer = null;
        var executor = new FakeTranslationExecutor
        {
            OnStreamText = (_, _, _, _, sessionId, epoch, _) =>
            {
                buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
                buffer.TryAppend("received");
                return new TranslationStreamSession(buffer, completion.Task);
            },
        };
        var history = new FakeHistoryRepository();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);
        var updates = new List<TranslationStreamUpdate>();
        var request = coordinator.TranslateTextAsync("source", "en", "zh",
            TranslationInputSource.Manual, cts.Token,
            progress: new SynchronousProgress<TranslationStreamUpdate>(updates.Add));
        True(buffer is not null, "the injected stream must have started");
        cts.Cancel();
        buffer!.TryAppend("late");
        var session = await request.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(TranslationSessionStage.Cancelled, session.Stage);
        True(!updates.Any(update => update.Delta.Contains("late", StringComparison.Ordinal)),
            "cancelled pump must not publish its final drain");
        Equal(0, history.Entries.Count);
        True(!completion.Task.IsCompleted, "the coordinator must not depend on a cooperative executor");
        completion.SetResult(new TranslationResponse(
            new TranslationResult("late final", "", "", [], []),
            new ProviderDiagnostics("s", ProviderType.OpenAiCompatible, "https://example.test", 1, 200, 0)));

        executor.OnStreamText = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("pre-cancelled work must never reach an executor");
        var preCancelled = await coordinator.TranslateTextAsync("source", "en", "zh",
            TranslationInputSource.Manual, cts.Token);
        Equal(TranslationSessionStage.Cancelled, preCancelled.Stage,
            "pre-cancelled requests must stop before routing or transport work");
    }

    private static async Task RuntimeSummaryCacheCancelsAndDisposes()
    {
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);
        var lifecycle = new SummaryLifecycleCoordinator();
        var reading = new ReadingModeState();
        var snapshot = coordinator.CreateSummarySnapshot("source", "en", "zh", TextTaskKind.Summarize, 0);
        reading.RememberSummary(snapshot.Identity, "cached", "", true);
        var prior = new CancellationTokenSource();
        var priorToken = prior.Token;
        lifecycle.AdoptOperation(prior);
        var started = false;
        var succeeded = false;
        await lifecycle.ExecuteSummaryAsync(coordinator, reading, "source", "en", "zh",
            () => true, () => "source", () => "zh", () => started = true,
            result => succeeded = result.IsCurrent && result.SummaryText == "cached",
            error => throw new InvalidOperationException(error.Message));
        True(priorToken.IsCancellationRequested, "a cache hit must abort the preceding operation");
        True(!started && succeeded && !lifecycle.IsRunning, "cache must finish without starting new work");

        CancellationTokenSource? owned = null;
        executor.OnRunTextTask = (_, _, _, _, _, _, _) => Task.FromResult(new TranslationResponse(
            new TranslationResult("summary", "", "", [], []),
            new ProviderDiagnostics("s", ProviderType.OpenAiCompatible, "https://example.test", 1, 200, 0)));
        await lifecycle.ExecuteSummaryAsync(coordinator, reading, "another", "en", "zh",
            () => true, () => "another", () => "zh",
            () => owned = (CancellationTokenSource?)typeof(SummaryLifecycleCoordinator)
                .GetField("_activeCts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(lifecycle),
            _ => { }, error => throw new InvalidOperationException(error.Message));
        True(!lifecycle.IsRunning, "normal completion must release active ownership");
        True(owned is not null, "the operation must own a cancellation source");
        Throws<ObjectDisposedException>(() => owned!.Cancel());
    }

    private static Task RuntimeMarkdownLinksRenderOnce()
    {
        EnsureApplication();
        var document = new FlowDocument();
        MarkdownPresenter.RenderToFlowDocument(document,
            "Before [Guide](https://example.test/path_(part)) after `code_id`.",
            Application.Current.Resources);
        // TextRange represents embedded UI as whitespace. Read the visible
        // inline tree so the assertion includes the code chip's real text.
        static string VisibleText(Inline inline) => inline switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Cast<Inline>().Select(VisibleText)),
            InlineUIContainer { Child: Border { Child: TextBlock code } } => code.Text,
            _ => throw new InvalidOperationException($"Unexpected inline type: {inline.GetType().Name}"),
        };
        var text = string.Concat(document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.Cast<Inline>()).Select(VisibleText));
        Equal("Before Guide after code_id.", text,
            "named URL/text captures and balancing captures must never become additional runs");
        var links = document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()).ToList();
        Equal(1, links.Count);
        Equal("https://example.test/path_(part)", links[0].NavigateUri.OriginalString);
        return Task.CompletedTask;
    }

    private static void RuntimeSummaryOfflineCapability()
    {
        var settings = new FakeTranslationExecutor().Settings with { SafeDevMode = true };
        var profile = new ProviderProfile
        {
            Id = "remote", Name = "Remote", ApiBaseUrl = "https://api.openai.com/v1",
            TextModel = "gpt-4o-mini", SupportsText = true,
        };
        var capability = RouteCapabilityService.Evaluate(profile, false,
            FreeEngineConsent.Allowed, "fake-key", settings);
        Equal(RouteCapabilityState.Unsupported, capability.State,
            "offline policy must be visible before the user starts a summary");
        True(capability.Reason.Contains("离线"), "the denial must explain the policy");
        profile.IsLocal = true;
        Equal(RouteCapabilityState.Unsupported, RouteCapabilityService.Evaluate(profile, false,
            FreeEngineConsent.Allowed, "fake-key", settings).State,
            "a stale local-template flag must not bypass the endpoint's offline policy");
        profile.ApiBaseUrl = "http://127.0.0.1:11434/v1";
        profile.IsLocal = false;
        Equal(RouteCapabilityState.Available, RouteCapabilityService.Evaluate(profile, false,
            FreeEngineConsent.Unset, null, settings).State,
            "a genuine loopback model remains available offline without a template flag");
        profile.TextModel = string.Empty;
        Equal(RouteCapabilityState.NeedsConfiguration, RouteCapabilityService.Evaluate(profile, false,
            FreeEngineConsent.Unset, null, settings with { TextModel = "stale-global-model" }).State,
            "a local profile without its own model must not inherit a stale mirrored model");
    }
}
