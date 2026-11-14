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

    /// <summary>
    /// T9.22：翻译键文案统一从录制绑定生成；Ctrl+Enter 仅作未录制时的
    /// 固定兜底显示（功能上 Ctrl+Enter 始终可用）。录制值即 Ctrl+Enter 时
    /// 显示一致。
    /// </summary>
    internal static string TranslateKeyName =>
        TranslateHotkey.IsValid ? TranslateHotkey.DisplayName : "Ctrl+Enter";

    /// <summary>「{键} 翻译 · Enter 换行」——输入框帮助文本/工具栏提示。</summary>
    internal static string TranslateHintText => $"{TranslateKeyName} 翻译 · Enter 换行";

    /// <summary>浮窗原文占位符：「输入文本，{键} 翻译，Enter 换行」。</summary>
    internal static string TranslateSourcePlaceholder => $"输入文本，{TranslateKeyName} 翻译，Enter 换行";

    /// <summary>紧凑工具栏提示：Enter 缩写为 ↵，其余服从录制键。</summary>
    internal static string TranslateHintCompactText =>
        $"{TranslateKeyName.Replace("Enter", "↵")} 翻译 · ↵ 换行";

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
        if (!binding.IsValid)
        {
            // Unset bindings never dispatch; the action stays on its UI entry.
            return false;
        }
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
                // An unset binding names no key: the plain label stays honest
                // instead of advertising "未设置" as a combination.
                if (binding.IsValid)
                {
                    element.ToolTip = $"{text} ({binding.DisplayName})";
                    System.Windows.Automation.AutomationProperties.SetAcceleratorKey(element, binding.DisplayName);
                }
                else
                {
                    element.ToolTip = text;
                }
            }
        }
        Hint("NavTranslate", "工作台", WorkbenchHotkey);
        Hint("NavLibrary", "资料库", LibraryHotkey);
        Hint("NavSettingsButton", "设置", SettingsHotkey);
        Hint("SettingsButton", "设置", SettingsHotkey);
        // Route/style selectors own richer tooltips (route detail, current
        // style) that already embed the hotkey; only the accelerator key is
        // set here so two writers never fight over one ToolTip.
        HintAcceleratorOnly(scope, "RouteSelectorButton", ModelHotkey);
        HintAcceleratorOnly(scope, "StyleSelectorButton", StyleHotkey);
        Hint("SwapLangButton", "互换语言", SwapHotkey);
        Hint("TranslateSwapButton", "互换语言", SwapHotkey);
        Hint("LangBadgeButton", "互换语言", SwapHotkey);
        Hint("TranslateButton", "发起翻译", TranslateHotkey);
        var speechAction = TtsService.IsSpeaking ? "停止朗读" : "朗读译文";
        Hint("TranslateResultSpeakButton", speechAction, SpeakHotkey);
        Hint("ResultSpeakBtn", speechAction, SpeakHotkey);
        Hint("SpeakButton", speechAction, SpeakHotkey);
        foreach (var name in new[] { "TranslateInput", "SourceInputBox" })
        {
            if (scope.FindName(name) is FrameworkElement editor)
                System.Windows.Automation.AutomationProperties.SetHelpText(editor, TranslateHintText);
        }
        // T9.22：占位符与工具栏提示同样服从录制键，改绑后即时刷新。
        if (scope.FindName("SourceInputBox") is System.Windows.Controls.TextBox panelSource)
            panelSource.SetValue(Ui.PlaceholderProperty, TranslateSourcePlaceholder);
        if (scope.FindName("TranslateShortcutHint") is System.Windows.Controls.TextBlock toolbarHint)
        {
            toolbarHint.Text = TranslateHintText;
            if (toolbarHint.ToolTip is not null) toolbarHint.ToolTip = TranslateHintText;
        }
        if (scope.FindName("SearchBox") is FrameworkElement searchBox)
            System.Windows.Automation.AutomationProperties.SetHelpText(searchBox, "Enter 翻译");
    }

    private static void HintAcceleratorOnly(FrameworkElement scope, string name, HotkeyBinding binding)
    {
        if (binding.IsValid && scope.FindName(name) is FrameworkElement element)
        {
            System.Windows.Automation.AutomationProperties.SetAcceleratorKey(element, binding.DisplayName);
        }
    }
}
