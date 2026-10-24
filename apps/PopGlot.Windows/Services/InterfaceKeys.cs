using System.Windows;
using System.Windows.Input;

namespace PopGlot.Windows.Services;

internal enum InterfaceAction
{
    None, Model, Style, Swap, Workbench, Library, Settings, Translate, Speak,
}

/// <summary>Applied settings snapshot; keyboard dispatch never reads the settings file.</summary>
internal static class InterfaceKeys
{
    private static ShellSettings _settings = ShellSettings.Default;
    internal static ShellSettings Snapshot => _settings;

    public static HotkeyBinding ModelHotkey => _settings.InterfaceModelHotkey;
    public static HotkeyBinding StyleHotkey => _settings.InterfaceStyleHotkey;
    public static HotkeyBinding SwapHotkey => _settings.InterfaceSwapHotkey;
    public static HotkeyBinding WorkbenchHotkey => _settings.InterfaceWorkbenchHotkey;
    public static HotkeyBinding LibraryHotkey => _settings.InterfaceLibraryHotkey;
    public static HotkeyBinding SettingsHotkey => _settings.InterfaceSettingsHotkey;
    public static HotkeyBinding TranslateHotkey => _settings.InterfaceTranslateHotkey;
    public static HotkeyBinding SpeakHotkey => _settings.InterfaceSpeakHotkey;

    public static void Apply(ShellSettings settings)
    {
        _settings = settings;
        if (Application.Current is not { } app || !app.Dispatcher.CheckAccess()) return;
        foreach (Window window in app.Windows)
        {
            RefreshHints(window);
            if (window.FindName("TranslateSection") is FrameworkElement section) RefreshHints(section);
        }
    }

    public static Key Normalize(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    public static InterfaceAction Resolve(Key key, ModifierKeys modifiers)
    {
        if (Matches(ModelHotkey, key, modifiers)) return InterfaceAction.Model;
        if (Matches(StyleHotkey, key, modifiers)) return InterfaceAction.Style;
        if (Matches(SwapHotkey, key, modifiers)) return InterfaceAction.Swap;
        if (Matches(WorkbenchHotkey, key, modifiers)) return InterfaceAction.Workbench;
        if (Matches(LibraryHotkey, key, modifiers)) return InterfaceAction.Library;
        if (Matches(SettingsHotkey, key, modifiers)) return InterfaceAction.Settings;
        if (Matches(TranslateHotkey, key, modifiers)) return InterfaceAction.Translate;
        if (Matches(SpeakHotkey, key, modifiers)) return InterfaceAction.Speak;
        return InterfaceAction.None;
    }

    internal static bool IsFixedAction(HotkeyBinding binding) =>
        (binding.Modifiers == HotkeyBinding.ModControl && binding.VirtualKey is 0x52 or 0x53) ||
        (binding.Modifiers == (HotkeyBinding.ModControl | HotkeyBinding.ModShift) && binding.VirtualKey == 0x43);

    public static bool IsReserved(HotkeyBinding binding)
    {
        var key = binding.VirtualKey;
        var modifiers = binding.Modifiers;
        if (IsFixedAction(binding)) return true;
        if (key == 0x1B) return true; // Escape belongs to transient surfaces and IME.
        if (modifiers == HotkeyBinding.ModControl)
            return key is 0x41 or 0x42 or 0x43 or 0x49 or 0x55 or 0x56 or 0x58 or 0x59 or 0x5A
                or 0x08 or 0x2E or 0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23 or 0x2D or 0x09 or 0x20;
        if (modifiers == (HotkeyBinding.ModControl | HotkeyBinding.ModShift))
            return key is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A or 0x08 or 0x2E
                or 0x25 or 0x26 or 0x27 or 0x28 or 0x24 or 0x23 or 0x09 or 0x20;
        if (modifiers == (HotkeyBinding.ModAlt | HotkeyBinding.ModShift) && key == 0x08) return true;
        return modifiers == HotkeyBinding.ModAlt && key is 0x08 or 0x73 or 0x20; // Undo / Alt+F4 / system menu.
    }

    private static bool Matches(HotkeyBinding binding, Key key, ModifierKeys modifiers)
    {
        uint actual = 0;
        if ((modifiers & ModifierKeys.Control) != 0) actual |= HotkeyBinding.ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) actual |= HotkeyBinding.ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) actual |= HotkeyBinding.ModShift;
        if ((modifiers & ModifierKeys.Windows) != 0) actual |= HotkeyBinding.ModWin;
        return actual == binding.Modifiers && KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey) == key;
    }

    internal static void RefreshHints(FrameworkElement scope)
    {
        void Hint(string name, string text, HotkeyBinding binding)
        {
            if (scope.FindName(name) is FrameworkElement element)
            {
                element.ToolTip = $"{text} ({binding.DisplayName})";
                System.Windows.Automation.AutomationProperties.SetAcceleratorKey(element, binding.DisplayName);
            }
        }
        Hint("NavTranslate", "工作台", WorkbenchHotkey);
        Hint("NavLibrary", "资料库", LibraryHotkey);
        Hint("NavSettingsButton", "设置", SettingsHotkey);
        Hint("SettingsButton", "设置", SettingsHotkey);
        Hint("RouteSelectorButton", "切换模型", ModelHotkey);
        Hint("StyleSelectorButton", "切换风格", StyleHotkey);
        Hint("SwapLangButton", "互换语言", SwapHotkey);
        Hint("TranslateSwapButton", "互换语言", SwapHotkey);
        Hint("LangBadgeButton", "互换语言", SwapHotkey);
        Hint("TranslateButton", "发起翻译", TranslateHotkey);
        var speechAction = TtsService.IsSpeaking ? "停止朗读" : "朗读译文";
        Hint("TranslateResultSpeakButton", speechAction, SpeakHotkey);
        Hint("ResultSpeakBtn", speechAction, SpeakHotkey);
        Hint("SpeakButton", speechAction, SpeakHotkey);
        foreach (var name in new[] { "TranslateInput", "SourceInputBox", "SearchBox" })
        {
            if (scope.FindName(name) is FrameworkElement editor)
                System.Windows.Automation.AutomationProperties.SetHelpText(editor,
                    $"Enter 翻译 · Shift+Enter 换行 · {TranslateHotkey.DisplayName} 翻译");
        }
    }
}
