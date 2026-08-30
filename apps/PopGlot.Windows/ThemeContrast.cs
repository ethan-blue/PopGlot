namespace PopGlot.Windows;

/// <summary>
/// Deterministic WCAG 2.x contrast math over the hex tokens owned by
/// ThemeService. Pure functions only — the logic tests use them to audit the
/// palettes, so a token that quietly drops below AA fails the build instead
/// of shipping as an unreadable grey.
/// </summary>
internal static class ThemeContrast
{
    /// <summary>WCAG relative luminance of an sRGB colour given as #RRGGBB.</summary>
    internal static double Luminance(string hex)
    {
        var value = hex.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }
        if (value.Length == 8)
        {
            // #AARRGGBB: the alpha channel does not change the colour itself.
            value = value[2..];
        }
        if (value.Length != 6)
        {
            throw new FormatException($"主题色值格式无效：{hex}");
        }
        var r = Channel(value[0..2]);
        var g = Channel(value[2..4]);
        var b = Channel(value[4..6]);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>WCAG contrast ratio between two colours, from 1.0 to 21.0.</summary>
    internal static double Ratio(string foregroundHex, string backgroundHex)
    {
        var foreground = Luminance(foregroundHex);
        var background = Luminance(backgroundHex);
        var lighter = Math.Max(foreground, background);
        var darker = Math.Min(foreground, background);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Token list → lookup, so tests can address tokens by key.</summary>
    internal static IReadOnlyDictionary<string, string> TokenMap(
        IEnumerable<(string Key, string Value)> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in tokens)
        {
            map[key] = value;
        }
        return map;
    }

    private static double Channel(string hexPair)
    {
        var channel = Convert.ToInt32(hexPair, 16) / 255.0;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
