
namespace PopGlot.Windows.Sections;

public partial class ShortcutsSection : System.Windows.Controls.UserControl
{
    public ShortcutsSection()
    {
        InitializeComponent();
    }

    internal void SetCompact(bool stacked)
    {
        ShortcutGutter.Width = new System.Windows.GridLength(stacked ? 0 : 24);
        ShortcutRightColumn.Width = stacked
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(InterfaceShortcutGroup, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(InterfaceShortcutGroup, stacked ? 1 : 0);
        InterfaceShortcutGroup.Margin = new System.Windows.Thickness(0, stacked ? 12 : 0, 0, 0);
    }

    // ================= Public accessors for MainWindow =================

    internal HotkeyRecorder SelectionHotkey => SelectionHotkeyRecorder;
    internal HotkeyRecorder ScreenshotHotkey => ScreenshotHotkeyRecorder;
    internal HotkeyRecorder QuickSearchHotkey => QuickSearchHotkeyRecorder;
    internal HotkeyRecorder CloseHotkey => CloseHotkeyRecorder;
    internal HotkeyRecorder ShowWindowHotkey => ShowWindowHotkeyRecorder;
    internal HotkeyRecorder InterfaceWorkbenchHotkey => InterfaceWorkbenchHotkeyRecorder;
    internal HotkeyRecorder InterfaceLibraryHotkey => InterfaceLibraryHotkeyRecorder;
    internal HotkeyRecorder InterfaceSettingsHotkey => InterfaceSettingsHotkeyRecorder;
    internal HotkeyRecorder InterfaceTranslateHotkey => InterfaceTranslateHotkeyRecorder;
    internal HotkeyRecorder InterfaceSpeakHotkey => InterfaceSpeakHotkeyRecorder;
    internal HotkeyRecorder InterfaceModelHotkey => InterfaceModelHotkeyRecorder;
    internal HotkeyRecorder InterfaceStyleHotkey => InterfaceStyleHotkeyRecorder;
    internal HotkeyRecorder InterfaceSwapHotkey => InterfaceSwapHotkeyRecorder;

    /// <summary>Resets all shortcut fields to their factory defaults.</summary>
    internal void ResetDefaults()
    {
        SelectionHotkey.BindingValue = HotkeyBinding.SelectionDefault;
        ScreenshotHotkey.BindingValue = HotkeyBinding.ScreenshotDefault;
        QuickSearchHotkey.BindingValue = HotkeyBinding.QuickSearchDefault;
        CloseHotkey.BindingValue = HotkeyBinding.CloseDefault;
        ShowWindowHotkey.BindingValue = HotkeyBinding.ShowWindowDefault;
        InterfaceModelHotkey.BindingValue = HotkeyBinding.InterfaceModelDefault;
        InterfaceStyleHotkey.BindingValue = HotkeyBinding.InterfaceStyleDefault;
        InterfaceSwapHotkey.BindingValue = HotkeyBinding.InterfaceSwapDefault;
        InterfaceWorkbenchHotkey.BindingValue = HotkeyBinding.InterfaceWorkbenchDefault;
        InterfaceLibraryHotkey.BindingValue = HotkeyBinding.InterfaceLibraryDefault;
        InterfaceSettingsHotkey.BindingValue = HotkeyBinding.InterfaceSettingsDefault;
        InterfaceTranslateHotkey.BindingValue = HotkeyBinding.InterfaceTranslateDefault;
        InterfaceSpeakHotkey.BindingValue = HotkeyBinding.InterfaceSpeakDefault;
    }

    private void ResetDefaultsButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ResetDefaults();
}
