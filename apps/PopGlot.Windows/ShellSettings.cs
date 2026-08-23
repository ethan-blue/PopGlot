using System.IO;
using System.Text.Json;

namespace PopGlot.Windows;

internal enum HotkeyAction
{
    TranslateSelection,
    CaptureScreen,
    ClosePanel,
}

internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

internal sealed record ShortcutOption(string Id, string DisplayName, uint Modifiers, uint VirtualKey)
{
    public static readonly IReadOnlyList<ShortcutOption> Available =
    [
        new("ctrl-alt-w", "Ctrl+Alt+W", 0x0002 | 0x0001, 0x57),
        new("ctrl-alt-space", "Ctrl+Alt+Space", 0x0002 | 0x0001, 0x20),
        new("ctrl-alt-x", "Ctrl+Alt+X", 0x0002 | 0x0001, 0x58),
        new("ctrl-shift-t", "Ctrl+Shift+T", 0x0002 | 0x0004, 0x54),
        new("ctrl-alt-g", "Ctrl+Alt+G", 0x0002 | 0x0001, 0x47),
        new("ctrl-shift-q", "Ctrl+Shift+Q", 0x0002 | 0x0004, 0x51),
    ];

    public static ShortcutOption Find(string? id, string fallbackId) =>
        Available.FirstOrDefault(option => option.Id == id) ??
        Available.First(option => option.Id == fallbackId);
}

internal sealed record ShellSettings(
    int SchemaVersion,
    string SelectionShortcutId,
    string ScreenshotShortcutId,
    string CloseShortcutId,
    bool HistoryEnabled,
    ThemePreference Theme)
{
    public static ShellSettings Default => new(
        2,
        "ctrl-alt-w",
        "ctrl-alt-space",
        "ctrl-alt-x",
        HistoryEnabled: false,
        ThemePreference.System);

    public ShortcutOption SelectionShortcut =>
        ShortcutOption.Find(SelectionShortcutId, "ctrl-alt-w");

    public ShortcutOption ScreenshotShortcut =>
        ShortcutOption.Find(ScreenshotShortcutId, "ctrl-alt-space");

    public ShortcutOption CloseShortcut =>
        ShortcutOption.Find(CloseShortcutId, "ctrl-alt-x");

    public IReadOnlyDictionary<HotkeyAction, ShortcutOption> Hotkeys =>
        new Dictionary<HotkeyAction, ShortcutOption>
        {
            [HotkeyAction.TranslateSelection] = SelectionShortcut,
            [HotkeyAction.CaptureScreen] = ScreenshotShortcut,
            [HotkeyAction.ClosePanel] = CloseShortcut,
        };

    public string? ValidateHotkeys()
    {
        var duplicate = Hotkeys
            .GroupBy(pair => pair.Value.Id)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null
            ? null
            : $"快捷键 {duplicate.First().Value.DisplayName} 被重复使用。";
    }
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

            var json = File.ReadAllText(path);
            var persisted = JsonSerializer.Deserialize<PersistedShellSettings>(json, JsonOptions);
            if (persisted is null)
            {
                return ShellSettings.Default;
            }

            // v1 stored only the screenshot shortcut as `ShortcutId`.
            return new ShellSettings(
                2,
                persisted.SelectionShortcutId ?? ShellSettings.Default.SelectionShortcutId,
                persisted.ScreenshotShortcutId ?? persisted.ShortcutId ?? ShellSettings.Default.ScreenshotShortcutId,
                persisted.CloseShortcutId ?? ShellSettings.Default.CloseShortcutId,
                persisted.HistoryEnabled ?? false,
                persisted.Theme ?? ThemePreference.System);
        }
        catch (IOException)
        {
            return ShellSettings.Default;
        }
        catch (JsonException)
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
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private sealed record PersistedShellSettings(
        int? SchemaVersion,
        string? ShortcutId,
        string? SelectionShortcutId,
        string? ScreenshotShortcutId,
        string? CloseShortcutId,
        bool? HistoryEnabled,
        ThemePreference? Theme);
}
