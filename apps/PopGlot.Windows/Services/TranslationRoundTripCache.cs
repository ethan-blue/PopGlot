namespace PopGlot.Windows.Services;

internal sealed record TranslationRoundTripResult(
    string Text,
    string Explanation,
    string? PipelineLabel,
    string? PromptTemplateName,
    bool IsReverse)
{

    internal TranslationSession CreateSession(
        string source,
        string sourceLanguage,
        string targetLanguage,
        TranslationInputSource inputSource) => new()
    {
        InputSource = inputSource,
        SourceText = source,
        SourceLanguage = sourceLanguage,
        TargetLanguage = targetLanguage,
        Stage = TranslationSessionStage.Completed,
        TranslatedText = Text,
        Explanation = Explanation,
        PipelineLabel = PipelineLabel,
        PromptTemplateName = PromptTemplateName,
    };
}

/// <summary>One in-memory completed translation, reusable in either direction.</summary>
internal sealed class TranslationRoundTripCache
{
    internal const string ReusedStatus = "已复用上次译文";
    internal const string ReusedFact = "上次译文";

    private readonly object _gate = new();
    private Entry? _entry;

    internal bool TryGet(
        string source,
        string sourceLanguage,
        string targetLanguage,
        int configVersion,
        string styleIdentity,
        out TranslationRoundTripResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);
        ArgumentNullException.ThrowIfNull(styleIdentity);

        var sourceTag = LanguageCatalog.Normalize(sourceLanguage);
        var targetTag = LanguageCatalog.Normalize(targetLanguage);
        lock (_gate)
        {
            if (_entry is not { } entry)
            {
                result = null!;
                return false;
            }

            if (entry.ConfigVersion != configVersion ||
                !string.Equals(entry.StyleIdentity, styleIdentity, StringComparison.Ordinal))
            {
                _entry = null;
                result = null!;
                return false;
            }

            if (string.Equals(source, entry.SourceText, StringComparison.Ordinal) &&
                sourceTag == entry.SourceLanguage && targetTag == entry.TargetLanguage)
            {
                result = new TranslationRoundTripResult(
                    entry.ResultText, entry.Explanation, entry.PipelineLabel, entry.PromptTemplateName, IsReverse: false);
                return true;
            }

            if (string.Equals(source, entry.ResultText, StringComparison.Ordinal) &&
                sourceTag == entry.TargetLanguage && targetTag == entry.SourceLanguage)
            {
                result = new TranslationRoundTripResult(
                    entry.SourceText, string.Empty, entry.PipelineLabel, entry.PromptTemplateName, IsReverse: true);
                return true;
            }
        }

        result = null!;
        return false;
    }

    internal void Set(
        string source,
        string sourceLanguage,
        string targetLanguage,
        string result,
        string explanation,
        int configVersion,
        string styleIdentity,
        string? pipelineLabel = null,
        string? promptTemplateName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceLanguage);
        ArgumentNullException.ThrowIfNull(targetLanguage);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(explanation);
        ArgumentNullException.ThrowIfNull(styleIdentity);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(result)) return;

        var entry = new Entry(
            source,
            LanguageCatalog.Normalize(sourceLanguage),
            LanguageCatalog.Normalize(targetLanguage),
            result,
            explanation,
            configVersion,
            styleIdentity,
            pipelineLabel,
            promptTemplateName);
        lock (_gate)
        {
            _entry = entry;
        }
    }

    internal static string ExplanationFor(TranslationSession session) =>
        string.Join("\n", new[] { session.Explanation }
            .Concat(session.Warnings)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .Distinct());

    private sealed record Entry(
        string SourceText,
        string SourceLanguage,
        string TargetLanguage,
        string ResultText,
        string Explanation,
        int ConfigVersion,
        string StyleIdentity,
        string? PipelineLabel,
        string? PromptTemplateName);
}
