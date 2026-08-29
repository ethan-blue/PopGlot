using System.Diagnostics;

namespace PopGlot.Windows.Services;

/// <summary>
/// Unified Translation Coordinator that mediates all translation entry points
/// (Selection, Screenshot, Manual, QuickSearch) through an authoritative
/// privacy, routing, error handling, and session tracking lifecycle.
/// </summary>
internal sealed class TranslationCoordinator
{
    private readonly IHistoryRepository? _history;
    private readonly IVocabularyRepository? _vocabulary;

    public TranslationCoordinator(IHistoryRepository? history = null, IVocabularyRepository? vocabulary = null)
    {
        _history = history;
        _vocabulary = vocabulary;
    }

    public static TranslationCoordinator Instance { get; } = new(new HistoryStore(), new VocabularyStore());

    public async Task<TranslationSession> TranslateTextAsync(
        string source,
        string sourceLang,
        string targetLang,
        TranslationInputSource sourceKind,
        CancellationToken cancellationToken = default,
        Action<TranslationSessionStage>? onStageChanged = null)
    {
        var session = new TranslationSession
        {
            InputSource = sourceKind,
            SourceText = source,
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            Stage = TranslationSessionStage.Created,
        };

        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            var trimmed = source?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(trimmed))
            {
                session.Stage = TranslationSessionStage.Failed;
                session.Error = new TranslationError(
                    TranslationErrorKind.EmptyInput,
                    "翻译原文不能为空。",
                    "请输入或选择要翻译的文字。");
                onStageChanged?.Invoke(session.Stage);
                return session;
            }

            session.SourceText = trimmed;
            session.Stage = TranslationSessionStage.Routing;
            onStageChanged?.Invoke(session.Stage);

            var settings = CoreBridge.GetSettings();
            var apiKey = CredentialStore.LoadApiKey(ProfileManager.ResolveActiveCredentialTarget());
            var isLocal = settings.TargetsLocalRuntime;
            var hasConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || isLocal;

            if (settings.SafeDevMode || !settings.NetworkEnabled)
            {
                if (!isLocal)
                {
                    session.Stage = TranslationSessionStage.Failed;
                    session.Error = new TranslationError(
                        TranslationErrorKind.OfflineOnly,
                        "已开启安全离线模式或网络已关闭；未发送任何出网请求。",
                        "可在设置中配置本地模型 (如 Ollama/LM Studio)，或开启网络。");
                    onStageChanged?.Invoke(session.Stage);
                    return session;
                }
            }

            session.Stage = TranslationSessionStage.Translating;
            onStageChanged?.Invoke(session.Stage);

            var netStopwatch = Stopwatch.StartNew();
            TranslationResponse response;

