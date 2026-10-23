using System.Windows.Input;

namespace PopGlot.Windows.Services;

/// <summary>
/// 0.1.27：界面快捷键（工作台 / 翻译浮窗 / 极速查词共用的应用内组合键）
/// 的统一判定入口。绑定保存在 <see cref="ShellSettings"/>（默认 Ctrl+M /
/// Ctrl+T / Ctrl+L），与全局热键同一 <see cref="HotkeyBinding"/> 形态；
/// 用户组合与其他软件冲突时可以自行改绑。
///
/// 判定规则：修饰键必须与绑定完全一致（Ctrl+Shift+M 不会误命中 Ctrl+M）。
/// 读取设置失败时回落默认组合，绝不抛出——快捷键路径不允许炸窗口。
/// </summary>
internal static class InterfaceKeys
{
    public static HotkeyBinding ModelHotkey => LoadOrNull()?.InterfaceModelHotkey
        ?? HotkeyBinding.InterfaceModelDefault;

    public static HotkeyBinding StyleHotkey => LoadOrNull()?.InterfaceStyleHotkey
        ?? HotkeyBinding.InterfaceStyleDefault;

    public static HotkeyBinding SwapHotkey => LoadOrNull()?.InterfaceSwapHotkey
        ?? HotkeyBinding.InterfaceSwapDefault;

    /// <summary>切换当前模型（默认 Ctrl+M）。</summary>
    public static bool IsSwitchModel(Key key, ModifierKeys modifiers) =>
        Matches(ModelHotkey, key, modifiers);

    /// <summary>切换翻译风格（默认 Ctrl+T）。</summary>
    public static bool IsSwitchStyle(Key key, ModifierKeys modifiers) =>
        Matches(StyleHotkey, key, modifiers);

    /// <summary>互换源/目标语言（默认 Ctrl+L）。</summary>
    public static bool IsSwapLanguages(Key key, ModifierKeys modifiers) =>
        Matches(SwapHotkey, key, modifiers);

    private static ShellSettings? LoadOrNull()
    {
        try
        {
            return ShellSettingsStore.Load();
        }
        catch
        {
            return null;
        }
    }

    private static bool Matches(HotkeyBinding binding, Key key, ModifierKeys modifiers)
    {
        var expected = ModifierKeys.None;
        if ((binding.Modifiers & HotkeyBinding.ModControl) != 0)
        {
            expected |= ModifierKeys.Control;
        }
        if ((binding.Modifiers & HotkeyBinding.ModAlt) != 0)
        {
            expected |= ModifierKeys.Alt;
        }
        if ((binding.Modifiers & HotkeyBinding.ModShift) != 0)
        {
            expected |= ModifierKeys.Shift;
        }
        if ((binding.Modifiers & HotkeyBinding.ModWin) != 0)
        {
            expected |= ModifierKeys.Windows;
        }
        if (modifiers != expected)
        {
            return false;
        }
        return KeyInterop.KeyFromVirtualKey((int)binding.VirtualKey) == key;
    }
}
