using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PopGlot.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// Wave regression batch: high-value guards for the bugs that already bit
/// once. Placed in a separate file so main test orchestrators can invoke the
/// members without Program.cs merge conflicts; registration lives in
/// Program.Main. Naming style and isolation rules follow the existing suite.
/// </summary>
internal static class WaveRegressionTests
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    // ===================== Assertions =====================

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected <{expected}>, got <{actual}>.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
        }
    }

    private static void Throws<TException>(Action operation)
        where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    // ===================== Harness =====================

    private static Application? _bootstrappedApp;

    /// <summary>STA-thread twin of Program.EnsureApplication (it is private
    /// there); safe to call after Program's version already ran.</summary>
    private static void EnsureApp()
    {
        if (Application.Current is null)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.InitializeComponent();
            }
            catch
            {
                // Fallback mirrors Program.EnsureApplication.
            }
            _bootstrappedApp = app;
            app.DispatcherUnhandledException += (_, args) => args.Handled = true;
        }
        ThemeService.Apply(ThemePreference.Dark);
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>Pumps the CURRENT dispatcher (the STA harness thread) until the
    /// task completes, so awaited production continuations can resume without
    /// blocking the thread they must run on.</summary>
    private static void PumpUntil(Task task)
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("PumpUntil must run on the STA harness thread");
        for (var attempt = 0; attempt < 5000 && !task.IsCompleted; attempt++)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("the awaited production task never settled");
        }
    }

    /// <summary>Flushes queued dispatcher work ONCE: the Background-priority
    /// marker blocks in a frame, so everything queued above it (Loaded, Input,
    /// Render…) runs before the marker. Unlike <see cref="PumpUntil"/> with an
    /// already-completed task, this always really pumps.</summary>
    private static void PumpOnce()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("PumpOnce must run on the STA harness thread");
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static HistoryStore IsolatedHistory => new(TestIsolation.HistoryPath);

    private static VocabularyStore IsolatedVocabulary => new(TestIsolation.VocabularyPath);

    private static ProviderProfile EditorProfile() => new()
    {
        Id = "demo-text",
        Name = "Demo Text Service",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "http://127.0.0.1:9/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = "demo-text-model",
        VisionModel = "demo-vision-model",
        AnthropicVersion = "2023-06-01",
        SupportsText = true,
        SupportsVision = true,
        CredentialTarget = "PopGlot/provider/demo-text",
        IsLocal = true,
        ExtraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    private static TranslationPanelWindow NewPanel() => new(
        new System.Windows.Rect(100, 100, 20, 20),
        IsolatedHistory,
        () => ShellSettings.Default,
        null,
        null,
        IsolatedVocabulary);

    /// <summary>Synchronous twin of Program.DrivePanelSession (private there).</summary>
    private static void DrivePanelSession(TranslationPanelWindow panel, string source, TranslationSession session)
    {
        typeof(TranslationPanelWindow)
            .GetMethod("HandleSessionResultAsync", NonPublicInstance)!
            .Invoke(panel, new object?[] { source, session, 0L, null });
        panel.UpdateLayout();
    }

    private static void InvokePrivate(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, NonPublicInstance)!.Invoke(target, args);

    private static T GetPrivate<T>(object target, string field) =>
        (T)(target.GetType().GetField(field, NonPublicInstance)!.GetValue(target)
            ?? throw new InvalidOperationException($"field {field} was null"));

    private static void SetPrivate(object target, string field, object? value) =>
        target.GetType().GetField(field, NonPublicInstance)!.SetValue(target, value);

    /// <summary>Null-tolerant field probe (GetPrivate throws on null values).</summary>
    private static bool PrivateFieldIsNull(object target, string field) =>
        target.GetType().GetField(field, NonPublicInstance)!.GetValue(target) is null;

    // ===================== A: workbench / library honesty =====================

    /// <summary>
    /// Old bugs this pins: loading a library entry used to write raw state
    /// over the controls (bypassing the reducer, leaking markdown-raw text,
    /// persisting the entry's language pair as the global default) and a
    /// Failed restore opened the full result actions.
    /// </summary>
    public static void WorkbenchRestoreHonestyAndLanguageNotPersisted()
    {
        EnsureApp();
        var before = CoreBridge.GetSettings();
        var section = new TranslateSection();
        section.Initialize(
            new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary),
            null);

        // 1) A Completed library load: markdown stays in the box for the
        //    result layer, the badge says where it came from, the explanation
        //    row shows, the empty-state guide retires and the actions open.
        section.FocusTranslate(
            initialText: "library source",
            targetLang: "en",
            sourceLang: "zh-CN",
            existingTranslation: "载入的**译文**",
            storedState: TranslationSessionState.Completed,
            explanation: "载入说明",
            badge: "生词本");
        Equal("生词本", section.CurrentState.BadgeText, "the loaded entry keeps its origin badge");
        Equal("载入的**译文**", section.ResultBox.Text, "the stored translation is restored verbatim");
        True(section.CurrentState.AreResultActionsEnabled, "a Completed load opens the result actions");
        True(section.CurrentState.IsExplanationVisible, "a carried explanation is visible");
        Equal("载入说明", section.CurrentState.ExplanationText, "the explanation text is restored");
        Equal(Visibility.Collapsed, section.EmptyStateGuide.Visibility,
            "an expanded translation must retire the first-use guide");
        Equal("已载入记录。", section.CurrentState.StatusText, "the status says the entry was loaded, not retranslated");

        // 2) The borrowed dropdown pair is DISPLAY ONLY: the persisted core
        //    pair must be untouched by the load.
        Equal("zh-CN", ((LanguageOption)section.SourceLangCombo.SelectedItem!).Tag,
            "the source dropdown borrows the entry's pair for display");
        Equal("en", ((LanguageOption)section.TargetLangCombo.SelectedItem!).Tag,
            "the target dropdown borrows the entry's pair for display");
        var after = CoreBridge.GetSettings();
        Equal(before.SourceLanguage, after.SourceLanguage,
            "loading an entry must never persist its source language as the global default");
        Equal(before.TargetLanguage, after.TargetLanguage,
            "loading an entry must never persist its target language as the global default");

        // 3) A Failed restore: text survives, actions stay gated, badge honest.
        section.FocusTranslate(
            initialText: "failed source",
            existingTranslation: "失败残留",
            storedState: TranslationSessionState.Failed);
        Equal("失败残留", section.ResultBox.Text, "the failed text is still shown");
        True(!section.CurrentState.AreResultActionsEnabled,
            "a Failed restore must keep the full result actions gated");
        Equal("未完成", section.CurrentState.BadgeText, "a Failed restore is never labelled complete");
        True(section.CurrentState.IsPartialIncomplete, "a Failed restore stays flagged incomplete");
        True(!section.StarButton.IsEnabled, "starring must stay gated for a Failed restore");

        // 4) The library copy path and the workbench share one plain-text
        //    formatter, so the stored raw markdown never leaks as markup.
        Equal("载入的译文", MarkdownPresenter.ToPlainText("载入的**译文**"),
            "copy must deliver the agreed plain text");
    }

    /// <summary>
    /// The stash-before-overwrite contract: when the session store REFUSES
    /// the draft (budget exceeded), the overwrite must be blocked and the
    /// refusal must be visible — silently losing the draft is the bug.
    /// </summary>
    public static void RejectedDraftStashBlocksOverwrite()
    {
        EnsureApp();
        var store = App.SharedSessionStore;
        store.Clear();
        try
        {
            var section = new TranslateSection();
            section.Initialize(
                new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary),
                null);

            var bigDraft = new string('x', 2_100_000); // > the 2 MiB store budget
            section.InputBox.Text = bigDraft;
            section.FocusTranslate("新的一段文本");

            Equal(bigDraft, section.InputBox.Text,
                "a rejected stash must BLOCK the overwrite: the workbench keeps the draft");
            True(section.TranslateStatus.Text.Contains("未能暂存", StringComparison.Ordinal),
                $"the refusal must be visible, got '{section.TranslateStatus.Text}'");
            True(store.GetAll().Count == 0, "the rejected stash must not have entered the store");
        }
        finally
        {
            store.Clear();
        }
    }

    // ===================== B: floating panel =====================

    /// <summary>
    /// A new operation starts from a clean slate: the previous attempt's
    /// partial text must never resurface as this attempt's content or as a
    /// stored "result" when the new attempt fails before anything streams.
    /// </summary>
    public static void PanelNewOperationClearsStaleText()
    {
        EnsureApp();
        var panel = NewPanel();
        // The real flow keeps the source inside the input box for the whole
        // session; without it the panel has no session to snapshot.
        panel.SourceInputBox.Text = "first source";

        // Attempt 1 fails WITH retained partial text.
        DrivePanelSession(panel, "first source", new TranslationSession
        {
            Stage = TranslationSessionStage.Failed,
            TranslatedText = "半截译文",
            Error = new TranslationError(TranslationErrorKind.ServerError, "连接中断"),
        });
        Equal("半截译文", panel.StreamTextBox.Text, "setup: the partial text is visible");
        var staleSnapshot = panel.CreateSessionSnapshot();
        True(staleSnapshot is not null && staleSnapshot.ResultText == "半截译文",
            "setup: the stale attempt is still the panel's stored content");

        // Attempt 2 begins and fails BEFORE anything streams.
        var runOperation = typeof(TranslationPanelWindow)
            .GetMethod("RunOperationAsync", NonPublicInstance)!;
        var task = (Task)runOperation.Invoke(panel, new object?[]
        {
            (Func<CancellationToken, long, Task>)((_, _) =>
            {
                var tcs = new TaskCompletionSource<object?>();
                tcs.TrySetException(new InvalidOperationException("boom：新操作立即失败"));
                return tcs.Task;
            }),
            null,
        })!;
        PumpUntil(task);

        True(!panel.StreamTextBox.Text.Contains("半截译文", StringComparison.Ordinal),
            "the previous attempt's text must never resurface in the new attempt");
        Equal("未完成", panel.EngineBadge.Text, "the failed new attempt keeps its own honest badge");
        True(!panel.ResultCopyBtn.IsEnabled && !panel.ResultSpeakBtn.IsEnabled && !panel.StarToggle.IsEnabled,
            "a clean failure keeps every result action gated");

        var freshSnapshot = panel.CreateSessionSnapshot();
        True(freshSnapshot is not null, "the new attempt still owns a session snapshot");
        Equal(TranslationSessionState.Failed, freshSnapshot!.State, "the new attempt failed");
        True(string.IsNullOrEmpty(freshSnapshot.ResultText),
            "the stale partial must not be stored as the new attempt's result");
    }

    /// <summary>
    /// Old defect this pins: while the gate is still Streaming/Finalizing,
    /// the async cancellation callback has NOT re-classified it yet, so
    /// HasPartialText was false and CreateSessionSnapshot (close path
    /// included) WIPED the real streamed delta — the partial was lost from
    /// the session store. The snapshot must trust the gate's live
    /// StreamedText immediately: keep it, mark Cancelled + partial.
    /// </summary>
    public static void PanelStreamingSnapshotKeepsRealPartial()
    {
        EnsureApp();
        var store = App.SharedSessionStore;
        store.Clear();
        var panel = NewPanel();
        try
        {
            panel.SourceInputBox.Text = "流式中的源文本";
            var gate = GetPrivate<TranslationPanelStreamGate>(panel, "_gate");
            var (epoch, _) = gate.BeginNewOperation();
            // The real production path: a streamed delta lands in the gate
            // AND the panel's live translation field.
            InvokePrivate(panel, "OnStreamUpdate", new TranslationStreamUpdate(
                "s1", epoch, TranslationStreamUpdateKind.Delta, "部分", "部分流式译文", 6));
            Equal(TranslationPanelStage.Streaming, gate.Stage, "setup: the gate is mid-stream");
            Equal("部分流式译文", gate.StreamedText, "setup: the gate holds the real delta");

            // The close/snapshot runs BEFORE any async cancellation callback
            // can re-classify the gate: the stage is still Streaming now.
            var snapshot = panel.CreateSessionSnapshot();
            Equal(TranslationPanelStage.Streaming, gate.Stage,
                "the snapshot must not wait for the async cancel callback to move the gate");
            True(snapshot is not null, "a mid-stream session still owns a snapshot");
            Equal("部分流式译文", snapshot!.ResultText, "the real streamed delta must be kept, never wiped");
            Equal(TranslationSessionState.Cancelled, snapshot.State,
                "an interrupted stream is cancelled, never completed");
            True(snapshot.IsPartial, "the kept delta is flagged partial");

            // The real close path (close hotkey / CloseActivePanel) stores
            // the same partial.
            panel.CloseAsUserIntent();
            var stored = store.PeekRecent();
            True(stored is not null, "CloseAsUserIntent must store the session");
            Equal("部分流式译文", stored!.ResultText, "the close path keeps the real streamed delta");
            Equal(TranslationSessionState.Cancelled, stored.State, "the stored session stays cancelled");
            True(stored.IsPartial, "the stored session stays flagged partial");
        }
        finally
        {
            store.Clear();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    /// <summary>
    /// The store only ever accepts real streamed grain: a failed attempt's
    /// friendly headline (FailedWithoutPartial) is cleared, and a new attempt
    /// that reached Finalizing with ZERO deltas stores no text at all — even
    /// if a stale headline still sits in the live result field.
    /// </summary>
    public static void PanelWithoutPartialStoresNoFakeText()
    {
        EnsureApp();
        var panel = NewPanel();
        try
        {
            panel.SourceInputBox.Text = "没有结果的源文本";

            // A failed attempt renders its friendly headline into the result
            // field for reading — but it is not a translation.
            DrivePanelSession(panel, "没有结果的源文本", new TranslationSession
            {
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(TranslationErrorKind.ServerError, "连接超时（120 秒无响应）"),
            });
            var gate = GetPrivate<TranslationPanelStreamGate>(panel, "_gate");
            Equal(TranslationPanelStage.FailedWithoutPartial, gate.Stage, "setup: the failure kept no partial");
            True(!string.IsNullOrWhiteSpace(GetPrivate<string>(panel, "_translation")),
                "setup: the friendly failure headline is rendered for reading");

            var failedSnapshot = panel.CreateSessionSnapshot();
            True(failedSnapshot is not null, "the failed attempt still owns a session");
            Equal(TranslationSessionState.Failed, failedSnapshot!.State, "the attempt failed");
            True(string.IsNullOrWhiteSpace(failedSnapshot.ResultText),
                $"the friendly failure headline must never be stored as a translation, got '{failedSnapshot.ResultText}'");
            True(!failedSnapshot.IsPartial, "a failure without partial is not partial");

            // The next attempt reaches Finalizing with zero streamed deltas:
            // nothing may materialise in the store.
            gate.BeginNewOperation();
            gate.OnStageChanged(TranslationSessionStage.Finalizing);
            var emptySnapshot = panel.CreateSessionSnapshot();
            True(emptySnapshot is not null, "the in-flight attempt still owns a session");
            True(string.IsNullOrWhiteSpace(emptySnapshot!.ResultText),
                "a stream without deltas must not save any text");
            True(!emptySnapshot.IsPartial, "no delta means no partial");
            Equal(TranslationSessionState.Cancelled, emptySnapshot.State,
                "an in-flight attempt with nothing streamed is only ever cancelled-and-empty");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    /// <summary>
    /// Old defect this pins: the draft-guard bounce-back set
    /// NavProvider/NavPrompt.IsChecked WITHOUT the _syncingNav guard, so the
    /// rebound radio re-entered ShowPage synchronously (a full page switch
    /// with list refreshes) while the guard branch was still deciding. The
    /// rebound must be wrapped in _syncingNav and the guard flow must hold.
    /// </summary>
    public static void SettingsDraftGuardNavBounceStaysSynced()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            window.Show();
            PumpUntil(Task.CompletedTask);
            window.ShowPage("Provider");
            var profile = ProfileManager.Load().TryGetActiveProfile() ?? EditorProfile();
            window.ProviderSection.LoadProfileIntoForm(profile);
            InvokePrivate(window.ProviderSection, "ShowEditorForm", false);
            True(window.ProviderSection.IsEditorOpen, "setup: the engine editor is open");
            window.ProviderSection.ServiceNameTextBox.Text = "改名后的引擎";
            True(window.ProviderSection.IsEditorDirty, "setup: the editor holds an unsaved draft");

            // The user clicks another page in the rail.
            window.NavGeneral.IsChecked = true;

            // The draft guard keeps the window on the engine page, with the
            // rail bounced back and the requested page withheld.
            Equal(Visibility.Visible, window.ProviderSection.Visibility,
                "the draft guard keeps the engine page up");
            Equal(Visibility.Collapsed, window.GeneralSection.Visibility,
                "the requested page stays withheld until the draft is settled");
            True(window.NavProvider.IsChecked == true, "the rail bounces back to the guarded page");
            True(window.NavGeneral.IsChecked == false, "the requested nav item does not stay lit");
            Equal(Visibility.Visible, window.ProviderSection.DraftGuardBar.Visibility,
                "the draft guard bar is visible");
            True(window.ProviderSection.IsEditorOpen && window.ProviderSection.IsEditorDirty,
                "the bounce must not clobber the unsaved draft");

            // Wiring: the rebound IsChecked must sit inside the _syncingNav
            // guard so SubNav_Checked cannot re-enter ShowPage.
            var source = File.ReadAllText(Path.Combine(
                FindProjectRoot(), "apps", "PopGlot.Windows", "SettingsWindow.xaml.cs"));
            var providerGuard = source.IndexOf("ProviderSection.BeginDraftGuard(", StringComparison.Ordinal);
            var promptGuard = source.IndexOf("PromptSectionHost.BeginDraftGuard(", StringComparison.Ordinal);
            True(providerGuard > 0 && promptGuard > providerGuard,
                "both draft guards must stay in ShowPage");
            var providerRebind = source.LastIndexOf("NavProvider.IsChecked = true", providerGuard, StringComparison.Ordinal);
            True(providerRebind > 0 &&
                 source.LastIndexOf("_syncingNav = true", providerRebind, StringComparison.Ordinal) > 0,
                "the provider draft-guard nav rebound must be preceded by _syncingNav = true");
            True(source[providerRebind..providerGuard].Contains("_syncingNav = false", StringComparison.Ordinal),
                "the provider draft-guard nav rebound must be followed by _syncingNav = false");
            var promptRebind = source.LastIndexOf("NavPrompt.IsChecked = true", promptGuard, StringComparison.Ordinal);
            True(promptRebind > 0 &&
                 source.LastIndexOf("_syncingNav = true", promptRebind, StringComparison.Ordinal) > 0,
                "the prompt draft-guard nav rebound must be preceded by _syncingNav = true");
            True(source[promptRebind..promptGuard].Contains("_syncingNav = false", StringComparison.Ordinal),
                "the prompt draft-guard nav rebound must be followed by _syncingNav = false");

            // Discarding the draft resolves the guard; the deferred
            // navigation (BeginInvoke) then opens the requested page.
            InvokePrivate(window.ProviderSection, "DraftDiscard_Click",
                window.ProviderSection, new RoutedEventArgs());
            True(window.ProviderSection.DraftGuardBar.Visibility == Visibility.Collapsed,
                "discarding dismisses the guard bar");
            for (var attempt = 0; attempt < 200 && window.GeneralSection.Visibility != Visibility.Visible; attempt++)
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            Equal(Visibility.Visible, window.GeneralSection.Visibility,
                "the deferred navigation opens the requested page after the draft is settled");
            True(window.NavGeneral.IsChecked == true, "the rail follows the opened page");
        }
        finally
        {
            window.ForceClose = true;
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// Restores are decided by the session's real state: Completed opens the
    /// full action set, Failed keeps its「未完成」badge (never relabelled
    ///「已取消」) and Cancelled stays a warning — with speak/star always gated
    /// for the unfinished two.
    /// </summary>
    public static void PanelRestoreKeepsStatesDistinct()
    {
        EnsureApp();

        // 1) Completed: full actions, engine name restored as the badge.
        var completed = NewPanel();
        completed.RestoreSession(StoredSession.Create(
            sessionId: null, origin: SessionOrigin.TranslationPanel,
            sourceText: "源文本", sourceLang: "en", targetLang: "zh-CN",
            engineProfileId: null, engineName: "DeepSeek",
            state: TranslationSessionState.Completed,
            resultText: "完整译文", explanationText: null, isPartial: false));
        Equal("DeepSeek", completed.EngineBadge.Text, "the engine name rides the badge");
        Equal("已恢复最近翻译（未重发）", completed.StatusTextBlock.Text,
            "the restore must say it did not resend");
        True(completed.ResultCopyBtn.IsEnabled && completed.ResultSpeakBtn.IsEnabled && completed.StarToggle.IsEnabled,
            "a Completed restore opens the full result actions");
        Equal("完整译文", completed.StreamTextBox.Text, "the restored result is shown");

        // 2) Failed: own badge + danger tone, never「已取消」; only manual
        //    copy stays alive for the retained text.
        var failed = NewPanel();
        failed.RestoreSession(StoredSession.Create(
            sessionId: null, origin: SessionOrigin.TranslationPanel,
            sourceText: "源文本", sourceLang: "en", targetLang: "zh-CN",
            engineProfileId: null, engineName: null,
            state: TranslationSessionState.Failed,
            resultText: "半截残留", explanationText: "失败原因", isPartial: true));
        Equal("未完成", failed.EngineBadge.Text,
            "a failed session must never be relabelled「已取消」");
        True(!ReferenceEquals(failed.EngineBadge.Foreground, failed.TryFindResource("WarningBrush")),
            "the failed badge must not wear the cancelled warning tone");
        Equal("已恢复失败会话（未重发）", failed.StatusTextBlock.Text, "the failed restore says so");
        True(failed.ResultCopyBtn.IsEnabled, "manual copy of the retained text stays alive");
        True(!failed.ResultSpeakBtn.IsEnabled && !failed.StarToggle.IsEnabled,
            "speak/star stay gated for a failed restore");

        // 3) Cancelled with partial: warning tone, copy only.
        var cancelled = NewPanel();
        cancelled.RestoreSession(StoredSession.Create(
            sessionId: null, origin: SessionOrigin.TranslationPanel,
            sourceText: "源文本", sourceLang: "en", targetLang: "zh-CN",
            engineProfileId: null, engineName: null,
            state: TranslationSessionState.Cancelled,
            resultText: "取消前内容", explanationText: null, isPartial: true));
        Equal("已取消", cancelled.EngineBadge.Text, "the cancelled badge is honest");
        Equal("已恢复未完成内容（未重发）", cancelled.StatusTextBlock.Text, "the cancelled restore says so");
        True(cancelled.ResultCopyBtn.IsEnabled, "copy of the retained partial stays alive");
        True(!cancelled.ResultSpeakBtn.IsEnabled && !cancelled.StarToggle.IsEnabled,
            "speak/star stay gated for a cancelled restore");
    }

    /// <summary>
    /// Every height tier produced by FitInitialHeightToSource must sit at or
    /// above the window MinHeight, so the bottom status bar is never clipped
    /// exactly on short sources.
    /// </summary>
    public static void PanelHeightTiersRespectMinHeight()
    {
        EnsureApp();
        var panel = NewPanel();
        var fit = typeof(TranslationPanelWindow).GetMethod("FitInitialHeightToSource", NonPublicInstance)!;
        True(panel.MinHeight >= 380, $"the window MinHeight floor must hold, got {panel.MinHeight}");

        foreach (var (source, label) in new[]
                 {
                     ("短文本", "short"),
                     (new string('m', 100), "medium"),
                     (new string('l', 500), "long"),
                 })
        {
            fit.Invoke(panel, new object?[] { source });
            True(panel.Height >= panel.MinHeight,
                $"the {label} source tier must stay at or above the MinHeight, got {panel.Height} < {panel.MinHeight}");
        }
    }

    // ===================== C: quick search =====================

    /// <summary>
    /// The header badge must not go stale across a hide/show cycle: the
    /// persisted pair may have changed on another surface while hidden.
    /// </summary>
    public static void QuickSearchReshowRefreshesLangBadge()
    {
        EnsureApp();
        var original = CoreBridge.GetSettings();
        try
        {
            var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
            quickSearch.Show();
            PumpUntil(Task.CompletedTask);
            quickSearch.Hide();
            PumpUntil(Task.CompletedTask);

            // Change the persisted pair while the window is hidden.
            PumpUntil(CoreBridge.SaveSettingsAsync(original with
            {
                SourceLanguage = "fr",
                TargetLanguage = "de",
            }));

            quickSearch.Show(); // the re-show must repaint, not reuse
            PumpUntil(Task.CompletedTask);

            True(quickSearch.LangBadge.Text.Contains("法语", StringComparison.Ordinal) &&
                 quickSearch.LangBadge.Text.Contains("德语", StringComparison.Ordinal),
                $"the re-show must repaint the badge from the persisted pair, got '{quickSearch.LangBadge.Text}'");

            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
        finally
        {
            PumpUntil(CoreBridge.SaveSettingsAsync(original));
        }
    }

    /// <summary>
    /// The pre-read timeout posts its landing notice into a QuickSearchWindow
    /// that may have JUST been created. The old raw footer write lost the race
    /// against the first-show's queued Loaded sync and the Idle copy swallowed
    /// the notice. The notice is STATE-driven now
    /// (<see cref="QuickSearchState.PendingNotice"/>): every SyncUiWithState
    /// re-applies it, so the first Show→Loaded keeps it. The USER hide
    /// lifecycle (Esc/focus-loss/X close-as-hide) is the truth that ends it:
    /// an ordinary re-show must greet with the idle copy again — never a stale
    /// timeout notice. An already-loaded visible window shows a fresh post
    /// immediately, and the next real query clears it.
    /// </summary>
    public static void QuickSearchPendingNoticeSurvivesFirstShowLoaded()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            // 1. FIRST CREATION: Show() queues the Loaded broadcast; the
            //    notice is posted right after (the production timeout path's
            //    exact Show-then-Post ordering). The Loaded sync must
            //    re-apply the pending notice, not swallow it.
            quickSearch.Show();
            quickSearch.PostPendingNotice("未能及时读取选区（测试落地提示）");
            PumpOnce(); // let the queued Loaded → SyncUiWithState run
            PumpOnce();

            True(quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal),
                $"the first-show Loaded sync must re-apply the pending notice, not swallow it, got '{quickSearch.FooterStatusBlock.Text}'");

            // 2. USER HIDE ends the notice lifecycle: the window itself (not
            //    the caller) clears it, and an ordinary re-show returns to the
            //    idle copy — a stale timeout notice must not greet the user.
            quickSearch.Hide();
            Equal("输入后按 Enter 翻译", quickSearch.FooterStatusBlock.Text,
                $"the user hide must clear the pending notice immediately, got '{quickSearch.FooterStatusBlock.Text}'");
            quickSearch.Show();
            PumpOnce();
            PumpOnce();
            Equal("输入后按 Enter 翻译", quickSearch.FooterStatusBlock.Text,
                $"an ordinary re-show must show the idle copy, not a resurrected notice, got '{quickSearch.FooterStatusBlock.Text}'");

            // 3. ALREADY LOADED AND VISIBLE: a fresh post shows immediately.
            quickSearch.PostPendingNotice("可见窗口提示");
            True(quickSearch.FooterStatusBlock.Text.Contains("可见窗口提示", StringComparison.Ordinal),
                $"a loaded, visible window must show the notice immediately, got '{quickSearch.FooterStatusBlock.Text}'");

            // 4. THE NEXT REAL QUERY clears it (TextChanged → debounce →
            //    OnQueryTextChanged); pump frames while the debounce elapses.
            quickSearch.SearchBox.Text = "真正的查询词";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   quickSearch.FooterStatusBlock.Text.Contains("可见窗口提示", StringComparison.Ordinal))
            {
                Thread.Sleep(25);
                PumpOnce();
            }

            True(!quickSearch.FooterStatusBlock.Text.Contains("可见窗口提示", StringComparison.Ordinal),
                $"the next real query must clear the pending notice, got '{quickSearch.FooterStatusBlock.Text}'");
            Equal("按 Enter 立即翻译 · Shift+Enter 换行", quickSearch.FooterStatusBlock.Text,
                "after the notice clears, the footer must return to the idle copy");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
    }

    /// <summary>
    /// The pending notice is ONE-SHOT: the next real query (TextChanged →
    /// debounce → OnQueryTextChanged) must clear it and return the footer to
    /// the idle copy — the notice can never permanently occupy the footer.
    /// </summary>
    public static void QuickSearchPendingNoticeClearsOnNextQuery()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            quickSearch.Show();
            quickSearch.PostPendingNotice("未能及时读取选区（测试落地提示）");
            PumpOnce();
            True(quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal),
                "setup: the notice must be visible before the query");

            // A real query flows through TextChanged → 150 ms debounce →
            // OnQueryTextChanged → SyncUiWithState; pump frames while the
            // debounce elapses.
            quickSearch.SearchBox.Text = "真正的查询词";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal))
            {
                Thread.Sleep(25);
                PumpOnce();
            }

            True(!quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal),
                $"the next real query must clear the pending notice, got '{quickSearch.FooterStatusBlock.Text}'");
            Equal("按 Enter 立即翻译 · Shift+Enter 换行", quickSearch.FooterStatusBlock.Text,
                "after the notice clears, the footer must return to the idle copy");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
    }

    /// <summary>
    /// Explicit priority and consumption rules between the two footer
    /// notices: the error/timeout-class <see cref="QuickSearchState.PendingNotice"/>
    /// OUTRANKS the style-only pre-flight explanation, and taking over
    /// CONSUMES the style notice on the spot — it is neither swallowed by the
    /// style retirement branch nor revived by a later sync. The style notice
    /// alone still follows its original rule: shown while a query is
    /// preparing (streaming/finalizing with no text), retired by the FIRST
    /// real delta.
    /// </summary>
    public static void QuickSearchPendingNoticeOutranksStyleNotice()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            quickSearch.Show();
            PumpOnce();

            // Arm the style pre-flight notice exactly like the production
            // pre-translate path: a real query preparing, no text yet.
            quickSearch.State.StartNewSearch("style race");
            SetPrivate(quickSearch, "_pendingStyleNotice", "风格提示：当前引擎不支持自定义样式");
            InvokePrivate(quickSearch, "SyncUiWithState");
            True(quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                $"setup: the style notice must show while preparing, got '{quickSearch.FooterStatusBlock.Text}'");

            // 1. Both exist: the timeout-class notice WINS and consumes the
            //    style notice immediately.
            quickSearch.PostPendingNotice("未能及时读取选区（测试落地提示）");
            True(quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal) &&
                 !quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                $"the pending notice must outrank the style notice, got '{quickSearch.FooterStatusBlock.Text}'");
            True(PrivateFieldIsNull(quickSearch, "_pendingStyleNotice"),
                "the style notice must be consumed the moment the pending notice takes over");

            // 2. Later syncs must not revive the style notice nor drop the
            //    pending notice.
            InvokePrivate(quickSearch, "SyncUiWithState");
            True(quickSearch.FooterStatusBlock.Text.Contains("测试落地提示", StringComparison.Ordinal) &&
                 !quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                $"a later sync must keep the pending notice and never revive the style notice, got '{quickSearch.FooterStatusBlock.Text}'");

            // 3. The next real query clears the pending notice; the consumed
            //    style notice must not reappear either.
            quickSearch.State.OnQueryTextChanged("真实查询");
            InvokePrivate(quickSearch, "SyncUiWithState");
            Equal("按 Enter 立即翻译 · Shift+Enter 换行", quickSearch.FooterStatusBlock.Text,
                $"after the query clears the notice, only the idle copy remains, got '{quickSearch.FooterStatusBlock.Text}'");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }

        // 4. Style notice ALONE (no pending notice): unchanged expiry rule —
        //    the first real delta retires it permanently.
        var expiring = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            expiring.Show();
            PumpOnce();
            expiring.State.StartNewSearch("delta expiry");
            SetPrivate(expiring, "_pendingStyleNotice", "风格提示：独立存在时正常展示");
            InvokePrivate(expiring, "SyncUiWithState");
            True(expiring.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                "setup: the style notice shows while the query is preparing");

            expiring.State.OnStreamUpdate(
                new TranslationStreamUpdate(
                    "s1", expiring.State.CurrentEpoch, TranslationStreamUpdateKind.Delta, "首", "首字", 2),
                "delta expiry");
            InvokePrivate(expiring, "SyncUiWithState");
            Equal("正在生成…", expiring.FooterStatusBlock.Text,
                $"the first real delta must retire the style notice, got '{expiring.FooterStatusBlock.Text}'");
            True(PrivateFieldIsNull(expiring, "_pendingStyleNotice"),
                "the style notice must stay consumed after the first delta");
        }
        finally
        {
            expiring.ForceClose = true;
            expiring.Close();
        }
    }

    /// <summary>
    /// Hiding during the PREPARING phase (streaming, no text yet, style-only
    /// explanation showing) is a view-lifecycle end for BOTH footer notices:
    /// a stale style explanation must not greet the user on re-show. The C05
    /// session semantics stay untouched — query and in-flight stage survive
    /// the hide, and the snapshot stored on hide follows the SAME contract as
    /// before: a preparing session snapshots as Cancelled with NO fabricated
    /// result text.
    /// </summary>
    public static void QuickSearchHideDuringPreparationClearsStyleNotice()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            quickSearch.Show();
            PumpOnce();

            // Real preparing phase: streaming with no accumulated text, style
            // pre-flight notice armed exactly like production.
            quickSearch.State.StartNewSearch("style hide query");
            SetPrivate(quickSearch, "_pendingStyleNotice", "风格提示：准备期说明");
            InvokePrivate(quickSearch, "SyncUiWithState");
            True(quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                "setup: the style notice must show while preparing");

            // USER HIDE clears BOTH one-shot notices; the session survives.
            quickSearch.Hide();
            True(PrivateFieldIsNull(quickSearch, "_pendingStyleNotice"),
                "the hide must clear the style notice");
            True(quickSearch.State.PendingNotice is null,
                "the hide must clear the pending notice");
            True(!quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                $"the footer must not keep the stale style notice after hide, got '{quickSearch.FooterStatusBlock.Text}'");

            // Re-show: no stale style explanation greets the user, while the
            // kept session state is untouched (C05).
            quickSearch.Show();
            PumpOnce();
            PumpOnce();
            True(!quickSearch.FooterStatusBlock.Text.Contains("风格提示", StringComparison.Ordinal),
                $"the re-show must not resurrect the stale style notice, got '{quickSearch.FooterStatusBlock.Text}'");
            Equal("style hide query", quickSearch.State.CurrentQuery,
                "C05: the kept session's query must survive the hide");
            Equal(QuickSearchUiStage.Streaming, quickSearch.State.Stage,
                "C05: the in-flight stage must survive the hide");

            // Snapshot semantics: the hide still stores the session with the
            // same contract as before — preparing snapshots as Cancelled with
            // no fabricated result text.
            var recent = App.SharedSessionStore.PeekRecent();
            True(recent is not null, "the hide must still store the session snapshot");
            Equal(SessionOrigin.QuickSearch, recent!.Origin,
                "the snapshot must come from quick search");
            Equal("style hide query", recent.SourceText,
                "the snapshot must carry the kept query");
            Equal(TranslationSessionState.Cancelled, recent.State,
                "a preparing session must snapshot as cancelled");
            True(recent.ResultText is null,
                "a snapshot must never fabricate result text for an empty stream");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
    }

    /// <summary>
    /// The star button's accessibility name must follow the dynamic starred
    /// state, so screen readers announce the action the click will actually
    /// perform.
    /// </summary>
    public static void QuickSearchStarAutomationNameFollowsState()
    {
        EnsureApp();
        var vocab = IsolatedVocabulary;
        // 生词本身份 = (word, 当前持久化语言对)。与生产一致，先把隔离核心
        // 的语言对固定为本用例使用的 en→zh-CN，结束后恢复。
        var original = CoreBridge.GetSettings();
        try
        {
            PumpUntil(CoreBridge.SaveSettingsAsync(original with
            {
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
            }));
            var quickSearch = new QuickSearchWindow(IsolatedHistory, vocab);
            try
            {
                quickSearch.SearchBox.Text = "wave_star_word";
                InvokePrivate(quickSearch, "UpdateStarButton");
                Equal("收藏到生词本", AutomationProperties.GetName(quickSearch.StarButton),
                    "an unstarred word must announce the star action");
                Equal(quickSearch.StarButton.ToolTip as string, AutomationProperties.GetName(quickSearch.StarButton),
                    "tooltip and accessibility name must agree");

                var result = vocab.ToggleStar("wave_star_word", "wave translation", "", "", "en", "zh-CN");
                True(result.Persisted && result.Starred, "setup: the word is starred");
                InvokePrivate(quickSearch, "UpdateStarButton");
                Equal("从生词本移除", AutomationProperties.GetName(quickSearch.StarButton),
                    "a starred word must announce the remove action");
                Equal(quickSearch.StarButton.ToolTip as string, AutomationProperties.GetName(quickSearch.StarButton),
                    "tooltip and accessibility name must agree after starring");
            }
            finally
            {
                quickSearch.ForceClose = true;
                quickSearch.Close();
            }
        }
        finally
        {
            foreach (var word in vocab.GetAll().Where(w => w.Word == "wave_star_word").ToList())
            {
                vocab.Remove(word.Id);
            }
            PumpUntil(CoreBridge.SaveSettingsAsync(original));
        }
    }

    /// <summary>
    /// The window may grow (image drag, DPI change, splitter): when its
    /// bottom would fall off the work area, ClampToWorkArea must pull it
    /// back instead of leaving the footer unreachable.
    /// </summary>
    public static void QuickSearchClampToWorkArea()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            quickSearch.Show();
            PumpUntil(Task.CompletedTask);
            var workArea = SystemParameters.WorkArea;

            // Park the window INSIDE the primary work area this assertion
            // compares against, then near its bottom edge, and grow it — the
            // HeightChanged/SizeChanged path runs the production clamp. The
            // window must not be left wherever the shell cascade-placed it:
            // on a multi-monitor desktop that can be a different monitor with
            // a different work area, which is not what this test exercises.
            quickSearch.Left = workArea.Left + 40;
            quickSearch.Top = workArea.Bottom - 120;
            PumpUntil(Task.CompletedTask);
            var topBefore = quickSearch.Top;
            quickSearch.Height = Math.Min(320, quickSearch.MaxHeight);
            PumpUntil(Task.CompletedTask);
            PumpUntil(Task.CompletedTask);

            True(quickSearch.Top + quickSearch.ActualHeight <= workArea.Bottom + 5,
                $"the clamp must keep the bottom inside the work area: bottom " +
                $"{quickSearch.Top + quickSearch.ActualHeight:F0} vs area bottom {workArea.Bottom:F0}");
            True(quickSearch.Top < topBefore + 1,
                "the clamp must actually pull the window up when it overflows");

            // Oversized window check: when requested height exceeds the work area,
            // effective height must be capped first, keeping top on screen and bottom inside work area.
            quickSearch.Height = workArea.Height + 200;
            PumpUntil(Task.CompletedTask);
            PumpUntil(Task.CompletedTask);

            True(quickSearch.ActualHeight <= workArea.Height,
                $"effective height must be capped to work area: {quickSearch.ActualHeight:F0} vs {workArea.Height:F0}");
            True(quickSearch.Top + quickSearch.ActualHeight <= workArea.Bottom + 5,
                $"oversized bottom must stay inside work area: bottom {quickSearch.Top + quickSearch.ActualHeight:F0} vs area bottom {workArea.Bottom:F0}");
            True(quickSearch.Top >= workArea.Top - 5,
                $"oversized top must stay on screen: top {quickSearch.Top:F0} vs area top {workArea.Top:F0}");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
    }

    // ===================== D: settings window =====================

    /// <summary>Saving locks BOTH save-bar actions — the save must not be
    /// re-entered and a revert must not roll half a commit back — and a
    /// close attempt during Saving is refused.</summary>
    public static void SettingsSavingLocksSaveBar()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            window.Show();
            PumpUntil(Task.CompletedTask);
            window.GeneralSection.AutoCopy.IsChecked = !ShellSettings.Default.CopyTranslationAutomatically;
            True(window.IsDirty, "setup: the form is dirty");
            True(window.SaveButton.IsEnabled && window.RevertButton.IsEnabled,
                "a plain dirty form keeps both actions available");

            GetPrivate<object>(window, "_state"); // field exists probe
            window.GetType().GetField("_state", NonPublicInstance)!
                .SetValue(window, SettingsEditState.Saving);
            InvokePrivate(window, "UpdateSaveBar");

            Equal(Visibility.Visible, window.SaveActionsPanel.Visibility, "the save bar stays visible while saving");
            Equal("正在保存…", (string)window.SaveButton.Content, "the save button names the in-flight commit");
            True(!window.SaveButton.IsEnabled, "saving must disable save (no re-entry)");
            True(!window.RevertButton.IsEnabled, "saving must disable revert");

            window.Close();
            True(window.IsLoaded, "a Saving window must refuse to close");
        }
        finally
        {
            window.ForceClose = true;
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// A CLEAN editor is folded away on close so the DIRTY main form faces
    /// the user with a VISIBLE save bar — not with instructions to press a
    /// hidden button.
    /// </summary>
    public static void DirtyFormCleanEditorCloseShowsSaveBar()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            window.Show();
            PumpUntil(Task.CompletedTask);
            window.ProviderSection.LoadProfileIntoForm(EditorProfile());
            InvokePrivate(window.ProviderSection, "ShowEditorForm", false);
            True(window.ProviderSection.IsEditorOpen, "setup: the engine editor is open and clean");

            window.GeneralSection.AutoCopy.IsChecked = !ShellSettings.Default.CopyTranslationAutomatically;
            True(window.IsDirty, "setup: the main form is dirty");

            window.Close();
            True(window.IsLoaded, "a dirty form must refuse to close");
            True(!window.ProviderSection.IsEditorOpen, "the clean editor folds away first");
            Equal(Visibility.Visible, window.SaveActionsPanel.Visibility,
                "the dirty form must face the user with a visible save bar");
        }
        finally
        {
            window.ForceClose = true;
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// Policy fail-closed is real: a failed read locks the policy controls
    /// away, the save refuses on the latch, and every rollback outcome —
    /// full, partial, failed — is worded honestly.
    /// </summary>
    public static void SettingsPolicyFailClosedAndRollbackHonesty()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        InvokePrivate(window, "SetPolicyControlsEnabled", false);
        True(!window.CaptureSection.NetworkEnabled.IsEnabled &&
             !window.CaptureSection.SafeMode.IsEnabled &&
             !window.CaptureSection.ModeCombo.IsEnabled,
            "a failed policy read must lock the policy controls away");
        InvokePrivate(window, "SetPolicyControlsEnabled", true);
        True(window.CaptureSection.NetworkEnabled.IsEnabled,
            "a successful re-read unlocks the controls again");

        var source = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "SettingsWindow.xaml.cs"));
        True(source.Contains("策略设置仍未读取成功，本次未保存任何修改", StringComparison.Ordinal),
            "a still-failing re-read must state that NOTHING was saved");
        True(source.Contains("已回滚写盘设置，但快捷键恢复失败", StringComparison.Ordinal),
            "a partial rollback must not claim a full restore");
        True(source.Contains("回滚未完成", StringComparison.Ordinal),
            "a failed rollback must admit the changes may still be live");
    }

    // ===================== E: prompt section =====================

    /// <summary>
    /// The UTF-8 byte budgets are enforced exactly at the byte boundary:
    /// 2730 CJK characters are 8190 bytes (inside the 8192 instruction
    /// budget) while 2731 are 8193 bytes (over it) — and the char/byte mix
    /// cannot smuggle an overflow through.
    /// </summary>
    public static void PromptValidateDraftByteBudgets()
    {
        // Instruction boundary: 2730*3 = 8190 bytes OK; 2731*3 = 8193 rejected.
        var within = PromptSection.ValidateDraft("名", "", new string('汉', 2730), "", "");
        True(within is null, "2730 CJK characters (8190 bytes) must stay within the 8192-byte instruction budget");
        var over = PromptSection.ValidateDraft("名", "", new string('汉', 2731), "", "");
        True(over is not null && over.Contains("8192", StringComparison.Ordinal) &&
             over.Contains("8193", StringComparison.Ordinal),
            $"2731 CJK characters (8193 bytes) must be rejected with the byte counts, got '{over}'");

        // Mixed char/byte: the same char count is legal in ASCII, illegal in CJK.
        True(PromptSection.ValidateDraft("名", "", new string('a', 8190), "", "") is null,
            "8190 ASCII bytes still fit");
        True(PromptSection.ValidateDraft("名", "", new string('a', 8193), "", "") is not null,
            "8193 ASCII bytes are rejected by the same budget");

        // Name / description char budgets.
        True(PromptSection.ValidateDraft(new string('名', 64), "", "", "", "") is null, "64-char name fits");
        True(PromptSection.ValidateDraft(new string('名', 65), "", "", "", "") is not null,
            "a 65-char name is rejected");
        True(PromptSection.ValidateDraft("名", new string('x', 256), "", "", "") is null, "256-char description fits");
        True(PromptSection.ValidateDraft("名", new string('x', 257), "", "", "") is not null,
            "a 257-char description is rejected");

        // Domain / audience carry BOTH a char and a byte budget — the 256-char
        // quota mirrors the authoritative Rust MAX_DOMAIN_SCALARS, so the
        // boundary that binds for CJK input is the char one (any string over
        // the 1024-byte budget necessarily exceeds 256 chars first).
        True(PromptSection.ValidateDraft("名", "", "", new string('汉', 256), "") is null,
            "256 CJK characters (768 bytes) fit both the 256-char and the 1024-byte domain budget");
        True(PromptSection.ValidateDraft("名", "", "", new string('汉', 257), "") is not null,
            "a 257-character domain is rejected even though its 771 bytes would fit the 1024-byte budget");
        True(PromptSection.ValidateDraft("名", "", "", "", new string('a', 257)) is not null,
            "the 256-char audience limit holds even when the bytes would fit");
    }

    /// <summary>The counters must show char + UTF-8 byte usage, append
    /// 「已超出」on overflow, and move from tertiary to warning (>=90%) to
    /// danger (over).</summary>
    public static void PromptCountersShowUsageAndOverflow()
    {
        EnsureApp();
        var counterText = typeof(PromptSection).GetMethod("CounterText",
            NonPublicInstance | BindingFlags.Static)!;
        var setCounterTone = typeof(PromptSection).GetMethod("SetCounterTone",
            NonPublicInstance | BindingFlags.Static)!;

        string Counter(int chars, int? charLimit, int? bytes = null, int? byteLimit = null) =>
            (string)counterText.Invoke(null, new object?[] { chars, charLimit, bytes, byteLimit })!;

        Equal("5/64", Counter(5, 64), "plain char counters stay compact");
        Equal("2730 字符 · 8190/8192 字节", Counter(2730, null, 8190, 8192),
            "byte-budgeted counters show both dimensions");
        True(Counter(2731, null, 8193, 8192).Contains("已超出", StringComparison.Ordinal),
            "an overflow is stated on the counter itself");
        True(Counter(257, 256).Contains("已超出", StringComparison.Ordinal),
            "char-only counters state overflow too");

        var dangerBrush = (System.Windows.Media.Brush)Application.Current!.Resources["DangerBrush"];
        var warningBrush = (System.Windows.Media.Brush)Application.Current!.Resources["WarningBrush"];
        var tertiaryBrush = (System.Windows.Media.Brush)Application.Current!.Resources["TextTertiaryBrush"];

        System.Windows.Media.Brush Tone(int? chars = null, int? charLimit = null, int? bytes = null, int? byteLimit = null)
        {
            var block = new TextBlock();
            setCounterTone.Invoke(null, new object?[] { block, chars, charLimit, bytes, byteLimit });
            return (System.Windows.Media.Brush)block.GetValue(TextBlock.ForegroundProperty);
        }

        True(ColorsEqual(Tone(chars: 2731, charLimit: null, bytes: 8193, byteLimit: 8192), dangerBrush),
            "an overflowing counter turns danger");
        True(ColorsEqual(Tone(chars: 60, charLimit: 64), warningBrush),
            ">=90% usage turns warning");
        True(ColorsEqual(Tone(chars: 10, charLimit: 64), tertiaryBrush),
            "comfortable usage stays tertiary");
    }

    private static bool ColorsEqual(System.Windows.Media.Brush left, System.Windows.Media.Brush right) =>
        left is System.Windows.Media.SolidColorBrush a && right is System.Windows.Media.SolidColorBrush b && a.Color == b.Color;

    /// <summary>While a save is committing the template list must be dead to
    /// mouse AND keyboard, and the save button must name the in-flight
    /// work instead of offering a second click.</summary>
    public static void PromptSavingLocksTemplateList()
    {
        EnsureApp();
        var section = new PromptSection();
        var saving = section.GetType().GetField("_saving", NonPublicInstance)!;

        saving.SetValue(section, true);
        InvokePrivate(section, "UpdateInteractivity");
        True(!section.ListHost.IsEnabled && !section.ListHost.IsHitTestVisible,
            "saving must lock the list against mouse and keyboard");
        True(!section.SaveTemplateButton.IsEnabled, "the save must not be re-entered");
        Equal("正在保存…", (string)section.SaveTemplateButton.Content, "the button names the in-flight save");
        True(!section.DeleteTemplateButton.IsEnabled, "deletes wait for the save to settle");

        saving.SetValue(section, false);
        InvokePrivate(section, "UpdateInteractivity");
        True(section.ListHost.IsEnabled && section.ListHost.IsHitTestVisible,
            "a settled save hands the list back");
        Equal("保存", (string)section.SaveTemplateButton.Content, "the button returns to its save wording");
    }

    /// <summary>The two-step delete confirmation must arm (visual + tooltip +
    /// auto-disarm timer) and MUST be dismountable directly, with the exact
    /// original Content/ToolTip restored — the test never waits the 3 s.</summary>
    public static void PromptDeleteArmDisarmWithoutWaiting()
    {
        EnsureApp();
        var section = new PromptSection();
        var button = new Button { Content = "删除", Tag = "wave-delete-probe" };

        InvokePrivate(section, "DeleteTemplate_Click", button, new RoutedEventArgs());
        Equal("确认删除", (string)button.Content!, "the first click arms the confirmation");
        True((button.ToolTip as string)?.Contains("再次点击确认删除", StringComparison.Ordinal) == true,
            "the armed tooltip states the two-step contract");
        True(GetPrivate<DispatcherTimer>(section, "_deleteArmTimer").IsEnabled,
            "the 3-second auto-disarm timer is running (and this test never waits for it)");

        InvokePrivate(section, "DisarmDelete");
        Equal("删除", (string)button.Content!, "disarming restores the exact original content");
        True(button.ToolTip is null, "disarming restores the exact original tooltip");
        True(ReferenceEquals(button.ReadLocalValue(Button.BackgroundProperty), DependencyProperty.UnsetValue) &&
             ReferenceEquals(button.ReadLocalValue(Button.ForegroundProperty), DependencyProperty.UnsetValue),
            "disarming clears the danger paint back to the style default");
        True(!GetPrivate<DispatcherTimer>(section, "_deleteArmTimer").IsEnabled,
            "the timer stops on disarm");

        // Arming never touched the core: the probe id still resolves to nothing deleted.
        True(CoreBridge.ListPromptTemplates().Any(t => t.IsBuiltIn),
            "arming alone must not delete anything");
    }

    /// <summary>
    /// The Enabled toggle is a real persistence channel (the old bug wrote
    /// an unconditional true, so a disable could never be saved), and a
    /// pointed-at but disabled template honestly falls back to the built-in
    /// faithful style until it is re-enabled.
    /// </summary>
    public static async Task PromptEnabledPersistenceAndFaithfulFallbackAsync()
    {
        const string templateId = "wave-enabled-fallback";
        try
        {
            var saved = await CoreBridge.SavePromptTemplateAsync(new PromptTemplateDto(templateId)
            {
                Name = "停用回退",
                Description = "wave 回归",
                Instruction = "把{{source_language}}译成{{target_language}}。",
                Enabled = false,
            });
            True(!saved.Enabled, "the save must persist the disabled toggle, never an unconditional true");
            True(!CoreBridge.ListPromptTemplates().First(t => t.Id == templateId).Enabled,
                "the disabled state survives persistence");

            await CoreBridge.SetActivePromptTemplateAsync(templateId);
            var active = CoreBridge.GetActivePromptTemplate();
            Equal(CoreBridge.FaithfulTemplateId, active.Id,
                "a disabled template must not run as the active style: the core falls back to faithful");
            True(active.IsBuiltIn, "the fallback active template is the built-in faithful");

            var enabled = await CoreBridge.SavePromptTemplateAsync(saved with { Enabled = true });
            True(enabled.Enabled, "re-enabling persists");
            Equal(templateId, CoreBridge.GetActivePromptTemplate().Id,
                "an enabled pointed-at template becomes the real active style");

            var source = File.ReadAllText(Path.Combine(
                FindProjectRoot(), "apps", "PopGlot.Windows", "Sections", "PromptSection.xaml.cs"));
            True(source.Contains("Enabled: EnabledToggle.IsChecked == true", StringComparison.Ordinal),
                "TrySaveAsync must write the editor toggle value verbatim");
        }
        finally
        {
            await CoreBridge.SetActivePromptTemplateAsync(null);
            try { await CoreBridge.DeletePromptTemplateAsync(templateId); }
            catch { /* cleanup best effort — the isolated core is discarded with the run */ }
        }
    }

    // ===================== F: theme / chrome / wording =====================

    /// <summary>
    /// The thin-scrollbar contract: a 5 DIP visible sliver centred in a full
    /// 12 DIP track, both axes — thin look, forgiving grab.
    /// </summary>
    public static void ScrollbarSliverAndHitTarget()
    {
        var controls = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Themes", "Controls.xaml"));
        True(controls.Contains("<Border x:Name=\"Bar\" Width=\"5\" HorizontalAlignment=\"Center\"", StringComparison.Ordinal),
            "the vertical visible bar must stay a 5 DIP centred sliver");
        True(controls.Contains("<Border x:Name=\"Bar\" Height=\"5\" VerticalAlignment=\"Center\"", StringComparison.Ordinal),
            "the horizontal visible bar must stay a 5 DIP centred sliver");
        True(controls.Contains("<Setter Property=\"Width\" Value=\"12\" />", StringComparison.Ordinal) &&
             controls.Contains("<Setter Property=\"MinWidth\" Value=\"12\" />", StringComparison.Ordinal),
            "the vertical track must own the full 12 DIP hit width");
        True(controls.Contains("<Setter Property=\"Height\" Value=\"12\" />", StringComparison.Ordinal) &&
             controls.Contains("<Setter Property=\"MinHeight\" Value=\"12\" />", StringComparison.Ordinal),
            "the horizontal track must own the full 12 DIP hit height");
        True(controls.Contains("Opacity=\"0.55\"", StringComparison.Ordinal),
            "the resting sliver stays visible (0.55), not the vanishing 0.4");
    }

    /// <summary>Caption buttons participate in keyboard focus with the shared
    /// ring — the old Focusable=False made min/max/close unreachable.</summary>
    public static void CaptionButtonsStayFocusable()
    {
        var controls = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Themes", "Controls.xaml"));
        var captionStyle = controls[controls.IndexOf("<Style x:Key=\"CaptionButton\"", StringComparison.Ordinal)..];
        var styleEnd = captionStyle.IndexOf("</Style>", StringComparison.Ordinal);
        True(styleEnd > 0, "the CaptionButton style must terminate");
        var body = captionStyle[..styleEnd];
        True(body.Contains("FocusVisualStyle\" Value=\"{StaticResource FocusRing}\"", StringComparison.Ordinal),
            "CaptionButton must use the shared FocusRing focus visual, not x:Null");
        True(body.Contains("<Setter Property=\"Focusable\" Value=\"False\" />", StringComparison.Ordinal) == false,
            "CaptionButton must not opt out of focus");
        True(controls.Contains("x:Key=\"FocusRing\"", StringComparison.Ordinal),
            "the FocusRing resource itself must exist");
        True(controls.Contains("x:Key=\"CaptionCloseButton\" TargetType=\"Button\" BasedOn=\"{StaticResource CaptionButton}\"", StringComparison.Ordinal),
            "the close variant inherits the focusable base style");
    }

    /// <summary>
    /// Single responsive source: RootGrid_SizeChanged is the ONLY driver of
    /// the breakpoints (two racing reporters used to conflict at window
    /// edges), and the agreed 720/880/960 breakpoints reach the section.
    /// </summary>
    public static void MainWindowSingleResponsiveSource()
    {
        EnsureApp();
        var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");
        var source = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml.cs"));
        var sizeHandlers = Regex.Matches(source, @"SizeChanged\s*\+=").Count;
        True(sizeHandlers <= 1,
            $"RootGrid_SizeChanged must be the single responsive driver, found {sizeHandlers} SizeChanged subscriptions");
        var mainXaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));
        True(mainXaml.Contains("SizeChanged=\"RootGrid_SizeChanged\"", StringComparison.Ordinal),
            "the single handler is wired exactly once, from XAML");
        True(source.Contains("< 720", StringComparison.Ordinal) &&
             source.Contains("< 880", StringComparison.Ordinal) &&
             source.Contains(">= 960", StringComparison.Ordinal),
            "the agreed breakpoints 720/880/960 must stay in the one handler");

        var main = new MainWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        var apply = typeof(MainWindow).GetMethod("ApplyResponsiveBreakpoints", NonPublicInstance)!;
        apply.Invoke(main, new object?[] { 600d });
        Equal(48d, main.SidebarColumn.Width.Value, "below 720 folds the sidebar to compact 48 DIP");
        Equal(1, main.TranslateSection.PaneGrid.ColumnDefinitions.Count,
            "below 720 stacks the workbench panes vertically");
        apply.Invoke(main, new object?[] { 959d });
        Equal(168d, main.SidebarColumn.Width.Value, "720–959 restores the workstation sidebar");
        Equal(3, main.TranslateSection.PaneGrid.ColumnDefinitions.Count,
            "720–959 returns the panes side by side (source | centre axis | target, per the T11 grid)");
        apply.Invoke(main, new object?[] { 960d });
        Equal(3, main.TranslateSection.PaneGrid.ColumnDefinitions.Count,
            ">= 960 keeps the wide dual-column workstation layout (source | centre axis | target)");
    }

    /// <summary>
    /// The close button means what it will do:「关闭到托盘」under tray
    /// residency,「退出 PopGlot」without — and Wording constants stay the
    /// single source of truth with no legacy terms anywhere in the app.
    /// </summary>
    public static void CloseButtonWordingFollowsTraySetting()
    {
        EnsureApp();
        var original = ShellSettingsStore.Load();
        try
        {
            var main = new MainWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);

            ShellSettingsStore.Save(original with { CloseMainWindowToTray = true });
            main.RefreshCloseButtonForTraySetting();
            Equal(EngineWording.CloseToTrayAction, AutomationProperties.GetName(main.CloseBtn),
                "with tray residency the close button announces tray");
            Equal(EngineWording.CloseToTrayAction, main.CloseBtn.ToolTip as string,
                "tooltip and accessibility name must agree");

            ShellSettingsStore.Save(original with { CloseMainWindowToTray = false });
            main.RefreshCloseButtonForTraySetting();
            Equal(EngineWording.ExitAppAction, AutomationProperties.GetName(main.CloseBtn),
                "without tray residency the same button announces exit");
            Equal(EngineWording.ExitAppAction, main.CloseBtn.ToolTip as string,
                "tooltip and accessibility name must agree after the flip");

            // The retired legacy term must not reappear anywhere in the app
            // (EngineWording.cs only names it to ban it in its doc comment).
            var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");
            var offenders = new List<string>();
            foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".xaml", StringComparison.Ordinal)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                if (Path.GetFileName(file) == "EngineWording.cs") continue;
                if (File.ReadAllText(file).Contains("纯离线", StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
            True(offenders.Count == 0,
                "「纯离线模式」 is retired — the unified term is 安全离线模式: " + string.Join(", ", offenders));
        }
        finally
        {
            ShellSettingsStore.Save(original);
        }
    }

    /// <summary>
    /// The typed route matrix: every remaining UI control decision rides the
    /// typed enums, never the Chinese PipelineLabel display string, and the
    /// coordinator's free/vision branches set the typed triple honestly.
    /// </summary>
    public static void EngineWordingAndTypedRouteMatrix()
    {
        var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");

        // 1) No control logic may branch on the display label.
        foreach (var file in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            True(!Regex.IsMatch(text, @"PipelineLabel\s*(==|!=)"),
                $"{Path.GetFileName(file)} must not branch on the PipelineLabel display string");
        }

        // 2) The two post-hoc honesty notices branch on a TYPED route fact —
        //    the executor or the coordinator's prompt-support marking
        //    (NotSupported ⇔ the free engine ran the text stage) — never on
        //    the Chinese PipelineLabel display string.
        var quickSearch = File.ReadAllText(Path.Combine(appDir, "QuickSearchWindow.xaml.cs"));
        True(quickSearch.Contains("TranslationTextExecutor.FreeEngine", StringComparison.Ordinal) ||
             quickSearch.Contains("TranslationPromptSupport.NotSupported", StringComparison.Ordinal),
            "the quick-search free-engine notice must ride the typed executor/prompt-support fact");
        var translate = File.ReadAllText(Path.Combine(appDir, "Sections", "TranslateSection.xaml.cs"));
        True(translate.Contains("TranslationTextExecutor.FreeEngine", StringComparison.Ordinal) ||
             translate.Contains("TranslationPromptSupport.NotSupported", StringComparison.Ordinal),
            "the workbench free-engine notice must ride the typed executor/prompt-support fact");

        // 3) The coordinator's route matrix sets the typed triple honestly.
        var coordinator = File.ReadAllText(Path.Combine(appDir, "Services", "TranslationCoordinator.cs"));
        True(coordinator.Contains("session.TextExecutor = TranslationTextExecutor.FreeEngine;", StringComparison.Ordinal) &&
             coordinator.Contains("session.PromptSupport = TranslationPromptSupport.NotSupported;", StringComparison.Ordinal),
            "both free routes must record TextExecutor=FreeEngine + PromptSupport=NotSupported");
        True(coordinator.Contains("session.PipelineKind = TranslationPipelineKind.VisionDirect;", StringComparison.Ordinal) &&
             coordinator.Contains("session.PromptSupport = TranslationPromptSupport.NotApplicable;", StringComparison.Ordinal),
            "the vision-direct route must record NotApplicable (no text stage, nothing promised)");
        True(coordinator.Contains("session.PipelineLabel = EngineWording.ActiveFreeEngineName();", StringComparison.Ordinal),
            "the free-engine label comes from the single EngineWording source");
        True(coordinator.Contains("TranslationPipelineKind.OcrUserText;", StringComparison.Ordinal) &&
             coordinator.Contains("TranslationPipelineKind.OcrFreeText;", StringComparison.Ordinal),
            "both OCR+text routes carry their own typed kind");

        // 4) The display label and the typed engine agree by construction.
        Equal("内置免费引擎", EngineWording.FreeEngineName,
            "the single wording source keeps the free-engine name");
        Equal("备用免费引擎", EngineWording.AlternateFreeEngineName,
            "the alternate public engine has one name");
        Equal(EngineWording.FreeEngineName, EngineWording.NameFor(FreeEngineProvider.Google));
        Equal(EngineWording.AlternateFreeEngineName, EngineWording.NameFor(FreeEngineProvider.MyMemory));
    }

    // ===================== F2: judged-visual regressions =====================

    /// <summary>
    /// Old bug this pins: SettingsWindow.ShowPage never moved the nav rail,
    /// so a programmatic page change (the main-window add-engine flow on an
    /// already-open window, close-time draft guards) showed the new page with
    /// the PREVIOUS sidebar item still highlighted. The rail must follow the
    /// page exactly like a radio click would.
    /// </summary>
    public static void SettingsNavRailFollowsProgrammaticShowPage()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            window.ShowPage("Privacy");
            True(window.NavPrivacy.IsChecked == true,
                "ShowPage(\"Privacy\") must check the 隐私与数据 nav item");
            True(window.NavProvider.IsChecked == false,
                "ShowPage(\"Privacy\") must uncheck the 翻译引擎 nav item");
            True(window.PrivacyPageHost.Visibility == Visibility.Visible,
                "ShowPage(\"Privacy\") must show the privacy page");

            window.ShowPage("Prompt");
            True(window.NavPrompt.IsChecked == true,
                "ShowPage(\"Prompt\") must check the 翻译与提示词 nav item");
            True(window.NavPrivacy.IsChecked == false,
                "ShowPage(\"Prompt\") must uncheck the previous nav item");
            True(window.NavGeneral.IsChecked == false && window.NavShortcuts.IsChecked == false &&
                 window.NavProvider.IsChecked == false,
                "exactly one nav item may stay checked");

            // The production trigger: the add-engine CTA on a window parked on
            // another page must bring the rail back to 翻译引擎.
            window.ShowPage("Privacy");
            True(window.NavPrivacy.IsChecked == true, "parking on Privacy must light its nav item");
            window.ShowProviderAddFlow();
            True(window.NavProvider.IsChecked == true,
                "ShowProviderAddFlow must return the rail highlight to 翻译引擎");
            True(window.ProviderSection.Visibility == Visibility.Visible,
                "ShowProviderAddFlow must actually show the provider page");
        }
        finally
        {
            window.ForceClose = true;
            window.Close();
        }
    }

    /// <summary>
    /// Old bug this pins: the panel's failure path assigned theme brushes via
    /// (Brush)FindResource — a static reference that keeps the color of the
    /// theme active AT FAILURE TIME. A dark screenshot rendered with light
    /// tokens (#4D545F explanation on a #181B22 surface): near-black on black.
    /// The failure copy must hold dynamic resource references and its RESOLVED
    /// colors must follow a live theme switch.
    /// </summary>
    public static void PanelErrorBrushesTrackLiveTheme()
    {
        EnsureApp();
        ThemeService.Apply(ThemePreference.Dark);
        var panel = NewPanel();
        try
        {
            DrivePanelSession(panel, "demo source for the error state", new TranslationSession
            {
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(
                    TranslationErrorKind.ServerError,
                    "连接超时（120 秒无响应）：mock-provider/deployment",
                    "检查服务地址与网络后重试。"),
            });

            // 1) The failure-critical visuals must be dynamic resource
            //    references, never baked brushes.
            True(panel.TranslationTextBox.ReadLocalValue(Control.ForegroundProperty) is not Brush,
                "the error headline foreground must be a dynamic resource reference");
            True(panel.ExplanationText.ReadLocalValue(TextBlock.ForegroundProperty) is not Brush,
                "the error explanation foreground must be a dynamic resource reference");
            True(panel.EngineBadge.ReadLocalValue(TextBlock.ForegroundProperty) is not Brush,
                "the result badge foreground must be a dynamic resource reference");
            True(panel.StatusDot.ReadLocalValue(Border.BackgroundProperty) is not Brush,
                "the status dot background must be a dynamic resource reference");

            // 2) The resolved colors must be the DARK palette while dark.
            var darkSecondary = (Color)ColorConverter.ConvertFromString("#A8B0BD");
            var darkDanger = (Color)ColorConverter.ConvertFromString("#FF6B7D");
            Equal(darkSecondary, ((SolidColorBrush)panel.ExplanationText.GetValue(TextBlock.ForegroundProperty)!).Color,
                "the error explanation must resolve the dark TextSecondaryBrush");
            Equal(darkDanger, ((SolidColorBrush)panel.EngineBadge.GetValue(TextBlock.ForegroundProperty)!).Color,
                "the failed badge must resolve the dark DangerBrush");
            Equal(darkDanger, ((SolidColorBrush)panel.StatusDot.GetValue(Border.BackgroundProperty)!).Color,
                "the failed status dot must resolve the dark DangerBrush");

            // 3) Switching the theme NOW must repaint the already-rendered
            //    failure state — this is exactly what went black before.
            ThemeService.Apply(ThemePreference.Light);
            var lightSecondary = (Color)ColorConverter.ConvertFromString("#4D545F");
            var lightDanger = (Color)ColorConverter.ConvertFromString("#C93148");
            Equal(lightSecondary, ((SolidColorBrush)panel.ExplanationText.GetValue(TextBlock.ForegroundProperty)!).Color,
                "the error explanation must follow the theme switch to light");
            Equal(lightDanger, ((SolidColorBrush)panel.EngineBadge.GetValue(TextBlock.ForegroundProperty)!).Color,
                "the failed badge must follow the theme switch to light");
            Equal(lightDanger, ((SolidColorBrush)panel.StatusDot.GetValue(Border.BackgroundProperty)!).Color,
                "the failed status dot must follow the theme switch to light");
        }
        finally
        {
            ThemeService.Apply(ThemePreference.Dark);
            panel.ForceClose = true;
            panel.Close();
        }
    }

    /// <summary>
    /// The floating panel's REAL footprint is 540x380 DIP (opening size and
    /// the height tier for short sources) with a 460x380 user-resize floor —
    /// the retired 420x520/560 captures showed windows the product cannot
    /// open. At every reachable bound the fixed footer must stay inside the
    /// canvas; if this ever fails, the production layout is clipped, not the
    /// screenshot producer.
    /// </summary>
    public static void PanelFooterStaysInsideRealFootprint()
    {
        EnsureApp();
        var panel = NewPanel();
        try
        {
            DrivePanelSession(panel, "demo source for the error state", new TranslationSession
            {
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(
                    TranslationErrorKind.ServerError,
                    "连接超时（120 秒无响应）：mock-provider/northcentralus/deployments/very-long-deployment-name-20260905",
                    "检查服务地址与网络后重试；离线模式会阻止本次请求。"),
            });

            var content = (FrameworkElement)panel.Content;
            foreach (var (width, height, label) in new[]
                     {
                         (540d, 380d, "default 540x380"),
                         (460d, 380d, "minimum 460x380"),
                     })
            {
                panel.Width = width;
                panel.Height = height;
                content.Width = width;
                content.Height = height;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();

                True(panel.StatusTextBlock.ActualHeight > 0,
                    $"{label}: the footer must be laid out before the bounds check");
                var footerBottom = panel.StatusTextBlock
                    .TransformToVisual(content).Transform(new Point(0, panel.StatusTextBlock.ActualHeight)).Y;
                True(footerBottom <= height + 0.5,
                    $"{label}: the footer bottom ({footerBottom:F1}) exceeds the {height} DIP window — the real layout clips the status row");
            }
        }
        finally
        {
            panel.ForceClose = true;
            panel.Close();
        }
    }

    // ===================== H: accessibility names =====================

    /// <summary>
    /// Raises the REAL static <see cref="TtsService.SpeakingStateChanged"/>
    /// event the three surfaces subscribe to. Reflection is only needed
    /// because C# forbids raising another class's event from outside.
    /// </summary>
    private static void FireTtsSpeakingStateChanged(bool isSpeaking)
    {
        var field = typeof(TtsService).GetField(
                nameof(TtsService.SpeakingStateChanged),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("the TtsService.SpeakingStateChanged event field was not found");
        (field.GetValue(null) as EventHandler<bool>)?.Invoke(null, isSpeaking);
    }

    /// <summary>
    /// All five TTS buttons across the three surfaces must flip their
    /// accessibility name between「停止朗读」(speaking) and the concrete
    ///「朗读译文」/「朗读原文」(idle), and the live name must stay consistent
    /// with the live ToolTip — screen readers announce the action the click
    /// performs RIGHT NOW, not the idle label.
    /// </summary>
    public static void TtsAutomationNamesFollowSpeakingState()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        var panel = NewPanel();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        // The workbench subscribes to the TTS event only from Loaded: host it
        // in a real window and show it so the production subscription path runs.
        var host = new Window
        {
            Content = section,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        try
        {
            host.Show();
            PumpUntil(Task.CompletedTask);
            PumpOnce();

            var buttons = new (System.Windows.Controls.Button Button, string Idle)[]
            {
                (quickSearch.SpeakButton, "朗读译文"),
                (panel.SourceSpeakBtn, "朗读原文"),
                (panel.ResultSpeakBtn, "朗读译文"),
                (section.TranslateSourceSpeakButton, "朗读原文"),
                (section.TranslateResultSpeakButton, "朗读译文"),
            };

            // Speaking: every button announces the stop action it now performs.
            FireTtsSpeakingStateChanged(true);
            PumpOnce();
            foreach (var (button, _) in buttons)
            {
                Equal("停止朗读", AutomationProperties.GetName(button),
                    $"{button.Name} must announce 停止朗读 while speaking");
                True((button.ToolTip as string ?? string.Empty)
                        .StartsWith(AutomationProperties.GetName(button), StringComparison.Ordinal),
                    $"{button.Name}: the live ToolTip must agree with the live name");
            }

            // Idle: the concrete reading action returns.
            FireTtsSpeakingStateChanged(false);
            PumpOnce();
            foreach (var (button, idle) in buttons)
            {
                Equal(idle, AutomationProperties.GetName(button),
                    $"{button.Name} must announce {idle} again once speech stopped");
                True((button.ToolTip as string ?? string.Empty)
                        .StartsWith(idle, StringComparison.Ordinal),
                    $"{button.Name}: the live ToolTip must agree with the live name");
            }
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            host.Close();
        }
    }

    /// <summary>
    /// The star controls on the workbench and the floating panel must sync
    /// their accessibility name with the starred state (and the ToolTip) on
    /// BOTH transitions, so screen readers announce the action the click will
    /// actually perform.
    /// </summary>
    public static void StarAutomationNamesFollowStarredState()
    {
        EnsureApp();
        // Workbench: the shared visual-state writer owns ToolTip + name.
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        InvokePrivate(section, "UpdateStarVisualState", true);
        Equal("从生词本移除", AutomationProperties.GetName(section.StarButton),
            "a starred workbench entry must announce the remove action");
        Equal(section.StarButton.ToolTip as string, AutomationProperties.GetName(section.StarButton),
            "the workbench tooltip and accessibility name must agree");
        InvokePrivate(section, "UpdateStarVisualState", false);
        Equal("收藏到生词本", AutomationProperties.GetName(section.StarButton),
            "an unstarred workbench entry must announce the star action");
        Equal(section.StarButton.ToolTip as string, AutomationProperties.GetName(section.StarButton),
            "the workbench tooltip and accessibility name must agree after unstarring");

        // Floating panel: the same contract on the toggle.
        var panel = NewPanel();
        try
        {
            InvokePrivate(panel, "UpdateStarIcon", true);
            Equal("从生词本移除", AutomationProperties.GetName(panel.StarToggle),
                "a starred panel result must announce the remove action");
            Equal(panel.StarToggle.ToolTip as string, AutomationProperties.GetName(panel.StarToggle),
                "the panel tooltip and accessibility name must agree");
            InvokePrivate(panel, "UpdateStarIcon", false);
            Equal("收藏到生词本", AutomationProperties.GetName(panel.StarToggle),
                "an unstarred panel result must announce the star action");
            Equal(panel.StarToggle.ToolTip as string, AutomationProperties.GetName(panel.StarToggle),
                "the panel tooltip and accessibility name must agree after unstarring");
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }

        // Quick search: the STATIC first frame must already announce the star
        // action, agreeing with the first-frame ToolTip before any star
        // interaction runs (it used to say「加入生词本」while the tooltip said
        //「收藏到生词本」).
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            Equal("收藏到生词本", AutomationProperties.GetName(quickSearch.StarButton),
                "the quick search star must offer the star action on its static first frame");
            True(((string?)quickSearch.StarButton.ToolTip ?? string.Empty)
                    .StartsWith(AutomationProperties.GetName(quickSearch.StarButton), StringComparison.Ordinal),
                "the quick search first-frame tooltip and accessibility name must agree");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
        }
    }

    /// <summary>
    /// The settings caption button must announce the state the click will
    /// switch TO —「向下还原」while maximized,「最大化」while restored — with
    /// the name riding the same caption as the ToolTip.
    /// </summary>
    public static void SettingsMaximizeAutomationNameFollowsWindowState()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            window.Show();
            PumpUntil(Task.CompletedTask);
            var maximize = window.MaximizeBtn;
            Equal("最大化", AutomationProperties.GetName(maximize),
                "a restored window must offer 最大化");
            Equal(maximize.ToolTip as string, AutomationProperties.GetName(maximize),
                "tooltip and accessibility name must agree while restored");

            // The real StateChanged hook drives the caption; wait bounded so a
            // deferred native maximize still settles before the assertion.
            window.WindowState = WindowState.Maximized;
            for (var attempt = 0; attempt < 200 && AutomationProperties.GetName(maximize) != "向下还原"; attempt++)
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            Equal("向下还原", AutomationProperties.GetName(maximize),
                "a maximized window must offer 向下还原");
            Equal(maximize.ToolTip as string, AutomationProperties.GetName(maximize),
                "tooltip and accessibility name must agree while maximized");

            window.WindowState = WindowState.Normal;
            for (var attempt = 0; attempt < 200 && AutomationProperties.GetName(maximize) != "最大化"; attempt++)
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(5);
            }
            Equal("最大化", AutomationProperties.GetName(maximize),
                "restoring must offer 最大化 again");
            Equal(maximize.ToolTip as string, AutomationProperties.GetName(maximize),
                "tooltip and accessibility name must agree after restoring");
        }
        finally
        {
            window.ForceClose = true;
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// The core reading surfaces' input/output controls and the floating
    /// panel's window title carry their agreed exact names — assistive tech
    /// and window enumerators get stable, specific labels, never blanks.
    /// </summary>
    public static void CoreReadingControlsCarryExactAutomationNames()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        var panel = NewPanel();
        try
        {
            Equal("翻译结果", AutomationProperties.GetName(quickSearch.RichBox),
                "the quick-search final result box must be named 翻译结果");

            Equal("翻译原文输入框", AutomationProperties.GetName(panel.SourceInputBox),
                "the panel source input must be named 翻译原文输入框");
            Equal("翻译结果", AutomationProperties.GetName(panel.StreamTextBox),
                "the panel plain result box must be named 翻译结果");
            Equal("格式化翻译结果", AutomationProperties.GetName(panel.FinalRichBox),
                "the panel markdown result box must be named 格式化翻译结果");
            Equal("原文语言", AutomationProperties.GetName(panel.SourceLangCombo),
                "the panel source language picker must be named 原文语言");
            Equal("译文语言", AutomationProperties.GetName(panel.TargetLangCombo),
                "the panel target language picker must be named 译文语言");
            Equal("PopGlot 翻译浮窗", panel.Title,
                "the floating panel window must carry its agreed title");
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
        }
    }

    /// <summary>
    /// Each of the five hotkey recorders must announce its ROW's function,
    /// carry the real recording contract as HelpText, and behave exactly as
    /// that HelpText promises: clicking starts recording, and a real Escape
    /// key event cancels it (falling back to the stop path the Esc branch
    /// calls when no key seam exists).
    /// </summary>
    public static void HotkeyRecordersAnnounceRowFunctionAndHowToRecord()
    {
        EnsureApp();
        var section = new ShortcutsSection();
        var recorders = new (HotkeyRecorder Recorder, string Row)[]
        {
            (section.SelectionHotkey, "划词翻译"),
            (section.ScreenshotHotkey, "截图翻译"),
            (section.QuickSearchHotkey, "极速查词快捷键"),
            (section.CloseHotkey, "关闭浮窗"),
            (section.ShowWindowHotkey, "打开主窗口快捷键"),
        };
        // A real window gives the recorders a PresentationSource so the real
        // Esc key path can be exercised below.
        var host = new Window
        {
            Content = section,
            Width = 800,
            Height = 500,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        try
        {
            host.Show();
            PumpUntil(Task.CompletedTask);
            PumpOnce();

            foreach (var (recorder, row) in recorders)
            {
                Equal(row, AutomationProperties.GetName(recorder),
                    $"{recorder.Name} must announce the function of its row");
                Equal("点击后按下组合键完成录制，Esc 取消录制", AutomationProperties.GetHelpText(recorder),
                    $"{recorder.Name} must state the real recording contract");

                // The HelpText must not be a lie: exercise the REAL methods the
                // click and Esc paths use.
                InvokePrivate(recorder, "StartRecording");
                True(recorder.IsRecording, "starting a recording must enter the recording state");
                Equal("按下组合键…", (string)recorder.Content!, "recording names the wait for the combination");

                var keySource = PresentationSource.FromDependencyObject(recorder);
                if (keySource is not null)
                {
                    // The recorder sits in a shown window, so this hand-built
                    // KeyEventArgs is a real seam. RoutedEvent must be set: the
                    // Esc branch marks the args Handled, which validates it.
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice!, keySource, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    typeof(HotkeyRecorder)
                        .GetMethod("OnPreviewKeyDown", NonPublicInstance)!
                        .Invoke(recorder, new object?[] { escape });
                }
                else
                {
                    // No safe KeyEventArgs seam: fall back to the same stop
                    // method the Esc branch itself calls.
                    InvokePrivate(recorder, "StopRecording");
                }
                True(!recorder.IsRecording, "Esc during recording must cancel it, as the HelpText promises");
                Equal(recorder.BindingValue?.DisplayName ?? "未设置", (string)recorder.Content!,
                    "cancelling restores the binding label");
            }
        }
        finally
        {
            host.Close();
        }
    }

    /// <summary>
    /// While SPEAKING, the workbench speak icon's accent must be a live
    /// dynamic resource reference: switching the theme MID-SPEECH repaints the
    /// icon (a static FindResource accent used to bake in the start-time
    /// theme). Stopping restores the XAML's exact idle semantics — the dynamic
    /// TextSecondaryBrush — which keeps following the theme too.
    /// </summary>
    public static void TtsSpeakingIconTracksLiveTheme()
    {
        EnsureApp();
        ThemeService.Apply(ThemePreference.Dark);
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        try
        {
            host.Show();
            PumpUntil(Task.CompletedTask);
            PumpOnce();

            Color LiveColor(string key) =>
                ((SolidColorBrush)Application.Current!.Resources[key]).Color;
            var fill = System.Windows.Shapes.Shape.FillProperty;

            // Speaking: the accent is a dynamic reference resolving the live
            // accent — never a baked brush.
            FireTtsSpeakingStateChanged(true);
            PumpOnce();
            True(section.TranslateSourceSpeakIcon.ReadLocalValue(fill) is not Brush,
                "the speaking accent must be a dynamic resource reference, never a baked brush");
            True(section.TranslateResultSpeakIcon.ReadLocalValue(fill) is not Brush,
                "the speaking accent must be a dynamic resource reference, never a baked brush");
            Equal(LiveColor("AccentBrush"),
                ((SolidColorBrush)section.TranslateSourceSpeakIcon.GetValue(fill)).Color,
                "while speaking the icon resolves the live accent");

            // The production bug this pins: a theme switch MID-SPEECH.
            ThemeService.Apply(ThemePreference.Light);
            PumpOnce();
            Equal(LiveColor("AccentBrush"),
                ((SolidColorBrush)section.TranslateResultSpeakIcon.GetValue(fill)).Color,
                "a theme switch while speaking must repaint the icon accent");

            // Stopping restores the XAML's dynamic TextSecondaryBrush (a
            // ClearValue would erase the XAML expression, leaving no fill).
            FireTtsSpeakingStateChanged(false);
            PumpOnce();
            True(section.TranslateSourceSpeakIcon.ReadLocalValue(fill) is not Brush,
                "the idle fill must stay a dynamic resource reference");
            Equal(LiveColor("TextSecondaryBrush"),
                ((SolidColorBrush)section.TranslateSourceSpeakIcon.GetValue(fill)).Color,
                "the idle icon resolves the live TextSecondaryBrush");
            ThemeService.Apply(ThemePreference.Dark);
            PumpOnce();
            Equal(LiveColor("TextSecondaryBrush"),
                ((SolidColorBrush)section.TranslateResultSpeakIcon.GetValue(fill)).Color,
                "the idle icon keeps following the theme");
        }
        finally
        {
            ThemeService.Apply(ThemePreference.Dark);
            host.Close();
        }
    }

    /// <summary>
    /// The QuickSearch and Panel speak icons' XAML Fill is a RelativeSource
    /// Button.Foreground binding. While SPEAKING every surface's icon rides
    /// the dynamic AccentBrush and follows a mid-speech theme switch. Stopping
    /// must RE-BIND the original Foreground binding — the old ClearValue left
    /// the local value UnsetValue and the idle icon with no fill — the binding
    /// must stay live across theme switches, and a second speak/stop cycle
    /// must behave identically.
    /// </summary>
    public static void TtsSpeakIconFillRestoresForegroundBinding()
    {
        EnsureApp();
        ThemeService.Apply(ThemePreference.Dark);
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        var panel = NewPanel();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(IsolatedHistory, IsolatedVocabulary), null);
        var host = new Window
        {
            Content = section,
            Width = 1000,
            Height = 700,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
        };
        try
        {
            host.Show();
            PumpUntil(Task.CompletedTask);
            PumpOnce();

            var fill = System.Windows.Shapes.Shape.FillProperty;
            Color LiveColor(string key) => ((SolidColorBrush)Application.Current!.Resources[key]).Color;
            Color Resolved(System.Windows.Shapes.Path icon) => ((SolidColorBrush)icon.GetValue(fill)).Color;
            Color ForegroundOf(Button button) => ((SolidColorBrush)button.GetValue(Control.ForegroundProperty)).Color;
            var icons = new (System.Windows.Shapes.Path Icon, Button Button, bool RestoresBinding)[]
            {
                (quickSearch.SpeakIcon, quickSearch.SpeakButton, true),
                (panel.SourceSpeakIcon, panel.SourceSpeakBtn, true),
                (panel.ResultSpeakIcon, panel.ResultSpeakBtn, true),
                (section.TranslateSourceSpeakIcon, section.TranslateSourceSpeakButton, false),
                (section.TranslateResultSpeakIcon, section.TranslateResultSpeakButton, false),
            };

            // Speaking: the dynamic accent on all three surfaces, live across
            // a mid-speech theme switch.
            FireTtsSpeakingStateChanged(true);
            PumpOnce();
            foreach (var (icon, _, _) in icons)
            {
                True(icon.ReadLocalValue(fill) is not Brush,
                    "the speaking accent must be a dynamic reference, never a baked brush");
                Equal(LiveColor("AccentBrush"), Resolved(icon), "speaking resolves the live accent");
            }
            ThemeService.Apply(ThemePreference.Light);
            PumpOnce();
            foreach (var (icon, _, _) in icons)
            {
                Equal(LiveColor("AccentBrush"), Resolved(icon),
                    "a mid-speech theme switch must repaint every speaking icon");
            }

            // Stop: QuickSearch and Panel restore the original Foreground
            // binding — never UnsetValue, never a dead fill. The binding is
            // LIVE: a resting speak button is DISABLED (idle gating), so its
            // Foreground legitimately reads TextDisabledBrush — the icon must
            // follow whatever the button Foreground actually is.
            FireTtsSpeakingStateChanged(false);
            PumpOnce();
            foreach (var (icon, button, restoresBinding) in icons)
            {
                True(!ReferenceEquals(icon.ReadLocalValue(fill), DependencyProperty.UnsetValue),
                    "stopping must not leave the fill UnsetValue");
                if (restoresBinding)
                {
                    True(icon.GetBindingExpression(fill) is not null,
                        "the idle icon must carry the restored Foreground binding");
                    Equal(ForegroundOf(button), Resolved(icon),
                        "the restored binding keeps the icon on the live button Foreground (enabled or disabled)");
                }
                else
                {
                    Equal(LiveColor("TextSecondaryBrush"), Resolved(icon),
                        "the workbench idle icon resolves the dynamic TextSecondaryBrush reference");
                }
            }

            // The restored binding is LIVE: repaint the tokens via the theme.
            ThemeService.Apply(ThemePreference.Dark);
            PumpOnce();
            foreach (var (icon, button, restoresBinding) in icons)
            {
                if (restoresBinding)
                {
                    Equal(ForegroundOf(button), Resolved(icon),
                        "the idle icon must follow the button Foreground across a theme switch");
                }
                else
                {
                    Equal(LiveColor("TextSecondaryBrush"), Resolved(icon),
                        "the workbench idle icon keeps following the theme");
                }
            }

            // A second speak/stop cycle must behave identically.
            FireTtsSpeakingStateChanged(true);
            PumpOnce();
            foreach (var (icon, _, _) in icons)
            {
                Equal(LiveColor("AccentBrush"), Resolved(icon),
                    "the second speaking phase resolves the live accent again");
            }
            FireTtsSpeakingStateChanged(false);
            PumpOnce();
            foreach (var (icon, button, restoresBinding) in icons)
            {
                True(!ReferenceEquals(icon.ReadLocalValue(fill), DependencyProperty.UnsetValue),
                    "the second stop must not leave the fill UnsetValue");
                if (restoresBinding)
                {
                    True(icon.GetBindingExpression(fill) is not null,
                        "the second stop must restore the Foreground binding again");
                    Equal(ForegroundOf(button), Resolved(icon),
                        "the second stop must leave the icon on the live button Foreground");
                }
                else
                {
                    Equal(LiveColor("TextSecondaryBrush"), Resolved(icon),
                        "the second stop must restore the dynamic TextSecondaryBrush");
                }
            }
        }
        finally
        {
            quickSearch.ForceClose = true;
            quickSearch.Close();
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            host.Close();
            ThemeService.Apply(ThemePreference.Dark);
        }
    }

    /// <summary>
    /// The panel copy icons' XAML Fill is a RelativeSource Button.Foreground
    /// binding. The copy-success feedback must ride the dynamic accent
    /// (following a mid-feedback theme switch) and its reset must RE-BIND the
    /// original Foreground binding — the old ClearValue left the local value
    /// UnsetValue and the idle icon with no fill. The restored binding must
    /// follow the button Foreground (enabled TextSecondaryBrush or the
    /// disabled trigger's TextDisabledBrush) across theme switches, and a
    /// second feedback cycle must behave identically. Driven through the REAL
    /// ShowCopyFeedback/EndCopyFeedback methods the click handlers call — the
    /// clipboard is never touched.
    /// </summary>
    public static void CopyFeedbackIconsRestoreForegroundBinding()
    {
        EnsureApp();
        ThemeService.Apply(ThemePreference.Dark);
        var panel = NewPanel();
        try
        {
            var fill = System.Windows.Shapes.Shape.FillProperty;
            Color LiveColor(string key) => ((SolidColorBrush)Application.Current!.Resources[key]).Color;
            Color Resolved(System.Windows.Shapes.Path icon) => ((SolidColorBrush)icon.GetValue(fill)).Color;
            Color ForegroundOf(Button button) => ((SolidColorBrush)button.GetValue(Control.ForegroundProperty)).Color;
            var icons = new (System.Windows.Shapes.Path Icon, Button Button, string BindingField)[]
            {
                (panel.SourceCopyIcon, panel.SourceCopyBtn, "_sourceCopyIconFillBinding"),
                (panel.ResultCopyIcon, panel.ResultCopyBtn, "_resultCopyIconFillBinding"),
            };

            for (var cycle = 1; cycle <= 2; cycle++)
            {
                // Feedback begins on both icons: live accent, check glyph.
                foreach (var (icon, _, _) in icons)
                {
                    InvokePrivate(panel, "ShowCopyFeedback", icon);
                    True(icon.ReadLocalValue(fill) is not Brush,
                        "the feedback accent must be a dynamic reference, never a baked brush");
                    Equal(LiveColor("AccentBrush"), Resolved(icon),
                        $"cycle {cycle}: the feedback resolves the live accent");
                    True(ReferenceEquals(icon.Data, panel.FindResource("IconCheck")),
                        $"cycle {cycle}: the feedback swaps the glyph to the check mark");
                }

                // A theme switch MID-FEEDBACK must repaint the accent.
                ThemeService.Apply(ThemePreference.Light);
                PumpOnce();
                foreach (var (icon, _, _) in icons)
                {
                    Equal(LiveColor("AccentBrush"), Resolved(icon),
                        $"cycle {cycle}: a mid-feedback theme switch must repaint the accent");
                }

                // Feedback ends: the original Foreground binding is restored —
                // never UnsetValue, following the button Foreground whatever
                // its enabled state resolves to. FindAncestor bindings queue
                // their initial ancestor walk on the Dispatcher, so pump once
                // before reading the resolved fill.
                foreach (var (icon, button, bindingField) in icons)
                {
                    InvokePrivate(panel, "EndCopyFeedback", icon, GetPrivate<System.Windows.Data.Binding>(panel, bindingField));
                    PumpOnce();
                    True(!ReferenceEquals(icon.ReadLocalValue(fill), DependencyProperty.UnsetValue),
                        $"cycle {cycle}: the reset must not leave the fill UnsetValue");
                    True(icon.GetBindingExpression(fill) is not null,
                        $"cycle {cycle}: the reset must restore the Foreground binding");
                    Equal(ForegroundOf(button), Resolved(icon),
                        $"cycle {cycle}: the restored binding keeps the icon on the live button Foreground (enabled or disabled)");
                    True(ReferenceEquals(icon.Data, panel.FindResource("IconCopy")),
                        $"cycle {cycle}: the reset swaps the glyph back to the copy mark");
                }

                // The restored binding is LIVE: repaint the tokens via theme.
                ThemeService.Apply(ThemePreference.Dark);
                PumpOnce();
                foreach (var (icon, button, _) in icons)
                {
                    Equal(ForegroundOf(button), Resolved(icon),
                        $"cycle {cycle}: the idle icon follows the button Foreground across a theme switch");
                }
            }
        }
        finally
        {
            panel.ForceClose = true;
            try { panel.Close(); } catch { }
            ThemeService.Apply(ThemePreference.Dark);
        }
    }

    // ===================== G: screenshot helper =====================

    /// <summary>
    /// Hosts the real PromptSection editor with an over-budget instruction so
    /// the screenshot shows the danger counters and the honest overflow.
    /// Called from Program's screenshot pass (RenderScreenshotsAndMeasureBaseline).
    /// </summary>
    internal static Window CreatePromptEditorOverLimitPreview(string instruction)
    {
        var section = new PromptSection();
        InvokePrivate(section, "OpenEditor",
            new PromptTemplateDto("wave-over-limit")
            {
                Name = "超限演示模板",
                Description = "演示计数与保存校验",
                Instruction = string.Empty,
                Domain = "软件文档",
                Audience = "开发者",
            },
            true,
            false);
        section.InstructionTextBox.Text = instruction; // fires the real counter + dirty pipeline
        var host = new System.Windows.Controls.Border
        {
            Child = section,
            Padding = new Thickness(24),
        };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CanvasBrush");
        return new Window { Content = host };
    }

    // ===================== H: E3 follow-ups — high contrast + PerMonitorV2 DPI =====================

    /// <summary>Flushes everything up to and including idle priority, so a
    /// production DispatcherPriority.ApplicationIdle continuation (the DPI
    /// settle path) really runs. PumpOnce's Background marker is NOT enough:
    /// ApplicationIdle sits below it.</summary>
    private static void PumpIdle()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("PumpIdle must run on the STA harness thread");
        dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        dispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    /// <summary>
    /// E3-D3: driving the runtime ApplyResolved through HC on → off via the
    /// internal test seam must map the system colours AND fully restore the
    /// normal theme afterwards — without ever touching a real system setting.
    /// Real-HC behaviour stays an explicit E3 machine verification TODO.
    /// </summary>
    public static void HighContrastApplyResolvedRoundTripRestoresNormalTheme()
    {
        EnsureApp();
        try
        {
            ThemeService.Apply(ThemePreference.Dark);
            var darkCanvas = ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color;
            var darkAccent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;

            ThemeService.HighContrastTestOverride = true;
            ThemeService.ApplyResolved();

            Equal(SystemColors.WindowTextColor, ((SolidColorBrush)Application.Current.Resources["WindowEdgeBrush"]).Color,
                "HC must map WindowEdgeBrush to WindowText for a crisp window boundary");
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color,
                "HC must map AccentBrush to Highlight");
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)Application.Current.Resources["AccentHoverBrush"]).Color,
                "HC must map AccentHoverBrush to Highlight");
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)Application.Current.Resources["AccentPressedBrush"]).Color,
                "HC must map AccentPressedBrush to Highlight");
            Equal(SystemColors.WindowColor, ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color,
                "HC must map CanvasBrush to Window");
            Equal(Colors.Transparent, (Color)Application.Current.Resources["ShadowColor"],
                "HC must force ShadowColor transparent");
            True(((SolidColorBrush)Application.Current.Resources["OverlayScrimBrush"]).Color.A < 255,
                "the capture scrim stays translucent in HC (explicit whitelist), never a solid fill");

            ThemeService.HighContrastTestOverride = false;
            ThemeService.ApplyResolved();

            Equal(darkCanvas, ((SolidColorBrush)Application.Current.Resources["CanvasBrush"]).Color,
                "closing HC must restore the normal dark canvas");
            Equal(darkAccent, ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color,
                "closing HC must restore the normal accent");
            Equal(Colors.Transparent, ((SolidColorBrush)Application.Current.Resources["WindowEdgeBrush"]).Color,
                "closing HC must make the window edge transparent again");
            Equal(Colors.Black, (Color)Application.Current.Resources["ShadowColor"],
                "closing HC must restore the shadow colour");
        }
        finally
        {
            ThemeService.HighContrastTestOverride = null;
            ThemeService.ApplyResolved();
        }
    }

    /// <summary>
    /// Pure DIP↔pixel geometry: at 100/125/150/200% reading a physical size
    /// back on the monitor it is on is lossless within one pixel even after
    /// 10 hops, and the F10 anchor holds — a 480 DIP quick search renders
    /// exactly 720 px at 150% and exactly 480 px back at 100% (the DIP value
    /// itself must be preserved across monitors; deriving it from another
    /// monitor's pixels is exactly the 480→720 pollution E3 recorded).
    /// </summary>
    public static void DpiGeometryRoundtripsAreIdentity()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            foreach (var dip in new[] { 480.0, 540.0, 760.0, 1120.0 })
            {
                var pixel = ScreenGeometry.RoundPixel(ScreenGeometry.DipToPixel(dip, scale));
                var back = ScreenGeometry.PixelToDip(pixel, scale);
                True(Math.Abs(back - dip) <= 0.5 / scale + 1e-9,
                    $"{dip} DIP must survive one {scale:P0} hop within half a pixel, got {back}");

                // 10 hops, always read back on the monitor the window is on.
                var value = dip;
                for (var hop = 0; hop < 10; hop++)
                {
                    value = ScreenGeometry.RoundtripDip(value, scale, scale);
                }
                True(Math.Abs(ScreenGeometry.RoundPixel(ScreenGeometry.DipToPixel(value, scale)) - pixel) <= 1,
                    $"10 {scale:P0} hops must drift at most 1 px, {dip} became {value}");

                // The DIP value is monitor-independent: preserving it means the
                // physical size at 100% stays the original value.
                var preserved = ScreenGeometry.RoundPixel(ScreenGeometry.DipToPixel(dip, 1.0));
                True(Math.Abs(preserved - dip) <= 1,
                    $"a preserved {dip} DIP size must render as {dip} px at 100%, got {preserved}");
            }
        }
        Equal(720.0, ScreenGeometry.RoundPixel(ScreenGeometry.DipToPixel(480.0, 1.5)),
            "480 DIP at 150% must be exactly 720 px");
        Equal(480.0, ScreenGeometry.RoundPixel(ScreenGeometry.DipToPixel(480.0, 1.0)),
            "back at 100% the preserved 480 DIP minimum width must be exactly 480 px");
    }

    /// <summary>
    /// Pure replay of the E3-F14 evidence: the main window came home from the
    /// 150% monitor as 747x560 (1120x760 shrunk by 1/1.5). The restored Normal
    /// rect must be the STABLE DIP size at the current position, clamped into
    /// the target monitor's work area — never the polluted transition size.
    /// </summary>
    public static void MainWindowF14StableSizeSurvivesDpiRoundtrip()
    {
        var stable = new Size(1120, 760);
        var minSize = new Size(560, 560);
        var workArea = new Rect(0, 0, 1920, 1040);

        // F14 exactly: window returns at (200,200) polluted to 747x560.
        var restored = MainWindow.RestoredNormalRect(
            new Rect(200, 200, 747, 560), stable, workArea, minSize);
        Equal(200.0, restored.Left);
        Equal(200.0, restored.Top);
        Equal(1120.0, restored.Width, "the stable DIP width must win over the polluted 747");
        Equal(760.0, restored.Height, "the stable DIP height must win over the polluted 560");

        // A window parked off-screen is pulled back inside the target work area.
        var pulledBack = MainWindow.RestoredNormalRect(
            new Rect(-5000, -5000, 747, 560), stable, workArea, minSize);
        True(pulledBack.Left >= workArea.Left && pulledBack.Top >= workArea.Top,
            "the restore must re-clamp into the target monitor's work area");
        Equal(1120.0, pulledBack.Width);
        Equal(760.0, pulledBack.Height);

        // A monitor that cannot fit the stable size caps it (visibility wins).
        var capped = MainWindow.RestoredNormalRect(
            new Rect(0, 0, 747, 560), stable, new Rect(0, 0, 800, 600), minSize);
        True(capped.Width <= 800 && capped.Height <= 600,
            "a smaller work area must cap the restored size instead of hiding the edges");
    }

    /// <summary>Pure generation/flag rules of the stable-size tracker.</summary>
    public static void StableNormalSizeTrackerRejectsPollution()
    {
        var tracker = new StableNormalSizeTracker(new Size(1120, 760));

        // Maximized/minimized phases are never observations.
        True(!tracker.TryObserveResize(new Size(800, 600), isNormalState: false),
            "a maximized/minimized resize must never rewrite the stable normal size");
        Equal(1120.0, tracker.StableSize.Width);

        // While a DPI transition is open, its resizes are ignored.
        tracker.BeginDpiTransition();
        True(tracker.InDpiTransition, "BeginDpiTransition must open the transition");
        True(!tracker.TryObserveResize(new Size(747, 560), isNormalState: true),
            "a transition-driven resize must never pollute the stable size");
        Equal(760.0, tracker.StableSize.Height);

        // Closing the transition yields the stable size for the restore.
        Equal(760.0, tracker.EndDpiTransition().Height);
        True(!tracker.InDpiTransition, "EndDpiTransition must close the transition");

        // After the settle, real user resizes are honoured again.
        True(tracker.TryObserveResize(new Size(900, 650), isNormalState: true),
            "a settled Normal-state resize must be observed");
        Equal(900.0, tracker.StableSize.Width);
    }

    /// <summary>
    /// STA replay against the real MainWindow (no second monitor needed):
    /// OnDpiChanged opens the transition, transition-driven SizeChanged events
    /// cannot pollute the stable DIP size, the ApplicationIdle restore puts
    /// the stable size back, and only post-settle resizes update it.
    /// </summary>
    public static void MainWindowDpiTransitionGuardsStableSize()
    {
        EnsureApp();
        var main = new MainWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
        try
        {
            // Seed assertions run on the constructed window, before Show():
            // on a work area narrower than the 1120 DIP design size the
            // first-show clamp is a legitimate Normal-state observation, so
            // the XAML seed is only guaranteed pre-layout.
            var tracker = GetPrivate<StableNormalSizeTracker>(main, "_normalSizeTracker");
            Equal(1120.0, tracker.StableSize.Width, "the stable size seeds from the XAML width");
            Equal(760.0, tracker.StableSize.Height, "the stable size seeds from the XAML height");

            main.Show();
            PumpUntil(Task.CompletedTask);

            // A settled user resize outside any transition updates the stable
            // size. 900 DIP stays clear of the minimum width and of every
            // runner's work area (the 1120 design width would clamp there).
            main.Width = 900;
            PumpUntil(Task.CompletedTask);
            PumpUntil(Task.CompletedTask);
            Equal(900.0, tracker.StableSize.Width, "a settled Normal-state resize must be observed");

            // WM_DPICHANGED: the transition opens.
            InvokePrivate(main, "OnDpiChanged",
                new System.Windows.DpiScale(1.0, 1.0), new System.Windows.DpiScale(1.5, 1.5));
            True(tracker.InDpiTransition, "OnDpiChanged must open the stable-size transition");

            // Transition-driven resize: must NOT become the new stable size.
            main.Width = 1120;
            PumpUntil(Task.CompletedTask);
            True(tracker.InDpiTransition, "the transition stays open until the idle restore");
            Equal(900.0, tracker.StableSize.Width, "the polluted transition size must not win");

            // Idle restore: the stable DIP size is applied, transition closes.
            PumpIdle();
            True(!tracker.InDpiTransition, "the idle restore must close the transition");
            Equal(900.0, tracker.StableSize.Width);
            True(Math.Abs(main.Width - 900) <= 0.5,
                $"the restore must put the stable DIP width back, got {main.Width:F1}");

            // Post-settle user resizes are observations again.
            main.Width = 747;
            PumpUntil(Task.CompletedTask);
            PumpUntil(Task.CompletedTask);
            // 150% DPI snaps 747 DIP (1120.5 px) to the next physical pixel,
            // which comes back as 747.333 DIP. The resize was still observed.
            True(Math.Abs(tracker.StableSize.Width - 747) < 1,
                $"after the settle a user resize is honoured again, got {tracker.StableSize.Width}");
        }
        finally
        {
            main.AllowClose = true;
            try { main.Close(); } catch { }
        }
    }

    /// <summary>
    /// STA replay against the real quick search window: OnDpiChanged must
    /// settle EXACTLY ONCE via the ApplicationIdle fence (no mid-transition
    /// repositioning), and the settle must keep the 480 DIP minimum width.
    /// </summary>
    public static void QuickSearchDpiTransitionSettlesOnceAtMinWidth()
    {
        EnsureApp();
        var quickSearch = new QuickSearchWindow(IsolatedHistory, IsolatedVocabulary);
        try
        {
            quickSearch.Show();
            PumpUntil(Task.CompletedTask);
            Equal(480.0, quickSearch.MinWidth, "the 480 DIP minimum width contract must hold");
            True(quickSearch.ActualWidth >= 480 - 0.5,
                $"the window must start at or above the minimum width, got {quickSearch.ActualWidth:F1}");
            var widthBefore = quickSearch.ActualWidth;

            InvokePrivate(quickSearch, "OnDpiChanged",
                new System.Windows.DpiScale(1.0, 1.0), new System.Windows.DpiScale(1.5, 1.5));
            var generation = GetPrivate<long>(quickSearch, "_dpiGeneration");
            True(generation != GetPrivate<long>(quickSearch, "_settledDpiGeneration"),
                "right after OnDpiChanged the transition must be open (idle reposition pending)");

            PumpIdle();
            Equal(generation, GetPrivate<long>(quickSearch, "_settledDpiGeneration"),
                "the idle reposition must settle the transition exactly once");
            True(quickSearch.ActualWidth >= widthBefore - 0.5,
                $"the settle must keep the window width (no 480→720 DIP pollution), got {quickSearch.ActualWidth:F1}");
        }
        finally
        {
            quickSearch.ForceClose = true;
            try { quickSearch.Close(); } catch { }
        }
    }

    /// <summary>
    /// Guards the model ComboBox against template regressions: DropDownScrollViewer must
    /// be named for WPF wheel/arrow support, CanContentScroll must be true for virtualization,
    /// and the popup must exist.
    /// </summary>
    public static void ProviderEditorModelComboBoxWheelAndPopupInvariants()
    {
        EnsureApp();
        var section = new ServicesSection();
        section.LoadProfileIntoForm(new ProviderProfile
        {
            Id = "alignment-probe",
            Name = "自定义引擎",
            ApiBaseUrl = "https://example.invalid/v1",
            TextEndpoint = "/chat/completions",
            TextModel = "model-probe",
            CredentialTarget = "PopGlot/tests/alignment-probe",
        });
        InvokePrivate(section, "ShowEditorForm", false);
        var hostWindow = new Window
        {
            Width = 800,
            Height = 600,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = section,
        };
        hostWindow.Show();
        section.Measure(new Size(800, 600));
        section.Arrange(new Rect(0, 0, 800, 600));
        section.UpdateLayout();

        // The watermark and real TextBoxView used to apply Padding through
        // different layout paths (12 DIP vs 24 DIP), which is visible as a
        // jumping insertion point when typing begins.
        var nameField = section.ServiceNameTextBox;
        nameField.Text = string.Empty;
        nameField.ApplyTemplate();
        section.UpdateLayout();
        var placeholder = nameField.Template.FindName("Placeholder", nameField) as FrameworkElement;
        True(placeholder != null, "FormTextBox must expose its placeholder for alignment checks");
        var placeholderX = placeholder!.TranslatePoint(new Point(0, 0), nameField).X;
        nameField.Text = "自定义引擎";
        section.UpdateLayout();
        var contentHost = nameField.Template.FindName("PART_ContentHost", nameField) as ScrollViewer;
        var textView = contentHost?.Content as FrameworkElement;
        True(textView != null, "FormTextBox must expose the live text view for alignment checks");
        var textX = textView!.TranslatePoint(new Point(0, 0), nameField).X;
        // WPF's internal TextBoxView reserves a 2 DIP caret/glyph bearing;
        // the TextBlock glyph has the matching font bearing even though its
        // element starts at the raw inset. The old defect was 15 DIP apart.
        True(Math.Abs(placeholderX - textX) <= 2.1,
            $"placeholder glyph and typed caret must share one visual x origin; placeholder={placeholderX:F1}, text={textX:F1}");

        var keyField = section.ApiKeyPasswordBox;
        keyField.Clear();
        keyField.ApplyTemplate();
        section.UpdateLayout();
        var keyPlaceholder = keyField.Template.FindName("Placeholder", keyField) as FrameworkElement;
        var keyHost = keyField.Template.FindName("PART_ContentHost", keyField) as ScrollViewer;
        True(keyPlaceholder != null && keyHost?.Content is FrameworkElement,
            "FormPasswordBox must expose both placeholder and secure text view");
        var keyPlaceholderX = keyPlaceholder!.TranslatePoint(new Point(0, 0), keyField).X;
        keyField.Password = "probe-secret";
        section.UpdateLayout();
        var secureTextX = ((FrameworkElement)keyHost!.Content).TranslatePoint(new Point(0, 0), keyField).X;
        True(Math.Abs(keyPlaceholderX - secureTextX) <= 2.1,
            $"password placeholder and typed caret must share one visual x origin; placeholder={keyPlaceholderX:F1}, text={secureTextX:F1}");
        InvokePrivate(section, "UpdateCredentialGating");
        Equal(Visibility.Collapsed, section.ApiKeyStateText.Visibility,
            "credential state must not add a heavy second line below the field");
        keyField.Clear();

        Ui.SetPlaceholder(keyField, "••••••••••••  已保存");
        Ui.SetIsCredentialMask(keyField, true);
        section.UpdateLayout();
        Equal("••••••••••••  已保存", ((TextBlock)keyPlaceholder).Text,
            "the control helper must keep a stored credential visibly distinct from an empty field");
        Equal((Brush)Application.Current.Resources["TextPrimaryBrush"], ((TextBlock)keyPlaceholder).Foreground,
            "the stored credential mask must use primary text colour instead of placeholder grey");
        Ui.SetIsCredentialMask(keyField, false);

        InvokePrivate(section, "UpdateCredentialGating");
        Equal(true, section.TestConnectionButton.IsEnabled,
            "validation must remain clickable so an incomplete configuration gets an inline explanation");
        section.TestConnectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(Visibility.Visible, section.TestStatusPanel.Visibility,
            "clicking validation with an incomplete configuration must render inline feedback");
        True(section.TestSummaryText.Text.Contains("API Key", StringComparison.Ordinal),
            "the inline validation result must name the missing credential");

        var textCombo = section.TextModelCombo;
        ArgumentNullException.ThrowIfNull(textCombo);
        Equal(true, ScrollViewer.GetCanContentScroll(textCombo), "ComboBox must enable CanContentScroll for virtualized scrolling");

        textCombo.ApplyTemplate();
        var popup = textCombo.Template?.FindName("PART_Popup", textCombo) as System.Windows.Controls.Primitives.Popup;
        True(popup != null, "PART_Popup must exist on TextModelCombo");
        var chevron = textCombo.Template?.FindName("Chevron", textCombo) as FrameworkElement;
        var toggle = textCombo.Template?.FindName("Toggle", textCombo) as FrameworkElement;
        Equal(Visibility.Collapsed, chevron?.Visibility,
            "an empty model list must not advertise a dropdown affordance");
        Equal(false, toggle?.IsHitTestVisible,
            "an empty model list must not open a blank popup when clicked");

        textCombo.ItemsSource = Enumerable.Range(1, 40).Select(index => $"provider-model-{index:00}").ToList();
        section.UpdateLayout();
        Equal(Visibility.Visible, chevron?.Visibility,
            "a fetched model list must advertise its dropdown affordance");
        Equal(true, toggle?.IsHitTestVisible,
            "a fetched model list must remain interactive");

        textCombo.IsDropDownOpen = true;
        popup!.UpdateLayout();

        var scrollViewer = textCombo.Template?.FindName("DropDownScrollViewer", textCombo) as ScrollViewer;
        True(scrollViewer != null, "DropDownScrollViewer must exist in ComboBox template for WPF wheel/arrow navigation");
        scrollViewer!.LineDown();
        scrollViewer.LineDown();
        Equal(true, textCombo.IsDropDownOpen,
            "scrolling inside the fetched model list must not close the dropdown");
        textCombo.SelectedIndex = 24;
        Equal("provider-model-25", textCombo.SelectedItem as string,
            "models below the initial viewport must remain selectable");
        textCombo.IsDropDownOpen = false;
        hostWindow.Close();
    }

    /// <summary>
    /// Guard that shared text-vision model mirrors correctly and unlocking behaves as expected.
    /// </summary>
    public static void ProviderEditorSharedModelSyncAndUnlockingInvariants()
    {
        EnsureApp();
        var section = new ServicesSection();
        section.Measure(new Size(800, 600));
        section.Arrange(new Rect(0, 0, 800, 600));
        section.UpdateLayout();

        // 1. Initial shared state
        section.UseTextModelForVisionCheckBox.IsChecked = true;
        section.TextModelCombo.Text = "gpt-4o";
        Equal("gpt-4o", section.VisionModelCombo.Text, "When shared, vision model should mirror text model");
        Equal(Visibility.Visible, section.SharedModelHintText.Visibility, "SharedModelHintText should be visible when shared");

        // 2. Unchecking shared allows independent model
        section.UseTextModelForVisionCheckBox.IsChecked = false;
        Equal(Visibility.Collapsed, section.SharedModelHintText.Visibility, "SharedModelHintText should be collapsed when independent");
        section.VisionModelCombo.Text = "gemini-1.5-pro";
        section.TextModelCombo.Text = "claude-3-5-sonnet";
        Equal("gemini-1.5-pro", section.VisionModelCombo.Text, "When independent, vision model should not be overwritten");

        // 3. Virtualization flags on ComboBoxes
        Equal(true, VirtualizingPanel.GetIsVirtualizing(section.TextModelCombo), "IsVirtualizing should be true");
        Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(section.TextModelCombo), "VirtualizationMode should be Recycling");
    }

    /// <summary>
    /// C25: the offline help viewer must render the packaged help/ articles
    /// locally, guard the catalog against doc drift, and land on the index.
    /// Never network: content comes from BaseDirectory/help only.
    /// </summary>
    public static void HelpWindowRendersPackagedArticles()
    {
        EnsureApp();
        var root = HelpWindow.ResolveHelpRoot();
        True(root is not null, "the help root must resolve in the test host (packaged help/ or repo docs/help)");
        True(Path.GetFullPath(root!).EndsWith("help", StringComparison.Ordinal),
            $"the resolved help root must be the help directory itself; got '{root}'");

        foreach (var (_, file) in HelpWindow.Articles)
        {
            var path = HelpWindow.ResolveArticlePath(file);
            True(path is not null && File.Exists(path),
                $"help article must ship on disk next to the binary: {file}");
        }

        var help = new HelpWindow();
        try
        {
            var title = (TextBlock)help.FindName("ArticleTitle")!;
            var viewer = (RichTextBox)help.FindName("ArticleViewer")!;
            var list = (ListBox)help.FindName("ArticleList")!;
            var meta = (TextBlock)help.FindName("ArticleMeta")!;

            Equal(6, HelpWindow.Articles.Length, "the help catalog stays at its six documented articles");
            True(ArticleListAllEnabled(list.Items), "every packaged article must be enabled when its file ships");
            Equal("帮助首页", title.Text, "the window must open on the index article");
            True(viewer.Document.Blocks.Count > 0, "the index article must render non-empty content blocks");
            True(meta.Text!.Contains("不需要联网"), "the meta line must keep the offline promise");
            True(help.FindName("MinimizeBtn") is Button && help.FindName("MaximizeBtn") is Button,
                "help must use the same complete caption controls as the other desktop windows");
            True(viewer.Parent is Grid,
                "the help reader must sit directly in the content grid without a second overlapping card frame");

            list.SelectedIndex = HelpWindow.Articles.Length - 1;
            help.UpdateLayout();
            Equal("故障排查", title.Text, "nav selection must load the matching article");
            True(viewer.Document.Blocks.Count > 0, "the troubleshooting article must render non-empty content blocks");

            // 仓库内 .md 链接语法必须在渲染层剥壳：正文里不允许再出现
            // 「](xxx.md)」这种 GitHub 语法残留。
            True(!HelpWindow.StripLocalMarkdownLinks(
                    File.ReadAllText(HelpWindow.ResolveArticlePath("index.md")!))
                .Contains("]("),
                "index article display text must not leak raw markdown link syntax");
        }
        finally
        {
            help.Close();
        }

        static bool ArticleListAllEnabled(System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item is ListBoxItem { IsEnabled: false })
                {
                    return false;
                }
            }
            return true;
        }
    }
}
