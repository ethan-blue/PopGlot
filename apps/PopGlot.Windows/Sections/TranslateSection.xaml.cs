using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

internal enum TranslateUiPhase
{
    Idle,
    Preparing,
    Streaming,
    Finalizing,
    Completed,
    Partial,
    Failed,
    Cancelled,
}

internal sealed record TranslateUiState(
    long Epoch = 0,
    TranslateUiPhase Phase = TranslateUiPhase.Idle,
    string StreamText = "",
    string FinalText = "",
    string StatusText = "就绪",
    string BadgeText = "",
    string ExplanationText = "",
    bool IsStreamLayerVisible = false,
    bool IsFinalLayerVisible = true,
    bool IsStreamIndicatorVisible = false,
    bool IsProgressVisible = false,
    bool AreResultActionsEnabled = false,
    bool IsTranslateButtonEnabled = true,
    bool IsExplanationVisible = false,
    bool IsPartialIncomplete = false)
{
    public static TranslateUiState Initial => new(
        Epoch: 0,
        Phase: TranslateUiPhase.Idle,
        StreamText: string.Empty,
        FinalText: string.Empty,
        StatusText: "就绪",
        BadgeText: "",
        ExplanationText: string.Empty,
        IsStreamLayerVisible: false,
        IsFinalLayerVisible: true,
        IsStreamIndicatorVisible: false,
        IsProgressVisible: false,
        AreResultActionsEnabled: false,
        IsTranslateButtonEnabled: true,
        IsExplanationVisible: false,
        IsPartialIncomplete: false);
}

internal static class TranslateSectionReducer
{
    public static TranslateUiState StartTranslation(TranslateUiState current, long epoch)
    {
        return current with
        {
            Epoch = epoch,
            Phase = TranslateUiPhase.Preparing,
            StreamText = string.Empty,
            FinalText = string.Empty,
            StatusText = "连接中",
            BadgeText = "",
            ExplanationText = string.Empty,
            IsStreamLayerVisible = false,
            IsFinalLayerVisible = true,
            IsStreamIndicatorVisible = false,
            IsProgressVisible = true,
            AreResultActionsEnabled = false,
            IsTranslateButtonEnabled = false,
            IsExplanationVisible = false,
            IsPartialIncomplete = false,
        };
    }

    public static TranslateUiState ApplyStage(TranslateUiState current, TranslationSessionStage stage, long epoch)
    {
        if (current.Epoch != epoch) return current;

        return stage switch
        {
            TranslationSessionStage.Routing or TranslationSessionStage.Translating =>
                current.Phase == TranslateUiPhase.Preparing
                    ? current with { StatusText = "连接中", BadgeText = "" }
                    : current,
            TranslationSessionStage.Streaming =>
                current with
                {
                    Phase = TranslateUiPhase.Streaming,
                    StatusText = "正在生成…",
                    BadgeText = "",
                    IsStreamIndicatorVisible = true,
                    AreResultActionsEnabled = false,
                },
            TranslationSessionStage.Finalizing =>
                current with
                {
                    Phase = TranslateUiPhase.Finalizing,
                    StatusText = "正在整理",
                    BadgeText = "",
                    IsStreamIndicatorVisible = true,
                    AreResultActionsEnabled = false,
                },
            _ => current,
        };
    }

    public static TranslateUiState ApplyStreamUpdate(TranslateUiState current, TranslationStreamUpdate update, long epoch)
    {
        if (current.Epoch != epoch || update.Epoch != epoch) return current;

        if (update.Kind == TranslationStreamUpdateKind.Reset)
        {
            return current with
            {
                StreamText = string.Empty,
                Phase = TranslateUiPhase.Preparing,
                IsStreamLayerVisible = false,
                IsStreamIndicatorVisible = false,
                StatusText = "连接中",
                BadgeText = "",
                AreResultActionsEnabled = false,
            };
        }

        if (update.Kind == TranslationStreamUpdateKind.Delta)
        {
            return current with
            {
                Phase = TranslateUiPhase.Streaming,
                StreamText = update.AccumulatedText,
                IsStreamLayerVisible = true,
                IsFinalLayerVisible = false,
                IsStreamIndicatorVisible = true,
                StatusText = "正在生成…",
                BadgeText = "",
                AreResultActionsEnabled = false,
            };
        }

        return current;
    }

