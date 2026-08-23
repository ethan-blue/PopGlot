using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Size = System.Windows.Size;

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
    private string _translation = string.Empty;
    private string _routeLabel = "文本模型";

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
    }

    internal async Task StartSelectionAsync(ClipboardSelectionService selectionService)
    {
        ArgumentNullException.ThrowIfNull(selectionService);
        await BeginOperationAsync(async cancellation =>
        {
            RenderState(TranslationSessionState.ReadingSelection);
            SourceLabel.Text = "所选文字";
            _routeLabel = "划词 · 文本模型";
            SourceText.Text = "正在安全读取选区并恢复剪贴板…";
            var source = await selectionService.ReadSelectionAsync(cancellation);
            SourceText.Text = source;
            _retry = () => TranslateTextAsync(source);
            await TranslateTextAsync(source);
        });
    }

    internal async Task StartScreenshotAsync(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        await BeginOperationAsync(async cancellation =>
        {
            RenderState(TranslationSessionState.Capturing);
            SourceLabel.Text = "截图";
            var mode = CoreBridge.GetSettings().Mode;
            _routeLabel = mode switch
            {
                TranslationMode.Auto => "自动 · 本地 OCR 未安装，使用视觉直译",
                TranslationMode.LocalOcr => "截图 · 本地 OCR",
                TranslationMode.VisionDirect => "截图 · 视觉直译",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            RouteText.Text = _routeLabel;
            SourceText.Text = $"已捕获截图 · {image.Length / 1024.0:0.#} KiB";
            _retry = () => TranslateVisionAsync(image);
            await TranslateVisionAsync(image, cancellation);
        });
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

    private async Task TranslateTextAsync(string source)
    {
        var cancellation = _operation?.Token ?? CancellationToken.None;
        RenderState(TranslationSessionState.Translating);
        cancellation.ThrowIfCancellationRequested();
        var response = await CoreBridge.TranslateTextAsync(RequireApiKey(), source);
        cancellation.ThrowIfCancellationRequested();
        RenderResult("划词", source, response);
    }

    private async Task TranslateVisionAsync(byte[] image, CancellationToken? cancellation = null)
    {
        var token = cancellation ?? _operation?.Token ?? CancellationToken.None;
        RenderState(TranslationSessionState.Translating);
        token.ThrowIfCancellationRequested();
        var response = await CoreBridge.TranslateVisionAsync(RequireApiKey(), "image/png", image);
        token.ThrowIfCancellationRequested();
        var source = string.IsNullOrWhiteSpace(response.Result.Transcription)
            ? "截图内容（模型未返回转录）"
            : response.Result.Transcription;
        SourceText.Text = source;
        RenderResult("截图", source, response);
    }

    private static string RequireApiKey()
    {
        var apiKey = CredentialStore.LoadApiKey();
        return string.IsNullOrWhiteSpace(apiKey)
            ? throw new InvalidOperationException("尚未配置当前提供商的 API Key。请打开设置完成配置。")
            : apiKey;
    }

    private void RenderState(TranslationSessionState state)
    {
        StatusText.Text = TranslationSessionStateText.Describe(state);
        Progress.Visibility = state is TranslationSessionState.Completed or
            TranslationSessionState.Failed or TranslationSessionState.Cancelled
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusDot.Background = state == TranslationSessionState.Failed
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("AccentBrush");
        if (state is not TranslationSessionState.Completed)
        {
            CopyButton.IsEnabled = false;
        }
    }

    private void RenderResult(string sourceKind, string source, TranslationResponse response)
    {
        RenderState(TranslationSessionState.Completed);
        _translation = response.Result.TranslatedText;
        TranslationText.Text = _translation;
        ExplanationText.Text = response.Result.Explanation;
        ExplanationText.Visibility = string.IsNullOrWhiteSpace(response.Result.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TermsText.Text = response.Result.ProtectedTerms.Count == 0
            ? string.Empty
            : $"原样保留 · {string.Join("   ", response.Result.ProtectedTerms)}";
        TermsBorder.Visibility = response.Result.ProtectedTerms.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        RouteText.Text = $"{_routeLabel} · {response.Diagnostics.ProviderType} · {response.Diagnostics.ElapsedMs} ms";
        CopyButton.IsEnabled = true;
        RetryButton.Visibility = Visibility.Visible;

        var historyResult = _history.TryAdd(
            new TranslationHistoryEntry(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                sourceKind,
                source,
                response.Result.TranslatedText,
                response.Result.Explanation,
                response.Result.ProtectedTerms),
            _shellSettings().HistoryEnabled);
        if (historyResult == HistoryAddResult.SkippedSensitiveOrLarge)
        {
            StatusText.Text = "翻译完成 · 敏感或过大内容未记历史";
        }
        else if (historyResult == HistoryAddResult.Failed)
        {
            StatusText.Text = "翻译完成 · 本地历史写入失败";
        }
    }

    private void RenderFailure(string message)
    {
        RenderState(TranslationSessionState.Failed);
        TranslationText.Text = FriendlyError(message);
        ExplanationText.Text = message;
        ExplanationText.Visibility = Visibility.Visible;
        TermsBorder.Visibility = Visibility.Collapsed;
        RouteText.Text = "未完成 · 可检查设置后重试";
        RetryButton.Visibility = _retry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string FriendlyError(string message)
    {
        if (message.Contains("API Key", StringComparison.OrdinalIgnoreCase))
        {
            return "还差一步：配置模型密钥";
        }
        if (message.Contains("网络", StringComparison.OrdinalIgnoreCase) ||
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
            return;
        }
        await BeginOperationAsync(_ => _retry());
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_translation);
            StatusText.Text = "译文已复制";
        }
        catch (Exception exception)
        {
            RenderFailure($"复制译文失败：{exception.Message}");
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        PinButton.Content = _pinned ? "已固定" : "固定";
        PinButton.Background = _pinned
            ? (Brush)FindResource("AccentMutedBrush")
            : Brushes.Transparent;
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
