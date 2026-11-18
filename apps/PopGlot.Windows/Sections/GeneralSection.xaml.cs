using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Reflection;

namespace PopGlot.Windows.Sections;

public partial class GeneralSection : System.Windows.Controls.UserControl
{
    private bool _loading;
    private bool _loadingExtended;

    public GeneralSection()
    {
        InitializeComponent();
        ShowAppVersion();
        LoadExtendedSettings();
    }

    /// <summary>
    /// 0.1.25：唯一的版本展示面。正文是三段式程序集版本；悬停提示是完整
    /// InformationalVersion（构建带有 +commit 哈希时一并显示）。
    /// </summary>
    private void ShowAppVersion()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        AppVersionText.Text = version;
        // 0.1.40: plain version only — the +commit hash reads as noise to users.
        AppVersionText.ToolTip = version;
    }

    // ================= Public accessors for MainWindow =================

    internal ToggleButton CloseOnFocusLoss => CloseOnFocusLossToggle;
    internal ToggleButton AutoCopy => AutoCopyToggle;
    internal ToggleButton StartWithWindows => StartWithWindowsToggle;
    internal ToggleButton CloseToTray => CloseToTrayToggle;
    internal ToggleButton IncludeExplanation => IncludeExplanationToggle;
    internal ToggleButton ProtectTokens => ProtectTokensToggle;
    internal ComboBox ThemeCombo => ThemeComboBox;
    internal TextBlock StartupStateHintText => StartupStateHint;
    internal Button StartupRepair => StartupRepairButton;
    internal ToggleButton AutoUpdateCheck => AutoUpdateCheckToggle;
    internal ToggleButton LiveTranslate => LiveTranslateToggle;
    internal ComboBox LiveTranslateDelay => LiveTranslateDelayCombo;
    internal ComboBox LiveTranslateScope => LiveTranslateScopeCombo;
    internal ComboBox QuickSearchMode => QuickSearchModeCombo;
    internal RadioButton ProxySystem => ProxySystemRadio;
    internal RadioButton ProxyDirect => ProxyDirectRadio;
    internal RadioButton ProxyCustom => ProxyCustomRadio;
    internal TextBox CustomProxyUrlInput => CustomProxyTextBox;
    internal TextBlock ProxyValidationHint => ProxyValidationText;
    internal ToggleButton ProxyBypassLocal => ProxyBypassLocalToggle;
    internal ToggleButton ProxyBypassDomestic => ProxyBypassDomesticToggle;

    internal bool IsLoading
    {
        get => _loading;
        set
        {
            var finishedLoading = _loading && !value;
            _loading = value;
            if (finishedLoading)
            {
                LoadExtendedSettings();
            }
        }
    }

    internal void LoadExtendedSettings()
    {
        if (LiveTranslateToggle is null || ProxySystemRadio is null)
        {
            return;
        }

        _loadingExtended = true;
        try
        {
            var settings = ShellSettingsStore.Load();
            LiveTranslateToggle.IsChecked = settings.LiveTranslateEnabled;
            Helpers.SelectComboByTag(LiveTranslateDelayCombo, settings.LiveTranslateDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Helpers.SelectComboByTag(LiveTranslateScopeCombo, settings.LiveTranslateScope.ToString());
            Helpers.SelectComboByTag(QuickSearchModeCombo, settings.QuickSearchWindowMode.ToString());

            ProxySystemRadio.IsChecked = settings.NetworkProxyMode == NetworkProxyMode.System;
            ProxyDirectRadio.IsChecked = settings.NetworkProxyMode == NetworkProxyMode.Direct;
            ProxyCustomRadio.IsChecked = settings.NetworkProxyMode == NetworkProxyMode.Custom;
            CustomProxyTextBox.Text = settings.CustomProxyUrl ?? string.Empty;
            ProxyBypassLocalToggle.IsChecked = settings.ProxyBypassLocal;
            ProxyBypassDomesticToggle.IsChecked = settings.ProxyBypassDomesticRoutes;

            UpdateProxyUiState(validateCustomInput: settings.NetworkProxyMode == NetworkProxyMode.Custom);
        }
        catch (Exception)
        {
            // Keep default controls if storage is temporarily unavailable.
        }
        finally
        {
            _loadingExtended = false;
        }
    }

    private void LiveTranslateSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingExtended)
        {
            return;
        }

        var enabled = LiveTranslateToggle.IsChecked == true;
        var delayTag = (LiveTranslateDelayCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var delayMs = int.TryParse(delayTag, out var parsedDelay)
            ? ShellSettings.NormalizeLiveTranslateDelayMs(parsedDelay)
            : ShellSettings.DefaultLiveTranslateDelayMs;
        var scope = Helpers.SelectedEnum(LiveTranslateScopeCombo, PopGlot.Windows.LiveTranslateScope.FreeEngineOnly);
        var mode = Helpers.SelectedEnum(QuickSearchModeCombo, QuickSearchWindowMode.Compact);

        if (LiveTranslateDelayCombo is not null)
        {
            LiveTranslateDelayCombo.IsEnabled = enabled;
        }
        if (LiveTranslateScopeCombo is not null)
        {
            LiveTranslateScopeCombo.IsEnabled = enabled;
        }

        try
        {
            ShellSettingsStore.Update(current => current with
            {
                LiveTranslateEnabled = enabled,
                LiveTranslateDelayMs = delayMs,
                LiveTranslateScope = scope,
                QuickSearchWindowMode = mode,
            });
        }
        catch (Exception)
        {
            // Keep UI responsive even if disk write transiently fails.
        }
    }

    private void ProxyModeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingExtended)
        {
            return;
        }

        var selectedMode = ReadSelectedProxyMode();
        UpdateProxyUiState(validateCustomInput: selectedMode == NetworkProxyMode.Custom);

        if (selectedMode == NetworkProxyMode.Custom)
        {
            if (ValidateCustomProxyUrl(out var normalizedUrl))
            {
                SaveProxySettings(NetworkProxyMode.Custom, normalizedUrl);
            }
            else
            {
                CustomProxyTextBox.Focus();
            }
            return;
        }

        SaveProxySettings(selectedMode, ShellSettings.NormalizeCustomProxyUrl(CustomProxyTextBox.Text));
    }

    private void ProxyBypass_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingExtended)
        {
            return;
        }

        var selectedMode = ReadSelectedProxyMode();
        if (selectedMode == NetworkProxyMode.Custom && !ValidateCustomProxyUrl(out var validUrl))
        {
            SaveProxySettings(null, null);
            return;
        }

        SaveProxySettings(
            selectedMode,
            selectedMode == NetworkProxyMode.Custom
                ? ShellSettings.NormalizeCustomProxyUrl(CustomProxyTextBox.Text)
                : ShellSettings.NormalizeCustomProxyUrl(CustomProxyTextBox.Text));
    }

    private void CustomProxyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _loadingExtended || ProxyCustomRadio.IsChecked != true)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomProxyTextBox.Text))
        {
            ShowProxyValidation("请输入代理地址，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7891", isError: true);
            return;
        }

        if (App.TryValidateCustomProxyUrl(CustomProxyTextBox.Text, out _))
        {
            ShowProxyValidation(null, isError: false);
        }
    }

    private void CustomProxyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingExtended || ProxyCustomRadio.IsChecked != true)
        {
            return;
        }

        TryCommitCustomProxy(showFeedbackIfValid: false);
    }

    private void CustomProxyTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ProxyCustomRadio.IsChecked == true)
        {
            e.Handled = true;
            TryCommitCustomProxy(showFeedbackIfValid: true);
        }
    }

    private void ApplyCustomProxy_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _loadingExtended)
        {
            return;
        }

        ProxyCustomRadio.IsChecked = true;
        UpdateProxyUiState(validateCustomInput: true);
        TryCommitCustomProxy(showFeedbackIfValid: true);
    }

    private void TryCommitCustomProxy(bool showFeedbackIfValid)
    {
        if (!ValidateCustomProxyUrl(out var normalizedUrl))
        {
            return;
        }

        if (normalizedUrl is not null && !string.Equals(CustomProxyTextBox.Text.Trim(), normalizedUrl, StringComparison.Ordinal))
        {
            _loadingExtended = true;
            try
            {
                CustomProxyTextBox.Text = normalizedUrl;
            }
            finally
            {
                _loadingExtended = false;
            }
        }

        SaveProxySettings(NetworkProxyMode.Custom, normalizedUrl);
        if (showFeedbackIfValid)
        {
            ShowProxyValidation($"已应用自定义代理：{normalizedUrl}", isError: false);
        }
    }

    private NetworkProxyMode ReadSelectedProxyMode()
    {
        if (ProxyDirectRadio.IsChecked == true)
        {
            return NetworkProxyMode.Direct;
        }
        if (ProxyCustomRadio.IsChecked == true)
        {
            return NetworkProxyMode.Custom;
        }
        return NetworkProxyMode.System;
    }

    private void UpdateProxyUiState(bool validateCustomInput)
    {
        var isCustom = ProxyCustomRadio.IsChecked == true;
        CustomProxyRow.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        CustomProxyTextBox.IsEnabled = isCustom;
        ApplyCustomProxyButton.IsEnabled = isCustom;

        if (LiveTranslateDelayCombo is not null)
        {
            LiveTranslateDelayCombo.IsEnabled = LiveTranslateToggle.IsChecked == true;
        }
        if (LiveTranslateScopeCombo is not null)
        {
            LiveTranslateScopeCombo.IsEnabled = LiveTranslateToggle.IsChecked == true;
        }

        if (!isCustom)
        {
            ShowProxyValidation(null, isError: false);
        }
        else if (validateCustomInput)
        {
            ValidateCustomProxyUrl(out _);
        }
    }

    private bool ValidateCustomProxyUrl(out string? normalizedUrl)
    {
        normalizedUrl = null;
        var raw = CustomProxyTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowProxyValidation("请输入代理地址，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7891", isError: true);
            return false;
        }

        if (!App.TryValidateCustomProxyUrl(raw, out var validUrl) || validUrl is null)
        {
            ShowProxyValidation("代理地址格式无效，请使用 http://、https://、socks5:// 或 socks5h:// 并包含端口。", isError: true);
            return false;
        }

        normalizedUrl = validUrl;
        ShowProxyValidation(null, isError: false);
        return true;
    }

    private void ShowProxyValidation(string? message, bool isError)
    {
        if (ProxyValidationText is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ProxyValidationText.Text = string.Empty;
            ProxyValidationText.Visibility = Visibility.Collapsed;
            return;
        }

        ProxyValidationText.Text = message;
        ProxyValidationText.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "DangerBrush" : "SuccessBrush");
        ProxyValidationText.Visibility = Visibility.Visible;
    }

    private void SaveProxySettings(NetworkProxyMode? modeOverride, string? customUrlOverride)
    {
        var bypassLocal = ProxyBypassLocalToggle.IsChecked == true;
        var bypassDomestic = ProxyBypassDomesticToggle.IsChecked == true;
        try
        {
            var updated = ShellSettingsStore.Update(current => current with
            {
                NetworkProxyMode = modeOverride ?? current.NetworkProxyMode,
                CustomProxyUrl = customUrlOverride ?? current.CustomProxyUrl,
                ProxyBypassLocal = bypassLocal,
                ProxyBypassDomesticRoutes = bypassDomestic,
            });
            App.SyncNetworkProxyToCore(updated);
        }
        catch (Exception)
        {
            // Keep UI responsive even if persistence fails transiently.
        }
    }

    /// <summary>Writes the startup update-check switch without a settings save bar.</summary>
    private void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var enabled = AutoUpdateCheckToggle.IsChecked == true;
        try
        {
            ShellSettingsStore.Update(current => current with { CheckUpdatesEnabled = enabled });
        }
        catch (System.Exception)
        {
            // 开关只影响一次后台检查，写盘失败不必打断界面。
        }
        UpdateStatusText.Text = enabled
            ? "启动时检查一次新版，有更新会提醒你，不会自动下载。"
            : "已关闭启动检查；需要时可随时点「立即检查」。";
    }

    /// <summary>
    /// 手动检查：读 GitHub Releases（公开只读，不需要令牌），把结果写在状态
    /// 行里。发现新版本时打开发布页——下载与安装始终由用户亲手完成。
    /// </summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            var result = await Services.UpdateService.CheckAsync();
            if (!result.Ok)
            {
                UpdateStatusText.Text = $"检查未完成：{result.Error ?? "未知原因"}。可稍后重试或手动访问发布页。";
                return;
            }
            if (result.UpToDate)
            {
                UpdateStatusText.Text = $"已是最新版本（{result.CurrentVersion}）。";
                return;
            }
            UpdateStatusText.Text = string.IsNullOrEmpty(result.InstallerName)
                ? $"发现新版本 {result.LatestVersion}；正在打开发布页。"
                : $"发现新版本 {result.LatestVersion}（安装包：{result.InstallerName}）；正在打开发布页。";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = result.ReleaseUrl ?? Services.UpdateService.ReleasesPageUrl,
                    UseShellExecute = true,
                });
            }
            catch (System.Exception)
            {
                // 打不开浏览器不构成故障：状态行已经给出版本号与安装包名。
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private bool _compact;

    /// <summary>
    /// Stacks theme and startup controls only when less than 400 DIP is available.
    /// </summary>
    internal void SetCompact(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;

        if (ThemeComboBox is not null && ThemeRowGrid?.ColumnDefinitions.Count > 1)
        {
            if (compact)
            {
                ThemeRowGrid.ColumnDefinitions[1].Width = new GridLength(0);
                PlaceThemeChoice(0, 1, new Thickness(0, 8, 0, 0), HorizontalAlignment.Left);
            }
            else
            {
                ThemeRowGrid.ColumnDefinitions[1].Width = GridLength.Auto;
                PlaceThemeChoice(1, 0, new Thickness(0), HorizontalAlignment.Right);
            }
        }

        if (StartupControlsPanel is not null && StartupRowGrid?.ColumnDefinitions.Count > 1)
        {
            if (compact)
            {
                StartupRowGrid.ColumnDefinitions[1].Width = new GridLength(0);
                Grid.SetColumn(StartupControlsPanel, 0);
                Grid.SetRow(StartupControlsPanel, 1);
                StartupControlsPanel.Margin = new Thickness(0, 8, 0, 0);
                StartupControlsPanel.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                StartupRowGrid.ColumnDefinitions[1].Width = GridLength.Auto;
                Grid.SetColumn(StartupControlsPanel, 1);
                Grid.SetRow(StartupControlsPanel, 0);
                StartupControlsPanel.Margin = new Thickness(0);
                StartupControlsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }
    }

    /// <summary>
    /// C07: paints the honest desired-vs-actual startup picture. The toggle
    /// is the DESIRE; the hint is what Windows will actually do, and the
    /// repair button appears whenever the requested state is not effective.
    /// </summary>
    internal void UpdateStartupState(StartupState state)
    {
        StartupStateHint.Text = state.DescribeZh();
        StartupStateHint.Opacity = state.EffectiveEnabled || state.LastError is not null ? 1.0 : 0.85;
        StartupRepair.Visibility =
            state.DesiredEnabled && !state.EffectiveEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // ================= Event handlers =================

    private bool _syncingTheme;

    private void PlaceThemeChoice(int column, int row, Thickness margin, HorizontalAlignment alignment)
    {
        Grid.SetColumn(ThemeComboBox, column);
        Grid.SetRow(ThemeComboBox, row);
        ThemeComboBox.Margin = margin;
        ThemeComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (ThemeChoicePanel is null)
        {
            return;
        }

        Grid.SetColumn(ThemeChoicePanel, column);
        Grid.SetRow(ThemeChoicePanel, row);
        ThemeChoicePanel.Margin = margin;
        ThemeChoicePanel.HorizontalAlignment = alignment;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncThemeChoices();
        // Load writes the saved value through the combo. A user click does
        // not: ChooseTheme previews even when the combo value does not change.
        if (_loading)
        {
            return;
        }

        ApplyThemePreview();
    }

    private void ThemeChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        ChooseTheme(tag);
    }

    /// <summary>
    /// The swatch is the control. Preview runs even when the hidden combo
    /// already has this value, and even while settings are still loading.
    /// </summary>
    internal void ChooseTheme(string tag)
    {
        if (_syncingTheme || string.IsNullOrEmpty(tag))
        {
            return;
        }

        var selected = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!string.Equals(selected, tag, StringComparison.Ordinal))
        {
            Helpers.SelectComboByTag(ThemeComboBox, tag);
        }
        else
        {
            SyncThemeChoices();
        }

        ApplyThemePreview();
    }

    private void ApplyThemePreview()
    {
        ThemeService.Apply(Helpers.SelectedEnum(ThemeComboBox, ThemePreference.System));
        var window = Window.GetWindow(this);
        if (window is not null)
        {
            ThemeService.ApplyWindowChrome(window);
        }
    }

    private void SyncThemeChoices()
    {
        if (ThemeSystemChoice is null)
        {
            return;
        }

        var tag = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        _syncingTheme = true;
        try
        {
            ThemeSystemChoice.IsChecked = tag == "System";
            ThemeLightChoice.IsChecked = tag == "Light";
            ThemeDarkChoice.IsChecked = tag == "Dark";
        }
        finally
        {
            _syncingTheme = false;
        }
    }

    /// <summary>
    /// 「重新显示引导」入口：0.1.35 起直接打开首次配置向导——隐私选择
    /// （含内置免费引擎开关）就住在那里，这是关掉免费引擎后唯一的回头路。
    /// 写盘失败只损失"下次启动再出现"，不影响本次展示。
    /// </summary>
    private void RestartOnboarding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellSettingsStore.Update(current => current with { HasCompletedOnboarding = false });
        }
        catch (System.Exception)
        {
            // 展示引导本身不依赖这次写盘成功。
        }
        var owner = Window.GetWindow(this)
            ?? System.Windows.Application.Current?.MainWindow;
        var wizard = new FirstRunWindow { Owner = owner };
        WindowPositioner.ConvergeFirstShow(wizard);
        wizard.ShowDialog();
    }

    // C25：离线帮助查看器。纯本机渲染，FindProjectRoot 式回退保证开发与
    // 测试宿主也能打开；找不到文档时窗口自身给出诚实降级，不联网。
    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = Window.GetWindow(this) };
        // 帮助窗口跟着所有者所在显示器居中，而不是无条件落在主屏。
        WindowPositioner.ConvergeFirstShow(help);
        help.Show();
    }
}
