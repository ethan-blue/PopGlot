using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal enum TranslationSessionState
{
    ReadingSelection,
    Capturing,
    Recognizing,
    Translating,
    Completed,
    Failed,
    Cancelled,
}

internal static class TranslationSessionStateText
{
    public static string Describe(TranslationSessionState state) => state switch
    {
        TranslationSessionState.ReadingSelection => "正在读取选中的文字",
        TranslationSessionState.Capturing => "正在准备截图",
        TranslationSessionState.Recognizing => "正在识别画面文字",
        TranslationSessionState.Translating => "正在翻译",
        TranslationSessionState.Completed => "翻译完成",
        TranslationSessionState.Failed => "需要处理",
        TranslationSessionState.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

/// <summary>
/// The floating result card shown for selection, screenshot, and typed input.
/// </summary>
public partial class TranslationPanelWindow : Window
{
    private readonly Rect _anchorPixels;
    private readonly VocabularyStore? _vocabulary;
    private readonly Func<ShellSettings> _shellSettings;
    private readonly Action? _openSettings;
    // The fifth argument is the session's REAL state: expanding must never
    // upgrade an unfinished panel result to a Completed one in the workbench.
    private readonly Action<string, string?, string?, string?, TranslationSessionState>? _openInMain;
    private readonly TranslationCoordinator _coordinator;
    private readonly TranslationPanelStreamGate _gate = new();
    private readonly EventHandler _themeChangedHandler;
    // The XAML binds both speak icons' Fill to the button Foreground.
    // Speaking replaces those expressions with dynamic AccentBrush
    // references, so the original bindings are captured here for the stop
    // path: ClearValue would erase the expressions for good and leave the
    // idle icons with no fill (the expression itself is the local value).
    private readonly System.Windows.Data.Binding? _sourceSpeakIconFillBinding;
    private readonly System.Windows.Data.Binding? _resultSpeakIconFillBinding;
    // Same contract for the copy-success feedback on both copy icons.
    private readonly System.Windows.Data.Binding? _sourceCopyIconFillBinding;
    private readonly System.Windows.Data.Binding? _resultCopyIconFillBinding;

    private CancellationTokenSource? _operation;
    private readonly SummaryLifecycleCoordinator _summaryLifecycle = new();
    private bool _holdingSummary;
    private Func<CancellationToken, long, Task>? _retry;
    private byte[]? _screenshot;
    private string _translation = string.Empty;
    private readonly ReadingModeState _reading = new();
    private string _translationNote = string.Empty;
    private string _sourceKind = "划词";
    private bool _userMoved;
    private Point? _lockedTopLeftPixels;
    // DPI transitions reposition exactly once, at ApplicationIdle: during the
    // WM_DPICHANGED transition itself the window's own scale is stale.
    private bool _dpiRepositionPending;
    private bool _languageChangeSuspended = true;
    private bool _readyForKeyboard;
    private bool _closing;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private int _openDropDowns;
    private long _inputAcquisitionMs;
    private long _lastStreamRenderTicks;
    // 风格预提示（免费引擎或截图视觉直译）：随本次操作传入或于截图操作
    // 起点就地设置，准备态状态文本不得瞬间覆盖它；真实流式文本或终态
    // 一旦落地即让位。
    private string? _pendingStyleNotice;

    internal TranslationPanelWindow(
        Rect anchorPixels,
        HistoryStore history,
        Func<ShellSettings> shellSettings,
        Action? openSettings = null,
        Action<string, string?, string?, string?, TranslationSessionState>? openInMain = null,
        VocabularyStore? vocabulary = null)
    {
        _anchorPixels = anchorPixels;
        _vocabulary = vocabulary;
        _shellSettings = shellSettings;
        _openSettings = openSettings;
        _openInMain = openInMain;
        _coordinator = new TranslationCoordinator(history, vocabulary);

        InitializeComponent();
        Ui.AttachCompositionTracker(SourceInputBox);
        _sourceSpeakIconFillBinding = SourceSpeakIcon.GetBindingExpression(System.Windows.Shapes.Shape.FillProperty)?.ParentBinding;
        _resultSpeakIconFillBinding = ResultSpeakIcon.GetBindingExpression(System.Windows.Shapes.Shape.FillProperty)?.ParentBinding;
        _sourceCopyIconFillBinding = SourceCopyIcon.GetBindingExpression(System.Windows.Shapes.Shape.FillProperty)?.ParentBinding;
        _resultCopyIconFillBinding = ResultCopyIcon.GetBindingExpression(System.Windows.Shapes.Shape.FillProperty)?.ParentBinding;

        SourceLangCombo.ItemsSource = LanguageCatalog.Sources;
        TargetLangCombo.ItemsSource = LanguageCatalog.Targets;
        var stored = CoreBridge.GetSettings();
        SourceLangCombo.SelectedItem = LanguageCatalog.ResolveSource(stored.SourceLanguage);
        TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget(stored.TargetLanguage);
        _languageChangeSuspended = false;

        TrackDropDown(SourceLangCombo);
        TrackDropDown(TargetLangCombo);
        IsVisibleChanged += OnIsVisibleChanged;

        TtsService.SpeakingStateChanged += OnTtsSpeakingStateChanged;

        // Opaque window now: DWM rounds the corners and draws the shadow,
        // and the immersive-dark attribute keeps the frame theme-correct.
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

        SourceInitialized += (_, _) => PositionNearAnchor();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionNearAnchor();
        Closed += (_, _) =>
        {
            _closing = true;
            ThemeService.ThemeChanged -= _themeChangedHandler;
            TtsService.SpeakingStateChanged -= OnTtsSpeakingStateChanged;
            CancelOperation();
        };

        RenderIdle();
        RefreshStyleSelector();
    }


    private void FitInitialHeightToSource(string? source)
    {
        if (_userMoved)
        {
            return;
        }

        // Every tier sits at or above the window MinHeight (380): the old
        // 320/360 tiers were below the fixed chrome (header, source area,
        // engine bar, footer) plus the result floor, so the bottom status
        // bar was clipped exactly on short sources. The top tier keeps a
        // full 80 DIP explanation row plus the result floor visible.
        var length = source?.Trim().Length ?? 0;
        Height = length switch
        {
            <= 80 => 380,
            <= 420 => 420,
            _ => 460,
        };
    }

    private void OnTtsSpeakingStateChanged(object? sender, bool isSpeaking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isSpeaking)
            {
                // Dynamic reference: a static FindResource brush goes stale
                // when the theme flips while speech is running.
                SourceSpeakIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
                ResultSpeakIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
            }
            else
            {
                // Re-apply the captured Foreground bindings instead of
                // ClearValue: the binding expressions ARE the local values,
                // so clearing them would leave the idle icons with no fill.
                if (_sourceSpeakIconFillBinding is not null)
                {
                    SourceSpeakIcon.SetBinding(System.Windows.Shapes.Shape.FillProperty, _sourceSpeakIconFillBinding);
                }
                if (_resultSpeakIconFillBinding is not null)
                {
                    ResultSpeakIcon.SetBinding(System.Windows.Shapes.Shape.FillProperty, _resultSpeakIconFillBinding);
                }
            }
            SourceSpeakBtn.ToolTip = isSpeaking ? "停止朗读" : "朗读原文";
            ResultSpeakBtn.ToolTip = isSpeaking ? "停止朗读" : "朗读译文";
            // The accessibility name follows the ToolTip so screen readers
            // announce the action the click will actually perform right now.
            System.Windows.Automation.AutomationProperties.SetName(SourceSpeakBtn, (string)SourceSpeakBtn.ToolTip);
            System.Windows.Automation.AutomationProperties.SetName(ResultSpeakBtn, (string)ResultSpeakBtn.ToolTip);
        });
    }

    private string SourceLanguage =>
        (SourceLangCombo.SelectedItem as LanguageOption)?.Tag ?? LanguageCatalog.Auto;

    private string TargetLanguage =>
        (TargetLangCombo.SelectedItem as LanguageOption)?.Tag ?? "zh-CN";

    internal Border StreamIndicatorPill => StreamIndicator;
    internal TextBlock StatusTextBlock => StatusText;
    internal TextBox StreamTextBox => TranslationTextBox;
    internal RichTextBox FinalRichBox => TranslationRichBox;
    internal void AdoptSummaryOperation(CancellationTokenSource cts) => _summaryLifecycle.AdoptOperation(cts);

    // ================= Entry points =================

    internal async Task StartSelectionAsync(ClipboardSelectionService selectionService, nint targetWindow = 0)
    {
        ArgumentNullException.ThrowIfNull(selectionService);
        _sourceKind = "划词";
        SourceKindLabel.Text = "· 划词";
        _screenshot = null;
        await RunOperationAsync(async (cancellation, epoch) =>
        {
            RenderState(TranslationSessionState.ReadingSelection);
            SourceLabel.Text = "所选文字";
            var inputTimer = Stopwatch.StartNew();
            var source = await selectionService.ReadSelectionAsync(cancellation, targetWindow);
            inputTimer.Stop();
            _inputAcquisitionMs = inputTimer.ElapsedMilliseconds;
            SourceInputBox.Text = source;
            FitInitialHeightToSource(source);

            // Only now take focus: activating earlier would have moved the
            // foreground window away from the app we just sent Ctrl+C to.
            AllowKeyboardInteraction();

            _retry = (token, ep) => TranslateTextAsync(source, token, ep);
            await TranslateTextAsync(source, cancellation, epoch);
        });
    }

    internal async Task StartScreenshotAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _sourceKind = "截图";
        SourceKindLabel.Text = "· 截图";
        _screenshot = image;
        SourceLabel.Text = "画面文字";
        AllowKeyboardInteraction();
        _retry = (token, ep) => TranslateScreenshotAsync(image, token, ep);
        await RunOperationAsync((cancellation, epoch) => TranslateScreenshotAsync(image, cancellation, epoch));
    }

    internal async Task StartScreenshotOcrAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _sourceKind = "取字";
        SourceKindLabel.Text = "· 截图取字";
        _screenshot = image;
        SourceLabel.Text = "画面提取文字";
        AllowKeyboardInteraction();
        _retry = (token, ep) => RecognizeOcrAsync(image, token, ep);
        await RunOperationAsync((cancellation, epoch) => RecognizeOcrAsync(image, cancellation, epoch));
    }

    private async Task RecognizeOcrAsync(byte[] image, CancellationToken cancellationToken, long epoch)
    {
        RenderState(TranslationSessionState.Recognizing);
        SetResultActionsEnabled(false);
        if (!WindowsOcrService.IsSupported)
        {
            throw new InvalidOperationException("系统未安装 Windows OCR 语言包，无法进行离线文字提取。");
        }

        var sourceLang = SourceLanguage == LanguageCatalog.Auto ? "zh-Hans-CN" : SourceLanguage;
        var recognized = await WindowsOcrService.RecognizeTextAsync(image, sourceLang);
        if (string.IsNullOrWhiteSpace(recognized))
        {
            throw new InvalidOperationException("未能在所选区域识别出有效文字。");
        }

        // The recognized text is the original text: it is shown, stored and
        // auto-copied verbatim. Visual Pangu spacing must never be written
        // back into the original (it used to insert spaces inside paths).
        SourceInputBox.Text = recognized;
        _gate.OnCompleted(recognized);
        SetTranslationContent(recognized, isMarkdown: false);
        SetResultActionsEnabled(true);
        await TrySetClipboardAsync(recognized);

        RenderState(TranslationSessionState.Completed);
        EngineBadge.Text = "离线 OCR 取字";
        SetBadgeTone(failed: false);
        StatusText.Text = "已提取文字并复制";
        SetRouteText($"{recognized.Length} 字符");
    }

    internal async Task StartTextAsync(string text)
    {
        _inputAcquisitionMs = 0;
        _sourceKind = "输入";
        SourceKindLabel.Text = "· 输入";
        SourceLabel.Text = "原文";
        SourceInputBox.Text = text ?? string.Empty;
        FitInitialHeightToSource(text);
        _screenshot = null;
        AllowKeyboardInteraction();

        if (string.IsNullOrWhiteSpace(text))
        {
            _gate.ResetToIdle();
            RenderIdle();
            SourceInputBox.Focus();
            return;
        }
        _retry = (token, ep) => TranslateTextAsync(text, token, ep);
        await RunOperationAsync((cancellation, epoch) => TranslateTextAsync(text, cancellation, epoch));
    }

    internal void ShowImmediateFailure(string message)
    {
        AllowKeyboardInteraction();
        RenderFailure(message);
        SourceInputBox.Focus();
        SourceInputBox.SelectAll();
    }

    // ================= Operation plumbing =================

    private async Task RunOperationAsync(Func<CancellationToken, long, Task> operation, string? styleNotice = null)
    {
        // Every operation owns its notice: the text-entry path passes a
        // free-engine pre-flight notice, and the screenshot path states the
        // vision-direct style fact for itself (set inside
        // TranslateScreenshotAsync so start, retry and language-change reruns
        // all carry it). Entry points with nothing at stake start clean.
        _pendingStyleNotice = styleNotice;
        CancelOperation();
        var (epoch, _) = _gate.BeginNewOperation();
        // A new operation starts from a clean slate: the previous attempt's
        // text — including a previously rendered failure message — must never
        // resurface as this attempt's "partial" content when this attempt
        // also fails or is cancelled before anything streams.
        _translation = string.Empty;
        var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        try
        {
            await operation(cancellation.Token, epoch);
        }
        catch (OperationCanceledException)
        {
            if (epoch == _gate.CurrentEpoch)
            {
                _gate.OnCancelled(_translation);
                if (!_closing && IsVisible)
                {
                    if (_gate.HasPartialText)
                    {
                        RenderCancelledWithPartial(_translation);
                    }
                    else
                    {
                        RenderCancelledWithoutPartial();
                    }
                    if (!_holdingSummary)
                    {
                        Progress.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!_closing)
            {
                _gate.OnFailed(exception.Message, _translation);
                if (_gate.HasPartialText)
                {
                    RenderFailedWithPartial(_translation, exception.Message);
                }
                else
                {
                    RenderFailure(exception.Message);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_operation, cancellation))
            {
                _operation = null;
            }
            cancellation.Dispose();
        }
    }

    private void ShowTranslation_Click(object sender, RoutedEventArgs e) => ShowStoredTranslation();

    private void ShowStoredTranslation()
    {
        _holdingSummary = false;
        _reading.ShowTranslation();
        ShowTranslationChoice.IsChecked = true;
        if (_operation is { IsCancellationRequested: false })
        {
            TranslationRichBox.Visibility = Visibility.Collapsed;
            TranslationTextBox.Visibility = string.IsNullOrEmpty(_translation)
                ? Visibility.Collapsed
                : Visibility.Visible;
            TranslationTextBox.Text = _translation;
            Progress.Visibility = Visibility.Visible;
            StreamIndicator.Visibility = string.IsNullOrEmpty(_translation)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ResultSkeleton.Visibility = string.IsNullOrEmpty(_translation)
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = "正在翻译";
            return;
        }

        SetTranslationContent(_translation);
        ExplanationText.Text = _translationNote;
        ExplanationBox.Visibility = string.IsNullOrWhiteSpace(_translationNote)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.Text = string.IsNullOrWhiteSpace(_translation) ? "还没有译文" : "译文";
    }

    private async void ShowSummary_Click(object sender, RoutedEventArgs e) =>
        await TriggerPanelSummaryAsync();

    internal async Task TriggerPanelSummaryAsync()
    {
        var source = SourceInputBox.Text.Trim();
        if (source.Length == 0)
        {
            StatusText.Text = ReadingRequestCopy.NeedSource;
            ShowTranslationChoice.IsChecked = true;
            return;
        }

        // R03: Probe capability before issuing request or throwing exceptions
        var capability = RouteCapabilityService.EvaluateCurrent();
        if (capability.State is RouteCapabilityState.Unsupported or RouteCapabilityState.NeedsConfiguration)
        {
            StatusText.Text = capability.Reason;
            ShowTranslationChoice.IsChecked = true;
            return;
        }

        await _summaryLifecycle.ExecuteSummaryAsync(
            _coordinator,
            _reading,
            source,
            SourceLanguage,
            TargetLanguage,
            isHoldingSummary: () => _holdingSummary,
            getCurrentSource: () => SourceInputBox.Text,
            getCurrentTargetLanguage: () => TargetLanguage,
            onStarting: () =>
            {
                BeginPanelSummaryReading();
                StatusText.Text = "正在整理要点（独立模型请求，可能产生额外服务费用）…";
                ShowSummaryChoice.IsEnabled = false;
            },
            onSuccess: outcome =>
            {
                if (_holdingSummary)
                {
                    ResultSkeleton.Visibility = Visibility.Collapsed;
                    Progress.Visibility = Visibility.Collapsed;
                }
                ShowSummaryChoice.IsEnabled = true;

                if (outcome.ElapsedMs == 0 && string.IsNullOrEmpty(outcome.EngineLabel))
                {
                    PaintPanelSummary(outcome.SummaryText, outcome.Notes);
                    StatusText.Text = ReadingRequestCopy.Finished(0);
                    return;
                }

                if (!outcome.IsCurrent)
                {
                    if (_holdingSummary)
                    {
                        ShowStoredTranslation();
                    }
                    return;
                }

                PaintPanelSummary(outcome.SummaryText, outcome.Notes);
                StatusText.Text = ReadingRequestCopy.Finished(outcome.ElapsedMs);
                RouteText.Text = outcome.EngineLabel;
            },
            onError: error =>
            {
                if (_holdingSummary)
                {
                    ResultSkeleton.Visibility = Visibility.Collapsed;
                    Progress.Visibility = Visibility.Collapsed;
                }
                ShowSummaryChoice.IsEnabled = true;

                if (error.IsCurrent)
                {
                    ShowStoredTranslation();
                    StatusText.Text = error.Message;
                }
            });
    }

    private void BeginPanelSummaryReading()
    {
        _reading.CaptureTranslation(_translation, _translationNote);
        _holdingSummary = true;
        ShowSummaryChoice.IsChecked = true;
        StatusText.Text = ReadingRequestCopy.WhileRequesting(
            _operation is { IsCancellationRequested: false });
        ResultSkeleton.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
    }

    private void PaintPanelSummary(string text, string note)
    {
        _holdingSummary = true;
        ShowSummaryChoice.IsChecked = true;
        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        StreamIndicator.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        TranslationTextBox.Visibility = Visibility.Visible;
        TranslationTextBox.Text = text;
        ExplanationText.Text = note;
        ExplanationBox.Visibility = string.IsNullOrWhiteSpace(note)
            ? Visibility.Collapsed
            : Visibility.Visible;
        // 「打开设置」只属于错误说明；此前失败残留的按钮不能跟着
        // 出现在要点视图的说明行旁。
        ErrorSettingsButton.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        StatusText.Text = ReadingRequestCopy.SummaryReadyHint;
        ResultCopyBtn.IsEnabled = !string.IsNullOrWhiteSpace(text);
        ResultSpeakBtn.IsEnabled = !string.IsNullOrWhiteSpace(text);
    }

    private async Task TranslateTextAsync(string source, CancellationToken cancellation, long epoch)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        RenderPreparingState("正在翻译");
        TranslationTextBox.Clear();
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "正在翻译…");
        ExplanationBox.Visibility = Visibility.Collapsed;
        cancellation.ThrowIfCancellationRequested();

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (!_gate.ShouldAcceptUpdate(update.Epoch, _closing, IsLoaded || IsVisible))
            {
                return;
            }
            OnStreamUpdate(update);
        });

        var session = await _coordinator.TranslateTextAsync(
            source,
            SourceLanguage,
            TargetLanguage,
            _sourceKind == "划词" ? TranslationInputSource.Selection : TranslationInputSource.Manual,
            cancellationToken: cancellation,
            onStageChanged: stage =>
            {
                if (!_gate.ShouldAcceptUpdate(epoch, _closing, IsLoaded || IsVisible)) return;
                _gate.OnStageChanged(stage);
                OnStageChanged(stage);
            },
            progress: progress,
            epoch: epoch);

        if (!_gate.ShouldAcceptUpdate(epoch, _closing, IsLoaded || IsVisible))
        {
            return;
        }

        await HandleSessionResultAsync(source, session, epoch, pipelineNote: string.Empty);
    }

    private async Task TranslateScreenshotAsync(byte[] image, CancellationToken cancellation, long epoch)
    {
        // Honesty before the request goes out: when the resolved screenshot
        // route is vision-direct, the active TEXT style will not take part
        // (0.1.6 has no vision prompt support). Stated up front, rides above
        // the preparing statuses, and never blocks, reroutes or re-sends
        // anything. OCR/VisionOcr pipelines translate through the text
        // provider and honour the style, so they stay silent here.
        _pendingStyleNotice = TranslationStyleMenu.PreScreenshotNotice();
        RenderPreparingState("正在识别画面文字");
        SourceInputBox.Clear();
        SourceInputBox.SetValue(Ui.PlaceholderProperty, $"正在识别截图画面…（{image.Length / 1024.0:0.#} KiB）");
        TranslationTextBox.Clear();
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "等待画面文字识别后翻译…");
        ExplanationBox.Visibility = Visibility.Collapsed;
        cancellation.ThrowIfCancellationRequested();

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (!_gate.ShouldAcceptUpdate(update.Epoch, _closing, IsLoaded || IsVisible))
            {
                return;
            }
            OnStreamUpdate(update);
        });

        var session = await _coordinator.TranslateScreenshotAsync(
            image,
            SourceLanguage,
            TargetLanguage,
            cancellationToken: cancellation,
            onStageChanged: stage =>
            {
                if (!_gate.ShouldAcceptUpdate(epoch, _closing, IsLoaded || IsVisible)) return;
                _gate.OnStageChanged(stage);
                OnStageChanged(stage);
            },
            progress: progress,
            epoch: epoch);

        if (!_gate.ShouldAcceptUpdate(epoch, _closing, IsLoaded || IsVisible))
        {
            return;
        }

        SourceInputBox.SetValue(Ui.PlaceholderProperty, "输入文本，Enter 翻译，Shift+Enter 换行");
        var recognized = string.IsNullOrWhiteSpace(session.Transcription)
            ? (!string.IsNullOrWhiteSpace(session.SourceText) ? session.SourceText : "（模型未回传识别文本）")
            : session.Transcription;
        SourceInputBox.Text = recognized;

        await HandleSessionResultAsync(recognized, session, epoch, pipelineNote: session.RoutingReason ?? string.Empty);
    }

    private void OnStreamUpdate(TranslationStreamUpdate update)
    {
        if (!_gate.ApplyUpdate(update))
        {
            return;
        }

        if (update.Kind == TranslationStreamUpdateKind.Reset)
        {
            _lastStreamRenderTicks = 0;
            _translation = string.Empty;
            _reading.CaptureTranslation(_translation, _translationNote);
            if (_holdingSummary)
            {
                return;
            }
            TranslationTextBox.Text = string.Empty;
            ResultSkeleton.Visibility = Visibility.Visible;
            TranslationTextBox.Visibility = Visibility.Collapsed;
            TranslationRichBox.Visibility = Visibility.Collapsed;
            StreamIndicator.Visibility = Visibility.Collapsed;
            SetResultActionsEnabled(false);
            return;
        }

        if (update.Kind == TranslationStreamUpdateKind.Delta)
        {
            _translation = update.AccumulatedText ?? string.Empty;
            _reading.CaptureTranslation(_translation, _translationNote);
            if (_holdingSummary)
            {
                return;
            }
            if (ResultSkeleton.Visibility == Visibility.Visible)
            {
                ResultSkeleton.Visibility = Visibility.Collapsed;
                TranslationTextBox.Visibility = Visibility.Visible;
                TranslationRichBox.Visibility = Visibility.Collapsed;
            }
            StreamIndicator.Visibility = Visibility.Visible;

            // PERF-LIST-01: 60ms throttle on intermediate text pumps to avoid layout thrashing.
            // When the final completion signal arrives, HandleSessionResultAsync forces a full final render.
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(_lastStreamRenderTicks, now).TotalMilliseconds;
            if (_lastStreamRenderTicks != 0 && elapsedMs < 60)
            {
                return;
            }
            _lastStreamRenderTicks = now;

            // Stick-to-bottom: follow the stream only while the reader sits at
            // the bottom, so scrolling up to re-read is never overridden.
            var stickToBottom = Ui.IsScrolledToBottom(Ui.FindScrollViewer(TranslationTextBox));
            // Append only the new suffix: reassigning Text wholesale on every
            // pump tick forces a full re-layout and makes the bottom edge
            // strobe while the window hugs its growing content.
            if (_translation.StartsWith(TranslationTextBox.Text, StringComparison.Ordinal))
            {
                TranslationTextBox.AppendText(_translation[TranslationTextBox.Text.Length..]);
            }
            else
            {
                TranslationTextBox.Text = _translation;
            }
            if (stickToBottom)
            {
                TranslationTextBox.ScrollToEnd();
            }
            SetResultActionsEnabled(false);
            // 真实文本已到达：预提示完成使命，恢复常规生成状态。
            _pendingStyleNotice = null;
            StatusText.Text = "正在生成…";
        }
    }

    private void OnStageChanged(TranslationSessionStage stage)
    {
        if (_holdingSummary)
        {
            return;
        }
        // 预提示待决期间，准备态文本（选择模型/翻译/生成…）不覆盖它。
        switch (stage)
        {
            case TranslationSessionStage.OcrRunning:
                // OcrRunning 之下若还挂着视觉直译预提示，只可能是视觉线路
                // 已回退到本地 OCR：此后由文本模型翻译，风格必然生效，
                // 「本次不会应用所选风格」就成了假话。立即撤下提示并回到
                // 诚实的处理中状态，终态文本也不得再被它顶替。
                _pendingStyleNotice = null;
                StatusText.Text = "正在识别画面文字";
                StreamIndicator.Visibility = Visibility.Collapsed;
                break;
            case TranslationSessionStage.Routing:
                StatusText.Text = _pendingStyleNotice ?? "正在选择最佳模型";
                StreamIndicator.Visibility = Visibility.Collapsed;
                break;
            case TranslationSessionStage.Translating:
                StatusText.Text = _pendingStyleNotice ?? "正在翻译";
                StreamIndicator.Visibility = Visibility.Collapsed;
                break;
            case TranslationSessionStage.Streaming:
                StatusText.Text = _pendingStyleNotice ?? "正在生成…";
                StreamIndicator.Visibility = Visibility.Visible;
                break;
            case TranslationSessionStage.Finalizing:
                StatusText.Text = _pendingStyleNotice ?? "正在整理译文…";
                StreamIndicator.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private async Task HandleSessionResultAsync(string source, TranslationSession session, long epoch, string pipelineNote)
    {
        // C10 结构化时间点：划词读取耗时随会话记录；绘制滞后从本方法
        // （协调器返回、UI 接管）起算，到终态渲染完成为止。
        session.Timing = session.Timing with
        {
            SelectionReadMs = (ulong)Math.Max(0, _inputAcquisitionMs),
        };
        var paintLagTimer = Stopwatch.StartNew();
        if (session.Stage == TranslationSessionStage.Cancelled)
        {
            if (!_holdingSummary)
            {
                Progress.Visibility = Visibility.Collapsed;
            }
            _gate.OnCancelled(session.TranslatedText);
            if (_gate.HasPartialText)
            {
                RenderCancelledWithPartial(session.TranslatedText);
            }
            else
            {
                RenderCancelledWithoutPartial();
            }
            return;
        }

        if (!session.IsCleanCompletion || session.Stage == TranslationSessionStage.Failed)
        {
            if (!_holdingSummary)
            {
                Progress.Visibility = Visibility.Collapsed;
            }
            // A Partial keeps its visible text but lands here on purpose: the
            // gate records FailedWithPartial so result actions, auto-copy and
            // starring stay blocked while the retained text stays readable.
            var message = session.Error?.Message
                ?? (session.Stage == TranslationSessionStage.Partial
                    ? "译文不完整，未执行自动复制等动作"
                    : "翻译未完成");
            var suggestion = session.Error?.ActionableSuggestion;
            var fullMessage = string.IsNullOrWhiteSpace(suggestion) ? message : $"{message} {suggestion}".Trim();
            _gate.OnFailed(fullMessage, session.TranslatedText);
            if (_gate.HasPartialText)
            {
                RenderFailedWithPartial(session.TranslatedText, fullMessage);
            }
            else
            {
                RenderFailure(fullMessage);
            }
            return;
        }

        _gate.OnCompleted(session.TranslatedText);
        await RenderFinalSuccessAsync(source, session, pipelineNote);
        // C10：终态已渲染——滞后期结束。写在此处（方法尾部）即"painted"时刻。
        session.Timing = session.Timing with
        {
            PaintedLagMs = (ulong)paintLagTimer.ElapsedMilliseconds,
        };
    }

    // ================= Rendering =================

    private void RenderPreparingState(string status)
    {
        _lastStreamRenderTicks = 0;
        _holdingSummary = false;
        _reading.ShowTranslation();
        ShowTranslationChoice.IsChecked = true;
        // 预提示待决时压过「正在翻译/正在识别画面文字」等准备态。
        StatusText.Text = _pendingStyleNotice ?? status;
        Progress.Visibility = Visibility.Visible;
        ResultSkeleton.Visibility = Visibility.Visible;
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        TranslationTextBox.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        StreamIndicator.Visibility = Visibility.Collapsed;
        ExplanationBox.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
        EngineBadge.Text = "翻译中";
        SetBadgeTone(failed: false);
        StatusDot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        SetResultActionsEnabled(false);
        // 准备态开始：空闲快捷键提示立即让位给真实状态。
        SetRouteText(string.Empty);
    }

    private void RenderCancelledWithPartial(string partialText)
    {
        _translation = partialText;
        _reading.CaptureTranslation(_translation, _translationNote);
        if (_holdingSummary)
        {
            return;
        }
        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        TranslationTextBox.Visibility = Visibility.Visible;
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        TranslationTextBox.Text = partialText;
        StreamIndicator.Visibility = Visibility.Collapsed;
        SetResultActionsEnabled(false);
        ResultCopyBtn.IsEnabled = !string.IsNullOrWhiteSpace(partialText);

        WarningText.Text = "已取消，内容不完整";
        WarningBox.Visibility = Visibility.Visible;
        EngineBadge.Text = "已取消";
        EngineBadge.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        StatusDot.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
        StatusText.Text = "翻译已取消 · 内容不完整";
        SetRouteText("已中断");
        ExplanationBox.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
    }

    private void RenderCancelledWithoutPartial()
    {
        _translation = string.Empty;
        _reading.CaptureTranslation(_translation, _translationNote);
        if (_holdingSummary)
        {
            return;
        }
        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        // 无 partial 的取消收起空结果区：空框与“译文将显示在这里”占位符是
        // 对一次没有发生的翻译的空承诺；终态只保留在徽标与状态行上。
        TranslationTextBox.Visibility = Visibility.Collapsed;
        TranslationTextBox.Text = string.Empty;
        StreamIndicator.Visibility = Visibility.Collapsed;
        SetResultActionsEnabled(false);

        WarningBox.Visibility = Visibility.Collapsed;
        EngineBadge.Text = "已取消";
        EngineBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        StatusDot.SetResourceReference(Border.BackgroundProperty, "TextTertiaryBrush");
        StatusText.Text = "翻译已取消";
        SetRouteText(string.Empty);
        ExplanationBox.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
    }

    private void RenderFailedWithPartial(string partialText, string errorMessage)
    {
        _translation = partialText;
        _translationNote = errorMessage;
        _reading.CaptureTranslation(_translation, _translationNote);
        if (_holdingSummary)
        {
            return;
        }
        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        TranslationTextBox.Visibility = Visibility.Visible;
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        TranslationTextBox.Text = partialText;
        StreamIndicator.Visibility = Visibility.Collapsed;
        SetResultActionsEnabled(false);
        ResultCopyBtn.IsEnabled = !string.IsNullOrWhiteSpace(partialText);

        WarningText.Text = "生成中断，内容不完整";
        WarningBox.Visibility = Visibility.Visible;
        ExplanationText.Text = errorMessage;
        ExplanationText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        ExplanationText.Visibility = Visibility.Visible;
        ExplanationBox.Visibility = Visibility.Visible;
        ErrorSettingsButton.Visibility = Visibility.Visible;
        EngineBadge.Text = "生成中断";
        EngineBadge.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        StatusDot.SetResourceReference(Border.BackgroundProperty, "DangerBrush");
        StatusText.Text = "生成中断 · 内容不完整";
        SetRouteText("可检查网络或设置后重试");
        TermsList.Visibility = Visibility.Collapsed;
        PhoneticText.Visibility = Visibility.Collapsed;
    }

    private void RenderIdle()
    {
        StatusText.Text = "输入后按 Enter 翻译";
        Progress.Visibility = Visibility.Collapsed;
        // 空闲态的低对比一次性快捷键提示：进入准备/流式后立即让位给真实状态。
        SetRouteText("Enter 翻译 · Shift+Enter 换行 · Ctrl+R 重试 · Esc 关闭", idleHint: true);
        StatusDot.SetResourceReference(Border.BackgroundProperty, "TextTertiaryBrush");
        ResultSkeleton.Visibility = Visibility.Collapsed;
        TranslationRichBox.Visibility = Visibility.Collapsed;
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        TranslationTextBox.Visibility = Visibility.Visible;
        TranslationTextBox.Text = string.Empty;
        StreamIndicator.Visibility = Visibility.Collapsed;
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "译文将显示在这里…");
        SetResultActionsEnabled(false);
    }

    private void RenderState(TranslationSessionState state)
    {
        StatusText.Text = TranslationSessionStateText.Describe(state);
        var busy = state is TranslationSessionState.ReadingSelection
            or TranslationSessionState.Capturing
            or TranslationSessionState.Recognizing
            or TranslationSessionState.Translating;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        // The result area shows a skeleton instead of an empty promise while
        // the request is in flight.
        ResultSkeleton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            // Never layer placeholder text or a previous result under the
            // skeleton bars; that caused the visible strike-through/covered
            // glyphs in the selection popup.
            TranslationTextBox.Visibility = Visibility.Collapsed;
            TranslationRichBox.Visibility = Visibility.Collapsed;
            StreamIndicator.Visibility = Visibility.Collapsed;
            // 流式/准备期间空闲快捷键提示必须让位（清空右槽）。
            SetRouteText(string.Empty);
        }

        // Dynamic references: the dot's tone must follow the live theme — a
        // static FindResource brush keeps the color of whatever theme was
        // active when the state rendered.
        StatusDot.SetResourceReference(
            Border.BackgroundProperty,
            state switch
            {
                TranslationSessionState.Failed => "DangerBrush",
                TranslationSessionState.Cancelled => "TextTertiaryBrush",
                _ => "AccentBrush",
            });
    }

    private void SetTranslationContent(string text, bool isMarkdown = true)
    {
        _translation = text;
        TranslationTextBox.Text = text;
        StreamIndicator.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslationRichBox.Document.Blocks.Clear();
            TranslationRichBox.Visibility = Visibility.Collapsed;
            TranslationTextBox.Visibility = Visibility.Visible;
        }
        else if (isMarkdown)
        {
            try
            {
                MarkdownPresenter.RenderToFlowDocument(TranslationRichBox.Document, text, Application.Current?.Resources ?? Resources);
                TranslationRichBox.Visibility = Visibility.Visible;
                TranslationTextBox.Visibility = Visibility.Collapsed;
            }
            catch
            {
                TranslationRichBox.Visibility = Visibility.Collapsed;
                TranslationTextBox.Visibility = Visibility.Visible;
            }
        }
        else
        {
            TranslationRichBox.Visibility = Visibility.Collapsed;
            TranslationTextBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The translation finished while the summary is on screen. Keep the
    /// clipboard and star side effects, and leave the summary text where it is.
    /// </summary>
    private async Task FinishTranslationBehindSummaryAsync(string source)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_closing && _vocabulary is not null)
            {
                var starred = _vocabulary.IsStarred(source, SourceLanguage, TargetLanguage);
                UpdateStarIcon(starred);
            }
        });
        var settings = _shellSettings();
        if (_gate.ShouldTriggerAutoCopy(settings.CopyTranslationAutomatically))
        {
            var clean = MarkdownPresenter.ToPlainText(_translation);
            await TrySetClipboardAsync(clean);
        }
        if (StatusText.Text == ReadingRequestCopy.SummaryWhileTranslating)
        {
            StatusText.Text = ReadingRequestCopy.SummaryKeptTranslation;
        }
    }

    private async Task RenderFinalSuccessAsync(string source, TranslationSession session, string pipelineNote)
    {
        // Only clean completions reach this method (HandleSessionResultAsync
        // routes everything else to the partial/failure renderers).
        _translation = session.TranslatedText;
        _translationNote = session.Explanation;
        _reading.CaptureTranslation(session.TranslatedText, session.Explanation);
        if (_holdingSummary)
        {
            await FinishTranslationBehindSummaryAsync(source);
            return;
        }
        _reading.ShowTranslation();
        ShowTranslationChoice.IsChecked = true;
        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        StreamIndicator.Visibility = Visibility.Collapsed;
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        ExplanationText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        ErrorSettingsButton.Visibility = Visibility.Collapsed;

        try
        {
            MarkdownPresenter.RenderToFlowDocument(TranslationRichBox.Document, session.TranslatedText, Application.Current?.Resources ?? Resources,
                resultActionsEnabled: session.IsCleanCompletion);
            TranslationRichBox.Visibility = Visibility.Visible;
            TranslationTextBox.Visibility = Visibility.Collapsed;
        }
        catch
        {
            TranslationTextBox.Text = session.TranslatedText;
            TranslationTextBox.Visibility = Visibility.Visible;
            TranslationRichBox.Visibility = Visibility.Collapsed;
        }

        SetResultActionsEnabled(true);

        // Phonetic is romanization of the source; it is a distinct field from
        // Transcription so a screenshot's OCR text is never rendered as one.
        ShowIfPresent(PhoneticText, session.Phonetic, value => $"[{value}]");
        ShowIfPresent(ExplanationText, session.Explanation, value => value, ExplanationBox);

        TermsList.ItemsSource = session.ProtectedTerms;
        TermsList.Visibility = session.ProtectedTerms.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        var warnings = session.Warnings;
        WarningText.Text = warnings.Count == 0 ? string.Empty : string.Join("\n", warnings);
        WarningBox.Visibility = warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        EngineBadge.Text = session.PipelineLabel ?? "翻译完成";
        EngineBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        StatusDot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        // 0.1.6 页脚减法：只保留用户需要的事实——管线名、截图是否上传
        // （隐私事实）与总用时（秒级）。取词/OCR/路由/网络等内部毫秒拆分
        // 不再展示；Timing 数据结构本身保持不变。
        var totalMs = session.Timing.TotalElapsedMs + (ulong)Math.Max(0, _inputAcquisitionMs);
        var footerParts = new List<string> { session.PipelineLabel ?? "翻译" };
        if (session.InputSource == TranslationInputSource.Screenshot)
        {
            footerParts.Add(session.ImageUploaded ? "图片已进入视觉请求" : "图片未上传");
        }
        footerParts.Add(TranslationElapsedText.ForMilliseconds(totalMs));
        SetRouteText(string.Join(" · ", footerParts));
        StatusText.Text = string.IsNullOrWhiteSpace(pipelineNote)
            ? TranslationSessionStateText.Describe(TranslationSessionState.Completed)
            : pipelineNote;

        var settings = _shellSettings();
        // PERF-HOTKEY-02: Move vocabulary star check out of the critical first-frame rendering path
        // to avoid holding lock(_gate) during initial render.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_closing && _vocabulary is not null)
            {
                var starred = _vocabulary.IsStarred(source, SourceLanguage, TargetLanguage);
                UpdateStarIcon(starred);
            }
        });
        if (_gate.ShouldTriggerAutoCopy(settings.CopyTranslationAutomatically))
        {
            var clean = MarkdownPresenter.ToPlainText(_translation);
            if (await TrySetClipboardAsync(clean))
            {
                StatusText.Text = "翻译完成 · 已自动复制译文";
            }
        }

        // Honest provenance, typed not string-matched: the free engine (plain
        // text or OCR + free text → PromptSupport.NotSupported) and the
        // vision-direct route (no text stage → NotApplicable) never received
        // the active TEXT style. 0.1.6 states both with one shared short
        // status per kind, decided by the TYPED support fact — never by
        // matching the PipelineLabel display string.
        if (session.PromptSupport is TranslationPromptSupport.NotSupported or TranslationPromptSupport.NotApplicable &&
            TranslationStyleMenu.TryGetActiveTemplate() is { } activeStyle &&
            activeStyle.Id != TranslationStyleMenu.FaithfulTemplateId)
        {
            var copied = StatusText.Text.Contains("已自动复制", StringComparison.Ordinal);
            StatusText.Text = TranslationStyleMenu.StyleStatusFor(session.PromptSupport) +
                (copied ? " · 已自动复制译文" : "。");
        }
    }

    private static void ShowIfPresent(TextBlock target, string value, Func<string, string> format, UIElement? container = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Visibility = Visibility.Collapsed;
            if (container is not null)
            {
                container.Visibility = Visibility.Collapsed;
            }
            target.Text = string.Empty;
            return;
        }
        target.Text = format(value.Trim());
        target.Visibility = Visibility.Visible;
        if (container is not null)
        {
            container.Visibility = Visibility.Visible;
        }
    }

    private void RenderFailure(string message)
    {
        if (_holdingSummary)
        {
            _translationNote = message;
            _reading.CaptureTranslation(_translation, _translationNote);
            return;
        }
        RenderState(TranslationSessionState.Failed);
        SetTranslationContent(FriendlyError(message), isMarkdown: false);
        TranslationTextBox.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        // The headline is deliberately short; the raw provider message stays
        // available underneath because it is what makes the problem fixable.
        ExplanationText.Text = message;
        ExplanationText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        ExplanationText.Visibility = Visibility.Visible;
        ExplanationBox.Visibility = Visibility.Visible;
        ErrorSettingsButton.Visibility = Visibility.Visible;
        PhoneticText.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        EngineBadge.Text = "未完成";
        SetBadgeTone(failed: true);
        SetRouteText("可检查网络或设置后重试");
        SetResultActionsEnabled(false);
    }

    private void SetResultActionsEnabled(bool enabled)
    {
        ResultCopyBtn.IsEnabled = enabled;
        ResultSpeakBtn.IsEnabled = enabled;
        StarToggle.IsEnabled = enabled;
    }

    /// <summary>
    /// Keeps the result badge from claiming success in red-dot states. The
    /// tone is a dynamic resource reference, so the badge also follows a
    /// theme switch after the state has rendered.
    /// </summary>
    private void SetBadgeTone(bool failed) => SetResultTone(failed, partial: false);

    /// <summary>
    /// Partial results get their own warning tone so an unverified translation
    /// never looks identical to a clean success.
    /// </summary>
    private void SetResultTone(bool failed, bool partial)
    {
        var strong = failed ? "DangerBrush" : partial ? "WarningBrush" : "TextTertiaryBrush";
        EngineBadge.SetResourceReference(TextBlock.ForegroundProperty, strong);
    }

    internal static string FriendlyError(string message)
    {
        // Ordered from most specific to least: rate-limit and offline messages
        // also mention keys and networks, so they must match first.
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("限流", StringComparison.Ordinal) ||
            message.Contains("受限", StringComparison.Ordinal))
        {
            return "翻译请求被限流，请稍后重试";
        }
        if (message.Contains("安全离线模式", StringComparison.Ordinal))
        {
            return "安全离线模式已开启";
        }
        if (message.Contains("API Key", StringComparison.OrdinalIgnoreCase))
        {
            return "还差一步：配置模型密钥";
        }
        if (message.Contains("鉴权", StringComparison.Ordinal) ||
            message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("403", StringComparison.Ordinal))
        {
            return "密钥无效或没有权限";
        }
        if (message.Contains("未授权上传", StringComparison.Ordinal) ||
            message.Contains("上传截图", StringComparison.Ordinal))
        {
            return "截图上传未获授权";
        }
        if (message.Contains("OCR", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 OCR 没能识别出文字";
        }
        if (message.Contains("网络访问未启用", StringComparison.Ordinal) ||
            message.Contains("网络", StringComparison.Ordinal) ||
            message.Contains("Safe", StringComparison.OrdinalIgnoreCase))
        {
            return "模型网络目前未启用";
        }
        if (message.Contains("选中", StringComparison.Ordinal) ||
            message.Contains("选区", StringComparison.Ordinal))
        {
            return "没有读到选中的文字";
        }
        if (message.Contains("超时", StringComparison.Ordinal) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "模型响应超时";
        }
        if (message.Contains("500", StringComparison.Ordinal) ||
            message.Contains("502", StringComparison.Ordinal) ||
            message.Contains("503", StringComparison.Ordinal) ||
            message.Contains("504", StringComparison.Ordinal) ||
            message.Contains("Internal Server", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("服务不可用", StringComparison.Ordinal) ||
            message.Contains("服务器错误", StringComparison.Ordinal))
        {
            return "翻译引擎服务端错误，请稍后重试";
        }
        if (message.Contains("坏响应", StringComparison.Ordinal) ||
            message.Contains("格式错误", StringComparison.Ordinal) ||
            message.Contains("解析失败", StringComparison.Ordinal) ||
            message.Contains("反序列化", StringComparison.Ordinal) ||
            message.Contains("invalid response", StringComparison.OrdinalIgnoreCase))
        {
            return "翻译引擎返回了无法解析的响应";
        }
        return "这次没有翻译成功";
    }

    // ================= Commands =================

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var retry = _retry;
        if (retry is null)
        {
            var text = SourceInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            retry = (token, ep) => TranslateTextAsync(text, token, ep);
            _retry = retry;
        }
        await RunOperationAsync(retry);
    }

    /// <summary>
    /// Begins the copy-success feedback: check glyph on the live accent.
    /// </summary>
    private void ShowCopyFeedback(System.Windows.Shapes.Path icon)
    {
        icon.Data = (Geometry)FindResource("IconCheck");
        icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
    }

    /// <summary>
    /// Ends the copy-success feedback: restores the icon's XAML Foreground
    /// binding. The binding expression IS the local value, so ClearValue
    /// would erase it for good and leave the idle icon with no fill. A null
    /// capture (abnormal XAML) falls back to the theme-following
    /// TextSecondaryBrush instead of silently leaving the accent stuck on.
    /// </summary>
    private void EndCopyFeedback(System.Windows.Shapes.Path icon, System.Windows.Data.Binding? originalFillBinding)
    {
        icon.Data = (Geometry)FindResource("IconCopy");
        if (originalFillBinding is not null)
        {
            icon.SetBinding(System.Windows.Shapes.Shape.FillProperty, originalFillBinding);
        }
        else
        {
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextSecondaryBrush");
        }
    }

    private async void ResultCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = _holdingSummary ? _reading.SummaryText : _translation;
        if (string.IsNullOrWhiteSpace(text) || (!_holdingSummary && !_gate.CanCopy))
        {
            return;
        }
        var clean = MarkdownPresenter.ToPlainText(text);
        if (await TrySetClipboardAsync(clean))
        {
            StatusText.Text = _holdingSummary ? "已复制要点" : "已复制译文到剪贴板";
            ShowCopyFeedback(ResultCopyIcon);
            await Task.Delay(1400);
            EndCopyFeedback(ResultCopyIcon, _resultCopyIconFillBinding);
        }
        else
        {
            StatusText.Text = "复制失败，剪贴板被占用";
        }
    }

    private async void SourceCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (await TrySetClipboardAsync(text))
        {
            StatusText.Text = "已复制原文到剪贴板";
            ShowCopyFeedback(SourceCopyIcon);
            await Task.Delay(1400);
            EndCopyFeedback(SourceCopyIcon, _sourceCopyIconFillBinding);
        }
        else
        {
            StatusText.Text = "复制失败，剪贴板被占用";
        }
    }

    private async void TermChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string term && !string.IsNullOrWhiteSpace(term))
        {
            if (await TrySetClipboardAsync(term))
            {
                StatusText.Text = $"已复制术语：{term}";
            }
            else
            {
                StatusText.Text = "复制失败，剪贴板被占用";
            }
        }
    }

    /// <summary>
    /// The gate is the authority on what this panel's result actually is.
    /// Its stage is passed through verbatim so the workbench can never
    /// upgrade a cancelled/failed (partial) result to a Completed one —
    /// unsafe expansions are refused downstream by keeping the result
    /// actions gated there.
    /// </summary>
    internal TranslationSessionState CurrentSessionState => _gate.Stage switch
    {
        TranslationPanelStage.Completed => TranslationSessionState.Completed,
        TranslationPanelStage.FailedWithPartial or
        TranslationPanelStage.FailedWithoutPartial => TranslationSessionState.Failed,
        _ => TranslationSessionState.Cancelled,
    };

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text;
        var sourceLang = SourceLanguage;
        var targetLang = TargetLanguage;
        var translation = _translation;
        // Nothing to hand over (fresh, cleared panel): expanding would only
        // wipe the workbench with an empty non-Completed state, so refuse.
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(translation))
        {
            return;
        }
        var state = CurrentSessionState;
        // The main window receives the current session — with its real
        // state — before the panel disappears: nothing is re-translated and
        // the result never flickers.
        _openInMain?.Invoke(text, targetLang, sourceLang, translation, state);
        Close();
    }

    private void SourceClear_Click(object sender, RoutedEventArgs e)
    {
        CancelOperation();
        CancelSummary();
        _holdingSummary = false;
        _reading.ClearSummaryDisplay();
        _gate.ResetToIdle();
        SourceInputBox.Clear();
        SetTranslationContent(string.Empty);
        _retry = null;
        _screenshot = null;
        PhoneticText.Visibility = Visibility.Collapsed;
        ExplanationText.Visibility = Visibility.Collapsed;
        ExplanationBox.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        EngineBadge.Text = "译文";
        SetBadgeTone(failed: false);
        SetResultActionsEnabled(false);
        ResetRetryButtonLabel();
        RenderIdle();
        SourceInputBox.Focus();
    }

    private void MergeLines_Click(object sender, RoutedEventArgs e)
    {
        var merged = MergeHardLineBreaks(SourceInputBox.Text);
        if (merged == SourceInputBox.Text)
        {
            return;
        }
        SourceInputBox.Text = merged;
        StatusText.Text = "已合并断行，Enter 重译";
        SourceInputBox.CaretIndex = merged.Length;
        SourceInputBox.Focus();
    }

    /// <summary>
    /// Joins the hard line breaks that PDF and e-book copies leave behind:
    /// blank lines stay paragraph breaks, a CJK line fuses directly to the
    /// next one, and anything else joins with a single space so Latin words
    /// never stick together. Trailing hyphens undo when the next line starts
    /// with a lowercase word.
    /// </summary>
    internal static string MergeHardLineBreaks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('\n'))
        {
            return text ?? string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        var paragraphs = normalized.Split(new[] { "\n\n" }, StringSplitOptions.None);
        var merged = new StringBuilder(normalized.Length);

        for (var paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
        {
            if (paragraphIndex > 0)
            {
                merged.Append("\n\n");
            }
            var lines = paragraphs[paragraphIndex].Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex].Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                if (merged.Length == 0 || merged[^1] == '\n')
                {
                    merged.Append(line);
                    continue;
                }

                var last = merged[^1];
                if (last is '-' or '–' && line[0] is >= 'a' and <= 'z')
                {
                    // "transla-\ntion" was one word before the page broke it.
                    merged.Length--;
                    merged.Append(line);
                }
                else if (JoinsTight(last) || JoinsTight(line[0]))
                {
                    merged.Append(line);
                }
                else
                {
                    merged.Append(' ').Append(line);
                }
            }
        }
        return merged.ToString();
    }

    private static bool JoinsTight(char c) =>
        (c >= '\u2E80' && c <= '\u9FFF') ||  // CJK radicals, kana, punctuation, ideographs
        (c >= '\uF900' && c <= '\uFAFF') ||  // CJK compatibility ideographs
        (c >= '\uFF00' && c <= '\uFFEF');    // fullwidth forms

    private void SourceSpeak_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            SpeakOrStop(text, SourceLanguage);
        }
    }

    private void ResultSpeak_Click(object sender, RoutedEventArgs e)
    {
        if (_holdingSummary)
        {
            if (!string.IsNullOrWhiteSpace(_reading.SummaryText))
            {
                SpeakOrStop(_reading.SummaryText, TargetLanguage);
            }
            return;
        }
        if (!_gate.CanPerformResultActions || string.IsNullOrWhiteSpace(_translation))
        {
            return;
        }
        var clean = MarkdownPresenter.ToPlainText(_translation);
        SpeakOrStop(clean, TargetLanguage);
    }

    private void SpeakOrStop(string? text, string languageTag)
    {
        if (TtsService.IsSpeaking)
        {
            TtsService.Stop();
            return;
        }
        TtsService.Speak(text, languageTag);
    }

    private async void StarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_gate.CanPerformResultActions || _vocabulary is null) return;
        var source = SourceInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(_translation)) return;

        var clean = MarkdownPresenter.ToPlainText(_translation);
        var result = await _vocabulary.ToggleStarAsync(
            source,
            clean,
            PhoneticText.Text.Trim('[', ']'),
            ExplanationText.Text,
            SourceLanguage,
            TargetLanguage);

        // A failed write must not light the star: reflect the store's actual
        // state and say what went wrong.
        UpdateStarIcon(result.Persisted
            ? result.Starred
            : _vocabulary.IsStarred(source, SourceLanguage, TargetLanguage));
        StatusText.Text = result.Persisted
            ? (result.Starred ? "已加入生词本" : "已从生词本移除")
            : result.DescribeFailureZh();
    }

    private void UpdateStarIcon(bool starred)
    {
        StarToggle.IsChecked = starred;
        StarToggle.ToolTip = starred ? "从生词本移除" : "收藏到生词本";
        // The accessibility name must follow the dynamic state so screen
        // readers announce the action the click will actually perform.
        System.Windows.Automation.AutomationProperties.SetName(StarToggle, (string)StarToggle.ToolTip);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings?.Invoke();
    }

    // ================= Global translation-style selector =================

    /// <summary>
    /// Repaints the shared style selector from local state. The built-in free
    /// engine disables the selector with the honest wording; an unreadable
    /// local state keeps it visible with an honest caveat instead.
    /// </summary>
    private void RefreshStyleSelector()
    {
        try
        {
            TranslationStyleMenu.ApplyTo(StyleSelectorButton);
            StyleSelectorLabel.Text = TranslationStyleMenu.ActiveLabel();
        }
        catch (Exception)
        {
            // The selector is additive UI; never break the panel over it.
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
            StatusText.Text = $"内置免费引擎{TranslationStyleMenu.FreeEngineToolTip}。";
            return;
        }
        var menu = TranslationStyleMenu.Build(
            styleChosen: template => _ = ChooseStyleAsync(template),
            reportStatus: message => StatusText.Text = message,
            // 管理提示词 reuses the panel's existing open-settings callback.
            managePrompts: () => SettingsButton_Click(this, new RoutedEventArgs()));
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
            message => StatusText.Text = message);
        if (!applied)
        {
            RefreshStyleSelector();
        }
    }

    /// <summary>
    /// Honest pre-flight notice when the route cannot be proven to honour the
    /// active style. Stated before the request goes out; the request itself
    /// is never blocked. Returns the notice so the operation keeps it above
    /// the preparing statuses instead of being instantly overwritten.
    /// </summary>
    private string? NotifyStyleBeforeTranslate()
    {
        if (TranslationStyleMenu.PreTranslateNotice() is { } styleNotice)
        {
            StatusText.Text = styleNotice;
            RefreshStyleSelector();
            return styleNotice;
        }
        return null;
    }

    private async void SourceInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        // Shift+Enter inserts a newline; Enter translates.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }
        // IME composition confirm: the composition owns this Enter.
        if (Ui.IsImeComposing(SourceInputBox, e))
        {
            return;
        }
        e.Handled = true;
        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _sourceKind = _sourceKind == "截图" ? "截图" : "输入";
        _screenshot = null;
        _retry = (token, ep) => TranslateTextAsync(text, token, ep);
        var styleNotice = NotifyStyleBeforeTranslate();
        await RunOperationAsync((cancellation, ep) => TranslateTextAsync(text, cancellation, ep), styleNotice);
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageChangeSuspended || !IsLoaded)
        {
            return;
        }
        CancelSummary();
        if (_holdingSummary)
        {
            ShowStoredTranslation();
        }
        PersistLanguagePair();

        // A screenshot re-runs the whole pipeline so a language change can pick a
        // different OCR engine; text just re-translates.
        if (_screenshot is { } image)
        {
            _retry = (token, ep) => TranslateScreenshotAsync(image, token, ep);
            await RunOperationAsync((cancellation, ep) => TranslateScreenshotAsync(image, cancellation, ep));
            return;
        }

        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _retry = (token, ep) => TranslateTextAsync(text, token, ep);
        await RunOperationAsync((cancellation, ep) => TranslateTextAsync(text, cancellation, ep));
    }

    private async void SwapLangButton_Click(object sender, RoutedEventArgs e)
    {
        CancelSummary();
        if (_holdingSummary)
        {
            ShowStoredTranslation();
        }

        var (source, target) = LanguageCatalog.Swap(SourceLanguage, TargetLanguage);

        // Move the finished translation into the source box first, so the
        // re-translation runs on the swapped text rather than the original.
        var swapped = _translation;
        if (!string.IsNullOrWhiteSpace(swapped))
        {
            SourceInputBox.Text = swapped;
            SetTranslationContent(string.Empty);
            _screenshot = null;
            _sourceKind = "输入";
        }

        _languageChangeSuspended = true;
        try
        {
            SourceLangCombo.SelectedItem = LanguageCatalog.ResolveSource(source);
            TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget(target);
        }
        finally
        {
            _languageChangeSuspended = false;
        }
        PersistLanguagePair();

        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _retry = (token, ep) => TranslateTextAsync(text, token, ep);
        await RunOperationAsync((cancellation, ep) => TranslateTextAsync(text, cancellation, ep));
    }

    /// <summary>Remembers the pair so the next popup opens the same way.</summary>
    private void PersistLanguagePair()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            if (settings.SourceLanguage == SourceLanguage && settings.TargetLanguage == TargetLanguage)
            {
                return;
            }
            _ = CoreBridge.SaveSettingsAsync(settings with
            {
                SourceLanguage = SourceLanguage,
                TargetLanguage = TargetLanguage,
            });
        }
        catch (InvalidOperationException)
        {
            // Remembering the pair is a convenience, never a reason to fail a
            // translation the user asked for.
        }
    }

    // Shared clipboard entry with the other surfaces (quick search, workbench,
    // library): one hardened writer, one place to audit or substitute.
    private static Task<bool> TrySetClipboardAsync(string text)
        => Helpers.CopyToClipboardAsync(text);

    /// <summary>
    /// Single writer for the footer's right slot: route/timing facts when a
    /// result exists, and a low-contrast one-shot shortcut hint while idle.
    /// Preparing/streaming paths clear the slot so real status always wins.
    /// </summary>
    private void SetRouteText(string text, bool idleHint = false)
    {
        RouteText.Text = text;
        RouteText.Opacity = idleHint ? 0.55 : 1.0;
    }

    // ================= Window behaviour =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // No window-level fade: animating the whole window's opacity breaks
        // ClearType on every glyph and makes text blur-then-sharpen.
        PositionNearAnchor();
    }

    /// <summary>A05: the gate approves automatic side effects only while the
    /// window is actually visible; every Hide/Show path flows through here.</summary>
    private void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        _gate.WindowVisible = e.NewValue is true;
        if (e.NewValue is true)
        {
            // The panel lives across shows: the engine (and therefore style
            // support) may have changed since it was last on screen.
            RefreshStyleSelector();
        }
    }

    /// <summary>
    /// Lets the panel accept keys, and arms the focus-loss auto-close.
    /// </summary>
    /// <remarks>
    /// The window is created with <c>ShowActivated="False"</c> so reading a
    /// selection can synthesize Ctrl+C into whatever app the user was in.
    /// Escape and typing only work once we take focus, which is why that is
    /// deferred to here rather than done at Show() time.
    /// </remarks>
    private void AllowKeyboardInteraction()
    {
        if (_readyForKeyboard)
        {
            return;
        }
        _readyForKeyboard = true;
        Activate();
        Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            // A06: transient surfaces own Escape first — an open context menu
            // or a language drop-down closes itself instead of the panel
            // reacting. This stays ahead of the IME check below: while a
            // menu is open, the Escape is aimed at the menu.
            if (_openDropDowns > 0 || IsContextMenuOpen())
            {
                return;
            }
            // E3: a live IME composition owns this Escape — hiding or
            // cancelling the panel out from under the composition would
            // strand its residue in the input box. Clear the residual
            // IsComposing flag safely (the box-level tracker repeats the
            // same reset when the tunnelling event reaches it, and the
            // IME's own composition-end events converge to false) and
            // leave the key unhandled so the IME can cancel its own
            // composition. The isolation suite drives the WPF-level
            // events only; real Microsoft Pinyin / Sogou behaviour stays
            // an explicit E3 manual verification TODO.
            if (Ui.GetIsComposing(SourceInputBox))
            {
                Ui.SetIsComposing(SourceInputBox, false);
                return;
            }
            e.Handled = true;
            // C05/R04 Esc ladder:
            // When reading summary, cancel summary first; if translation is also running, next Esc cancels it; final Esc hides panel.
            // When reading translation, cancel translation first; if summary is also running, next Esc cancels it; final Esc hides panel.
            // Running requests always cancel before window hiding, keeping partial results intact.
            if (_holdingSummary)
            {
                if (_summaryLifecycle.IsRunning)
                {
                    CancelSummary();
                    return;
                }
                if (_operation is { IsCancellationRequested: false })
                {
                    CancelOperation();
                    return;
                }
            }
            else
            {
                if (_operation is { IsCancellationRequested: false })
                {
                    CancelOperation();
                    return;
                }
                if (_summaryLifecycle.IsRunning)
                {
                    CancelSummary();
                    return;
                }
            }
            Hide();
            return;
        }

        // Ctrl+R: quickly retry translation
        if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            Retry_Click(this, new RoutedEventArgs());
            return;
        }

        // Ctrl+Shift+C: copy translation directly
        if (e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (_gate.CanPerformResultActions && !string.IsNullOrWhiteSpace(_translation))
            {
                e.Handled = true;
                ResultCopy_Click(this, new RoutedEventArgs());
                return;
            }
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Closing the window itself raises WM_ACTIVATE; reacting here would
        // fight an in-progress close.
        if (_closing || !_readyForKeyboard || PinToggle.IsChecked == true || _openDropDowns > 0)
        {
            return;
        }
        if (!_shellSettings().ClosePanelOnFocusLoss)
        {
            return;
        }
        // A ComboBox drop-down or a text-box context menu lives in its own HWND
        // and deactivates this window. Hiding then would make the language
        // pickers unusable, so only react when focus really left the app.
        if (ForegroundBelongsToThisProcess())
        {
            return;
        }
        // C05/F08: auto-hide is a visibility change only — a running request
        // keeps going to its deadline and the session can be restored from
        // the tray. The window is never destroyed by focus loss.
        Hide();
    }

    /// <summary>
    /// C05: set by the host when the panel must really die (session
    /// replacement, app exit). Without it, a close request cancels the
    /// in-flight request, keeps the partial and hides instead.
    /// </summary>
    internal bool ForceClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!ForceClose)
        {
            // C05: X/Alt+F4 cancel the in-flight request and keep the
            // partial; the panel hides and stays restorable from the tray.
            e.Cancel = true;
            CancelOperation();
            CancelSummary();
            Hide();
            return;
        }
        _closing = true;
        base.OnClosing(e);
    }

    private static bool ForegroundBelongsToThisProcess()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private void TrackDropDown(ComboBox comboBox)
    {
        comboBox.DropDownOpened += (_, _) => _openDropDowns++;
        comboBox.DropDownClosed += (_, _) => _openDropDowns = Math.Max(0, _openDropDowns - 1);
    }

    /// <summary>A06: true while any ContextMenu inside this window is open —
    /// such a menu must consume Escape itself.</summary>
    private bool IsContextMenuOpen()
    {
        var queue = new Queue<System.Windows.Media.Visual>();
        queue.Enqueue(this);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(current, i);
                if (child is FrameworkElement { ContextMenu: { IsOpen: true } })
                {
                    return true;
                }
                if (child is System.Windows.Media.Visual visual)
                {
                    queue.Enqueue(visual);
                }
            }
        }
        return false;
    }

    private const int WmEnterSizeMove = 0x0231;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(HwndHook);
        }
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmEnterSizeMove)
        {
            _userMoved = true;
        }
        return 0;
    }

    /// <summary>
    /// E3 DPI: one re-clamp at ApplicationIdle after a WM_DPICHANGED — never a
    /// mid-transition reposition (the window's own scale is stale there), and
    /// the same single MoveToPixels writer as every other landing.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_closing || _userMoved || _dpiRepositionPending)
        {
            return;
        }
        _dpiRepositionPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _dpiRepositionPending = false;
            if (_closing || _userMoved)
            {
                return;
            }
            PositionNearAnchor();
        }));
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        var pinned = PinToggle.IsChecked == true;
        StatusText.Text = pinned ? "浮窗已固定" : "浮窗已取消固定";
        var tooltip = pinned ? "取消固定（失焦不隐藏）" : "固定浮窗";
        PinToggle.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, tooltip);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        // A dragged panel must stop chasing its anchor when it resizes.
        _userMoved = true;
        AllowKeyboardInteraction();
        DragMove();
    }

    private void PositionNearAnchor()
    {
        if (_userMoved)
        {
            return;
        }
        // Target-monitor scale from the anchor point — the window's own DPI is
        // mid-transition stale right after a WM_DPICHANGED.
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(new Point(
            _anchorPixels.Left + (_anchorPixels.Width / 2),
            _anchorPixels.Top + (_anchorPixels.Height / 2)));
        var widthDip = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : 540);
        // Fallback matches the XAML Height/MinHeight (380): the old 360
        // fallback disagreed with the real minimum and clipped the footer.
        var heightDip = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 380);
        var sizePixels = new Size(widthDip * scale.X, heightDip * scale.Y);
        var workArea = ScreenGeometry.WorkAreaForAnchor(_anchorPixels);

        if (_lockedTopLeftPixels is null)
        {
            var topLeft = WindowPositioner.NearAnchor(_anchorPixels, sizePixels, workArea);
            _lockedTopLeftPixels = topLeft;
            ScreenGeometry.MoveToPixels(this, topLeft);
            return;
        }

        // WIN-04: Once initial anchor position is locked, do not re-flip candidates during streaming.
        // Clamp vertically and horizontally within the work area (touching bottom shifts upward, no side flip).
        const double edge = 12;
        var currentX = _lockedTopLeftPixels.Value.X;
        var currentY = _lockedTopLeftPixels.Value.Y;
        var clampedX = Math.Clamp(currentX, workArea.Left + edge, Math.Max(workArea.Left + edge, workArea.Right - sizePixels.Width - edge));
        var clampedY = Math.Clamp(currentY, workArea.Top + edge, Math.Max(workArea.Top + edge, workArea.Bottom - sizePixels.Height - edge));
        ScreenGeometry.MoveToPixels(this, new Point(clampedX, clampedY));
    }

    private void CancelOperation()
    {
        var operation = _operation;
        _operation = null;
        if (operation is null)
        {
            return;
        }
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation already completed and disposed its source.
        }
    }

    private void CancelSummary()
    {
        _summaryLifecycle.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A05: the close hotkey is a user intent — it cancels the running
    /// request and hides, exactly like X/Alt+F4. Distinct from blur
    /// auto-hide, which keeps the request running but suspends automatic
    /// side effects while hidden.
    /// </summary>
    internal void CloseAsUserIntent()
    {
        CancelOperation();
        CancelSummary();
        var session = CreateSessionSnapshot();
        if (session is not null && !App.SharedSessionStore.TryStore(session, out var rejection))
        {
            // The panel is on its way out — no surface remains to show the
            // rejection, so the structured diagnostics entry is the record.
            App.LogSessionStoreRejection(rejection);
        }
        Hide();
    }

    /// <summary>
    /// N01: Captures an immutable snapshot of this window's current session for the session store.
    /// Strictly excludes image bytes per V2 & G05 rules.
    /// </summary>
    internal StoredSession? CreateSessionSnapshot()
    {
        var source = SourceInputBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var origin = _sourceKind switch
        {
            "截图" => SessionOrigin.ScreenshotVision,
            "取字" => SessionOrigin.ScreenshotOcr,
            _ => SessionOrigin.TranslationPanel
        };

        var resultText = _translation ?? string.Empty;
        var hasResult = !string.IsNullOrWhiteSpace(resultText);
        var state = CurrentSessionState;
        if (state != TranslationSessionState.Completed)
        {
            // 流式/整理中关闭时，异步取消回调还没机会把 gate 推进到
            // CancelledWithPartial：快照不能依赖它先完成。gate 是唯一权威，
            // 只要它握有真实流式文本，就必须立即按「已取消 · 不完整」保留。
            if (!string.IsNullOrWhiteSpace(_gate.StreamedText))
            {
                resultText = _gate.StreamedText;
                hasResult = true;
            }
            // 一次失败的新尝试会把「友好的失败标题」渲染进结果框（便于阅读），
            // 但那不是译文：会话库存必须只认真实的部分译文存粮，否则失败文案
            // 会在恢复/下一次快照里冒充本次尝试的翻译结果。
            else if (!_gate.HasPartialText)
            {
                resultText = string.Empty;
                hasResult = false;
            }
        }
        var isPartial = state != TranslationSessionState.Completed && hasResult;

        return StoredSession.Create(
            sessionId: _sessionId,
            origin: origin,
            sourceText: source,
            sourceLang: SourceLanguage,
            targetLang: TargetLanguage,
            engineProfileId: null,
            engineName: EngineBadge?.Text,
            state: state,
            resultText: resultText,
            explanationText: ExplanationText?.Visibility == Visibility.Visible ? ExplanationText.Text : null,
            isPartial: isPartial
        );
    }

    /// <summary>
    /// N01: Restores a session from the session store into this window.
    /// ZERO-RESEND CONTRACT: restores the existing text and actions; NEVER triggers a new request.
    /// </summary>
    internal void RestoreSession(StoredSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessionId = session.SessionId;

        SourceInputBox.Text = session.SourceText;
        _translation = session.ResultText ?? string.Empty;

        _languageChangeSuspended = true;
        try
        {
            if (!string.IsNullOrEmpty(session.SourceLanguage))
            {
                SourceLangCombo.SelectedItem = LanguageCatalog.ResolveSource(session.SourceLanguage);
            }
            if (!string.IsNullOrEmpty(session.TargetLanguage))
            {
                TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget(session.TargetLanguage);
            }
        }
        finally
        {
            _languageChangeSuspended = false;
        }

        Progress.Visibility = Visibility.Collapsed;
        ResultSkeleton.Visibility = Visibility.Collapsed;
        StreamIndicator.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(session.ResultText))
        {
            SetTranslationContent(session.ResultText, isMarkdown: true);
        }
        else
        {
            SetTranslationContent(string.Empty, isMarkdown: false);
        }

        if (!string.IsNullOrEmpty(session.ExplanationText))
        {
            ExplanationText.Text = session.ExplanationText;
            ExplanationBox.Visibility = Visibility.Visible;
        }
        else
        {
            ExplanationBox.Visibility = Visibility.Collapsed;
        }

        // Actions are decided by the session's real state, never by "there is
        // text": Completed restores the full action set, while Cancelled and
        // Failed keep only manual copy alive (and only while partial text
        // exists) — speak/star/auto-copy stay gated per G06/N04.
        if (session.State == TranslationSessionState.Completed && !session.IsPartial)
        {
            _gate.OnCompleted(_translation);
            EngineBadge.Text = string.IsNullOrEmpty(session.EngineName) ? "译文" : session.EngineName;
            SetResultTone(failed: false, partial: false);
            StatusText.Text = "已恢复最近翻译（未重发）";
            SetResultActionsEnabled(!string.IsNullOrWhiteSpace(_translation));
        }
        else if (session.State == TranslationSessionState.Failed)
        {
            // A failed session must never be relabelled「已取消」: its badge,
            // tone and wording keep saying the run did not finish.
            _gate.OnFailed(session.ExplanationText ?? "失败", _translation);
            EngineBadge.Text = "未完成";
            SetBadgeTone(failed: true);
            StatusText.Text = "已恢复失败会话（未重发）";
            SetResultActionsEnabled(false);
            ResultCopyBtn.IsEnabled = _gate.CanCopy;
        }
        else
        {
            _gate.OnCancelled(_translation);
            EngineBadge.Text = "已取消";
            SetResultTone(failed: false, partial: true);
            StatusText.Text = "已恢复未完成内容（未重发）";
            SetResultActionsEnabled(false);
            ResultCopyBtn.IsEnabled = _gate.CanCopy;
        }

        _retry = (token, ep) => TranslateTextAsync(session.SourceText, token, ep);
        // 截图会话恢复后图片字节并不在会话存档里（隐私契约 V2/G05：快照
        // 严格不含图片）。重试因此只能是普通文字翻译：把这个事实直接写在
        // 重试按钮与状态槽上，绝不暗示会重发截图。
        if (session.Origin is SessionOrigin.ScreenshotVision or SessionOrigin.ScreenshotOcr)
        {
            RetryButton.ToolTip = "以文本方式重译 (Ctrl+R)";
            System.Windows.Automation.AutomationProperties.SetName(RetryButton, "以文本方式重译");
            SetRouteText("截图未保存 · 重试将以文本方式重译");
        }
        else
        {
            ResetRetryButtonLabel();
        }
        AllowKeyboardInteraction();
    }

    /// <summary>Restores the retry button's default wording for in-session
    /// (image-carrying or plain text) retries.</summary>
    private void ResetRetryButtonLabel()
    {
        RetryButton.ToolTip = "重试 (Ctrl+R)";
        System.Windows.Automation.AutomationProperties.SetName(RetryButton, "重试翻译");
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        var session = CreateSessionSnapshot();
        if (session is not null && !App.SharedSessionStore.TryStore(session, out var rejection))
        {
            // The window is gone; the rejection can only be recorded.
            App.LogSessionStoreRejection(rejection);
        }
        ThemeService.ThemeChanged -= _themeChangedHandler;
        TtsService.Stop();
        CancelOperation();
        CancelSummary();
        // 面板关闭后重试不再可能发生：释放截图与重试闭包（闭包本身
        // 捕获同一份 PNG 字节数组），避免已关闭面板长期钉住数 MB 内存。
        _screenshot = null;
        _retry = null;
        base.OnClosed(e);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    }
}
