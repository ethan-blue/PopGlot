using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// 0.1.37 remediation batch T9.11–T9.19: three-surface copy/explanation parity,
/// hidden-panel terminal honesty, panel-scoped auto-copy wording, quick-search
/// retry/clear affordances, panel Ctrl+S star, and Esc-first menu dismissal.
/// Offscreen/isolated only; clipboard writes use the sanctioned
/// <see cref="Helpers.ClipboardWriterOverride"/> hook, never a real send.
/// </summary>
internal static class RemediationBatchTests
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

    private static HistoryStore IsolatedHistory => new(TestIsolation.HistoryPath);
    private static VocabularyStore IsolatedVocabulary => new(TestIsolation.VocabularyPath);

    private static void PumpOnce()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("must run on the STA harness thread");
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void PumpUntil(Task task)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("must run on the STA harness thread");
        for (var attempt = 0; attempt < 5000 && !task.IsCompleted; attempt++)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        if (!task.IsCompleted) throw new InvalidOperationException("the awaited production task never settled");
    }

    private static void PumpUntilCondition(Func<bool> condition, string message)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++)
        {
            PumpOnce();
            Thread.Sleep(10);
        }
        True(condition(), message);
    }

    private static T GetPrivate<T>(object target, string member)
    {
        var type = target.GetType();
        var field = type.GetField(member, NonPublicInstance);
        if (field is not null)
            return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"field {member} was null"));
        var prop = type.GetProperty(member, NonPublicInstance)
            ?? throw new InvalidOperationException($"member {member} not found");
        return (T)(prop.GetValue(target) ?? throw new InvalidOperationException($"property {member} was null"));
    }

    private static void InvokePrivate(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, NonPublicInstance)!.Invoke(target, args);

    private static void SetPrivate(object target, string field, object? value) =>
        target.GetType().GetField(field, NonPublicInstance)!.SetValue(target, value);

    private static string RichText(RichTextBox box) =>
        new System.Windows.Documents.TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Trim();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    // ===================== T9.11 quick-search source copy =====================

    public static void QuickSearchCopySourceKeepsWhitespace()
    {
        EnsureApp();
        var writes = new List<string>();
        var originalWriter = Helpers.ClipboardWriterOverride;
        Helpers.ClipboardWriterOverride = text =>
        {
            lock (writes) { writes.Add(text ?? string.Empty); }
            return Task.FromResult(true);
        };
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        try
        {
            window.Show();
            PumpOnce();
            // Leading/trailing whitespace must survive verbatim (T9.9 parity).
            window.SearchInputBox.Text = "  padded source  ";
            PumpOnce();
            var copySource = (Button)window.FindName("CopySourceButton")!;
            copySource.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntilCondition(() => { lock (writes) { return writes.Count >= 1; } },
                "the source copy must land on the clipboard");
            string copied;
            lock (writes) { copied = writes[0]; }
            Equal("  padded source  ", copied, "source copy must not trim the input");
            Equal("已复制原文到剪贴板", window.FooterStatusBlock.Text, "the success footer must name the source copy");
        }
        finally
        {
            Helpers.ClipboardWriterOverride = originalWriter;
            window.Close();
        }
    }

    // ===================== T9.12 selectable explanations =====================

    public static void ExplanationSurfacesAreReadOnlySelectable()
    {
        EnsureApp();
        // Floating panel: a completed session with a note shows a read-only
        // selectable TextBox; an empty note collapses the row.
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        try
        {
            panel.Show();
            PumpOnce();
            True(panel.ExplanationText is TextBoxBase, "the panel explanation must be a selectable TextBoxBase");
            True(panel.ExplanationText.IsReadOnly, "the panel explanation must be read-only");
            InvokePrivate(panel, "HandleSessionResultAsync", "demo source", new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                SourceText = "demo source",
                TranslatedText = "译文",
                Explanation = "补充说明",
            }, 0L, null);
            PumpOnce();
            Equal(Visibility.Visible, panel.ExplanationBox.Visibility, "a carried note must show the explanation row");
            Equal("补充说明", panel.ExplanationText.Text, "the note text is restored verbatim");

            InvokePrivate(panel, "HandleSessionResultAsync", "demo source", new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                SourceText = "demo source",
                TranslatedText = "译文",
                Explanation = "",
            }, 0L, null);
            PumpOnce();
            Equal(Visibility.Collapsed, panel.ExplanationBox.Visibility, "an empty note must collapse the explanation row");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }

        // Workbench: same contract on the restored explanation row.
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        True(section.ExplanationText is TextBoxBase, "the workbench explanation must be a selectable TextBoxBase");
        True(section.ExplanationText.IsReadOnly, "the workbench explanation must be read-only");
        section.FocusTranslate("source", "en", "zh-CN", "译文", TranslationSessionState.Completed, explanation: "载入说明");
        Equal(Visibility.Visible, section.ExplanationBox.Visibility, "a carried workbench note must show");
        Equal("载入说明", section.ExplanationText.Text, "the workbench note text is restored verbatim");
        section.FocusTranslate("source", "en", "zh-CN", "译文", TranslationSessionState.Completed, explanation: "");
        Equal(Visibility.Collapsed, section.ExplanationBox.Visibility, "an empty workbench note must collapse");
    }

    // ===================== T9.14 hidden panel keeps its terminal =====================

    public static void HiddenPanelCompletionRendersTerminalOnRestore()
    {
        EnsureApp();
        var writes = new List<string>();
        var originalWriter = Helpers.ClipboardWriterOverride;
        Helpers.ClipboardWriterOverride = text =>
        {
            lock (writes) { writes.Add(text ?? string.Empty); }
            return Task.FromResult(true);
        };
        var executor = new BatchFakeExecutor();
        var completion = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.OnStreamTextDraft = (_, _, _, _, _, sessionId, epoch, _) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, completion.Task);
        };
        var coordinator = new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary, executor: executor);
        var settings = ShellSettings.Default with { CopyTranslationAutomatically = true };
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => settings, null, null, IsolatedVocabulary, coordinator: coordinator);
        try
        {
            panel.Show();
            PumpOnce();
            var start = panel.StartTextAsync("hidden terminal source");
            PumpUntilCondition(() => executor.DraftCalls >= 1, "the real request must reach the executor");

            // Hide mid-flight: the request keeps running (focus-loss does not cancel).
            panel.Hide();
            PumpOnce();
            True(!panel.IsVisible, "setup: the panel is hidden while the request runs");

            // Complete while hidden. The terminal must land on the gate and render.
            completion.SetResult(new TranslationResponse(
                new TranslationResult("隐藏期间完成的译文", "", "", [], []),
                new ProviderDiagnostics("hidden", ProviderType.OpenAiCompatible, "https://loopback.test/v1", 1, 200, 10)));
            PumpUntil(start);
            Equal(TranslationPanelStage.Completed, GetPrivate<TranslationPanelStreamGate>(panel, "_gate").Stage,
                "the hidden completion must land on the gate, not be dropped");
            lock (writes)
            {
                Equal(0, writes.Count, "a completion while hidden must never auto-copy");
            }

            // Restore: the terminal is already painted — no skeleton, no replayed copy.
            panel.Show();
            PumpOnce();
            Equal(Visibility.Collapsed, panel.ResultSkeleton.Visibility, "the restored panel must not be stuck on the skeleton");
            Equal("隐藏期间完成的译文", RichText(panel.FinalRichBox), "the restored panel shows the terminal translation");
            lock (writes)
            {
                Equal(0, writes.Count, "restoring must not replay the missed auto-copy");
            }
        }
        finally
        {
            Helpers.ClipboardWriterOverride = originalWriter;
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    // ===================== T9.15 panel-scoped auto-copy wording =====================

    public static void AutoCopyWordingIsScopedToFloatingPanel()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary)
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            window.Show();
            window.ShowPage("General");
            PumpOnce();
            var general = (GeneralSection)window.FindName("GeneralSection")!;
            var texts = Descendants<TextBlock>(general).Select(t => t.Text).ToList();
            True(texts.Contains("浮窗自动复制"), "the auto-copy title must name the floating panel");
            True(texts.Contains("划词/截图浮窗翻译完成后复制到剪贴板"),
                "the auto-copy hint must scope the behaviour to selection/screenshot panels");
            True(!texts.Contains("自动复制译文"), "the unscoped title must be gone");
        }
        finally
        {
            window.ForceClose = true;
            window.Close();
        }
    }

    // ===================== T9.16 quick-search failed retry =====================

    public static void QuickSearchFailedStateOffersRetryThatReissues()
    {
        EnsureApp();
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        try
        {
            window.Show();
            PumpOnce();
            window.SearchInputBox.Text = "retry query";
            PumpOnce();

            // Drive a genuine failed terminal through the state machine.
            window.State.StartNewSearch("retry query");
            var epoch = window.State.CurrentEpoch;
            window.State.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(TranslationErrorKind.ServerError, "服务不可用", "可在设置中切换引擎"),
            }, epoch, "retry query");
            InvokePrivate(window, "SyncUiWithState");
            PumpOnce();

            Equal(QuickSearchUiStage.Failed, window.State.Stage, "setup: the surface is failed");
            var errorCard = (Border)window.FindName("ErrorCard")!;
            Equal(Visibility.Visible, errorCard.Visibility, "the failed state must show the error card");
            var retry = (Button)window.FindName("ErrorRetryBtn")!;
            True(retry.Visibility == Visibility.Visible, "the error card must offer a retry button");
            True(window.State.HintsText.Contains("Enter 重试", StringComparison.Ordinal),
                "the failed hints must advertise the Enter retry");

            // Click retry: a fresh search epoch proves a new request was issued.
            retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();
            True(window.State.CurrentEpoch > epoch, "clicking retry must start a new search epoch (a new request)");
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== T9.17 quick-search clear button =====================

    public static void QuickSearchClearButtonEmptiesInput()
    {
        EnsureApp();
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        try
        {
            window.Show();
            PumpOnce();
            var clear = (Button)window.FindName("SearchClearButton")!;
            Equal(Visibility.Collapsed, clear.Visibility, "an empty input must hide the clear button");

            window.SearchInputBox.Text = "something to clear";
            PumpOnce();
            Equal(Visibility.Visible, clear.Visibility, "a non-empty input must show the clear button");

            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();
            Equal(string.Empty, window.SearchInputBox.Text, "clicking clear must empty the input");
            Equal(Visibility.Collapsed, clear.Visibility, "the clear button hides again once empty");
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== T9.18 panel Ctrl+S star =====================

    public static void PanelCtrlSStarsCompletedButNotPartial()
    {
        EnsureApp();
        var vocab = IsolatedVocabulary;
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, vocab)
        { ShowInTaskbar = false };
        try
        {
            panel.Show();
            PumpOnce();
            panel.Activate();
            Keyboard.Focus(panel);
            PumpOnce();
            var sourceLang = GetPrivate<string>(panel, "SourceLanguage");
            var targetLang = GetPrivate<string>(panel, "TargetLanguage");

            // Completed: a real Ctrl+S must star through the same path as the button.
            panel.SourceInputBox.Text = "ctrl s source";
            InvokePrivate(panel, "HandleSessionResultAsync", "ctrl s source", new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                SourceText = "ctrl s source",
                TranslatedText = "收藏译文",
            }, 0L, null);
            PumpOnce();
            True(panel.StarToggle.IsEnabled, "setup: the completed panel allows starring");
            Keyboard.Focus(panel);
            PumpOnce();
            True(SendCtrlSTo(panel), "the real Ctrl+S keystroke must be injected");
            PumpUntilCondition(() => vocab.IsStarred("ctrl s source", sourceLang, targetLang),
                "Ctrl+S on a completed panel must star the entry");

            // Partial: the gate must block the star even though the key is handled.
            panel.SourceInputBox.Text = "partial source";
            InvokePrivate(panel, "HandleSessionResultAsync", "partial source", new TranslationSession
            {
                Stage = TranslationSessionStage.Partial,
                SourceText = "partial source",
                TranslatedText = "半截译文",
                Error = new TranslationError(TranslationErrorKind.ServerError, "连接中断"),
            }, 0L, null);
            PumpOnce();
            True(!panel.StarToggle.IsEnabled, "setup: the partial panel gates starring");
            Keyboard.Focus(panel);
            PumpOnce();
            True(SendCtrlSTo(panel), "the real Ctrl+S keystroke must be injected");
            for (var i = 0; i < 20; i++) { PumpOnce(); Thread.Sleep(10); }
            True(!vocab.IsStarred("partial source", sourceLang, targetLang),
                "Ctrl+S on a partial panel must not star");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    // ===================== T9.19 Esc closes menus first =====================

    public static void PanelEscClosesRouteMenuWithoutCancellingOrHiding()
    {
        EnsureApp();
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-t919") { Width = 8, Height = 8 });
        try
        {
            panel.Show();
            PumpOnce();
            var cts = new CancellationTokenSource();
            SetPrivate(panel, "_operation", cts);

            // Open the route menu through the real button click.
            panel.RouteSelectorButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();
            var menu = GetPrivate<ContextMenu>(panel, "_routeMenu");
            True(menu.IsOpen, "setup: the route menu is open");

            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            panel.RaiseEvent(esc);
            PumpOnce();
            True(!menu.IsOpen, "Esc must close the open route menu");
            True(!cts.IsCancellationRequested, "Esc with an open menu must not cancel the in-flight request");
            True(panel.IsVisible, "Esc with an open menu must not hide the panel");
        }
        finally
        {
            source.Dispose();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    public static void WorkbenchEscClosesRestoreMenuFirst()
    {
        EnsureApp();
        var store = App.SharedSessionStore;
        store.Clear();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section, Width = 720, Height = 620,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-t919w") { Width = 8, Height = 8 });
        try
        {
            store.TryStore(StoredSession.Create(null, SessionOrigin.Workbench, "暂存原文", "en", "zh-CN",
                null, null, TranslationSessionState.Completed, "暂存译文", null), out _);
            host.Show();
            PumpOnce();
            var restore = (Button)section.FindName("RestoreSessionButton")!;
            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();
            var menu = GetPrivate<ContextMenu>(section, "_restoreMenu");
            True(menu.IsOpen, "setup: the restore menu is open");

            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            section.RaiseEvent(esc);
            PumpOnce();
            True(!menu.IsOpen, "Esc must close the open restore menu first");
            True(esc.Handled, "the workbench must handle the menu-dismissing Esc");
        }
        finally
        {
            source.Dispose();
            store.Clear();
            host.Close();
        }
    }

    // ===================== T9.20 library-page engine switch =====================

    public static void LibraryPageEngineSwitchDoesNotRetranslate()
    {
        EnsureApp();
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-t920-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        CoreBridge.Initialize();
        // Two loopback profiles (port 9 = discard, connection refused): the
        // switch is executable, and any accidental request fails locally —
        // never a real send.
        var profileA = new ProviderProfile
        {
            Id = "t920-a", Name = "引擎 A", SupportsText = true, TextModel = "model-a",
            ApiBaseUrl = "http://127.0.0.1:9/v1", CredentialTarget = "PopGlot/provider/t920-a", IsLocal = true,
        };
        var profileB = new ProviderProfile
        {
            Id = "t920-b", Name = "引擎 B", SupportsText = true, TextModel = "model-b",
            ApiBaseUrl = "http://127.0.0.1:9/v1", CredentialTarget = "PopGlot/provider/t920-b", IsLocal = true,
        };
        ProfileManager.Save(new CoreProductConfig { Profiles = [profileA, profileB], ActiveProfileId = profileA.Id });
        ProfileManager.ApplyActiveToCore(ProfileManager.Load());
        ProfileManager.BumpRevision();

        var window = new MainWindow(ShellSettings.Default,
            new HistoryStore(Path.Combine(dir, "history.json")),
            new VocabularyStore(Path.Combine(dir, "vocabulary.json")))
        {
            AllowClose = true, ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            window.Show();
            PumpOnce();
            window.TranslateSection.InputBox.Text = "library switch source";
            PumpOnce();

            // Library page: switching the engine must NOT retranslate the draft.
            window.NavLibrary.IsChecked = true;
            PumpOnce();
            True(!window.TranslateSection.IsVisible, "setup: the workbench is hidden on the library page");
            var epochBeforeLibrarySwitch = window.TranslateSection.CurrentEpoch;
            ClickEngineChoice(window, profileB.Id);
            for (var i = 0; i < 30; i++) { PumpOnce(); Thread.Sleep(10); }
            Equal(epochBeforeLibrarySwitch, window.TranslateSection.CurrentEpoch,
                "switching engines on the library page must not start a workbench translation");
            Equal(TranslateUiPhase.Idle, window.TranslateSection.CurrentState.Phase,
                "the hidden workbench must stay idle after a library-page engine switch");

            // Back to the workbench with source: switching now retranslates.
            window.NavTranslate.IsChecked = true;
            PumpOnce();
            True(window.TranslateSection.IsVisible, "setup: the workbench is visible again");
            var epochBeforeWorkbenchSwitch = window.TranslateSection.CurrentEpoch;
            ClickEngineChoice(window, profileA.Id);
            PumpUntilCondition(() => window.TranslateSection.CurrentEpoch > epochBeforeWorkbenchSwitch,
                "switching engines on the workbench with source must start a retranslation");

            // Empty source: even on the workbench, no request is issued.
            window.TranslateSection.InputBox.Clear();
            PumpOnce();
            var epochBeforeEmptySwitch = window.TranslateSection.CurrentEpoch;
            ClickEngineChoice(window, profileB.Id);
            for (var i = 0; i < 30; i++) { PumpOnce(); Thread.Sleep(10); }
            Equal(epochBeforeEmptySwitch, window.TranslateSection.CurrentEpoch,
                "an empty workbench source must not trigger a retranslation on engine switch");
        }
        finally
        {
            window.Close();
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Clicks the real engine-switch menu item for a profile id.</summary>
    private static void ClickEngineChoice(MainWindow window, string profileId)
    {
        var menu = window.BuildEngineSwitchMenu();
        var item = menu.Items.OfType<MenuItem>().Single(i => i.Tag is string tag && tag == profileId);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpOnce();
    }

    // ===================== T9.21 mid-stream source editing =====================

    public static void PanelSourceStaysEditableMidStreamAndCancelsOldRequest()
    {
        EnsureApp();
        var executor = new BatchFakeExecutor();
        var completion = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken capturedToken = default;
        executor.OnStreamTextDraft = (_, _, _, _, _, sessionId, epoch, token) =>
        {
            capturedToken = token;
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, completion.Task);
        };
        var coordinator = new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary, executor: executor);
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary, coordinator: coordinator);
        try
        {
            panel.Show();
            PumpOnce();
            var start = panel.StartTextAsync("original source");
            PumpUntilCondition(() => executor.DraftCalls >= 1, "the real request must reach the executor");
            var gate = GetPrivate<TranslationPanelStreamGate>(panel, "_gate");
            var epochBefore = gate.CurrentEpoch;

            // Mid-stream the source must stay editable (no read-only coupling).
            True(!panel.SourceInputBox.IsReadOnly, "the panel source must remain editable while a request runs");
            True(!capturedToken.IsCancellationRequested, "setup: the request is still in flight");

            // A genuine user edit cancels the old request and fences the epoch.
            panel.SourceInputBox.Text = "edited source";
            PumpOnce();
            True(capturedToken.IsCancellationRequested, "editing the source mid-stream must cancel the old request");
            True(gate.CurrentEpoch > epochBefore, "the edit must fence the epoch so queued callbacks are invalidated");
            Equal(TranslationPanelStage.Idle, gate.Stage, "the edit must reset the gate to idle");
            True(panel.StatusTextBlock.Text.Contains("原文已修改"),
                "the status must tell the user the source changed and how to retranslate");

            // The cancelled request settles without painting a stale terminal.
            completion.TrySetCanceled();
            PumpUntil(start);
            Equal(TranslationPanelStage.Idle, gate.Stage, "the cancelled request must not repaint a terminal over the edit");
            Equal("edited source", panel.SourceInputBox.Text, "the user's edited source must survive the cancel");
        }
        finally
        {
            completion.TrySetCanceled();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    // ===================== T9.22 translate-key hints follow the binding =====================

    public static void TranslateKeyHintsFollowRecordedBinding()
    {
        EnsureApp();
        var previous = InterfaceKeys.Snapshot;
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        var window = new MainWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary)
        {
            AllowClose = true, ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            panel.Show();
            window.Show();
            PumpOnce();

            // Default binding is Ctrl+Enter: hints show it.
            True(Ui.GetPlaceholder(panel.SourceInputBox).Contains("Ctrl+Enter"),
                "the default panel placeholder must name Ctrl+Enter");
            True(window.TranslateSection.ShortcutHint.Text.Contains("Ctrl+Enter"),
                "the default workbench hint must name Ctrl+Enter");

            // Rebind translate to Alt+Enter: every hint surface must follow.
            InterfaceKeys.Apply(previous with { InterfaceTranslateHotkey = new HotkeyBinding(HotkeyBinding.ModAlt, 0x0D) });
            PumpOnce();
            var placeholder = Ui.GetPlaceholder(panel.SourceInputBox);
            True(placeholder.Contains("Alt+Enter") && !placeholder.Contains("Ctrl+Enter"),
                $"the panel placeholder must follow the recorded binding, got '{placeholder}'");
            var help = System.Windows.Automation.AutomationProperties.GetHelpText(panel.SourceInputBox);
            True(help.Contains("Alt+Enter"), $"the panel source HelpText must follow the binding, got '{help}'");
            var toolbar = window.TranslateSection.ShortcutHint.Text;
            True(toolbar.Contains("Alt+Enter") && !toolbar.Contains("Ctrl+Enter"),
                $"the workbench toolbar hint must follow the binding, got '{toolbar}'");
            var inputHelp = System.Windows.Automation.AutomationProperties.GetHelpText(window.TranslateSection.InputBox);
            True(inputHelp.Contains("Alt+Enter"), $"the workbench input HelpText must follow the binding, got '{inputHelp}'");

            // The idle status line names the recorded key too.
            InvokePrivate(panel, "RenderIdle");
            True(panel.StatusTextBlock.Text.Contains("Alt+Enter"),
                $"the panel idle status must name the recorded key, got '{panel.StatusTextBlock.Text}'");
        }
        finally
        {
            InterfaceKeys.Apply(previous);
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            window.Close();
        }
    }

    // ===================== T9.24 copy/star shortcut tooltip parity =====================

    public static void CopyStarShortcutTooltipsMatchAcrossSurfaces()
    {
        EnsureApp();
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        var quick = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        try
        {
            panel.Show();
            quick.Show();
            PumpOnce();

            // Copy buttons all advertise the fixed Ctrl+Shift+C.
            Equal("复制译文 (Ctrl+Shift+C)", panel.ResultCopyBtn.ToolTip,
                "the panel copy tooltip must name Ctrl+Shift+C");
            Equal("复制译文 (Ctrl+Shift+C)", quick.CopyButton.ToolTip,
                "the quick-search copy tooltip must name Ctrl+Shift+C");
            Equal("复制译文 (Ctrl+Shift+C)", ((Button)section.FindName("TranslateResultCopyButton")!).ToolTip,
                "the workbench copy tooltip must name Ctrl+Shift+C");

            // Star controls all advertise the fixed Ctrl+S on the unstarred frame.
            Equal("收藏到生词本 (Ctrl+S)", panel.StarToggle.ToolTip,
                "the panel star tooltip must name Ctrl+S");
            Equal("收藏到生词本 (Ctrl+S)", quick.StarButton.ToolTip,
                "the quick-search star tooltip must name Ctrl+S");
            Equal("收藏到生词本 (Ctrl+S)", section.StarButton.ToolTip,
                "the workbench star tooltip must name Ctrl+S");

            // The starred (remove) state keeps the same key on every surface.
            InvokePrivate(panel, "UpdateStarIcon", true);
            InvokePrivate(section, "UpdateStarVisualState", true);
            Equal("从生词本移除 (Ctrl+S)", panel.StarToggle.ToolTip,
                "the panel remove tooltip must keep Ctrl+S");
            Equal("从生词本移除 (Ctrl+S)", section.StarButton.ToolTip,
                "the workbench remove tooltip must keep Ctrl+S");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            quick.Close();
        }
    }

    // ===================== T9.25 pin tooltip reflects focus-loss policy =====================

    public static void PinTooltipStatesFocusLossPolicyHonestly()
    {
        EnsureApp();
        // Default policy: focus loss does NOT hide the panel — the pin button
        // must say so instead of implying a hide action that never happens.
        var defaultPanel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        try
        {
            defaultPanel.Show();
            PumpOnce();
            var tooltip = (string)GetPrivate<ToggleButton>(defaultPanel, "PinToggle").ToolTip;
            True(tooltip.Contains("当前不会失焦收起"),
                $"the default pin tooltip must state the panel does not hide on focus loss, got '{tooltip}'");
        }
        finally
        {
            defaultPanel.ForceClose = true;
            try { defaultPanel.Close(); } catch { }
        }

        // Opt-in auto-hide: the pin button now genuinely prevents hiding.
        var autoHidePanel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default with { ClosePanelOnFocusLoss = true }, null, null, IsolatedVocabulary);
        try
        {
            autoHidePanel.Show();
            PumpOnce();
            var tooltip = (string)GetPrivate<ToggleButton>(autoHidePanel, "PinToggle").ToolTip;
            True(tooltip.Contains("失焦不隐藏") && !tooltip.Contains("当前不会失焦收起"),
                $"the auto-hide pin tooltip must describe the real pin effect, got '{tooltip}'");
        }
        finally
        {
            autoHidePanel.ForceClose = true;
            try { autoHidePanel.Close(); } catch { }
        }
    }

    // ===================== T9.26 status hover keeps engine/style detail =====================

    public static void WorkbenchStatusHoverKeepsEngineStyleDetail()
    {
        EnsureApp();
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-t926-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        CoreBridge.Initialize();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(new HistoryStore(Path.Combine(dir, "history.json"))), vocabulary: null);
        var host = new Window
        {
            Content = section, Width = 900, Height = 680,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            host.Show();
            PumpOnce();

            // Drive a completed translation through the real reducer + ApplyState.
            var session = new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                TranslatedText = "完成的译文",
                PipelineLabel = "测试引擎",
                PromptTemplateName = "正式风格",
            };
            var started = TranslateSectionReducer.StartTranslation(TranslateUiState.Initial, 1);
            section.ApplyState(started);
            section.ApplyState(TranslateSectionReducer.ApplyCompletion(started, session, 1));
            section.EngineBadge.ToolTip = "引擎：测试引擎\n风格：正式风格";
            PumpOnce();

            // The status ToolTip must remain the structured object (not a string).
            var tooltip = section.StatusBlock.ToolTip as ToolTip;
            True(tooltip is not null, "the status tooltip must stay a structured ToolTip, not a replaced string");
            tooltip!.IsOpen = true;
            PumpOnce();
            var detail = Descendants<TextBlock>(tooltip).FirstOrDefault(b => b.Name == "TranslateStatusDetail");
            True(detail is not null, "the structured tooltip must contain the detail text block");
            True(detail!.Text.Contains("测试引擎") && detail.Text.Contains("正式风格"),
                $"the hover detail must expose the engine and style, got '{detail.Text}'");
            tooltip.IsOpen = false;

            // A summary error writes the detail block without destroying the
            // structured tooltip; clearing it restores engine/style reachability.
            InvokePrivate(section, "SetSummaryStatusDetail", "连接失败原因");
            True(ReferenceEquals(tooltip, section.StatusBlock.ToolTip),
                "writing an error detail must not replace the structured ToolTip object");
            tooltip.IsOpen = true;
            PumpOnce();
            detail = Descendants<TextBlock>(tooltip).FirstOrDefault(b => b.Name == "TranslateStatusDetail");
            True(detail!.Text.Contains("连接失败原因"),
                $"the error detail must be readable in the structured tooltip, got '{detail.Text}'");
            tooltip.IsOpen = false;

            InvokePrivate(section, "SetSummaryStatusDetail", new object?[] { null });
            tooltip.IsOpen = true;
            PumpOnce();
            detail = Descendants<TextBlock>(tooltip).FirstOrDefault(b => b.Name == "TranslateStatusDetail");
            True(detail!.Text.Contains("测试引擎"),
                $"clearing the detail must restore the engine/style binding, got '{detail.Text}'");
            tooltip.IsOpen = false;
        }
        finally
        {
            host.Close();
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // ===================== T9.27 over-budget counter warning tone =====================

    public static void WorkbenchCounterWarnsNearInputBudget()
    {
        EnsureApp();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section, Width = 900, Height = 680,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            host.Show();
            PumpOnce();
            var counter = (TextBlock)section.FindName("TranslateCounter")!;
            var warning = section.TryFindResource("WarningBrush");
            var tertiary = section.TryFindResource("TextTertiaryBrush");
            var budget = CoreBridge.MaxSegmentChars * CoreBridge.MaxSegments;

            // Comfortable usage stays tertiary.
            section.InputBox.Text = new string('a', 100);
            PumpOnce();
            True(ReferenceEquals(counter.Foreground, tertiary),
                "a short input counter must stay the neutral tertiary tone");

            // At/above 90% of the session input budget the counter turns warning.
            section.InputBox.Text = new string('a', budget * 9 / 10);
            PumpOnce();
            True(ReferenceEquals(counter.Foreground, warning),
                "the counter must turn warning near the single-session input budget");

            // Over the budget stays warning (the request will be rejected).
            section.InputBox.Text = new string('a', budget + 1);
            PumpOnce();
            True(ReferenceEquals(counter.Foreground, warning),
                "an over-budget input counter must keep the warning tone");
        }
        finally
        {
            host.Close();
        }
    }

    // ===================== T9.28 new translation resets result scroll =====================

    public static void NewTranslationResetsResultScroll()
    {
        EnsureApp();
        var longText = string.Join("\n", Enumerable.Repeat("这是一段较长的译文行。", 120));

        // Workbench: scroll a long result to the bottom, then start a new request.
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section, Width = 720, Height = 480,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            host.Show();
            PumpOnce();
            var completed = TranslateSectionReducer.ApplyCompletion(
                TranslateSectionReducer.StartTranslation(TranslateUiState.Initial, 1),
                new TranslationSession { Stage = TranslationSessionStage.Completed, TranslatedText = longText }, 1);
            section.ApplyState(completed);
            PumpOnce();
            section.ResultScroll.ScrollToEnd();
            PumpOnce();
            True(section.ResultScroll.VerticalOffset > 0, "setup: the long workbench result is scrolled down");
            section.ApplyState(TranslateSectionReducer.StartTranslation(TranslateUiState.Initial, 2));
            PumpOnce();
            Equal(0d, section.ResultScroll.VerticalOffset,
                "a new workbench translation must reset the result scroll to the top");
        }
        finally
        {
            host.Close();
        }

        // Floating panel: the same reset on the plain result box.
        var panel = new TranslationPanelWindow(new Rect(100, 100, 320, 300),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        try
        {
            panel.Show();
            PumpOnce();
            panel.StreamTextBox.Text = longText;
            panel.StreamTextBox.Visibility = Visibility.Visible;
            PumpOnce();
            var scroll = Ui.FindScrollViewer(panel.StreamTextBox);
            True(scroll is not null, "setup: the panel result box has a scroll host");
            scroll!.ScrollToEnd();
            PumpOnce();
            True(scroll.VerticalOffset > 0, "setup: the long panel result is scrolled down");
            InvokePrivate(panel, "RenderPreparingState", "正在翻译");
            PumpOnce();
            Equal(0d, scroll.VerticalOffset,
                "a new panel translation must reset the result scroll to the top");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    // ===================== T9.29 quick-search single-layer result scroll =====================

    public static void QuickSearchLongResultScrollsOneLayerOnly()
    {
        EnsureApp();
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary)
        {
            ForceClose = true, ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            window.Show();
            PumpOnce();
            var longText = string.Join("\n", Enumerable.Repeat("这是一段较长的查词结果行。", 120));
            window.State.StartNewSearch("long query");
            window.State.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                TranslatedText = longText,
            }, window.State.CurrentEpoch, "long query");
            // Render the long result the same way the completion handler does.
            MarkdownPresenter.RenderToFlowDocument(window.RichBox.Document, longText,
                Application.Current?.Resources ?? window.Resources, resultActionsEnabled: true);
            InvokePrivate(window, "SyncUiWithState");
            PumpOnce();
            window.UpdateLayout();

            // The outer container is the single scroll host; the inner result
            // boxes no longer cap their height or scroll independently.
            var outer = (ScrollViewer)window.FindName("ResultScroll")!;
            True(outer.ScrollableHeight > 0, "the long result must overflow the outer scroll container");
            Equal(ScrollBarVisibility.Disabled, window.StreamBox.VerticalScrollBarVisibility,
                "the streaming box must not scroll independently");
            Equal(ScrollBarVisibility.Disabled, window.RichBox.VerticalScrollBarVisibility,
                "the rich result box must not scroll independently");
            var innerStream = Ui.FindScrollViewer(window.StreamBox);
            var innerRich = Ui.FindScrollViewer(window.RichBox);
            True(innerStream is null || innerStream.ScrollableHeight <= 0,
                "the streaming box must have no scrollable overflow of its own");
            True(innerRich is null || innerRich.ScrollableHeight <= 0,
                "the rich result box must have no scrollable overflow of its own");

            // Scrolling the outer container reaches the end of the long result.
            outer.ScrollToEnd();
            PumpOnce();
            True(outer.VerticalOffset > 0, "the outer container must scroll the long result");
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== T9.30 partial-with-error shows the recovery card =====================

    public static void QuickSearchPartialWithErrorShowsRecoveryCard()
    {
        EnsureApp();
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        try
        {
            window.Show();
            PumpOnce();

            // Partial with a non-truncation error: the error card (with the
            // settings recovery entry) must appear, not just a footer note.
            window.State.StartNewSearch("partial query");
            window.State.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Partial,
                TranslatedText = "部分译文",
                Error = new TranslationError(TranslationErrorKind.ServerError, "服务暂时不可用", "稍后重试"),
            }, window.State.CurrentEpoch, "partial query");
            InvokePrivate(window, "SyncUiWithState");
            PumpOnce();
            Equal(QuickSearchUiStage.Partial, window.State.Stage, "setup: the surface is partial");
            var errorCard = (Border)window.FindName("ErrorCard")!;
            Equal(Visibility.Visible, errorCard.Visibility,
                "a partial with an error must show the error card");
            var settings = (Button)window.FindName("ErrorSettingsBtn")!;
            Equal("打开设置", settings.Content, "the error card must keep the settings recovery entry");

            // Truncation (warning tone) keeps the badge, not a duplicate error card.
            window.State.StartNewSearch("truncated query");
            window.State.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Partial,
                TranslatedText = "截断的译文",
                Error = new TranslationError(TranslationErrorKind.Unknown, "输出达到长度上限", null),
            }, window.State.CurrentEpoch, "truncated query");
            InvokePrivate(window, "SyncUiWithState");
            PumpOnce();
            Equal(Visibility.Collapsed, errorCard.Visibility,
                "a warning-tone truncation keeps the incomplete badge instead of a duplicate error card");
        }
        finally
        {
            window.Close();
        }
    }

    // ===================== T9.31 empty-state privacy copy avoids absolutes =====================

    public static void EmptyStatePrivacyCopyAvoidsAbsolutePromise()
    {
        EnsureApp();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section, Width = 720, Height = 620,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000,
        };
        try
        {
            host.Show();
            PumpOnce();
            section.RefreshAfterSettingsChanged();
            PumpOnce();
            var description = (TextBlock)section.FindName("GuideDescription")!;
            Equal("无需配置即可使用公共直译；仅发送待翻译文本，不发送截图或本地数据。", description.Text,
                "the empty-state copy must state the concrete data boundary, not an absolute privacy promise");
            True(!description.Text.Contains("保障隐私安全"),
                "the absolute privacy promise must be gone");
        }
        finally
        {
            host.Close();
        }
    }

    // ===================== T9.32 panel empty-input retry gives a hint =====================

    public static void PanelEmptyInputRetryShowsHint()
    {
        EnsureApp();
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        try
        {
            panel.Show();
            PumpOnce();
            // Empty source, no prior request: retry must not be a silent no-op.
            True(string.IsNullOrEmpty(panel.SourceInputBox.Text), "setup: the panel source is empty");
            GetPrivate<Button>(panel, "RetryButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpOnce();
            Equal("请先输入要翻译的内容。", panel.StatusTextBlock.Text,
                "retrying with empty input must tell the user to enter content first");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    // ===================== T9.34 partial snapshot restores as incomplete =====================

    public static void QuickSearchPartialSnapshotRestoresAsIncomplete()
    {
        EnsureApp();
        App.SharedSessionStore.Clear();
        var window = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary) { ForceClose = true };
        var panel = new TranslationPanelWindow(new Rect(100, 100, 20, 20),
            IsolatedHistory, () => ShellSettings.Default, null, null, IsolatedVocabulary);
        try
        {
            window.Show();
            PumpOnce();
            window.State.StartNewSearch("partial snapshot query");
            window.State.OnSessionCompleted(new TranslationSession
            {
                Stage = TranslationSessionStage.Partial,
                TranslatedText = "保留的部分译文",
                Error = new TranslationError(TranslationErrorKind.Unknown, "输出达到长度上限", null),
            }, window.State.CurrentEpoch, "partial snapshot query");
            window.StoreSessionSnapshot();

            var stored = App.SharedSessionStore.PeekRecent();
            True(stored is not null, "the partial session must be stored");
            True(stored!.IsPartial, "the partial snapshot must be flagged incomplete");
            True(stored.State != TranslationSessionState.Failed,
                "a partial snapshot must not be stored as a failure");
            Equal("保留的部分译文", stored.ResultText, "the partial text must be preserved");

            // Restoring marks the content incomplete (not failed) and keeps the text.
            panel.Show();
            PumpOnce();
            panel.RestoreSession(stored);
            PumpOnce();
            Equal("已恢复未完成内容（未重发）", panel.StatusTextBlock.Text,
                "restoring a partial must say incomplete, not failed");
            Equal("保留的部分译文", RichText(panel.FinalRichBox),
                "the restored partial text must be preserved");
        }
        finally
        {
            window.Close();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            App.SharedSessionStore.Clear();
        }
    }

    // ===================== T9.35 connection test failure reason =====================

    public static void ConnectionTestFailurePrefersTruncationReason()
    {
        EnsureApp();
        ProfileManager.ResetForTests();
        ConnectionTestRunner.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-t935-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        CoreBridge.Initialize();
        var section = new ServicesSection();
        var host = new Window { Content = section, Width = 960, Height = 700,
            ShowActivated = false, Left = -12000, Top = -12000 };
        try
        {
            host.Show();
            PumpOnce();
            var profile = new ProviderProfile
            {
                Id = "t935-fixture", Name = "T935 fixture",
                ExecutionKind = EngineExecutionKindNames.ChatGptDirect, TextModel = "saved-model",
                DirectConnection = new() { AccountId = "isolated-fixture-account", ModelId = "saved-model", Enabled = true },
            };
            ProfileManager.Save(new CoreProductConfig { Profiles = [profile] });
            section.SavedConnectionTestExecutor = (_, _) => Task.FromResult(new TranslationResponse(
                new TranslationResult("部分译文", "", "", [],
                    ["中性提示：这是一条建议性说明", "输出达到长度上限，内容不完整"], IsPartial: true),
                new ProviderDiagnostics("t935-fixture", ProviderType.OpenAiResponses, "isolated-fixture", 1, 200, 1)));
            string? failureMessage = null;
            section.StatusChanged += (message, tone) =>
            {
                if (tone == StatusTone.Error) failureMessage = message;
            };
            var button = new Button { Tag = profile.Id };
            typeof(ServicesSection).GetMethod("TestProfile_Click", NonPublicInstance)!
                .Invoke(section, [button, new RoutedEventArgs()]);
            PumpUntilCondition(() => failureMessage is not null,
                "the failing connection test must report a status");
            True(failureMessage!.Contains("输出达到长度上限，内容不完整"),
                "a truncation-caused test failure must show the truncation reason, not the neutral note");
            True(!failureMessage.Contains("中性提示"),
                "the neutral advisory note must not masquerade as the failure reason");
        }
        finally
        {
            host.Close();
            ProfileManager.ConfigPathOverride = null;
            ProfileManager.ResetForTests();
            ConnectionTestRunner.ResetForTests();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ===================== real keyboard input =====================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, WindowsSelectionClipboardAdapter.NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    private static WindowsSelectionClipboardAdapter.NativeInput KeyInput(ushort vk, uint flags) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Union = new WindowsSelectionClipboardAdapter.NativeInputUnion
        {
            Keyboard = new WindowsSelectionClipboardAdapter.NativeKeyboardInput { VirtualKey = vk, Flags = flags },
        },
    };

    /// <summary>
    /// A genuine Ctrl+S through the OS input pipeline. The harness STA thread is
    /// attached to the foreground thread's input queue so the injected modifier
    /// state is visible to <c>Keyboard.Modifiers</c> (which reads the thread key
    /// state), and the panel is brought to the foreground so SendInput delivers
    /// the keystroke to it. Requires an unlocked, idle workstation — the same
    /// precondition the suite's real-mouse acceptance already relies on. Returns
    /// true only when the panel owns the foreground and every injection succeeded.
    /// </summary>
    private static bool SendCtrlSTo(Window window)
    {
        const ushort vkControl = 0x11;
        const ushort vkS = 0x53;
        const uint keyUp = 0x0002;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            window.Activate();
            SetForegroundWindow(hwnd);
            window.Focus();
            Keyboard.Focus(window);
            PumpOnce();
            for (var attempt = 0; attempt < 50 && GetForegroundWindow() != hwnd; attempt++)
            {
                SetForegroundWindow(hwnd);
                PumpOnce();
                Thread.Sleep(10);
            }
            if (GetForegroundWindow() != hwnd) return false;

            var inputs = new[]
            {
                KeyInput(vkControl, 0),
                KeyInput(vkS, 0),
                KeyInput(vkS, keyUp),
                KeyInput(vkControl, keyUp),
            };
            return SendInput((uint)inputs.Length, inputs, WindowsSelectionClipboardAdapter.InputStructureSize) == 4;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>Minimal executor for the hidden-panel terminal test: counts draft
    /// calls and hands back a caller-controlled completion.</summary>
    private sealed class BatchFakeExecutor : ITranslationExecutor
    {
        public int DraftCalls;
        public Func<ProviderSettings, string, string, string, string, string, long, CancellationToken, TranslationStreamSession>? OnStreamTextDraft { get; set; }

        public ProviderSettings Settings { get; set; } = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.OpenAiCompatible,
            TextModel = "fake-model",
            NetworkEnabled = true,
            SafeDevMode = false,
            Mode = TranslationMode.Auto,
        };

        public ProviderRoute? TextRoute { get; set; } = new(
            new ProviderProfile
            {
                Id = "batch-profile", Name = "Batch", ApiBaseUrl = "https://loopback.test/v1",
                TextModel = "fake-model", SupportsText = true, CredentialTarget = "PopGlot/provider/batch",
            },
            "PopGlot/provider/batch");
        public ProviderRoute? VisionRoute { get; set; }
        public ResolvedRoute? ScreenshotRoute { get; set; }
        public string? ApiKey { get; set; } = "fake-api-key";
        public bool IsOcrSupported { get; set; } = true;
        public string OcrRecognizedText { get; set; } = "Recognized";
        public int OcrCallCount { get; set; }

        public ProviderSettings GetSettings() => Settings;
        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() => (TextRoute, VisionRoute);
        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable) =>
            ScreenshotRoute ?? new ResolvedRoute(TextRoute, VisionRoute, ScreenshotPipeline.LocalOcr, false, "Auto local OCR");
        public string? LoadApiKey(string target) => ApiKey;
        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default)
        {
            OcrCallCount++;
            return Task.FromResult(OcrRecognizedText);
        }

        public TranslationStreamSession StreamText(string? apiKey, string source, string sourceLang, string targetLang,
            string sessionId, long epoch, CancellationToken cancellationToken)
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, new TaskCompletionSource<TranslationResponse>().Task);
        }

        public TranslationStreamSession StreamTextDraft(ProviderSettings draftSettings, string apiKey, string source,
            string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref DraftCalls);
            if (OnStreamTextDraft is not null)
                return OnStreamTextDraft(draftSettings, apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, new TaskCompletionSource<TranslationResponse>().Task);
        }

        public TranslationStreamSession StreamVisionDraft(ProviderSettings draftSettings, string textApiKey, string visionApiKey,
            byte[] image, string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken)
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, new TaskCompletionSource<TranslationResponse>().Task);
        }

        public Task<TranslationResponse> TranslateFreeAsync(string source, string sourceLang, string targetLang,
            FreeEngineAuthorization authorization, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the batch test never uses the free engine");

        public TranslationStreamSession StreamTextTask(string? apiKey, string source, string sourceLang, string targetLang,
            TextTaskKind task, string sessionId, long epoch, CancellationToken cancellationToken,
            ProviderSettings? routeSettings = null, PromptAnchorSnapshot? preferenceAnchor = null)
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(buffer, new TaskCompletionSource<TranslationResponse>().Task);
        }
    }
}
