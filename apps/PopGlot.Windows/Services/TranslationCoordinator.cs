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
            var (textRoute, _) = ProfileManager.ResolveRoutes();
            var textApiKey = textRoute is null
                ? CredentialStore.LoadApiKey(ProfileManager.ResolveActiveCredentialTarget())
                : CredentialStore.LoadApiKey(textRoute.CredentialTarget);
            var textRuntimeSettings = textRoute?.Profile.ToProviderSettings(settings);
            var isLocal = textRuntimeSettings?.TargetsLocalRuntime ?? settings.TargetsLocalRuntime;
            var hasConfiguredProvider = (textRuntimeSettings is not null &&
                (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                || !string.IsNullOrWhiteSpace(textApiKey)
                || isLocal;

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
                session.PipelineLabel = isLocal ? "本地模型" : DescribeProvider(textRuntimeSettings?.ProviderType ?? settings.ProviderType);
                if (textRuntimeSettings is not null &&
                    (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                {
                    response = await CoreBridge.TranslateTextDraftAsync(
                        textRuntimeSettings,
                        textApiKey ?? string.Empty,
                        trimmed,
                        sourceLang,
                        targetLang,
                        session.SessionId,
                        cancellationToken);
                }
                else
                {
                    response = await CoreBridge.TranslateTextAsync(
                        textApiKey, trimmed, sourceLang, targetLang, session.SessionId, cancellationToken);
                }
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

            // ---- Routing: an explicit state machine over resolved routes ----
            session.Stage = TranslationSessionStage.Routing;
            onStageChanged?.Invoke(session.Stage);

            var routingStopwatch = Stopwatch.StartNew();
            var settings = CoreBridge.GetSettings();
            var ocrAvailable = WindowsOcrService.IsSupported;
            var route = ProfileManager.ResolveRoute(settings, ocrAvailable);
            var textRoute = route.Text;
            var visionRoute = route.Vision;

            var textApiKey = textRoute is null
                ? null
                : CredentialStore.LoadApiKey(textRoute.CredentialTarget);
            var visionApiKey = visionRoute is null
                ? null
                : CredentialStore.LoadApiKey(visionRoute.CredentialTarget);
            // These snapshots are the execution contract. Never reconstruct a
            // provider from CoreBridge's mirrored global settings after this
            // point: the selected text and vision profiles may be unrelated.
            var textRuntimeSettings = textRoute?.Profile.ToProviderSettings(settings);
            var visionRuntimeSettings = visionRoute is null
                ? null
                : visionRoute.Profile.ToProviderSettings(settings) with
                {
                    Mode = TranslationMode.VisionDirect,
                    AllowImageUploadInAuto = true,
                    VisionProvider = null,
                };
            routingStopwatch.Stop();

            // Both routes dead: say what would fix it, before any pixel work.
            if (route.ScreenshotPipeline == ScreenshotPipeline.Unavailable)
            {
                session.Stage = TranslationSessionStage.Failed;
                session.Error = new TranslationError(
                    TranslationErrorKind.OcrFailed,
                    "没有可用的截图翻译线路。",
                    "任选其一：安装 Windows OCR 语言包（设置 → 时间和语言 → 语言 → 光学字符识别）；" +
                    "在服务页配置并启用支持图片的视觉模型；或在「隐私与数据」中允许截图上传。");
                onStageChanged?.Invoke(session.Stage);
                return session;
            }

            ulong ocrElapsedMs = 0;
            ulong networkElapsedMs = 0;
            var imageSentToProvider = false;
            var imageLeftDevice = false;
            TranslationResponse response;
            var pipelineLabel = "本地 OCR";
            var routingReason = route.ExplanationZh;

            if (route.ScreenshotPipeline == ScreenshotPipeline.VisionDirect && visionRoute is not null)
            {
                session.Stage = TranslationSessionStage.Translating;
                onStageChanged?.Invoke(session.Stage);

                if (visionRuntimeSettings is null)
                {
                    throw new InvalidOperationException("所选图片服务没有可执行配置。");
                }
                try
                {
                    // A remote vision call means the image crossed the device
                    // boundary. A loopback vision call still sends the image to
                    // the selected provider, but it is not an upload.
                    imageSentToProvider = true;
                    imageLeftDevice = !visionRuntimeSettings.TargetsLocalRuntime;
                    response = await CoreBridge.TranslateVisionDraftAsync(
                        visionRuntimeSettings,
                        string.Empty,
                        visionApiKey ?? string.Empty,
                        imageBytes,
                        sourceLang,
                        targetLang,
                        session.SessionId,
                        cancellationToken);
                    networkElapsedMs = response.Diagnostics.ElapsedMs;
                    pipelineLabel = visionRuntimeSettings.TargetsLocalRuntime
                        ? "本地视觉模型"
                        : "视觉模型 · 独立服务";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception visionError) when (ocrAvailable && settings.Mode == TranslationMode.Auto)
                {
                    // Vision failed: fall back to local OCR + text translation.
                    pipelineLabel = "本地 OCR";
                    var fallback = await CoreBridge.TranslateScreenshotViaOcrAsync(
                        textApiKey, imageBytes, sourceLang, targetLang, session.SessionId, cancellationToken,
                        textRuntimeSettings);
                    response = fallback.Response;
                    routingReason = $"视觉模型失败（{visionError.Message}），已回退到本地 OCR。";
                    ocrElapsedMs = fallback.OcrElapsedMs;
                    networkElapsedMs = fallback.NetworkElapsedMs;
                }
            }
            else
            {
                session.Stage = TranslationSessionStage.OcrRunning;
                onStageChanged?.Invoke(session.Stage);

                var local = await CoreBridge.TranslateScreenshotViaOcrAsync(
                    textApiKey, imageBytes, sourceLang, targetLang, session.SessionId, cancellationToken,
                    textRuntimeSettings);
                response = local.Response;
                ocrElapsedMs = local.OcrElapsedMs;
                networkElapsedMs = local.NetworkElapsedMs;
            }

            session.PipelineLabel = pipelineLabel;
            session.RoutingReason = routingReason;
            // Record what ACTUALLY happened, never the routing plan.
            session.ImageSentToProvider = imageSentToProvider;
            session.ImageLeftDevice = imageLeftDevice;
            session.ImageUploaded = imageLeftDevice;
            var textLeavesDevice = textRuntimeSettings is not null &&
                !textRuntimeSettings.TargetsLocalRuntime;
            // For OCR routes, only the recognised text may leave the device;
            // for vision routes, use the actual selected vision locality.
            session.OutboundOccurred = imageLeftDevice || textLeavesDevice ||
                (textRuntimeSettings is null && !string.IsNullOrWhiteSpace(textApiKey));

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
                OcrElapsedMs: ocrElapsedMs,
                RoutingElapsedMs: (ulong)routingStopwatch.ElapsedMilliseconds,
                NetworkElapsedMs: networkElapsedMs,
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
