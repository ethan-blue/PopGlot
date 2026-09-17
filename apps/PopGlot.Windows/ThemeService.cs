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
    private static UserPreferenceChangedEventHandler? _systemPreferenceHandler;

    /// <summary>Raised after the effective (resolved) theme changes.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>True when the resolved theme is the dark palette.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>True when Windows high contrast mode is currently active.</summary>
    public static bool IsHighContrast => SystemParameters.HighContrast;

    public static void Apply(ThemePreference preference)
    {
        _preference = preference;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Apply(preference)));
            return;
        }
        ApplyResolved();
        EnsureSystemWatcher();
    }

    private static void ApplyResolved()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyResolved));
            return;
        }
        var isHighContrast = SystemParameters.HighContrast;
        var dark = _preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.Dark => true,
            _ => !SystemPrefersLight(),
        };

        IsDark = isHighContrast ? !SystemPrefersLight() : dark;
        var tokens = dark ? DarkTokens : LightTokens;
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var (key, value) in tokens)
        {
            var color = ParseColor(value);
            if (key == "ShadowColor")
            {
                resources[key] = isHighContrast ? Colors.Transparent : color;
                continue;
            }
            if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
                continue;
            }
            // Theme token brushes remain mutable so controls that retained a
            // FindResource reference observe future theme changes in place.
            resources[key] = new SolidColorBrush(color);
        }

        if (isHighContrast)
        {
            ApplyHighContrastOverrides(resources);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static void ApplyHighContrastOverrides(ResourceDictionary resources)
    {
        var windowColor = System.Windows.SystemColors.WindowColor;
        var windowTextColor = System.Windows.SystemColors.WindowTextColor;
        var highlightColor = System.Windows.SystemColors.HighlightColor;
        var highlightTextColor = System.Windows.SystemColors.HighlightTextColor;
        var grayTextColor = System.Windows.SystemColors.GrayTextColor;
        var hotTrackColor = System.Windows.SystemColors.HotTrackColor;

        SetTokenBrush(resources, "CanvasBrush", windowColor);
        SetTokenBrush(resources, "SidebarBrush", windowColor);
        SetTokenBrush(resources, "SurfaceBrush", windowColor);
        SetTokenBrush(resources, "SurfaceMutedBrush", windowColor);
        SetTokenBrush(resources, "SurfaceRaisedBrush", windowColor);
        SetTokenBrush(resources, "InputBrush", windowColor);
        SetTokenBrush(resources, "ResultSurfaceBrush", windowColor);

        SetTokenBrush(resources, "TextPrimaryBrush", windowTextColor);
        SetTokenBrush(resources, "TextSecondaryBrush", windowTextColor);
        SetTokenBrush(resources, "TextTertiaryBrush", grayTextColor);
        SetTokenBrush(resources, "TextDisabledBrush", grayTextColor);

        SetTokenBrush(resources, "AccentBrush", highlightColor);
        SetTokenBrush(resources, "AccentTextBrush", highlightTextColor);
        SetTokenBrush(resources, "AccentBorderBrush", highlightColor);
        SetTokenBrush(resources, "AccentSoftBrush", windowColor);
        SetTokenBrush(resources, "FocusBrush", highlightColor);
        SetTokenBrush(resources, "PrimaryBrush", highlightColor);
        SetTokenBrush(resources, "PrimaryHoverBrush", highlightColor);
        SetTokenBrush(resources, "PrimaryPressedBrush", highlightColor);
        SetTokenBrush(resources, "PrimaryTextBrush", highlightTextColor);

        SetTokenBrush(resources, "SurfaceHoverBrush", windowColor);
        SetTokenBrush(resources, "SurfacePressedBrush", highlightColor);

        // Semantic status colors in high contrast (C18 / A10):
        // Danger/Warning/Success mapped to system alert & highlight hues,
        // while all soft background fills collapse to window background to prevent low-contrast halos.
        SetTokenBrush(resources, "DangerBrush", hotTrackColor);
        SetTokenBrush(resources, "DangerSoftBrush", windowColor);
        SetTokenBrush(resources, "DangerHoverBrush", highlightColor);
        SetTokenBrush(resources, "DangerHoverTextBrush", highlightTextColor);
        SetTokenBrush(resources, "DangerPressedBrush", highlightColor);
        SetTokenBrush(resources, "DangerPressedTextBrush", highlightTextColor);

        SetTokenBrush(resources, "WarningBrush", hotTrackColor);
        SetTokenBrush(resources, "WarningSoftBrush", windowColor);

        SetTokenBrush(resources, "SuccessBrush", highlightColor);
        SetTokenBrush(resources, "SuccessSoftBrush", windowColor);

        SetTokenBrush(resources, "BorderSubtleBrush", windowTextColor);
        SetTokenBrush(resources, "BorderStrongBrush", windowTextColor);

        resources["ShadowColor"] = Colors.Transparent;
    }

    private static void SetTokenBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
        {
            existing.Color = color;
            return;
        }
        resources[key] = new SolidColorBrush(color);
    }

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value)!;

    /// <summary>Applies immersive dark mode & rounded corners to the window chrome.</summary>
    public static void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ApplyWindowChrome(window)));
            return;
        }
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

        // A one-pixel glass extension keeps the native DWM elevation/shadow
        // for opaque borderless windows without sacrificing ClearType.
        var margins = new Margins(1, 1, 1, 1);
        _ = NativeMethods.DwmExtendFrameIntoClientArea(handle, ref margins);
    }

    private static void EnsureSystemWatcher()
    {
        if (_watchingSystem)
        {
            return;
        }
        _watchingSystem = true;
        _systemPreferenceHandler = (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
                && _preference != ThemePreference.System && !SystemParameters.HighContrast)
            {
                return;
            }
            Application.Current?.Dispatcher.BeginInvoke(ApplyResolved);
        };
        SystemEvents.UserPreferenceChanged += _systemPreferenceHandler;
    }

    internal static void UnregisterSystemWatcher()
    {
        if (_watchingSystem && _systemPreferenceHandler is not null)
        {
            SystemEvents.UserPreferenceChanged -= _systemPreferenceHandler;
            _watchingSystem = false;
            _systemPreferenceHandler = null;
        }
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
    //   Canvas        window base background
    //   Sidebar       stable navigation rail
    //   Surface       primary content area
    //   SurfaceMuted  secondary/read-only areas, list backgrounds
    //   SurfaceRaised popups, dropdown menus, floating overlays
    //   Input         editable controls
    //   ResultSurface translation result reading canvas
    // Accent (blue-purple / 蓝紫) is the brand only — success/warning/danger are separate
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
        ("CanvasBrush", "#101216"),
        ("SidebarBrush", "#14171E"),
        ("SurfaceBrush", "#181B22"),
        ("SurfaceMutedBrush", "#14161D"),
        ("SurfaceRaisedBrush", "#20242E"),
        ("SurfaceHoverBrush", "#272C38"),
        ("SurfacePressedBrush", "#353C4D"),
        ("InputBrush", "#181B22"),
        ("ResultSurfaceBrush", "#181B22"),
        ("BorderSubtleBrush", "#2D3342"),
        ("BorderStrongBrush", "#6B768D"),
        ("AccentBrush", "#7C89D9"),
        ("AccentHoverBrush", "#8F9BE3"),
        ("AccentPressedBrush", "#6976C4"),
        ("AccentTextBrush", "#071224"),
        ("AccentSoftBrush", "#20243A"),
        ("AccentBorderBrush", "#59649D"),
        // Focus ring: strong enough to clear 3:1 against every resting
        // surface, unlike the soft AccentBorder it replaces.
        ("FocusBrush", "#7C89D9"),
        // 主按钮用品牌蓝系（深一档，配白字）：既保留品牌色又保证按钮
        // 文字 AA 级对比；浅色 Accent 只用于强调/链接/选中。
        ("PrimaryBrush", "#5562B3"),
        ("PrimaryHoverBrush", "#5B69BE"),
        ("PrimaryPressedBrush", "#4B579F"),
        ("PrimaryTextBrush", "#F7F8FC"),
        ("TextPrimaryBrush", "#EEF0F4"),
        ("TextSecondaryBrush", "#A8B0BD"),
        ("TextTertiaryBrush", "#939BAA"),
        ("TextDisabledBrush", "#565F6E"),
        ("DangerBrush", "#FF6B7D"),
        ("DangerSoftBrush", "#401C25"),
        // Danger hover/pressed keep AA text contrast in the dark theme: the
        // bright red fill would drop white text to ~2.6:1, so the dark theme
        // deepens the soft fill and keeps the red text instead.
        ("DangerHoverBrush", "#52222E"),
        ("DangerHoverTextBrush", "#FF6B7D"),
        ("DangerPressedBrush", "#4E1E28"),
        ("DangerPressedTextBrush", "#FF6B7D"),
        ("WarningBrush", "#F2B95C"),
        ("WarningSoftBrush", "#3D2D14"),
        ("SuccessBrush", "#3DD68C"),
        ("SuccessSoftBrush", "#143826"),
        ("OverlayScrimBrush", "#C8101216"),
        ("ShadowColor", "#000000"),
    ];

    internal static readonly (string Key, string Value)[] LightTokens =
    [
        ("CanvasBrush", "#F6F7F9"),
        ("SidebarBrush", "#FAFAFC"),
        ("SurfaceBrush", "#FFFFFF"),
        ("SurfaceMutedBrush", "#F8F9FA"),
        ("SurfaceRaisedBrush", "#FFFFFF"),
        ("SurfaceHoverBrush", "#EDEFF3"),
        ("SurfacePressedBrush", "#D7DDE6"),
        ("InputBrush", "#FFFFFF"),
        ("ResultSurfaceBrush", "#FFFFFF"),
        ("BorderSubtleBrush", "#E2E5E9"),
        ("BorderStrongBrush", "#8590A0"),
        ("AccentBrush", "#5563B8"),
        ("AccentHoverBrush", "#4855A4"),
        ("AccentPressedBrush", "#3D478E"),
        ("AccentTextBrush", "#FFFFFF"),
        ("AccentSoftBrush", "#EEF0FA"),
        // Input-class hover border: must clear WCAG non-text 3:1 against the
        // white InputBackground. The old #AAB1D9 measured 2.10:1; #737ECB
        // measures 3.77:1 on #FFFFFF (and 3.31:1 on AccentSoft #EEF0FA)
        // while staying lighter than AccentBrush #5563B8, so the focus ring
        // still outranks the hover hint.
        ("AccentBorderBrush", "#737ECB"),
        ("FocusBrush", "#5260B5"),
        ("PrimaryBrush", "#5260B5"),
        ("PrimaryHoverBrush", "#4652A0"),
        ("PrimaryPressedBrush", "#3B4589"),
        ("PrimaryTextBrush", "#FFFFFF"),
        ("TextPrimaryBrush", "#15171C"),
        ("TextSecondaryBrush", "#4D545F"),
        ("TextTertiaryBrush", "#656F7C"),
        ("TextDisabledBrush", "#A6ACB7"),
        ("DangerBrush", "#C93148"),
        ("DangerSoftBrush", "#FCEBEE"),
        ("DangerHoverBrush", "#C93148"),
        ("DangerHoverTextBrush", "#FFFFFF"),
        ("DangerPressedBrush", "#B02A3E"),
        ("DangerPressedTextBrush", "#FFFFFF"),
        ("WarningBrush", "#9C5B00"),
        ("WarningSoftBrush", "#FFF3DB"),
        ("SuccessBrush", "#0B7350"),
        ("SuccessSoftBrush", "#E3F6EF"),
        ("OverlayScrimBrush", "#A615171C"),
        ("ShadowColor", "#000000"),
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int left, int right, int top, int bottom)
    {
        public int Left = left;
        public int Right = right;
        public int Top = top;
        public int Bottom = bottom;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmExtendFrameIntoClientArea(
            nint window,
            ref Margins margins);

        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmSetWindowAttribute(
            nint window,
            int attribute,
            ref int value,
            int size);
    }
}
