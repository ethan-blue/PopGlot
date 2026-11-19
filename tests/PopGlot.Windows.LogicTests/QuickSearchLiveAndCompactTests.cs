using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static class QuickSearchLiveAndCompactTests
{
    private const System.Reflection.BindingFlags NonPublicInstance =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected <{expected}>, got <{actual}>.");
    }

    private static void EnsureApp() => Program.EnsureApplication();

    private static void PumpOnce()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("must run on the STA harness thread");
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void PumpUntilCondition(Func<bool> condition, string message, int maxAttempts = 400)
    {
        for (var attempt = 0; attempt < maxAttempts && !condition(); attempt++)
        {
            PumpOnce();
            Thread.Sleep(10);
        }
        True(condition(), message);
    }

    private static void InvokePrivate(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, NonPublicInstance)!.Invoke(target, args);

    public static void LiveTranslateDebounceCollapsesTypingSkipsHistoryAndManualEnterWritesHistory()
    {
        EnsureApp();
        var tempDir = Path.Combine(TestIsolation.Root, $"qs-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(tempDir, "vocab.json"));
        var executor = new FakeLiveExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor: executor);

        var shell = ShellSettings.Default with
        {
            LiveTranslateEnabled = true,
            LiveTranslateDelayMs = 300,
            LiveTranslateScope = LiveTranslateScope.FreeEngineOnly,
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Bing,
        };
        var originalShell = ShellSettingsStore.Load();
        ShellSettingsStore.Save(shell);
        FreeTranslateService.ResetQuotaForTest();
        App.SharedSessionStore.Clear();

        var window = new QuickSearchWindow(
            history,
            vocab,
            coordinator: coordinator,
            shellSettingsLoader: () => shell,
            shellSettingsSaver: s => shell = s)
        {
            ForceClose = true,
            IsFreeRouteResolverForTest = () => true,
        };

        try
        {
            window.Show();
            PumpOnce();

            // Rapid intermediate typing: "h" -> "he" -> "hel" -> "hello"
            // Only the final settled query ("hello") should fire after debounce.
            window.SearchBox.Text = "h";
            PumpOnce();
            window.SearchBox.Text = "he";
            PumpOnce();
            window.SearchBox.Text = "hel";
            PumpOnce();
            window.SearchBox.Text = "hello";
            PumpOnce();

            // Wait for the 300ms debounce to fire and complete the fake free translation
            PumpUntilCondition(() => executor.FreeCalls.Count >= 1 && window.State.Stage == QuickSearchUiStage.Completed,
                "live translate debounce must trigger once for settled query");

            Equal(1, executor.FreeCalls.Count, "intermediate keystrokes must collapse into a single live request");
            Equal("hello", executor.FreeCalls[0], "settled query must be sent");
            Equal("译:hello", window.State.FinalRenderedText, "live translation result must render");
            True(window.IsCurrentResultAutoTriggered, "live result must be marked autoTriggered");
            Equal(0, history.Load().Count, "autoTriggered live translation must skip HistoryStore");

            // Hide must NOT store auto-triggered intermediate snapshot into SharedSessionStore
            window.StoreSessionSnapshot();
            True(App.SharedSessionStore.PeekRecent() is null,
                "autoTriggered live translation must not pollute SharedSessionStore before user commits");

            // Single-character CJK query ("猫") must also auto-trigger (while single ASCII "a" does not)
            window.SearchBox.Text = "a";
            Thread.Sleep(350);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "single ASCII char must not auto-trigger live translate");

            // Zero-flicker dimming check: restore "hello" result so previous result is visible,
            // then trigger a new live query "猫" with a paused completion.
            window.SearchBox.Text = "hello";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Completed && window.State.FinalRenderedText == "译:hello",
                "restored hello query must complete before dimming check");

            var gate = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.NextFreeResponseTask = gate.Task;
            window.SearchBox.Text = "猫";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpOnce();

            True(window.State.IsResultVisible, "ResultContainer must stay visible during zero-flicker live loading");
            True(window.State.IsDimmingPreviousResult, "previous result must be dimmed while waiting for live response");
            var resultContainer = (Border)window.FindName("ResultContainer")!;
            Equal(0.55, Math.Round(resultContainer.Opacity, 2), "ResultContainer opacity must dim to 0.55");

            gate.SetResult(executor.BuildResponse("猫"));
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Completed && window.State.FinalRenderedText == "译:猫",
                "single-char CJK live query must complete");
            True(!window.State.IsDimmingPreviousResult, "dimming must clear on completion");
            Equal(1.0, Math.Round(resultContainer.Opacity, 2), "ResultContainer opacity must restore to 1.0");
            Equal(0, history.Load().Count, "autoTriggered CJK query must still skip HistoryStore");

            // Route + latency badge check in footer
            var latencyBadge = (Border)window.FindName("RouteLatencyBadge")!;
            var latencyText = (TextBlock)window.FindName("RouteLatencyText")!;
            Equal(Visibility.Visible, latencyBadge.Visibility, "route latency badge must be visible on completion");
            True(latencyText.Text.Contains("Bing", StringComparison.Ordinal) && latencyText.Text.Contains("ms", StringComparison.Ordinal),
                $"route latency badge must show route and ms, got '{latencyText.Text}'");

            // Manual Enter on "猫" must commit with autoTriggered: false, write HistoryStore, and allow session snapshot
            executor.NextFreeResponseTask = null;
            window.SearchEnterKeycap.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCondition(() => history.Load().Count == 1,
                "manual Enter / keycap must commit translation to HistoryStore");

            True(!window.IsCurrentResultAutoTriggered, "manual Enter must clear autoTriggered flag");
            Equal("猫", history.Load()[0].Source, "HistoryStore must record manually committed query");

            window.StoreSessionSnapshot();
            var recent = App.SharedSessionStore.PeekRecent();
            True(recent is not null && recent.SourceText == "猫",
                "manually committed session must be saved to SharedSessionStore");
        }
        finally
        {
            window.Close();
            ShellSettingsStore.Save(originalShell);
            App.SharedSessionStore.Clear();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static void ImeComposingAndQuotaAndLengthGuardsSuppressLiveTranslateCleanly()
    {
        EnsureApp();
        var tempDir = Path.Combine(TestIsolation.Root, $"qs-guards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(tempDir, "vocab.json"));
        var executor = new FakeLiveExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor: executor);

        var shell = ShellSettings.Default with
        {
            LiveTranslateEnabled = true,
            LiveTranslateDelayMs = 300,
            LiveTranslateScope = LiveTranslateScope.FreeEngineOnly,
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Bing,
        };
        var originalShell = ShellSettingsStore.Load();
        ShellSettingsStore.Save(shell);
        FreeTranslateService.ResetQuotaForTest();

        var window = new QuickSearchWindow(
            history,
            vocab,
            coordinator: coordinator,
            shellSettingsLoader: () => shell,
            shellSettingsSaver: s => shell = s)
        {
            ForceClose = true,
            IsFreeRouteResolverForTest = () => true,
        };

        try
        {
            window.Show();
            PumpOnce();

            // 1. IME composing suppresses live translate until composition finishes
            Ui.SetIsComposing(window.SearchBox, true);
            window.SearchBox.Text = "nihao";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpOnce();
            Equal(0, executor.FreeCalls.Count, "active IME composition must suppress live translate on debounce tick");

            // Finish composition -> next tick translates
            Ui.SetIsComposing(window.SearchBox, false);
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpUntilCondition(() => executor.FreeCalls.Count == 1,
                "finishing IME composition must allow debounce tick to translate");

            // 2. Quota < 30 suppresses live translate with exact hint
            var quotaGuard = QuickSearchLivePolicy.EvaluateFreeRouteGuards(
                "low quota test", FreeEngineProvider.Bing, quotaRemaining: 29);
            True(!quotaGuard.Allowed, "quota < 30 must disallow live translate");
            Equal("剩余自动配额不足，按 Enter 手动翻译", quotaGuard.Hint, "low quota hint must match exact copy");

            // Simulate quota < 30 on live window via FreeTranslateService quota state
            var quotaField = typeof(FreeTranslateService).GetField("_quotaUsed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var windowStartField = typeof(FreeTranslateService).GetField("_quotaWindowStartMs",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var monotonicProp = typeof(FreeTranslateService).GetProperty("MonotonicMilliseconds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            windowStartField.SetValue(null, (long)monotonicProp.GetValue(null)!);
            quotaField.SetValue(null, FreeTranslateService.HourlySendBudget - 15); // 15 remaining (< 30)

            window.SearchBox.Text = "quota blocked query";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "low quota must prevent auto-send");
            Equal("剩余自动配额不足，按 Enter 手动翻译", window.FooterStatusBlock.Text,
                "footer must show non-intrusive low quota hint");

            FreeTranslateService.ResetQuotaForTest();

            // 3. MyMemory > 500 UTF-8 bytes guard
            shell = shell with { FreeEngineProvider = FreeEngineProvider.MyMemory };
            window.SearchBox.Text = new string('中', 200); // 600 UTF-8 bytes > 500
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "MyMemory over 500 bytes must not auto-send");
            Equal("内容较长，按 Enter 使用分段翻译", window.FooterStatusBlock.Text,
                "footer must show MyMemory length hint");

            // 4. Youdao multi-sentence / > 120 chars guard
            shell = shell with { FreeEngineProvider = FreeEngineProvider.Youdao };
            window.SearchBox.Text = "第一句测试内容。第二句测试内容。";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "Youdao multi-sentence must not auto-send");
            Equal("有道词典适合单词和短语，长句可按 Enter 或切换 Bing", window.FooterStatusBlock.Text,
                "footer must show Youdao dict hint");
        }
        finally
        {
            Ui.SetIsComposing(window.SearchBox, false);
            FreeTranslateService.ResetQuotaForTest();
            window.Close();
            ShellSettingsStore.Save(originalShell);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static void EscapeCancelsInFlightLiveRequestAndCompactExpandedModePersists()
    {
        EnsureApp();
        var tempDir = Path.Combine(TestIsolation.Root, $"qs-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(tempDir, "vocab.json"));
        var executor = new FakeLiveExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor: executor);

        var shell = ShellSettings.Default with
        {
            LiveTranslateEnabled = true,
            LiveTranslateDelayMs = 300,
            LiveTranslateScope = LiveTranslateScope.FreeEngineOnly,
            FreeEngineConsent = FreeEngineConsent.Allowed,
            QuickSearchWindowMode = QuickSearchWindowMode.Compact,
            QuickSearchExpandedWidth = 700,
            QuickSearchExpandedHeight = 540,
        };
        var originalShell = ShellSettingsStore.Load();
        ShellSettingsStore.Save(shell);

        var window = new QuickSearchWindow(
            history,
            vocab,
            coordinator: coordinator,
            shellSettingsLoader: () => shell,
            shellSettingsSaver: s =>
            {
                shell = s;
                ShellSettingsStore.Save(s);
            })
        {
            ForceClose = true,
            IsFreeRouteResolverForTest = () => true,
        };
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-qs-live-esc") { Width = 8, Height = 8 });

        try
        {
            window.Show();
            PumpOnce();

            // Verify initial Compact mode constraints
            Equal(QuickSearchWindowMode.Compact, window.CurrentWindowMode, "window starts in Compact mode");
            Equal(QuickSearchWindow.CompactWindowWidth, window.Width, "Compact mode width is 430 DIP");
            Equal(ResizeMode.NoResize, window.ResizeMode, "Compact mode disables resize border");
            var styleBtn = (Button)window.FindName("StyleSelectorButton")!;
            var settingsBtn = (Button)window.FindName("SettingsButton")!;
            var openMainBtn = (Button)window.FindName("OpenInMainButton")!;
            Equal(Visibility.Collapsed, styleBtn.Visibility, "Compact mode collapses style selector");
            Equal(Visibility.Collapsed, settingsBtn.Visibility, "Compact mode collapses settings button");
            Equal(Visibility.Collapsed, openMainBtn.Visibility, "Compact mode collapses open-in-main button");

            // Start an in-flight live request and cancel it with Escape
            var pending = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.NextFreeResponseTask = pending.Task;
            window.SearchBox.Text = "inflight cancel test";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Streaming,
                "live request must enter Streaming stage");

            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            window.RaiseEvent(esc);
            True(esc.Handled, "Esc must be handled when cancelling an in-flight live request");
            True(window.IsVisible, "first Esc must cancel the in-flight live request without hiding the window");

            pending.TrySetCanceled();
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Cancelled,
                "cancelled live request must transition to Cancelled stage");

            // Complete a query with explanation to verify inline explanation toggle in Compact vs Expanded
            executor.NextFreeResponseTask = null;
            executor.NextExplanation = "n. 核心词义；详细释义说明";
            window.SearchBox.Text = "explanation word";
            InvokePrivate(window, "DebounceTimer_Tick", window, EventArgs.Empty);
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Completed,
                "query with explanation must complete");

            var expToggle = (Button)window.FindName("ExplanationToggleButton")!;
            var expCard = (Border)window.FindName("ExplanationCard")!;
            Equal(Visibility.Visible, expToggle.Visibility, "Compact mode shows inline explanation toggle");
            Equal(Visibility.Collapsed, expCard.Visibility, "Compact mode starts with ExplanationCard collapsed");
            Equal("释义详情 ▾", expToggle.Content as string, "toggle button shows expand label");

            expToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(Visibility.Visible, expCard.Visibility, "clicking inline toggle expands ExplanationCard in Compact mode");

            // Toggle Compact -> Expanded via ModeToggleButton
            var modeBtn = (Button)window.FindName("ModeToggleButton")!;
            modeBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();

            Equal(QuickSearchWindowMode.Expanded, window.CurrentWindowMode, "mode switches to Expanded");
            Equal(QuickSearchWindowMode.Expanded, shell.QuickSearchWindowMode, "Expanded mode persists to ShellSettings");
            Equal( QuickSearchWindowMode.Expanded, ShellSettingsStore.Load().QuickSearchWindowMode, "Expanded mode saved to store");
            Equal(ResizeMode.CanResize, window.ResizeMode, "Expanded mode enables resizing");
            Equal(QuickSearchWindow.ExpandedMinWidth, window.MinWidth, "Expanded mode enforces 480 DIP MinWidth");
            Equal(700.0, window.Width, "Expanded mode restores persisted QuickSearchExpandedWidth");
            Equal(Visibility.Visible, styleBtn.Visibility, "Expanded mode shows style selector");
            Equal(Visibility.Visible, settingsBtn.Visibility, "Expanded mode shows settings button");
            Equal(Visibility.Visible, openMainBtn.Visibility, "Expanded mode shows open-in-main button");
            Equal(Visibility.Collapsed, expToggle.Visibility, "Expanded mode hides compact explanation toggle");
            Equal(Visibility.Visible, expCard.Visibility, "Expanded mode shows full ExplanationCard directly");

            // Toggle Expanded -> Compact back
            window.ToggleWindowMode();
            PumpOnce();
            Equal(QuickSearchWindowMode.Compact, window.CurrentWindowMode, "mode switches back to Compact");
            Equal(QuickSearchWindowMode.Compact, shell.QuickSearchWindowMode, "Compact mode persists to ShellSettings");
            Equal(QuickSearchWindow.CompactWindowWidth, window.Width, "Compact mode restores 430 DIP width");
        }
        finally
        {
            source.Dispose();
            window.Close();
            ShellSettingsStore.Save(originalShell);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class FakeLiveExecutor : ITranslationExecutor
    {
        public List<string> FreeCalls { get; } = [];
        public Task<TranslationResponse>? NextFreeResponseTask { get; set; }
        public string NextExplanation { get; set; } = string.Empty;

        public ProviderSettings GetSettings() => CoreBridge.GetSettings() with
        {
            TextModel = string.Empty,
            ApiBaseUrl = string.Empty,
            NetworkEnabled = true,
            SafeDevMode = false,
        };

        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() => (null, null);

        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable) =>
            new(null, null, ScreenshotPipeline.LocalOcr, false, "Local OCR");

        public string? LoadApiKey(string target) => null;

        public bool IsOcrSupported => false;

        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public TranslationStreamSession StreamText(string? apiKey, string source, string sourceLang, string targetLang,
            string sessionId, long epoch, CancellationToken cancellationToken)
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, Task.FromResult(BuildResponse(source)));
        }

        public TranslationStreamSession StreamTextDraft(ProviderSettings draftSettings, string apiKey, string source,
            string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) =>
            StreamText(apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);

        public TranslationStreamSession StreamVisionDraft(ProviderSettings draftSettings, string textApiKey, string visionApiKey,
            byte[] image, string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) =>
            StreamText(textApiKey, string.Empty, sourceLang, targetLang, sessionId, epoch, cancellationToken);

        public async Task<TranslationResponse> TranslateFreeAsync(
            string source,
            string sourceLang,
            string targetLang,
            FreeEngineAuthorization authorization,
            CancellationToken cancellationToken)
        {
            FreeCalls.Add(source);
            if (NextFreeResponseTask is { } customTask)
            {
                return await customTask.WaitAsync(cancellationToken);
            }
            return BuildResponse(source);
        }

        public TranslationResponse BuildResponse(string source) =>
            new(
                new TranslationResult($"译:{source}", "yin", NextExplanation, [], []),
                new ProviderDiagnostics("free-test", ProviderType.OpenAiCompatible, "https://cn.bing.com", 1, 200, 85),
                OverrideEngineLabel: "Bing 翻译");
    }
}
