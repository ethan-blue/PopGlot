using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal enum HotkeyAction
{
    TranslateSelection,
    CaptureScreen,
    ClosePanel,
    ShowWindow,
}

internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// One global hotkey, stored as a portable "Ctrl+Alt+W" string.
/// </summary>
/// <remarks>
/// The previous build offered a fixed list of six combinations, so a user whose
/// combination was taken by another app had no way out. Any modifier + key
/// combination is accepted now, validated the same way Windows validates it.
/// </remarks>
internal sealed record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static HotkeyBinding SelectionDefault => new(ModControl | ModAlt, 0x57); // Ctrl+Alt+W
    public static HotkeyBinding ScreenshotDefault => new(ModControl | ModAlt, 0x20); // Ctrl+Alt+Space
    public static HotkeyBinding CloseDefault => new(ModControl | ModAlt, 0x58); // Ctrl+Alt+X
    public static HotkeyBinding ShowWindowDefault => new(ModControl | ModAlt, 0x4F); // Ctrl+Alt+O

    public string DisplayName
    {
        get
        {
            // Windows writes its own shortcuts Win-first ("Win+Shift+S"), so
            // that order is what users expect to read back.
            var parts = new List<string>(4);
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    /// <summary>A combination Windows will actually hand back to us.</summary>
    /// <remarks>
    /// Bare keys and Shift-only combinations would swallow ordinary typing
    /// system-wide, so at least one of Ctrl/Alt/Win is required.
    /// </remarks>
    public bool IsValid =>
        (Modifiers & (ModControl | ModAlt | ModWin)) != 0 &&
        VirtualKey != 0 &&
        !IsModifierKey(VirtualKey);

    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or // Shift, Ctrl, Alt, LWin, RWin
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xBA => ";",
        0xDE => "'",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBD => "-",
        0xBB => "=",
        0xC0 => "`",
        _ => FriendlyKeyName(virtualKey),
    };

    private static string FriendlyKeyName(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        var name = key.ToString();
        // WPF spells the digit row `D0`..`D9`, which reads as nonsense in a
        // shortcut label.
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1..];
        }
        return name;
    }

    /// <summary>
    /// Parses any user-typed or persisted shortcut string. Accepts both
    /// the new readable syntax ("Ctrl+Alt+W") and the legacy preset ids
    /// ("ctrl-alt-w", "ctrl-shift-t", etc.).
    /// </summary>
    public static HotkeyBinding Parse(string? input, HotkeyBinding fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var text = input.Trim();
        var fromLegacy = ParseLegacyId(text);
        if (fromLegacy is not null)
        {
            return fromLegacy;
        }

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return fallback;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        foreach (var raw in tokens)
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows" or "super":
                    modifiers |= ModWin;
                    break;
                default:
                    virtualKey = ParseKeyName(raw);
                    break;
            }
        }

        var candidate = new HotkeyBinding(modifiers, virtualKey);
        return candidate.IsValid ? candidate : fallback;
    }

    private static uint ParseKeyName(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }
            return ch switch
            {
                ' ' => 0x20,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                ';' => 0xBA,
                '\'' => 0xDE,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                '-' => 0xBD,
                '=' => 0xBB,
                '`' => 0xC0,
                _ => 0,
            };
        }

        return upper switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            _ => Enum.TryParse<Key>(upper, ignoreCase: true, out var wpfKey)
                ? (uint)KeyInterop.VirtualKeyFromKey(wpfKey)
                : 0,
        };
    }

    private static HotkeyBinding? ParseLegacyId(string id) => id.ToLowerInvariant() switch
    {
        "ctrl-alt-w" => SelectionDefault,
        "ctrl-alt-space" => ScreenshotDefault,
        "ctrl-alt-x" => CloseDefault,
        "ctrl-shift-f" => new(ModControl | ModShift, 0x46),
        "ctrl-shift-t" => new(ModControl | ModShift, 0x54),
        "ctrl-shift-x" => new(ModControl | ModShift, 0x58),
        _ => null,
    };

    public string Serialize() => DisplayName;
}

