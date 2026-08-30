using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PopGlot.Windows;

/// <summary>
/// Owns the colour tokens and window chrome styling for the application.
/// Formatted with high-contrast, premium Raycast/Linear-grade design tokens.
/// </summary>
internal static partial class ThemeService
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ThemePreference _preference = ThemePreference.System;
    private static bool _watchingSystem;

    /// <summary>Raised after the effective (resolved) theme changes.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>True when the resolved theme is the dark palette.</summary>
    public static bool IsDark { get; private set; } = true;

    public static void Apply(ThemePreference preference)
    {
        _preference = preference;
        ApplyResolved();
        EnsureSystemWatcher();
    }

    private static void ApplyResolved()
    {
        var dark = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => !SystemPrefersLight(),
        };

        IsDark = dark;
        var tokens = dark ? DarkTokens : LightTokens;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var (key, value) in tokens)
        {
            var color = ParseColor(value);
            if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
                continue;
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    /// <summary>Applies immersive dark mode & rounded corners to the window chrome.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            window.SourceInitialized += OnSourceInitialized;
            return;
        }
        ApplyImmersiveDarkMode(handle);
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window)
        {
            return;
        }
        window.SourceInitialized -= OnSourceInitialized;
        ApplyImmersiveDarkMode(new WindowInteropHelper(window).Handle);
    }

    private static void ApplyImmersiveDarkMode(nint handle)
    {
        if (handle == 0)
        {
            return;
        }
        var useDark = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE (20 on Win10 20H1+/Win11, 19 on older Win10)
        _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int));
        _ = NativeMethods.DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));

        // DWMWA_WINDOW_CORNER_PREFERENCE (33): 2 = DWMWCP_ROUND
        var cornerPref = 2;
        _ = NativeMethods.DwmSetWindowAttribute(handle, 33, ref cornerPref, sizeof(int));
    }

    private static void EnsureSystemWatcher()
    {
        if (_watchingSystem)
        {
            return;
        }
        _watchingSystem = true;
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)
                || _preference != ThemePreference.System)
            {
                return;
            }
            Application.Current?.Dispatcher.BeginInvoke(ApplyResolved);
        };
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            return Convert.ToInt32(
                Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return false;
        }
    }

    // Role semantics (see docs/UI-REFACTOR-PLAN.md §13):
    //   Canvas       window base background
    //   Sidebar      stable navigation rail
    //   Surface      primary content area
    //   SurfaceMuted secondary/read-only areas, list backgrounds
    //   SurfaceRaised popups, dropdown menus, floating overlays
    //   Input        editable controls
    // Accent (indigo) is the brand only — success/warning/danger are separate
    // hues, so "online/OK/default" never borrows the brand colour.
    //
    // Contrast budget (audited by tests/PopGlot.Windows.LogicTests via
    // ThemeContrast): TextTertiary ≥ 4.5:1 on every surface it renders on
    // (placeholders, captions), control edges (BorderStrong on inputs, the
    // toggle track outline) ≥ 3:1, status hues ≥ 4.5:1 on their soft chips.
    // Dimming is reserved for disabled states — never for plain "tertiary"
    // text, so low-emphasis copy never turns into grey mush.
    internal static readonly (string Key, string Value)[] DarkTokens =
    [
        ("CanvasBrush", "#0A0B0F"),
        ("SidebarBrush", "#0F1015"),
        ("SurfaceBrush", "#14161C"),
        ("SurfaceMutedBrush", "#111318"),
        ("SurfaceRaisedBrush", "#1B1E26"),
        ("SurfaceHoverBrush", "#21242E"),
        ("SurfacePressedBrush", "#333B49"),
        ("InputBrush", "#0E1014"),
        ("BorderSubtleBrush", "#2A303D"),
        ("BorderStrongBrush", "#626C82"),
        ("AccentBrush", "#8A8FFF"),
        ("AccentHoverBrush", "#A3A7FF"),
        ("AccentPressedBrush", "#7376EE"),
        ("AccentTextBrush", "#0B0C21"),
        ("AccentSoftBrush", "#262850"),
        ("AccentBorderBrush", "#6E74B8"),
        ("TextPrimaryBrush", "#EEF0F4"),
        ("TextSecondaryBrush", "#A3A9B4"),
        ("TextTertiaryBrush", "#8A93A2"),
        ("TextDisabledBrush", "#525A66"),
        ("DangerBrush", "#FF6B7D"),
        ("DangerSoftBrush", "#401C25"),
        ("WarningBrush", "#F2B95C"),
        ("WarningSoftBrush", "#3D2D14"),
        ("SuccessBrush", "#3DD68C"),
        ("SuccessSoftBrush", "#143826"),
        ("OverlayScrimBrush", "#C80A0B0F"),
    ];

    internal static readonly (string Key, string Value)[] LightTokens =
    [
        ("CanvasBrush", "#F6F7F9"),
        ("SidebarBrush", "#FCFCFD"),
        ("SurfaceBrush", "#FFFFFF"),
        ("SurfaceMutedBrush", "#F4F5F7"),
        ("SurfaceRaisedBrush", "#FFFFFF"),
        ("SurfaceHoverBrush", "#EDEFF3"),
        ("SurfacePressedBrush", "#D7DDE6"),
        ("InputBrush", "#FFFFFF"),
        ("BorderSubtleBrush", "#D5DAE1"),
        ("BorderStrongBrush", "#8590A0"),
        ("AccentBrush", "#5457E5"),
        ("AccentHoverBrush", "#4548D6"),
        ("AccentPressedBrush", "#3A3CC4"),
        ("AccentTextBrush", "#FFFFFF"),
        ("AccentSoftBrush", "#EDEDFE"),
        ("AccentBorderBrush", "#7D82E8"),
        ("TextPrimaryBrush", "#15171C"),
        ("TextSecondaryBrush", "#4D545F"),
        ("TextTertiaryBrush", "#656F7C"),
        ("TextDisabledBrush", "#A6ACB7"),
        ("DangerBrush", "#C93148"),
        ("DangerSoftBrush", "#FCEBEE"),
        ("WarningBrush", "#9C5B00"),
        ("WarningSoftBrush", "#FFF3DB"),
        ("SuccessBrush", "#0B7350"),
        ("SuccessSoftBrush", "#E3F6EF"),
        ("OverlayScrimBrush", "#A615171C"),
    ];

    private static partial class NativeMethods
    {
        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmSetWindowAttribute(
            nint window,
            int attribute,
            ref int value,
            int size);
    }
}
