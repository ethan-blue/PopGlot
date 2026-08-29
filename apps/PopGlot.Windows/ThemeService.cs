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

    private static readonly (string Key, string Value)[] DarkTokens =
    [
        ("CanvasBrush", "#0B0D11"),
        ("SidebarBrush", "#101318"),
        ("SurfaceBrush", "#15181F"),
        ("SurfaceAltBrush", "#1B202A"),
        ("SurfaceHoverBrush", "#232936"),
        ("SurfacePressedBrush", "#2C3444"),
        ("InputBrush", "#11141A"),
        ("BorderSubtleBrush", "#232834"),
        ("BorderStrongBrush", "#353D4E"),
        ("AccentBrush", "#10B981"),
        ("AccentHoverBrush", "#34D399"),
        ("AccentPressedBrush", "#059669"),
        ("AccentTextBrush", "#022C22"),
        ("AccentSoftBrush", "#064E3B"),
        ("AccentBorderBrush", "#047857"),
        ("TextPrimaryBrush", "#F8FAFC"),
        ("TextSecondaryBrush", "#94A3B8"),
        ("TextTertiaryBrush", "#64748B"),
        ("TextInverseBrush", "#0B0D11"),
        ("DangerBrush", "#F87171"),
        ("DangerSoftBrush", "#450A0A"),
        ("WarningBrush", "#FBBF24"),
        ("WarningSoftBrush", "#451A03"),
        ("SuccessBrush", "#10B981"),
        ("SuccessSoftBrush", "#064E3B"),
        ("OverlayScrimBrush", "#C8080A0E"),
    ];

    private static readonly (string Key, string Value)[] LightTokens =
    [
        ("CanvasBrush", "#F8FAFC"),
        ("SidebarBrush", "#FFFFFF"),
        ("SurfaceBrush", "#FFFFFF"),
        ("SurfaceAltBrush", "#F1F5F9"),
        ("SurfaceHoverBrush", "#E2E8F0"),
        ("SurfacePressedBrush", "#CBD5E1"),
        ("InputBrush", "#FFFFFF"),
        ("BorderSubtleBrush", "#E2E8F0"),
        ("BorderStrongBrush", "#CBD5E1"),
        ("AccentBrush", "#059669"),
        ("AccentHoverBrush", "#10B981"),
        ("AccentPressedBrush", "#047857"),
        ("AccentTextBrush", "#FFFFFF"),
        ("AccentSoftBrush", "#ECFDF5"),
        ("AccentBorderBrush", "#A7F3D0"),
        ("TextPrimaryBrush", "#0F172A"),
        ("TextSecondaryBrush", "#475569"),
        ("TextTertiaryBrush", "#94A3B8"),
        ("TextInverseBrush", "#FFFFFF"),
        ("DangerBrush", "#DC2626"),
        ("DangerSoftBrush", "#FEF2F2"),
        ("WarningBrush", "#D97706"),
        ("WarningSoftBrush", "#FFFBEB"),
        ("SuccessBrush", "#059669"),
        ("SuccessSoftBrush", "#ECFDF5"),
        ("OverlayScrimBrush", "#A60F172A"),
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