internal sealed record ShellSettings(
    int SchemaVersion,
    HotkeyBinding SelectionHotkey,
    HotkeyBinding ScreenshotHotkey,
    HotkeyBinding CloseHotkey,
    bool HistoryEnabled,
    ThemePreference Theme,
    bool ClosePanelOnFocusLoss = true,
    bool CopyTranslationAutomatically = false,
    bool StartWithWindows = false,
    HotkeyBinding? ShowWindowHotkey = null,
    FreeEngineConsent FreeEngineConsent = FreeEngineConsent.Unset,
    bool CloseHintShown = false)
{
    public const int CurrentSchemaVersion = 3;

    public static ShellSettings Default => new(
        CurrentSchemaVersion,
        HotkeyBinding.SelectionDefault,
        HotkeyBinding.ScreenshotDefault,
        HotkeyBinding.CloseDefault,
        HistoryEnabled: true,
        ThemePreference.System,
        ClosePanelOnFocusLoss: true,
        CopyTranslationAutomatically: false,
        StartWithWindows: false,
        ShowWindowHotkey: HotkeyBinding.ShowWindowDefault,
        FreeEngineConsent: FreeEngineConsent.Unset,
        CloseHintShown: false);

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> Hotkeys
    {
        get
        {
            var dict = new Dictionary<HotkeyAction, HotkeyBinding>
            {
                [HotkeyAction.TranslateSelection] = SelectionHotkey,
                [HotkeyAction.CaptureScreen] = ScreenshotHotkey,
                [HotkeyAction.ClosePanel] = CloseHotkey,
            };
            if (ShowWindowHotkey is not null && ShowWindowHotkey.IsValid)
            {
                dict[HotkeyAction.ShowWindow] = ShowWindowHotkey;
            }
            return dict;
        }
    }

    /// <summary>Returns the first problem with this set, or null when usable.</summary>
    public string? ValidateHotkeys()
    {
        foreach (var (action, binding) in Hotkeys)
        {
            if (!binding.IsValid)
            {
                return $"{ActionName(action)}的快捷键“{binding.DisplayName}”无效：至少需要包含 Ctrl、Alt 或 Win，并搭配一个普通按键。";
            }
        }

        var duplicate = Hotkeys
            .GroupBy(pair => pair.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null
            ? null
            : $"快捷键 {duplicate.Key} 被重复用于{string.Join("、", duplicate.Select(pair => ActionName(pair.Key)))}。";
    }

    internal static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.TranslateSelection => "划词翻译",
        HotkeyAction.CaptureScreen => "截图翻译",
        HotkeyAction.ClosePanel => "关闭浮窗",
        HotkeyAction.ShowWindow => "打开主窗口",
        _ => action.ToString(),
    };
}

internal static class ShellSettingsStore
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "windows-shell.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ShellSettings Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                return ShellSettings.Default;
            }

            var persisted = JsonSerializer.Deserialize<PersistedShellSettings>(
                File.ReadAllText(path), JsonOptions);
            if (persisted is null)
            {
                return ShellSettings.Default;
            }

            var defaults = ShellSettings.Default;
            return new ShellSettings(
                ShellSettings.CurrentSchemaVersion,
                // v1 had a single `ShortcutId`; v2 split it into three ids;
                // v3 stores readable combinations. All three parse here.
                HotkeyBinding.Parse(
                    persisted.SelectionHotkey ?? persisted.SelectionShortcutId,
                    defaults.SelectionHotkey),
                HotkeyBinding.Parse(
                    persisted.ScreenshotHotkey ?? persisted.ScreenshotShortcutId ?? persisted.ShortcutId,
                    defaults.ScreenshotHotkey),
                HotkeyBinding.Parse(
                    persisted.CloseHotkey ?? persisted.CloseShortcutId,
                    defaults.CloseHotkey),
                persisted.HistoryEnabled ?? (persisted.SchemaVersion is not null && persisted.SchemaVersion >= 3 ? defaults.HistoryEnabled : false),
                persisted.Theme ?? defaults.Theme,
                persisted.ClosePanelOnFocusLoss ?? defaults.ClosePanelOnFocusLoss,
                persisted.CopyTranslationAutomatically ?? defaults.CopyTranslationAutomatically,
                persisted.StartWithWindows ?? defaults.StartWithWindows,
                persisted.ShowWindowHotkey is not null
                    ? HotkeyBinding.Parse(persisted.ShowWindowHotkey, defaults.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault)
                    : defaults.ShowWindowHotkey,
                persisted.FreeEngineConsent is not null
                    ? Enum.TryParse<FreeEngineConsent>(persisted.FreeEngineConsent, ignoreCase: true, out var consent)
                        ? consent
                        : FreeEngineConsent.Unset
                    : FreeEngineConsent.Unset,
                persisted.CloseHintShown ?? false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return ShellSettings.Default;
        }
    }

    public static void Save(ShellSettings settings, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validationError = settings.ValidateHotkeys();
        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        var path = settingsPath ?? DefaultSettingsPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Unable to resolve the PopGlot settings directory.");
        Directory.CreateDirectory(directory);

        var persisted = new PersistedShellSettings(
            ShellSettings.CurrentSchemaVersion,
            ShortcutId: null,
            SelectionShortcutId: null,
            ScreenshotShortcutId: null,
            CloseShortcutId: null,
            settings.SelectionHotkey.Serialize(),
            settings.ScreenshotHotkey.Serialize(),
            settings.CloseHotkey.Serialize(),
            settings.HistoryEnabled,
            settings.Theme,
            settings.ClosePanelOnFocusLoss,
            settings.CopyTranslationAutomatically,
            settings.StartWithWindows,
            settings.ShowWindowHotkey?.Serialize(),
            settings.FreeEngineConsent.ToString(),
            settings.CloseHintShown);

        // Write through a temporary file so a crash mid-write cannot leave the
        // user without settings on the next launch.
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persisted, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record PersistedShellSettings(
        int? SchemaVersion,
        string? ShortcutId,
        string? SelectionShortcutId,
        string? ScreenshotShortcutId,
        string? CloseShortcutId,
        string? SelectionHotkey,
        string? ScreenshotHotkey,
        string? CloseHotkey,
        bool? HistoryEnabled,
        ThemePreference? Theme,
        bool? ClosePanelOnFocusLoss,
        bool? CopyTranslationAutomatically,
        bool? StartWithWindows,
        string? ShowWindowHotkey = null,
        string? FreeEngineConsent = null,
        bool? CloseHintShown = null);
}
