using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace PopGlot.Windows;

internal enum TranslationSessionState
{
    ReadingSelection,
    Capturing,
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
        TranslationSessionState.Translating => "正在翻译",
        TranslationSessionState.Completed => "翻译完成",
        TranslationSessionState.Failed => "需要处理",
        TranslationSessionState.Cancelled => "已取消",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

public partial class TranslationPanelWindow : Window
{
    private readonly Rect _anchor;
    private readonly HistoryStore _history;
    private readonly Func<ShellSettings> _shellSettings;
    private CancellationTokenSource? _operation;
    private Func<Task>? _retry;
    private bool _pinned;
    private bool _userMoved;
    private bool _suspendAutoTranslate;
    private string _translation = string.Empty;
    private string _currentSource = string.Empty;
    private string _sourceKind = "划词";

    internal TranslationPanelWindow(
        Rect anchor,
        HistoryStore history,
        Func<ShellSettings> shellSettings)
    {
        _anchor = anchor;
        _history = history;
        _shellSettings = shellSettings;
        InitializeComponent();
        Loaded += Window_Loaded;
        SizeChanged += (_, _) => PositionNearAnchor();
        Closed += (_, _) => CancelOperation();
        Deactivated += Window_Deactivated;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_pinned)
        {
            Close();
        }
    }

    private string CurrentSourceLang =>
        ((ComboBoxItem)SourceLangCombo.SelectedItem).Tag?.ToString() ?? "auto";

    private string CurrentTargetLang =>
        ((ComboBoxItem)TargetLangCombo.SelectedItem).Tag?.ToString() ?? "zh-CN";

    internal async Task StartSelectionAsync(ClipboardSelectionService selectionService)
    {
        ArgumentNullException.ThrowIfNull(selectionService);
        _sourceKind = "划词";
        await BeginOperationAsync(async cancellation =>
        {
            RenderState(TranslationSessionState.ReadingSelection);
            SourceLabel.Text = "所选文字";
            SourceInputBox.Text = "正在读取选区…";
            var source = await selectionService.ReadSelectionAsync(cancellation);
            _currentSource = source;
            SourceInputBox.Text = source;
            _retry = () => PerformTranslationAsync(source);
            await PerformTranslationAsync(source);
        });
    }

