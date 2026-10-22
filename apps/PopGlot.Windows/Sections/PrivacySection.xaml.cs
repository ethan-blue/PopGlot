using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

    private void PrivacySection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 560;
        Grid.SetColumn(FreeEngineConsentActions, compact ? 0 : 1);
        Grid.SetRow(FreeEngineConsentActions, compact ? 1 : 0);
        FreeEngineConsentActions.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
        Grid.SetColumn(ModeComboBox, compact ? 0 : 1);
        Grid.SetRow(ModeComboBox, compact ? 1 : 0);
        ModeComboBox.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ModeComboBox.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
    }

    // ================= Public accessors for MainWindow =================

    internal ToggleButton SafeMode => SafeModeToggle;
    internal ToggleButton NetworkEnabled => NetworkEnabledToggle;
    internal ToggleButton AllowImageUpload => AllowImageUploadToggle;
    internal ToggleButton CloudSpeech => CloudSpeechToggle;
    internal ComboBox ModeCombo => ModeComboBox;

    internal bool IsLoading { get => _loading; set => _loading = value; }

    internal void SetShellSettings(ShellSettings settings)
    {
        _shellSettings = settings;
        // 0.1.25：设置窗口每次同步设置快照时刷新 GitHub 上报卡（同意开关
        // 与凭据掩码），与 RefreshCloudSpeechState/RefreshFreeEngineState
        // 同一初始化路径。
        RefreshIssueUploadState();
    }

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
        if (IsRouteDraftPending)
        {
            RefreshDraftRoutePreview();
            return;
        }
        try
        {
            RenderRoute(
                ProfileManager.ResolveRoute(CoreBridge.GetSettings(), WindowsOcrService.IsSupported),
                "当前实际线路");
        }
        catch (Exception exception)
        {
            RouteCard.Visibility = Visibility.Visible;
            RouteBadgeText.Text = "未知";
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
            RouteDraftNote.Text = "保存后线路将按新设置重新评估。";
            RouteDraftNote.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            RouteCard.Visibility = Visibility.Visible;
            RouteBadgeText.Text = "未知";
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
        RouteCard.Visibility = Visibility.Visible;
        RouteCardTitle.Text = title;
        RouteBadgeText.Text = pipeline;
        RoutePreviewText.Text = route.ExplanationZh;
        var warning = route.MayUploadImage || route.ScreenshotPipeline == ScreenshotPipeline.Unavailable;
        RouteCard.SetResourceReference(Border.BackgroundProperty, warning ? "WarningSoftBrush" : "SurfaceMutedBrush");
        RouteBadge.SetResourceReference(Border.BackgroundProperty, warning ? "WarningSoftBrush" : "AccentSoftBrush");
        RouteBadge.SetResourceReference(Border.BorderBrushProperty, warning ? "WarningBrush" : "AccentBorderBrush");
        RouteBadgeText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "AccentBrush");
    }

    // ================= Cloud speech consent =================

    /// <summary>
    /// Loads the cloud-speech toggle from the persisted consent. The consent
    /// is independent from every translation permission: it only ever says
    /// "send read-aloud text to the Microsoft voice service".
    /// </summary>
    internal void RefreshCloudSpeechState()
    {
        if (CloudSpeechToggle is null)
        {
            return;
        }
        _loading = true;
        try
        {
            CloudSpeechToggle.IsChecked = _shellSettings.CloudSpeechEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    private void CloudSpeech_ToggleChanged(object sender, RoutedEventArgs e)
    {
        UpdateSafeModeGating();
        if (_loading)
        {
            return;
        }

        var enabled = CloudSpeechToggle.IsChecked == true;
        try
        {
            _shellSettings = ShellSettingsStore.Update(current => current with { CloudSpeechEnabled = enabled });
            SidebarChanged?.Invoke();
            StatusChanged?.Invoke(
                enabled
                    ? "已允许云端朗读：朗读文字将发送到 Microsoft 在线语音服务。"
                    : "已关闭云端朗读：仅使用 Windows 本地语音。",
                StatusTone.Info);
        }
        catch (Exception exception)
        {
            // Never leave the toggle claiming a state that was not persisted.
            _loading = true;
            try
            {
                CloudSpeechToggle.IsChecked = !enabled;
            }
            finally
            {
                _loading = false;
            }
            StatusChanged?.Invoke($"保存云端朗读选择失败：{exception.Message}", StatusTone.Error);
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
        SetFreeEngineConsent(FreeEngineConsent.Denied, "已停用公共翻译；自建引擎的联网许可不变。");

    private void ResetFreeEngineConsent_Click(object sender, RoutedEventArgs e) =>
        SetFreeEngineConsent(FreeEngineConsent.Unset, "已恢复首次询问。");

    private void SetFreeEngineConsent(FreeEngineConsent consent, string message)
    {
        try
        {
            _shellSettings = ShellSettingsStore.Update(current => current with { FreeEngineConsent = consent });
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
        if (CloudSpeechToggle is not null)
        {
            CloudSpeechToggle.IsEnabled = !safe;
        }
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

    // ================= GitHub 问题上报（自我迭代） =================

    private bool _issueUploadHasStoredPat;
    private bool _issueUploadTestBusy;

    /// <summary>
    /// Loads the consent checkbox from the persisted settings and paints the
    /// credential mask state. The consent is an independent ShellSettings
    /// bool persisted immediately on toggle — the same save flow the cloud
    /// speech consent on this page uses (ShellSettingsStore.Update +
    /// SidebarChanged), never a draft the page save bar could drop.
    /// </summary>
    internal void RefreshIssueUploadState()
    {
        if (IssueUploadConsentToggle is null || IssueUploadPatBox is null)
        {
            return;
        }
        _loading = true;
        try
        {
            IssueUploadConsentToggle.IsChecked = _shellSettings.IssueUploadConsent;
        }
        finally
        {
            _loading = false;
        }
        try
        {
            _issueUploadHasStoredPat = CredentialStore.HasApiKey(GitHubIssueReporter.CredentialTarget);
        }
        catch (Exception)
        {
            // An unreadable vault must not brick the page; treat as absent.
            _issueUploadHasStoredPat = false;
        }
        Ui.SetIsCredentialMask(IssueUploadPatBox, _issueUploadHasStoredPat && IssueUploadPatBox.Password.Length == 0);
        Ui.SetPlaceholder(IssueUploadPatBox,
            _issueUploadHasStoredPat ? "••••••••••••  已保存，留空保留" : "粘贴 GitHub PAT（留空保留）");
        UpdateIssueUploadGating();
    }

    /// <summary>
    /// 「发送测试诊断」仅在同意为开且令牌可用（已保存或刚粘贴）时可用；
    /// 禁用时仍显示提示（ToolTipService.ShowOnDisabled），说明缺什么。
    /// </summary>
    private void UpdateIssueUploadGating()
    {
        if (IssueUploadTestButton is null)
        {
            return;
        }
        var consent = IssueUploadConsentToggle.IsChecked == true;
        var hasPat = _issueUploadHasStoredPat || IssueUploadPatBox.Password.Length > 0;
        IssueUploadTestButton.IsEnabled = consent && hasPat && !_issueUploadTestBusy;
    }

    private void IssueUploadConsent_Changed(object sender, RoutedEventArgs e)
    {
        UpdateIssueUploadGating();
        if (_loading)
        {
            return;
        }

        var enabled = IssueUploadConsentToggle.IsChecked == true;
        try
        {
            // 同意变化就是本卡的保存时刻：一并落盘刚粘贴的令牌（留空则保留）。
            CommitIssueUploadCredential();
            _shellSettings = ShellSettingsStore.Update(current => current with { IssueUploadConsent = enabled });
            SidebarChanged?.Invoke();
            StatusChanged?.Invoke(
                enabled
                    ? "已同意在程序崩溃时上传脱敏诊断到 GitHub Issues；可随时取消勾选撤销。"
                    : "已撤销同意：不再向 GitHub 上传任何诊断。",
                StatusTone.Info);
        }
        catch (Exception exception)
        {
            // Never leave the checkbox claiming a state that was not persisted.
            _loading = true;
            try
            {
                IssueUploadConsentToggle.IsChecked = !enabled;
            }
            finally
            {
                _loading = false;
            }
            StatusChanged?.Invoke($"保存 GitHub 上报选择失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void IssueUploadPat_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Ui.SetIsCredentialMask(IssueUploadPatBox, _issueUploadHasStoredPat && IssueUploadPatBox.Password.Length == 0);
        UpdateIssueUploadGating();
    }

    /// <summary>
    /// Writes a newly pasted PAT into the Windows credential vault and clears
    /// the field (no fake mask ever re-enters the control). An empty field
    /// deliberately keeps the stored token — 留空保留. Throws on vault failure
    /// so callers report honestly.
    /// </summary>
    internal bool CommitIssueUploadCredential()
    {
        if (IssueUploadPatBox is null)
        {
            return false;
        }
        var typed = IssueUploadPatBox.Password.Trim();
        if (typed.Length == 0)
        {
            return false;
        }
        CredentialStore.SaveApiKey(typed, GitHubIssueReporter.CredentialTarget);
        IssueUploadPatBox.Clear();
        _issueUploadHasStoredPat = true;
        Ui.SetIsCredentialMask(IssueUploadPatBox, true);
        Ui.SetPlaceholder(IssueUploadPatBox, "••••••••••••  已保存，留空保留");
        UpdateIssueUploadGating();
        return true;
    }

    private void IssueUploadPatClear_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CredentialStore.DeleteApiKey(GitHubIssueReporter.CredentialTarget);
            IssueUploadPatBox.Clear();
            _issueUploadHasStoredPat = false;
            Ui.SetIsCredentialMask(IssueUploadPatBox, false);
            Ui.SetPlaceholder(IssueUploadPatBox, "粘贴 GitHub PAT（留空保留）");
            UpdateIssueUploadGating();
            IssueUploadStatusText.Text = string.Empty;
            StatusChanged?.Invoke("已从 Windows 凭据管理器清除 GitHub 访问令牌。", StatusTone.Info);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"清除令牌失败：{exception.Message}", StatusTone.Error);
        }
    }

    private async void IssueUploadTest_Click(object sender, RoutedEventArgs e)
    {
        if (_issueUploadTestBusy)
        {
            return;
        }
        _issueUploadTestBusy = true;
        UpdateIssueUploadGating();
        IssueUploadStatusText.Text = "正在发送测试诊断…";
        try
        {
            // 测试前先把刚粘贴的令牌落盘（留空保留），上传始终读凭据库。
            CommitIssueUploadCredential();
            var title = $"[test] GitHub 上报连通性 {GitHubIssueReporter.AssemblyVersionString}";
            var (ok, issueUrl, error) = await GitHubIssueReporter.UploadAsync(
                title, GitHubIssueReporter.BuildConnectivityReport());
            if (ok && !string.IsNullOrEmpty(issueUrl))
            {
                RenderIssueUploadStatusLink(issueUrl);
            }
            else
            {
                IssueUploadStatusText.Inlines.Clear();
                IssueUploadStatusText.Inlines.Add(
                    $"测试失败：{error ?? "GitHub 未返回 Issue 地址。"}");
            }
        }
        catch (Exception exception)
        {
            IssueUploadStatusText.Inlines.Clear();
            IssueUploadStatusText.Inlines.Add($"测试失败：{GitHubIssueReporter.SafeError(exception)}");
        }
        finally
        {
            _issueUploadTestBusy = false;
            UpdateIssueUploadGating();
        }
    }

    /// <summary>
    /// Renders "已创建测试 Issue：<link>"；链接沿用帮助/Markdown 渲染的同款
    /// 安全打开方式：仅 http/https 且 UseShellExecute，异常一律吞掉。
    /// </summary>
    private void RenderIssueUploadStatusLink(string issueUrl)
    {
        IssueUploadStatusText.Inlines.Clear();
        if (Uri.TryCreate(issueUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            var link = new Hyperlink(new Run(uri.AbsoluteUri))
            {
                NavigateUri = uri,
                ToolTip = uri.AbsoluteUri,
                Cursor = Cursors.Hand,
            };
            link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
            link.RequestNavigate += (_, args) =>
            {
                try
                {
                    if (args.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        args.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                    }
                }
                catch
                {
                }
                args.Handled = true;
            };
            IssueUploadStatusText.Inlines.Add("已创建测试 Issue：");
            IssueUploadStatusText.Inlines.Add(link);
        }
        else
        {
            IssueUploadStatusText.Inlines.Add($"已创建测试 Issue：{issueUrl}");
        }
    }
}
