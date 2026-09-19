
namespace PopGlot.Windows.Sections;

public partial class ShortcutsSection : System.Windows.Controls.UserControl
{
    public ShortcutsSection()
    {
        InitializeComponent();
    }

    // ================= Public accessors for MainWindow =================

    internal HotkeyRecorder SelectionHotkey => SelectionHotkeyRecorder;
    internal HotkeyRecorder ScreenshotHotkey => ScreenshotHotkeyRecorder;
    internal HotkeyRecorder QuickSearchHotkey => QuickSearchHotkeyRecorder;
    internal HotkeyRecorder CloseHotkey => CloseHotkeyRecorder;
    internal HotkeyRecorder ShowWindowHotkey => ShowWindowHotkeyRecorder;

    /// <summary>Resets all shortcut fields to their factory defaults.</summary>
    internal void ResetDefaults()
    {
        SelectionHotkey.BindingValue = HotkeyBinding.SelectionDefault;
        ScreenshotHotkey.BindingValue = HotkeyBinding.ScreenshotDefault;
        QuickSearchHotkey.BindingValue = HotkeyBinding.QuickSearchDefault;
        CloseHotkey.BindingValue = HotkeyBinding.CloseDefault;
        ShowWindowHotkey.BindingValue = HotkeyBinding.ShowWindowDefault;
    }
}
