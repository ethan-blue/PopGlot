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
    }

    private static void AuditPalette(string name, (string Key, string Value)[] tokens)
    {
        var map = ThemeContrast.TokenMap(tokens);

        // WCAG AA for normal text: >= 4.5:1
        AssertRatio(name, "TextPrimaryBrush", map["TextPrimaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextSecondaryBrush", map["TextSecondaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextTertiaryBrush (placeholder/caption)", map["TextTertiaryBrush"], "SurfaceBrush", map["SurfaceBrush"], 4.5);
        AssertRatio(name, "TextTertiaryBrush (on input)", map["TextTertiaryBrush"], "InputBrush", map["InputBrush"], 4.5);

        // WCAG non-text contrast: >= 3.0:1 for input borders
        AssertRatio(name, "BorderStrongBrush (input edge)", map["BorderStrongBrush"], "InputBrush", map["InputBrush"], 3.0);
        AssertRatio(name, "BorderStrongBrush (surface edge)", map["BorderStrongBrush"], "SurfaceBrush", map["SurfaceBrush"], 3.0);

        // Status badges on their soft chips: >= 4.5:1
        AssertRatio(name, "WarningBrush", map["WarningBrush"], "WarningSoftBrush", map["WarningSoftBrush"], 4.5);
        AssertRatio(name, "DangerBrush", map["DangerBrush"], "DangerSoftBrush", map["DangerSoftBrush"], 4.5);
        AssertRatio(name, "SuccessBrush", map["SuccessBrush"], "SuccessSoftBrush", map["SuccessSoftBrush"], 4.5);

        // Accent text on accent button: >= 4.5:1
        AssertRatio(name, "AccentTextBrush", map["AccentTextBrush"], "AccentBrush", map["AccentBrush"], 4.5);
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
