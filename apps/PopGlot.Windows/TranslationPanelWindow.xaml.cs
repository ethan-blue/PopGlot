using System.Windows;
using System.Windows.Input;

namespace PopGlot.Windows;

public partial class TranslationPanelWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public TranslationPanelWindow(Rect selection)
    {
        InitializeComponent();
        PositionNear(selection);
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    internal async Task RunSafePreviewAsync()
    {
        try
        {
            StatusText.Text = "正在分析内容类型…";
            await Task.Delay(180, _lifetime.Token);
            StatusText.Text = "正在选择安全翻译管线…";
            await Task.Delay(180, _lifetime.Token);

            var settings = CoreBridge.GetSettings();
            var result = CoreBridge.Preview(new PreviewRequest(
                settings.Mode,
                "NullReferenceException in getUserProfile at C:\\src\\UserService.cs --verbose",
                LooksLikeCode: true,
                ComplexLayout: false,
                ImageQuality: 0.9f,
                OcrConfidence: 0.9f));

            StatusText.Text = result.RequiresConfiguration ? "需要配置" : "安全预览完成";
            TranslationText.Text = result.TranslatedText;
            ExplanationText.Text = $"{result.Decision.ExplanationZh}\n{result.Explanation}";
            TermsText.Text = result.ProtectedTerms.Count == 0
                ? "未检测到需要保护的技术元素"
                : string.Join("  ·  ", result.ProtectedTerms);
            TermsBorder.Visibility = Visibility.Visible;
            RouteText.Text = result.Decision.SelectedMode switch
            {
                TranslationMode.LocalOcr => "本地 OCR 路线",
                TranslationMode.VisionDirect => "视觉直译路线",
                _ => "自动路线",
            };
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window ownership ended; no UI update is required.
        }
        catch (Exception exception)
        {
            StatusText.Text = "处理失败";
            TranslationText.Text = "PopGlot 无法完成本次安全预览。";
            ExplanationText.Text = exception.Message;
            RouteText.Text = "未发送网络请求";
        }
    }

    private void PositionNear(Rect selection)
    {
        var workArea = SystemParameters.WorkArea;
        var preferredLeft = selection.Right + 14;
        Left = preferredLeft + Width <= workArea.Right
            ? preferredLeft
            : Math.Max(workArea.Left, selection.Left - Width - 14);
        Top = Math.Clamp(selection.Top, workArea.Top + 12, workArea.Bottom - 360);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TranslationText.Text);
            StatusText.Text = "已复制";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{exception.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
