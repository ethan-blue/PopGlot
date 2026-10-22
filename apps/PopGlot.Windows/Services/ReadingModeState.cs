namespace PopGlot.Windows.Services;

/// <summary>
/// Which reading of the current source the result pane is showing.
/// Translation already carries its own explanation. A summary is a second
/// reading of the same source and must not erase the translation.
/// </summary>
internal enum ReadingMode
{
    Translation,
    Summary,
}

/// <summary>
/// Immutable identity of a summary (or text task) request.
/// Distinguishes not just source text, but target language, engine, model and configuration version.
/// </summary>
internal sealed record SummaryRequestIdentity(
    string Source,
    string SourceLanguage,
    string TargetLanguage,
    TextTaskKind TaskKind,
    string? EngineProfileId,
    string? ModelName,
    int ConfigVersion,
    string? PromptTemplateId = null,
    ulong? PromptTemplateRevision = null);

/// <summary>
/// Snapshot tying a request identity to a specific UI generation.
/// </summary>
internal sealed record SummaryRequestSnapshot(
    SummaryRequestIdentity Identity,
    long Generation,
    PromptAnchorSnapshot? PreferenceAnchor = null);

/// <summary>
/// Status lines for the summary reading. A summary is its own request:
/// it must not sound like it replaced the translation.
/// </summary>
internal static class ReadingRequestCopy
{
    public const string NeedSource = "请先输入原文";

    public const string Preparing = "整理中…";

    public const string SummaryReadyHint = "要点";

    public const string SummaryCancelled = "已取消要点";

    public const string Cached = "已缓存";

    public static string Finished(ulong elapsedMs) =>
        $"已完成 · {TranslationElapsedText.ForMilliseconds(elapsedMs)}";
}

/// <summary>
/// Remembers the translation and, separately, a summary of one source.
/// Switching readings never throws the other one away.
/// </summary>
internal sealed class ReadingModeState
{
    public ReadingMode Mode { get; private set; } = ReadingMode.Translation;

    public string TranslationText { get; private set; } = string.Empty;

    public string TranslationNote { get; private set; } = string.Empty;

    public string SummaryText { get; private set; } = string.Empty;

    public string SummaryNote { get; private set; } = string.Empty;

    public SummaryRequestIdentity? CurrentSummaryIdentity { get; private set; }

    internal sealed record CachedSummary(
        string Text,
        string Note,
        ulong ElapsedMs,
        string EngineLabel);

    private readonly Dictionary<SummaryRequestIdentity, CachedSummary> _summaryCache = new();

    public void CaptureTranslation(string? text, string? note)
    {
        TranslationText = text ?? string.Empty;
        TranslationNote = note ?? string.Empty;
    }

    public void ShowTranslation() => Mode = ReadingMode.Translation;

    public bool HasSummary(SummaryRequestIdentity identity) =>
        _summaryCache.TryGetValue(identity, out var item) && item.Text.Length > 0;

    public bool HasSummary(string source) =>
        CurrentSummaryIdentity is not null &&
        string.Equals(CurrentSummaryIdentity.Source, source, StringComparison.Ordinal) &&
        SummaryText.Length > 0;

    public bool TryGetSummary(SummaryRequestIdentity identity, out CachedSummary summary)
    {
        if (_summaryCache.TryGetValue(identity, out var cached))
        {
            summary = cached;
            return true;
        }
        summary = null!;
        return false;
    }

    public bool ShowSummary(SummaryRequestIdentity identity)
    {
        if (_summaryCache.TryGetValue(identity, out var item))
        {
            CurrentSummaryIdentity = identity;
            SummaryText = item.Text;
            SummaryNote = item.Note;
            Mode = ReadingMode.Summary;
            return true;
        }
        return false;
    }

    public void RememberSummary(
        SummaryRequestIdentity identity,
        string? text,
        string? note,
        bool show = true,
        ulong elapsedMs = 0,
        string? engineLabel = null)
    {
        var safeText = text ?? string.Empty;
        var safeNote = note ?? string.Empty;
        _summaryCache[identity] = new CachedSummary(
            safeText,
            safeNote,
            elapsedMs,
            engineLabel ?? string.Empty);
        if (show || CurrentSummaryIdentity is null || CurrentSummaryIdentity.Equals(identity))
        {
            CurrentSummaryIdentity = identity;
            SummaryText = safeText;
            SummaryNote = safeNote;
        }
        if (show)
        {
            Mode = ReadingMode.Summary;
        }
    }

    public void RememberSummary(string source, string? text, string? note, bool show = true)
    {
        var identity = new SummaryRequestIdentity(source, string.Empty, string.Empty, TextTaskKind.Summarize, null, null, 0);
        RememberSummary(identity, text, note, show);
    }

    public void ClearSummaryDisplay()
    {
        CurrentSummaryIdentity = null;
        SummaryText = string.Empty;
        SummaryNote = string.Empty;
        Mode = ReadingMode.Translation;
    }
}
