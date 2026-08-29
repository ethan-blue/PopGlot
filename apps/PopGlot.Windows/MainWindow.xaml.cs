using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>One row in the history list, shaped for display.</summary>
internal sealed record VocabularyRow(
    VocabularyWord Word,
    string PhoneticDisplay,
    string Timestamp,
    string Source,
    string Translation);

internal sealed record HistoryRow(
    TranslationHistoryEntry Entry,
    string Kind,
    string Timestamp,
    string LanguagePair,
    string Source,
    string Translation);

/// <summary>One row in the service list, shaped for display.</summary>
internal sealed record ProfilesRow(
    string Id,
    string Name,
    string Protocol,
    string Models,
    string Kind,
    System.Windows.Visibility IsDefaultBadge);

public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore? _vocabulary;
    private readonly TranslationCoordinator _coordinator;
    private ShellSettings _shellSettings;
    private IReadOnlyList<TranslationHistoryEntry> _allHistory = [];
    private CancellationTokenSource? _translateOperation;
    private bool _loading = true;
    private bool _hasUnsavedProviderChanges;
    private string? _editingProfileId;
    private bool _wizardActive;
    private int _wizardStep;

    internal MainWindow(ShellSettings shellSettings, HistoryStore history, VocabularyStore? vocabulary = null)
    {
        _shellSettings = shellSettings;
        _history = history;
        _vocabulary = vocabulary;
        _coordinator = new TranslationCoordinator(history, vocabulary);

        InitializeComponent();

        TranslateSourceLang.ItemsSource = LanguageCatalog.Sources;
        TranslateTargetLang.ItemsSource = LanguageCatalog.Targets;

        LoadAll();
        _loading = false;

        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += (_, _) => ThemeService.ApplyWindowChrome(this);
        StateChanged += (_, _) => UpdateMaximizeButtonGlyph();
    }

    // ================= Window Caption Controls =================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonGlyph();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void UpdateMaximizeButtonGlyph()
    {
        if (MaximizeBtn is null) return;
        Ui.SetIcon(
            MaximizeBtn,
            (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconCaptionRestore" : "IconCaptionMax"));
        MaximizeBtn.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";
    }

    internal bool AllowClose { get; set; }
    internal Func<ShellSettings, bool>? ApplyShellSettings { get; init; }

    // ================= Loading =================

    private void LoadAll()
    {
        LoadShellSettings(_shellSettings);
        LoadPolicySettings();
        LoadActiveProfileIntoForm();
        RefreshProfilesList();
        LoadOcrState();
        ReloadHistory();
        RefreshSidebar();
        RefreshRoutePreview();
    }

    private void LoadShellSettings(ShellSettings settings)
    {
        SelectionHotkeyRecorder.BindingValue = settings.SelectionHotkey;
        ScreenshotHotkeyRecorder.BindingValue = settings.ScreenshotHotkey;
        CloseHotkeyRecorder.BindingValue = settings.CloseHotkey;
        ShowWindowHotkeyRecorder.BindingValue = settings.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault;
        HistoryEnabledToggle.IsChecked = settings.HistoryEnabled;
        CloseOnFocusLossToggle.IsChecked = settings.ClosePanelOnFocusLoss;
        AutoCopyToggle.IsChecked = settings.CopyTranslationAutomatically;
        StartWithWindowsToggle.IsChecked = settings.StartWithWindows || StartupRegistration.IsEnabled();
        SelectComboByTag(ThemeComboBox, settings.Theme.ToString());
        RefreshFreeEngineState();
    }

    /// <summary>
    /// Loads the outbound-policy and behaviour toggles that are shared by every
    /// service — never the per-service endpoint/model fields.
    /// </summary>
    private void LoadPolicySettings()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var settings = CoreBridge.GetSettings();
            NetworkEnabledToggle.IsChecked = settings.NetworkEnabled;
            SafeModeToggle.IsChecked = settings.SafeDevMode;
            AllowImageUploadToggle.IsChecked = settings.AllowImageUploadInAuto;
            IncludeExplanationToggle.IsChecked = settings.IncludeExplanation;
            ProtectTokensToggle.IsChecked = settings.ProtectCodeTokens;
            SelectComboByTag(ModeComboBox, settings.Mode.ToString());

            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(settings.SourceLanguage);
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(settings.TargetLanguage);
        }
        catch (Exception exception)
        {
            SetStatus($"读取设置失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>Fills the service editor with the currently active profile.</summary>
    private void LoadActiveProfileIntoForm()
    {
        _editingProfileId = ProfileManager.Load().ActiveProfileId;
        var profile = ProfileManager.Load().GetActiveProfile();
        ServiceEditorTitle.Text = "编辑服务";
        LoadProfileIntoForm(profile);
        ApplyWizardStep();
        RefreshApiKeyState();
    }

    private void LoadProfileIntoForm(Services.ProviderProfile profile)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            ServiceNameTextBox.Text = profile.Name;
            SelectComboByTag(ProviderTypeComboBox, profile.ProviderType.ToString());
            BaseUrlTextBox.Text = profile.ApiBaseUrl;
            TextEndpointTextBox.Text = profile.TextEndpoint;
            VisionEndpointTextBox.Text = profile.VisionEndpoint;
            TextModelTextBox.Text = profile.TextModel;
            VisionModelTextBox.Text = profile.VisionModel;
            ExtraHeadersTextBox.Text = string.Join(
                Environment.NewLine,
                profile.ExtraHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
            AnthropicVersionTextBox.Text = profile.AnthropicVersion;
            SupportsTextCheckBox.IsChecked = profile.SupportsText;
            SupportsVisionCheckBox.IsChecked = profile.SupportsVision;
            AllowInsecureTlsCheckBox.IsChecked = profile.AllowInsecureTls;
            ApiKeyPasswordBox.Clear();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>The credential target for what the editor is showing right now.</summary>
    private string CurrentCredentialTarget()
    {
        if (_editingProfileId is not null)
        {
            var profile = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.CredentialTarget))
            {
                // Continuity: a key saved before profiles existed lives at the
                // legacy target; keep reading it until the user edits the key.
                if (CredentialStore.HasApiKey(profile.CredentialTarget))
                {
                    return profile.CredentialTarget;
                }
                if (CredentialStore.HasApiKey(CredentialStore.DefaultTargetName))
                {
                    return CredentialStore.DefaultTargetName;
                }
                return profile.CredentialTarget;
            }
        }
        return CredentialStore.DefaultTargetName;
    }

    private void RefreshApiKeyState()
    {
        try
        {
            var target = CurrentCredentialTarget();
            ApiKeyStateText.Text = CredentialStore.HasApiKey(target)
                ? "该服务的密钥已保存在 Windows 凭据管理器。输入框留空即保持不变。"
                : "该服务尚未配置密钥。本地模型（Ollama 等）无需密钥；未配置且未允许免费引擎时不会出网。";
        }
        catch (Exception exception)
        {
            ApiKeyStateText.Text = $"无法读取密钥状态：{exception.Message}";
        }
    }

    private void LoadOcrState()
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

    /// <summary>Sidebar summary of what is actually configured right now.</summary>
    private void RefreshSidebar()
    {
        var showWindow = _shellSettings.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault;
        ShortcutHintText.Text =
            $"{_shellSettings.SelectionHotkey.DisplayName}   划词翻译\n" +
            $"{_shellSettings.ScreenshotHotkey.DisplayName}   截图翻译\n" +
            $"{_shellSettings.CloseHotkey.DisplayName}   关闭浮窗\n" +
            $"{showWindow.DisplayName}   打开主窗口";

        try
        {
            var settings = CoreBridge.GetSettings();
            var hasKey = CredentialStore.HasApiKey();
            var (summary, tone) = DescribeEngine(settings, hasKey, _shellSettings.FreeEngineConsent);
            EngineSummary.Text = summary;
            EngineDot.Background = (Brush)FindResource(tone switch
            {
                StatusTone.Error => "DangerBrush",
                StatusTone.Warning => "WarningBrush",
                _ => "AccentBrush",
            });
        }
        catch (InvalidOperationException)
        {
            EngineSummary.Text = "配置不可用";
        }
    }

    private static (string Summary, StatusTone Tone) DescribeEngine(
        ProviderSettings settings, bool hasKey, FreeEngineConsent consent)
    {
        if (settings.SafeDevMode)
        {
            return ("安全离线模式", StatusTone.Warning);
        }
        if (!settings.NetworkEnabled)
        {
            return ("网络翻译已关闭", StatusTone.Warning);
        }
        if (!hasKey && !settings.TargetsLocalRuntime)
        {
            return consent == FreeEngineConsent.Denied
                ? ("免费引擎已关闭，且未配置模型服务", StatusTone.Warning)
                : ("内置免费引擎", StatusTone.Info);
        }
        return (string.IsNullOrWhiteSpace(settings.TextModel)
            ? "未填写文本模型"
            : settings.TextModel, StatusTone.Success);
    }

    // ================= Free web engine consent =================

    private void RefreshFreeEngineState()
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
            RefreshSidebar();
            SetStatus(message, StatusTone.Info);
        }
        catch (Exception exception)
        {
            SetStatus($"保存选择失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void RefreshRoutePreview()
    {
        try
        {
            var route = CoreBridge.PlanScreenshotRoute(
                WindowsOcrService.IsSupported,
                CredentialStore.HasApiKey());
            var pipeline = route.MayUploadImage ? "视觉模型" : "本地 OCR";
            var text = $"截图将走「{pipeline}」。{route.ExplanationZh}";
            if (_hasUnsavedProviderChanges)
            {
                text += "\n（以上基于已保存的设置；当前改动需点击“保存设置”后才会生效。）";
            }
            RoutePreviewText.Text = text;
        }
        catch (Exception exception)
        {
            RoutePreviewText.Text = $"无法判断线路：{exception.Message}";
        }
    }

    // ================= Navigation =================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && _loading)
        {
            return;
        }
        var tag = (sender as RadioButton)?.Tag as string;
        ShowSection(tag);
    }

    private void ShowSection(string? tag)
    {
        TranslateSection.Visibility = Visibility.Collapsed;
        LibrarySection.Visibility = Visibility.Collapsed;
        GeneralSection.Visibility = Visibility.Collapsed;
        ShortcutsSection.Visibility = Visibility.Collapsed;
        ProviderSection.Visibility = Visibility.Collapsed;
        CaptureSection.Visibility = Visibility.Collapsed;
        DataSection.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "Library":
                ReloadHistory();
                ReloadVocabulary();
                LibrarySection.Visibility = Visibility.Visible;
                break;
            case "General":
                GeneralSection.Visibility = Visibility.Visible;
                break;
            case "Shortcuts":
                ShortcutsSection.Visibility = Visibility.Visible;
                break;
            case "Provider":
                RefreshApiKeyState();
                RefreshProfilesList();
                ProviderSection.Visibility = Visibility.Visible;
                break;
            case "Capture":
                LoadOcrState();
                RefreshRoutePreview();
                CaptureSection.Visibility = Visibility.Visible;
                break;
            case "Data":
                DataSection.Visibility = Visibility.Visible;
                break;
            default:
                TranslateSection.Visibility = Visibility.Visible;
                break;
        }

        // Each section starts at its own top; carrying the previous scroll
        // offset over made short sections look blank.
        ContentScroll?.ScrollToTop();
    }

    // ================= Standalone translation =================

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
        var source = TranslateInput.Text.Trim();
        if (string.IsNullOrEmpty(source))
        {
            TranslateStatus.Text = "请先输入要翻译的内容。";
            return;
        }

        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        var operation = new CancellationTokenSource();
        _translateOperation = operation;

        var sourceLang = SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
        var targetLang = SelectedLanguage(TranslateTargetLang, "zh-CN");

        TranslateButton.IsEnabled = false;
        TranslateStatus.Text = "正在翻译…";
        TranslateEngineBadge.Text = "翻译中";

        try
        {
            // The manual entry uses the same coordinator as selection,
            // screenshot, and quick search: one privacy policy, one history.
            var session = await _coordinator.TranslateTextAsync(
                source, sourceLang, targetLang, TranslationInputSource.Manual, operation.Token);

            if (session.IsSuccess)
            {
                TranslateResult.Text = session.TranslatedText;
                TranslateEngineBadge.Text = session.PipelineLabel ?? "已翻译";
                TranslateStatus.Text = session.Stage == TranslationSessionStage.Partial
                    ? $"部分完成 · {session.Timing.TotalElapsedMs} ms · 见下方说明"
                    : $"完成 · {session.Timing.TotalElapsedMs} ms";

                var notes = new List<string>();
                if (!string.IsNullOrWhiteSpace(session.Explanation))
                {
                    notes.Add(session.Explanation.Trim());
                }
                notes.AddRange(session.Warnings);
                TranslateExplanation.Text = string.Join("\n", notes);
                TranslateExplanation.Visibility = notes.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            else if (session.Stage == TranslationSessionStage.Cancelled)
            {
                TranslateStatus.Text = "已取消。";
                TranslateEngineBadge.Text = "已取消";
            }
            else
            {
                var message = session.Error?.Message ?? "翻译未完成";
                var suggestion = session.Error?.ActionableSuggestion;
                TranslateEngineBadge.Text = "未完成";
                TranslateStatus.Text = string.IsNullOrWhiteSpace(suggestion)
                    ? message
                    : $"{message} {suggestion}";
                TranslateResult.Text = TranslationPanelWindow.FriendlyError(message);
                TranslateExplanation.Text = string.IsNullOrWhiteSpace(suggestion)
                    ? message
                    : $"{message}\n{suggestion}";
                TranslateExplanation.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            TranslateStatus.Text = "已取消。";
            TranslateEngineBadge.Text = "已取消";
        }
        catch (Exception exception)
        {
            TranslateEngineBadge.Text = "未完成";
            TranslateStatus.Text = $"翻译失败：{exception.Message}";
            TranslateResult.Text = TranslationPanelWindow.FriendlyError(exception.Message);
            TranslateExplanation.Text = exception.Message;
            TranslateExplanation.Visibility = Visibility.Visible;
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            if (ReferenceEquals(_translateOperation, operation))
            {
                _translateOperation = null;
            }
            operation.Dispose();
        }
    }

    private void TranslateInput_TextChanged(object sender, TextChangedEventArgs e) =>
        TranslateCounter.Text = $"{TranslateInput.Text.Length} 字符";

    private void TranslateSwap_Click(object sender, RoutedEventArgs e)
    {
        var (source, target) = LanguageCatalog.Swap(
            SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            SelectedLanguage(TranslateTargetLang, "zh-CN"));
        TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(source);
        TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(target);

        // Swapping is nearly always "now translate this back", so move the
        // result into the input instead of leaving the user to copy it across.
        if (!string.IsNullOrWhiteSpace(TranslateResult.Text))
        {
            TranslateInput.Text = TranslateResult.Text;
            TranslateResult.Clear();
            TranslateExplanation.Visibility = Visibility.Collapsed;
        }
    }

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

    private async Task CopySourceToClipboardAsync()
    {
        if (await CopyToClipboardAsync(TranslateInput.Text))
        {
            TranslateStatus.Text = "已复制原文。";
        }
    }

    private async Task CopyResultToClipboardAsync()
    {
        if (await CopyToClipboardAsync(TranslateResult.Text))
        {
            TranslateStatus.Text = "已复制译文。";
        }
    }

    private void TranslateClear_Click(object sender, RoutedEventArgs e)
    {
        _translateOperation?.Cancel();
        TranslateInput.Clear();
        TranslateResult.Clear();
        TranslateExplanation.Visibility = Visibility.Collapsed;
        TranslateEngineBadge.Text = "等待输入";
        TranslateStatus.Text = "就绪";
        TranslateInput.Focus();
    }

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    /// <remarks>
    /// When <paramref name="existingTranslation"/> is present the session is
    /// carried over verbatim — the text is never re-translated.
    /// </remarks>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null)
    {
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            TranslateInput.Text = initialText;
        }
        if (!string.IsNullOrWhiteSpace(sourceLang))
        {
            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(sourceLang);
        }
        if (!string.IsNullOrWhiteSpace(targetLang))
        {
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(targetLang);
        }
        if (existingTranslation is not null)
        {
            TranslateResult.Text = existingTranslation;
            TranslateEngineBadge.Text = "已展开的译文";
            TranslateStatus.Text = "已从浮窗展开，未重新翻译。";
        }
        TranslateInput.Focus();
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
    }

    // ================= Provider =================

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string preset)
        {
            return;
        }

        // Presets set every field they own so a half-applied previous preset
        // cannot leak through (e.g. an Anthropic endpoint left behind).
        var (type, baseUrl, endpoint, textModel, visionModel, note) = preset switch
        {
            "openai" => (ProviderType.OpenAiCompatible, "https://api.openai.com/v1",
                "/chat/completions", "gpt-4o-mini", "gpt-4o-mini", "已应用 OpenAI 预设，填入 API Key 即可。"),
            "deepseek" => (ProviderType.OpenAiCompatible, "https://api.deepseek.com",
                "/chat/completions", "deepseek-chat", "", "已应用 DeepSeek 预设（无视觉模型）。"),
            "gemini" => (ProviderType.GeminiGenerateContent, "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent", "gemini-2.0-flash", "gemini-2.0-flash",
                "已应用 Google Gemini 预设。"),
            "claude" => (ProviderType.AnthropicMessages, "https://api.anthropic.com",
                "/v1/messages", "claude-sonnet-4-5", "claude-sonnet-4-5", "已应用 Anthropic Claude 预设。"),
            "zhipu" => (ProviderType.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4",
                "/chat/completions", "glm-4-flash", "glm-4v-flash", "已应用智谱 GLM 预设。"),
            "ollama" => (ProviderType.OpenAiCompatible, "http://localhost:11434/v1",
                "/chat/completions", "qwen2.5:7b", "llava", "已应用本地 Ollama 预设，无需 API Key。"),
            _ => (ProviderType.OpenAiCompatible, "https://api.openai.com/v1",
                "/chat/completions", "gpt-4o-mini", "gpt-4o-mini", "已应用预设。"),
        };

        _loading = true;
        try
        {
            SelectComboByTag(ProviderTypeComboBox, type.ToString());
            BaseUrlTextBox.Text = baseUrl;
            TextEndpointTextBox.Text = endpoint;
            VisionEndpointTextBox.Text = endpoint;
            TextModelTextBox.Text = textModel;
            VisionModelTextBox.Text = visionModel;
            AnthropicVersionTextBox.Text = "2023-06-01";
            SupportsTextCheckBox.IsChecked = true;
            SupportsVisionCheckBox.IsChecked = !string.IsNullOrEmpty(visionModel);
        }
        finally
        {
            _loading = false;
        }

        MarkProviderDirty();
        SetStatus(note + " 记得点击「保存设置」。", StatusTone.Info);
    }

    private void ProviderTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderTypeComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }
        var providerType = Enum.Parse<ProviderType>(selected.Tag.ToString()!);
        var (baseUrl, endpoint) = ProviderDefaults(providerType);

        // Only rewrite endpoints that still match another protocol's defaults;
        // switching protocols used to wipe a hand-tuned relay configuration.
        if (IsKnownDefaultUrl(BaseUrlTextBox.Text))
        {
            BaseUrlTextBox.Text = baseUrl;
        }
        if (IsKnownDefaultEndpoint(TextEndpointTextBox.Text))
        {
            TextEndpointTextBox.Text = endpoint;
        }
        if (IsKnownDefaultEndpoint(VisionEndpointTextBox.Text))
        {
            VisionEndpointTextBox.Text = endpoint;
        }
        MarkProviderDirty();
        SetStatus("已切换接口协议，请确认模型名与 Base URL。", StatusTone.Info);
    }

    private static bool IsKnownDefaultUrl(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Enum.GetValues<ProviderType>().Any(type =>
            string.Equals(ProviderDefaults(type).BaseUrl, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownDefaultEndpoint(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Enum.GetValues<ProviderType>().Any(type =>
            string.Equals(ProviderDefaults(type).Endpoint, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static (string BaseUrl, string Endpoint) ProviderDefaults(ProviderType providerType) =>
        providerType switch
        {
            ProviderType.OpenAiCompatible => ("https://api.openai.com/v1", "/chat/completions"),
            ProviderType.OpenAiResponses => ("https://api.openai.com/v1", "/responses"),
            ProviderType.AnthropicMessages => ("https://api.anthropic.com", "/v1/messages"),
            ProviderType.GeminiGenerateContent => (
                "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent"),
            _ => ("https://api.openai.com/v1", "/chat/completions"),
        };

    private void ProviderGate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        MarkProviderDirty();
        WarnAboutGates();
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        MarkProviderDirty();
    }

    private void MarkProviderDirty()
    {
        _hasUnsavedProviderChanges = true;
        RefreshRoutePreview();
    }

    private void WarnAboutGates()
    {
        if (SafeModeToggle.IsChecked == true)
        {
            SetStatus("安全离线模式已开启：保存后所有模型请求都会被拒绝。", StatusTone.Warning);
        }
        else if (NetworkEnabledToggle.IsChecked != true)
        {
            SetStatus("「启用大模型网络翻译」已关闭：保存后模型请求会被拒绝。", StatusTone.Warning);
        }
        else
        {
            SetStatus("有未保存的修改。", StatusTone.Info);
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        try
        {
            // Testing exercises exactly the draft on screen: nothing is written
            // to disk, the active provider is untouched, and a typed key is used
            // in memory without saving over the stored credential.
            var draft = BuildProviderSettingsFromForm();
            var typedKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? CredentialStore.LoadApiKey(CurrentCredentialTarget())
                : ApiKeyPasswordBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(typedKey) && !draft.TargetsLocalRuntime)
            {
                throw new InvalidOperationException("请先填写 API Key（不会被保存），或改用本地模型地址。");
            }
            SetStatus("正在测试连接（仅发送一小段文本，不含截图）…", StatusTone.Info);
            var response = await CoreBridge.TestConnectionDraftAsync(
                draft, string.IsNullOrWhiteSpace(typedKey) ? "local" : typedKey);
            SetStatus(
                $"连接成功 · HTTP {response.Diagnostics.StatusCode} · {response.Diagnostics.ElapsedMs} ms（草稿未保存）",
                StatusTone.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"连接测试失败：{exception.Message}（设置未被修改）", StatusTone.Error);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
            RefreshSidebar();
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CredentialStore.SaveApiKey(string.Empty, CurrentCredentialTarget());
            ApiKeyPasswordBox.Clear();
            RefreshApiKeyState();
            RefreshSidebar();
            SetStatus("该服务的 API Key 已清除；未配置密钥且未允许免费引擎时不会出网。", StatusTone.Info);
        }
        catch (Exception exception)
        {
            SetStatus($"清除 API Key 失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ================= Service profiles =================

    private static string DescribeProtocol(ProviderType type) => type switch
    {
        ProviderType.OpenAiCompatible => "OpenAI 兼容",
        ProviderType.OpenAiResponses => "OpenAI Responses",
        ProviderType.AnthropicMessages => "Anthropic",
        ProviderType.GeminiGenerateContent => "Gemini",
        _ => type.ToString(),
    };

    private void RefreshProfilesList()
    {
        var config = ProfileManager.Load();
        ProfilesListBox.ItemsSource = config.Profiles.Select(profile => new ProfilesRow(
            profile.Id,
            profile.Name,
            DescribeProtocol(profile.ProviderType),
            string.IsNullOrWhiteSpace(profile.TextModel) ? "未填模型" : profile.TextModel,
            profile.IsLocal ? "本地" : "在线",
            profile.Id == config.ActiveProfileId ? Visibility.Visible : Visibility.Collapsed)).ToList();
        ProfilesEmptyText.Visibility = config.Profiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var active = config.GetActiveProfile();
        ProfileHintText.Text = $"当前默认：{active.Name} · {DescribeProtocol(active.ProviderType)}";
    }

    private Services.ProviderProfile? SelectedProfile()
    {
        if (ProfilesListBox.SelectedItem is not ProfilesRow row)
        {
            return null;
        }
        return ProfileManager.Load().Profiles.FirstOrDefault(profile => profile.Id == row.Id);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        _editingProfileId = null;
        _wizardActive = true;
        _wizardStep = 1;
        ServiceEditorTitle.Text = "添加服务";
        ProfilesListBox.SelectedIndex = -1;
        LoadProfileIntoForm(NewProfileDraft());
        ServiceNameTextBox.Clear();
        ApiKeyPasswordBox.Clear();
        RefreshApiKeyState();
        ApplyWizardStep();
        SetStatus("添加服务向导：先选择服务类型，再填凭据与模型，最后测试并保存。", StatusTone.Info);
    }

    /// <summary>Shows only the field groups the current wizard step needs.</summary>
    private void ApplyWizardStep()
    {
        if (!_wizardActive)
        {
            // Edit mode: everything visible, wizard chrome hidden.
            WizardStrip.Visibility = Visibility.Collapsed;
            WizardNav.Visibility = Visibility.Collapsed;
            ServiceNameGroup.Visibility = Visibility.Visible;
            ProtocolGroup.Visibility = Visibility.Visible;
            ModelsGroup.Visibility = Visibility.Visible;
            CredentialGroup.Visibility = Visibility.Visible;
            AdvancedGroup.Visibility = Visibility.Visible;
            SaveServiceButton.Visibility = Visibility.Visible;
            TestConnectionButton.Visibility = Visibility.Visible;
            return;
        }

        WizardStrip.Visibility = Visibility.Visible;
        WizardNav.Visibility = Visibility.Visible;
        WizardStepText.Text = _wizardStep switch
        {
            1 => "第 1/3 步 · 选择服务类型",
            2 => "第 2/3 步 · 凭据与模型",
            _ => "第 3/3 步 · 测试并保存",
        };
        var protocol = SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible);
        var model = string.IsNullOrWhiteSpace(TextModelTextBox.Text) ? "（未填）" : TextModelTextBox.Text.Trim();
        WizardSummary.Text = _wizardStep switch
        {
            1 => "从预设或协议开始；下一步填写名称、密钥与模型。",
            2 => $"{DescribeProtocol(protocol)} · {BaseUrlTextBox.Text.Trim()}",
            _ => $"{ServiceNameTextBox.Text.Trim()} · {DescribeProtocol(protocol)} · {model}",
        };

        ServiceNameGroup.Visibility = _wizardStep >= 2 ? Visibility.Visible : Visibility.Collapsed;
        ModelsGroup.Visibility = _wizardStep >= 2 ? Visibility.Visible : Visibility.Collapsed;
        CredentialGroup.Visibility = _wizardStep >= 2 ? Visibility.Visible : Visibility.Collapsed;
        AdvancedGroup.Visibility = _wizardStep >= 2 ? Visibility.Visible : Visibility.Collapsed;
        ProtocolGroup.Visibility = _wizardStep == 1 || _wizardStep >= 3 ? Visibility.Visible : Visibility.Collapsed;

        // Presets are the step-1 content.
        PresetsPanel.Visibility = _wizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        TestConnectionButton.Visibility = _wizardStep >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SaveServiceButton.Visibility = _wizardStep >= 3 ? Visibility.Visible : Visibility.Collapsed;
        WizardNextButton.Visibility = _wizardStep >= 3 ? Visibility.Collapsed : Visibility.Visible;
        WizardBackButton.Content = _wizardStep == 1 ? "退出向导" : "上一步";
    }

    private void WizardNext_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardStep == 2 && string.IsNullOrWhiteSpace(ServiceNameTextBox.Text))
        {
            SetStatus("请先填写服务名称，再进入测试与保存。", StatusTone.Warning);
            return;
        }
        _wizardStep = Math.Min(3, _wizardStep + 1);
        ApplyWizardStep();
    }

    private void WizardBack_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardStep <= 1)
        {
            // Back out of the wizard entirely: return to the active service.
            ExitWizardToActiveProfile();
            return;
        }
        _wizardStep--;
        ApplyWizardStep();
    }

    private void ExitWizardToActiveProfile()
    {
        _wizardActive = false;
        _wizardStep = 0;
        _editingProfileId = null;
        LoadActiveProfileIntoForm();
        RefreshProfilesList();
        SelectActiveProfileInList();
        ApplyWizardStep();
        SetStatus("已退出添加向导。", StatusTone.Info);
    }

    private static Services.ProviderProfile NewProfileDraft() => new()
    {
        Name = string.Empty,
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "https://api.openai.com/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = string.Empty,
        VisionModel = string.Empty,
        SupportsText = true,
        SupportsVision = false,
    };

    private void EditProfile_Click(object sender, RoutedEventArgs e) => LoadSelectedProfileIntoEditor();

    private void ProfilesListBox_DoubleClick(object sender, MouseButtonEventArgs e) =>
        LoadSelectedProfileIntoEditor();

    private void LoadSelectedProfileIntoEditor()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            SetStatus("请先在列表中选择一个服务。", StatusTone.Info);
            return;
        }
        _editingProfileId = profile.Id;
        _wizardActive = false;
        _wizardStep = 0;
        ServiceEditorTitle.Text = "编辑服务";
        LoadProfileIntoForm(profile);
        ApplyWizardStep();
        RefreshApiKeyState();
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            SetStatus("请先在列表中选择要删除的服务。", StatusTone.Info);
            return;
        }
        var confirmation = MessageBox.Show(
            $"确定要删除服务「{profile.Name}」吗？\n\n该服务独立保存的 API Key 也会一并删除。",
            "PopGlot",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var config = ProfileManager.Load();
            var wasActive = profile.Id == config.ActiveProfileId;
            config.Profiles.Remove(profile);
            CredentialStore.DeleteApiKey(profile.CredentialTarget);

            if (config.ActiveProfileId == profile.Id)
            {
                config.ActiveProfileId = config.Profiles.FirstOrDefault()?.Id ?? string.Empty;
            }
            ProfileManager.Save(config);

            if (wasActive && config.ActiveProfileId is { Length: > 0 })
            {
                ApplyProfileToCore(config.GetActiveProfile());
            }

            _editingProfileId = null;
            RefreshProfilesList();
            RefreshSidebar();
            if (config.Profiles.Count == 0)
            {
                SetStatus("服务已删除。未配置模型服务时，翻译将使用已授权的内置免费引擎。", StatusTone.Info);
            }
            else
            {
                SetStatus($"服务已删除，默认服务切换为「{config.GetActiveProfile().Name}」。", StatusTone.Info);
            }
            await Task.CompletedTask;
        }
        catch (Exception exception)
        {
            SetStatus($"删除服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void ActivateProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            SetStatus("请先在列表中选择要设为默认的服务。", StatusTone.Info);
            return;
        }
        try
        {
            var config = ProfileManager.Load();
            config.ActiveProfileId = profile.Id;
            ProfileManager.Save(config);
            ApplyProfileToCore(profile);
            _wizardActive = false;
            _wizardStep = 0;
            _editingProfileId = profile.Id;
            ServiceEditorTitle.Text = "编辑服务";
            LoadProfileIntoForm(profile);
            ApplyWizardStep();
            RefreshProfilesList();
            RefreshApiKeyState();
            RefreshSidebar();
            RefreshRoutePreview();
            SetStatus($"已将「{profile.Name}」设为默认服务。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"切换默认服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void SaveService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ServiceNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("请先填写服务名称。");
            }
            var profile = BuildProfileFromForm(name);

            var typedKey = ApiKeyPasswordBox.Password?.Trim();
            if (!string.IsNullOrEmpty(typedKey))
            {
                CredentialStore.SaveApiKey(typedKey, profile.CredentialTarget);
                ApiKeyPasswordBox.Clear();
            }

            var config = ProfileManager.Load();
            if (_editingProfileId is null)
            {
                // Fresh profile: mint a stable id and its own credential slot.
                profile.Id = $"p-{Guid.NewGuid().ToString("N")[..10]}";
                profile.CredentialTarget = $"PopGlot/provider/{profile.Id}";
                config.Profiles.Add(profile);
                _editingProfileId = profile.Id;
                // A brand-new service does not become the default silently.
            }
            else
            {
                var existing = config.Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
                if (existing is null)
                {
                    profile.Id = _editingProfileId;
                    profile.CredentialTarget = $"PopGlot/provider/{profile.Id}";
                    config.Profiles.Add(profile);
                }
                else
                {
                    profile.Id = existing.Id;
                    profile.CredentialTarget = existing.CredentialTarget;
                    config.Profiles[config.Profiles.IndexOf(existing)] = profile;
                }
            }

            ProfileManager.Save(config);
            if (config.ActiveProfileId == profile.Id)
            {
                ApplyProfileToCore(profile);
                RefreshRoutePreview();
            }

            ServiceEditorTitle.Text = "编辑服务";
            _wizardActive = false;
            _wizardStep = 0;
            ApplyWizardStep();
            RefreshProfilesList();
            SelectProfileInList(profile.Id);
            RefreshApiKeyState();
            RefreshSidebar();
            SetStatus($"服务「{profile.Name}」已保存。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"保存服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _wizardActive = false;
        _wizardStep = 0;
        _editingProfileId = null;
        LoadActiveProfileIntoForm();
        RefreshProfilesList();
        SelectActiveProfileInList();
        ApplyWizardStep();
        SetStatus("已放弃服务编辑。", StatusTone.Info);
    }

    private void TogglePresets_Click(object sender, RoutedEventArgs e)
    {
        if (_wizardActive)
        {
            // In the wizard the preset list belongs to step 1.
            return;
        }
        PresetsPanel.Visibility = PresetsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SelectProfileInList(string profileId)
    {
        var items = ProfilesListBox.ItemsSource as IEnumerable<ProfilesRow> ?? [];
        var index = items.ToList().FindIndex(row => row.Id == profileId);
        ProfilesListBox.SelectedIndex = index >= 0 ? index : -1;
    }

    private void SelectActiveProfileInList() => SelectProfileInList(ProfileManager.Load().ActiveProfileId);

    private Services.ProviderProfile BuildProfileFromForm(string name)
    {
        var baseUrl = BaseUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("API Base URL 不能为空。");
        }
        var isLocal = ProviderSettings.IsLocalBaseUrl(baseUrl);
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !isLocal)
        {
            throw new InvalidOperationException("API Base URL 必须使用 HTTPS；仅本机或局域网服务允许 HTTP。");
        }
        return new Services.ProviderProfile
        {
            Name = name,
            ProviderType = SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible),
            ApiBaseUrl = baseUrl,
            TextEndpoint = string.IsNullOrWhiteSpace(TextEndpointTextBox.Text)
                ? "/chat/completions" : TextEndpointTextBox.Text.Trim(),
            VisionEndpoint = string.IsNullOrWhiteSpace(VisionEndpointTextBox.Text)
                ? "/chat/completions" : VisionEndpointTextBox.Text.Trim(),
            TextModel = TextModelTextBox.Text.Trim(),
            VisionModel = VisionModelTextBox.Text.Trim(),
            ExtraHeaders = new Dictionary<string, string>(
                ParseExtraHeaders(ExtraHeadersTextBox.Text),
                StringComparer.OrdinalIgnoreCase),
            AnthropicVersion = string.IsNullOrWhiteSpace(AnthropicVersionTextBox.Text)
                ? "2023-06-01" : AnthropicVersionTextBox.Text.Trim(),
            SupportsText = SupportsTextCheckBox.IsChecked == true,
            SupportsVision = SupportsVisionCheckBox.IsChecked == true,
            AllowInsecureTls = AllowInsecureTlsCheckBox.IsChecked == true,
            IsLocal = isLocal,
        };
    }

    /// <summary>Mirrors the profile into the core so the running app uses it now.</summary>
    private void ApplyProfileToCore(Services.ProviderProfile profile)
    {
        var current = CoreBridge.GetSettings();
        CoreBridge.SaveSettings(profile.ToProviderSettings(current));
        _hasUnsavedProviderChanges = false;
    }

    // ================= Save / revert =================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // The footer saves shell settings and the shared outbound policy;
            // per-service fields live in the service editor and its own button.
            CoreBridge.SaveSettings(BuildPolicySettingsFromForm());

            var shellSettings = new ShellSettings(
                ShellSettings.CurrentSchemaVersion,
                SelectionHotkeyRecorder.BindingValue ?? _shellSettings.SelectionHotkey,
                ScreenshotHotkeyRecorder.BindingValue ?? _shellSettings.ScreenshotHotkey,
                CloseHotkeyRecorder.BindingValue ?? _shellSettings.CloseHotkey,
                HistoryEnabledToggle.IsChecked == true,
                SelectedEnum(ThemeComboBox, ThemePreference.System),
                CloseOnFocusLossToggle.IsChecked == true,
                AutoCopyToggle.IsChecked == true,
                StartWithWindowsToggle.IsChecked == true,
                ShowWindowHotkeyRecorder.BindingValue ?? _shellSettings.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault,
                FreeEngineConsent: _shellSettings.FreeEngineConsent);

            var validationError = shellSettings.ValidateHotkeys();
            if (validationError is not null)
            {
                throw new InvalidOperationException(validationError);
            }
            if (ApplyShellSettings is not null && !ApplyShellSettings(shellSettings))
            {
                throw new InvalidOperationException(
                    "快捷键注册失败，已保留原快捷键。请换一个未被占用的组合。");
            }

            try
            {
                ShellSettingsStore.Save(shellSettings);
            }
            catch
            {
                // Roll the live registration back to whatever is on disk so the
                // running app and the persisted file cannot disagree.
                _ = ApplyShellSettings?.Invoke(_shellSettings);
                throw;
            }

            if (!StartupRegistration.TrySet(shellSettings.StartWithWindows))
            {
                SetStatus("设置已保存，但无法写入开机启动项（可能被安全软件拦截）。", StatusTone.Warning);
            }
            else
            {
                SetStatus("设置已保存。", StatusTone.Success);
            }

            _shellSettings = shellSettings;
            _hasUnsavedProviderChanges = false;
            RefreshSidebar();
            RefreshRoutePreview();
            RefreshApiKeyState();

            if (SafeModeToggle.IsChecked == true || NetworkEnabledToggle.IsChecked != true)
            {
                WarnAboutGates();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            ApiKeyPasswordBox.Clear();
            LoadAll();
        }
        finally
        {
            _loading = false;
        }
        SetStatus("已放弃未保存的修改。", StatusTone.Info);
    }

    /// <summary>
    /// Applies only the shared outbound-policy and behaviour fields to the
    /// saved settings, leaving the active service's endpoint/model fields —
    /// which belong to the service editor — untouched.
    /// </summary>
    private ProviderSettings BuildPolicySettingsFromForm()
    {
        var current = CoreBridge.GetSettings();
        return current with
        {
            NetworkEnabled = NetworkEnabledToggle.IsChecked == true,
            SafeDevMode = SafeModeToggle.IsChecked == true,
            AllowImageUploadInAuto = AllowImageUploadToggle.IsChecked == true,
            Mode = SelectedEnum(ModeComboBox, TranslationMode.Auto),
            IncludeExplanation = IncludeExplanationToggle.IsChecked == true,
            ProtectCodeTokens = ProtectTokensToggle.IsChecked == true,
            SourceLanguage = SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            TargetLanguage = SelectedLanguage(TranslateTargetLang, "zh-CN"),
        };
    }

    /// <summary>
    /// Snapshots the service editor into a draft settings object without
    /// touching disk or credentials — used by the draft connection test.
    /// </summary>
    private ProviderSettings BuildProviderSettingsFromForm()
    {
        var current = CoreBridge.GetSettings();
        return current with
        {
            SchemaVersion = current.SchemaVersion,
            ProviderType = SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible),
            ApiBaseUrl = BaseUrlTextBox.Text.Trim(),
            TextEndpoint = TextEndpointTextBox.Text.Trim(),
            VisionEndpoint = VisionEndpointTextBox.Text.Trim(),
            TextModel = TextModelTextBox.Text.Trim(),
            VisionModel = VisionModelTextBox.Text.Trim(),
            ExtraHeaders = ParseExtraHeaders(ExtraHeadersTextBox.Text),
            AnthropicVersion = AnthropicVersionTextBox.Text.Trim(),
            SupportsText = SupportsTextCheckBox.IsChecked == true,
            SupportsVision = SupportsVisionCheckBox.IsChecked == true,
            NetworkEnabled = NetworkEnabledToggle.IsChecked == true,
            Mode = SelectedEnum(ModeComboBox, TranslationMode.Auto),
            AllowImageUploadInAuto = AllowImageUploadToggle.IsChecked == true,
            SafeDevMode = SafeModeToggle.IsChecked == true,
            AllowInsecureTls = AllowInsecureTlsCheckBox.IsChecked == true,
            ApiKeyConfigured = CredentialStore.HasApiKey(CurrentCredentialTarget()),
            SourceLanguage = SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            TargetLanguage = SelectedLanguage(TranslateTargetLang, "zh-CN"),
            IncludeExplanation = IncludeExplanationToggle.IsChecked == true,
            ProtectCodeTokens = ProtectTokensToggle.IsChecked == true,
        };
    }

    private static IReadOnlyDictionary<string, string> ParseExtraHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"自定义请求头格式无效：{line}（应为 Header: Value）");
            }
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return headers;
    }

        // ================= Vocabulary =================

    private IReadOnlyList<VocabularyWord> _allVocabulary = [];

    internal void ReloadVocabulary()
    {
        if (_vocabulary is null) return;
        try
        {
            _allVocabulary = _vocabulary.GetAll();
            ApplyVocabularyFilter();
        }
        catch (Exception exception)
        {
            SetStatus($"加载生词本失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void ApplyVocabularyFilter()
    {
        var query = VocabularySearchBox?.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _allVocabulary
            : _allVocabulary.Where(entry =>
                entry.Word.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        VocabularyListBox.ItemsSource = filtered.Select(entry => new VocabularyRow(
            entry,
            string.IsNullOrWhiteSpace(entry.Phonetic) ? string.Empty : $"[{entry.Phonetic}]",
            entry.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            entry.Word,
            entry.Translation)).ToList();

        var count = VocabularyListBox.Items.Count;
        VocabularyEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        VocabularyCountText.Text = string.IsNullOrEmpty(query)
            ? $"共 {_allVocabulary.Count} 个生词"
            : $"匹配 {count} / {_allVocabulary.Count} 个";
    }

    private void VocabularySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyVocabularyFilter();

    private void SpeakVocabulary_Click(object sender, RoutedEventArgs e)
    {
        if (VocabularyListBox.SelectedItem is not VocabularyRow row)
        {
            SetStatus("请先选择一个生词。", StatusTone.Info);
            return;
        }
        TtsService.Speak(row.Source);
    }

    private void DeleteVocabulary_Click(object sender, RoutedEventArgs e)
    {
        if (VocabularyListBox.SelectedItem is not VocabularyRow row || _vocabulary is null)
        {
            SetStatus("请先选择一个生词。", StatusTone.Info);
            return;
        }
        _vocabulary.Remove(row.Word.Id);
        ReloadVocabulary();
        SetStatus("已从生词本移除该词条。", StatusTone.Info);
    }

    private void LoadVocabulary_Click(object sender, RoutedEventArgs e) => LoadSelectedVocabulary();

    private void ClearVocabulary_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "确定要删除全部生词与收藏吗？此操作无法撤销。",
            "PopGlot",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK || _vocabulary is null)
        {
            return;
        }
        _vocabulary.Clear();
        ReloadVocabulary();
        SetStatus("生词本已清空。", StatusTone.Info);
    }

    private void VocabularyList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelectedVocabulary();

    private void LoadSelectedVocabulary()
    {
        if (VocabularyListBox.SelectedItem is not VocabularyRow row)
        {
            return;
        }
        TranslateInput.Text = row.Word.Word;
        TranslateResult.Text = row.Word.Translation;
        TranslateExplanation.Text = row.Word.Explanation;
        TranslateExplanation.Visibility = string.IsNullOrWhiteSpace(row.Word.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(row.Word.SourceLanguage);
        TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(row.Word.TargetLanguage);
        TranslateEngineBadge.Text = "生词本";
        TranslateStatus.Text = $"已载入生词「{row.Word.Word}」。";
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
    }

    private async void ExportAnki_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null) return;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(desktop, $"PopGlot_Anki_Export_{DateTime.Now:yyyyMMdd_HHmm}.tsv");
            var tsv = _vocabulary.ExportToAnkiTsv();
            await System.IO.File.WriteAllTextAsync(path, tsv, System.Text.Encoding.UTF8);
            SetStatus($"已成功导出 Anki 牌组到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"导出 Anki 失败：{ex.Message}", StatusTone.Error);
        }
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null) return;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(desktop, $"PopGlot_Vocabulary_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            var csv = _vocabulary.ExportToCsv();
            await System.IO.File.WriteAllTextAsync(path, csv, System.Text.Encoding.UTF8);
            SetStatus($"已成功导出生词 CSV 到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"导出生词 CSV 失败：{ex.Message}", StatusTone.Error);
        }
    }

    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null) return;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(desktop, $"PopGlot_Vocabulary_{DateTime.Now:yyyyMMdd_HHmm}.md");
            var md = _vocabulary.ExportToMarkdown();
            await System.IO.File.WriteAllTextAsync(path, md, System.Text.Encoding.UTF8);
            SetStatus($"已成功导出生词 Markdown 到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"导出生词 Markdown 失败：{ex.Message}", StatusTone.Error);
        }
    }

