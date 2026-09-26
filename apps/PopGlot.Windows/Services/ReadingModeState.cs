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
    int ConfigVersion);

/// <summary>
/// Snapshot tying a request identity to a specific UI generation.
/// </summary>
internal sealed record SummaryRequestSnapshot(
    SummaryRequestIdentity Identity,
    long Generation);

/// <summary>
/// Status lines for the summary reading. A summary is its own request:
/// it must not sound like it replaced the translation.
/// </summary>
internal static class ReadingRequestCopy
{
    public const string NeedSource = "请先输入原文，再看要点。";

    public const string SummaryWhileTranslating = "正在整理要点，翻译继续";

    public const string SummaryKeepsTranslation = "正在整理要点，译文保留";

    public const string SummaryKeptTranslation = "正在整理要点，译文已保留";

    public const string SummaryReadyHint = "要点 · 切回「译文」可看翻译";

    public const string SummaryCancelled = "已取消要点";

    public static string WhileRequesting(bool translationRunning) =>
        translationRunning ? SummaryWhileTranslating : SummaryKeepsTranslation;

    public static string Finished(ulong elapsedMs) => $"要点 · {elapsedMs} ms · 可切回译文";
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

    private readonly Dictionary<SummaryRequestIdentity, (string Text, string Note)> _summaryCache = new();

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

    public bool TryGetSummary(SummaryRequestIdentity identity, out (string Text, string Note) summary) =>
        _summaryCache.TryGetValue(identity, out summary);

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

    public void RememberSummary(SummaryRequestIdentity identity, string? text, string? note, bool show = true)
    {
        var safeText = text ?? string.Empty;
        var safeNote = note ?? string.Empty;
        _summaryCache[identity] = (safeText, safeNote);
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
