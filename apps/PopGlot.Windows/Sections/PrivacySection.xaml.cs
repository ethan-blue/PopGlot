using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class PrivacySection : System.Windows.Controls.UserControl
{
    private bool _loading;
    private ShellSettings _shellSettings = ShellSettings.Default;

    /// <summary>Raised when the section needs to show a status message in the footer.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised when provider gate toggles change (dirty tracking).</summary>
    internal event Action? ProviderDirty;

    /// <summary>Raised when free engine consent or sidebar-relevant state changes.</summary>
    internal event Action? SidebarChanged;

    public PrivacySection()
    {
        InitializeComponent();
    }

    // ================= Public accessors for MainWindow =================

    internal ToggleButton SafeMode => SafeModeToggle;
    internal ToggleButton NetworkEnabled => NetworkEnabledToggle;
    internal ToggleButton AllowImageUpload => AllowImageUploadToggle;
    internal ComboBox ModeCombo => ModeComboBox;

    internal bool IsLoading { get => _loading; set => _loading = value; }

    internal void SetShellSettings(ShellSettings settings) => _shellSettings = settings;

    // ================= OCR =================

    internal void LoadOcrState()
    {
        try
        {
            if (WindowsOcrService.IsSupported)
            {
                var languages = WindowsOcrService.AvailableLanguageDescriptions;
                OcrStatusText.Text = $"已就绪，检测到 {languages.Count} 个离线识别语言包。";
                OcrLanguagesListBox.ItemsSource = languages;
            }
            else
            {
                OcrStatusText.Text = "系统没有安装任何 Windows OCR 语言包，本地识别不可用。";
                OcrLanguagesListBox.ItemsSource = new[] { "未检测到语言包" };
            }
        }
        catch (Exception exception)
        {
            OcrStatusText.Text = $"检测 OCR 状态失败：{exception.Message}";
        }
    }

    // ================= Route preview =================

    internal void RefreshRoutePreview()
    {
        try
        {
            RenderRoute(
                ProfileManager.ResolveRoute(CoreBridge.GetSettings(), WindowsOcrService.IsSupported),
                "当前实际线路");
        }
        catch (Exception exception)
        {
            RoutePreviewText.Text = $"无法判断线路：{exception.Message}";
        }
    }

    /// <summary>Marks the route card as stale while the form has unsaved changes.</summary>
    internal void SetRouteDraftPending(bool pending)
    {
        RouteDraftNote.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>True while the route card is flagged as a stale draft.</summary>
    internal bool IsRouteDraftPending => RouteDraftNote.Visibility == Visibility.Visible;

    private void RefreshDraftRoutePreview()
    {
        try
        {
            var draft = CoreBridge.GetSettings() with
            {
                Mode = Helpers.SelectedEnum(ModeComboBox, TranslationMode.Auto),
                AllowImageUploadInAuto = AllowImageUploadToggle.IsChecked == true,
                NetworkEnabled = NetworkEnabledToggle.IsChecked == true,
                SafeDevMode = SafeModeToggle.IsChecked == true,
            };
            RenderRoute(
                ProfileManager.ResolveRoute(draft, WindowsOcrService.IsSupported),
                "保存后预计线路");
            RouteDraftNote.Text = "这是未保存设置的预计线路；保存后成为实际线路。";
            RouteDraftNote.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            RoutePreviewText.Text = $"无法预估保存后的线路：{exception.Message}";
        }
    }

    private void RenderRoute(ResolvedRoute route, string title)
    {
        var pipeline = route.ScreenshotPipeline switch
        {
            ScreenshotPipeline.VisionDirect => "视觉模型",
            ScreenshotPipeline.VisionOcr => "视觉识别 + 文本模型",
            ScreenshotPipeline.LocalOcr => "本地 OCR",
            _ => "不可用",
        };
        RouteCardTitle.Text = title;
        RouteBadgeText.Text = pipeline;
        RoutePreviewText.Text = $"截图将走「{pipeline}」。{route.ExplanationZh}";
        var warning = route.MayUploadImage || route.ScreenshotPipeline == ScreenshotPipeline.Unavailable;
        RouteCard.Background = (Brush)FindResource(warning ? "WarningSoftBrush" : "SurfaceMutedBrush");
        RouteBadge.Background = (Brush)FindResource(warning ? "WarningSoftBrush" : "AccentSoftBrush");
        RouteBadge.BorderBrush = (Brush)FindResource(warning ? "WarningBrush" : "AccentBorderBrush");
        RouteBadgeText.Foreground = (Brush)FindResource(warning ? "WarningBrush" : "AccentBrush");
    }

    // ================= Free engine consent =================

    internal void RefreshFreeEngineState()
    {
        if (FreeEngineConsentState is null)
        {
            return;
        }
        FreeEngineConsentState.Text = _shellSettings.FreeEngineConsent switch
        {
            FreeEngineConsent.Allowed => "当前状态：已允许",
            FreeEngineConsent.Denied => "当前状态：已拒绝（不会出网）",
            _ => "当前状态：首次使用时会询问",
        };
    }

    private void AllowFreeEngine_Click(object sender, RoutedEventArgs e) =>
        SetFreeEngineConsent(FreeEngineConsent.Allowed, "已允许内置免费引擎。");

    private void DenyFreeEngine_Click(object sender, RoutedEventArgs e) =>
        SetFreeEngineConsent(FreeEngineConsent.Denied, "已拒绝内置免费引擎，翻译将不再出网。");

    private void ResetFreeEngineConsent_Click(object sender, RoutedEventArgs e) =>
        SetFreeEngineConsent(FreeEngineConsent.Unset, "已恢复首次询问。");

    private void SetFreeEngineConsent(FreeEngineConsent consent, string message)
    {
        try
        {
            var updated = _shellSettings with { FreeEngineConsent = consent };
            ShellSettingsStore.Save(updated);
            _shellSettings = updated;
            RefreshFreeEngineState();
            SidebarChanged?.Invoke();
            StatusChanged?.Invoke(message, StatusTone.Info);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"保存选择失败：{exception.Message}", StatusTone.Error);
        }
    }

    internal ShellSettings CurrentShellSettings => _shellSettings;

    // ================= Gate change handlers =================

    /// <summary>
    /// Safe mode is the total switch: while it is on, every outbound option
    /// below it is disabled and the reason is spelled out inline.
    /// </summary>
    internal void UpdateSafeModeGating()
    {
        var safe = SafeModeToggle.IsChecked == true;
        NetworkEnabledToggle.IsEnabled = !safe;
        ModeComboBox.IsEnabled = !safe;
        AllowImageUploadToggle.IsEnabled = !safe;
        SafeModeGateNote.Visibility = safe ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProviderGate_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSafeModeGating();
        if (_loading)
        {
            return;
        }
        RefreshDraftRoutePreview();
        ProviderDirty?.Invoke();
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        RefreshDraftRoutePreview();
        ProviderDirty?.Invoke();
    }
}
