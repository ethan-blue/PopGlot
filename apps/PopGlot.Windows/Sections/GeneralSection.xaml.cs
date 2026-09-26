using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PopGlot.Windows.Sections;

public partial class GeneralSection : System.Windows.Controls.UserControl
{
    private bool _loading;

    public GeneralSection()
    {
        InitializeComponent();
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

    internal bool IsLoading { get => _loading; set => _loading = value; }

    private bool _compact;

    /// <summary>
    /// Stacks form fields vertically when the settings window narrows below 700 DIP.
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
    /// 「重新显示引导」入口：把 HasCompletedOnboarding 复位为未完成并回到
    /// 主窗口工作台重新展开三步引导条。与「重新启用」自启按钮一样是即时
    /// 动作，不走设置页的保存条；写盘失败只损失"下次启动再出现"，不影响
    /// 本次展示。
    /// </summary>
    private void RestartOnboarding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var shell = ShellSettingsStore.Load();
            ShellSettingsStore.Save(shell with { HasCompletedOnboarding = false });
        }
        catch (System.Exception)
        {
            // 展示引导本身不依赖这次写盘成功。
        }
        var main = Window.GetWindow(this) as MainWindow
            ?? System.Windows.Application.Current?.MainWindow as MainWindow;
        if (main is null)
        {
            return;
        }
        main.RestartOnboarding();
    }

    // C25：离线帮助查看器。纯本机渲染，FindProjectRoot 式回退保证开发与
    // 测试宿主也能打开；找不到文档时窗口自身给出诚实降级，不联网。
    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow { Owner = Window.GetWindow(this) };
        help.Show();
    }
}
