using System.Diagnostics;
using System.Text;

namespace PopGlot.Windows.Services;

internal interface ITranslationExecutor
{
    ProviderSettings GetSettings();
    (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes();
    ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable);
    string? LoadApiKey(string target);
    bool IsOcrSupported { get; }
    Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default);

    TranslationStreamSession StreamText(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken);

    TranslationStreamSession StreamTextDraft(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken);

    TranslationStreamSession StreamVisionDraft(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken);

    Task<TranslationResponse> TranslateFreeAsync(
        string source,
        string sourceLang,
        string targetLang,
        FreeEngineAuthorization authorization,
        CancellationToken cancellationToken);
}

internal sealed class DefaultTranslationExecutor : ITranslationExecutor
{
    public ProviderSettings GetSettings() => CoreBridge.GetSettings();

    public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() =>
        ProfileManager.ResolveRoutes();

    public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable) =>
        ProfileManager.ResolveRoute(settings, ocrAvailable);

    public string? LoadApiKey(string target) =>
        CredentialStore.LoadApiKey(target);

    public bool IsOcrSupported => WindowsOcrService.IsSupported;

    public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default) =>
        WindowsOcrService.RecognizeTextAsync(imageBytes, sourceLang);

    public TranslationStreamSession StreamText(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken) =>
        CoreBridge.TranslateTextStream(
            apiKey, source, sourceLang, targetLang, sessionId, sessionId, epoch, null, cancellationToken);

    public TranslationStreamSession StreamTextDraft(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken) =>
        CoreBridge.TranslateTextDraftStream(
            draftSettings, apiKey, source, sourceLang, targetLang, sessionId, sessionId, epoch, null, cancellationToken);

    public TranslationStreamSession StreamVisionDraft(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken) =>
        CoreBridge.TranslateVisionDraftStream(
            draftSettings, textApiKey, visionApiKey, image, sourceLang, targetLang, sessionId, sessionId, epoch, null, cancellationToken);

    public Task<TranslationResponse> TranslateFreeAsync(
        string source,
        string sourceLang,
        string targetLang,
        FreeEngineAuthorization authorization,
        CancellationToken cancellationToken) =>
        FreeTranslateService.TranslateAsync(source, sourceLang, targetLang, authorization, cancellationToken);
}

/// <summary>
/// Unified Translation Coordinator that mediates all translation entry points
/// (Selection, Screenshot, Manual, QuickSearch) through an authoritative
/// privacy, routing, error handling, and session tracking lifecycle.
/// </summary>
internal sealed class TranslationCoordinator
{
    private readonly IHistoryRepository? _history;
    private readonly IVocabularyRepository? _vocabulary;
    private readonly ITranslationExecutor _executor;
    private readonly ISettingsService? _settingsService;

    public TranslationCoordinator(
        IHistoryRepository? history = null,
        IVocabularyRepository? vocabulary = null,
        ITranslationExecutor? executor = null,
        ISettingsService? settingsService = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _executor = executor ?? new DefaultTranslationExecutor();
        _settingsService = settingsService;
    }

    public static TranslationCoordinator Instance { get; } = new(new HistoryStore(), new VocabularyStore());

    /// <summary>
    /// The free engine runs the SAME protect → translate → restore contract as
    /// the configured providers: masking goes through the Rust FFI helper (one
    /// regex set, never a C# copy), and dropped, duplicated or unknown
    /// placeholders turn the result Partial with an explicit warning instead
    /// of a silent green success.
    /// </summary>
    private async Task<TranslationResponse> TranslateFreeWithTokenProtectionAsync(
        ProviderSettings settings,
        string source,
        string sourceLang,
        string targetLang,
        FreeEngineAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (!settings.ProtectCodeTokens)
        {
            return await _executor.TranslateFreeAsync(source, sourceLang, targetLang, authorization, cancellationToken);
        }

        var protectedText = CoreBridge.ProtectTokens(source);
        var response = await _executor.TranslateFreeAsync(
            protectedText.SanitizedText, sourceLang, targetLang, authorization, cancellationToken);
        if (protectedText.Tokens.Count == 0)
        {
            return response;
        }

        var restored = CoreBridge.RestoreTokens(response.Result.TranslatedText, protectedText.Tokens);
        var warnings = response.Result.Warnings.ToList();
        if (restored.DroppedTerms.Count > 0)
        {
            warnings.Add($"模型未在译文中保留这些代码元素：{string.Join("、", restored.DroppedTerms)}");
        }
        if (restored.DuplicatedTerms.Count > 0)
        {
            warnings.Add($"这些占位符在译文中重复出现，结果不完整：{string.Join("、", restored.DuplicatedTerms)}");
        }
        if (restored.UnknownPlaceholders.Count > 0)
        {
            warnings.Add($"译文中出现了本请求未发出的占位符：{string.Join("、", restored.UnknownPlaceholders)}");
        }

        return response with
        {
            Result = response.Result with
            {
                TranslatedText = restored.Text,
                ProtectedTerms = protectedText.Tokens.Select(token => token.Original).ToList(),
                Warnings = warnings,
                IsPartial = response.Result.IsPartial || warnings.Count > 0,
            },
        };
    }