            if (hasConfiguredProvider)
            {
                session.OutboundOccurred = !isLocal;
                session.PipelineLabel = isLocal ? "本地模型" : DescribeProvider(settings.ProviderType);
                response = await CoreBridge.TranslateTextAsync(
                    apiKey, trimmed, sourceLang, targetLang, session.SessionId, cancellationToken);
            }
            else
            {
                // The free web engine is an explicit, consented provider —
                // never a silent fallback. OutboundPolicy owns that decision.
                if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial))
                {
                    session.Stage = TranslationSessionStage.Failed;
                    session.Error = freeDenial ?? new TranslationError(
                        TranslationErrorKind.NetworkDisabled,
                        "未允许出网翻译。",
                        "可在设置中配置自己的模型服务。");
                    onStageChanged?.Invoke(session.Stage);
                    return session;
                }

                session.OutboundOccurred = true;
                session.PipelineLabel = "内置免费引擎";
                response = await FreeTranslateService.TranslateAsync(
                    trimmed, sourceLang, targetLang, cancellationToken);
            }

            netStopwatch.Stop();

            session.TranslatedText = response.Result.TranslatedText;
            session.Transcription = response.Result.Transcription;
            session.Explanation = response.Result.Explanation;
            session.Phonetic = response.Result.Phonetic;
            session.ProtectedTerms = response.Result.ProtectedTerms;
            session.Warnings = response.Result.Warnings;

            session.Stage = response.Result.Warnings.Count > 0
                ? TranslationSessionStage.Partial
                : TranslationSessionStage.Completed;

            totalStopwatch.Stop();
            session.Timing = new TranslationSessionTiming(
                OcrElapsedMs: 0,
                RoutingElapsedMs: 0,
                NetworkElapsedMs: (ulong)netStopwatch.ElapsedMilliseconds,
                TotalElapsedMs: (ulong)totalStopwatch.ElapsedMilliseconds);
            session.CompletedAt = DateTimeOffset.UtcNow;

            onStageChanged?.Invoke(session.Stage);

            // Record to history if successful and history is enabled
            if (session.IsSuccess && _history is not null)
            {
                var shellSettings = ShellSettingsStore.Load();
                var kindLabel = sourceKind switch
                {
                    TranslationInputSource.Selection => "划词",
                    TranslationInputSource.Screenshot => "截图",
                    TranslationInputSource.QuickSearch => "查词",
                    _ => "输入",
                };
                var entry = new TranslationHistoryEntry(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    kindLabel,
                    session.SourceText,
                    session.TranslatedText,
                    session.Explanation,
                    session.ProtectedTerms,
                    session.SourceLanguage,
                    session.TargetLanguage);
                _history.TryAdd(entry, shellSettings.HistoryEnabled);
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            session.Stage = TranslationSessionStage.Cancelled;
            session.Error = new TranslationError(
                TranslationErrorKind.Cancelled,
                "翻译请求已取消。");
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
        catch (Exception ex)
        {
            session.Stage = TranslationSessionStage.Failed;
            session.Error = ClassifyException(ex);
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
    }

    public async Task<TranslationSession> TranslateScreenshotAsync(
        byte[] imageBytes,
        string sourceLang,
        string targetLang,
        CancellationToken cancellationToken = default,
        Action<TranslationSessionStage>? onStageChanged = null)
    {
        var session = new TranslationSession
        {
            InputSource = TranslationInputSource.Screenshot,
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            Stage = TranslationSessionStage.Created,
        };

        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            if (imageBytes is null || imageBytes.Length == 0)
            {
                session.Stage = TranslationSessionStage.Failed;
                session.Error = new TranslationError(
                    TranslationErrorKind.EmptyInput,
                    "截图数据为空。",
                    "请重新框选屏幕区域。");
                onStageChanged?.Invoke(session.Stage);
                return session;
            }

            session.Stage = TranslationSessionStage.Routing;
            onStageChanged?.Invoke(session.Stage);

            var apiKey = CredentialStore.LoadApiKey(ProfileManager.ResolveActiveCredentialTarget());
            var ocrAvailable = WindowsOcrService.IsSupported;
            var route = CoreBridge.PlanScreenshotRoute(ocrAvailable, !string.IsNullOrWhiteSpace(apiKey));
            session.RoutingReason = route.ExplanationZh;

            session.Stage = route.MayUploadImage
                ? TranslationSessionStage.Translating
                : TranslationSessionStage.OcrRunning;
            onStageChanged?.Invoke(session.Stage);

            var netStopwatch = Stopwatch.StartNew();
            var screenshotResult = await CoreBridge.TranslateScreenshotAsync(
                apiKey, imageBytes, sourceLang, targetLang, session.SessionId, cancellationToken);
            netStopwatch.Stop();

            session.PipelineLabel = screenshotResult.Pipeline;
            session.RoutingReason = screenshotResult.PipelineReason;
            session.ImageUploaded = route.MayUploadImage;
            session.OutboundOccurred = route.MayUploadImage || !CoreBridge.GetSettings().TargetsLocalRuntime;

            var response = screenshotResult.Response;
            session.SourceText = response.Result.Transcription;
            session.TranslatedText = response.Result.TranslatedText;
            session.Transcription = response.Result.Transcription;
            session.Explanation = response.Result.Explanation;
            session.Phonetic = response.Result.Phonetic;
            session.ProtectedTerms = response.Result.ProtectedTerms;
            session.Warnings = response.Result.Warnings;

            session.Stage = response.Result.Warnings.Count > 0
                ? TranslationSessionStage.Partial
                : TranslationSessionStage.Completed;

            totalStopwatch.Stop();
            session.Timing = new TranslationSessionTiming(
                OcrElapsedMs: 0,
                RoutingElapsedMs: 0,
                NetworkElapsedMs: (ulong)netStopwatch.ElapsedMilliseconds,
                TotalElapsedMs: (ulong)totalStopwatch.ElapsedMilliseconds);
            session.CompletedAt = DateTimeOffset.UtcNow;

            onStageChanged?.Invoke(session.Stage);

            if (session.IsSuccess && _history is not null && !string.IsNullOrWhiteSpace(session.SourceText))
            {
                var shellSettings = ShellSettingsStore.Load();
                var entry = new TranslationHistoryEntry(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    "截图",
                    session.SourceText,
                    session.TranslatedText,
                    session.Explanation,
                    session.ProtectedTerms,
                    session.SourceLanguage,
                    session.TargetLanguage);
                _history.TryAdd(entry, shellSettings.HistoryEnabled);
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            session.Stage = TranslationSessionStage.Cancelled;
            session.Error = new TranslationError(
                TranslationErrorKind.Cancelled,
                "截图翻译已取消。");
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
        catch (Exception ex)
        {
            session.Stage = TranslationSessionStage.Failed;
            session.Error = ClassifyException(ex);
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
    }

    private static string DescribeProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.OpenAiCompatible => "OpenAI 兼容服务",
        ProviderType.OpenAiResponses => "OpenAI 服务",
        ProviderType.AnthropicMessages => "Anthropic 服务",
        ProviderType.GeminiGenerateContent => "Gemini 服务",
        _ => providerType.ToString(),
    };

    private static TranslationError ClassifyException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("离线") || msg.Contains("SafeDevMode") || msg.Contains("offline", StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationError(
                TranslationErrorKind.OfflineOnly,
                msg,
                "可在设置中关闭安全离线模式或配置本地模型服务。");
        }
        if (msg.Contains("网络") || msg.Contains("NetworkDisabled") || msg.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationError(
                TranslationErrorKind.NetworkDisabled,
                msg,
                "可在设置中开启大模型网络翻译。");
        }
        if (msg.Contains("429") || msg.Contains("限流") || msg.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationError(
                TranslationErrorKind.RateLimited,
                msg,
                "请稍候重试，或在设置中配置独立 API Key。",
                IsTransient: true);
        }
        if (msg.Contains("401") || msg.Contains("403") || msg.Contains("Unauthorized") || msg.Contains("Key"))
        {
            return new TranslationError(
                TranslationErrorKind.Unauthorized,
                msg,
                "请检查 API Key 是否正确或已过期。");
        }
        if (msg.Contains("OCR"))
        {
            return new TranslationError(
                TranslationErrorKind.OcrFailed,
                msg,
                "请框选更清晰的高对比度文字区域。");
        }

        return new TranslationError(
            TranslationErrorKind.Unknown,
            msg,
            "请检查网络或服务配置后重试。");
    }
}
