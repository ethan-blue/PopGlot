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

    /// <summary>
    /// 会话级显式锚点版本：同一会话的所有分段/重试必须传同一
    /// <see cref="PromptAnchorSnapshot"/>。默认实现转发旧链路（null 锚点 →
    /// Rust 在请求起点自行解析），既有执行器无需改动即可保持编译与行为。
    /// </summary>
    TranslationStreamSession StreamText(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken,
        PromptAnchorSnapshot? preferenceAnchor) =>
        StreamText(apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);

    TranslationStreamSession StreamTextDraft(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken);

    /// <summary>See <see cref="StreamText(string?, string, string, string, string, long, CancellationToken, PromptAnchorSnapshot?)"/>.</summary>
    TranslationStreamSession StreamTextDraft(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken,
        PromptAnchorSnapshot? preferenceAnchor) =>
        StreamTextDraft(draftSettings, apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);

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

    public TranslationStreamSession StreamText(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken,
        PromptAnchorSnapshot? preferenceAnchor) =>
        CoreBridge.TranslateTextStream(
            apiKey, source, sourceLang, targetLang, sessionId, sessionId, epoch, null, cancellationToken,
            preferenceAnchor);

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

    public TranslationStreamSession StreamTextDraft(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string sessionId,
        long epoch,
        CancellationToken cancellationToken,
        PromptAnchorSnapshot? preferenceAnchor) =>
        CoreBridge.TranslateTextDraftStream(
            draftSettings, apiKey, source, sourceLang, targetLang, sessionId, sessionId, epoch, null, cancellationToken,
            preferenceAnchor);

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
    /// 文字线路判定的唯一事实来源：coordinator 的真实路由与 UI 的
    /// <c>TranslationStyleMenu.ProbeNextTextRoute</c> 共用同一个谓词，两者
    /// 不可能漂移。遗留的全局 <see cref="ProviderSettings.TargetsLocalRuntime"/>
    /// 必须保留：旧版单服务安装没有引擎档案、但全局设置指向本机运行时
    /// （Ollama/LM Studio），请求仍会走本地模型——若把这条线路误判为
    /// 内置免费引擎，风格选择器就会被错误禁用。
    /// </summary>
    internal static (bool HasConfiguredProvider, bool IsLocal) ResolveTextProviderCapability(
        ProviderSettings? textRuntimeSettings,
        string? textApiKey,
        ProviderSettings legacySettings)
    {
        var isLocal = textRuntimeSettings?.TargetsLocalRuntime ?? legacySettings.TargetsLocalRuntime;
        var hasConfiguredProvider = (textRuntimeSettings is not null &&
            (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
            || !string.IsNullOrWhiteSpace(textApiKey)
            || isLocal;
        return (hasConfiguredProvider, isLocal);
    }

    /// <summary>
    /// 一个顶层翻译会话的 prompt 快照：身份（进入历史标注）+ 编译锚点（进入
    /// 本会话每一个 provider 请求）。Anchor 为 null 表示沿用旧默认 —— faithful
    /// 或编译失败时由 Rust 在请求起点自行解析，请求字节与旧链路完全一致。
    /// </summary>
    private sealed record PromptSessionSnapshot(
        string Id,
        string Name,
        ulong Revision,
        PromptAnchorSnapshot? Anchor);

    /// <summary>
    /// One identity+anchor snapshot of the active prompt template (local config
    /// read + local pure compile — zero network). Called exactly once per
    /// session at start, so an in-flight session keeps the style it began with
    /// even if the user switches templates afterwards. Returns null when no
    /// usable identity exists; labeling must never fail a translation.
    /// </summary>
    private static PromptSessionSnapshot? BuildPromptSessionSnapshot(
        string? sourceLang,
        string? targetLang)
    {
        try
        {
            var template = CoreBridge.GetActivePromptTemplate();
            if (template is null || string.IsNullOrWhiteSpace(template.Id))
            {
                return null;
            }
            // 会话起点一次性本地编译（Rust CompilePrompt 纯函数）：偏好正文由
            // Rust 生成，C# 绝不代传编辑器正文。faithful 不编译，锚点保持
            // null 与旧默认字节级等价。
            var anchor = CoreBridge.CompilePromptAnchor(template, sourceLang, targetLang);
            return new PromptSessionSnapshot(template.Id, template.Name, template.Revision, anchor);
        }
        catch (Exception)
        {
            // Bridge unavailable or core not initialized (e.g. tests):
            // provenance is best-effort metadata only.
            return null;
        }
    }

    /// <summary>
    /// Freezes the request-start template snapshot onto the session at most
    /// once and returns it: every provider segment and retry of this session
    /// must carry the SAME anchor, so a mid-flight template switch can never
    /// relabel or restyle the in-flight work. The free engine (no prompt of
    /// its own, source text never augmented) never reaches this method and
    /// stays metadata-free.
    /// </summary>
    private static PromptSessionSnapshot? AttachPromptTemplateSnapshot(
        TranslationSession session,
        string? sourceLang,
        string? targetLang)
    {
        if (session.PromptTemplateId is not null)
        {
            // already snapshotted for this session — switching mid-flight must not re-label it
            return null;
        }
        var snapshot = BuildPromptSessionSnapshot(sourceLang, targetLang);
        if (snapshot is null)
        {
            return null;
        }
        session.PromptTemplateId = snapshot.Id;
        session.PromptTemplateName = snapshot.Name;
        session.PromptTemplateRevision = snapshot.Revision;
        return snapshot;
    }

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
        // A10: a fused app refuses new work instead of half-starting it.
        if (!RuntimeGate.NewWorkAllowed)
        {
            return new TranslationSession
            {
                InputSource = sourceKind,
                SourceText = source ?? string.Empty,
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(TranslationErrorKind.Unknown, RuntimeGate.RefusalZh, null),
            };
        }
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
            // 与 UI 侧 ProbeNextTextRoute 共用同一判定（含遗留
            // TargetsLocalRuntime 回退），两边永远一致。
            var (hasConfiguredProvider, isLocal) =
                ResolveTextProviderCapability(textRuntimeSettings, textApiKey, settings);

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
                // Request start: freeze the active template identity AND its
                // compiled anchor once, so every segment/retry of this session
                // and the final history entry carry the style this session
                // actually began with — a mid-flight template switch can
                // neither re-label nor restyle the in-flight work. The
                // free-engine branch below deliberately keeps the metadata
                // null and never compiles an anchor.
                var promptSnapshot = AttachPromptTemplateSnapshot(session, sourceLang, targetLang);

                session.OutboundOccurred = !isLocal;
                session.PipelineKind = isLocal
                    ? TranslationPipelineKind.LocalModel
                    : TranslationPipelineKind.UserProvider;
                session.TextExecutor = isLocal
                    ? TranslationTextExecutor.LocalModel
                    : TranslationTextExecutor.UserProvider;
                // snapshot 非 null 即已冻结身份并编译锚点；null 且首次快照失败
                // 视为 Unknown，诚实不承诺。
                session.PromptSupport = promptSnapshot is not null || session.PromptTemplateId is not null
                    ? TranslationPromptSupport.Applied
                    : TranslationPromptSupport.Unknown;
                session.PipelineLabel = isLocal ? EngineWording.LocalModelName : DescribeProvider(textRuntimeSettings?.ProviderType ?? settings.ProviderType);

                response = await TranslateProviderTextAsync(
                    trimmed,
                    sourceLang,
                    targetLang,
                    textRuntimeSettings,
                    textApiKey,
                    session,
                    epoch,
                    startTimestampTicks,
                    promptSnapshot,
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
                session.PipelineKind = TranslationPipelineKind.FreeEngine;
                session.TextExecutor = TranslationTextExecutor.FreeEngine;
                // 免费引擎没有 prompt 通道，外壳也绝不拼接风格文本：如实记录
                // 自定义风格未应用（OCR+免费文字同一事实，见下）。
                session.PromptSupport = TranslationPromptSupport.NotSupported;
                session.PipelineLabel = EngineWording.FreeEngineName;
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

    public async Task<TranslationResponse> RunTextTaskAsync(
        string source,
        string sourceLang,
        string targetLang,
        TextTaskKind task,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeGate.NewWorkAllowed)
        {
            throw new InvalidOperationException(RuntimeGate.RefusalZh);
        }
        var settings = _executor.GetSettings();
        var routes = _executor.ResolveRoutes();
        var textRoute = routes.Text;
        var apiKey = textRoute is null ? null : _executor.LoadApiKey(textRoute.CredentialTarget);
        if (textRoute is null && !settings.TargetsLocalRuntime)
        {
            throw new InvalidOperationException("请先在设置中配置模型引擎，再使用总结或快速解释。");
        }
        var routeSettings = textRoute?.Profile.ToProviderSettings(settings);
        return await CoreBridge.RunTextTaskAsync(
            apiKey, source, sourceLang, targetLang, task, cancellationToken, routeSettings);
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
        // A10: a fused app refuses new work instead of half-starting it.
        if (!RuntimeGate.NewWorkAllowed)
        {
            return new TranslationSession
            {
                InputSource = TranslationInputSource.Screenshot,
                Stage = TranslationSessionStage.Failed,
                Error = new TranslationError(TranslationErrorKind.Unknown, RuntimeGate.RefusalZh, null),
            };
        }

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
            var pipelineLabel = EngineWording.LocalOcrStepName;
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

                    // 视觉直译：图片连指令一起交给视觉模型，文本阶段不存在，
                    // 自定义 prompt 无从参与（VisionOcr 两段式在下方改写）。
                    session.PipelineKind = TranslationPipelineKind.VisionDirect;
                    session.TextExecutor = TranslationTextExecutor.None;
                    session.PromptSupport = TranslationPromptSupport.NotApplicable;

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
                    pipelineLabel = EngineWording.LocalOcrStepName;
                    routingReason = $"视觉模型失败（{visionError.Message}），已回退到本地 OCR。";

                    session.Stage = TranslationSessionStage.Translating;
                    onStageChanged?.Invoke(session.Stage);

                    var fallbackNetStopwatch = Stopwatch.StartNew();
                    var fallbackStartTicks = Stopwatch.GetTimestamp();

                    if ((textRuntimeSettings is not null &&
                            (textRuntimeSettings.TextIsConfigured || textRuntimeSettings.TargetsLocalRuntime))
                        || !string.IsNullOrWhiteSpace(textApiKey))
                    {
                        // Text-phase request start: freeze identity + compiled
                        // anchor once, shared by every segment/retry below.
                        var promptSnapshot = AttachPromptTemplateSnapshot(session, sourceLang, targetLang);

                        MarkUserTextStage(session, textRuntimeSettings?.TargetsLocalRuntime ?? false, promptSnapshot);

                        response = await TranslateProviderTextAsync(
                            recognized,
                            sourceLang,
                            targetLang,
                            textRuntimeSettings,
                            textApiKey,
                            session,
                            epoch,
                            fallbackStartTicks,
                            promptSnapshot,
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

                        // 视觉失败回退 → 本地 OCR → 免费引擎：文本阶段风格未应用。
                        MarkFreeTextStage(session);

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
            // Request start of the text phase: one identity + anchor snapshot
            // for this session, carried by every segment/retry below; the
            // free-engine fallback below stays metadata-free and never
            // compiles an anchor.
            var promptSnapshot = AttachPromptTemplateSnapshot(session, sourceLang, targetLang);

            MarkUserTextStage(session, textRuntimeSettings?.TargetsLocalRuntime ?? false, promptSnapshot);

            return await TranslateProviderTextAsync(
                recognized,
                sourceLang,
                targetLang,
                textRuntimeSettings,
                textApiKey,
                session,
                epoch,
                startTicks,
                promptSnapshot,
                progress,
                onStageChanged,
                cancellationToken);
        }

        if (!OutboundPolicy.AllowsFreeEngine(settings, out var freeDenial, out var freeAuth))
        {
            throw new InvalidOperationException(
                freeDenial is null ? "未允许出网翻译。" : $"{freeDenial.Message} {freeDenial.ActionableSuggestion}".Trim());
        }

        // OCR（本地或视觉识别）+ 免费文字：诚实记录 —— 文本阶段未应用自定义风格。
        MarkFreeTextStage(session);

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
    /// Typed 路由标记（识别文字 → 用户文本通道，含本地运行时）：
    /// UI 据此分支，不再匹配 PipelineLabel 中文字符串。
    /// </summary>
    private static void MarkUserTextStage(
        TranslationSession session,
        bool textIsLocal,
        PromptSessionSnapshot? promptSnapshot)
    {
        session.PipelineKind = TranslationPipelineKind.OcrUserText;
        session.TextExecutor = textIsLocal
            ? TranslationTextExecutor.LocalModel
            : TranslationTextExecutor.UserProvider;
        session.PromptSupport = promptSnapshot is not null || session.PromptTemplateId is not null
            ? TranslationPromptSupport.Applied
            : TranslationPromptSupport.Unknown;
    }

    /// <summary>Typed 路由标记（识别文字 → 免费引擎）：风格未应用必须如实记录。</summary>
    private static void MarkFreeTextStage(TranslationSession session)
    {
        session.PipelineKind = TranslationPipelineKind.OcrFreeText;
        session.TextExecutor = TranslationTextExecutor.FreeEngine;
        session.PromptSupport = TranslationPromptSupport.NotSupported;
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
    /// <remarks>
    /// <paramref name="promptSnapshot"/> 是会话起点冻结的 prompt 快照：其锚点
    /// 显式传给本会话每一个分段请求（含 Rust 侧自动重试），多段/重试在途不变；
    /// null（faithful/编译失败/未快照）保持旧默认逐请求解析。
    /// </remarks>
    private async Task<TranslationResponse> TranslateProviderTextAsync(
        string source,
        string sourceLang,
        string targetLang,
        ProviderSettings? textRuntimeSettings,
        string? textApiKey,
        TranslationSession session,
        long epoch,
        long startTimestampTicks,
        PromptSessionSnapshot? promptSnapshot,
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
        // 同一会话所有分段共用同一锚点实例 —— 在途不变性由构造保证。
        var preferenceAnchor = promptSnapshot?.Anchor;

        // One request behaves exactly as before.
        if (segments.Count == 1)
        {
            var single = CreateTextStream(
                segments[0], sourceLang, targetLang, textRuntimeSettings, textApiKey,
                session.SessionId, epoch, preferenceAnchor, cancellationToken);
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
                session.SessionId, epoch, preferenceAnchor, sessionCts.Token);

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
        PromptAnchorSnapshot? preferenceAnchor,
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
                cancellationToken,
                preferenceAnchor);
        }
        return _executor.StreamText(
            textApiKey,
            segment,
            sourceLang,
            targetLang,
            sessionId,
            epoch,
            cancellationToken,
            preferenceAnchor);
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
            session.TargetLanguage,
            session.PromptTemplateId,
            session.PromptTemplateName,
            session.PromptTemplateRevision);
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

    internal static TranslationError ClassifyException(Exception ex)
    {
        var msg = ex.Message;
        // The free engine fails TYPED: classification must not guess from the
        // message. A free-endpoint 401/403 has no API key to check, and a
        // transport timeout/DNS miss is not the "网络翻译已关闭" setting —
        // both used to be misclassified by the string rules below.
        if (ex is FreeTranslateException freeFailure)
        {
            return freeFailure.Kind switch
            {
                FreeTranslateFailureKind.RateLimited => new TranslationError(
                    TranslationErrorKind.RateLimited,
                    msg,
                    "请稍候重试，或在设置中配置自己的模型服务。",
                    IsTransient: true),
                FreeTranslateFailureKind.Unauthorized => new TranslationError(
                    TranslationErrorKind.Unknown,
                    msg,
                    "内置免费引擎无需 API Key；请稍后重试，或在设置中配置自己的模型服务。"),
                FreeTranslateFailureKind.NetworkOrTimeout => new TranslationError(
                    TranslationErrorKind.Unknown,
                    msg,
                    "请检查本机网络连接后重试。"),
                FreeTranslateFailureKind.LongContent => new TranslationError(
                    TranslationErrorKind.Unknown,
                    msg,
                    "请缩短内容，或在设置中配置自己的模型服务。"),
                FreeTranslateFailureKind.Unparsable => new TranslationError(
                    TranslationErrorKind.ParseError,
                    msg,
                    "请稍候重试，或在设置中配置自己的模型服务。"),
                _ => new TranslationError(
                    TranslationErrorKind.Unknown,
                    msg,
                    "请稍后重试，或在设置中配置自己的模型服务。"),
            };
        }
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
