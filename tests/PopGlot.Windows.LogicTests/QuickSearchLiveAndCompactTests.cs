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

    private static void PumpFor(int milliseconds)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.ElapsedMilliseconds < milliseconds)
        {
            PumpOnce();
            Thread.Sleep(10);
        }
        PumpOnce();
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
            PumpFor(550);
            PumpUntilCondition(() => window.State.Stage == QuickSearchUiStage.Completed && window.State.FinalRenderedText == "译:hello",
                "restored hello query must complete before dimming check");

            var gate = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            executor.NextFreeResponseTask = gate.Task;
            window.SearchBox.Text = "猫";
            PumpFor(550);
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
            PumpFor(550);
            PumpOnce();
            Equal(0, executor.FreeCalls.Count, "active IME composition must suppress live translate on debounce tick");

            // Finish composition -> next tick translates
            Ui.SetIsComposing(window.SearchBox, false);
            PumpFor(550);
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
            PumpFor(550);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "low quota must prevent auto-send");
            Equal("剩余自动配额不足，按 Enter 手动翻译", window.FooterStatusBlock.Text,
                "footer must show non-intrusive low quota hint");

            FreeTranslateService.ResetQuotaForTest();

            // 3. MyMemory > 500 UTF-8 bytes guard
            shell = shell with { FreeEngineProvider = FreeEngineProvider.MyMemory };
            window.SearchBox.Text = new string('中', 200); // 600 UTF-8 bytes > 500
            PumpFor(550);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "MyMemory over 500 bytes must not auto-send");
            Equal("内容较长，按 Enter 使用分段翻译", window.FooterStatusBlock.Text,
                "footer must show MyMemory length hint");

            // 4. Youdao multi-sentence / > 120 chars guard
            shell = shell with { FreeEngineProvider = FreeEngineProvider.Youdao };
            window.SearchBox.Text = "第一句测试内容。第二句测试内容。";
            PumpFor(550);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "Youdao multi-sentence must not auto-send");
            Equal("有道词典适合单词和短语，长句请切换 Bing", window.FooterStatusBlock.Text,
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
            PumpFor(550);
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
            PumpFor(550);
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

            VerifyQuickSearchKeyboardAndUiContracts();
        }
        finally
        {
            source.Dispose();
            window.Close();
            ShellSettingsStore.Save(originalShell);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static void ShowSuppressesWarmupWhenTestSuppressionOrCustomRouteIsActive()
    {
        EnsureApp();
        var tempDir = Path.Combine(TestIsolation.Root, $"qs-warmup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(tempDir, "vocab.json"));
        var executor = new FakeLiveExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor: executor);

        var originalSuppress = App.SuppressStartupForTests;
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalShell = ShellSettingsStore.Load();
        var originalCore = CoreBridge.GetSettings();

        var shell = ShellSettings.Default with
        {
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Bing,
        };
        ShellSettingsStore.Save(shell);
        CoreBridge.SaveSettings(originalCore with { NetworkEnabled = true, SafeDevMode = false });
        FreeTranslateService.ResetQuotaForTest();
        FreeTranslateService.ResetRateLimitStateForTest();
        App.SharedSessionStore.Clear();

        var warmupMethods = new List<System.Net.Http.HttpMethod>();
        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            lock (warmupMethods)
            {
                warmupMethods.Add(request.Method);
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
        };

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
            // 1. Test startup suppression blocks warmup even when active route is free.
            App.SuppressStartupForTests = true;
            window.Show();
            PumpOnce();
            Thread.Sleep(60);
            PumpOnce();
            lock (warmupMethods)
            {
                Equal(0, warmupMethods.Count, "SuppressStartupForTests=true must block QuickSearch warmup");
            }

            // 2. Active custom/local engine (Demo Text Service, PreferFreeEngine=false) blocks warmup even with suppression off.
            App.SuppressStartupForTests = false;
            window.IsFreeRouteResolverForTest = null;
            window.Hide();
            PumpOnce();
            window.Show();
            PumpOnce();
            Thread.Sleep(60);
            PumpOnce();
            lock (warmupMethods)
            {
                Equal(0, warmupMethods.Count, "active custom engine must block QuickSearch free-route warmup");
            }

            // 3. Eligible free route with suppression off fires exactly one HEAD warmup (no duplicate Show/IsVisibleChanged fire).
            window.IsFreeRouteResolverForTest = () => true;
            window.Hide();
            PumpOnce();
            window.Show();
            PumpUntilCondition(() =>
            {
                lock (warmupMethods) return warmupMethods.Count >= 1;
            }, "eligible free-route Show must trigger warmup once");
            Thread.Sleep(60);
            PumpOnce();
            lock (warmupMethods)
            {
                Equal(1, warmupMethods.Count, "Show + IsVisibleChanged must not trigger duplicate warmup");
                Equal(System.Net.Http.HttpMethod.Head, warmupMethods[0], "warmup must send a zero-payload HEAD request");
            }
        }
        finally
        {
            window.Close();
            App.SuppressStartupForTests = originalSuppress;
            FreeTranslateService.HttpSenderOverride = originalSender;
            CoreBridge.SaveSettings(originalCore);
            ShellSettingsStore.Save(originalShell);
            App.SharedSessionStore.Clear();
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void VerifyQuickSearchKeyboardAndUiContracts()
    {
        EnsureApp();
        var tempDir = Path.Combine(TestIsolation.Root, $"qs-kbd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(tempDir, "vocab.json"));
        var executor = new FakeLiveExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor: executor);

        var shell = ShellSettings.Default with
        {
            LiveTranslateEnabled = false,
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Bing,
            QuickSearchWindowMode = QuickSearchWindowMode.Compact,
        };
        var originalShell = ShellSettingsStore.Load();
        var originalInterface = InterfaceKeys.Snapshot;
        ShellSettingsStore.Save(shell);
        InterfaceKeys.Apply(shell);

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
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-qs-kbd-test") { Width = 8, Height = 8 });

        try
        {
            window.Show();
            PumpOnce();

            // Compact mode keeps clickable submit affordance visible
            Equal(Visibility.Visible, window.EnterKeycapButton.Visibility,
                "Compact mode must keep SearchEnterKeycap visible");

            // 1. Plain Key.Enter on SearchBox triggers translation and does NOT insert newline
            window.SearchBox.Text = "keyboard enter submit";
            window.ModifiersOverrideForTest = ModifierKeys.None;
            var previewEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.SearchBox.RaiseEvent(previewEnter);
            True(previewEnter.Handled, "plain Enter PreviewKeyDown must be handled to submit");
            if (!previewEnter.Handled)
            {
                window.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                });
            }
            PumpUntilCondition(() => executor.FreeCalls.Count == 1 && window.State.Stage == QuickSearchUiStage.Completed,
                "plain Enter on SearchBox must trigger translation");
            True(!window.SearchBox.Text.Contains('\r') && !window.SearchBox.Text.Contains('\n'),
                "plain Enter submit must not insert CR/LF into SearchBox.Text");

            // 2. Shift+Enter does NOT submit and allows native newline insertion
            window.ModifiersOverrideForTest = ModifierKeys.Shift;
            var previewShiftEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.SearchBox.RaiseEvent(previewShiftEnter);
            True(!previewShiftEnter.Handled, "Shift+Enter must remain unhandled so TextBox inserts newline");
            System.Windows.Documents.EditingCommands.EnterLineBreak.Execute(null, window.SearchBox);
            PumpOnce();
            Equal(1, executor.FreeCalls.Count, "Shift+Enter must not trigger translation");
            True(window.SearchBox.Text.Contains('\n'), "Shift+Enter must insert a newline in SearchBox");

            // 3. IME-composing Key.Enter does NOT trigger translation
            window.SearchBox.Text = "ime composing test";
            window.ModifiersOverrideForTest = ModifierKeys.None;
            Ui.SetIsComposing(window.SearchBox, true);
            var previewImeEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.SearchBox.RaiseEvent(previewImeEnter);
            PumpOnce();
            True(!previewImeEnter.Handled, "IME-composing Enter must stay unhandled for IME commit");
            Equal(1, executor.FreeCalls.Count, "IME-composing Enter must not submit translation");
            Ui.SetIsComposing(window.SearchBox, false);

            // 4. Ctrl+Enter still submits cleanly
            window.SearchBox.Text = "ctrl enter submit";
            window.ModifiersOverrideForTest = ModifierKeys.Control;
            var previewCtrlEnter = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.SearchBox.RaiseEvent(previewCtrlEnter);
            True(previewCtrlEnter.Handled, "Ctrl+Enter must be handled");
            PumpUntilCondition(() => executor.FreeCalls.Count == 2, "Ctrl+Enter must trigger translation");

            // 5. Ctrl+R retries a failed translation cleanly when SearchBox has focus
            executor.NextFreeResponseTask = Task.FromException<TranslationResponse>(new InvalidOperationException("transient"));
            window.SearchBox.Text = "ctrl r retry";
            window.ModifiersOverrideForTest = ModifierKeys.None;
            window.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            PumpUntilCondition(() => executor.FreeCalls.Count == 3 && window.State.Stage == QuickSearchUiStage.Failed,
                "failed query must enter Failed stage");

            executor.NextFreeResponseTask = null;
            window.ModifiersOverrideForTest = ModifierKeys.Control;
            var retryKey = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.R)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            window.SearchBox.RaiseEvent(retryKey);
            True(retryKey.Handled, "Ctrl+R must be handled in QuickSearchWindow");
            PumpUntilCondition(() => executor.FreeCalls.Count == 4 && window.State.Stage == QuickSearchUiStage.Completed,
                "Ctrl+R must trigger retry translation");

            // 6. Compact mode Ctrl+T does not fire hidden StyleSelectorButton
            var previewStyle = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.T)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.RaiseEvent(previewStyle);
            True(!previewStyle.Handled, "Ctrl+T in Compact mode must not trigger collapsed StyleSelectorButton");

            // 7. Expanded mode uses filled IconCollapse (not stroked IconCaptionRestore) and shows idle guide
            window.SearchBox.Text = string.Empty;
            window.ToggleWindowMode();
            PumpOnce();
            var modeIcon = (System.Windows.Shapes.Path)window.FindName("ModeToggleIcon")!;
            var iconCollapse = Application.Current!.TryFindResource("IconCollapse");
            True(ReferenceEquals(modeIcon.Data, iconCollapse),
                "Expanded mode must assign filled IconCollapse geometry to ModeToggleIcon");
            var emptyGuide = (FrameworkElement)window.FindName("ExpandedEmptyGuide")!;
            Equal(Visibility.Visible, emptyGuide.Visibility,
                "Expanded idle mode must show centered ExpandedEmptyGuide instead of blank void");

            // 8. TranslateSection example/onboarding hints & narrow stacked swap/divider fixes
            var translateSection = new PopGlot.Windows.Sections.TranslateSection();
            translateSection.Initialize(coordinator, vocab);
            InvokePrivate(translateSection, "FillExample_Click", translateSection, new RoutedEventArgs());
            True(translateSection.StatusBlock.Text.Contains(InterfaceKeys.TranslateKeyName, StringComparison.Ordinal),
                $"FillExample status must contain {InterfaceKeys.TranslateKeyName}, got '{translateSection.StatusBlock.Text}'");
            True(!translateSection.StatusBlock.Text.Contains("按 Enter 翻译", StringComparison.Ordinal),
                "FillExample status must never claim 按 Enter 翻译");

            translateSection.BeginOnboarding();
            InvokePrivate(translateSection, "AdvanceOnboarding");
            InvokePrivate(translateSection, "AdvanceOnboarding");
            var onboardingDesc = (TextBlock)translateSection.FindName("OnboardingDescription")!;
            True(onboardingDesc.Text.Contains(InterfaceKeys.TranslateKeyName, StringComparison.Ordinal),
                $"Onboarding step 3 must contain {InterfaceKeys.TranslateKeyName}, got '{onboardingDesc.Text}'");
            True(!onboardingDesc.Text.Contains("按 Enter 翻译", StringComparison.Ordinal),
                "Onboarding step 3 must never claim 按 Enter 翻译");

            var axisDivider = (System.Windows.Shapes.Rectangle)translateSection.FindName("AxisDividerLine")!;
            Equal(2, System.Windows.Controls.Grid.GetRowSpan(axisDivider),
                "AxisDividerLine must span both body and footer rows");

            translateSection.SetStacked(true);
            var stackedSwap = (Button)translateSection.FindName("StackedSwapButton")!;
            Equal(Visibility.Visible, stackedSwap.Visibility,
                "StackedSwapButton must be visible in narrow stacked mode");

            // 9. LibrarySection SpeakHotkey dynamic hints/shortcut + rounded inner borders
            var librarySection = new PopGlot.Windows.Sections.LibrarySection();
            librarySection.Initialize(history, vocab);
            var reboundShell = shell with { InterfaceSpeakHotkey = new HotkeyBinding(HotkeyBinding.ModAlt, 0x50) };
            InterfaceKeys.Apply(reboundShell);
            librarySection.RefreshHotkeyHints();
            var placeholderHints = (TextBlock)librarySection.FindName("DetailPlaceholderHints")!;
            True(placeholderHints.Text.Contains("Alt+P 朗读", StringComparison.Ordinal) &&
                 !placeholderHints.Text.Contains("Ctrl+P", StringComparison.Ordinal),
                $"LibrarySection placeholder hints must reflect rebound SpeakHotkey, got '{placeholderHints.Text}'");
            True(librarySection.HandleSectionShortcut(Key.P, ModifierKeys.Alt),
                "LibrarySection must honor rebound SpeakHotkey (Alt+P)");
            True(!librarySection.HandleSectionShortcut(Key.P, ModifierKeys.Control),
                "LibrarySection must not hardcode Ctrl+P when rebound");

            librarySection.Measure(new Size(900, 600));
            librarySection.Arrange(new Rect(0, 0, 900, 600));
            librarySection.UpdateLayout();
            var masterSummary = (Border)librarySection.FindName("MasterSummaryBorder")!;
            var detailPane = (Border)librarySection.FindName("DetailPane")!;
            var detailFooter = (Border)librarySection.FindName("DetailFooterBorder")!;
            True(masterSummary.CornerRadius.BottomLeft > 0, "MasterSummaryBorder must round outer bottom-left corner");
            True(detailPane.CornerRadius.TopRight > 0 && detailPane.CornerRadius.BottomRight > 0,
                "DetailPane must round outer right corners");
            True(detailFooter.CornerRadius.BottomRight > 0, "DetailFooterBorder must round outer bottom-right corner");
        }
        finally
        {
            window.ModifiersOverrideForTest = null;
            Ui.SetIsComposing(window.SearchBox, false);
            source.Dispose();
            window.Close();
            InterfaceKeys.Apply(originalInterface);
            ShellSettingsStore.Save(originalShell);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public static void LiveConsentUsesRealWindowTimerAndOnlyDeniedBlocks()
    {
        foreach (var consent in new[] { FreeEngineConsent.Unset, FreeEngineConsent.Allowed, FreeEngineConsent.Denied })
        {
            using var fixture = new LiveWindowFixture();
            fixture.Save(fixture.Shell with { FreeEngineConsent = consent });
            fixture.Window.SearchBox.Text = "consent query";
            PumpFor(650);
            Equal(consent == FreeEngineConsent.Denied ? 0 : 1, fixture.Executor.FreeCalls.Count,
                $"real debounce send count for {consent}");
            True(RuntimeGate.NewWorkAllowed, "consent refusal must not degrade the application");
        }
    }

    public static void AutoAllCooldownsStayLocalAndRuntimeGateRemainsUsable()
    {
        foreach (var marker in new[] { "MarkHostRateLimited", "MarkHostTransportCooldown" })
        {
            using var fixture = new LiveWindowFixture();
            fixture.Save(fixture.Shell with { FreeEngineProvider = FreeEngineProvider.Auto });
            var mark = typeof(FreeTranslateService).GetMethod(marker,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            foreach (var host in new[] { "cn.bing.com", "dict.youdao.com", "translate.google.com", "translate.googleapis.com",
                         "clients5.google.com", "api.mymemory.translated.net" })
            {
                mark.Invoke(null, [host]);
            }
            var unhandled = 0;
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
            {
                unhandled++;
                e.Handled = true;
            };
            fixture.Window.Dispatcher.UnhandledException += handler;
            try
            {
                fixture.Window.SearchBox.Text = "apple";
                PumpFor(650);
                Equal(0, fixture.Executor.FreeCalls.Count, "all-cooling Auto must not reach the executor");
                Equal(0, unhandled, "business cooldown must not escape the real DispatcherTimer");
                True(!string.IsNullOrWhiteSpace(fixture.Window.State.PendingNotice), "cooldown must have a local hint");
                True(RuntimeGate.NewWorkAllowed, "all-cooling Auto must not trip the global fuse");

                FreeTranslateService.ResetRateLimitStateForTest();
                fixture.Window.SearchBox.Text = "available again";
                PumpUntilCondition(() => fixture.Window.State.Stage == QuickSearchUiStage.Completed,
                    "new work must still complete after cooldown refusal");
                Equal(1, fixture.Executor.FreeCalls.Count, "recovered window must send exactly once");
            }
            finally
            {
                fixture.Window.Dispatcher.UnhandledException -= handler;
            }
        }
    }

    public static void HidingBeforeDebounceSendsNothingAndReopeningDoesNotReviveIt()
    {
        foreach (var closePath in new[] { "Hide", "BaseHide", "X", "Close", "Esc" })
        {
            using var fixture = new LiveWindowFixture();
            var window = fixture.Window;
            window.ForceClose = false;
            window.SearchBox.Text = "must never leave";
            switch (closePath)
            {
                case "Hide": window.Hide(); break;
                case "BaseHide": ((Window)window).Hide(); break;
                case "X": ((Button)window.FindName("CloseButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                case "Close": window.Close(); break; // Same OnClosing path as Alt+F4.
                default: fixture.Key(Key.Escape); break;
            }
            True(!window.IsVisible, $"{closePath} must hide the reusable window");
            // Reopen before the old deadline; a visibility-only check would fail this case.
            window.Show();
            PumpFor(650);
            Equal(0, fixture.Executor.FreeCalls.Count, $"{closePath}: reopening must not revive the old timer");
            window.Hide();
            window.SearchBox.Text = "hidden programmatic edit";
            PumpFor(650);
            Equal(0, fixture.Executor.FreeCalls.Count, $"{closePath}: hidden editing must not arm a request");
            window.Show();
            PumpFor(400);
            Equal(0, fixture.Executor.FreeCalls.Count, $"{closePath}: hidden edit must not revive on Show");
            window.SearchBox.Text = "fresh visible query";
            PumpUntilCondition(() => fixture.Executor.FreeCalls.Count == 1,
                $"{closePath}: a fresh visible edit must still translate");
        }
    }

    public static void EditingImmediatelyRevokesActionsAndNeverTransfersOldResult()
    {
        foreach (var partial in new[] { false, true })
        {
            using var fixture = new LiveWindowFixture();
            var window = fixture.Window;
            fixture.Executor.NextIsPartial = partial;
            window.SearchBox.Text = "apple";
            window.EnterKeycapButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCondition(() => window.State.Stage == (partial ? QuickSearchUiStage.Partial : QuickSearchUiStage.Completed),
                "manual apple result must finish before editing");
            window.StoreSessionSnapshot();
            Equal("apple", App.SharedSessionStore.PeekRecent()!.SourceText, "baseline snapshot owns apple");
            App.SharedSessionStore.Clear();

            window.SearchBox.Text = "banana";
            True(!window.State.CanStar && !window.State.CanSpeak && !window.State.CanCopy,
                "actions must be revoked in TextChanged, without pumping the debounce");
            Equal("apple", window.State.ResultQuery, "visual result must retain its original owner");
            True(window.State.IsResultVisible && window.State.IsDimmingPreviousResult,
                "editing must preserve the previous result visually only");
            fixture.Key(Key.S, ModifierKeys.Control);
            ((Button)window.FindName("StarButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Equal(0, fixture.Vocabulary.GetAll().Count, "neither shortcut nor direct click may star banana with apple's result");
            window.StoreSessionSnapshot();
            True(App.SharedSessionStore.PeekRecent() is null, "neither completed nor partial old text may be stored under banana");

            var main = new MainWindow(fixture.Shell, fixture.History, fixture.Vocabulary)
            {
                AllowClose = true, ShowActivated = false,
            };
            var mainField = typeof(App).GetField("_mainWindow", NonPublicInstance)!;
            var app = Application.Current as App;
            var originalMain = app is null ? null : mainField.GetValue(app);
            if (app is not null) mainField.SetValue(app, main);
            try
            {
                ((Button)window.FindName("OpenInMainButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Equal("banana", main.TranslateSection.InputBox.Text, "workbench must receive the new query");
                Equal(string.Empty, main.TranslateSection.ResultBox.Text, "workbench must not receive apple's final result");
                Equal(string.Empty, main.TranslateSection.StreamResultBox.Text, "workbench must not receive apple's partial result");
                True(App.SharedSessionStore.PeekRecent() is null, "transfer/hide must not persist a mixed snapshot");
            }
            finally
            {
                if (app is not null) mainField.SetValue(app, originalMain);
                main.Close();
            }
        }
    }

    public static void ExistingWindowReevaluatesLiveSettingsAndCancelsAutomaticWork()
    {
        using var fixture = new LiveWindowFixture();
        var window = fixture.Window;
        fixture.Save(fixture.Shell with { LiveTranslateEnabled = false });
        window.SearchBox.Text = "settings query";
        PumpFor(500);
        Equal(0, fixture.Executor.FreeCalls.Count, "disabled live translation must not send");

        fixture.Save(fixture.Shell with { LiveTranslateEnabled = true, LiveTranslateDelayMs = 800 });
        PumpFor(250);
        fixture.Save(fixture.Shell with { LiveTranslateDelayMs = 300 });
        PumpUntilCondition(() => fixture.Executor.FreeCalls.Count == 1 && window.State.Stage == QuickSearchUiStage.Completed,
            "enabling and changing delay must apply without another edit", maxAttempts: 65);

        fixture.Save(fixture.Shell with { LiveTranslateDelayMs = 800 });
        window.SearchBox.Text = "longer delay";
        PumpFor(450);
        Equal(1, fixture.Executor.FreeCalls.Count, "increasing delay must not use the previous 300ms timer");
        fixture.Save(fixture.Shell with { LiveTranslateEnabled = false });
        PumpFor(900);
        Equal(1, fixture.Executor.FreeCalls.Count, "disabling must discard the scheduled automatic query");

        var pending = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.NextFreeResponseTask = pending.Task;
        fixture.Save(fixture.Shell with { LiveTranslateEnabled = true, LiveTranslateDelayMs = 300 });
        PumpUntilCondition(() => fixture.Executor.FreeCalls.Count == 2, "reenabling must schedule the current visible draft");
        var automaticToken = fixture.Executor.FreeTokens.Last();
        fixture.Save(fixture.Shell with { LiveTranslateEnabled = false });
        PumpUntilCondition(() => automaticToken.IsCancellationRequested, "disabling must cancel in-flight automatic work");
        pending.TrySetResult(fixture.Executor.BuildResponse("longer delay"));
        PumpFor(400);
        True(window.State.Stage != QuickSearchUiStage.Completed, "late automatic completion must not restore actions");

        fixture.Executor.NextFreeResponseTask = null;
        window.IsFreeRouteResolverForTest = () => false;
        fixture.Save(fixture.Shell with { LiveTranslateEnabled = true, LiveTranslateScope = LiveTranslateScope.FreeEngineOnly });
        window.SearchBox.Text = "scope query";
        PumpFor(650);
        Equal(2, fixture.Executor.FreeCalls.Count, "free-only scope must not auto-trigger a non-free route");
        fixture.Save(fixture.Shell with { LiveTranslateScope = LiveTranslateScope.AllEngines });
        PumpUntilCondition(() => fixture.Executor.FreeCalls.Count == 3, "scope change must reevaluate the existing draft");
    }

    private sealed class LiveWindowFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(TestIsolation.Root, $"qs-regression-{Guid.NewGuid():N}");
        private readonly ShellSettings _originalShell;
        private readonly ProviderSettings _originalCore;
        private readonly bool _originalGate = RuntimeGate.NewWorkAllowed;
        private readonly bool _originalSuppression = App.SuppressStartupForTests;
        private readonly Func<ShellSettings> _originalPolicyLoader = OutboundPolicy.SettingsLoader;
        private readonly Func<ProviderSettings>? _originalLiveLoader = OutboundPolicy.LiveSettingsLoader;
        public ShellSettings Shell { get; private set; }
        public HistoryStore History { get; }
        public VocabularyStore Vocabulary { get; }
        public FakeLiveExecutor Executor { get; } = new();
        public QuickSearchWindow Window { get; }

        public LiveWindowFixture()
        {
            EnsureApp();
            _originalShell = ShellSettingsStore.Load();
            _originalCore = CoreBridge.GetSettings();
            Directory.CreateDirectory(_directory);
            History = new HistoryStore(Path.Combine(_directory, "history.json"));
            Vocabulary = new VocabularyStore(Path.Combine(_directory, "vocabulary.json"));
            Shell = _originalShell with
            {
                LiveTranslateEnabled = true, LiveTranslateDelayMs = 300,
                LiveTranslateScope = LiveTranslateScope.FreeEngineOnly,
                FreeEngineConsent = FreeEngineConsent.Allowed, FreeEngineProvider = FreeEngineProvider.Bing,
            };
            Save(Shell);
            CoreBridge.SaveSettings(_originalCore with { NetworkEnabled = true, SafeDevMode = false });
            RuntimeGate.NewWorkAllowed = true;
            App.SuppressStartupForTests = true;
            OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load();
            OutboundPolicy.LiveSettingsLoader = () => CoreBridge.GetSettings();
            FreeTranslateService.ResetQuotaForTest();
            FreeTranslateService.ResetRateLimitStateForTest();
            App.SharedSessionStore.Clear();
            Window = new QuickSearchWindow(History, Vocabulary,
                coordinator: new TranslationCoordinator(History, Vocabulary, executor: Executor))
            {
                ForceClose = true, IsFreeRouteResolverForTest = () => true,
            };
            Window.Show();
            PumpOnce();
        }

        public void Save(ShellSettings shell)
        {
            Shell = shell;
            ShellSettingsStore.Save(shell);
        }

        public void Key(Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            Window.ModifiersOverrideForTest = modifiers;
            Window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Window)!, 0, key)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            });
        }

        public void Dispose()
        {
            Window.ForceClose = true;
            Window.Close();
            PumpOnce();
            ShellSettingsStore.Save(_originalShell);
            CoreBridge.SaveSettings(_originalCore);
            RuntimeGate.NewWorkAllowed = _originalGate;
            App.SuppressStartupForTests = _originalSuppression;
            OutboundPolicy.SettingsLoader = _originalPolicyLoader;
            OutboundPolicy.LiveSettingsLoader = _originalLiveLoader;
            FreeTranslateService.ResetQuotaForTest();
            FreeTranslateService.ResetRateLimitStateForTest();
            App.SharedSessionStore.Clear();
            try { Directory.Delete(_directory, true); } catch (IOException) { }
        }
    }

    private sealed class FakeLiveExecutor : ITranslationExecutor
    {
        public List<string> FreeCalls { get; } = [];
        public List<CancellationToken> FreeTokens { get; } = [];
        public bool NextIsPartial { get; set; }
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
            FreeTokens.Add(cancellationToken);
            if (NextFreeResponseTask is { } customTask)
            {
                return await customTask.WaitAsync(cancellationToken);
            }
            return BuildResponse(source);
        }

        public TranslationResponse BuildResponse(string source) =>
            new(
                new TranslationResult($"译:{source}", "yin", NextExplanation, [], [], IsPartial: NextIsPartial),
                new ProviderDiagnostics("free-test", ProviderType.OpenAiCompatible, "https://cn.bing.com", 1, 200, 85),
                OverrideEngineLabel: "Bing 翻译");
    }
}
