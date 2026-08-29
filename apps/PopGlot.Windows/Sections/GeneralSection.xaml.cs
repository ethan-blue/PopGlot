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
    internal ToggleButton IncludeExplanation => IncludeExplanationToggle;
    internal ToggleButton ProtectTokens => ProtectTokensToggle;
    internal ComboBox ThemeCombo => ThemeComboBox;

    internal bool IsLoading { get => _loading; set => _loading = value; }

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
}
