using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PopGlot.Windows;

public partial class MainWindow : Window
{
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        ShortcutComboBox.ItemsSource = ShortcutOption.Available;
        CurrentShortcut = ShellSettingsStore.LoadShortcut();
        ShortcutComboBox.SelectedItem = CurrentShortcut;
        LoadProviderSettings();
    }

    internal bool AllowClose { get; set; }
    internal ShortcutOption CurrentShortcut { get; private set; }
    internal event EventHandler<ShortcutOption>? ShortcutChanged;

    private void LoadProviderSettings()
    {
        _loadingSettings = true;
        try
        {
            var settings = CoreBridge.GetSettings();
            SelectProviderType(settings.ProviderType);
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
            SelectMode(settings.Mode);
            StatusTextBlock.Text = CredentialStore.HasApiKey()
                ? "已在 Windows 凭据管理器中保存当前活动 API Key。"
                : "尚未配置 API Key；应用不会发起模型网络请求。";
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

    private void SelectMode(TranslationMode mode)
    {
        SelectComboBoxItem(ModeComboBox, mode.ToString());
    }

    private void SelectProviderType(ProviderType providerType)
    {
        SelectComboBoxItem(ProviderTypeComboBox, providerType.ToString());
    }

    private static void SelectComboBoxItem(ComboBox comboBox, string tag)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
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
        StatusTextBlock.Text = "已切换协议；请填写该提供商的模型名称并确认活动 API Key。";
    }

    private static (string BaseUrl, string Endpoint) ProviderDefaults(ProviderType providerType)
    {
        return providerType switch
        {
            ProviderType.OpenAiCompatible => ("https://api.openai.com/v1", "/chat/completions"),
            ProviderType.OpenAiResponses => ("https://api.openai.com/v1", "/responses"),
            ProviderType.AnthropicMessages => ("https://api.anthropic.com", "/v1/messages"),
            ProviderType.GeminiGenerateContent => (
                "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent"),
            _ => throw new ArgumentOutOfRangeException(nameof(providerType)),
        };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings();
            CurrentShortcut = (ShortcutOption)ShortcutComboBox.SelectedItem;
            ShellSettingsStore.SaveShortcut(CurrentShortcut);
            ShortcutChanged?.Invoke(this, CurrentShortcut);
            StatusTextBlock.Text = "设置已保存；保存操作本身不会发送网络请求。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"保存失败：{exception.Message}";
        }
    }

    private ProviderSettings SaveCurrentSettings()
    {
        var selectedMode = SelectedEnum<TranslationMode>(ModeComboBox);
        var providerType = SelectedEnum<ProviderType>(ProviderTypeComboBox);
        if (!string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
        {
            CredentialStore.SaveApiKey(ApiKeyPasswordBox.Password.Trim());
            ApiKeyPasswordBox.Clear();
        }

        var settings = new ProviderSettings(
            2,
            providerType,
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
            selectedMode,
            AllowImageUploadCheckBox.IsChecked == true,
            SafeDevModeCheckBox.IsChecked == true,
            CredentialStore.HasApiKey());
        CoreBridge.SaveSettings(settings);
        return settings;
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
            var settings = SaveCurrentSettings();
            if (!settings.NetworkEnabled)
            {
                throw new InvalidOperationException("请先启用“允许模型网络请求”。");
            }
            if (settings.SafeDevMode)
            {
                throw new InvalidOperationException("安全开发模式仍开启，按设计禁止网络请求。");
            }

            var apiKey = CredentialStore.LoadApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("请先保存当前提供商的 API Key。");
            }

            StatusTextBlock.Text = "正在发送最小文本连接测试；不会上传截图……";
            var response = await CoreBridge.TestConnectionAsync(apiKey);
            StatusTextBlock.Text = $"连接成功：HTTP {response.Diagnostics.StatusCode}，" +
                $"{response.Diagnostics.Attempts} 次尝试，{response.Diagnostics.ElapsedMs} ms。";
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
            StatusTextBlock.Text = "已从 Windows 凭据管理器删除 API Key。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"删除密钥失败：{exception.Message}";
        }
    }

    internal void ShowShortcutConflict(string shortcut)
    {
        Show();
        Activate();
        StatusTextBlock.Text = $"无法注册 {shortcut}，它可能已被其他应用占用。请选择其他组合键。";
    }
}
