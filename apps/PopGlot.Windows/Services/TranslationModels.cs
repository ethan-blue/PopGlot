namespace PopGlot.Windows.Services;

internal enum TranslationInputSource
{
    Selection,
    Screenshot,
    Manual,
    QuickSearch,
}

internal enum TranslationSessionStage
{
    Created,
    AcquiringInput,
    OcrRunning,
    Routing,
    Translating,
    Completed,
    Partial,
    Failed,
    Cancelled,
}

internal enum TranslationErrorKind
{
    Configuration,
    NetworkDisabled,
    OfflineOnly,
    RateLimited,
    Unauthorized,
    ServerError,
    ParseError,
    Cancelled,
    OcrFailed,
    Sensitive,
    EmptyInput,
    Unknown,
}

internal sealed record TranslationError(
    TranslationErrorKind Kind,
    string Message,
    string? ActionableSuggestion = null,
    bool IsTransient = false);

internal sealed record TranslationSessionTiming(
    ulong OcrElapsedMs = 0,
    ulong RoutingElapsedMs = 0,
    ulong NetworkElapsedMs = 0,
    ulong TotalElapsedMs = 0);

internal sealed class TranslationSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public TranslationInputSource InputSource { get; init; }
    public string SourceText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = LanguageCatalog.Auto;
    public string TargetLanguage { get; set; } = "zh-CN";

    public TranslationSessionStage Stage { get; set; } = TranslationSessionStage.Created;
    public string? PipelineLabel { get; set; }
    public string? RoutingReason { get; set; }
    public bool OutboundOccurred { get; set; }
    /// <summary>True when the screenshot entered any vision Provider request.</summary>
    public bool ImageSentToProvider { get; set; }
    /// <summary>True only when the screenshot crossed the device boundary.</summary>
    public bool ImageLeftDevice { get; set; }
    // Compatibility/display alias for existing callers: means remote upload.
    public bool ImageUploaded { get; set; }

    public string TranslatedText { get; set; } = string.Empty;
    public string Transcription { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Phonetic { get; set; } = string.Empty;
    public IReadOnlyList<string> ProtectedTerms { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];

    public TranslationError? Error { get; set; }
    public TranslationSessionTiming Timing { get; set; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public bool IsSuccess => Stage is TranslationSessionStage.Completed or TranslationSessionStage.Partial;
}

internal interface ISettingsService
{
    ShellSettings GetShellSettings();
    void SaveShellSettings(ShellSettings settings);

    ProviderSettings GetProviderSettings();
    void SaveProviderSettings(ProviderSettings settings);
}

internal interface ICredentialVault
{
    bool HasCredential(string target = "PopGlot/OpenAICompatibleApiKey");
    string? LoadCredential(string target = "PopGlot/OpenAICompatibleApiKey");
    void SaveCredential(string secret, string target = "PopGlot/OpenAICompatibleApiKey");
    void DeleteCredential(string target = "PopGlot/OpenAICompatibleApiKey");
}

internal interface IHistoryRepository
{
    IReadOnlyList<TranslationHistoryEntry> Load();
    HistoryAddResult TryAdd(TranslationHistoryEntry entry, bool enabled);
    bool Remove(Guid id);
    bool Clear();
    string ExportToCsv();
    string ExportToMarkdown();
}

internal interface IVocabularyRepository
{
    IReadOnlyList<VocabularyWord> GetAll();
    bool IsStarred(string word);
    bool ToggleStar(
        string word,
        string translation,
        string phonetic = "",
        string explanation = "",
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        List<string>? tags = null);
    bool Remove(Guid id);
    void Clear();
    string ExportToCsv();
    string ExportToAnkiTsv();
    string ExportToMarkdown();
}
