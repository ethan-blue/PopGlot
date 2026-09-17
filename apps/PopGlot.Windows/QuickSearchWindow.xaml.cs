using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

public partial class QuickSearchWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore _vocabulary;
    private readonly TranslationCoordinator _coordinator;
    private readonly EventHandler _themeChangedHandler;
    private readonly Action? _openSettings;
    private readonly QuickSearchState _state = new();
    private readonly DispatcherTimer _debounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };
    private long _searchVersion;
    private CancellationTokenSource? _cts;
    private bool _isClosed;
    // E3: true when the Escape currently travelling down the tunnel belonged
    // to a live IME composition on the search box. Window_KeyDown is a
    // BUBBLING KeyDown handler — by the time it runs, the Ui composition
    // tracker has already reset the IsComposing flag in the preview pass —
    // so this is the only reliable place to sample the fact and let the
    // window handler spare the composition.
    private bool _escapeOwnedByComposition;
    // 免费引擎预提示：在真实进展（流式文本、终态）出现前，不允许被
    // 「正在生成…」等准备态瞬间覆盖。快速搜索只有文字线路，不涉及
    // 截图视觉直译提示。
    private string? _pendingStyleNotice;

    /// <summary>
    /// C05: set by the host when the window must really die (app exit).
    /// Without it, close requests cancel the running request and hide —
    /// the session and any partial result survive.
    /// </summary>
    private Point? _initialCenterPointPixels;
    internal bool ForceClose { get; set; }

    internal QuickSearchWindow(HistoryStore history, VocabularyStore vocabulary, Action? openSettings = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _openSettings = openSettings;
        _coordinator = new TranslationCoordinator(_history, _vocabulary);
        InitializeComponent();
        // E3: the sampler must register BEFORE the Ui composition tracker —
        // it reads IsComposing, which the tracker resets on Escape during
        // the same tunnelling pass.
        SearchBox.PreviewKeyDown += OnSearchBoxPreviewKeyDownSample;
        Ui.AttachCompositionTracker(SearchBox);

        _themeChangedHandler = (_, _) =>
        {
            if (Dispatcher.CheckAccess())
            {
                ThemeService.ApplyWindowChrome(this);
            }
            else
            {
                _ = Dispatcher.BeginInvoke(() => ThemeService.ApplyWindowChrome(this));
            }
        };
        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += _themeChangedHandler;

        TtsService.SpeakingStateChanged += OnTtsSpeakingStateChanged;

        SourceInitialized += (_, _) => CenterOnCurrentMonitor();
        Loaded += (_, _) =>
        {
            CenterOnCurrentMonitor();
            SearchBox.Focus();
            RefreshLangBadge();
            SyncUiWithState();
            RefreshStyleSelector();
        };
        // The window hides instead of closing; on every re-show the engine
        // (and therefore style support) may have changed.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                // The persisted language pair may have changed on another
                // surface while this window was hidden — the badge must not
                // go stale on a re-show.
                RefreshLangBadge();
                RefreshStyleSelector();
            }
        };
        SizeChanged += (_, _) => ClampToWorkArea();

        Closed += (_, _) => OnClosedCleanup();
    }

    private void CenterOnCurrentMonitor()
    {
        var cursor = ScreenGeometry.CursorPixels();
        var workArea = ScreenGeometry.WorkAreaForPixel(cursor);
        var scale = ScreenGeometry.ScaleOf(this);

        var workAreaHeightDip = workArea.Height / (scale.Y > 0 ? scale.Y : 1.0);
        MaxHeight = Math.Min(600, Math.Max(200, workAreaHeightDip - 40));

        var widthPixels = (ActualWidth > 0 ? ActualWidth : Width) * scale.X;
        var heightPixels = (ActualHeight > 0 ? ActualHeight : 100) * scale.Y;

        var x = workArea.Left + Math.Max(0, (workArea.Width - widthPixels) / 2);
        var y = workArea.Top + Math.Max(0, (workArea.Height - heightPixels) / 2);
        _initialCenterPointPixels = new Point(x, y);
        ScreenGeometry.MoveToPixels(this, new Point(x, y));
    }

    private void ClampToWorkArea()
    {
        if (_isClosed || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0) return;
        var cursor = ScreenGeometry.CursorPixels();
        var workArea = ScreenGeometry.WorkAreaForPixel(cursor);
        var scale = ScreenGeometry.ScaleOf(this);

        var widthPixels = ActualWidth * scale.X;
        var heightPixels = ActualHeight * scale.Y;

        var x = _initialCenterPointPixels?.X ?? (workArea.Left + Math.Max(0, (workArea.Width - widthPixels) / 2));
        var y = _initialCenterPointPixels?.Y ?? (workArea.Top + Math.Max(0, (workArea.Height - heightPixels) / 2));

        if (y + heightPixels > workArea.Bottom - 20)
        {
            y = Math.Max(workArea.Top + 20, workArea.Bottom - heightPixels - 20);
        }

        ScreenGeometry.MoveToPixels(this, new Point(x, y));
    }

    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    internal QuickSearchState State => _state;
    internal TextBox StreamBox => ResultStreamBox;
    internal RichTextBox RichBox => ResultRichBox;
    internal TextBlock FooterStatusBlock => FooterStatus;
    internal TextBlock FooterHintsBlock => FooterHints;
    internal TextBlock StreamIndicatorBlock => StreamIndicator;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isClosed) return;

        var text = SearchBox.Text;
        var version = Interlocked.Increment(ref _searchVersion);
        _debounceTimer.Stop();

        // If input is cleared, update state immediately without debounce lag
        if (string.IsNullOrWhiteSpace(text))
        {
            CancelRunningRequest();
            if (text.Trim() != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        // PERF-LIST-03: 150ms DispatcherTimer debounce to avoid per-keystroke churn.
        // Stale query results are discarded via version check.
        CancelRunningRequest();

        EventHandler? handler = null;
        handler = (s, ev) =>
        {
            _debounceTimer.Stop();
            _debounceTimer.Tick -= handler;

            if (_isClosed || Volatile.Read(ref _searchVersion) != version)
            {
                return;
            }

            if (text.Trim() != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
        };

        _debounceTimer.Tick += handler;
        _debounceTimer.Start();
    }

    /// <summary>
    /// E3: samples whether this Escape belongs to a live IME composition on
    /// the search box, and clears the residual composition flag safely (some
    /// IMEs tear the composition down without a composition-end event). The
    /// key itself is never marked handled: the IME still needs it to cancel.
    /// The isolation suite drives the WPF-level events only; real Microsoft
    /// Pinyin / Sogou behaviour stays an explicit E3 manual verification TODO.
    /// </summary>
    private void OnSearchBoxPreviewKeyDownSample(object sender, KeyEventArgs e)
    {
        _escapeOwnedByComposition =
            (e.Key == Key.Escape || e.ImeProcessedKey == Key.Escape) &&
            Ui.GetIsComposing(SearchBox);
        if (_escapeOwnedByComposition)
        {
            Ui.SetIsComposing(SearchBox, false);
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return; // Shift+Enter inserts newline
            }
            // IME composition confirm: the composition owns this Enter.
            if (Ui.IsImeComposing(SearchBox, e))
            {
                return;
            }
            e.Handled = true;
            _debounceTimer.Stop();
            _state.OnQueryTextChanged(SearchBox.Text);
            await PerformTranslateAsync();
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+P: speak. Ctrl+R stays reserved for retry semantics
            // elsewhere (translation panel), one key one meaning.
            e.Handled = true;
            SpeakCurrent();
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            Copy_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanStar)
        {
            e.Handled = true;
            Star_Click(this, new RoutedEventArgs());
        }
    }

    private async Task PerformTranslateAsync()
    {
        if (_isClosed) return;

        var text = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _state.StartNewSearch(text);
        var epoch = _state.CurrentEpoch;
        SyncUiWithState();

        // Honest pre-flight notice when the route cannot be proven to honour
        // the active style: stated BEFORE the request goes out, never blocking
        // it and never cancelling anything already in flight. The pending
        // notice rides on top of the preparing statuses in SyncUiWithState
        // until real progress lands.
        _pendingStyleNotice = null;
        if (TranslationStyleMenu.PreTranslateNotice() is { } styleNotice)
        {
            _pendingStyleNotice = styleNotice;
            FooterStatus.Text = styleNotice;
            RefreshStyleSelector();
        }

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (_isClosed) return;
            if (_state.OnStreamUpdate(update, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        });

        try
        {
            var settings = CoreBridge.GetSettings();
            var sourceLang = settings.SourceLanguage ?? LanguageCatalog.Auto;
            var targetLang = settings.TargetLanguage ?? "zh-CN";

            var session = await _coordinator.TranslateTextAsync(
                text,
                sourceLang,
                targetLang,
                TranslationInputSource.QuickSearch,
                token,
                onStageChanged: stage =>
                {
                    if (_isClosed) return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosed) return;
                        if (_state.OnStageChanged(stage, epoch, SearchBox.Text.Trim()))
                        {
                            SyncUiWithState();
                        }
                    });
                },
                progress: progress,
                epoch: epoch);

            if (_isClosed || token.IsCancellationRequested) return;

            if (_state.OnSessionCompleted(session, epoch, SearchBox.Text.Trim()))
            {
                if (_state.IsRichBoxVisible && !string.IsNullOrWhiteSpace(_state.FinalRenderedText))
                {
                    try
                    {
                        MarkdownPresenter.RenderToFlowDocument(
                            ResultRichBox.Document,
                            _state.FinalRenderedText,
                            Application.Current?.Resources ?? Resources,
                            resultActionsEnabled: _state.CanCopy);
                    }
                    catch
                    {
                        ResultRichBox.Visibility = Visibility.Collapsed;
                        ResultStreamBox.Visibility = Visibility.Visible;
                    }
                }
                SyncUiWithState();

                // Durable post-hoc honesty: when the free engine actually ran,
                // the selected style did not apply. Decided by the TYPED
                // executor, never by matching the PipelineLabel display string.
                if (session.TextExecutor == TranslationTextExecutor.FreeEngine &&
                    TranslationStyleMenu.TryGetActiveTemplate() is { } activeStyle &&
                    activeStyle.Id != TranslationStyleMenu.FaithfulTemplateId)
                {
                    FooterStatus.Text =
                        $"已由内置免费引擎完成：{TranslationStyleMenu.FreeEngineToolTip}，所选文字翻译风格未应用。";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_isClosed) return;
            if (_state.OnCancelled(epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            if (_state.OnException(ex, epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
    }

    private void SyncUiWithState()
    {
        if (_isClosed) return;

        ResultContainer.Visibility = _state.IsResultVisible ? Visibility.Visible : Visibility.Collapsed;
        ResultStreamBox.Visibility = _state.IsStreamLayerVisible ? Visibility.Visible : Visibility.Collapsed;
        if (_state.IsStreamLayerVisible)
        {
            // Stick-to-bottom: follow the stream only while the reader sits at
            // the bottom, so scrolling up to re-read is never overridden.
            var stickToBottom = Ui.IsScrolledToBottom(Ui.FindScrollViewer(ResultStreamBox));
            // Append-only when the accumulated text simply grew: wholesale
            // reassignment re-layouts the whole box on every pump tick and
            // makes the window edge flicker.
            var accumulated = _state.AccumulatedText;
            if (accumulated.StartsWith(ResultStreamBox.Text, StringComparison.Ordinal))
            {
                ResultStreamBox.AppendText(accumulated[ResultStreamBox.Text.Length..]);
            }
            else
            {
                ResultStreamBox.Text = accumulated;
            }
            if (stickToBottom)
            {
                ResultStreamBox.ScrollToEnd();
            }
        }

        ResultRichBox.Visibility = _state.IsRichBoxVisible ? Visibility.Visible : Visibility.Collapsed;
        StreamIndicator.Visibility = _state.IsStreamIndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        IncompleteBadge.Visibility = _state.IsIncompleteBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.Visibility = _state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

        CopyButton.IsEnabled = _state.CanCopy;
        SpeakButton.IsEnabled = _state.CanSpeak;
        StarButton.IsEnabled = _state.CanStar;

        // 免费引擎预提示在其待决期间压过「正在生成…」这类准备态：一旦真实
        // 文本开始到达或终态（完成/失败/取消）落地，立即让位给真实状态。
        if (_pendingStyleNotice is not null)
        {
            if (string.IsNullOrEmpty(_state.AccumulatedText) &&
                _state.Stage is QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing)
            {
                FooterStatus.Text = _pendingStyleNotice;
            }
            else
            {
                _pendingStyleNotice = null;
                FooterStatus.Text = _state.StatusText;
            }
        }
        else
        {
            FooterStatus.Text = _state.StatusText;
        }

        // 动态快捷键提示：只承诺当前阶段真正可用的操作（流式仅 Esc 取消、
        // 完成含复制/朗读/收藏、partial 只含复制）。
        FooterHints.Text = _state.HintsText;

        if (_state.Stage == QuickSearchUiStage.Failed)

        {

            FooterStatus.Foreground = (Brush)FindResource("DangerBrush");

        }

        else if (_state.Stage is QuickSearchUiStage.Cancelled or QuickSearchUiStage.Partial)

        {

            FooterStatus.Foreground = (Brush)FindResource("WarningBrush");

        }

        else

        {

            FooterStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");

        }



        if (_state.IsIncompleteBadgeVisible)

        {

            IncompleteBadge.Text = _state.Stage == QuickSearchUiStage.Cancelled ? "已取消" : "未完成";

            IncompleteBadge.Foreground = (Brush)FindResource("WarningBrush");

        }



        if (!string.IsNullOrWhiteSpace(_state.Phonetic))

        {

            PhoneticLabel.Text = $"[{_state.Phonetic}]";

            PhoneticLabel.Visibility = Visibility.Visible;

        }

        else

        {

            PhoneticLabel.Visibility = Visibility.Collapsed;

        }



        if (!string.IsNullOrWhiteSpace(_state.Explanation))
        {
            ExplanationLabel.Text = _state.Explanation;
            ExplanationCard.Visibility = Visibility.Visible;
        }
        else
        {
            ExplanationCard.Visibility = Visibility.Collapsed;
        }

        if (_state.Stage == QuickSearchUiStage.Failed && !string.IsNullOrWhiteSpace(_state.ErrorMessage))
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorTitle.Text = TranslationPanelWindow.FriendlyError(_state.ErrorMessage);
            ErrorDetail.Text = _state.ErrorMessage;
        }
        else
        {
            ErrorCard.Visibility = Visibility.Collapsed;
        }

        UpdateStarButton();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ================= Language badge =================

    /// <summary>
    /// Repaints the header badge from the persisted pair. Called on Loaded
    /// and on every re-show: quick search keeps no language dropdown of its
    /// own, so the badge must always mirror what the next request will use.
    /// Degrades quietly when local state is unreadable.
    /// </summary>
    private void RefreshLangBadge()
    {
        if (_isClosed) return;
        try
        {
            var settings = CoreBridge.GetSettings();
            LangBadge.Text =
                $"{LanguageCatalog.DisplayName(settings.SourceLanguage)} → " +
                $"{LanguageCatalog.DisplayName(settings.TargetLanguage)}";
        }
        catch (Exception)
        {
            // Headless/offline contexts keep the last badge.
        }
    }

    // ================= Global translation-style selector =================

    /// <summary>
    /// Repaints the shared style selector from local state. The built-in free
    /// engine disables the selector with the honest wording; an unreadable
    /// local state keeps it visible with an honest caveat instead.
    /// </summary>
    private void RefreshStyleSelector()
    {
        if (_isClosed) return;
        try
        {
            TranslationStyleMenu.ApplyTo(StyleSelectorButton, TranslationStyleMenu.SupportedToolTip);
            StyleSelectorLabel.Text = TranslationStyleMenu.ActiveLabel();
        }
        catch (Exception)
        {
            // The selector is additive UI; never break quick search over it.
        }
    }

    private void StyleSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        // Re-probe at open time so the menu never claims support the next
        // request would not have.
        RefreshStyleSelector();
        if (TranslationStyleMenu.ProbeNextTextRoute() == TranslationStyleSupport.FreeEngine)
        {
            FooterStatus.Text = $"内置免费引擎{TranslationStyleMenu.FreeEngineToolTip}。";
            return;
        }
        var menu = TranslationStyleMenu.Build(
            styleChosen: template => _ = ChooseStyleAsync(template),
            reportStatus: message => FooterStatus.Text = message,
            // 管理提示词 reuses the existing open-settings callback chain.
            managePrompts: OpenSettings);
        if (menu is null)
        {
            return;
        }
        TranslationStyleMenu.Show(button, menu);
    }

    private async Task ChooseStyleAsync(PromptTemplateDto template)
    {
        // Optimistic label; repainted from the authoritative state if the
        // persist fails. Nothing is re-translated and nothing in flight is
        // cancelled — the switch lands with the next request only.
        StyleSelectorLabel.Text = TranslationStyleMenu.ShortLabel(template);
        var applied = await TranslationStyleMenu.SwitchActiveTemplateAsync(
            template.Id,
            message => FooterStatus.Text = message);
        if (!applied)
        {
            RefreshStyleSelector();
        }
    }

    internal void OpenSettings()
    {
        if (_openSettings is not null)
        {
            _openSettings.Invoke();
            return;
        }

        if (Application.Current is not null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is MainWindow main && main.OpenSettings is not null)
                {
                    main.OpenSettings.Invoke();
                    return;
                }
            }

            try
            {
                var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault()
                    ?? new SettingsWindow(ShellSettingsStore.Load(), _history, _vocabulary);
                settingsWindow.Show();
                settingsWindow.Activate();
            }
            catch { }
        }
    }



    private void SpeakCurrent()

    {

        if (_isClosed || !_state.CanSpeak) return;



        var textToSpeak = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : !string.IsNullOrWhiteSpace(_state.AccumulatedText)

                ? _state.AccumulatedText

                : SearchBox.Text.Trim();



        if (!string.IsNullOrWhiteSpace(textToSpeak))

        {

            var clean = MarkdownPresenter.ToPlainText(textToSpeak);

            if (TtsService.IsSpeaking)

            {

                TtsService.Stop();

            }

            else

            {

                TtsService.Speak(clean, CoreBridge.GetSettings().TargetLanguage);

            }

        }

    }



    private void Speak_Click(object sender, RoutedEventArgs e) => SpeakCurrent();



    private async void Copy_Click(object sender, RoutedEventArgs e)

    {

        if (_isClosed || !_state.CanCopy) return;



        var textToCopy = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : _state.AccumulatedText;



        if (!string.IsNullOrWhiteSpace(textToCopy))

        {

            var clean = MarkdownPresenter.ToPlainText(textToCopy);

            // Hardened write: a raw Clipboard.SetText on the UI thread freezes

            // the whole window while another app holds the clipboard open.

            FooterStatus.Text = await PopGlot.Windows.Sections.Helpers.CopyToClipboardAsync(clean)

                ? "已复制译文到剪贴板"

                : "剪贴板被其他应用占用，未复制";

        }

    }



    private void Star_Click(object sender, RoutedEventArgs e)

    {

        if (_isClosed || !_state.CanStar) return;



        var word = SearchBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(word)) return;



        var targetText = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : _state.AccumulatedText;



        if (string.IsNullOrWhiteSpace(targetText)) return;



        var cleanTarget = MarkdownPresenter.ToPlainText(targetText);

        var languageSettings = CoreBridge.GetSettings();

        var result = _vocabulary.ToggleStar(

            word,

            cleanTarget,

            _state.Phonetic ?? "",

            _state.Explanation ?? "",

            languageSettings.SourceLanguage ?? LanguageCatalog.Auto,

            languageSettings.TargetLanguage ?? "zh-CN");



        UpdateStarButton();
        FooterStatus.Text = result.Persisted
            ? (result.Starred ? "已加入生词本" : "已从生词本移除")
            : result.DescribeFailureZh();
    }

    private void OnTtsSpeakingStateChanged(object? sender, bool isSpeaking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isSpeaking)
            {
                SpeakIcon.Fill = (Brush)FindResource("AccentBrush");
                SpeakButton.ToolTip = "停止朗读 (Ctrl+P)";
            }
            else
            {
                SpeakIcon.ClearValue(System.Windows.Shapes.Shape.FillProperty);
                SpeakButton.ToolTip = "朗读译文 (Ctrl+P)";
            }
        });
    }

    private void UpdateStarButton()
    {
        var word = SearchBox.Text.Trim();
        var settings = CoreBridge.GetSettings();
        var starred = !string.IsNullOrWhiteSpace(word) &&
            _vocabulary.IsStarred(
                word,
                settings.SourceLanguage ?? LanguageCatalog.Auto,
                settings.TargetLanguage ?? "zh-CN");
        StarIcon.Fill = (Brush)FindResource(starred ? "AccentBrush" : "TextSecondaryBrush");
        StarButton.Background = starred
            ? (Brush)FindResource("AccentSoftBrush")
            : System.Windows.Media.Brushes.Transparent;
        StarButton.ToolTip = starred ? "从生词本移除" : "收藏到生词本";
        // The static XAML name must follow the dynamic state so screen
        // readers announce the action the click will actually perform.
        System.Windows.Automation.AutomationProperties.SetName(StarButton, (string)StarButton.ToolTip);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Cancels the in-flight request WITHOUT destroying the session state, so
    /// the already-streamed partial stays visible (C05 X/Esc semantics).
    /// </summary>
    private void CancelRunningRequest()
    {
        try
        {
            _cts?.Cancel();
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceClose)
        {
            // C05: X/Alt+F4 cancel the running request, keep the partial and
            // hide. Real destruction happens only on app exit.
            e.Cancel = true;
            CancelRunningRequest();
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // E3: a live IME composition owns this Escape — hiding or
            // cancelling the window out from under the composition would
            // strand its residue in the search box. The residue was already
            // cleared safely in the preview pass; leave the key unhandled so
            // the IME can cancel its own composition, and consume the sample
            // so it cannot outlive this key press.
            if (_escapeOwnedByComposition)
            {
                _escapeOwnedByComposition = false;
                return;
            }
            e.Handled = true;
            // C05/A06 Esc ladder: ANY active phase cancels first (the live
            // _cts is the truth, not a UI stage snapshot), keeping the
            // partial; an idle/final window hides with its session.
            if (_cts is { IsCancellationRequested: false })
            {
                CancelRunningRequest();
                return;
            }
            Hide();
        }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanStar)
        {
            e.Handled = true;
            Star_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanSpeak)
        {
            e.Handled = true;
            SpeakCurrent();
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanCopy)

        {

            // Plain Ctrl+C keeps the native selection copy everywhere: the

            // search editor and any selected range in the result boxes are

            // handled by the focused control itself. Copying the FULL

            // translation from the keyboard is Ctrl+Shift+C ONLY — never the

            // bare shortcut.

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)

            {

                return;

            }

            e.Handled = true;

            Copy_Click(this, new RoutedEventArgs());

        }

    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // C05/F07: focus loss never destroys the session any more. A
        // same-process surface (IME, context menu, drop-down) is not a real
        // leave at all; a window with a draft, a running task or a result
        // stays visible; only a fresh, empty window hides.
        if (_isClosed)
        {
            return;
        }
        if (ForegroundBelongsToThisProcess())
        {
            return;
        }
        if (HasContentOrActivity())
        {
            return;
        }
        Hide();
    }

    private bool HasContentOrActivity() =>
        !string.IsNullOrWhiteSpace(SearchBox.Text) ||
        _state.Stage is not QuickSearchUiStage.Idle ||
        _state.IsResultVisible ||
        _state.IsStreamLayerVisible ||
        _state.AccumulatedText.Length > 0;

    private bool ForegroundBelongsToThisProcess()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != 0 && foreground == handle)
        {
            return false;
        }
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    }

    private void StoreSessionSnapshot()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var resultText = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)
            ? _state.FinalRenderedText
            : _state.AccumulatedText;
        if (string.IsNullOrEmpty(query) || _state.Stage == QuickSearchUiStage.Idle)
        {
            return;
        }

        var hasResult = !string.IsNullOrWhiteSpace(resultText);
        var (state, isPartial) = _state.Stage switch
        {
            QuickSearchUiStage.Completed => (TranslationSessionState.Completed, false),
            QuickSearchUiStage.Partial => (TranslationSessionState.Failed, true),
            QuickSearchUiStage.Failed => (TranslationSessionState.Failed, hasResult),
            _ => (TranslationSessionState.Cancelled, hasResult),
        };
        ProviderSettings settings;
        try
        {
            settings = CoreBridge.GetSettings();
        }
        catch
        {
            return;
        }

        var session = StoredSession.Create(
            sessionId: _sessionId,
            origin: SessionOrigin.QuickSearch,
            sourceText: query,
            sourceLang: settings.SourceLanguage ?? LanguageCatalog.Auto,
            targetLang: settings.TargetLanguage ?? "zh-CN",
            engineProfileId: null,
            engineName: null,
            state: state,
            resultText: hasResult ? resultText : null,
            explanationText: _state.Explanation ?? _state.ErrorMessage,
            isPartial: isPartial);
        if (!App.SharedSessionStore.TryStore(session, out var rejection))
        {
            // Called from the close path: no surface remains to show it.
            App.LogSessionStoreRejection(rejection);
        }
    }

    private void OnClosedCleanup()
    {
        if (_isClosed) return;
        _isClosed = true;
        _debounceTimer.Stop();
        StoreSessionSnapshot();
        ThemeService.ThemeChanged -= _themeChangedHandler;
        TtsService.SpeakingStateChanged -= OnTtsSpeakingStateChanged;
        _state.OnClose();
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;
    }
}