    public static TranslateUiState ApplyCompletion(TranslateUiState current, TranslationSession session, long epoch)
    {
        if (current.Epoch != epoch) return current;

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Explanation))
        {
            notes.Add(session.Explanation.Trim());
        }
        notes.AddRange(session.Warnings);
        var explanation = string.Join("\n", notes);

        if (session.Stage == TranslationSessionStage.Completed)
        {
            return current with
            {
                Phase = TranslateUiPhase.Completed,
                FinalText = session.TranslatedText,
                IsStreamLayerVisible = false,
                IsFinalLayerVisible = true,
                IsStreamIndicatorVisible = false,
                IsProgressVisible = false,
                IsTranslateButtonEnabled = true,
                AreResultActionsEnabled = true,
                BadgeText = session.PipelineLabel ?? "完成",
                StatusText = $"完成 · {session.Timing.TotalElapsedMs} ms",
                ExplanationText = explanation,
                IsExplanationVisible = notes.Count > 0,
                IsPartialIncomplete = false,
            };
        }

        if (session.Stage == TranslationSessionStage.Partial)
        {
            var text = !string.IsNullOrEmpty(session.TranslatedText) ? session.TranslatedText : current.StreamText;
            return current with
            {
                Phase = TranslateUiPhase.Partial,
                FinalText = text,
                IsStreamLayerVisible = false,
                IsFinalLayerVisible = true,
                IsStreamIndicatorVisible = false,
                IsProgressVisible = false,
                IsTranslateButtonEnabled = true,
                AreResultActionsEnabled = false,
                BadgeText = "内容不完整",
                StatusText = $"内容不完整 · {session.Timing.TotalElapsedMs} ms · 见下方说明",
                ExplanationText = explanation,
                IsExplanationVisible = notes.Count > 0,
                IsPartialIncomplete = true,
            };
        }

        if (session.Stage == TranslationSessionStage.Cancelled)
        {
            var hasPartial = !string.IsNullOrWhiteSpace(current.StreamText);
            return current with
            {
                Phase = hasPartial ? TranslateUiPhase.Partial : TranslateUiPhase.Cancelled,
                FinalText = hasPartial ? current.StreamText : string.Empty,
                IsStreamLayerVisible = false,
                IsFinalLayerVisible = true,
                IsStreamIndicatorVisible = false,
                IsProgressVisible = false,
                IsTranslateButtonEnabled = true,
                AreResultActionsEnabled = false,
                BadgeText = hasPartial ? "内容不完整" : "已取消",
                StatusText = hasPartial ? "内容不完整 · 已取消" : "已取消。",
                ExplanationText = string.Empty,
                IsExplanationVisible = false,
                IsPartialIncomplete = hasPartial,
            };
        }

        // Failed / Error
        var message = session.Error?.Message ?? "翻译未完成";
        var suggestion = session.Error?.ActionableSuggestion;
        var failExplanation = string.IsNullOrWhiteSpace(suggestion) ? message : $"{message}\n{suggestion}";
        var hasPartialFail = !string.IsNullOrWhiteSpace(current.StreamText);

        return current with
        {
            Phase = hasPartialFail ? TranslateUiPhase.Partial : TranslateUiPhase.Failed,
            FinalText = hasPartialFail ? current.StreamText : TranslationPanelWindow.FriendlyError(message),
            IsStreamLayerVisible = false,
            IsFinalLayerVisible = true,
            IsStreamIndicatorVisible = false,
            IsProgressVisible = false,
            IsTranslateButtonEnabled = true,
            AreResultActionsEnabled = false,
            BadgeText = hasPartialFail ? "内容不完整" : "未完成",
            StatusText = hasPartialFail
                ? $"内容不完整 · {message}"
                : (string.IsNullOrWhiteSpace(suggestion) ? message : $"{message} {suggestion}"),
            ExplanationText = failExplanation,
            IsExplanationVisible = true,
            IsPartialIncomplete = hasPartialFail,
        };
    }

    public static TranslateUiState ApplyError(TranslateUiState current, Exception exception, long epoch)
    {
        if (current.Epoch != epoch) return current;

        if (exception is OperationCanceledException)
        {
            var hasPartial = !string.IsNullOrWhiteSpace(current.StreamText);
            return current with
            {
                Phase = hasPartial ? TranslateUiPhase.Partial : TranslateUiPhase.Cancelled,
                FinalText = hasPartial ? current.StreamText : string.Empty,
                IsStreamLayerVisible = false,
                IsFinalLayerVisible = true,
                IsStreamIndicatorVisible = false,
                IsProgressVisible = false,
                IsTranslateButtonEnabled = true,
                AreResultActionsEnabled = false,
                BadgeText = hasPartial ? "内容不完整" : "已取消",
                StatusText = hasPartial ? "内容不完整 · 已取消" : "已取消。",
                ExplanationText = string.Empty,
                IsExplanationVisible = false,
                IsPartialIncomplete = hasPartial,
            };
        }

        var hasPartialErr = !string.IsNullOrWhiteSpace(current.StreamText);
        return current with
        {
            Phase = hasPartialErr ? TranslateUiPhase.Partial : TranslateUiPhase.Failed,
            FinalText = hasPartialErr ? current.StreamText : TranslationPanelWindow.FriendlyError(exception.Message),
            IsStreamLayerVisible = false,
            IsFinalLayerVisible = true,
            IsStreamIndicatorVisible = false,
            IsProgressVisible = false,
            IsTranslateButtonEnabled = true,
            AreResultActionsEnabled = false,
            BadgeText = hasPartialErr ? "内容不完整" : "未完成",
            StatusText = hasPartialErr ? $"内容不完整 · 翻译失败：{exception.Message}" : $"翻译失败：{exception.Message}",
            ExplanationText = exception.Message,
            IsExplanationVisible = true,
            IsPartialIncomplete = hasPartialErr,
        };
    }
}

public partial class TranslateSection : System.Windows.Controls.UserControl
{
    private TranslationCoordinator? _coordinator;
    private VocabularyStore? _vocabulary;
    private CancellationTokenSource? _translateOperation;
    private long _currentEpoch;
    private TranslateUiState _currentState = TranslateUiState.Initial;
    // 免费引擎预提示：待决期间压过「连接中」等准备态，真实进展出现即让位。
    private string? _pendingStyleNotice;
    private bool _languageChangeSuspended = true;
    private bool _isUnloaded;
    // 恢复暂存按钮的启用态跟随内存会话仓：仓的变化也可能来自浮窗/托盘，
    // 轻量轮询（纯内存计数）保证按钮状态在变化后最迟 2 秒内跟上。
    private readonly System.Windows.Threading.DispatcherTimer _sessionStorePollTimer;

    public TranslateSection()
    {
        InitializeComponent();
        Ui.AttachCompositionTracker(TranslateInput);
        TranslateSourceLang.ItemsSource = LanguageCatalog.Sources;
        TranslateTargetLang.ItemsSource = LanguageCatalog.Targets;
        _sessionStorePollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _sessionStorePollTimer.Tick += (_, _) => RefreshRestoreSessionAffordance();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 顶部工具栏的快捷键提示与左侧标题、右侧按钮组共享一行：窄态下
        // 三者拥挤，提示会被压成极小的低对比碎片。这里按工作台实际宽度
        // 自行收起提示；按键说明始终保留在输入框的 HelpText 上，读屏与
        // 悬停路径不受可见性影响。
        SizeChanged += (_, _) => UpdateShortcutHintVisibility();

        // Start from the persisted language pair; both this workbench and the
        // floating panel keep the pair in sync through core settings.
        try
        {
            var stored = CoreBridge.GetSettings();
            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(stored.SourceLanguage);
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(stored.TargetLanguage);
        }
        catch (Exception)
        {
            // Headless/offline contexts still get usable defaults.
            TranslateSourceLang.SelectedIndex = 0;
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget("zh-CN");
        }
        _languageChangeSuspended = false;
        ApplyState(_currentState);
        RefreshStyleSelector();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
        TtsService.SpeakingStateChanged += OnTtsSpeakingStateChanged;
        UpdateServiceAvailability();
        RefreshStarState();
        RefreshStyleSelector();
        RefreshRestoreSessionAffordance();
        _sessionStorePollTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        TtsService.SpeakingStateChanged -= OnTtsSpeakingStateChanged;
        _sessionStorePollTimer.Stop();
        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        _translateOperation = null;
    }

    internal void Initialize(TranslationCoordinator coordinator, VocabularyStore? vocabulary)
    {
        _coordinator = coordinator;
        _vocabulary = vocabulary;
        RefreshFreeEngineEntryVisibility();
        RefreshStyleSelector();
    }

    // ================= Public accessors for MainWindow =================

