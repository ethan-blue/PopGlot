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

    /// <summary>
    /// Testable seam: when non-null it replaces <see cref="SystemParameters.HighContrast"/>
    /// as the HC source so tests can drive ApplyResolved through the HC on/off
    /// cycle without flipping a real system setting. Null in production.
    /// </summary>
    internal static bool? HighContrastTestOverride;

    /// <summary>Raised after the effective (resolved) theme changes.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>True when the resolved theme is the dark palette.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>True when Windows high contrast mode is currently active.</summary>
    public static bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>HC source of truth with the test seam applied.</summary>
    private static bool IsHighContrastEffective => HighContrastTestOverride ?? SystemParameters.HighContrast;

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

    internal static void ApplyResolved()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyResolved));
            return;
        }
        var isHighContrast = IsHighContrastEffective;
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

    /// <summary>
    /// Tokens deliberately NOT remapped by the high-contrast palette. Every
    /// one of them is an explicit, audited decision — never a silent miss:
    /// <list type="bullet">
    /// <item><c>OverlayScrimBrush</c>: the capture overlay must dim the desktop
    /// behind the selection rectangle in every theme; collapsing it to a solid
    /// system colour would either blind the screen or remove the dimming.</item>
    /// <item><c>ShadowColor</c> / <c>ShadowBrush</c>: shadows are a depth cue
    /// with no meaning in high contrast — they are forced transparent instead
    /// of inheriting any theme colour.</item>
    /// </list>
    /// The LogicTests audit asserts every token of Dark/Light/seed is either in
    /// the HC palette or in this list, so a future token cannot leak past HC.
    /// </summary>
    internal static readonly string[] HighContrastExplicitWhitelist =
    [
        "OverlayScrimBrush",
        "ShadowColor",
        "ShadowBrush",
    ];

    /// <summary>
    /// The COMPLETE high-contrast palette as pure data: every token that must
    /// follow system colours under HC, derived only from the role colours
    /// passed in. Pure and parameterised so tests can exercise it without
    /// switching a real system high-contrast setting; production values come
    /// from <see cref="System.Windows.SystemColors"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, Color> HighContrastPalette(
        Color window,
        Color windowText,
        Color highlight,
        Color highlightText,
        Color grayText,
        Color hotTrack)
    {
        var palette = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            // Boundaries: a crisp window edge is THE HC affordance for floating
            // windows that would otherwise blend into a matching system theme.
            ["WindowEdgeBrush"] = windowText,

            // Surfaces collapse to the system window colour.
            ["CanvasBrush"] = window,
            ["SidebarBrush"] = window,
            ["SurfaceBrush"] = window,
            ["SurfaceMutedBrush"] = window,
            ["SurfaceRaisedBrush"] = window,
            ["InputBrush"] = window,
            ["ResultSurfaceBrush"] = window,

            // Text follows the system text colour; dimmed text uses the
            // system disabled colour.
            ["TextPrimaryBrush"] = windowText,
            ["TextSecondaryBrush"] = windowText,
            ["TextTertiaryBrush"] = grayText,
            ["TextDisabledBrush"] = grayText,

            // Accent/primary/selection states ride the system highlight pair.
            ["AccentBrush"] = highlight,
            ["AccentHoverBrush"] = highlight,
            ["AccentPressedBrush"] = highlight,
            ["AccentTextBrush"] = highlightText,
            ["AccentSoftBrush"] = window,
            ["AccentBorderBrush"] = highlight,
            ["FocusBrush"] = highlight,
            ["PrimaryBrush"] = highlight,
            ["PrimaryHoverBrush"] = highlight,
            ["PrimaryPressedBrush"] = highlight,
            ["PrimaryTextBrush"] = highlightText,

            ["SurfaceHoverBrush"] = window,
            ["SurfacePressedBrush"] = highlight,

            // Semantic status colours (C18 / A10): alert hues ride highlight /
            // hot track, soft fills collapse to the window colour.
            ["DangerBrush"] = hotTrack,
            ["DangerSoftBrush"] = window,
            ["DangerHoverBrush"] = highlight,
            ["DangerHoverTextBrush"] = highlightText,
            ["DangerPressedBrush"] = highlight,
            ["DangerPressedTextBrush"] = highlightText,
            ["WarningBrush"] = hotTrack,
            ["WarningSoftBrush"] = window,
            ["SuccessBrush"] = highlight,
            ["SuccessSoftBrush"] = window,

            ["BorderSubtleBrush"] = windowText,
            ["BorderStrongBrush"] = windowText,
        };
        return palette;
    }

    internal static void ApplyHighContrastOverrides(ResourceDictionary resources)
    {
        var palette = HighContrastPalette(
            System.Windows.SystemColors.WindowColor,
            System.Windows.SystemColors.WindowTextColor,
            System.Windows.SystemColors.HighlightColor,
            System.Windows.SystemColors.HighlightTextColor,
            System.Windows.SystemColors.GrayTextColor,
            System.Windows.SystemColors.HotTrackColor);

        foreach (var (key, color) in palette)
        {
            SetTokenBrush(resources, key, color);
        }

        // Explicit whitelist handling (see HighContrastExplicitWhitelist):
        // effects never inherit theme colours under HC.
        resources["ShadowColor"] = Colors.Transparent;
        resources["ShadowBrush"] = Brushes.Transparent;
        // OverlayScrimBrush stays at its translucent palette value on purpose.
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
        // 必须 ≥3:1 于 AccentSoft 软底与 Input 输入底（WCAG 非文本对比）。
        // 旧值 #59649D 在 AccentSoft #20243A 上仅 2.72:1。
        ("AccentBorderBrush", "#6B77B5"),
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
        // Window edge boundary: transparent in the normal themes (the windows
        // carry their own subtle borders), remapped to WindowText under system
        // high contrast so floating windows get a crisp visible boundary.
        ("WindowEdgeBrush", "#00FFFFFF"),
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
        // Window edge boundary: transparent in the normal themes (the windows
        // carry their own subtle borders), remapped to WindowText under system
        // high contrast so floating windows get a crisp visible boundary.
        ("WindowEdgeBrush", "#00FFFFFF"),
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