    public async Task<TranslationSession> TranslateTextAsync(
        string source,
        string sourceLang,
        string targetLang,
        TranslationInputSource sourceKind,
        CancellationToken cancellationToken = default,
        Action<TranslationSessionStage>? onStageChanged = null,
        IProgress<TranslationStreamUpdate>? progress = null,
        long epoch = 0)
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

            var settings = _executor.GetSettings();
            var (textRoute, _) = _executor.ResolveRoutes();
            // 没有生效的引擎档案（未配置，或快速切换器选了免费引擎）时，
            // 绝不从旧版默认凭据槽取 key：残留 key 会把空模型名的请求
            // 打到 Provider 上然后报「尚未配置文本模型」。旧版单服务安装
            // 由 ProfileManager 合成档案，不依赖这个回退。
            var textApiKey = textRoute is null
                ? null
                : _executor.LoadApiKey(textRoute.CredentialTarget);
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
            var startTimestampTicks = Stopwatch.GetTimestamp();
            TranslationResponse response;

            if (hasConfiguredProvider)
            {
                session.OutboundOccurred = !isLocal;
                session.PipelineLabel = isLocal ? "本地模型" : DescribeProvider(textRuntimeSettings?.ProviderType ?? settings.ProviderType);

                response = await TranslateProviderTextAsync(
                    trimmed,
                    sourceLang,
                    targetLang,
                    textRuntimeSettings,
                    textApiKey,
                    session,
                    epoch,
                    startTimestampTicks,
                    progress,
                    onStageChanged,
                    cancellationToken);
            }
            else
            {
                // The free web engine is an explicit, consented provider —
                // never a silent fallback. OutboundPolicy owns that decision
                // and issues the authorization the send boundary requires.
                if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial, out var freeAuth))
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
                response = await TranslateFreeWithTokenProtectionAsync(
                    settings, trimmed, sourceLang, targetLang, freeAuth!, cancellationToken);

                progress?.Report(new TranslationStreamUpdate(
                    SessionId: session.SessionId,
                    Epoch: epoch,
                    Kind: TranslationStreamUpdateKind.Reset,
                    Delta: string.Empty,
                    AccumulatedText: string.Empty,
                    AccumulatedCharCount: 0));

                progress?.Report(new TranslationStreamUpdate(
                    SessionId: session.SessionId,
                    Epoch: epoch,
                    Kind: TranslationStreamUpdateKind.Delta,
                    Delta: response.Result.TranslatedText,
                    AccumulatedText: response.Result.TranslatedText,
                    AccumulatedCharCount: response.Result.TranslatedText.Length,
                    Ttft: Stopwatch.GetElapsedTime(startTimestampTicks, Stopwatch.GetTimestamp()),
                    IsPartial: true));
            }

            netStopwatch.Stop();

            ApplyFinalResponse(
                session: session,
                response: response,
                sourceKind: sourceKind,
                epoch: epoch,
                progress: progress,
                onStageChanged: onStageChanged,
                networkElapsedMs: (ulong)netStopwatch.ElapsedMilliseconds,
                totalStopwatch: totalStopwatch,
                cancellationToken: cancellationToken);

            return session;
        }
        catch (SegmentSessionException segmentFailure)
        {
            // Some segments translated, a later one failed or came back
            // incomplete: the fragments surface as Partial — visible, gated
            // away from history and every automatic side effect (T03).
            totalStopwatch.Stop();
            ApplyFinalResponse(
                session: session,
                response: segmentFailure.PartialResponse,
                sourceKind: sourceKind,
                epoch: epoch,
                progress: progress,
                onStageChanged: onStageChanged,
                networkElapsedMs: segmentFailure.PartialResponse.Diagnostics.ElapsedMs,
                totalStopwatch: totalStopwatch,
                cancellationToken: cancellationToken);
            return session;
        }
        catch (OperationCanceledException)
        {
            session.Stage = TranslationSessionStage.Cancelled;
            session.Error = new TranslationError(
                TranslationErrorKind.Cancelled,
                "翻译请求已取消。");
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: string.Empty,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: session.TranslatedText.Length,
                IsPartial: true,
                Message: "翻译请求已取消。"));
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
        catch (Exception ex)
        {
            session.Stage = TranslationSessionStage.Failed;
            session.Error = ClassifyException(ex);
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: string.Empty,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: session.TranslatedText.Length,
                IsPartial: true,
                Message: ex.Message));
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
    }

    public async Task<TranslationSession> TranslateScreenshotAsync(
        byte[] imageBytes,
        string sourceLang,
        string targetLang,
        CancellationToken cancellationToken = default,
        Action<TranslationSessionStage>? onStageChanged = null,
        IProgress<TranslationStreamUpdate>? progress = null,
        long epoch = 0)
    {
        var session = new TranslationSession
        {
            InputSource = TranslationInputSource.Screenshot,
            SourceLanguage = sourceLang,
            TargetLanguage = targetLang,
            Stage = TranslationSessionStage.Created,
        };

        var totalStopwatch = Stopwatch.StartNew();
        // Hoisted for the segment-session catch, which routes fragments into
        // ApplyFinalResponse with the real routing/OCR timings.
        var routingStopwatch = Stopwatch.StartNew();
        ulong ocrElapsedMs = 0;

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

            var settings = _executor.GetSettings();
            var ocrAvailable = _executor.IsOcrSupported;
            var route = _executor.ResolveScreenshotRoute(settings, ocrAvailable);
            var textRoute = route.Text;
            var visionRoute = route.Vision;

            var textApiKey = textRoute is null
                ? null
                : _executor.LoadApiKey(textRoute.CredentialTarget);
            var visionApiKey = visionRoute is null
                ? null
                : _executor.LoadApiKey(visionRoute.CredentialTarget);

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
                    "任选其一：安装 Windows OCR 语言包（设置 → 时间和语言 → 语言和区域 → 语言选项 → 光学字符识别）；" +
                    "在服务页配置并启用支持图片的视觉模型；或在「隐私与数据」中允许截图上传。");
                onStageChanged?.Invoke(session.Stage);
                return session;
            }

            ulong networkElapsedMs = 0;
            var imageSentToProvider = false;
            var imageLeftDevice = false;
            TranslationResponse response;
            var pipelineLabel = "本地 OCR";
            var routingReason = route.ExplanationZh;

            if (route.ScreenshotPipeline is ScreenshotPipeline.VisionDirect or ScreenshotPipeline.VisionOcr &&
                visionRoute is not null)
            {
                session.Stage = TranslationSessionStage.Translating;
                onStageChanged?.Invoke(session.Stage);

                if (visionRuntimeSettings is null)
                {
                    throw new InvalidOperationException("所选图片服务没有可执行配置。");
                }

                var visionStreamSession = _executor.StreamVisionDraft(
                    visionRuntimeSettings,
                    string.Empty,
                    visionApiKey ?? string.Empty,
                    imageBytes,
                    sourceLang,
                    targetLang,
                    session.SessionId,
                    epoch,
                    cancellationToken);

                try
                {
                    imageSentToProvider = true;
                    imageLeftDevice = !visionRuntimeSettings.TargetsLocalRuntime;
                    var startTicks = Stopwatch.GetTimestamp();
                    var netStopwatch = Stopwatch.StartNew();

                    response = await PumpStreamAsync(
                        visionStreamSession,
                        session,
                        epoch,
                        startTicks,
                        progress,
                        onStageChanged,
                        textPrefix: string.Empty,
                        cancellationToken);

                    netStopwatch.Stop();
                    networkElapsedMs = (ulong)netStopwatch.ElapsedMilliseconds;
                    pipelineLabel = visionRuntimeSettings.TargetsLocalRuntime
                        ? "本地视觉模型"
                        : "视觉模型 · 独立服务";

                    // 两段式：视觉模型只取它的 transcription（识别原文），
                    // 译文交给文本模型流式生成。视觉调用自己的译文被丢弃。
                    if (route.ScreenshotPipeline == ScreenshotPipeline.VisionOcr)
                    {
                        var recognized = response.Result.Transcription?.Trim();
                        if (string.IsNullOrWhiteSpace(recognized))
                        {
                            throw new InvalidOperationException(
                                "视觉模型没有返回识别文字，无法交给文本模型翻译。可改用「视觉模型直译」，或换识别更稳的图片模型。");
                        }

                        progress?.Report(new TranslationStreamUpdate(
                            SessionId: session.SessionId,
                            Epoch: epoch,
                            Kind: TranslationStreamUpdateKind.Reset,
                            Delta: string.Empty,
                            AccumulatedText: string.Empty,
                            AccumulatedCharCount: 0));

                        session.SourceText = recognized;
                        session.Transcription = recognized;
                        session.Stage = TranslationSessionStage.Translating;
                        onStageChanged?.Invoke(session.Stage);

                        var textStopwatch = Stopwatch.StartNew();
                        response = await TranslateRecognizedTextAsync(
                            session,
                            recognized,
                            sourceLang,
                            targetLang,
                            settings,
                            textRuntimeSettings,
                            textApiKey,
                            epoch,
                            progress,
                            onStageChanged,
                            cancellationToken);
                        textStopwatch.Stop();
                        networkElapsedMs += (ulong)textStopwatch.ElapsedMilliseconds;
                        pipelineLabel = visionRuntimeSettings.TargetsLocalRuntime
                            ? "本地视觉识别 + 文本模型"
                            : "视觉识别 + 文本模型";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception visionError) when (
                    visionError is not SegmentSessionException &&
                    visionStreamSession.Buffer.DeltaCount == 0 && ocrAvailable && settings.Mode == TranslationMode.Auto)
                {
                    // Vision failed with zero visible delta, fall back to local
                    // OCR. A TEXT-phase segment failure never re-runs OCR: its
                    // fragments must stay visible as Partial instead.
                    progress?.Report(new TranslationStreamUpdate(
                        SessionId: session.SessionId,
                        Epoch: epoch,
                        Kind: TranslationStreamUpdateKind.Reset,
                        Delta: string.Empty,
                        AccumulatedText: string.Empty,
                        AccumulatedCharCount: 0));

                    session.Stage = TranslationSessionStage.OcrRunning;
                    onStageChanged?.Invoke(session.Stage);

                    var ocrStopwatch = Stopwatch.StartNew();
                    var recognized = await _executor.RecognizeOcrTextAsync(imageBytes, sourceLang, cancellationToken);
                    ocrStopwatch.Stop();
                    ocrElapsedMs = (ulong)ocrStopwatch.ElapsedMilliseconds;

                    if (string.IsNullOrWhiteSpace(recognized))
                    {
                        throw new InvalidOperationException(
                            "本地 OCR 未能在所选区域识别到文字。请重新框选更清晰的区域，或在设置中开启截图上传以使用视觉模型。");
                    }

                    session.SourceText = recognized;
                    session.Transcription = recognized;
                    pipelineLabel = "本地 OCR";
                    routingReason = $"视觉模型失败（{visionError.Message}），已回退到本地 OCR。";

                    session.Stage = TranslationSessionStage.Translating;
                    onStageChanged?.Invoke(session.Stage);

                    var fallbackNetStopwatch = Stopwatch.StartNew();
                    var fallbackStartTicks = Stopwatch.GetTimestamp();

                    if ((textRuntimeSettings is not null &&
                            (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                        || !string.IsNullOrWhiteSpace(textApiKey))
                    {
                        response = await TranslateProviderTextAsync(
                            recognized,
                            sourceLang,
                            targetLang,
                            textRuntimeSettings,
                            textApiKey,
                            session,
                            epoch,
                            fallbackStartTicks,
                            progress,
                            onStageChanged,
                            cancellationToken);
                    }
                    else
                    {
                        if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial, out var freeAuth))
                        {
                            throw new InvalidOperationException(
                                freeDenial is null ? "未允许出网翻译。" : $"{freeDenial.Message} {freeDenial.ActionableSuggestion}".Trim());
                        }

                        response = await TranslateFreeWithTokenProtectionAsync(
                            settings, recognized, sourceLang, targetLang, freeAuth!, cancellationToken);
                        progress?.Report(new TranslationStreamUpdate(
                            SessionId: session.SessionId,
                            Epoch: epoch,
                            Kind: TranslationStreamUpdateKind.Reset,
                            Delta: string.Empty,
                            AccumulatedText: string.Empty,
                            AccumulatedCharCount: 0));
                        progress?.Report(new TranslationStreamUpdate(
                            SessionId: session.SessionId,
                            Epoch: epoch,
                            Kind: TranslationStreamUpdateKind.Delta,
                            Delta: response.Result.TranslatedText,
                            AccumulatedText: response.Result.TranslatedText,
                            AccumulatedCharCount: response.Result.TranslatedText.Length,
                            Ttft: Stopwatch.GetElapsedTime(fallbackStartTicks, Stopwatch.GetTimestamp()),
                            IsPartial: true));
                    }

                    fallbackNetStopwatch.Stop();
                    networkElapsedMs = (ulong)fallbackNetStopwatch.ElapsedMilliseconds;
                }
            }
            else
            {
                session.Stage = TranslationSessionStage.OcrRunning;
                onStageChanged?.Invoke(session.Stage);

                var ocrStopwatch = Stopwatch.StartNew();
                var recognized = await _executor.RecognizeOcrTextAsync(imageBytes, sourceLang, cancellationToken);
                ocrStopwatch.Stop();
                ocrElapsedMs = (ulong)ocrStopwatch.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(recognized))
                {
                    throw new InvalidOperationException(
                        "本地 OCR 未能在所选区域识别到文字。请重新框选更清晰的区域，或在设置中开启截图上传以使用视觉模型。");
                }

                session.SourceText = recognized;
                session.Transcription = recognized;

                session.Stage = TranslationSessionStage.Translating;
                onStageChanged?.Invoke(session.Stage);

                var localNetStopwatch = Stopwatch.StartNew();

                response = await TranslateRecognizedTextAsync(
                    session,
                    recognized,
                    sourceLang,
                    targetLang,
                    settings,
                    textRuntimeSettings,
                    textApiKey,
                    epoch,
                    progress,
                    onStageChanged,
                    cancellationToken);

                localNetStopwatch.Stop();
                networkElapsedMs = (ulong)localNetStopwatch.ElapsedMilliseconds;
            }

            session.PipelineLabel = pipelineLabel;
            session.RoutingReason = routingReason;
            session.ImageSentToProvider = imageSentToProvider;
            session.ImageLeftDevice = imageLeftDevice;
            session.ImageUploaded = imageLeftDevice;
            var textLeavesDevice = textRuntimeSettings is not null && !textRuntimeSettings.TargetsLocalRuntime;
            session.OutboundOccurred = imageLeftDevice || textLeavesDevice ||
                (textRuntimeSettings is null && !string.IsNullOrWhiteSpace(textApiKey));

            ApplyFinalResponse(
                session: session,
                response: response,
                sourceKind: TranslationInputSource.Screenshot,
                epoch: epoch,
                progress: progress,
                onStageChanged: onStageChanged,
                networkElapsedMs: networkElapsedMs,
                totalStopwatch: totalStopwatch,
                ocrElapsedMs: ocrElapsedMs,
                routingElapsedMs: (ulong)routingStopwatch.ElapsedMilliseconds,
                cancellationToken: cancellationToken);

            return session;
        }
        catch (SegmentSessionException segmentFailure)
        {
            totalStopwatch.Stop();
            ApplyFinalResponse(
                session: session,
                response: segmentFailure.PartialResponse,
                sourceKind: TranslationInputSource.Screenshot,
                epoch: epoch,
                progress: progress,
                onStageChanged: onStageChanged,
                networkElapsedMs: segmentFailure.PartialResponse.Diagnostics.ElapsedMs,
                totalStopwatch: totalStopwatch,
                ocrElapsedMs: ocrElapsedMs,
                routingElapsedMs: (ulong)routingStopwatch.ElapsedMilliseconds,
                cancellationToken: cancellationToken);
            return session;
        }
        catch (OperationCanceledException)
        {
            session.Stage = TranslationSessionStage.Cancelled;
            session.Error = new TranslationError(
                TranslationErrorKind.Cancelled,
                "截图翻译已取消。");
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: string.Empty,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: session.TranslatedText.Length,
                IsPartial: true,
                Message: "截图翻译已取消。"));
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
        catch (Exception ex)
        {
            session.Stage = TranslationSessionStage.Failed;
            session.Error = ClassifyException(ex);
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: string.Empty,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: session.TranslatedText.Length,
                IsPartial: true,
                Message: ex.Message));
            onStageChanged?.Invoke(session.Stage);
            return session;
        }
    }

    /// <summary>
    /// 把已识别的截图文字送入统一文字翻译线路：优先文本服务草稿流，其次
    /// 全局文本设置，最后按授权使用内置免费引擎。本地 OCR 与视觉识别两
    /// 条管线在此汇合，避免两份几乎相同的分支漂移。
    /// </summary>
    private async Task<TranslationResponse> TranslateRecognizedTextAsync(
        TranslationSession session,
        string recognized,
        string sourceLang,
        string targetLang,
        ProviderSettings settings,
        ProviderSettings? textRuntimeSettings,
        string? textApiKey,
        long epoch,
        IProgress<TranslationStreamUpdate>? progress,
        Action<TranslationSessionStage>? onStageChanged,
        CancellationToken cancellationToken)
    {
        var startTicks = Stopwatch.GetTimestamp();

        if ((textRuntimeSettings is not null &&
                (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
            || !string.IsNullOrWhiteSpace(textApiKey) || (textRuntimeSettings?.TargetsLocalRuntime ?? false))
        {
            return await TranslateProviderTextAsync(
                recognized,
                sourceLang,
                targetLang,
                textRuntimeSettings,
                textApiKey,
                session,
                epoch,
                startTicks,
                progress,
                onStageChanged,
                cancellationToken);
        }

        if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial, out var freeAuth))
        {
            throw new InvalidOperationException(
                freeDenial is null ? "未允许出网翻译。" : $"{freeDenial.Message} {freeDenial.ActionableSuggestion}".Trim());
        }

        var response = await TranslateFreeWithTokenProtectionAsync(
            settings, recognized, sourceLang, targetLang, freeAuth!, cancellationToken);
        progress?.Report(new TranslationStreamUpdate(
            SessionId: session.SessionId,
            Epoch: epoch,
            Kind: TranslationStreamUpdateKind.Reset,
            Delta: string.Empty,
            AccumulatedText: string.Empty,
            AccumulatedCharCount: 0));
        progress?.Report(new TranslationStreamUpdate(
            SessionId: session.SessionId,
            Epoch: epoch,
            Kind: TranslationStreamUpdateKind.Delta,
            Delta: response.Result.TranslatedText,
            AccumulatedText: response.Result.TranslatedText,
            AccumulatedCharCount: response.Result.TranslatedText.Length,
            Ttft: Stopwatch.GetElapsedTime(startTicks, Stopwatch.GetTimestamp()),
            IsPartial: true));
        return response;
    }

    /// <summary>
    /// Total wall-clock budget for ONE translation session across all of its
    /// segments. Each request keeps its own internal timeout; this deadline
    /// only prevents 8 sequential requests from extending the session
    /// indefinitely. Expiry is surfaced like a user cancellation.
    /// </summary>
    internal static readonly TimeSpan SessionDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Translates provider text with an output budget that matches the input:
    /// short sources stay one request; long sources are planned into ordered
    /// ≤800-char segments by the Rust planner and translated sequentially,
    /// with fragments visible if any segment fails. A rejected plan fails the
    /// session BEFORE anything is sent.
    /// </summary>
    private async Task<TranslationResponse> TranslateProviderTextAsync(
        string source,
        string sourceLang,
        string targetLang,
        ProviderSettings? textRuntimeSettings,
        string? textApiKey,
        TranslationSession session,
        long epoch,
        long startTimestampTicks,
        IProgress<TranslationStreamUpdate>? progress,
        Action<TranslationSessionStage>? onStageChanged,
        CancellationToken cancellationToken)
    {
        var plan = CoreBridge.PlanSegments(source);
        if (plan.RejectedReason is not null)
        {
            throw new InvalidOperationException(
                plan.RejectedReason == "oversized_code_block"
                    ? "内容里有一个超过单段预算的代码块，无法安全切分。请缩短该代码块后重试。"
                    : $"内容超出一次会话的翻译预算（最多 {CoreBridge.MaxSegments} 段、每段 {CoreBridge.MaxSegmentChars} 字符）。请缩短或分批提交。");
        }

        var segments = plan.Mode == "segments"
            ? plan.Segments ?? [source]
            : new[] { source };

        // One request behaves exactly as before.
        if (segments.Count == 1)
        {
            var single = CreateTextStream(
                segments[0], sourceLang, targetLang, textRuntimeSettings, textApiKey,
                session.SessionId, epoch, cancellationToken);
            return await PumpStreamAsync(
                single, session, epoch, startTimestampTicks, progress, onStageChanged,
                textPrefix: string.Empty, cancellationToken);
        }

        // Sequential segments under ONE deadline and cancellation token.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        sessionCts.CancelAfter(SessionDeadline);

        var merged = new StringBuilder();
        var explanations = new List<string>();
        var warnings = new List<string>();
        ulong networkMs = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            sessionCts.Token.ThrowIfCancellationRequested();
            var segmentTicks = Stopwatch.GetTimestamp();
            var streamSession = CreateTextStream(
                segments[index], sourceLang, targetLang, textRuntimeSettings, textApiKey,
                session.SessionId, epoch, sessionCts.Token);

            TranslationResponse segmentResponse;
            try
            {
                segmentResponse = await PumpStreamAsync(
                    streamSession, session, epoch, segmentTicks, progress, onStageChanged,
                    textPrefix: merged.ToString(), sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Fragments already translated stay visible; the outer cancel
                // handler reports them.
                session.TranslatedText = merged.ToString();
                throw;
            }
            catch (Exception)
            {
                // Completed fragments must remain visible as a Partial, never
                // vanish into a Failed-with-no-body state.
                session.TranslatedText = merged.ToString();
                if (merged.Length > 0)
                {
                    warnings.Add($"第 {index + 1} 段翻译失败，仅保留已完成片段。");
                    throw new SegmentSessionException(
                        BuildSegmentResponse(merged, explanations, warnings, networkMs));
                }
                throw;
            }

            var result = segmentResponse.Result;
            networkMs += segmentResponse.Diagnostics.ElapsedMs;
            var segmentIncomplete =
                result.Warnings.Count > 0 || result.IsPartial || string.IsNullOrWhiteSpace(result.TranslatedText);
            if (segmentIncomplete)
            {
                warnings.Add($"第 {index + 1} 段未完整返回。");
            }
            if (!string.IsNullOrWhiteSpace(result.Explanation))
            {
                explanations.Add(result.Explanation.Trim());
            }
            warnings.AddRange(result.Warnings);

            var segmentText = !string.IsNullOrWhiteSpace(result.TranslatedText)
                ? result.TranslatedText
                : streamSession.Buffer.GetAccumulatedText();
            merged.Append(segmentText);
            session.TranslatedText = merged.ToString();

            // A visibly incomplete segment ends the session as Partial: the
            // following segments would glue onto an unreliable fragment.
            if (segmentIncomplete)
            {
                throw new SegmentSessionException(
                    BuildSegmentResponse(merged, explanations, warnings, networkMs));
            }
        }

        return BuildSegmentResponse(merged, explanations, warnings, networkMs);
    }

    private TranslationStreamSession CreateTextStream(
        string segment,
        string sourceLang,
        string targetLang,
        ProviderSettings? textRuntimeSettings,
        string? textApiKey,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken)
    {
        if (textRuntimeSettings is not null &&
            (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
        {
            return _executor.StreamTextDraft(
                textRuntimeSettings,
                textApiKey ?? string.Empty,
                segment,
                sourceLang,
                targetLang,
                sessionId,
                epoch,
                cancellationToken);
        }
        return _executor.StreamText(
            textApiKey,
            segment,
            sourceLang,
            targetLang,
            sessionId,
            epoch,
            cancellationToken);
    }

    private static TranslationResponse BuildSegmentResponse(
        StringBuilder merged,
        IReadOnlyList<string> explanations,
        IReadOnlyList<string> warnings,
        ulong networkMs) =>
        new(
            new TranslationResult(
                TranslatedText: merged.ToString(),
                Transcription: string.Empty,
                Explanation: string.Join("\n\n", explanations),
                ProtectedTerms: [],
                Warnings: warnings),
            new ProviderDiagnostics(
                RequestId: "segmented",
                ProviderType: ProviderType.OpenAiCompatible,
                Endpoint: string.Empty,
                Attempts: 1,
                StatusCode: 200,
                ElapsedMs: networkMs));

    /// <summary>
    /// Some segments completed and one failed or returned incomplete: the
    /// fragments travel back to <see cref="ApplyFinalResponse"/> inside this
    /// response so they surface as Partial (visible, never persisted) instead
    /// of a body-less Failed.
    /// </summary>
    private sealed class SegmentSessionException(TranslationResponse partialResponse)
        : Exception("会话分段翻译未全部完成。")
    {
        public TranslationResponse PartialResponse { get; } = partialResponse;
    }

    private static async Task<TranslationResponse> PumpStreamAsync(
        TranslationStreamSession streamSession,
        TranslationSession session,
        long epoch,
        long startTimestampTicks,
        IProgress<TranslationStreamUpdate>? progress,
        Action<TranslationSessionStage>? onStageChanged,
        string textPrefix,
        CancellationToken cancellationToken)
    {
        var buffer = streamSession.Buffer;
        var completion = streamSession.Completion;

        while (!completion.IsCompleted)
        {
            if (buffer.TryDrain(out var delta) && !string.IsNullOrEmpty(delta))
            {
                if (session.Stage != TranslationSessionStage.Streaming)
                {
                    session.Stage = TranslationSessionStage.Streaming;
                    onStageChanged?.Invoke(session.Stage);
                }
                session.TranslatedText = textPrefix + buffer.GetAccumulatedText();
                progress?.Report(new TranslationStreamUpdate(
                    SessionId: session.SessionId,
                    Epoch: epoch,
                    Kind: TranslationStreamUpdateKind.Delta,
                    Delta: delta,
                    AccumulatedText: session.TranslatedText,
                    AccumulatedCharCount: session.TranslatedText.Length,
                    Ttft: buffer.GetTtft(startTimestampTicks),
                    IsPartial: true));
            }

            var delayTask = Task.Delay(40, cancellationToken);
            var finished = await Task.WhenAny(completion, delayTask);
            if (finished == completion)
            {
                break;
            }
            if (delayTask.IsCanceled)
            {
                // A cancelled delay completes instantly, so looping on it
                // would hot-spin a thread-pool thread until the native abort
                // lands. Wait one plain tick for the abort, then stop pumping;
                // post-cancel deltas are discarded by epoch fencing anyway.
                await Task.WhenAny(completion, Task.Delay(40));
                break;
            }
        }

        if (buffer.TryDrain(out var finalDelta) && !string.IsNullOrEmpty(finalDelta))
        {
            if (session.Stage != TranslationSessionStage.Streaming)
            {
                session.Stage = TranslationSessionStage.Streaming;
                onStageChanged?.Invoke(session.Stage);
            }
            session.TranslatedText = textPrefix + buffer.GetAccumulatedText();
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: finalDelta,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: session.TranslatedText.Length,
                Ttft: buffer.GetTtft(startTimestampTicks),
                IsPartial: true));
        }

        return await completion;
    }

    private void ApplyFinalResponse(
        TranslationSession session,
        TranslationResponse response,
        TranslationInputSource sourceKind,
        long epoch,
        IProgress<TranslationStreamUpdate>? progress,
        Action<TranslationSessionStage>? onStageChanged,
        ulong networkElapsedMs,
        Stopwatch totalStopwatch,
        ulong ocrElapsedMs = 0,
        ulong routingElapsedMs = 0,
        CancellationToken cancellationToken = default)
    {
        session.Stage = TranslationSessionStage.Finalizing;
        onStageChanged?.Invoke(session.Stage);

        // A final that arrives after the user cancelled (an executor may
        // return a complete response instead of throwing) is NOT a completion:
        // the text stays visible but the session lands on Cancelled and
        // nothing persists (T19/F03).
        if (cancellationToken.IsCancellationRequested)
        {
            // Populate text so partially-streamed content is replaced with the
            // complete response — the user can still read and copy it.
            session.TranslatedText = response.Result.TranslatedText;
            session.Stage = TranslationSessionStage.Cancelled;
            session.Error = new TranslationError(
                TranslationErrorKind.Cancelled,
                "翻译请求已取消。");
            onStageChanged?.Invoke(session.Stage);
            return; // WriteHistoryOnce is never reached → nothing persists.
        }

        session.TranslatedText = response.Result.TranslatedText;
        if (!string.IsNullOrEmpty(response.Result.Transcription))
        {
            session.Transcription = response.Result.Transcription;
        }
        if (string.IsNullOrEmpty(session.SourceText) && !string.IsNullOrEmpty(session.Transcription))
        {
            session.SourceText = session.Transcription;
        }

        session.Explanation = response.Result.Explanation;
        session.Phonetic = response.Result.Phonetic;
        session.ProtectedTerms = response.Result.ProtectedTerms;
        session.Warnings = response.Result.Warnings;

        // Integrity comes from the provider contract, not just warnings: an
        // is_partial final, integrity warnings, or an empty translation are
        // all incomplete states. They stay visible but never qualify.
        var incomplete =
            response.Result.Warnings.Count > 0 ||
            response.Result.IsPartial ||
            string.IsNullOrWhiteSpace(response.Result.TranslatedText);
        session.Stage = incomplete
            ? TranslationSessionStage.Partial
            : TranslationSessionStage.Completed;

        totalStopwatch.Stop();
        session.Timing = new TranslationSessionTiming(
            OcrElapsedMs: ocrElapsedMs,
            RoutingElapsedMs: routingElapsedMs,
            NetworkElapsedMs: networkElapsedMs > 0 ? networkElapsedMs : response.Diagnostics.ElapsedMs,
            TotalElapsedMs: (ulong)totalStopwatch.ElapsedMilliseconds);
        session.CompletedAt = DateTimeOffset.UtcNow;

        onStageChanged?.Invoke(session.Stage);

        WriteHistoryOnce(session, sourceKind);
    }

    private void WriteHistoryOnce(TranslationSession session, TranslationInputSource sourceKind)
    {
        // Eligibility, not "a result arrived": integrity warnings, an
        // is_partial final, an empty translation, cancellations and failures
        // never enter history. At most one write per session, so a duplicated
        // final delivery cannot persist twice.
        if (!session.IsCleanCompletion || _history is null || session.HistoryCommitted)
        {
            return;
        }

        if (sourceKind == TranslationInputSource.Screenshot && string.IsNullOrWhiteSpace(session.SourceText))
        {
            return;
        }

        var shellSettings = _settingsService?.GetShellSettings() ?? ShellSettingsStore.Load();
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
        var addResult = _history.TryAdd(entry, shellSettings.HistoryEnabled);
        // "Committed" means the store actually accepted the entry — a failed
        // write must not let the session claim a save that never happened.
        if (addResult == HistoryAddResult.Stored)
        {
            session.HistoryCommitted = true;
        }
        else if (addResult == HistoryAddResult.Failed)
        {
            session.Warnings = [.. session.Warnings, "译文未保存到本机历史：写入失败。"];
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
