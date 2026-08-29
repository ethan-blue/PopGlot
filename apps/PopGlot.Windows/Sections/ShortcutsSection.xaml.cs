
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
    internal HotkeyRecorder CloseHotkey => CloseHotkeyRecorder;
    internal HotkeyRecorder ShowWindowHotkey => ShowWindowHotkeyRecorder;
}
