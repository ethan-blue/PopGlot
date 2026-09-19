using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using PopGlot.Windows;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// Independent audit helper for ThemeContrast math and key token contrast thresholds.
/// Placed in a separate file so main test orchestrators can invoke it without Program.cs merge conflicts.
/// </summary>
public static class ThemeAuditHelper
{
    public static void RunAudits()
    {
        // 1. Math verification
        var blackLum = ThemeContrast.Luminance("#000000");
        var whiteLum = ThemeContrast.Luminance("#FFFFFF");
        if (Math.Abs(blackLum - 0.0) > 0.001)
        {
            throw new InvalidOperationException($"Black luminance expected 0, got {blackLum}");
        }
        if (Math.Abs(whiteLum - 1.0) > 0.001)
        {
            throw new InvalidOperationException($"White luminance expected 1, got {whiteLum}");
        }

        var maxRatio = ThemeContrast.Ratio("#FFFFFF", "#000000");
        if (Math.Abs(maxRatio - 21.0) > 0.01)
        {
            throw new InvalidOperationException($"Black/white ratio expected 21.0, got {maxRatio}");
        }

        var sameRatio = ThemeContrast.Ratio("#8A8FFF", "#8A8FFF");
        if (Math.Abs(sameRatio - 1.0) > 0.01)
        {
            throw new InvalidOperationException($"Same colour ratio expected 1.0, got {sameRatio}");
        }

        // 2. Token audits
        AuditPalette("Dark", ThemeService.DarkTokens);
        AuditPalette("Light", ThemeService.LightTokens);

        // 3. High-contrast seam audits (pure — no system HC switch needed).
        AuditHighContrastSeam();
    }

    /// <summary>
    /// E3-D3 follow-up: the high-contrast palette must be COMPLETE and
    /// testable without flipping a real system setting. Every token declared
    /// anywhere (Dark, Light, App.xaml seeds) must be either mapped by the HC
    /// palette or explicitly whitelisted; the window edge must be transparent
    /// in the normal themes and WindowText under HC; and a representative
    /// High Contrast Black scheme must clear reasonable WCAG thresholds.
    /// </summary>
    private static void AuditHighContrastSeam()
    {
        var window = ParseHex("#000000");
        var windowText = ParseHex("#FFFFFF");
        var highlight = ParseHex("#CCCCFF");
        var highlightText = ParseHex("#000000");
        var grayText = ParseHex("#7F7F7F");
        var hotTrack = ParseHex("#1F5CC5");
        var palette = ThemeService.HighContrastPalette(
            window, windowText, highlight, highlightText, grayText, hotTrack);

        // 1. Completeness: HC maps every token or the token is whitelisted.
        var whitelisted = ThemeService.HighContrastExplicitWhitelist;
        var seedKeys = AppXamlBrushKeys();
        foreach (var key in ThemeService.DarkTokens.Select(t => t.Key)
                     .Concat(ThemeService.LightTokens.Select(t => t.Key))
                     .Concat(seedKeys)
                     .Distinct())
        {
            True(palette.ContainsKey(key) || whitelisted.Contains(key),
                $"token {key} must be mapped in the high-contrast palette or explicitly whitelisted");
        }

        // 2. Window edge: transparent in normal themes, WindowText under HC.
        var edgeHc = palette["WindowEdgeBrush"];
        True(edgeHc == windowText,
            $"WindowEdgeBrush must map to WindowText in high contrast, got {edgeHc}");
        foreach (var (name, tokens) in new[] { ("Dark", ThemeService.DarkTokens), ("Light", ThemeService.LightTokens) })
        {
            var edge = tokens.Single(t => t.Key == "WindowEdgeBrush").Value;
            var color = (Color)ColorConverter.ConvertFromString(edge);
            True(color.A == 0,
                $"[{name}] WindowEdgeBrush must be fully transparent in normal themes, got {edge}");
        }

        // 3. Contrast on a representative High Contrast Black scheme.
        AssertHcRatio(palette, "TextPrimaryBrush", "CanvasBrush", 7.0);
        AssertHcRatio(palette, "TextPrimaryBrush", "SurfaceBrush", 7.0);
        AssertHcRatio(palette, "TextSecondaryBrush", "CanvasBrush", 7.0);
        AssertHcRatio(palette, "TextTertiaryBrush", "SurfaceBrush", 2.5);
        AssertHcRatio(palette, "BorderStrongBrush", "InputBrush", 3.0);
        AssertHcRatio(palette, "WindowEdgeBrush", "CanvasBrush", 3.0);
        AssertHcRatio(palette, "PrimaryTextBrush", "PrimaryBrush", 4.5);
        AssertHcRatio(palette, "AccentTextBrush", "AccentBrush", 4.5);
        AssertHcRatio(palette, "DangerHoverTextBrush", "DangerHoverBrush", 4.5);
    }

    private static void AssertHcRatio(
        IReadOnlyDictionary<string, Color> palette,
        string foregroundKey,
        string backgroundKey,
        double minRatio)
    {
        var ratio = ThemeContrast.Ratio(ToHex(palette[foregroundKey]), ToHex(palette[backgroundKey]));
        if (ratio < minRatio)
        {
            throw new InvalidOperationException(
                $"[HC] {foregroundKey} on {backgroundKey} ratio is {ratio:F2}, expected >= {minRatio:F1}");
        }
    }

