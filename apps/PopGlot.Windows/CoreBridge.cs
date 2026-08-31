using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// The managed side of the Rust core's C ABI.
/// </summary>
internal static partial class CoreBridge
{
    private const string LibraryName = "popglot_ffi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lock SettingsGate = new();
    private static readonly SemaphoreSlim SaveQueue = new(1, 1);
    private static ProviderSettings? _cachedSettings;

    public static void Initialize()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot");
        Directory.CreateDirectory(configDirectory);
        EnsureSuccess<string>(Invoke(() => NativeMethods.Initialize(configDirectory)));
    }

    /// <summary>
    /// Returns and clears the core's one-shot startup notice, e.g. that a
    /// corrupted settings file was backed up and defaults restored.
    /// </summary>
    public static string TakeStartupNotice()
    {
        try
        {
            return EnsureSuccess<string>(Invoke(NativeMethods.TakeStartupNotice));
        }
        catch (Exception)
        {
            // A missing notice must never break startup.
            return string.Empty;
        }
    }

    /// <summary>
    /// Current provider settings.
    /// </summary>
    public static ProviderSettings GetSettings()
    {
        lock (SettingsGate)
        {
            _cachedSettings ??= EnsureSuccess<ProviderSettings>(Invoke(NativeMethods.GetSettings));
            return _cachedSettings;
        }
    }

    public static void SaveSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (SettingsGate)
        {
            EnsureSuccess<string>(Invoke(() => NativeMethods.SaveSettings(json)));
            _cachedSettings = settings;
        }
    }

    /// <summary>
    /// 后台线程执行设置持久化（Rust 侧写盘含 flush+rename，慢磁盘/杀软
    /// 扫描时可达秒级）。排队串行以保持调用顺序，UI 线程只发起不等待。
    /// </summary>
    public static async Task SaveSettingsAsync(ProviderSettings settings)
    {
        await SaveQueue.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => SaveSettings(settings)).ConfigureAwait(false);
        }
        finally
        {
            SaveQueue.Release();
        }
    }

    /// <summary>Asks the core which screenshot pipeline the settings imply.</summary>
    public static RoutingDecision PlanScreenshotRoute(bool localOcrAvailable, bool credentialPresent) =>
        EnsureSuccess<RoutingDecision>(Invoke(() => NativeMethods.PlanScreenshotRoute(
            localOcrAvailable ? 1 : 0,
            credentialPresent ? 1 : 0)));

    /// <summary>
    /// Translates one screenshot through a draft settings snapshot with a
    /// dedicated vision provider. The text key and the vision key travel
    /// separately; the settings JSON is used without being persisted.
    /// </summary>
    public static Task<TranslationResponse> TranslateVisionDraftAsync(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var imageBase64 = Convert.ToBase64String(image);
        // The draft is already the selected vision provider's complete
        // settings. Pass its credential as the primary key; never let the
        // text route become the authentication source for this request.
        var effectiveKey = string.IsNullOrWhiteSpace(visionApiKey) ? "local" : visionApiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateVisionV3(
                    effectiveKey, effectiveKey, draftJson, "image/png", imageBase64,
                    sourceLang, targetLang, reqId))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Executes text translation using the complete provider snapshot supplied
    /// by the caller. This is used for OCR output so the text route cannot be
    /// accidentally replaced by the Core's stale mirrored settings.
    /// </summary>
    public static Task<TranslationResponse> TranslateTextDraftAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateTextDraftV1(
                    draftJson, effectiveKey, source, sourceLang, targetLang, reqId))),
            reqId,
            cancellationToken);
    }

    public static Task<TranslationResponse> TestConnectionDraftAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TestConnectionDraft(draftJson, effectiveKey, reqId))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Translates text through the configured Provider only. Whether the
    /// built-in free engine may be used instead is a privacy decision owned by
    /// <see cref="Services.TranslationCoordinator"/>, never by the bridge.
    /// </summary>
    public static async Task<TranslationResponse> TranslateTextAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var settings = GetSettings();
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            if (!settings.TargetsLocalRuntime)
            {
                throw new InvalidOperationException(
                    "安全离线模式或网络翻译已禁用；未发送任何在线翻译请求。可在设置中配置本地模型或开启网络。");
            }
        }

        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (!usesConfiguredProvider)
        {
            throw new InvalidOperationException(
                "尚未配置模型服务；未发送任何请求。可配置 API Key、本地模型地址，或允许内置免费引擎。");
        }

        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        return await RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateTextV2(effectiveKey, source, sourceLang, targetLang, reqId))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Streams text translation through the configured active provider.
    /// </summary>
    public static TranslationStreamSession TranslateTextStream(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var settings = GetSettings();
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            if (!settings.TargetsLocalRuntime)
            {
                throw new InvalidOperationException(
                    "安全离线模式或网络翻译已禁用；未发送任何在线翻译请求。可在设置中配置本地模型或开启网络。");
            }
        }

        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (!usesConfiguredProvider)
        {
            throw new InvalidOperationException(
                "尚未配置模型服务；未发送任何请求。可配置 API Key、本地模型地址，或允许内置免费引擎。");
        }

        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateTextStreamV1(
                    effectiveKey, source, sourceLang, targetLang, reqId, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateTextStreamAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default) =>
        TranslateTextStream(apiKey, source, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken);

    /// <summary>
    /// Streams text translation through an unpersisted settings draft snapshot.
    /// </summary>
    public static TranslationStreamSession TranslateTextDraftStream(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateTextDraftStreamV1(
                    draftJson, effectiveKey, source, sourceLang, targetLang, reqId, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateTextDraftStreamAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default) =>
        TranslateTextDraftStream(draftSettings, apiKey, source, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken);

    /// <summary>
    /// Streams screenshot translation through an unpersisted (or stored) vision settings draft.
    /// </summary>
    public static TranslationStreamSession TranslateVisionDraftStream(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "截图必须大于 0 且不超过 8 MiB。");
        }

        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var imageBase64 = Convert.ToBase64String(image);
        var effectiveKey = string.IsNullOrWhiteSpace(visionApiKey) ? "local" : visionApiKey;
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateVisionDraftStreamV1(
                    effectiveKey, effectiveKey, draftJson, "image/png", imageBase64,
                    sourceLang, targetLang, reqId, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateVisionDraftStreamAsync(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default) =>
        TranslateVisionDraftStream(draftSettings, textApiKey, visionApiKey, image, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken);

    /// <summary>
    /// Translates already-recognized text (e.g. from local OCR) through the
    /// configured provider, or — with an explicit, persisted consent — through
    /// the free web engine when nothing else is configured.
    /// </summary>
    internal static async Task<TranslationResponse> TranslateRecognizedTextAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (usesConfiguredProvider)
        {
            return await TranslateTextAsync(apiKey, source, sourceLang, targetLang, requestId, cancellationToken);
        }

        if (!Services.OutboundPolicy.AllowsFreeEngine(settings, out var denial))
        {
            throw new InvalidOperationException(
                denial is null ? "未允许出网翻译。" : $"{denial.Message} {denial.ActionableSuggestion}".Trim());
        }

        return await FreeTranslateService.TranslateAsync(source, sourceLang, targetLang, cancellationToken);
    }

    public static Task<ScreenshotTranslation> TranslateScreenshotAsync(
        string? apiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        CancellationToken cancellationToken) =>
        TranslateScreenshotAsync(apiKey, image, sourceLang, targetLang, null, cancellationToken);

    public static async Task<ScreenshotTranslation> TranslateScreenshotAsync(
        string? apiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "截图必须大于 0 且不超过 8 MiB。");
        }

        var ocrAvailable = WindowsOcrService.IsSupported;
        var route = PlanScreenshotRoute(ocrAvailable, !string.IsNullOrWhiteSpace(apiKey));

        if (route.MayUploadImage)
        {
            var imageBase64 = Convert.ToBase64String(image);
            var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
            var reqId = requestId ?? Guid.NewGuid().ToString("N");
            try
            {
                var response = await RunCancellableAsync(
                    () => EnsureSuccess<TranslationResponse>(Invoke(
                        () => NativeMethods.TranslateVisionV2(
                            effectiveKey, "image/png", imageBase64, sourceLang, targetLang, reqId))),
                    reqId,
                    cancellationToken);
                return new ScreenshotTranslation(response, "视觉模型", route.ExplanationZh);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception visionError) when (ocrAvailable)
            {
                try
                {
                    var fallback = await TranslateViaLocalOcrAsync(
                        apiKey, image, sourceLang, targetLang, reqId, cancellationToken);
                    return fallback with
                    {
                        PipelineReason = $"视觉模型失败（{visionError.Message}），已回退到本地 OCR。",
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception fallbackError)
                {
                    throw new InvalidOperationException(
                        $"视觉模型翻译失败（{visionError.Message}）；本地 OCR 回退也失败（{fallbackError.Message}）。");
                }
            }
        }

        if (!ocrAvailable)
        {
            throw new InvalidOperationException(route.ExplanationZh);
        }

        var local = await TranslateViaLocalOcrAsync(
            apiKey, image, sourceLang, targetLang, requestId, cancellationToken);
        return local with { PipelineReason = route.ExplanationZh };
    }

    /// <summary>
    /// The OCR fallback path: recognise locally, then translate the text via
    /// the unified entry — configured provider when present, the authorised
    /// free engine otherwise. Never bypasses the free-engine decision.
    /// </summary>
    public static async Task<(TranslationResponse Response, ulong OcrElapsedMs, ulong NetworkElapsedMs)>
        TranslateScreenshotViaOcrAsync(
            string? apiKey,
            byte[] image,
            string sourceLang,
            string targetLang,
            string? requestId = null,
            CancellationToken cancellationToken = default,
            ProviderSettings? textRouteSettings = null)
    {
        var ocrStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var recognized = await WindowsOcrService.RecognizeTextAsync(image, sourceLang);
        ocrStopwatch.Stop();
        if (string.IsNullOrWhiteSpace(recognized))
        {
            throw new InvalidOperationException(
                "本地 OCR 未能在所选区域识别到文字。请重新框选更清晰的区域，或在设置中开启截图上传以使用视觉模型。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var networkStopwatch = System.Diagnostics.Stopwatch.StartNew();
        TranslationResponse response;
        if (textRouteSettings is not null &&
            (textRouteSettings.TextIsConfigured || textRouteSettings.TargetsLocalRuntime))
        {
            response = await TranslateTextDraftAsync(
                textRouteSettings,
                apiKey ?? string.Empty,
                recognized,
                sourceLang,
                targetLang,
                requestId,
                cancellationToken);
        }
        else
        {
            response = await TranslateRecognizedTextAsync(
                apiKey, recognized, sourceLang, targetLang, requestId, cancellationToken);
        }
        networkStopwatch.Stop();

        return (response with
            {
                Result = response.Result with { Transcription = recognized },
            },
            (ulong)ocrStopwatch.ElapsedMilliseconds,
            (ulong)networkStopwatch.ElapsedMilliseconds);
    }

    private static async Task<ScreenshotTranslation> TranslateViaLocalOcrAsync(
        string? apiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var recognized = await WindowsOcrService.RecognizeTextAsync(image, sourceLang);
        if (string.IsNullOrWhiteSpace(recognized))
        {
            throw new InvalidOperationException(
                "本地 OCR 未能在所选区域识别到文字。请重新框选更清晰的区域，或在设置中开启截图上传以使用视觉模型。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var response = await TranslateTextAsync(
            apiKey, recognized, sourceLang, targetLang, requestId, cancellationToken);
        var withTranscription = response with
        {
            Result = response.Result with { Transcription = recognized },
        };
        return new ScreenshotTranslation(withTranscription, "本地 OCR", string.Empty);
    }

    public static void CancelRequest(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            NativeMethods.CancelRequest(requestId);
        }
    }

    public static void CancelActiveRequest()
    {
        NativeMethods.CancelActiveRequest();
    }

    private static async Task<T> RunCancellableAsync<T>(
        Func<T> blockingWork,
        string requestId,
        CancellationToken cancellationToken)
    {
        var work = Task.Run(blockingWork);
        if (!cancellationToken.CanBeCanceled)
        {
            return await work;
        }

        await using var registration = cancellationToken.Register(
            () => CancelRequest(requestId));
        try
        {
            return await work;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private unsafe delegate nint NativeStreamInvoker(
        delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
        nint userData);

    private static async Task<TranslationResponse> ExecuteStreamRequestAsync(
        TranslationStreamBuffer buffer,
        NativeStreamInvoker nativeStreamingCall,
        string requestId,
        CancellationToken cancellationToken)
    {
        var handle = GCHandle.Alloc(buffer);
        try
        {
            var userData = GCHandle.ToIntPtr(handle);
            return await RunCancellableAsync(
                () =>
                {
                    try
                    {
                        string rawJson;
                        unsafe
                        {
                            rawJson = Invoke(() => nativeStreamingCall(&StreamCallbackThunk, userData));
                        }
                        var response = EnsureSuccess<TranslationResponse>(rawJson);
                        buffer.Complete();
                        return response;
                    }
                    catch (Exception ex)
                    {
                        buffer.Abort(ex.Message);
                        throw;
                    }
                    finally
                    {
                        GC.KeepAlive(buffer);
                    }
                },
                requestId,
                cancellationToken);
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    internal static int ProcessStreamDelta(nint userData, int eventType, nint payloadPtr, nuint byteLen)
    {
        if (userData == 0)
        {
            return 0;
        }

        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated)
            {
                return 1;
            }

            if (handle.Target is not TranslationStreamBuffer buffer)
            {
                return 1;
            }

            // eventType 1 = POPGLOT_STREAM_EVENT_TEXT_DELTA_V1
            if (eventType == 1)
            {
                if (payloadPtr == 0 || byteLen == 0)
                {
                    return buffer.IsActive ? 0 : 1;
                }

                if (byteLen > int.MaxValue)
                {
                    buffer.Abort("Payload length exceeds maximum supported size.");
                    return 1;
                }

                bool appended = buffer.TryAppendUtf8(payloadPtr, (int)byteLen);
                return appended ? 0 : 1;
            }

            return buffer.IsActive ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int StreamCallbackThunk(nint userData, int eventType, nint payloadPtr, nuint byteLen)
    {
        return ProcessStreamDelta(userData, eventType, payloadPtr, byteLen);
    }

    private static string Invoke(Func<nint> nativeCall)
    {
        var pointer = nativeCall();
        if (pointer == 0)
        {
            throw new InvalidOperationException("PopGlot Core returned an empty response.");
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                ?? throw new InvalidOperationException("PopGlot Core returned invalid UTF-8.");
        }
        finally
        {
            NativeMethods.FreeString(pointer);
        }
    }

    internal static T EnsureSuccess<T>(string json)
    {
        var response = JsonSerializer.Deserialize<Envelope<T>>(json, JsonOptions)
            ?? throw new InvalidOperationException("PopGlot Core response was empty.");
        if (!response.Ok || response.Data is null)
        {
            throw new InvalidOperationException(response.Error ?? "PopGlot Core operation failed.");
        }
        return response.Data;
    }

    private sealed record Envelope<T>(bool Ok, T? Data, string? Error);

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "popglot_initialize", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint Initialize(string configDirectory);

        [LibraryImport(LibraryName, EntryPoint = "popglot_get_settings")]
        internal static partial nint GetSettings();

        [LibraryImport(LibraryName, EntryPoint = "popglot_take_startup_notice")]
        internal static partial nint TakeStartupNotice();

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_settings", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SaveSettings(string json);

        [LibraryImport(LibraryName, EntryPoint = "popglot_plan_screenshot_route")]
        internal static partial nint PlanScreenshotRoute(int localOcrAvailable, int credentialPresent);

        [LibraryImport(LibraryName, EntryPoint = "popglot_test_connection_draft", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TestConnectionDraft(string draftJson, string apiKey, string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextDraftV1(
            string draftJson,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextDraftStreamV1(
            string settingsJson,
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextV2(
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextStreamV1(
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_v3", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateVisionV3(
            string apiKey,
            string visionApiKey,
            string settingsJson,
            string mediaType,
            string imageBase64,
            string sourceLang,
            string targetLang,
            string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_draft_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateVisionDraftStreamV1(
            string apiKey,
            string? visionApiKey,
            string? settingsJson,
            string mediaType,
            string imageBase64,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateVisionV2(
            string apiKey,
            string mediaType,
            string imageBase64,
            string sourceLang,
            string targetLang,
            string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_request", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int CancelRequest(string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_active_request")]
        internal static partial int CancelActiveRequest();

        [LibraryImport(LibraryName, EntryPoint = "popglot_free_string")]
        internal static partial void FreeString(nint value);
    }
}

internal enum TranslationMode
{
    Auto,
    LocalOcr,
    VisionDirect,
    /// 视觉模型识别截图文字，译文由文本模型翻译。
    VisionOcr,
}

internal enum ProviderType
{
    OpenAiCompatible,
    OpenAiResponses,
    AnthropicMessages,
    GeminiGenerateContent,
}

/// A dedicated vision provider: complete connection details for screenshot
/// traffic. The API key never travels inside this record — it is supplied per
/// request — so it is safe to persist as part of the settings document.
internal sealed record VisionProviderOverride(
    ProviderType ProviderType,
    string ApiBaseUrl,
    string VisionEndpoint,
    string VisionModel,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string AnthropicVersion,
    bool AllowInsecureTls = false);

internal sealed record ProviderSettings(
    uint SchemaVersion,
    ProviderType ProviderType,
    string ApiBaseUrl,
    string TextEndpoint,
    string VisionEndpoint,
    string TextModel,
    string VisionModel,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string AnthropicVersion,
    bool SupportsText,
    bool SupportsVision,
    bool NetworkEnabled,
    TranslationMode Mode,
    bool AllowImageUploadInAuto,
    bool SafeDevMode,
    bool AllowInsecureTls,
    bool ApiKeyConfigured,
    string SourceLanguage,
    string TargetLanguage,
    bool IncludeExplanation,
    bool ProtectCodeTokens,
    VisionProviderOverride? VisionProvider = null)
{
    public bool VisionIsConfigured => SupportsVision && !string.IsNullOrWhiteSpace(VisionModel);
    public bool TextIsConfigured => SupportsText && !string.IsNullOrWhiteSpace(TextModel);

    public bool TargetsLocalRuntime => IsLocalBaseUrl(ApiBaseUrl);

    internal static bool IsLocalBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }
        var text = baseUrl.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host is "::1" or "[::1]")
        {
            return true;
        }
        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            return false;
        }
        if (System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }
        var octets = address.GetAddressBytes();
        if (octets.Length != 4)
        {
            return false;
        }
        return octets[0] switch
        {
            10 => true,
            192 => octets[1] == 168,
            172 => octets[1] is >= 16 and <= 31,
            _ => false,
        };
    }
}

internal sealed record RoutingDecision(
    TranslationMode SelectedMode,
    string ReasonCode,
    string ExplanationZh,
    bool MayUploadImage);

internal sealed record TranslationResult(
    string TranslatedText,
    string Transcription,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    IReadOnlyList<string> Warnings,
    string Phonetic = "");

internal sealed record ProviderDiagnostics(
    string RequestId,
    ProviderType ProviderType,
    string Endpoint,
    byte Attempts,
    ushort StatusCode,
    ulong ElapsedMs);

internal sealed record TranslationResponse(
    TranslationResult Result,
    ProviderDiagnostics Diagnostics)
{
    public bool IsFreeEngine =>
        string.Equals(Diagnostics.RequestId, FreeTranslateService.RequestId, StringComparison.Ordinal);

    public string EngineLabel => IsFreeEngine ? "免费引擎" : Diagnostics.ProviderType.ToString();
}

internal sealed record ScreenshotTranslation(
    TranslationResponse Response,
    string Pipeline,
    string PipelineReason);
