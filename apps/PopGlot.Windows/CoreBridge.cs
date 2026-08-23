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

    public static Task<TranslationResponse> TranslateTextAsync(string apiKey, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Task.Run(() => EnsureSuccess<TranslationResponse>(
            Invoke(() => NativeMethods.TranslateText(apiKey, source))));
    }

    public static Task<TranslationResponse> TranslateVisionAsync(
        string apiKey,
        string mediaType,
        byte[] image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "截图必须大于 0 且不超过 8 MiB。");
        }
        var imageBase64 = Convert.ToBase64String(image);
        return Task.Run(() => EnsureSuccess<TranslationResponse>(
            Invoke(() => NativeMethods.TranslateVision(apiKey, mediaType, imageBase64))));
    }

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
    bool ApiKeyConfigured);

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
