using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PopGlot.Windows;

public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private ShellSettings _shellSettings;
    private bool _loadingSettings;
    private IReadOnlyList<TranslationHistoryEntry> _allHistory = [];

    internal MainWindow(ShellSettings shellSettings, HistoryStore history)
    {
        _shellSettings = shellSettings;
        _history = history;
        InitializeComponent();
        ConfigureShortcutCombo(SelectionShortcutComboBox, shellSettings.SelectionShortcut);
        ConfigureShortcutCombo(ScreenshotShortcutComboBox, shellSettings.ScreenshotShortcut);
        ConfigureShortcutCombo(CloseShortcutComboBox, shellSettings.CloseShortcut);
        HistoryEnabledCheckBox.IsChecked = shellSettings.HistoryEnabled;
        SelectComboBoxItem(ThemeComboBox, shellSettings.Theme.ToString());
        LoadProviderSettings();
        LoadOcrSettings();
        ShowSection(TranslatePanel, TranslateNavButton);
        ReloadHistory();
        UpdateShortcutHint(shellSettings);
    }

    internal bool AllowClose { get; set; }
    internal Func<ShellSettings, bool>? ApplyShellSettings { get; init; }

    private static void ConfigureShortcutCombo(ComboBox comboBox, ShortcutOption selected)
    {
        comboBox.ItemsSource = ShortcutOption.Available;
        comboBox.SelectedItem = selected;
    }

    private void LoadProviderSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = CoreBridge.GetSettings();
            SelectComboBoxItem(ProviderTypeComboBox, settings.ProviderType.ToString());
            BaseUrlTextBox.Text = settings.ApiBaseUrl;
            TextEndpointTextBox.Text = settings.TextEndpoint;
            VisionEndpointTextBox.Text = settings.VisionEndpoint;
            TextModelTextBox.Text = settings.TextModel;
            VisionModelTextBox.Text = settings.VisionModel;
            ExtraHeadersTextBox.Text = string.Join(
                Environment.NewLine,
                settings.ExtraHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
            AnthropicVersionTextBox.Text = settings.AnthropicVersion;
            SupportsTextCheckBox.IsChecked = settings.SupportsText;
            SupportsVisionCheckBox.IsChecked = settings.SupportsVision;
            NetworkEnabledCheckBox.IsChecked = settings.NetworkEnabled;
            AllowImageUploadCheckBox.IsChecked = settings.AllowImageUploadInAuto;
            AllowInsecureTlsCheckBox.IsChecked = settings.AllowInsecureTls;
            SafeDevModeCheckBox.IsChecked = settings.SafeDevMode;
            SelectComboBoxItem(ModeComboBox, settings.Mode.ToString());
            StatusTextBlock.Text = CredentialStore.HasApiKey()
                ? "当前模型密钥已安全保存在 Windows 凭据管理器。"
                : "尚未配置模型密钥；已启用内置免费基础翻译服务。";
            if (!settings.NetworkEnabled)
            {
                StatusTextBlock.Text += " ⚠️「启用大模型网络翻译」当前已关闭，模型请求会被直接拒绝。";
            }
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"读取设置失败：{exception.Message}";
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void LoadOcrSettings()
    {
        try
        {
            if (WindowsOcrService.IsSupported)
            {
                var langs = WindowsOcrService.AvailableLanguages;
                OcrStatusText.Text = $"✅ Windows Native OCR 正常就绪，已检测到 {langs.Count} 种语言识别包。";
                OcrLanguagesListBox.ItemsSource = langs;
            }
            else
            {
                OcrStatusText.Text = "⚠️ 系统未检测到 Windows OCR 语言包，可在系统设置中添加。";
                OcrLanguagesListBox.ItemsSource = new[] { "未检测到语言包" };
            }
        }
        catch (Exception ex)
        {
            OcrStatusText.Text = $"检测 OCR 状态失败：{ex.Message}";
        }
    }

    private static void SelectComboBoxItem(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }

    // ================= Navigation =================
    private void TranslateNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(TranslatePanel, TranslateNavButton);

    private void GeneralNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(GeneralPanel, GeneralNavButton);

    private void ProviderNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(ProviderPanel, ProviderNavButton);

    private void OcrNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(OcrPanel, OcrNavButton);

    private void PrivacyNavButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
        ShowSection(PrivacyPanel, PrivacyNavButton);
    }

    private void ShowSection(FrameworkElement section, Button activeButton)
    {
        TranslatePanel.Visibility = Visibility.Collapsed;
        GeneralPanel.Visibility = Visibility.Collapsed;
        ProviderPanel.Visibility = Visibility.Collapsed;
        OcrPanel.Visibility = Visibility.Collapsed;
        PrivacyPanel.Visibility = Visibility.Collapsed;
        section.Visibility = Visibility.Visible;

        TranslateNavButton.Background = Brushes.Transparent;
        GeneralNavButton.Background = Brushes.Transparent;
        ProviderNavButton.Background = Brushes.Transparent;
        OcrNavButton.Background = Brushes.Transparent;
        PrivacyNavButton.Background = Brushes.Transparent;
        activeButton.Background = (Brush)FindResource("AccentMutedBrush");
    }

    // ================= Standalone Translation =================
    private async void StandaloneTranslateButton_Click(object sender, RoutedEventArgs e)
    {
        var sourceText = StandaloneSourceInput.Text.Trim();
        if (string.IsNullOrEmpty(sourceText))
        {
            return;
        }

        var sourceLang = ((ComboBoxItem)StandaloneSourceLang.SelectedItem).Tag?.ToString() ?? "auto";
        var targetLang = ((ComboBoxItem)StandaloneTargetLang.SelectedItem).Tag?.ToString() ?? "zh-CN";

        StandaloneStatusText.Text = "正在翻译中…";
        StandaloneTranslateButton.IsEnabled = false;

        try
        {
            var apiKey = CredentialStore.LoadApiKey();
            var response = await CoreBridge.TranslateTextAsync(apiKey, sourceText, sourceLang, targetLang);
            StandaloneResultText.Text = response.Result.TranslatedText;
            var engineName = response.Diagnostics.RequestId == "free-web"
                ? "免费基础引擎"
                : response.Diagnostics.ProviderType.ToString();
            StandaloneStatusText.Text = $"翻译完成 · {engineName} · {response.Diagnostics.ElapsedMs} ms";

            _history.TryAdd(
                new TranslationHistoryEntry(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    "输入",
                    sourceText,
                    response.Result.TranslatedText,
                    response.Result.Explanation,
                    response.Result.ProtectedTerms),
                _shellSettings.HistoryEnabled);
        }
        catch (Exception ex)
        {
            StandaloneStatusText.Text = $"翻译失败：{ex.Message}";
            StandaloneResultText.Text = $"错误：{ex.Message}";
        }
        finally
        {
            StandaloneTranslateButton.IsEnabled = true;
        }
    }

    private void StandaloneSourceInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            StandaloneTranslateButton_Click(sender, e);
        }
    }

    private void StandaloneSwapButton_Click(object sender, RoutedEventArgs e)
    {
        var sTag = ((ComboBoxItem)StandaloneSourceLang.SelectedItem).Tag?.ToString() ?? "auto";
        var tTag = ((ComboBoxItem)StandaloneTargetLang.SelectedItem).Tag?.ToString() ?? "zh-CN";

        if (sTag == "auto") sTag = "zh-CN";

        SelectComboBoxItem(StandaloneSourceLang, tTag);
        SelectComboBoxItem(StandaloneTargetLang, sTag);

        var currentRes = StandaloneResultText.Text;
        if (!string.IsNullOrWhiteSpace(currentRes))
        {
            StandaloneSourceInput.Text = currentRes;
            StandaloneResultText.Clear();
        }
    }

    private void StandaloneSourceSpeak_Click(object sender, RoutedEventArgs e)
    {
        var text = StandaloneSourceInput.Text.Trim();
        if (!string.IsNullOrEmpty(text)) TtsService.Speak(text);
    }

    private void StandaloneSourceCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = StandaloneSourceInput.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            StandaloneStatusText.Text = "已复制原文";
        }
    }

    private void StandaloneSourceClear_Click(object sender, RoutedEventArgs e)
    {
        StandaloneSourceInput.Clear();
        StandaloneResultText.Clear();
        StandaloneStatusText.Text = "已清空";
        StandaloneSourceInput.Focus();
    }

    private void StandaloneResultSpeak_Click(object sender, RoutedEventArgs e)
    {
        var text = StandaloneResultText.Text.Trim();
        if (!string.IsNullOrEmpty(text)) TtsService.Speak(text);
    }

    private void StandaloneResultCopy_Click(object sender, RoutedEventArgs e)
    {
        var text = StandaloneResultText.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Clipboard.SetText(text);
            StandaloneStatusText.Text = "已复制译文";
        }
    }

    // ================= Presets =================
    private void PresetOpenAi_Click(object sender, RoutedEventArgs e)
    {
        SelectComboBoxItem(ProviderTypeComboBox, "OpenAiCompatible");
        BaseUrlTextBox.Text = "https://api.openai.com/v1";
        TextEndpointTextBox.Text = "/chat/completions";
        VisionEndpointTextBox.Text = "/chat/completions";
        TextModelTextBox.Text = "gpt-4o-mini";
        VisionModelTextBox.Text = "gpt-4o-mini";
        StatusTextBlock.Text = "已应用 OpenAI 官方预设，请填入 API Key。";
    }

    private void PresetDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        SelectComboBoxItem(ProviderTypeComboBox, "OpenAiCompatible");
        BaseUrlTextBox.Text = "https://api.deepseek.com";
        TextEndpointTextBox.Text = "/chat/completions";
        VisionEndpointTextBox.Text = "/chat/completions";
        TextModelTextBox.Text = "deepseek-chat";
        VisionModelTextBox.Text = string.Empty;
        StatusTextBlock.Text = "已应用 DeepSeek 预设，请填入 API Key。";
    }

    private void PresetGemini_Click(object sender, RoutedEventArgs e)
    {
        SelectComboBoxItem(ProviderTypeComboBox, "GeminiGenerateContent");
        BaseUrlTextBox.Text = "https://generativelanguage.googleapis.com";
        TextEndpointTextBox.Text = "/v1beta/models/{model}:generateContent";
        VisionEndpointTextBox.Text = "/v1beta/models/{model}:generateContent";
        TextModelTextBox.Text = "gemini-1.5-flash";
        VisionModelTextBox.Text = "gemini-1.5-flash";
        StatusTextBlock.Text = "已应用 Google Gemini 预设，请填入 API Key。";
    }

    private void PresetClaude_Click(object sender, RoutedEventArgs e)
    {
        SelectComboBoxItem(ProviderTypeComboBox, "AnthropicMessages");
        BaseUrlTextBox.Text = "https://api.anthropic.com";
        TextEndpointTextBox.Text = "/v1/messages";
        VisionEndpointTextBox.Text = "/v1/messages";
        TextModelTextBox.Text = "claude-3-5-sonnet-20241022";
        VisionModelTextBox.Text = "claude-3-5-sonnet-20241022";
        AnthropicVersionTextBox.Text = "2023-06-01";
        StatusTextBlock.Text = "已应用 Claude 预设，请填入 API Key。";
    }

    private void PresetOllama_Click(object sender, RoutedEventArgs e)
    {
        SelectComboBoxItem(ProviderTypeComboBox, "OpenAiCompatible");
        BaseUrlTextBox.Text = "http://localhost:11434/v1";
        TextEndpointTextBox.Text = "/chat/completions";
        VisionEndpointTextBox.Text = "/chat/completions";
        TextModelTextBox.Text = "qwen2.5:7b";
        VisionModelTextBox.Text = "llava";
        StatusTextBlock.Text = "已应用本地 Ollama 预设，无需 API Key 即可本地运行！";
    }

    private void ProviderTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ProviderTypeComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }
        var providerType = Enum.Parse<ProviderType>(selected.Tag.ToString()!, ignoreCase: false);
        var (baseUrl, endpoint) = ProviderDefaults(providerType);
        BaseUrlTextBox.Text = baseUrl;
        TextEndpointTextBox.Text = endpoint;
        VisionEndpointTextBox.Text = endpoint;
        TextModelTextBox.Clear();
        VisionModelTextBox.Clear();
        AnthropicVersionTextBox.Text = "2023-06-01";
        StatusTextBlock.Text = "已切换翻译服务，请填写对应模型与密钥。";
    }

    private static (string BaseUrl, string Endpoint) ProviderDefaults(ProviderType providerType) =>
        providerType switch
        {
            ProviderType.OpenAiCompatible => ("https://api.openai.com/v1", "/chat/completions"),
            ProviderType.OpenAiResponses => ("https://api.openai.com/v1", "/responses"),
            ProviderType.AnthropicMessages => ("https://api.anthropic.com", "/v1/messages"),
            ProviderType.GeminiGenerateContent => (
                "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent"),
            _ => throw new ArgumentOutOfRangeException(nameof(providerType)),
        };

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveProviderSettings();
            var shellSettings = new ShellSettings(
                2,
                ((ShortcutOption)SelectionShortcutComboBox.SelectedItem).Id,
                ((ShortcutOption)ScreenshotShortcutComboBox.SelectedItem).Id,
                ((ShortcutOption)CloseShortcutComboBox.SelectedItem).Id,
                HistoryEnabledCheckBox.IsChecked == true,
                SelectedEnum<ThemePreference>(ThemeComboBox));
            var validationError = shellSettings.ValidateHotkeys();
            if (validationError is not null)
            {
                throw new InvalidOperationException(validationError);
            }
            if (ApplyShellSettings is not null && !ApplyShellSettings(shellSettings))
            {
                throw new InvalidOperationException("快捷键注册失败，原快捷键已保留。请选择其他组合键。");
            }
            try
            {
                ShellSettingsStore.Save(shellSettings);
            }
            catch
            {
                _ = ApplyShellSettings?.Invoke(_shellSettings);
                throw;
            }
            _shellSettings = shellSettings;
            UpdateShortcutHint(shellSettings);
            StatusTextBlock.Text = "更改已保存。";
            if (CredentialStore.HasApiKey())
            {
                StatusTextBlock.Text += " API Key 已存入 Windows 凭据管理器，输入框清空属正常，无需重填。";
            }
            if (NetworkEnabledCheckBox.IsChecked != true)
            {
                StatusTextBlock.Text += " ⚠️「启用大模型网络翻译」仍未勾选，模型请求会被拒绝！";
            }
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"保存失败：{exception.Message}";
        }
    }

    private void SaveProviderSettings()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
        {
            CredentialStore.SaveApiKey(ApiKeyPasswordBox.Password.Trim());
            ApiKeyPasswordBox.Clear();
        }
        var settings = new ProviderSettings(
            2,
            SelectedEnum<ProviderType>(ProviderTypeComboBox),
            BaseUrlTextBox.Text.Trim(),
            TextEndpointTextBox.Text.Trim(),
            VisionEndpointTextBox.Text.Trim(),
            TextModelTextBox.Text.Trim(),
            VisionModelTextBox.Text.Trim(),
            ParseExtraHeaders(ExtraHeadersTextBox.Text),
            AnthropicVersionTextBox.Text.Trim(),
            SupportsTextCheckBox.IsChecked == true,
            SupportsVisionCheckBox.IsChecked == true,
            NetworkEnabledCheckBox.IsChecked == true,
            SelectedEnum<TranslationMode>(ModeComboBox),
            AllowImageUploadCheckBox.IsChecked == true,
            SafeDevModeCheckBox.IsChecked == true,
            AllowInsecureTlsCheckBox.IsChecked == true,
            CredentialStore.HasApiKey());
        CoreBridge.SaveSettings(settings);
    }

    private static T SelectedEnum<T>(ComboBox comboBox) where T : struct, Enum
    {
        if (comboBox.SelectedItem is not ComboBoxItem selected ||
            !Enum.TryParse<T>(selected.Tag?.ToString(), ignoreCase: false, out var value))
        {
            throw new InvalidOperationException("请选择有效的配置项。");
        }
        return value;
    }

    private static IReadOnlyDictionary<string, string> ParseExtraHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"自定义请求头格式无效：{line}");
            }
            headers.Add(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }
        return headers;
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        try
        {
            SaveProviderSettings();
            var apiKey = CredentialStore.LoadApiKey();
            var isLocal = CoreBridge.IsLocalBaseUrl(BaseUrlTextBox.Text);
            if (string.IsNullOrWhiteSpace(apiKey) && !isLocal)
            {
                throw new InvalidOperationException("请先保存当前提供商的 API Key。");
            }
            StatusTextBlock.Text = "正在测试文本连接…";
            var response = await CoreBridge.TestConnectionAsync(string.IsNullOrWhiteSpace(apiKey) ? "ollama" : apiKey);
            StatusTextBlock.Text = $"连接成功 · HTTP {response.Diagnostics.StatusCode} · {response.Diagnostics.ElapsedMs} ms";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"连接测试失败：{exception.Message}";
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CredentialStore.SaveApiKey(string.Empty);
            ApiKeyPasswordBox.Clear();
            SaveProviderSettings();
            StatusTextBlock.Text = "API Key 已清除。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"清除 API Key 失败：{exception.Message}";
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
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"加载历史记录失败：{ex.Message}";
        }
    }

    internal void ShowShortcutConflict(string conflict)
    {
        StatusTextBlock.Text = $"快捷键冲突：{conflict}，未能成功注册。";
    }

    private void ApplyHistoryFilter()
    {
        var query = HistorySearchBox?.Text?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _allHistory
            : _allHistory.Where(h =>
                h.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.Translation.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        HistoryListBox.ItemsSource = filtered.Select(h => new
        {
            SourceKind = h.SourceKind,
            CreatedAtText = h.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            SourceText = h.Source,
            TranslatedText = h.Translation,
            Raw = h
        }).ToList();
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyHistoryFilter();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        ReloadHistory();
        StatusTextBlock.Text = "历史记录已清空。";
    }

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not null)
        {
            dynamic selected = HistoryListBox.SelectedItem;
            StandaloneSourceInput.Text = selected.SourceText;
            StandaloneResultText.Text = selected.TranslatedText;
            ShowSection(TranslatePanel, TranslateNavButton);
        }
    }

    private void UpdateShortcutHint(ShellSettings settings)
    {
        ShortcutHintText.Text =
            $"{settings.SelectionShortcut.DisplayName}  划词翻译\n" +
            $"{settings.ScreenshotShortcut.DisplayName}  截图翻译\n" +
            $"{settings.CloseShortcut.DisplayName}  关闭浮窗";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
