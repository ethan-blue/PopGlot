using System.Text;
using PopGlot.Windows.Services;
namespace PopGlot.Windows;

/// <summary>
/// The languages the UI offers, mirroring <c>popglot-domain::language</c>.
/// </summary>
/// <remarks>
/// Every language list in the app used to be an inline block of
/// <c>ComboBoxItem</c> elements, duplicated across three XAML files that had
/// already drifted apart (the panel offered eight languages, the main window
/// nine, and neither matched what the core understood). One table now feeds
/// every picker.
/// </remarks>
internal sealed record LanguageOption(string Tag, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class LanguageCatalog
{
    public const string Auto = "auto";

    // Declared before Sources: static initializers run in textual order, and
    // Sources spreads this list.
    private static readonly LanguageOption[] TranslatableOptions =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("es", "西班牙语"),
        new("pt", "葡萄牙语"),
        new("ru", "俄语"),
        new("it", "意大利语"),
        new("ar", "阿拉伯语"),
        new("hi", "印地语"),
        new("th", "泰语"),
        new("vi", "越南语"),
    ];

    /// <summary>Source-language options, including automatic detection.</summary>
    public static IReadOnlyList<LanguageOption> Sources { get; } =
    [
        new(Auto, "自动检测"),
        .. TranslatableOptions,
    ];

    /// <summary>Target-language options. "Auto" is not a translation target.</summary>
    public static IReadOnlyList<LanguageOption> Targets => TranslatableOptions;

    public static string DisplayName(string tag)
    {
        var normalized = Normalize(tag);
        return Sources.FirstOrDefault(option => option.Tag == normalized)?.DisplayName ?? normalized;
    }

    /// <summary>Canonicalizes a tag; unknown tags pass through lowercased.</summary>
    public static string Normalize(string? tag) => (tag ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "auto" or "detect" => Auto,
        "zh" or "zh-cn" or "zh-hans" or "chs" => "zh-CN",
        "zh-tw" or "zh-hant" or "cht" => "zh-TW",
        "en" or "en-us" or "en-gb" => "en",
        "ja" or "jp" or "ja-jp" => "ja",
        "ko" or "kr" or "ko-kr" => "ko",
        "fr" or "fr-fr" => "fr",
        "de" or "de-de" => "de",
        "es" or "es-es" => "es",
        "pt" or "pt-br" => "pt",
        "ru" or "ru-ru" => "ru",
        "it" or "it-it" => "it",
        "ar" => "ar",
        "hi" => "hi",
        "th" => "th",
        "vi" => "vi",
        var other => other,
    };

    /// <summary>
    /// The option instance matching a tag, so a ComboBox bound by reference
    /// selects correctly.
    /// </summary>
    public static LanguageOption ResolveSource(string? tag) =>
        Sources.FirstOrDefault(option => option.Tag == Normalize(tag)) ?? Sources[0];

    public static LanguageOption ResolveTarget(string? tag)
    {
        var normalized = Normalize(tag);
        if (normalized == Auto)
        {
            normalized = "zh-CN";
        }
        return Targets.FirstOrDefault(option => option.Tag == normalized) ?? Targets[0];
    }

    /// <summary>
    /// Picks a sensible target when the user swaps a detected source language.
    /// </summary>
    /// <remarks>
    /// Swapping "auto → 简体中文" has no defined inverse. Treating the current
    /// target as the new source and Chinese-or-English as the new target keeps
    /// the button useful instead of producing a no-op pair.
    /// </remarks>
    public static (string Source, string Target) Swap(string source, string target)
    {
        var normalizedSource = Normalize(source);
        var normalizedTarget = Normalize(target);
        if (normalizedSource == Auto)
        {
            return (normalizedTarget, normalizedTarget.StartsWith("zh", StringComparison.Ordinal) ? "en" : "zh-CN");
        }
        return (normalizedTarget, normalizedSource);
    }

    /// <summary>Identifies a dominant supported script without inspecting code or URLs.</summary>
    internal static string? DetectScript(string text)
    {
        var prose = StripCodeAndUrls(text);
        var letters = 0;
        var latin = 0;
        var han = 0;
        var cyrillic = 0;
        var hasKana = false;
        var hasHangul = false;

        foreach (var rune in prose.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            letters++;
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff) hasKana = true;
            if (value is >= 0xac00 and <= 0xd7af or >= 0x1100 and <= 0x11ff or >= 0x3130 and <= 0x318f)
                hasHangul = true;
            if (value is >= 0x4e00 and <= 0x9fff) han++;
            if (value is >= 0x0400 and <= 0x052f) cyrillic++;
            if (IsLatinLetter(value)) latin++;
        }

        if (hasKana) return "ja";
        if (hasHangul) return "ko";
        if (letters == 0) return null;
        if (cyrillic * 100 >= letters * 30) return "ru";
        if (han > 0 && (han >= latin || (han >= 3 && han * 3 >= latin))) return "zh-CN";
        if ((latin > 0 && han == 0 && letters >= 2) || (latin >= 10 && latin >= han * 4)) return "en";
        return null;
    }

    private static string StripCodeAndUrls(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 32_768) return string.Empty;
        try
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\r\n]*`|https?://\S+|\{[^{}\r\n]{1,40}\}",
                " ",
                System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromMilliseconds(100));
            return stripped.IndexOf('`') >= 0 ? string.Empty : stripped.Trim();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>Chooses a sensible target for an auto-detected source language.</summary>
    internal static string ResolveAutoTarget(string sourceText, string currentTarget) =>
        DetectScript(sourceText) switch
        {
            "zh-CN" or "zh-TW" => "en",
            not null => "zh-CN",
            _ => currentTarget,
        };

    /// <summary>
    /// Resolves the effective (Source, Target) pair for selection translation ("划词").
    /// When the source is Auto, picks the target from the detected script.
    /// When a previous manual swap left an explicit direction that conflicts with
    /// the newly selected text (e.g. zh-CN → en on English text, or en → zh-CN on
    /// Chinese text), resets Source to Auto and switches Target so selection
    /// translation never stalls on same-language output.
    /// </summary>
    internal static (string Source, string Target) ResolveSelectionDirection(
        string sourceText, string currentSource, string currentTarget)
    {
        var normalizedSource = Normalize(currentSource);
        var normalizedTarget = Normalize(currentTarget);
        if (normalizedSource == Auto)
        {
            return (Auto, ResolveAutoTarget(sourceText, normalizedTarget));
        }

        var detected = DetectScript(sourceText);
        if (detected is null)
        {
            return (normalizedSource, normalizedTarget);
        }

        var detectedIsZh = detected.StartsWith("zh", StringComparison.Ordinal);
        var sourceIsZh = normalizedSource.StartsWith("zh", StringComparison.Ordinal);
        var targetIsZh = normalizedTarget.StartsWith("zh", StringComparison.Ordinal);

        if (detectedIsZh && (targetIsZh || !sourceIsZh))
        {
            return (Auto, "en");
        }
        if (!detectedIsZh && (normalizedTarget == detected || sourceIsZh))
        {
            return (Auto, "zh-CN");
        }

        return (normalizedSource, normalizedTarget);
    }

    private static bool IsLatinLetter(int value) => value is
        >= 0x0041 and <= 0x005a or >= 0x0061 and <= 0x007a or
        >= 0x00c0 and <= 0x02e4 or >= 0x1d00 and <= 0x1d7f or
        >= 0x1d80 and <= 0x1dbf or >= 0x1e00 and <= 0x1eff or
        >= 0x2c60 and <= 0x2c7f or >= 0xa720 and <= 0xa7ff or
        >= 0xab30 and <= 0xab6f;
}
