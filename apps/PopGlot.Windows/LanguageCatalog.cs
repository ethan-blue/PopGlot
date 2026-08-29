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
}
