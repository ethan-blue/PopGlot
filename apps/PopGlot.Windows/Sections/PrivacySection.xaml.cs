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
            // Same credential resolution as the engine status and the actual
            // translation route — never the legacy default slot.
            var route = CoreBridge.PlanScreenshotRoute(
                WindowsOcrService.IsSupported,
                CredentialStore.HasApiKey(ProfileManager.ResolveActiveCredentialTarget()));
            var pipeline = route.MayUploadImage ? "视觉模型" : "本地 OCR";
            RouteBadgeText.Text = pipeline;
            RouteCardTitle.Text = "当前实际线路";
            RoutePreviewText.Text = $"截图将走「{pipeline}」。{route.ExplanationZh}";
            // Uploading screenshots is the privacy-relevant case; the card
            // switches to the warning surface so it reads as a boundary.
            RouteCard.Background = (Brush)FindResource(
                route.MayUploadImage ? "WarningSoftBrush" : "SurfaceMutedBrush");
            RouteBadge.Background = (Brush)FindResource(
                route.MayUploadImage ? "WarningSoftBrush" : "AccentSoftBrush");
            RouteBadge.BorderBrush = (Brush)FindResource(
                route.MayUploadImage ? "WarningBrush" : "AccentBorderBrush");
            RouteBadgeText.Foreground = (Brush)FindResource(
                route.MayUploadImage ? "WarningBrush" : "AccentBrush");
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

    private void RefreshDraftRoutePreview()
    {
        try
        {
            var current = CoreBridge.GetSettings();
            var mode = Helpers.SelectedEnum(ModeComboBox, TranslationMode.Auto);
            var uploadAllowed = AllowImageUploadToggle.IsChecked == true;
            var networkAllowed = NetworkEnabledToggle.IsChecked == true && SafeModeToggle.IsChecked != true;
            var visionReady = current.SupportsVision &&
                !string.IsNullOrWhiteSpace(current.VisionModel) &&
                networkAllowed &&
                (current.TargetsLocalRuntime || CredentialStore.HasApiKey(ProfileManager.ResolveActiveCredentialTarget()));
            var useVision = mode switch
            {
                TranslationMode.VisionDirect => visionReady && uploadAllowed,
                TranslationMode.Auto => !WindowsOcrService.IsSupported && visionReady && uploadAllowed,
                _ => false,
            };
            var pipeline = useVision ? "视觉模型" : "本地 OCR";
            RouteCardTitle.Text = "保存后预计线路";
            RouteBadgeText.Text = pipeline;
            RoutePreviewText.Text = mode switch
            {
                TranslationMode.VisionDirect when useVision => "视觉模型将直接读取并翻译截图。",
                TranslationMode.VisionDirect when !uploadAllowed => "未允许上传截图，将回退到本地 OCR。",
                TranslationMode.VisionDirect => "视觉模型当前不可用，将回退到本地 OCR。",
                TranslationMode.LocalOcr => "截图只在本机 OCR，随后把文字交给文本模型。",
                _ when useVision => "本机没有可用 OCR，自动模式将使用视觉模型。",
                _ => "自动模式优先使用本地 OCR，不上传截图。",
            };
            RouteDraftNote.Text = "这是未保存设置的预计线路；保存后成为实际线路。";
            RouteDraftNote.Visibility = Visibility.Visible;
            RouteCard.Background = (Brush)FindResource(useVision ? "WarningSoftBrush" : "SurfaceMutedBrush");
            RouteBadge.Background = (Brush)FindResource(useVision ? "WarningSoftBrush" : "AccentSoftBrush");
            RouteBadge.BorderBrush = (Brush)FindResource(useVision ? "WarningBrush" : "AccentBorderBrush");
            RouteBadgeText.Foreground = (Brush)FindResource(useVision ? "WarningBrush" : "AccentBrush");
        }
        catch (Exception exception)
        {
            RoutePreviewText.Text = $"无法预估保存后的线路：{exception.Message}";
        }
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
