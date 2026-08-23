using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace PopGlot.Windows;

public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private ShellSettings _shellSettings;
    private bool _loadingSettings;

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
        ShowSection(GeneralPanel, GeneralNavButton);
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
            SafeDevModeCheckBox.IsChecked = settings.SafeDevMode;
            SelectComboBoxItem(ModeComboBox, settings.Mode.ToString());
            StatusTextBlock.Text = CredentialStore.HasApiKey()
                ? "当前活动密钥已安全保存在 Windows 凭据管理器。"
                : "尚未配置模型密钥；应用仍可使用托盘、快捷键和本地界面。";
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
            StatusTextBlock.Text = "更改已保存。保存设置本身不会发送网络请求。";
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
            var settings = CoreBridge.GetSettings();
            if (!settings.NetworkEnabled || settings.SafeDevMode)
            {
                throw new InvalidOperationException("请启用模型网络，并关闭安全开发模式。此操作只发送内置文本。");
            }
            var apiKey = CredentialStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("请先保存当前提供商的 API Key。");
            }
            StatusTextBlock.Text = "正在测试文本连接，不会上传截图…";
            var response = await CoreBridge.TestConnectionAsync(apiKey);
            StatusTextBlock.Text = $"连接成功 · HTTP {response.Diagnostics.StatusCode} · " +
                $"{response.Diagnostics.ElapsedMs} ms";
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
            var settings = CoreBridge.GetSettings();
            CoreBridge.SaveSettings(settings with { ApiKeyConfigured = false });
            ApiKeyPasswordBox.Clear();
            StatusTextBlock.Text = "已从 Windows 凭据管理器删除活动 API Key。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"删除密钥失败：{exception.Message}";
        }
    }

    internal void ReloadHistory()
    {
        var entries = _history.Load();
        HistoryListBox.ItemsSource = entries.Select(entry =>
            $"{entry.CreatedAt.ToLocalTime():MM-dd HH:mm}  ·  {entry.SourceKind}\n" +
            $"{SingleLine(entry.Source, 70)}  →  {SingleLine(entry.Translation, 70)}");
        HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryListBox.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string SingleLine(string value, int limit)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= limit ? line : $"{line[..limit]}…";
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "清除本机上的全部 PopGlot 翻译历史？此操作无法撤销。",
                "清除历史",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }
        _history.Clear();
        ReloadHistory();
        StatusTextBlock.Text = "本地翻译历史已清除。";
    }

    private void GeneralNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(GeneralPanel, GeneralNavButton);

    private void ProviderNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(ProviderPanel, ProviderNavButton);

    private void PrivacyNavButton_Click(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
        ShowSection(PrivacyPanel, PrivacyNavButton);
    }

    private void ShowSection(StackPanel panel, Button selectedButton)
    {
        GeneralPanel.Visibility = panel == GeneralPanel ? Visibility.Visible : Visibility.Collapsed;
        ProviderPanel.Visibility = panel == ProviderPanel ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPanel.Visibility = panel == PrivacyPanel ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { GeneralNavButton, ProviderNavButton, PrivacyNavButton })
        {
            button.Background = button == selectedButton
                ? (Brush)FindResource("AccentMutedBrush")
                : Brushes.Transparent;
        }
    }

    private void UpdateShortcutHint(ShellSettings settings)
    {
        ShortcutHintText.Text = $"{settings.SelectionShortcut.DisplayName} 划词\n" +
            $"{settings.ScreenshotShortcut.DisplayName} 截图";
    }

    internal void ShowShortcutConflict(string shortcut)
    {
        Show();
        Activate();
        ShowSection(GeneralPanel, GeneralNavButton);
        StatusTextBlock.Text = $"无法注册：{shortcut}。请选择未被其他应用占用的组合键。";
    }
}