    internal async Task StartScreenshotAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _sourceKind = "截图";
        await BeginOperationAsync(async cancellation =>
        {
            RenderState(TranslationSessionState.Capturing);
            SourceLabel.Text = "截图内容";
            SourceInputBox.Text = $"正在 OCR 识别中… ({image.Length / 1024.0:0.#} KiB)";

            var apiKey = CredentialStore.LoadApiKey();
            RenderState(TranslationSessionState.Translating);
            var response = await CoreBridge.TranslateVisionAsync(
                apiKey,
                "image/png",
                image,
                CurrentSourceLang,
                CurrentTargetLang);

            var source = !string.IsNullOrWhiteSpace(response.Result.Transcription)
                ? response.Result.Transcription
                : "截图内容已识别";

            _currentSource = source;
            SourceInputBox.Text = source;
            _retry = () => PerformTranslationAsync(source);
            RenderResult(_sourceKind, source, response);
        });
    }

    public async Task StartInputAsync(string initialText = "")
    {
        _sourceKind = "输入";
        SourceLabel.Text = "输入翻译";
        _currentSource = initialText;
        SourceInputBox.Text = initialText;
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            _retry = () => PerformTranslationAsync(initialText);
            await BeginOperationAsync(_ => PerformTranslationAsync(initialText));
        }
        else
        {
            RenderState(TranslationSessionState.Completed);
            TranslationTextBox.Text = "请输入要翻译的内容…";
            SourceInputBox.Focus();
        }
    }

    internal void ShowImmediateFailure(string message) => RenderFailure(message);

    private async Task BeginOperationAsync(Func<CancellationToken, Task> operation)
    {
        CancelOperation();
        _operation = new CancellationTokenSource();
        try
        {
            await operation(_operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsVisible)
            {
                RenderState(TranslationSessionState.Cancelled);
            }
        }
        catch (Exception exception)
        {
            RenderFailure(exception.Message);
        }
    }

    private async Task PerformTranslationAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var cancellation = _operation?.Token ?? CancellationToken.None;
        RenderState(TranslationSessionState.Translating);
        cancellation.ThrowIfCancellationRequested();

        var apiKey = CredentialStore.LoadApiKey();
        var response = await CoreBridge.TranslateTextAsync(
            apiKey,
            source,
            CurrentSourceLang,
            CurrentTargetLang);

        cancellation.ThrowIfCancellationRequested();
        RenderResult(_sourceKind, source, response);
    }

    private void RenderState(TranslationSessionState state)
    {
        StatusText.Text = TranslationSessionStateText.Describe(state);
        Progress.Visibility = state is TranslationSessionState.Completed or
            TranslationSessionState.Failed or TranslationSessionState.Cancelled
            ? Visibility.Collapsed
            : Visibility.Visible;

        StatusDot.Background = state switch
        {
            TranslationSessionState.Failed => (Brush)FindResource("DangerBrush"),
            TranslationSessionState.Translating or TranslationSessionState.ReadingSelection or TranslationSessionState.Capturing => (Brush)FindResource("AccentBrush"),
            _ => (Brush)FindResource("AccentBrush")
        };
    }

    private void RenderResult(string sourceKind, string source, TranslationResponse response)
    {
        RenderState(TranslationSessionState.Completed);
        _translation = response.Result.TranslatedText;
        TranslationTextBox.Text = _translation;

        // Phonetic
        if (!string.IsNullOrWhiteSpace(response.Result.Transcription))
        {
            PhoneticText.Text = $"/{response.Result.Transcription}/";
            PhoneticText.Visibility = Visibility.Visible;
        }
        else
        {
            PhoneticText.Visibility = Visibility.Collapsed;
        }

        // Explanation
        ExplanationText.Text = response.Result.Explanation;
        ExplanationText.Visibility = string.IsNullOrWhiteSpace(response.Result.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;

        // Protected Terms
        TermsText.Text = response.Result.ProtectedTerms.Count == 0
            ? string.Empty
            : $"原样保留代码元素 · {string.Join("   ", response.Result.ProtectedTerms)}";
        TermsBorder.Visibility = response.Result.ProtectedTerms.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        var isFree = response.Diagnostics.RequestId == "free-web";
        ResultEngineBadge.Text = isFree ? "🌐 基础翻译" : $"✨ {response.Diagnostics.ProviderType}";
        RouteText.Text = isFree
            ? $"免费引擎 · {response.Diagnostics.Endpoint} · {response.Diagnostics.ElapsedMs} ms"
            : $"{response.Diagnostics.ProviderType} · {response.Diagnostics.ElapsedMs} ms";

        _history.TryAdd(
            new TranslationHistoryEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                sourceKind,
                source,
                response.Result.TranslatedText,
                response.Result.Explanation,
                response.Result.ProtectedTerms),
            _shellSettings().HistoryEnabled);
    }

    private void RenderFailure(string message)
    {
        RenderState(TranslationSessionState.Failed);
        TranslationTextBox.Text = FriendlyError(message);
        ExplanationText.Text = message;
        ExplanationText.Visibility = Visibility.Visible;
        TermsBorder.Visibility = Visibility.Collapsed;
        RouteText.Text = "翻译失败 · 可检查网络或设置后重试";
    }

    internal static string FriendlyError(string message)
    {
        // Rate-limit messages also mention configuring a key, so match them
        // before the generic API Key hint.
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("限流", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("受限", StringComparison.OrdinalIgnoreCase))
        {
            return "翻译请求被限流，请稍后重试";
        }
        if (message.Contains("API Key", StringComparison.OrdinalIgnoreCase))
        {
            return "还差一步：配置模型密钥";
        }
        if (message.Contains("鉴权", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("403", StringComparison.OrdinalIgnoreCase))
        {
            return "密钥无效或没有权限";
        }
        if (message.Contains("未授权上传", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("上传截图", StringComparison.OrdinalIgnoreCase))
        {
            return "截图上传未获授权";
        }
        if (message.Contains("网络访问未启用", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("网络", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Safe", StringComparison.OrdinalIgnoreCase))
        {
            return "模型网络目前未启用";
        }
        if (message.Contains("选中", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("选区", StringComparison.OrdinalIgnoreCase))
        {
            return "没有读到选中的文字";
        }
        if (message.Contains("超时", StringComparison.OrdinalIgnoreCase))
        {
            return "模型响应超时";
        }
        return "这次没有翻译成功";
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_retry is null)
        {
            var text = SourceInputBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _retry = () => PerformTranslationAsync(text);
            }
        }
        if (_retry is not null)
        {
            await BeginOperationAsync(_ => _retry());
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_translation))
        {
            return;
        }
        try
        {
            Clipboard.SetText(_translation);
            StatusText.Text = "已复制译文到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void SourceCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = "已复制原文到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private void SourceClearButton_Click(object sender, RoutedEventArgs e)
    {
        SourceInputBox.Clear();
        TranslationTextBox.Clear();
        PhoneticText.Visibility = Visibility.Collapsed;
        ExplanationText.Visibility = Visibility.Collapsed;
        TermsBorder.Visibility = Visibility.Collapsed;
        StatusText.Text = "已清空";
        SourceInputBox.Focus();
    }

    private void SourceSpeakButton_Click(object sender, RoutedEventArgs e)
    {
        var text = SourceInputBox.Text.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            TtsService.Speak(text);
        }
    }

    private void ResultSpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_translation))
        {
            TtsService.Speak(_translation);
        }
    }

    private async void SourceInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            var text = SourceInputBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                _currentSource = text;
                _retry = () => PerformTranslationAsync(text);
                await BeginOperationAsync(_ => PerformTranslationAsync(text));
            }
        }
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspendAutoTranslate || !IsLoaded)
        {
            return;
        }
        var text = SourceInputBox.Text.Trim();
        if (!string.IsNullOrEmpty(text) && text != "正在读取选区…" && !text.StartsWith("正在 OCR"))
        {
            _retry = () => PerformTranslationAsync(text);
            await BeginOperationAsync(_ => PerformTranslationAsync(text));
        }
    }

    private async void SwapLangButton_Click(object sender, RoutedEventArgs e)
    {
        var currentSourceTag = ((ComboBoxItem)SourceLangCombo.SelectedItem).Tag?.ToString() ?? "zh-CN";
        var currentTargetTag = ((ComboBoxItem)TargetLangCombo.SelectedItem).Tag?.ToString() ?? "en";

        if (currentSourceTag == "auto")
        {
            currentSourceTag = "zh-CN";
        }

        // Move the previous translation into the source box before the combo
        // changes fire Language_SelectionChanged, so the re-translation uses
        // the swapped text instead of the original one.
        var swappedText = _translation;
        if (!string.IsNullOrWhiteSpace(swappedText))
        {
            SourceInputBox.Text = swappedText;
            _translation = string.Empty;
            TranslationTextBox.Clear();
        }

        _suspendAutoTranslate = true;
        try
        {
            SelectComboByTag(SourceLangCombo, currentTargetTag);
            SelectComboByTag(TargetLangCombo, currentSourceTag);
        }
        finally
        {
            _suspendAutoTranslate = false;
        }

        var text = SourceInputBox.Text.Trim();
        if (!string.IsNullOrEmpty(text) && text != "正在读取选区…" && !text.StartsWith("正在 OCR"))
        {
            _currentSource = text;
            _retry = () => PerformTranslationAsync(text);
            await BeginOperationAsync(_ => PerformTranslationAsync(text));
        }
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = true;
        PinButton.Foreground = _pinned
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("SecondaryText");
        StatusText.Text = _pinned ? "浮窗已置顶固定" : "浮窗已取消固定";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _userMoved = true;
            DragMove();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionNearAnchor();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void PositionNearAnchor()
    {
        if (_userMoved || !IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        var point = WindowPositioner.NearAnchor(
            _anchor,
            new Size(ActualWidth, Math.Min(ActualHeight, MaxHeight)),
            ScreenWorkArea.ForAnchor(_anchor));
        Left = point.X;
        Top = point.Y;
    }

    private void CancelOperation()
    {
        CoreBridge.CancelActiveRequest();
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = null;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