// ================= History =================

    internal void ReloadHistory()
    {
        try
        {
            _allHistory = _history.Load();
            ApplyHistoryFilter();
        }
        catch (Exception exception)
        {
            SetStatus($"加载历史记录失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void ApplyHistoryFilter()
    {
        var query = HistorySearchBox?.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _allHistory
            : _allHistory.Where(entry =>
                entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        HistoryListBox.ItemsSource = filtered.Select(entry => new HistoryRow(
            entry,
            entry.SourceKind,
            entry.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            $"{LanguageCatalog.DisplayName(entry.SourceLanguage)} → {LanguageCatalog.DisplayName(entry.TargetLanguage)}",
            entry.Source,
            entry.Translation)).ToList();

        var count = HistoryListBox.Items.Count;
        HistoryEmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = string.IsNullOrEmpty(query)
            ? $"共 {_allHistory.Count} 条记录"
            : $"匹配 {count} / {_allHistory.Count} 条";
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilter();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "确定要删除全部本地历史记录吗？此操作无法撤销。",
            "PopGlot",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }
        var cleared = _history.Clear();
        SetStatus(
            cleared ? "历史记录已清空。" : "清空历史失败：文件正被占用。",
            cleared ? StatusTone.Info : StatusTone.Error);
        ReloadHistory();
    }

    private void DeleteHistoryEntry_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not HistoryRow row)
        {
            SetStatus("请先选择一条历史记录。", StatusTone.Info);
            return;
        }
        _history.Remove(row.Entry.Id);
        ReloadHistory();
        SetStatus("已删除该条记录。", StatusTone.Info);
    }

    private async void CopyHistoryTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not HistoryRow row)
        {
            SetStatus("请先选择一条历史记录。", StatusTone.Info);
            return;
        }
        if (await CopyToClipboardAsync(row.Translation))
        {
            SetStatus("已复制该条译文。", StatusTone.Info);
        }
    }

    private async void ExportHistoryCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(desktop, $"PopGlot_History_{DateTime.Now:yyyyMMdd_HHmm}.csv");
            var csv = _history.ExportToCsv();
            await System.IO.File.WriteAllTextAsync(path, csv, System.Text.Encoding.UTF8);
            SetStatus($"已成功导出历史记录 CSV 到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"导出历史记录 CSV 失败：{ex.Message}", StatusTone.Error);
        }
    }

    private async void ExportHistoryMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(desktop, $"PopGlot_History_{DateTime.Now:yyyyMMdd_HHmm}.md");
            var md = _history.ExportToMarkdown();
            await System.IO.File.WriteAllTextAsync(path, md, System.Text.Encoding.UTF8);
            SetStatus($"已成功导出历史记录 Markdown 到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"导出历史记录 Markdown 失败：{ex.Message}", StatusTone.Error);
        }
    }

    private void LoadHistoryEntry_Click(object sender, RoutedEventArgs e) => LoadSelectedHistory();

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelectedHistory();

    private void LoadSelectedHistory()
    {
        if (HistoryListBox.SelectedItem is not HistoryRow row)
        {
            return;
        }
        TranslateInput.Text = row.Entry.Source;
        TranslateResult.Text = row.Entry.Translation;
        TranslateExplanation.Text = row.Entry.Explanation;
        TranslateExplanation.Visibility = string.IsNullOrWhiteSpace(row.Entry.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(row.Entry.SourceLanguage);
        TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(row.Entry.TargetLanguage);
        TranslateEngineBadge.Text = "历史记录";
        TranslateStatus.Text = $"已载入 {row.Timestamp} 的记录。";
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
    }

    // ================= Theme =================

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        // Apply immediately: a theme picker that needs a Save press to show what
        // it does is impossible to evaluate.
        ThemeService.Apply(SelectedEnum(ThemeComboBox, ThemePreference.System));
        ThemeService.ApplyWindowChrome(this);
    }

    // ================= Shared helpers =================

    private enum StatusTone
    {
        Info,
        Success,
        Warning,
        Error,
    }

    private void SetStatus(string message, StatusTone tone)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextSecondaryBrush",
        });
        StatusDot.Background = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextTertiaryBrush",
        });
    }

    internal void ShowShortcutConflict(string conflict) =>
        SetStatus($"快捷键注册失败 — {conflict}。请换一个组合后重试。", StatusTone.Error);

    private static string SelectedLanguage(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as LanguageOption)?.Tag ?? fallback;

    private static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem candidate &&
                string.Equals(candidate.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = candidate;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }

    private static T SelectedEnum<T>(ComboBox comboBox, T fallback) where T : struct, Enum =>
        comboBox.SelectedItem is ComboBoxItem selected &&
        Enum.TryParse<T>(selected.Tag?.ToString(), out var value)
            ? value
            : fallback;

    private static async Task<bool> CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Yield instead of sleeping: a busy clipboard owner needs the
                // message pump running to release it.
                await Task.Delay(15 * (attempt + 1));
            }
        }
        return false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The app lives in the tray: closing the window hides it instead of
        // quitting, unless the tray's Exit command asked for a real close.
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
