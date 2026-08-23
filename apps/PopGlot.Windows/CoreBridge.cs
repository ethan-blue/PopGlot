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

    public static PreviewResult Preview(PreviewRequest request)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        return EnsureSuccess<PreviewResult>(Invoke(() => NativeMethods.Preview(json)));
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

        [LibraryImport(LibraryName, EntryPoint = "popglot_preview", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint Preview(string json);

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

internal sealed record ProviderSettings(
    string ApiBaseUrl,
    string TextModel,
    string VisionModel,
    TranslationMode Mode,
    bool AllowImageUploadInAuto,
    bool SafeDevMode,
    bool ApiKeyConfigured);

internal sealed record PreviewRequest(
    TranslationMode Mode,
    string SampleText,
    bool LooksLikeCode,
    bool ComplexLayout,
    float ImageQuality,
    float OcrConfidence);

internal sealed record RoutingDecision(
    TranslationMode SelectedMode,
    string ReasonCode,
    string ExplanationZh,
    bool MayUploadImage);

internal sealed record PreviewResult(
    RoutingDecision Decision,
    string Title,
    string TranslatedText,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    bool RequiresConfiguration,
    bool NetworkRequestSent);
