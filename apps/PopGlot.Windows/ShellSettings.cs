using System.Text.Json;
using System.IO;

namespace PopGlot.Windows;

internal sealed record ShortcutOption(string Id, string DisplayName, uint Modifiers, uint VirtualKey)
{
    public static readonly IReadOnlyList<ShortcutOption> Available =
    [
        new("ctrl-alt-space", "Ctrl+Alt+Space", 0x0002 | 0x0001, 0x20),
        new("ctrl-shift-t", "Ctrl+Shift+T", 0x0002 | 0x0004, 0x54),
        new("ctrl-alt-g", "Ctrl+Alt+G", 0x0002 | 0x0001, 0x47),
    ];

    public static ShortcutOption Find(string? id) =>
        Available.FirstOrDefault(option => option.Id == id) ?? Available[0];
}

internal static class ShellSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "windows-shell.json");

    public static ShortcutOption LoadShortcut()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return ShortcutOption.Available[0];
            }
            var settings = JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(SettingsPath));
            return ShortcutOption.Find(settings?.ShortcutId);
        }
        catch (IOException)
        {
            return ShortcutOption.Available[0];
        }
        catch (JsonException)
        {
            return ShortcutOption.Available[0];
        }
    }

    public static void SaveShortcut(ShortcutOption shortcut)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Unable to resolve the PopGlot settings directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(
            new ShellSettings(shortcut.Id),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    private sealed record ShellSettings(string ShortcutId);
}
