using System.Text.RegularExpressions;

namespace PopGlot.Windows.Services;

/// <summary>
/// Advisory language diagnostics, not a language detector or a completeness
/// signal. Preserve the text and let the user retry explicitly; never silently
/// submit a second paid request.
/// </summary>
internal static class TranslationQuality
{
    public static string? Check(string source, string translated, string sourceLanguage, string targetLanguage)
    {
        var target = LanguageCatalog.Normalize(targetLanguage);
        var from = LanguageCatalog.Normalize(sourceLanguage);
        // Explicit same-language and simplified/traditional conversions are valid.
        if (from != LanguageCatalog.Auto && from.Split('-')[0] == target.Split('-')[0]) return null;
        if (target is not ("zh-CN" or "zh-TW" or "en")) return null;
        var input = Prose(source);
        var output = Prose(translated);
        if (input.Length == 0 || output.Length == 0) return null;
        static int Han(string text) => text.Count(c => c is >= '\u4e00' and <= '\u9fff');
        static bool OtherScript(string text) => text.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af');
        if (OtherScript(output)) return null; // Kanji alone is not proof of Chinese.
        var latin = output.Count(char.IsAsciiLetter);
        var words = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var enoughSource = input.Count(char.IsAsciiLetter) >= 20 || Han(input) >= 8;
        if (!enoughSource) return null;
        if (target.StartsWith("zh", StringComparison.Ordinal) && Han(output) == 0 && latin >= 40 && words >= 6)
            return "译文可能未使用所选中文：结果仍是较长的西文文本。已保留内容，请确认目标语言后手动重试。";
        if (target == "en" && Han(output) >= 12 && latin <= 3)
            return "译文可能未使用所选英语：结果仍以中文为主。已保留内容，请确认目标语言后手动重试。";
        return null;
    }

    private static string Prose(string text)
    {
        // Cap work and abstain on ambiguous machine syntax / unmatched fences.
        if (text.Length > 32_768) return string.Empty;
        try
        {
            var prose = Regex.Replace(text, @"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\r\n]*`|https?://\S+", " ",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return prose.IndexOfAny(['`', '{', '}', '=', ';', '\\', '<', '>']) >= 0 ? string.Empty : prose.Trim();
        }
        catch (RegexMatchTimeoutException) { return string.Empty; }
    }
}
