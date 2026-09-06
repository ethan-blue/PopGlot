using System.IO;

namespace PopGlot.Windows;

/// <summary>
/// Single source for every default storage location the shell uses. The test
/// host installs <see cref="RootOverride"/> once at startup so the whole
/// process — stores, profile config, native core and crash logs — operates
/// inside an isolated directory; production leaves it null and resolves
/// exactly the paths earlier builds used, so user data and upgrades are
/// unaffected.
/// </summary>
internal static class StoragePaths
{
    /// <summary>
    /// Test-only redirect for the entire storage root. Never set by the
    /// production app; when null every path below resolves under
    /// %LOCALAPPDATA%\PopGlot as before.
    /// </summary>
    internal static string? RootOverride { get; set; }

    private static string BaseDir =>
        RootOverride ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot");

    /// <summary>Directory handed to the native core for its settings file.</summary>
    public static string CoreConfigDirectory => BaseDir;

    public static string ShellSettings => Path.Combine(BaseDir, "windows-shell.json");

    public static string ProductConfig => Path.Combine(BaseDir, "product-config.json");

    public static string History => Path.Combine(BaseDir, "history.json");

    public static string Vocabulary => Path.Combine(BaseDir, "vocabulary.json");

    public static string Logs => Path.Combine(BaseDir, "logs");
}
