using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly Action<string, string?, string?, string?>? _openInMain;
    private readonly TranslationCoordinator _coordinator;

    private CancellationTokenSource? _operation;
    private Func<CancellationToken, Task>? _retry;
    private byte[]? _screenshot;
    private string _translation = string.Empty;
    private string _sourceKind = "划词";
    private bool _userMoved;
    private bool _languageChangeSuspended = true;
    private bool _readyForKeyboard;
    private bool _closing;
    private int _openDropDowns;
    private long _inputAcquisitionMs;

    internal TranslationPanelWindow(
        Rect anchorPixels,
        HistoryStore history,
        Func<ShellSettings> shellSettings,
        Action? openSettings = null,
        Action<string, string?, string?, string?>? openInMain = null,
        VocabularyStore? vocabulary = null)
    {
        _anchorPixels = anchorPixels;
        _vocabulary = vocabulary;
        _shellSettings = shellSettings;
        _openSettings = openSettings;
        _openInMain = openInMain;
        _coordinator = new TranslationCoordinator(history, vocabulary);

        InitializeComponent();

        SourceLangCombo.ItemsSource = LanguageCatalog.Sources;
        TargetLangCombo.ItemsSource = LanguageCatalog.Targets;
        var stored = CoreBridge.GetSettings();
        SourceLangCombo.SelectedItem = LanguageCatalog.ResolveSource(stored.SourceLanguage);
        TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget(stored.TargetLanguage);
        _languageChangeSuspended = false;

        TrackDropDown(SourceLangCombo);
        TrackDropDown(TargetLangCombo);

        TtsService.SpeakingStateChanged += OnTtsSpeakingStateChanged;

        // Opaque window now: DWM rounds the corners and draws the shadow,
        // and the immersive-dark attribute keeps the frame theme-correct.
        ThemeService.ApplyWindowChrome(this);

        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionNearAnchor();
        Closed += (_, _) =>
        {
            TtsService.SpeakingStateChanged -= OnTtsSpeakingStateChanged;
            CancelOperation();
        };
    }

    private void OnTtsSpeakingStateChanged(object? sender, bool isSpeaking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var brush = (Brush)FindResource(isSpeaking ? "AccentBrush" : "TextSecondaryBrush");
            SourceSpeakIcon.Fill = brush;
            ResultSpeakIcon.Fill = brush;
            SourceSpeakBtn.ToolTip = isSpeaking ? "停止朗读" : "朗读原文";
            ResultSpeakBtn.ToolTip = isSpeaking ? "停止朗读" : "朗读译文";
        });
    }

    private string SourceLanguage =>
        (SourceLangCombo.SelectedItem as LanguageOption)?.Tag ?? LanguageCatalog.Auto;

    private string TargetLanguage =>
        (TargetLangCombo.SelectedItem as LanguageOption)?.Tag ?? "zh-CN";

    // ================= Entry points =================

    internal async Task StartSelectionAsync(ClipboardSelectionService selectionService)
    {
        ArgumentNullException.ThrowIfNull(selectionService);
        _sourceKind = "划词";
        SourceKindLabel.Text = "· 划词";
        await RunOperationAsync(async cancellation =>
        {
            RenderState(TranslationSessionState.ReadingSelection);
            SourceLabel.Text = "所选文字";
            var inputTimer = Stopwatch.StartNew();
            var source = await selectionService.ReadSelectionAsync(cancellation);
            inputTimer.Stop();
            _inputAcquisitionMs = inputTimer.ElapsedMilliseconds;
            SourceInputBox.Text = source;

            // Only now take focus: activating earlier would have moved the
            // foreground window away from the app we just sent Ctrl+C to.
            AllowKeyboardInteraction();

            _retry = token => TranslateTextAsync(source, token);
            await TranslateTextAsync(source, cancellation);
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
        _retry = token => TranslateScreenshotAsync(image, token);
        await RunOperationAsync(cancellation => TranslateScreenshotAsync(image, cancellation));
    }

    internal async Task StartScreenshotOcrAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _sourceKind = "取字";
        SourceKindLabel.Text = "· 截图取字";
        _screenshot = image;
        SourceLabel.Text = "画面提取文字";
        AllowKeyboardInteraction();
        _retry = token => RecognizeOcrAsync(image, token);
        await RunOperationAsync(cancellation => RecognizeOcrAsync(image, cancellation));
    }

    private async Task RecognizeOcrAsync(byte[] image, CancellationToken cancellationToken)
    {
        RenderState(TranslationSessionState.Recognizing);
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

        var formatted = MarkdownPresenter.FormatPangu(recognized.Trim());
        SourceInputBox.Text = formatted;
        SetTranslationContent(formatted, isMarkdown: false);
        await TrySetClipboardAsync(formatted);

        RenderState(TranslationSessionState.Completed);
        EngineBadge.Text = "离线 OCR 取字";
        SetBadgeTone(failed: false);
        StatusText.Text = "已提取画面文字并自动复制到剪贴板";
        RouteText.Text = $"{formatted.Length} 字符";
    }

    internal async Task StartTextAsync(string text)
    {
        _inputAcquisitionMs = 0;
        _sourceKind = "输入";
        SourceKindLabel.Text = "· 输入";
        SourceLabel.Text = "原文";
        SourceInputBox.Text = text ?? string.Empty;
        AllowKeyboardInteraction();

        if (string.IsNullOrWhiteSpace(text))
        {
            RenderIdle();
            SourceInputBox.Focus();
            return;
        }
        _retry = token => TranslateTextAsync(text, token);
        await RunOperationAsync(cancellation => TranslateTextAsync(text, cancellation));
    }

    internal void ShowImmediateFailure(string message)
    {
        AllowKeyboardInteraction();
        RenderFailure(message);
        SourceInputBox.Focus();
        SourceInputBox.SelectAll();
    }

    // ================= Operation plumbing =================

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        CancelOperation();
        var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsVisible)
            {
                RenderState(TranslationSessionState.Cancelled);
                Progress.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception exception)
        {
            RenderFailure(exception.Message);
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

    private async Task TranslateTextAsync(string source, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        RenderState(TranslationSessionState.Translating);
        TranslationTextBox.Clear();
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "正在思考与翻译…");
        ExplanationBox.Visibility = Visibility.Collapsed;
        cancellation.ThrowIfCancellationRequested();

        var session = await _coordinator.TranslateTextAsync(
            source,
            SourceLanguage,
            TargetLanguage,
            _sourceKind == "划词" ? TranslationInputSource.Selection : TranslationInputSource.Manual,
            cancellation);
        ThrowForTerminalState(session, cancellation);
        await RenderResultAsync(source, session, pipelineNote: string.Empty);
    }

    private async Task TranslateScreenshotAsync(byte[] image, CancellationToken cancellation)
    {
        RenderState(TranslationSessionState.Recognizing);
        SourceInputBox.Clear();
        SourceInputBox.SetValue(Ui.PlaceholderProperty, $"正在识别截图画面…（{image.Length / 1024.0:0.#} KiB）");
        TranslationTextBox.Clear();
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "等待画面文字识别后翻译…");
        ExplanationBox.Visibility = Visibility.Collapsed;
        cancellation.ThrowIfCancellationRequested();

        var session = await _coordinator.TranslateScreenshotAsync(
            image, SourceLanguage, TargetLanguage, cancellation);
        ThrowForTerminalState(session, cancellation);

        SourceInputBox.SetValue(Ui.PlaceholderProperty, "输入或粘贴要翻译的文字，按 Enter 翻译，Shift+Enter 换行");
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "译文将显示在这里…");

        // The transcription is the text that was actually read from the image;
        // showing it as the source is what makes the result verifiable.
        var recognized = string.IsNullOrWhiteSpace(session.Transcription)
            ? "（模型未回传识别文本）"
            : session.Transcription;
        SourceInputBox.Text = recognized;
        await RenderResultAsync(recognized, session, pipelineNote: session.RoutingReason ?? string.Empty);
    }

    /// <summary>
    /// Surfaces coordinator outcomes through the panel's existing exception
    /// plumbing: cancellation stays cancellation, everything else fails with a
    /// message that says what happened and what to do next.
    /// </summary>
    private static void ThrowForTerminalState(TranslationSession session, CancellationToken cancellation)
    {
        if (session.Stage == TranslationSessionStage.Cancelled)
        {
            throw new OperationCanceledException(cancellation);
        }
        if (!session.IsSuccess)
        {
            var message = session.Error?.Message ?? "翻译未完成";
            var suggestion = session.Error?.ActionableSuggestion;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(suggestion)
                ? message
                : $"{message} {suggestion}");
        }
    }

    // ================= Rendering =================

    private void RenderIdle()
    {
        StatusText.Text = "输入文字后按 Enter 开始翻译";
        Progress.Visibility = Visibility.Collapsed;
        RouteText.Text = string.Empty;
        StatusDot.Background = (Brush)FindResource("TextTertiaryBrush");
        TranslationTextBox.SetValue(Ui.PlaceholderProperty, "译文将显示在这里…");
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
        }

        StatusDot.Background = state switch
        {
            TranslationSessionState.Failed => (Brush)FindResource("DangerBrush"),
            TranslationSessionState.Cancelled => (Brush)FindResource("TextTertiaryBrush"),
            _ => (Brush)FindResource("AccentBrush"),
        };
    }

    private void SetTranslationContent(string text, bool isMarkdown = true)
    {
        _translation = text;
        TranslationTextBox.Text = text;
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

    private async Task RenderResultAsync(string source, TranslationSession session, string pipelineNote)
    {
        var partial = session.Warnings.Count > 0;
        // A translation the model could not fully verify is never presented as
        // a plain success — the badge and status say "partial".
        RenderState(TranslationSessionState.Completed);
        SetTranslationContent(session.TranslatedText, isMarkdown: true);

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

        EngineBadge.Text = partial ? "部分成功" : session.PipelineLabel ?? "翻译完成";
        SetResultTone(failed: false, partial: partial);
        var totalMs = session.Timing.TotalElapsedMs + (ulong)Math.Max(0, _inputAcquisitionMs);
        var timingParts = new List<string> { session.PipelineLabel ?? "翻译" };
        if (_inputAcquisitionMs > 0)
        {
            timingParts.Add($"取词 {_inputAcquisitionMs} ms");
        }
        if (session.Timing.OcrElapsedMs > 0)
        {
            timingParts.Add($"OCR {session.Timing.OcrElapsedMs} ms");
        }
        if (session.InputSource == TranslationInputSource.Screenshot)
        {
            timingParts.Add(session.ImageUploaded ? "图片已进入视觉请求" : "图片未上传");
        }
        timingParts.Add($"路由 {session.Timing.RoutingElapsedMs} ms");
        timingParts.Add($"网络/模型 {session.Timing.NetworkElapsedMs} ms");
        timingParts.Add($"总计 {totalMs} ms");
        RouteText.Text = string.Join(" · ", timingParts);
        StatusText.Text = partial
            ? "部分成功 · 见下方提醒"
            : (string.IsNullOrWhiteSpace(pipelineNote)
                ? TranslationSessionStateText.Describe(TranslationSessionState.Completed)
                : pipelineNote);

        var settings = _shellSettings();
        UpdateStarIcon(_vocabulary?.IsStarred(source) == true);
        if (settings.CopyTranslationAutomatically && !string.IsNullOrWhiteSpace(_translation))
        {
            if (await TrySetClipboardAsync(_translation))
            {
                StatusText.Text = partial
                    ? "部分成功 · 已自动复制译文"
                    : "翻译完成 · 已自动复制译文";
            }
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
        RenderState(TranslationSessionState.Failed);
        SetTranslationContent(FriendlyError(message), isMarkdown: false);
        // The headline is deliberately short; the raw provider message stays
        // available underneath because it is what makes the problem fixable.
        ExplanationText.Text = message;
        ExplanationText.Visibility = Visibility.Visible;
        ExplanationBox.Visibility = Visibility.Visible;
        PhoneticText.Visibility = Visibility.Collapsed;
        TermsList.Visibility = Visibility.Collapsed;
        WarningBox.Visibility = Visibility.Collapsed;
        EngineBadge.Text = "未完成";
        SetBadgeTone(failed: true);
        RouteText.Text = "可检查网络或设置后重试";
    }

    /// <summary>Keeps the result badge from claiming success in red-dot states.</summary>
    private void SetBadgeTone(bool failed) => SetResultTone(failed, partial: false);

    /// <summary>
    /// Partial results get their own warning tone so an unverified translation
    /// never looks identical to a clean success.
    /// </summary>
    private void SetResultTone(bool failed, bool partial)
    {
        // The engine label is plain metadata text now; tone comes from the
        // foreground colour alone.
        var strong = failed ? "DangerBrush" : partial ? "WarningBrush" : "TextTertiaryBrush";
        EngineBadge.Foreground = (Brush)FindResource(strong);
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
        if (message.Contains("超时", StringComparison.Ordinal))
        {
            return "模型响应超时";
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
            retry = token => TranslateTextAsync(text, token);
            _retry = retry;
        }
        await RunOperationAsync(retry);
    }

    private async void ResultCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_translation))
        {
            return;
        }
        if (await TrySetClipboardAsync(_translation))
        {
            StatusText.Text = "已复制译文到剪贴板";
            ResultCopyIcon.Data = (Geometry)FindResource("IconCheck");
            ResultCopyIcon.Fill = (Brush)FindResource("AccentBrush");
            await Task.Delay(1400);
            ResultCopyIcon.Data = (Geometry)FindResource("IconCopy");
            ResultCopyIcon.Fill = (Brush)FindResource("TextSecondaryBrush");
        }
        else
        {
            StatusText.Text = "复制失败，剪贴板被占用";
        }
    }

    private async void SourceCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (await TrySetClipboardAsync(text))
        {
            StatusText.Text = "已复制原文到剪贴板";
            SourceCopyIcon.Data = (Geometry)FindResource("IconCheck");
            SourceCopyIcon.Fill = (Brush)FindResource("AccentBrush");
            await Task.Delay(1400);
            SourceCopyIcon.Data = (Geometry)FindResource("IconCopy");
            SourceCopyIcon.Fill = (Brush)FindResource("TextSecondaryBrush");
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
                if (sender is Button btn)
                {
                    var old = btn.Content;
                    btn.Content = $"✓ {term}";
                    await Task.Delay(1000);
                    btn.Content = old;
                }
            }
            else
            {
                StatusText.Text = "复制失败，剪贴板被占用";
            }
        }
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text;
        var sourceLang = SourceLanguage;
        var targetLang = TargetLanguage;
        var translation = _translation;
        // The main window receives the current session before the panel
        // disappears: nothing is re-translated and the result never flickers.
        _openInMain?.Invoke(text, targetLang, sourceLang, translation);
        Close();
    }

    private void SourceClear_Click(object sender, RoutedEventArgs e)
    {
        CancelOperation();
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
        StatusText.Text = "已合并断行，按 Enter 重新翻译";
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

    private void SourceSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(SourceInputBox.Text);

    private void ResultSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(_translation);

    private static void SpeakOrStop(string? text)
    {
        if (TtsService.IsSpeaking)
        {
            TtsService.Stop();
            return;
        }
        TtsService.Speak(text);
    }

    private void StarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null) return;
        var source = SourceInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(_translation)) return;

        var isStarred = _vocabulary.ToggleStar(
            source,
            _translation,
            PhoneticText.Text.Trim('[', ']'),
            ExplanationText.Text,
            SourceLanguage,
            TargetLanguage);

        UpdateStarIcon(isStarred);
        StatusText.Text = isStarred ? "★ 已加入生词本 (Anki)" : "已从生词本移除";
    }

    private void UpdateStarIcon(bool starred)
    {
        StarToggle.IsChecked = starred;
        StarToggle.ToolTip = starred ? "从生词本移除" : "加入生词本 / 收藏";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Settings is a destination, not a second layer over the transient
        // translation result. App.ShowSettings closes transient surfaces and
        // establishes the main/settings ownership relationship.
        _openSettings?.Invoke();
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
        e.Handled = true;
        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _sourceKind = _sourceKind == "截图" ? "截图" : "输入";
        _screenshot = null;
        _retry = token => TranslateTextAsync(text, token);
        await RunOperationAsync(cancellation => TranslateTextAsync(text, cancellation));
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languageChangeSuspended || !IsLoaded)
        {
            return;
        }
        PersistLanguagePair();

        // A screenshot re-runs the whole pipeline so a language change can pick a
        // different OCR engine; text just re-translates.
        if (_screenshot is { } image)
        {
            _retry = token => TranslateScreenshotAsync(image, token);
            await RunOperationAsync(cancellation => TranslateScreenshotAsync(image, cancellation));
            return;
        }

        var text = SourceInputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _retry = token => TranslateTextAsync(text, token);
        await RunOperationAsync(cancellation => TranslateTextAsync(text, cancellation));
    }

    private async void SwapLangButton_Click(object sender, RoutedEventArgs e)
    {
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
        _retry = token => TranslateTextAsync(text, token);
        await RunOperationAsync(cancellation => TranslateTextAsync(text, cancellation));
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
            CoreBridge.SaveSettings(settings with
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

    private static async Task<bool> TrySetClipboardAsync(string text)
    {
        // Another process can hold the clipboard open; a few awaited retries
        // keep the message pump alive so the owner can actually release it.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                await Task.Delay(15 * (attempt + 1));
            }
        }
        return false;
    }

    // ================= Window behaviour =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // No window-level fade: animating the whole window's opacity breaks
        // ClearType on every glyph and makes text blur-then-sharpen.
        PositionNearAnchor();
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
            e.Handled = true;
            // Escape cancels a running request first, and only closes an idle
            // panel; otherwise a slow request could not be abandoned without
            // losing the result that was already on screen.
            if (_operation is { IsCancellationRequested: false })
            {
                CancelOperation();
                RenderState(TranslationSessionState.Cancelled);
                return;
            }
            Close();
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
            if (!string.IsNullOrWhiteSpace(_translation))
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
        // Closing the window itself raises WM_ACTIVATE; calling Close() again
        // from here throws "cannot call Close during window closing".
        if (_closing || !_readyForKeyboard || PinToggle.IsChecked == true || _openDropDowns > 0)
        {
            return;
        }
        if (!_shellSettings().ClosePanelOnFocusLoss)
        {
            return;
        }
        // A ComboBox drop-down or a text-box context menu lives in its own HWND
        // and deactivates this window. Closing then would make the language
        // pickers unusable, so only close when focus really left the app.
        if (ForegroundBelongsToThisProcess())
        {
            return;
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
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

    private void PinToggle_Changed(object sender, RoutedEventArgs e) =>
        StatusText.Text = PinToggle.IsChecked == true ? "浮窗已固定" : "浮窗已取消固定";

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
        if (_userMoved || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var scale = ScreenGeometry.ScaleOf(this);
        var sizePixels = new Size(ActualWidth * scale.X, ActualHeight * scale.Y);
        var workArea = ScreenGeometry.WorkAreaForAnchor(_anchorPixels);
        var topLeft = WindowPositioner.NearAnchor(_anchorPixels, sizePixels, workArea);
        ScreenGeometry.MoveToPixels(this, topLeft);
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        TtsService.Stop();
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
