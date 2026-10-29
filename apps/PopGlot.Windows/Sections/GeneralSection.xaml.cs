using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Reflection;

namespace PopGlot.Windows.Sections;

public partial class GeneralSection : System.Windows.Controls.UserControl
{
    private bool _loading;

    public GeneralSection()
    {
        InitializeComponent();
        ShowAppVersion();
    }

    /// <summary>
    /// 0.1.25：唯一的版本展示面。正文是三段式程序集版本；悬停提示是完整
    /// InformationalVersion（构建带有 +commit 哈希时一并显示）。
    /// </summary>
    private void ShowAppVersion()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        AppVersionText.Text = version;
        AppVersionText.ToolTip = typeof(App).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? version;
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

    internal bool IsLoading { get => _loading; set => _loading = value; }

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
            ? "只在启动时静默查一次 GitHub Releases；有新版只提示，不自动下载安装。"
            : "已关闭启动检查；你可以随时点「立即检查」。";
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
