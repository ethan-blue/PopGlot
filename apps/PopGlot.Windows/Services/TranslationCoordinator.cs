using System.Diagnostics;

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
        CancellationToken cancellationToken) =>
        FreeTranslateService.TranslateAsync(source, sourceLang, targetLang, cancellationToken);
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

                TranslationStreamSession streamSession;
                if (textRuntimeSettings is not null &&
                    (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                {
                    streamSession = _executor.StreamTextDraft(
                        textRuntimeSettings,
                        textApiKey ?? string.Empty,
                        trimmed,
                        sourceLang,
                        targetLang,
                        session.SessionId,
                        epoch,
                        cancellationToken);
                }
                else
                {
                    streamSession = _executor.StreamText(
                        textApiKey,
                        trimmed,
                        sourceLang,
                        targetLang,
                        session.SessionId,
                        epoch,
                        cancellationToken);
                }

                response = await PumpStreamAsync(
                    streamSession,
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
                response = await _executor.TranslateFreeAsync(
                    trimmed, sourceLang, targetLang, cancellationToken);

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
                totalStopwatch: totalStopwatch);

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
                catch (Exception visionError) when (visionStreamSession.Buffer.DeltaCount == 0 && ocrAvailable && settings.Mode == TranslationMode.Auto)
                {
                    // Vision failed with zero visible delta, fall back to local OCR
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

                    if (textRuntimeSettings is not null &&
                        (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                    {
                        var textStream = _executor.StreamTextDraft(
                            textRuntimeSettings,
                            textApiKey ?? string.Empty,
                            recognized,
                            sourceLang,
                            targetLang,
                            session.SessionId,
                            epoch,
                            cancellationToken);
                        response = await PumpStreamAsync(
                            textStream,
                            session,
                            epoch,
                            fallbackStartTicks,
                            progress,
                            onStageChanged,
                            cancellationToken);
                    }
                    else if (!string.IsNullOrWhiteSpace(textApiKey) || (textRuntimeSettings?.TargetsLocalRuntime ?? false))
                    {
                        var textStream = _executor.StreamText(
                            textApiKey,
                            recognized,
                            sourceLang,
                            targetLang,
                            session.SessionId,
                            epoch,
                            cancellationToken);
                        response = await PumpStreamAsync(
                            textStream,
                            session,
                            epoch,
                            fallbackStartTicks,
                            progress,
                            onStageChanged,
                            cancellationToken);
                    }
                    else
                    {
                        if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial))
                        {
                            throw new InvalidOperationException(
                                freeDenial is null ? "未允许出网翻译。" : $"{freeDenial.Message} {freeDenial.ActionableSuggestion}".Trim());
                        }

                        response = await _executor.TranslateFreeAsync(recognized, sourceLang, targetLang, cancellationToken);
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
                routingElapsedMs: (ulong)routingStopwatch.ElapsedMilliseconds);

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

        if (textRuntimeSettings is not null &&
            (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
        {
            var textStream = _executor.StreamTextDraft(
                textRuntimeSettings,
                textApiKey ?? string.Empty,
                recognized,
                sourceLang,
                targetLang,
                session.SessionId,
                epoch,
                cancellationToken);
            return await PumpStreamAsync(
                textStream, session, epoch, startTicks, progress, onStageChanged, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(textApiKey) || (textRuntimeSettings?.TargetsLocalRuntime ?? false))
        {
            var textStream = _executor.StreamText(
                textApiKey!,
                recognized,
                sourceLang,
                targetLang,
                session.SessionId,
                epoch,
                cancellationToken);
            return await PumpStreamAsync(
                textStream, session, epoch, startTicks, progress, onStageChanged, cancellationToken);
        }

        if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial))
        {
            throw new InvalidOperationException(
                freeDenial is null ? "未允许出网翻译。" : $"{freeDenial.Message} {freeDenial.ActionableSuggestion}".Trim());
        }

        var response = await _executor.TranslateFreeAsync(recognized, sourceLang, targetLang, cancellationToken);
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

    private static async Task<TranslationResponse> PumpStreamAsync(
        TranslationStreamSession streamSession,
        TranslationSession session,
        long epoch,
        long startTimestampTicks,
        IProgress<TranslationStreamUpdate>? progress,
        Action<TranslationSessionStage>? onStageChanged,
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
                session.TranslatedText = buffer.GetAccumulatedText();
                progress?.Report(new TranslationStreamUpdate(
                    SessionId: session.SessionId,
                    Epoch: epoch,
                    Kind: TranslationStreamUpdateKind.Delta,
                    Delta: delta,
                    AccumulatedText: session.TranslatedText,
                    AccumulatedCharCount: buffer.CharCount,
                    Ttft: buffer.GetTtft(startTimestampTicks),
                    IsPartial: true));
            }

            var delayTask = Task.Delay(40, cancellationToken);
            var finished = await Task.WhenAny(completion, delayTask);
            if (finished == completion)
            {
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
            session.TranslatedText = buffer.GetAccumulatedText();
            progress?.Report(new TranslationStreamUpdate(
                SessionId: session.SessionId,
                Epoch: epoch,
                Kind: TranslationStreamUpdateKind.Delta,
                Delta: finalDelta,
                AccumulatedText: session.TranslatedText,
                AccumulatedCharCount: buffer.CharCount,
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
        ulong routingElapsedMs = 0)
    {
        session.Stage = TranslationSessionStage.Finalizing;
        onStageChanged?.Invoke(session.Stage);

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

        session.Stage = response.Result.Warnings.Count > 0
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
        if (session.IsSuccess && _history is not null && !string.IsNullOrWhiteSpace(session.TranslatedText))
        {
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
            _history.TryAdd(entry, shellSettings.HistoryEnabled);
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
