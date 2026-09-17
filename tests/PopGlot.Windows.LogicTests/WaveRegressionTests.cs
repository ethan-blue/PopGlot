using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    /// The star button's accessibility name must follow the dynamic starred
    /// state, so screen readers announce the action the click will actually
    /// perform.
    /// </summary>
    public static void QuickSearchStarAutomationNameFollowsState()
    {
        EnsureApp();
        var vocab = IsolatedVocabulary;
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
            foreach (var word in vocab.GetAll().Where(w => w.Word == "wave_star_word").ToList())
            {
                vocab.Remove(word.Id);
            }
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

            // Park the window near the bottom edge, then grow it — the
            // SizeChanged handler runs the production clamp.
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

    /// <summary>
    /// A CLEAN editor is folded away on close so the DIRTY main form faces
    /// the user with a VISIBLE save bar — not with instructions to press a
    /// hidden button.
    /// </summary>
    public static void DirtyFormCleanEditorCloseShowsSaveBar()
    {
        EnsureApp();
        var window = new SettingsWindow(ShellSettings.Default, IsolatedHistory, IsolatedVocabulary);
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

        // Domain / audience carry BOTH a char and a byte budget.
        True(PromptSection.ValidateDraft("名", "", "", new string('汉', 341), "") is null,
            "341 CJK characters (1023 bytes) fit the 1024-byte domain budget");
        True(PromptSection.ValidateDraft("名", "", "", new string('汉', 342), "") is not null,
            "342 CJK characters (1026 bytes) are rejected");
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
        Equal(2, main.TranslateSection.PaneGrid.ColumnDefinitions.Count,
            "720–959 returns the panes side by side");
        apply.Invoke(main, new object?[] { 960d });
        Equal(2, main.TranslateSection.PaneGrid.ColumnDefinitions.Count,
            ">= 960 keeps the wide dual-column workstation layout");
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

        // 2) The two post-hoc honesty notices branch on the TYPED executor.
        var quickSearch = File.ReadAllText(Path.Combine(appDir, "QuickSearchWindow.xaml.cs"));
        True(quickSearch.Contains("session.TextExecutor == TranslationTextExecutor.FreeEngine", StringComparison.Ordinal),
            "the quick-search free-engine notice must ride the typed executor");
        var translate = File.ReadAllText(Path.Combine(appDir, "Sections", "TranslateSection.xaml.cs"));
        True(translate.Contains("session.TextExecutor == TranslationTextExecutor.FreeEngine", StringComparison.Ordinal),
            "the workbench free-engine notice must ride the typed executor");

        // 3) The coordinator's route matrix sets the typed triple honestly.
        var coordinator = File.ReadAllText(Path.Combine(appDir, "Services", "TranslationCoordinator.cs"));
        True(coordinator.Contains("session.TextExecutor = TranslationTextExecutor.FreeEngine;", StringComparison.Ordinal) &&
             coordinator.Contains("session.PromptSupport = TranslationPromptSupport.NotSupported;", StringComparison.Ordinal),
            "both free routes must record TextExecutor=FreeEngine + PromptSupport=NotSupported");
        True(coordinator.Contains("session.PipelineKind = TranslationPipelineKind.VisionDirect;", StringComparison.Ordinal) &&
             coordinator.Contains("session.PromptSupport = TranslationPromptSupport.NotApplicable;", StringComparison.Ordinal),
            "the vision-direct route must record NotApplicable (no text stage, nothing promised)");
        True(coordinator.Contains("session.PipelineLabel = EngineWording.FreeEngineName;", StringComparison.Ordinal),
            "the free-engine label comes from the single EngineWording source");
        True(coordinator.Contains("TranslationPipelineKind.OcrUserText;", StringComparison.Ordinal) &&
             coordinator.Contains("TranslationPipelineKind.OcrFreeText;", StringComparison.Ordinal),
            "both OCR+text routes carry their own typed kind");

        // 4) The display label and the typed engine agree by construction.
        Equal("内置免费引擎", EngineWording.FreeEngineName,
            "the single wording source keeps the free-engine name");
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
}
