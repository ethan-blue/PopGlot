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
                    StatusText = "正在生成",
                    BadgeText = "正在生成",
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
                StatusText = "正在生成",
                BadgeText = "正在生成",
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
    }

    // ================= Public accessors for MainWindow =================

    internal ComboBox SourceLangCombo => TranslateSourceLang;
    internal ComboBox TargetLangCombo => TranslateTargetLang;
    internal TextBox InputBox => TranslateInput;
    internal TextBox ResultBox => TranslateResult;
    internal TextBox StreamResultBox => TranslateStreamResult;
    internal Border StreamIndicator => TranslateStreamIndicator;
    internal TextBlock ExplanationText => TranslateExplanation;
    internal ScrollViewer ExplanationBox => TranslateExplanationBox;
    internal TranslateUiState CurrentState => _currentState;
    internal long CurrentEpoch => _currentEpoch;

    /// <summary>Compact mode drops secondary hints; panes stay side by side.</summary>
    internal void SetCompact(bool compact)
    {
        TranslateShortcutHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
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
        TranslateResult.Visibility = state.IsFinalLayerVisible ? Visibility.Visible : Visibility.Collapsed;

        if (state.IsStreamLayerVisible)
        {
            TranslateStreamResult.Text = state.StreamText;
            TranslateStreamResult.CaretIndex = TranslateStreamResult.Text.Length;
            TranslateStreamResult.ScrollToEnd();
        }

        if (state.IsFinalLayerVisible)
        {
            TranslateResult.Text = state.FinalText;
        }

        TranslateEngineBadge.Text = state.BadgeText;
        TranslateStatus.Text = state.StatusText;

        TranslateResultSpeakButton.IsEnabled = state.AreResultActionsEnabled;
        TranslateResultCopyButton.IsEnabled = state.AreResultActionsEnabled;
        TranslateStarButton.IsEnabled = state.AreResultActionsEnabled;

        TranslateExplanation.Text = state.ExplanationText;
        TranslateExplanationBox.Visibility = state.IsExplanationVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TranslateInput_TextChanged(object sender, TextChangedEventArgs e) =>
        TranslateCounter.Text = $"{TranslateInput.Text.Length} 字符";

    // ================= Language pair =================

    private void SourceLang_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PersistLanguagePair();

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
            TranslateInput.Text = TranslateResult.Text;
            var epoch = Interlocked.Increment(ref _currentEpoch);
            ApplyState(TranslateUiState.Initial with { Epoch = epoch });
        }
    }

    // ================= Result actions =================

    private void TranslateSourceSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(TranslateInput.Text);

    private void TranslateResultSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(TranslateResult.Text);

    private static void SpeakOrStop(string text)
    {
        if (TtsService.IsSpeaking)
        {
            TtsService.Stop();
            return;
        }
        TtsService.Speak(text);
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
        var starred = _vocabulary.ToggleStar(
            source, translation,
            string.Empty, string.Empty,
            Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"));
        TranslateStatus.Text = starred ? "已加入生词本。" : "已从生词本移除。";
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
        if (await Helpers.CopyToClipboardAsync(TranslateResult.Text))
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
