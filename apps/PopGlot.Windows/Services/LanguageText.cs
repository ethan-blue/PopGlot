using System.Text.RegularExpressions;

namespace PopGlot.Windows.Services;

internal static class LanguageText
{
    internal static string Prose(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 32_768) return string.Empty;
        try
        {
            var prose = Regex.Replace(text, @"```[\s\S]*?```|~~~[\s\S]*?~~~|`[^`\r\n]*`|https?://\S+", " ",
                RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return prose.IndexOfAny(['`', '{', '}', '=', ';', '\\', '<', '>']) >= 0 ? string.Empty : prose.Trim();
        }
        catch (RegexMatchTimeoutException) { return string.Empty; }
    }
}
