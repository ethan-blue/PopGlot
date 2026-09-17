namespace PopGlot.Windows.Services;

internal enum TranslationInputSource
{
    Selection,
    Screenshot,
    Manual,
    QuickSearch,
}

/// <summary>
/// Typed identity of the execution pipeline a session runs on. The Chinese
/// <see cref="TranslationSession.PipelineLabel"/> remains a display string
/// only — control logic must branch on this enum so a label rewording can
/// never silently flip UI behaviour.
/// </summary>
internal enum TranslationPipelineKind
{
    /// <summary>Routing not resolved yet.</summary>
    Unknown,

    /// <summary>Text via the user's configured remote provider.</summary>
    UserProvider,

    /// <summary>Text via a local runtime (Ollama / LM Studio).</summary>
    LocalModel,

    /// <summary>The built-in free web engine.</summary>
    FreeEngine,

    /// <summary>Screenshot translated by the vision model directly (no text stage).</summary>
    VisionDirect,

    /// <summary>Recognized text (local OCR or vision transcription) translated by the user's text channel.</summary>
    OcrUserText,

    /// <summary>Recognized text translated by the free engine.</summary>
    OcrFreeText,
}

/// <summary>Which engine executes (or executed) a session's TEXT stage.</summary>
internal enum TranslationTextExecutor
{
    /// <summary>No text stage ran (vision-direct, or routing unresolved).</summary>
    None,

    /// <summary>The user's configured provider runs the text request.</summary>
    UserProvider,

    /// <summary>A local runtime runs the text request.</summary>
    LocalModel,

    /// <summary>The built-in free engine runs the text request.</summary>
    FreeEngine,
}

/// <summary>
/// Honest, typed answer to "did the active custom prompt/style take part in
/// this session's text stage?". The free engine has no prompt channel and the
/// shell never splices style text client-side, so free-engine and
/// vision-direct results must be reported as such instead of implying support.
/// </summary>
internal enum TranslationPromptSupport
{
    /// <summary>No text stage resolved yet.</summary>
    Pending,

    /// <summary>The active prompt template was compiled and sent with the request.</summary>
    Applied,

    /// <summary>
    /// The engine that ran has no prompt support: the selected custom style
    /// was NOT applied (free engine — both plain and OCR + free text).
    /// </summary>
    NotSupported,

    /// <summary>The route has no text stage at all, so a style could not apply (vision-direct).</summary>
    NotApplicable,

    /// <summary>The request-start snapshot was unreadable; no promise either way.</summary>
    Unknown,
}

internal enum TranslationSessionStage
{
    Created,
    AcquiringInput,
    OcrRunning,
    Routing,
    Translating,
    Streaming,
    Finalizing,
    Completed,
    Partial,
    Failed,
    Cancelled,
}

internal enum TranslationStreamUpdateKind
{
    Delta,
    Reset,
}

internal sealed record TranslationStreamUpdate(
    string SessionId,
    long Epoch,
    TranslationStreamUpdateKind Kind,
    string Delta,
    string AccumulatedText,
    long AccumulatedCharCount,
    TimeSpan? Ttft = null,
    bool IsPartial = false,
    string? Message = null);

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

/// <summary>
/// Identity + revision of a prompt template, snapshotted once at request
/// start. Deliberately carries NO instruction body: sessions and history
/// record WHICH style produced a result — never a copy of the prompt text —
/// so a later restore can label the style locally with zero network.
/// </summary>
internal sealed record PromptTemplateIdentity(string Id, string Name, ulong Revision);

internal sealed class TranslationSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public TranslationInputSource InputSource { get; init; }
    public string SourceText { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = LanguageCatalog.Auto;
    public string TargetLanguage { get; set; } = "zh-CN";

    public TranslationSessionStage Stage { get; set; } = TranslationSessionStage.Created;
    public string? PipelineLabel { get; set; }
    /// <summary>Typed pipeline identity — control logic branches here, never on <see cref="PipelineLabel"/>.</summary>
    public TranslationPipelineKind PipelineKind { get; set; } = TranslationPipelineKind.Unknown;
    /// <summary>Which engine runs (ran) the session's TEXT stage.</summary>
    public TranslationTextExecutor TextExecutor { get; set; } = TranslationTextExecutor.None;
    /// <summary>Whether the active custom prompt/style actually took part in the text stage.</summary>
    public TranslationPromptSupport PromptSupport { get; set; } = TranslationPromptSupport.Pending;
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

    /// <summary>Set once the coordinator has persisted this session, so a duplicated final delivery can never write history twice.</summary>
    public bool HistoryCommitted { get; set; }

    /// <summary>
    /// Identity-only provenance of the prompt template that produced this
    /// result, snapshotted once at request start (see TranslationCoordinator):
    /// an in-flight session never changes when the user switches styles.
    /// Null for the free engine (it has no prompt) and for older sessions.
    /// Never carries the instruction body.
    /// </summary>
    public string? PromptTemplateId { get; set; }
    public string? PromptTemplateName { get; set; }
    public ulong? PromptTemplateRevision { get; set; }

    /// <summary>
    /// The one completion contract every consumer shares: only a non-empty
    /// Completed stage with no integrity warnings is eligible for history,
    /// auto-copy, speech, starring or code-block copy. "The request returned
    /// a result" is not "the result is complete"; a Partial stays visible but
    /// never triggers side effects.
    /// </summary>
    public bool IsCleanCompletion =>
        Stage == TranslationSessionStage.Completed &&
        Warnings.Count == 0 &&
        !string.IsNullOrWhiteSpace(TranslatedText);
}

/// <summary>
/// Encapsulates an active streaming translation session with its fast delta buffer
/// and final completion response task.
/// </summary>
internal sealed class TranslationStreamSession
{
    public TranslationStreamBuffer Buffer { get; }
    public Task<TranslationResponse> Completion { get; }

    public TranslationStreamSession(TranslationStreamBuffer buffer, Task<TranslationResponse> completion)
    {
        Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        Completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }
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
    bool IsStarred(string word, string sourceLang = "auto", string targetLang = "zh-CN");
    VocabularySaveResult ToggleStar(
        string word,
        string translation,
        string phonetic = "",
        string explanation = "",
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        List<string>? tags = null);
    bool Remove(Guid id);
    bool Clear();
    string ExportToCsv();
    string ExportToAnkiTsv();
    string ExportToMarkdown();
}
