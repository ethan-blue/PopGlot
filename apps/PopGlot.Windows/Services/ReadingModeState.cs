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

    private string? _summarySource;

    public void CaptureTranslation(string? text, string? note)
    {
        TranslationText = text ?? string.Empty;
        TranslationNote = note ?? string.Empty;
    }

    public void ShowTranslation() => Mode = ReadingMode.Translation;

    public bool HasSummary(string source) =>
        SummaryText.Length > 0 &&
        _summarySource is not null &&
        string.Equals(_summarySource, source, StringComparison.Ordinal);

    public void RememberSummary(string source, string? text, string? note, bool show = true)
    {
        _summarySource = source;
        SummaryText = text ?? string.Empty;
        SummaryNote = note ?? string.Empty;
        if (show)
        {
            Mode = ReadingMode.Summary;
        }
    }
}