    internal ComboBox SourceLangCombo => TranslateSourceLang;
    internal ComboBox TargetLangCombo => TranslateTargetLang;
    internal TextBox InputBox => TranslateInput;
    internal TextBlock ShortcutHint => TranslateShortcutHint;
    internal TextBox ResultBox => TranslateResult;
    internal TextBox StreamResultBox => TranslateStreamResult;
    internal StackPanel EmptyStateGuide => TranslateEmptyState;
    internal Button StarButton => TranslateStarButton;
    internal Button FreeEngineEntryButton => EnableFreeEngineButton;
    internal Action? OpenSettings { get; set; }

    /// <summary>
    /// Direct route for the 添加翻译引擎 call to action: opens settings,
    /// selects the engine page and enters the add-engine flow. Set by the
    /// shell (App); when unset the plain OpenSettings fallback applies.
    /// </summary>
    internal Action? OpenAddEngineFlow { get; set; }
    internal System.Windows.Controls.Grid PaneGrid => TranslatePaneGrid;
    internal Border StreamIndicator => TranslateStreamIndicator;
    internal TextBlock ExplanationText => TranslateExplanation;
    internal StackPanel ExplanationBox => TranslateExplanationBox;
    internal ScrollViewer ResultScroll => TranslateResultScroll;
    internal TranslateUiState CurrentState => _currentState;
    internal long CurrentEpoch => _currentEpoch;

    /// <summary>
    /// Compact mode adjusts secondary text and responsive constraints so
    /// hints remain readable in tight viewports instead of abruptly vanishing.
    /// Panes stay side by side in true desktop workstation fashion.
    /// </summary>
    internal void SetCompact(bool compact)
    {
        if (compact)
        {
            TranslateShortcutHint.Text = "↵ 翻译 · ⇧↵ 换行";
            TranslateShortcutHint.ToolTip = "Enter 翻译 · Shift+Enter 换行";
            TranslateStatus.MaxWidth = 160;
        }
        else
        {
            TranslateShortcutHint.Text = "Enter 翻译 · Shift+Enter 换行";
            TranslateShortcutHint.ToolTip = null;
            TranslateStatus.MaxWidth = 260;
        }
        UpdateShortcutHintVisibility();
    }

    /// <summary>
    /// 工具栏单行能舒适容纳「标题 + 快捷键提示 + 三个按钮」的最小宽度：
    /// 低于该阈值时标题与按钮组挤占提示文字，收起提示（快捷键说明仍在
    /// 输入框 HelpText 与 SetCompact 设置的 ToolTip 里）。宽度的零值表示
    /// 尚未参与布局，保持可见默认，避免构造期误隐藏。
    /// </summary>
    internal const double ShortcutHintHideWidth = 620;

