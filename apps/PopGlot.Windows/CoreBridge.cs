using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopGlot.Windows;

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

    public static void Initialize()
    {
        var configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot");
        var response = Invoke(() => NativeMethods.Initialize(configDirectory));
        EnsureSuccess<string>(response);
    }

    public static ProviderSettings GetSettings()
    {
        return EnsureSuccess<ProviderSettings>(Invoke(NativeMethods.GetSettings));
    }

    public static void SaveSettings(ProviderSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        EnsureSuccess<string>(Invoke(() => NativeMethods.SaveSettings(json)));
    }

    public static Task<TranslationResponse> TestConnectionAsync(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return Task.Run(() =>
            EnsureSuccess<TranslationResponse>(Invoke(() => NativeMethods.TestConnection(apiKey))));
    }

    public static async Task<TranslationResponse> TranslateTextAsync(
        string? apiKey,
        string source,
        string sourceLang = "auto",
        string targetLang = "zh-CN")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var settings = GetSettings();
        var isLocal = IsLocalBaseUrl(settings.ApiBaseUrl);

        if (!string.IsNullOrWhiteSpace(apiKey) || isLocal)
        {
            // A provider is explicitly configured: surface its real error instead
            // of silently degrading to the free web engine.
            var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "ollama" : apiKey;
            return await Task.Run(() => EnsureSuccess<TranslationResponse>(
                Invoke(() => NativeMethods.TranslateText(effectiveKey, source))));
        }

        // Zero-config free web translation
        return await FreeTranslateService.TranslateAsync(source, sourceLang, targetLang);
    }

    public static async Task<TranslationResponse> TranslateVisionAsync(
        string? apiKey,
        string mediaType,
        byte[] image,
        string sourceLang = "auto",
        string targetLang = "zh-CN")
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "截图必须大于 0 且不超过 8 MiB。");
        }

        var settings = GetSettings();

        // Uploading requires the privacy toggle, a configured vision model, a
        // credential, and enabled networking. Otherwise use local OCR directly
        // instead of firing a request the core will refuse.
        var mayUpload = settings.NetworkEnabled
            && settings.AllowImageUploadInAuto
            && settings.Mode != TranslationMode.LocalOcr
            && settings.VisionIsConfigured
            && !string.IsNullOrWhiteSpace(apiKey);

        if (!mayUpload)
        {
            return await TranslateViaLocalOcrAsync(apiKey, image, sourceLang, targetLang);
        }

        var imageBase64 = Convert.ToBase64String(image);
        Exception visionError;
        try
        {
            return await Task.Run(() => EnsureSuccess<TranslationResponse>(
                Invoke(() => NativeMethods.TranslateVision(apiKey!, mediaType, imageBase64))));
        }
        catch (Exception exception)
        {
            visionError = exception;
        }

        // Vision failed (e.g. the model rejected the image); degrade to local
        // OCR + text translation, but keep the original failure visible if the
        // fallback also fails.
        try
        {
            return await TranslateViaLocalOcrAsync(apiKey, image, sourceLang, targetLang);
        }
        catch (Exception fallbackError)
        {
            throw new InvalidOperationException(
                $"视觉模型翻译失败（{visionError.Message}）；本地 OCR 回退也失败（{fallbackError.Message}）。");
        }
    }

    private static async Task<TranslationResponse> TranslateViaLocalOcrAsync(
        string? apiKey,
        byte[] image,
        string sourceLang,
        string targetLang)
    {
        var recognizedText = await WindowsOcrService.RecognizeTextAsync(image);
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            throw new InvalidOperationException("本地 OCR 未能识别到文字，请重新框选，或在设置中开启截图上传使用视觉模型。");
        }
        return await TranslateTextAsync(apiKey, recognizedText, sourceLang, targetLang);
    }

    internal static bool IsLocalBaseUrl(string baseUrl) =>
        baseUrl.Contains("localhost")
        || baseUrl.Contains("127.0.0.1")
        || baseUrl.Contains("192.168.")
        || baseUrl.Contains("10.");

    public static bool CancelActiveRequest() => NativeMethods.CancelActiveRequest() != 0;

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

    private static T EnsureSuccess<T>(string json)
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

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_settings", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SaveSettings(string json);

        [LibraryImport(LibraryName, EntryPoint = "popglot_test_connection", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TestConnection(string apiKey);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateText(string apiKey, string source);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateVision(string apiKey, string mediaType, string imageBase64);

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
}

internal enum ProviderType
{
    OpenAiCompatible,
    OpenAiResponses,
    AnthropicMessages,
    GeminiGenerateContent,
}

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
    bool ApiKeyConfigured)
{
    public bool VisionIsConfigured => SupportsVision && !string.IsNullOrWhiteSpace(VisionModel);
    public bool TextIsConfigured => SupportsText && !string.IsNullOrWhiteSpace(TextModel);
}

internal sealed record TranslationResult(
    string TranslatedText,
    string Transcription,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    IReadOnlyList<string> Warnings);

internal sealed record ProviderDiagnostics(
    string RequestId,
    ProviderType ProviderType,
    string Endpoint,
    byte Attempts,
    ushort StatusCode,
    ulong ElapsedMs);

internal sealed record TranslationResponse(
    TranslationResult Result,
    ProviderDiagnostics Diagnostics);
