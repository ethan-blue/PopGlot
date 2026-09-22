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

    public void RememberSummary(string source, string? text, string? note)
    {
        _summarySource = source;
        SummaryText = text ?? string.Empty;
        SummaryNote = note ?? string.Empty;
        Mode = ReadingMode.Summary;
    }
}