    /// <summary>Every brush/color token key seeded by App.xaml.</summary>
    private static IEnumerable<string> AppXamlBrushKeys()
    {
        var appXaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "App.xaml"));
        foreach (Match match in Regex.Matches(appXaml, @"x:Key=""(\w+)"""))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static Color ParseHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }


    private static void AuditPalette(string name, (string Key, string Value)[] tokens)
    {
        var map = ThemeContrast.TokenMap(tokens);

        // WCAG AA for normal text: >= 4.5:1
        AssertRatio(name, "TextPrimaryBrush", map["TextPrimaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextSecondaryBrush", map["TextSecondaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextTertiaryBrush (placeholder/caption)", map["TextTertiaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextTertiaryBrush (on input)", map["TextTertiaryBrush"], "InputBrush", map["InputBrush"], 4.5);
        AssertRatio(name, "TextPrimaryBrush (on result surface)", map["TextPrimaryBrush"], "ResultSurfaceBrush", map["ResultSurfaceBrush"], 4.5);
        AssertRatio(name, "TextSecondaryBrush (on result surface)", map["TextSecondaryBrush"], "ResultSurfaceBrush", map["ResultSurfaceBrush"], 4.5);
        AssertRatio(name, "TextTertiaryBrush (on result surface)", map["TextTertiaryBrush"], "ResultSurfaceBrush", map["ResultSurfaceBrush"], 4.5);

        // WCAG non-text contrast: >= 3.0:1 for input borders
        AssertRatio(name, "BorderStrongBrush (input edge)", map["BorderStrongBrush"], "InputBrush", map["InputBrush"], 3.0);
        AssertRatio(name, "BorderStrongBrush (surface edge)", map["BorderStrongBrush"], "SurfaceBrush", map["SurfaceBrush"], 3.0);

        // Accent hover/input border: the old light-theme AccentBorder measured
        // 2.10:1 on the white input — the token must clear non-text 3:1 on the
        // input fill and on the soft accent chip it sits on.
        AssertRatio(name, "AccentBorderBrush (input edge)", map["AccentBorderBrush"], "InputBrush", map["InputBrush"], 3.0);
        AssertRatio(name, "AccentBorderBrush (accent soft chip)", map["AccentBorderBrush"], "AccentSoftBrush", map["AccentSoftBrush"], 3.0);

        // Status badges on their soft chips: >= 4.5:1
        AssertRatio(name, "WarningBrush", map["WarningBrush"], "WarningSoftBrush", map["WarningSoftBrush"], 4.5);
        AssertRatio(name, "DangerBrush", map["DangerBrush"], "DangerSoftBrush", map["DangerSoftBrush"], 4.5);
        AssertRatio(name, "SuccessBrush", map["SuccessBrush"], "SuccessSoftBrush", map["SuccessSoftBrush"], 4.5);

        // Accent text on accent button: >= 4.5:1
        AssertRatio(name, "AccentTextBrush", map["AccentTextBrush"], "AccentBrush", map["AccentBrush"], 4.5);

        // Primary button: token text colour on the neutral primary fill: >= 4.5:1
        AssertRatio(name, "PrimaryTextBrush", map["PrimaryTextBrush"], "PrimaryBrush", map["PrimaryBrush"], 4.5);
        AssertRatio(name, "PrimaryTextBrush (hover)", map["PrimaryTextBrush"], "PrimaryHoverBrush", map["PrimaryHoverBrush"], 4.5);
        AssertRatio(name, "PrimaryTextBrush (pressed)", map["PrimaryTextBrush"], "PrimaryPressedBrush", map["PrimaryPressedBrush"], 4.5);

        // Danger button hover/pressed: the dark theme swaps to a deepened
        // soft fill + red text precisely because white-on-bright-red fails.
        AssertRatio(name, "DangerHoverTextBrush", map["DangerHoverTextBrush"], "DangerHoverBrush", map["DangerHoverBrush"], 4.5);
        AssertRatio(name, "DangerPressedTextBrush", map["DangerPressedTextBrush"], "DangerPressedBrush", map["DangerPressedBrush"], 4.5);

        // Focus ring: must clear non-text contrast against resting surfaces
        // (the old AccentBorder ring was 2.10:1 on the light canvas).
        AssertRatio(name, "FocusBrush (canvas)", map["FocusBrush"], "CanvasBrush", map["CanvasBrush"], 3.0);
        AssertRatio(name, "FocusBrush (surface)", map["FocusBrush"], "SurfaceBrush", map["SurfaceBrush"], 3.0);

        // Homepage reading plane consistency: luminance difference between InputBrush and ResultSurfaceBrush <= 2%
        var inputLum = ThemeContrast.Luminance(map["InputBrush"]);
        var resultLum = ThemeContrast.Luminance(map["ResultSurfaceBrush"]);
        var lumDiff = Math.Abs(inputLum - resultLum);
        if (lumDiff > 0.02)
        {
            throw new InvalidOperationException(
                $"[{name}] InputBrush ({map["InputBrush"]}) and ResultSurfaceBrush ({map["ResultSurfaceBrush"]}) luminance difference is {lumDiff:P2}, expected <= 2%");
        }
    }

    private static void AssertRatio(
        string palette,
        string fgName,
        string fgHex,
        string bgName,
        string bgHex,
        double minRatio)
    {
        var ratio = ThemeContrast.Ratio(fgHex, bgHex);
        if (ratio < minRatio)
        {
            throw new InvalidOperationException(
                $"[{palette}] {fgName} ({fgHex}) on {bgName} ({bgHex}) ratio is {ratio:F2}, expected >= {minRatio:F1}");
        }
    }
}
