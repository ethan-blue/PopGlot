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
                Grid.SetColumn(ThemeComboBox, 0);
                Grid.SetRow(ThemeComboBox, 1);
                ThemeComboBox.Margin = new Thickness(0, 8, 0, 0);
                ThemeComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                ThemeRowGrid.ColumnDefinitions[1].Width = new GridLength(170);
                Grid.SetColumn(ThemeComboBox, 1);
                Grid.SetRow(ThemeComboBox, 0);
                ThemeComboBox.Margin = new Thickness(0);
                ThemeComboBox.HorizontalAlignment = HorizontalAlignment.Stretch;
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
    ///「重新启用」button appears only when Task Manager disabled the entry.
    /// </summary>
    internal void UpdateStartupState(StartupState state)
    {
        StartupStateHint.Text = state.DescribeZh();
        StartupStateHint.Opacity = state.EffectiveEnabled || state.LastError is not null ? 1.0 : 0.85;
        StartupRepair.Visibility =
            state.OsDisabled == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Event handlers =================

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        ThemeService.Apply(Helpers.SelectedEnum(ThemeComboBox, ThemePreference.System));
        // Walk up to find the parent Window and apply chrome
        var window = Window.GetWindow(this);
        if (window is not null)
        {
            ThemeService.ApplyWindowChrome(window);
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
}