    private void UpdateShortcutHintVisibility()
    {
        if (ActualWidth <= 0)
        {
            return;
        }
        TranslateShortcutHint.Visibility = ActualWidth < ShortcutHintHideWidth
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private bool _stacked;
    private bool _wide;

    /// <summary>
    /// V2 §10: >= 960 DIP wide desktop mode.
    /// Dual panes stay side by side with the target reading pane receiving an enhanced
    /// width ratio (1.25*) for comfortable long-text translation reading on wide monitors.
    /// </summary>
    internal void SetWide(bool wide)
    {
        if (_wide == wide)
        {
            return;
        }
        _wide = wide;
        if (!_stacked)
        {
            ApplyColumnWidths();
        }
    }

    private void ApplyColumnWidths()
    {
        if (_stacked || TranslatePaneGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }
        TranslatePaneGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        TranslatePaneGrid.ColumnDefinitions[1].Width = new GridLength(40);
        TranslatePaneGrid.ColumnDefinitions[2].Width = new GridLength(_wide ? 1.25 : 1.0, GridUnitType.Star);
    }

    /// <summary>
    /// Below 720 DIP of content width the panes stack vertically (AI-RULES
    /// 6.2): source on top with at least 160 DIP of editor, target below,
    /// the swap button and centre axis hidden instead of squeezing the text.
    /// </summary>
    internal void SetStacked(bool stacked)
    {
        if (_stacked == stacked)
        {
            return;
        }
        _stacked = stacked;

        TranslatePaneGrid.RowDefinitions.Clear();
        TranslatePaneGrid.ColumnDefinitions.Clear();
        if (stacked)
        {
            for (var row = 0; row < 6; row++)
            {
                var inputRow = row == 1;
                TranslatePaneGrid.RowDefinitions.Add(new RowDefinition
                {
                    Height = inputRow ? GridLength.Auto : row == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
                    MinHeight = inputRow ? 160 : 0,
                });
            }
            TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Place(SourceLangBarCell, 0);
            Place(SourceEditorCell, 1);
            Place(SourceFooterCell, 2);
            Place(TargetLangBarCell, 3);
            Place(TargetEditorCell, 4);
            Place(TargetFooterCell, 5);
            TargetLangBarCell.BorderThickness = new Thickness(0, 1, 0, 1);

            TranslateSwapButton.Visibility = Visibility.Collapsed;
            AxisTopCell.Visibility = Visibility.Collapsed;
            AxisFooterCell.Visibility = Visibility.Collapsed;
            AxisDividerLine.Visibility = Visibility.Collapsed;
            return;
        }

        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_wide ? 1.25 : 1.0, GridUnitType.Star) });

        Place(SourceLangBarCell, 0, 0);
        Place(SourceEditorCell, 1, 0);
        Place(SourceFooterCell, 2, 0);
        Place(AxisTopCell, 0, 1);
        System.Windows.Controls.Grid.SetRow(TranslateSwapButton, 0);
        System.Windows.Controls.Grid.SetColumn(TranslateSwapButton, 1);
        // 竖线只走正文区：停在页脚顶边框，不与横线交叉成十字。
        System.Windows.Controls.Grid.SetRowSpan(AxisDividerLine, 1);
        Place(AxisDividerLine, 1, 1);
        Place(AxisFooterCell, 2, 1);
        Place(TargetLangBarCell, 0, 2);
        Place(TargetEditorCell, 1, 2);
        Place(TargetFooterCell, 2, 2);
        TargetLangBarCell.BorderThickness = new Thickness(0, 0, 0, 1);

        TranslateSwapButton.Visibility = Visibility.Visible;
        AxisTopCell.Visibility = Visibility.Visible;
        AxisFooterCell.Visibility = Visibility.Visible;
        AxisDividerLine.Visibility = Visibility.Visible;

        static void Place(System.Windows.UIElement element, int row, int column = 0)
        {
            System.Windows.Controls.Grid.SetRow(element, row);
            System.Windows.Controls.Grid.SetColumn(element, column);
            System.Windows.Controls.Grid.SetColumnSpan(element, 1);
        }
    }

    internal bool IsStacked => _stacked;

    internal TextBlock EngineBadge => TranslateEngineBadge;
    internal TextBlock StatusBlock => TranslateStatus;

    // ================= Event handlers =================

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async void TranslateInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }
        // An IME (Chinese pinyin etc.) consumes Enter to confirm the current
        // composition — that Enter must never submit a translation.
        if (Ui.IsImeComposing(TranslateInput, e))
        {
            return;
        }
        e.Handled = true;
        await TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        if (_coordinator is null) return;
        var source = TranslateInput.Text.Trim();
        if (string.IsNullOrEmpty(source))
        {
            TranslateStatus.Text = "请先输入要翻译的内容。";
            return;
        }

        var epoch = Interlocked.Increment(ref _currentEpoch);
        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        var operation = new CancellationTokenSource();
        _translateOperation = operation;

        var sourceLang = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
        var targetLang = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");

        // Honest pre-flight notice when the route cannot be proven to honour
        // the active style (the free engine never personalizes): state it
        // BEFORE the request goes out. The request itself is never blocked,
        // nothing in flight is cancelled and nothing is re-translated.
        // ApplyState keeps the notice above the preparing statuses until real
        // progress lands, instead of letting it vanish instantly.
        string? styleNotice = TranslationStyleMenu.PreTranslateNotice();
        _pendingStyleNotice = styleNotice;
        if (styleNotice is not null)
        {
            TranslateStatus.Text = styleNotice;
            RefreshStyleSelector();
        }

        ApplyState(TranslateSectionReducer.StartTranslation(_currentState, epoch));

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (_isUnloaded) return;
            if (epoch != _currentEpoch || _translateOperation != operation || operation.IsCancellationRequested)
            {
                return;
            }
            ApplyState(TranslateSectionReducer.ApplyStreamUpdate(_currentState, update, epoch));
        });

        try
        {
            var session = await _coordinator.TranslateTextAsync(
                source,
                sourceLang,
                targetLang,
                TranslationInputSource.Manual,
                operation.Token,
                onStageChanged: stage =>
                {
                    if (_isUnloaded) return;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_isUnloaded || epoch != _currentEpoch || _translateOperation != operation || operation.IsCancellationRequested)
                        {
                            return;
                        }
                        ApplyState(TranslateSectionReducer.ApplyStage(_currentState, stage, epoch));
                    });
                },
                progress: progress,
                epoch: epoch);

            if (_isUnloaded || epoch != _currentEpoch || _translateOperation != operation)
            {
                return;
            }

            ApplyState(TranslateSectionReducer.ApplyCompletion(_currentState, session, epoch));

            // Durable post-hoc honesty: when the free engine actually ran the
            // request, the selected style did not apply — keep saying so
            // instead of leaving only the transient pre-flight notice.
            // Decided by the TYPED prompt-support fact, rendered through the
            // shared short status; never by matching PipelineLabel.
            if (styleNotice is not null &&
                session.PromptSupport == TranslationPromptSupport.NotSupported)
            {
                TranslateStatus.Text = TranslationStyleMenu.StyleStatusFor(
                    TranslationPromptSupport.NotSupported);
            }
        }
        catch (OperationCanceledException ex)
        {
            if (_isUnloaded || epoch != _currentEpoch) return;
            ApplyState(TranslateSectionReducer.ApplyError(_currentState, ex, epoch));
        }
        catch (Exception ex)
        {
            if (_isUnloaded || epoch != _currentEpoch) return;
            ApplyState(TranslateSectionReducer.ApplyError(_currentState, ex, epoch));
        }
        finally
        {
            if (ReferenceEquals(_translateOperation, operation))
            {
                _translateOperation = null;
            }
            operation.Dispose();
        }
    }

    private void ApplyState(TranslateUiState state)
    {
        _currentState = state;
        if (_isUnloaded) return;

        TranslateButton.IsEnabled = state.IsTranslateButtonEnabled;
        TranslateProgress.Visibility = state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        TranslateStreamIndicator.Visibility = state.IsStreamIndicatorVisible ? Visibility.Visible : Visibility.Collapsed;

        TranslateStreamResult.Visibility = state.IsStreamLayerVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!state.IsFinalLayerVisible)
        {
            TranslateResult.Visibility = Visibility.Collapsed;
            TranslateRichResult.Visibility = Visibility.Collapsed;
            TranslateRichResult.Document.Blocks.Clear();
        }

        if (state.IsStreamLayerVisible)
        {
            // Stick-to-bottom: follow the stream only while the reader sits at
            // the bottom, so scrolling up to re-read is never overridden.
            var stickToBottom = Ui.IsScrolledToBottom(Ui.FindScrollViewer(TranslateStreamResult));
            TranslateStreamResult.Text = state.StreamText;
            if (stickToBottom)
            {
                TranslateStreamResult.ScrollToEnd();
            }
        }

        if (state.IsFinalLayerVisible)
        {
            TranslateResult.Text = state.FinalText;
            if (string.IsNullOrWhiteSpace(state.FinalText))
            {
                TranslateRichResult.Visibility = Visibility.Collapsed;
                TranslateResult.Visibility = Visibility.Visible;
            }
            else
            {
                try
                {
                    MarkdownPresenter.RenderToFlowDocument(
                        TranslateRichResult.Document,
                        state.FinalText,
                        Application.Current?.Resources ?? Resources,
                        resultActionsEnabled: state.AreResultActionsEnabled);
                    TranslateRichResult.Visibility = Visibility.Visible;
                    TranslateResult.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    TranslateRichResult.Visibility = Visibility.Collapsed;
                    TranslateResult.Visibility = Visibility.Visible;
                }
            }
        }

        TranslateEngineBadge.Text = state.BadgeText;
        TranslateStatus.Text = state.StatusText;
        if (!HasConfiguredUserEngine() && state.Phase == TranslateUiPhase.Idle && string.IsNullOrWhiteSpace(state.FinalText))
        {
            TranslateEngineBadge.Text = HasFallbackRoute() ? EngineWording.FreePublicTranslationName : "未配置";
            TranslateStatus.Text = HasFallbackRoute() ? "就绪" : "未配置引擎，请前往设置接入";
        }

        // 免费引擎预提示不得被「连接中/正在生成…」这类准备态瞬间覆盖：在
        // Preparing 阶段保持可见，流式文本、完成或失败一旦落地即让位。
        if (_pendingStyleNotice is not null)
        {
            if (state.Phase == TranslateUiPhase.Preparing)
            {
                TranslateStatus.Text = _pendingStyleNotice;
            }
            else
            {
                _pendingStyleNotice = null;
            }
        }

        TranslateResultSpeakButton.IsEnabled = state.AreResultActionsEnabled;
        TranslateResultCopyButton.IsEnabled = state.AreResultActionsEnabled;
        TranslateStarButton.IsEnabled = state.AreResultActionsEnabled;

        TranslateExplanation.Text = state.ExplanationText;
        TranslateExplanationBox.Visibility = state.IsExplanationVisible ? Visibility.Visible : Visibility.Collapsed;

        // First-use guidance lives only on the empty, idle result plane.
        var showEmptyState = !state.IsProgressVisible &&
            !state.IsStreamLayerVisible &&
            string.IsNullOrWhiteSpace(state.FinalText);
        TranslateEmptyState.Visibility = showEmptyState ? Visibility.Visible : Visibility.Collapsed;
        if (showEmptyState)
        {
            UpdateServiceAvailability();
        }

        if (state.Phase == TranslateUiPhase.Completed || state.Phase == TranslateUiPhase.Idle)
        {
            RefreshStarState();
        }
        else if (!state.AreResultActionsEnabled)
        {
            UpdateStarVisualState(false);
        }
    }

    private void OnTtsSpeakingStateChanged(object? sender, bool isSpeaking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isSpeaking)
            {
                // Dynamic references (mirrors QuickSearch/Panel): a static
                // FindResource brush keeps the accent of whatever theme was
                // active when speech started.
                TranslateSourceSpeakIcon?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
                TranslateResultSpeakIcon?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            }
            else
            {
                // ClearValue would ERASE the XAML's dynamic reference for good
                // (the expression itself is the local value), leaving the idle
                // icon with no fill. Re-establish the idle TextSecondaryBrush
                // dynamic reference — the exact semantics the XAML declares —
                // so idle stays dynamic and theme-following too.
                TranslateSourceSpeakIcon?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextSecondaryBrush");
                TranslateResultSpeakIcon?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextSecondaryBrush");
            }
            if (TranslateSourceSpeakButton is not null)
            {
                TranslateSourceSpeakButton.ToolTip = isSpeaking ? "停止朗读" : "朗读原文";
                // The accessibility name follows the ToolTip so screen readers
                // announce the action the click will actually perform now.
                System.Windows.Automation.AutomationProperties.SetName(TranslateSourceSpeakButton, (string)TranslateSourceSpeakButton.ToolTip);
            }
            if (TranslateResultSpeakButton is not null)
            {
                TranslateResultSpeakButton.ToolTip = isSpeaking ? "停止朗读" : "朗读译文";
                System.Windows.Automation.AutomationProperties.SetName(TranslateResultSpeakButton, (string)TranslateResultSpeakButton.ToolTip);
            }
        });
    }

    private void UpdateStarVisualState(bool isStarred)
    {
        if (TranslateStarIcon is null || TranslateStarButton is null) return;
        if (isStarred)
        {
            TranslateStarIcon.Fill = (Brush)FindResource("AccentBrush");
            TranslateStarButton.ToolTip = "从生词本移除";
        }
        else
        {
            TranslateStarIcon.Fill = (Brush)FindResource("TextSecondaryBrush");
            TranslateStarButton.ToolTip = "收藏到生词本";
        }
        // The accessibility name must follow the dynamic state so screen
        // readers announce the action the click will actually perform.
        System.Windows.Automation.AutomationProperties.SetName(TranslateStarButton, (string)TranslateStarButton.ToolTip);
    }

    private void RefreshStarState()
    {
        if (_vocabulary is null)
        {
            UpdateStarVisualState(false);
            return;
        }
        var source = TranslateInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            UpdateStarVisualState(false);
            return;
        }
        var sourceLang = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
        var targetLang = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");
        var starred = _vocabulary.IsStarred(source, sourceLang, targetLang);
        UpdateStarVisualState(starred);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (OpenSettings is not null)
        {
            OpenSettings.Invoke();
            return;
        }
        var main = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        main?.OpenSettings?.Invoke();
    }

    /// <summary>
    /// The 添加翻译引擎 call to action: navigates only. It must not probe
    /// the network, mutate consent or open any dialog — the settings window
    /// lands directly in the add-engine flow.
    /// </summary>
    private void OpenAddEngine_Click(object sender, RoutedEventArgs e)
    {
        if (OpenAddEngineFlow is not null)
        {
            OpenAddEngineFlow.Invoke();
            return;
        }
        var main = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        if (main?.OpenAddEngineFlow is not null)
        {
            main.OpenAddEngineFlow.Invoke();
            return;
        }
        OpenSettings_Click(sender, e);
    }

    /// <summary>
    /// Two independent concepts, never one boolean:
    /// <see cref="ProfileManager.HasConfiguredUserEngine"/> — a complete,
    /// executable user engine exists; <paramref name="hasFallbackRoute"/> —
    /// the built-in public translation is allowed. The CTA hides only when
    /// the first is true; the fallback never hides it.
    /// </summary>
    private static bool HasConfiguredUserEngine() => ProfileManager.HasConfiguredUserEngine();

    private static bool HasFallbackRoute()
    {
        try
        {
            return ShellSettingsStore.Load().FreeEngineConsent == FreeEngineConsent.Allowed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Settings save/delete/re-activation lands here: the empty state must
    /// repaint immediately, without a restart, a page switch or a translate.
    /// </summary>
    internal void RefreshAfterSettingsChanged()
    {
        RefreshStyleSelector();
        if (!_currentState.IsProgressVisible &&
            !_currentState.IsStreamLayerVisible &&
            string.IsNullOrWhiteSpace(_currentState.FinalText))
        {
            UpdateServiceAvailability();
        }
    }

    private void UpdateServiceAvailability()
    {
        var hasUserEngine = HasConfiguredUserEngine();
        var hasFallbackRoute = HasFallbackRoute();
        var consent = FreeEngineConsent.Unset;
        try
        {
            var settings = ShellSettingsStore.Load();
            consent = settings.FreeEngineConsent;
            if (ShortcutEntriesHint is not null)
            {
                var sel = settings.SelectionHotkey?.DisplayName ?? "Ctrl+Alt+W";
                var cap = settings.ScreenshotHotkey?.DisplayName ?? "Ctrl+Alt+Space";
                ShortcutEntriesHint.Text = $"划词 {sel} · 截图 {cap}";
            }
        }
        catch
        {
        }

        if (UnconfiguredGuidePanel is not null)
        {
            UnconfiguredGuidePanel.Visibility = hasUserEngine ? Visibility.Collapsed : Visibility.Visible;
        }
        if (NormalGuidePanel is not null)
        {
            NormalGuidePanel.Visibility = hasUserEngine ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!hasUserEngine)
        {
            // Two distinct empty states: fallback allowed vs no route at all.
            if (GuideTitle is not null)
            {
                GuideTitle.Text = hasFallbackRoute ? $"当前使用{EngineWording.FreePublicTranslationName}" : "尚未配置翻译引擎";
            }
            if (GuideDescription is not null)
            {
                GuideDescription.Text = hasFallbackRoute
                    ? "添加自己的翻译引擎，可使用指定模型和服务商。"
                    : "添加翻译引擎后即可开始使用。";
            }
        }
        if (EnableFreeEngineButton is not null)
        {
            EnableFreeEngineButton.Visibility = consent == FreeEngineConsent.Unset
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!hasUserEngine && _currentState.Phase == TranslateUiPhase.Idle && string.IsNullOrWhiteSpace(_currentState.FinalText))
        {
            TranslateEngineBadge.Text = hasFallbackRoute ? EngineWording.FreePublicTranslationName : "未配置";
            TranslateStatus.Text = hasFallbackRoute ? $"当前使用{EngineWording.FreePublicTranslationName}" : "未配置引擎，请前往设置接入";
        }
        else if (hasUserEngine && _currentState.Phase == TranslateUiPhase.Idle && string.IsNullOrWhiteSpace(_currentState.FinalText))
        {
            // Settings changed the engine while we were idle: the badge must
            // name the engine that will actually run next, never a stale one.
            try
            {
                var config = ProfileManager.Load();
                var active = config.TryGetActiveProfile();
                TranslateEngineBadge.Text = active?.Name ?? (config.PreferFreeEngine ? EngineWording.FreePublicTranslationName : "未配置");
            }
            catch
            {
                TranslateEngineBadge.Text = "未配置";
            }
            TranslateStatus.Text = "就绪";
        }
    }

    /// <summary>
    /// Fills the agreed demo error — text only, no request. The user's next
    /// action (Enter or the button) decides whether anything goes online.
    /// The entry point is explicitly labelled 演示 · 未联网 in the empty state.
    /// </summary>
    private void FillExample_Click(object sender, RoutedEventArgs e)
    {
        TranslateInput.Text = "FileNotFoundError: config.json not found";
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
        TranslateInput.Focus();
        TranslateStatus.Text = "已填入演示（未联网），按 Enter 翻译";
    }

    /// <summary>
    /// Inline first-use consent for the built-in public engine, with the
    /// destinations named. Saving here is the same action the privacy page
    /// performs; nothing is sent until the user actually translates.
    /// </summary>
    private void EnableFreeEngine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var shell = ShellSettingsStore.Load();
            var updated = shell with { FreeEngineConsent = FreeEngineConsent.Allowed };
            ShellSettingsStore.Save(updated);
            UpdateServiceAvailability();
            TranslateStatus.Text = $"已允许{EngineWording.FreePublicTranslationName}（联网公共服务）；首次翻译会连接 translate.googleapis.com。";
        }
        catch (Exception exception)
        {
            TranslateStatus.Text = $"保存授权失败：{exception.Message}";
        }
    }

    private void RefreshFreeEngineEntryVisibility() => UpdateServiceAvailability();

    // ================= Global translation-style selector =================

    /// <summary>
    /// Repaints the shared style selector from local state: the capability
    /// probe decides enablement and tooltip, and the authoritative active
    /// template provides the label. Safe to call any time; degrades quietly
    /// when local state is unreadable.
    /// </summary>
    internal void RefreshStyleSelector()
    {
        try
        {
            TranslationStyleMenu.ApplyTo(StyleSelectorButton);
            StyleSelectorLabel.Text = TranslationStyleMenu.ActiveLabel();
        }
        catch (Exception)
        {
            // The selector is additive UI; never break the workbench over it.
        }
    }

    private void StyleSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        // Re-probe at open time: the engine may have changed since the last
        // paint, and the honest state must match the route a new request
        // would actually take.
        RefreshStyleSelector();
        if (TranslationStyleMenu.ProbeNextTextRoute() == TranslationStyleSupport.FreeEngine)
        {
            TranslateStatus.Text = $"{EngineWording.FreeEngineName}{TranslationStyleMenu.FreeEngineToolTip}。";
            return;
        }
        var menu = TranslationStyleMenu.Build(
            styleChosen: template => _ = ChooseStyleAsync(template),
            reportStatus: message => TranslateStatus.Text = message,
            managePrompts: () => OpenSettings_Click(this, new RoutedEventArgs()));
        if (menu is null)
        {
            return;
        }
        TranslationStyleMenu.Show(button, menu);
    }

    private async Task ChooseStyleAsync(PromptTemplateDto template)
    {
        // Optimistic label; reverted from the authoritative state if the
        // persist fails. Nothing is re-translated and nothing in flight is
        // cancelled — the switch lands with the next request only.
        StyleSelectorLabel.Text = TranslationStyleMenu.ShortLabel(template);
        var applied = await TranslationStyleMenu.SwitchActiveTemplateAsync(
            template.Id,
            message => TranslateStatus.Text = message);
        if (!applied)
        {
            RefreshStyleSelector();
        }
    }

    private void TranslateInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        var length = TranslateInput.Text.Length;
        TranslateCounter.Text = $"{length} 字符";
        // 空输入时隐藏「0 字符」：数字零没有信息量，只会在页脚占位。
        TranslateCounter.Visibility = length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ================= Language pair =================

    private void SourceLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PersistLanguagePair();
    }

    private void TargetLang_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PersistLanguagePair();

    /// <summary>Remembers the pair so the floating panel opens the same way.</summary>
    private void PersistLanguagePair()
    {
        if (_languageChangeSuspended)
        {
            return;
        }
        try
        {
            var settings = CoreBridge.GetSettings();
            var source = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
            var target = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");
            if (settings.SourceLanguage == source && settings.TargetLanguage == target)
            {
                return;
            }
            _ = CoreBridge.SaveSettingsAsync(settings with
            {
                SourceLanguage = source,
                TargetLanguage = target,
            });
        }
        catch (InvalidOperationException)
        {
            // Remembering the pair is a convenience, never a reason to fail.
        }
    }

    private void TranslateSwap_Click(object sender, RoutedEventArgs e)
    {
        var (source, target) = LanguageCatalog.Swap(
            Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"));

        _languageChangeSuspended = true;
        try
        {
            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(source);
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(target);
        }
        finally
        {
            _languageChangeSuspended = false;
        }
        PersistLanguagePair();

        if (!string.IsNullOrWhiteSpace(TranslateResult.Text))
        {
            // 结果层保存的是原始 Markdown；换回原文方向时必须携带约定的
            // 纯文本（与浮窗/极速查词相同的 ToPlainText 格式化器），而不是
            // 把标记符号塞进输入框。
            var plain = MarkdownPresenter.ToPlainText(TranslateResult.Text);
            if (string.IsNullOrWhiteSpace(plain))
            {
                return;
            }
            TranslateInput.Text = plain;
            var epoch = Interlocked.Increment(ref _currentEpoch);
            ApplyState(TranslateUiState.Initial with { Epoch = epoch });
        }
    }

    // ================= Result actions =================

    private void TranslateSourceSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(TranslateInput.Text, Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto));

    private void TranslateResultSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(MarkdownPresenter.ToPlainText(TranslateResult.Text),
            Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"));

    private void SpeakOrStop(string text, string languageTag)
    {
        if (TtsService.IsSpeaking)
        {
            TtsService.Stop();
            return;
        }
        TtsService.Speak(text, languageTag);
    }

    private void TranslateSourceCopy_Click(object sender, RoutedEventArgs e) => _ = CopySourceToClipboardAsync();

    private void TranslateResultCopy_Click(object sender, RoutedEventArgs e) => _ = CopyResultToClipboardAsync();

    private async void TranslateStar_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null)
        {
            TranslateStatus.Text = "生词本不可用。";
            return;
        }
        var source = TranslateInput.Text.Trim();
        var translation = TranslateResult.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
        {
            TranslateStatus.Text = "先翻译一段内容再收藏。";
            return;
        }
        var sourceLang = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
        var targetLang = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");
        var result = await _vocabulary.ToggleStarAsync(source, translation, string.Empty, string.Empty, sourceLang, targetLang);
        if (result.Persisted)
        {
            UpdateStarVisualState(result.Starred);
        }
        TranslateStatus.Text = result.Persisted
            ? (result.Starred ? "已加入生词本" : "已从生词本移除")
            : result.DescribeFailureZh();
    }

    private void TranslateMergeLines_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslateInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var merged = TranslationPanelWindow.MergeHardLineBreaks(text);
        if (merged == text)
        {
            return;
        }
        TranslateInput.Text = merged;
        TranslateStatus.Text = "已合并断行。";
    }

    private async Task CopySourceToClipboardAsync()
    {
        if (await Helpers.CopyToClipboardAsync(TranslateInput.Text))
        {
            TranslateStatus.Text = "已复制原文。";
        }
    }

    private async Task CopyResultToClipboardAsync()
    {
        // Same formatter as the floating panel and quick search so every
        // entry point copies the identical agreed plain text.
        var clean = MarkdownPresenter.ToPlainText(TranslateResult.Text);
        if (await Helpers.CopyToClipboardAsync(clean))
        {
            TranslateStatus.Text = "已复制译文。";
        }
    }

    private void TranslateClear_Click(object sender, RoutedEventArgs e)
    {
        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        _translateOperation = null;
        var epoch = Interlocked.Increment(ref _currentEpoch);
        ApplyState(TranslateUiState.Initial with { Epoch = epoch });
        UpdateStarVisualState(false);
        TranslateInput.Clear();
        TranslateInput.Focus();
    }

    private void RestoreSession_Click(object sender, RoutedEventArgs e)
    {
        var sessions = App.SharedSessionStore.GetAll();
        RefreshRestoreSessionAffordance();
        if (sessions.Count == 0)
        {
            TranslateStatus.Text = "暂无暂存的会话。";
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var s in sessions)
        {
            var item = new System.Windows.Controls.MenuItem { Header = s.Summary() };
            item.Click += (_, _) =>
            {
                FocusTranslate(
                    s.SourceText, s.TargetLanguage, s.SourceLanguage,
                    s.ResultText, s.State, s.ExplanationText);
                TranslateStatus.Text = "已恢复暂存会话（未重发）";
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var clearItem = new System.Windows.Controls.MenuItem { Header = "清空所有暂存会话" };
        clearItem.Click += (_, _) =>
        {
            App.SharedSessionStore.Clear();
            RefreshRestoreSessionAffordance();
            TranslateStatus.Text = "已清空暂存的翻译。";
        };
        menu.Items.Add(clearItem);

        menu.PlacementTarget = RestoreSessionButton;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 恢复暂存按钮按会话仓的真实库存决定显示状态：有暂存可恢复时可用，
    /// 空仓时禁用并说明原因（按钮保持可见，避免工具栏布局跳动）。
    /// </summary>
    private void RefreshRestoreSessionAffordance()
    {
        if (RestoreSessionButton is null)
        {
            return;
        }
        var hasSessions = App.SharedSessionStore.GetAll().Count > 0;
        RestoreSessionButton.IsEnabled = hasSessions;
        RestoreSessionButton.ToolTip = hasSessions
            ? "从暂存的翻译恢复最近未完成或刚关闭的会话（最多5条）"
            : "暂存为空；翻译结束或载入内容后自动暂存";
    }

    /// <summary>
    /// N01 honesty for the preserved workbench draft: an untranslated draft
    /// must never be recorded as a Completed session. The session model has
    /// no Draft state, so the most honest compatible representation wins —
    /// Cancelled with no result text (a Draft state would take precedence if
    /// the model ever grows one). Partial or failed text is kept, but the
    /// state says so and <c>IsPartial</c> flags it, so restoring can never
    /// open the full result actions for content that was never finished.
    /// </summary>
    internal static (TranslationSessionState State, string? ResultText, string? ExplanationText, bool IsPartial) DraftSnapshotFor(TranslateUiState current)
    {
        var result = current.FinalText;
        var hasResult = !string.IsNullOrWhiteSpace(result);
        var explanation = !string.IsNullOrWhiteSpace(current.ExplanationText) ? current.ExplanationText : null;

        if (current.Phase == TranslateUiPhase.Completed && hasResult)
        {
            return (TranslationSessionState.Completed, result, explanation, IsPartial: false);
        }
        if (current.Phase == TranslateUiPhase.Failed)
        {
            return (TranslationSessionState.Failed, hasResult ? result : null, hasResult ? explanation : null, IsPartial: hasResult);
        }
        // Pure drafts, cancellations and partials: Cancelled — never Completed.
        return (TranslationSessionState.Cancelled, hasResult ? result : null, hasResult ? explanation : null, IsPartial: hasResult);
    }

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    /// <param name="storedState">
    /// State of the stored session the translation comes from, when known.
    /// Only a genuinely Completed session opens the full result actions on
    /// restore; partial/cancelled/failed snapshots restore their text but
    /// keep copy/speak/star gated. History/vocabulary loads pass Completed.
    /// </param>
    /// <param name="explanation">Explanation/notes carried with the loaded entry, if any.</param>
    /// <param name="badge">Optional badge text for the loaded entry (e.g. 历史记录/生词本).</param>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null,
        TranslationSessionState? storedState = null,
        string? explanation = null,
        string? badge = null)
    {
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            if (!string.IsNullOrWhiteSpace(TranslateInput.Text) && TranslateInput.Text != initialText)
            {
                // N01: Preserve the workbench draft in the session store before
                // overwriting — with the honest state, never a fake Completed
                // for an untranslated draft. A rejected stash BLOCKS the
                // overwrite: silently losing the user's draft is worse than
                // keeping it, so the reason is shown instead.
                var draft = DraftSnapshotFor(_currentState);
                var draftSession = StoredSession.Create(
                    sessionId: null,
                    origin: SessionOrigin.Workbench,
                    sourceText: TranslateInput.Text,
                    sourceLang: Helpers.SelectedLanguage(TranslateSourceLang, "auto"),
                    targetLang: Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"),
                    engineProfileId: null,
                    engineName: null,
                    state: draft.State,
                    resultText: draft.ResultText,
                    explanationText: draft.ExplanationText,
                    isPartial: draft.IsPartial
                );
                if (!App.SharedSessionStore.TryStore(draftSession, out var draftRejection))
                {
                    TranslateStatus.Text =
                        $"未能暂存当前草稿，已保留工作台内容：{draftRejection ?? "暂存的翻译暂不可用"}。";
                    TranslateInput.Focus();
                    return;
                }
            }
            TranslateInput.Text = initialText;
        }
        _languageChangeSuspended = true;
        try
        {
            // Loading an entry only borrows the dropdowns: suspension keeps
            // the pair from being persisted as the global default.
            if (!string.IsNullOrWhiteSpace(sourceLang))
            {
                TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(sourceLang);
            }
            if (!string.IsNullOrWhiteSpace(targetLang))
            {
                TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(targetLang);
            }
        }
        finally
        {
            _languageChangeSuspended = false;
        }
        if (existingTranslation is not null || storedState is not null)
        {
            // A restored text is only a finished result when the stored
            // session says so (callers that predate the state parameter
            // count as finished); anything else comes back with the full
            // result actions gated.
            var isFinishedResult = storedState is null || storedState == TranslationSessionState.Completed;
            var restoredText = existingTranslation ?? string.Empty;
            var epoch = Interlocked.Increment(ref _currentEpoch);
            ApplyState(TranslateUiState.Initial with
            {
                Epoch = epoch,
                FinalText = restoredText,
                BadgeText = badge ?? (isFinishedResult
                    ? "已展开的译文"
                    : storedState == TranslationSessionState.Failed ? "未完成" : "内容不完整"),
                StatusText = isFinishedResult
                    ? (badge is null ? "已载入，未重译" : "已载入记录。")
                    : "已恢复暂存会话（未重发）",
                AreResultActionsEnabled = isFinishedResult && !string.IsNullOrWhiteSpace(restoredText),
                IsPartialIncomplete = !isFinishedResult,
                ExplanationText = explanation ?? string.Empty,
                IsExplanationVisible = !string.IsNullOrWhiteSpace(explanation),
            });
        }
        TranslateInput.Focus();
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
        RefreshStarState();
        RefreshRestoreSessionAffordance();
    }

    // ================= First-run onboarding (non-blocking, in-workbench) =================
    //
    // 最小闭环：工作台内联的三步引导条（非模态、可跳过）。第一步复用空态
    // 卡的「添加翻译引擎」路径；完成或跳过由 shell 持久化
    // HasCompletedOnboarding（MainWindow.CompleteOnboarding），设置 → 通用
    // 提供「重新显示引导」入口。绝不弹窗、绝不打断翻译。

    private int _onboardingStep;

    /// <summary>Persisted by the shell (MainWindow) when the guide is completed or skipped.</summary>
    internal Action? CompleteOnboarding { get; set; }

    internal bool IsOnboardingActive => OnboardingBanner is { Visibility: Visibility.Visible };

    internal void BeginOnboarding()
    {
        _onboardingStep = 1;
        ShowOnboardingStep();
    }

    internal void EndOnboarding()
    {
        if (OnboardingBanner is not null)
        {
            OnboardingBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowOnboardingStep()
    {
        var hotkeys = ReadHotkeySummary();
        (string label, string title, string description, string primary) = _onboardingStep switch
        {
            1 => ("新手引导 · 第 1 步 / 共 3 步",
                  "接入一个翻译引擎",
                  "点「添加翻译引擎」接入服务；也可稍后允许内置免费引擎。",
                  "添加翻译引擎"),
            2 => ("新手引导 · 第 2 步 / 共 3 步",
                  "记住两个快捷键",
                  $"划词 {hotkeys.selection} · 截图 {hotkeys.screenshot}",
                  "下一步"),
            _ => ("新手引导 · 第 3 步 / 共 3 步",
                  "开始第一次翻译",
                  "输入文本按 Enter 翻译；历史与生词保存在本机。",
                  "完成引导"),
        };
        OnboardingStepLabel.Text = label;
        OnboardingTitle.Text = title;
        OnboardingDescription.Text = description;
        OnboardingPrimaryButton.Content = primary;
        OnboardingBanner.Visibility = Visibility.Visible;
    }

    private static (string selection, string screenshot) ReadHotkeySummary()
    {
        try
        {
            var settings = ShellSettingsStore.Load();
            return (settings.SelectionHotkey?.DisplayName ?? "Ctrl+Alt+W",
                    settings.ScreenshotHotkey?.DisplayName ?? "Ctrl+Alt+Space");
        }
        catch
        {
            return ("Ctrl+Alt+W", "Ctrl+Alt+Space");
        }
    }

    private void OnboardingPrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_onboardingStep)
        {
            case 1:
                // 复用空态卡完全相同的导航路径：只导航，不探测网络、不改授权。
                OpenAddEngine_Click(sender, e);
                AdvanceOnboarding();
                break;
            case 2:
                AdvanceOnboarding();
                break;
            default:
                CompleteOnboardingRun();
                break;
        }
    }

    private void OnboardingSkip_Click(object sender, RoutedEventArgs e) => CompleteOnboardingRun();

    private void AdvanceOnboarding()
    {
        if (_onboardingStep >= 3)
        {
            CompleteOnboardingRun();
            return;
        }
        _onboardingStep++;
        ShowOnboardingStep();
    }

    private void CompleteOnboardingRun()
    {
        EndOnboarding();
        CompleteOnboarding?.Invoke();
    }
}
