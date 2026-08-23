using System.Windows;
using System.Windows.Controls;

namespace PopGlot.Windows;

public partial class MainWindow : Window
{
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
        try
        {
            var settings = CoreBridge.GetSettings();
            BaseUrlTextBox.Text = settings.ApiBaseUrl;
            TextModelTextBox.Text = settings.TextModel;
            VisionModelTextBox.Text = settings.VisionModel;
            AllowImageUploadCheckBox.IsChecked = settings.AllowImageUploadInAuto;
            SafeDevModeCheckBox.IsChecked = settings.SafeDevMode;
            SelectMode(settings.Mode);
            StatusTextBlock.Text = CredentialStore.HasApiKey()
                ? "已在 Windows 凭据管理器中保存 API Key。"
                : "尚未配置 API Key；应用仍可在安全开发模式启动。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"读取设置失败：{exception.Message}";
        }
    }

    private void SelectMode(TranslationMode mode)
    {
        foreach (ComboBoxItem item in ModeComboBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.Ordinal))
            {
                ModeComboBox.SelectedItem = item;
                return;
            }
        }
        ModeComboBox.SelectedIndex = 0;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selectedMode = Enum.Parse<TranslationMode>(
                ((ComboBoxItem)ModeComboBox.SelectedItem).Tag.ToString()!,
                ignoreCase: false);
            if (!string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            {
                CredentialStore.SaveApiKey(ApiKeyPasswordBox.Password.Trim());
                ApiKeyPasswordBox.Clear();
            }

            var settings = new ProviderSettings(
                BaseUrlTextBox.Text.Trim(),
                TextModelTextBox.Text.Trim(),
                VisionModelTextBox.Text.Trim(),
                selectedMode,
                AllowImageUploadCheckBox.IsChecked == true,
                SafeDevModeCheckBox.IsChecked == true,
                CredentialStore.HasApiKey());
            CoreBridge.SaveSettings(settings);

            CurrentShortcut = (ShortcutOption)ShortcutComboBox.SelectedItem;
            ShellSettingsStore.SaveShortcut(CurrentShortcut);
            ShortcutChanged?.Invoke(this, CurrentShortcut);
            StatusTextBlock.Text = "设置已保存。当前初始版本仍不会发送 API 请求。";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"保存失败：{exception.Message}";
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
