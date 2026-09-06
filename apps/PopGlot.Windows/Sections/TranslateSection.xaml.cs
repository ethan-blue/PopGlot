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
    string BadgeText = "等待输入",
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
        BadgeText: "等待输入",
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
            BadgeText = "连接中",
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
                    ? current with { StatusText = "连接中", BadgeText = "连接中" }
                    : current,
            TranslationSessionStage.Streaming =>
                current with
                {
                    Phase = TranslateUiPhase.Streaming,
                    StatusText = "正在生成…",
                    BadgeText = "正在生成…",
                    IsStreamIndicatorVisible = true,
                    AreResultActionsEnabled = false,
                },
            TranslationSessionStage.Finalizing =>
                current with
                {
                    Phase = TranslateUiPhase.Finalizing,
                    StatusText = "正在整理",
                    BadgeText = "正在整理",
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
                BadgeText = "连接中",
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
                BadgeText = "正在生成…",
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
    private bool _languageChangeSuspended = true;
    private bool _isUnloaded;

    public TranslateSection()
    {
        InitializeComponent();
        TranslateSourceLang.ItemsSource = LanguageCatalog.Sources;
        TranslateTargetLang.ItemsSource = LanguageCatalog.Targets;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

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
        UpdateAutoDetectHint();
        ApplyState(_currentState);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloaded = true;
        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        _translateOperation = null;
    }

    internal void Initialize(TranslationCoordinator coordinator, VocabularyStore? vocabulary)
    {
        _coordinator = coordinator;
        _vocabulary = vocabulary;
        RefreshFreeEngineEntryVisibility();
    }

    // ================= Public accessors for MainWindow =================

    internal ComboBox SourceLangCombo => TranslateSourceLang;
    internal ComboBox TargetLangCombo => TranslateTargetLang;
    internal TextBox InputBox => TranslateInput;
    internal TextBox ResultBox => TranslateResult;
    internal TextBox StreamResultBox => TranslateStreamResult;
    internal StackPanel EmptyStateGuide => TranslateEmptyState;
    internal Button FreeEngineEntryButton => EnableFreeEngineButton;
    internal System.Windows.Controls.Grid PaneGrid => TranslatePaneGrid;
    internal Border StreamIndicator => TranslateStreamIndicator;
    internal TextBlock AutoDetectHint => TranslateAutoDetectHint;
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
    }

    private bool _stacked;

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
            EmptyStateHint.Text = "在上方输入或粘贴文本，按 Enter 翻译";
            return;
        }

        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        TranslatePaneGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        TranslatePaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Place(SourceLangBarCell, 0, 0);
        Place(SourceEditorCell, 1, 0);
        Place(SourceFooterCell, 2, 0);
        Place(AxisTopCell, 0, 1);
        System.Windows.Controls.Grid.SetRow(TranslateSwapButton, 0);
        System.Windows.Controls.Grid.SetColumn(TranslateSwapButton, 1);
        System.Windows.Controls.Grid.SetRowSpan(AxisDividerLine, 2);
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
        EmptyStateHint.Text = "在左侧输入或粘贴文本，按 Enter 翻译";

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
        if (System.Windows.Input.InputMethod.GetIsInputMethodEnabled(TranslateInput))
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
    }

    /// <summary>
    /// Fills the agreed demo error — text only, no request. The user's next
    /// action (Enter or the button) decides whether anything goes online.
    /// </summary>
    private void FillExample_Click(object sender, RoutedEventArgs e)
    {
        TranslateInput.Text = "FileNotFoundError: config.json not found";
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
        TranslateInput.Focus();
        TranslateStatus.Text = "已填入示例，按 Enter 或点「翻译」开始。";
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
            EnableFreeEngineButton.Visibility = Visibility.Collapsed;
            TranslateStatus.Text = "已允许内置公共翻译；首次翻译会连接 translate.googleapis.com。";
        }
        catch (Exception exception)
        {
            TranslateStatus.Text = $"保存授权失败：{exception.Message}";
        }
    }

    private void RefreshFreeEngineEntryVisibility()
    {
        var consent = ShellSettingsStore.Load().FreeEngineConsent;
        EnableFreeEngineButton.Visibility = consent == FreeEngineConsent.Unset
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TranslateInput_TextChanged(object sender, TextChangedEventArgs e) =>
        TranslateCounter.Text = $"{TranslateInput.Text.Length} 字符";

    // ================= Language pair =================

    private void SourceLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAutoDetectHint();
        PersistLanguagePair();
    }

    private void TargetLang_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PersistLanguagePair();

    private void UpdateAutoDetectHint()
    {
        var isAuto = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto) == LanguageCatalog.Auto;
        TranslateAutoDetectHint.Visibility = isAuto ? Visibility.Visible : Visibility.Collapsed;
    }

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
        UpdateAutoDetectHint();
        PersistLanguagePair();

        if (!string.IsNullOrWhiteSpace(TranslateResult.Text))
        {
            TranslateInput.Text = TranslateResult.Text;
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

    private void TranslateStar_Click(object sender, RoutedEventArgs e)
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
        var result = _vocabulary.ToggleStar(source, translation, string.Empty, string.Empty, sourceLang, targetLang);
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
        TranslateInput.Clear();
        TranslateInput.Focus();
    }

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null)
    {
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            TranslateInput.Text = initialText;
        }
        _languageChangeSuspended = true;
        try
        {
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
        UpdateAutoDetectHint();
        if (existingTranslation is not null)
        {
            var epoch = Interlocked.Increment(ref _currentEpoch);
            ApplyState(TranslateUiState.Initial with
            {
                Epoch = epoch,
                FinalText = existingTranslation,
                BadgeText = "已展开的译文",
                StatusText = "已从浮窗展开，未重新翻译。",
                AreResultActionsEnabled = true,
            });
        }
        TranslateInput.Focus();
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
    }
}
