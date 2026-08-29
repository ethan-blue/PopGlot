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
    private static readonly (string Key, string Value)[] DarkTokens =
    [
        ("CanvasBrush", "#0E0F12"),
        ("SidebarBrush", "#121419"),
        ("SurfaceBrush", "#191C22"),
        ("SurfaceMutedBrush", "#15181E"),
        ("SurfaceRaisedBrush", "#20242C"),
        ("SurfaceHoverBrush", "#222730"),
        ("SurfacePressedBrush", "#2A303B"),
        ("InputBrush", "#111318"),
        ("BorderSubtleBrush", "#2A2F38"),
        ("BorderStrongBrush", "#3B424F"),
        ("AccentBrush", "#8B8FF7"),
        ("AccentHoverBrush", "#A0A3FF"),
        ("AccentPressedBrush", "#777BE3"),
        ("AccentTextBrush", "#101124"),
        ("AccentSoftBrush", "#27294C"),
        ("AccentBorderBrush", "#45497B"),
        ("TextPrimaryBrush", "#F2F3F5"),
        ("TextSecondaryBrush", "#A8ADB7"),
        ("TextTertiaryBrush", "#747B87"),
        ("TextDisabledBrush", "#555C67"),
        ("DangerBrush", "#FF7180"),
        ("DangerSoftBrush", "#421D24"),
        ("WarningBrush", "#F0B35A"),
        ("WarningSoftBrush", "#3B2B15"),
        ("SuccessBrush", "#45C18A"),
        ("SuccessSoftBrush", "#15392B"),
        ("OverlayScrimBrush", "#C80E0F12"),
    ];

    private static readonly (string Key, string Value)[] LightTokens =
    [
        ("CanvasBrush", "#F2F3F5"),
        ("SidebarBrush", "#FAFAFB"),
        ("SurfaceBrush", "#FFFFFF"),
        ("SurfaceMutedBrush", "#F7F8FA"),
        ("SurfaceRaisedBrush", "#FFFFFF"),
        ("SurfaceHoverBrush", "#EEF0F4"),
        ("SurfacePressedBrush", "#E2E6EB"),
        ("InputBrush", "#FFFFFF"),
        ("BorderSubtleBrush", "#E1E4E9"),
        ("BorderStrongBrush", "#C5CAD3"),
        ("AccentBrush", "#5B5BD6"),
        ("AccentHoverBrush", "#4D4DC3"),
        ("AccentPressedBrush", "#4242AC"),
        ("AccentTextBrush", "#FFFFFF"),
        ("AccentSoftBrush", "#EEEEFF"),
        ("AccentBorderBrush", "#C9C9F8"),
        ("TextPrimaryBrush", "#17181C"),
        ("TextSecondaryBrush", "#555B66"),
        ("TextTertiaryBrush", "#858C98"),
        ("TextDisabledBrush", "#A8ADB6"),
        ("DangerBrush", "#C93F4F"),
        ("DangerSoftBrush", "#FDECEF"),
        ("WarningBrush", "#B86A00"),
        ("WarningSoftBrush", "#FFF4DE"),
        ("SuccessBrush", "#16875D"),
        ("SuccessSoftBrush", "#EAF7F1"),
        ("OverlayScrimBrush", "#A617181C"),
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
