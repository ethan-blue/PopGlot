using System.Globalization;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PopGlot.Windows;

/// <summary>
/// Offline recognition through the OCR engine shipped with Windows 10/11.
/// </summary>
internal static class WindowsOcrService
{
    public static bool IsSupported => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public static IReadOnlyList<string> AvailableLanguageTags =>
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToList();

    /// <summary>Installed packs as "简体中文 (zh-Hans-CN)" for the settings list.</summary>
    public static IReadOnlyList<string> AvailableLanguageDescriptions =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(language => $"{FriendlyName(language)}  ·  {language.LanguageTag}")
            .ToList();

    private static string FriendlyName(Language language)
    {
        if (!string.IsNullOrWhiteSpace(language.DisplayName))
        {
            return language.DisplayName;
        }
        try
        {
            return CultureInfo.GetCultureInfo(language.LanguageTag).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return language.LanguageTag;
        }
    }

    /// <summary>
    /// Recognizes text, preferring an engine that matches the source language.
    /// </summary>
    /// <param name="imageBytes">Encoded image (PNG from the capture path).</param>
    /// <param name="sourceLanguageTag">
    /// The user's chosen source language, or "auto". Matching the engine to the
    /// expected script materially improves accuracy — the previous version
    /// always used the user-profile engine even when the user had explicitly
    /// selected a different source language.
    /// </param>
    public static async Task<string> RecognizeTextAsync(
        byte[] imageBytes,
        string? sourceLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("Image bytes cannot be empty.", nameof(imageBytes));
        }

        var engine = ResolveEngine(sourceLanguageTag)
            ?? throw new InvalidOperationException(
                "系统未安装可用的 Windows OCR 语言包。请在「Windows 设置 → 时间和语言 → 语言和区域」中为目标语言添加“可选功能 → 光学字符识别”。");

        using var memoryStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memoryStream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }

        memoryStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(memoryStream).AsTask(cancellationToken);
        var size = FitDimensions(decoder.PixelWidth, decoder.PixelHeight, OcrEngine.MaxImageDimension);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = size.Width, ScaledHeight = size.Height },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        if (result is null || result.Lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var lineText = line.Text.Trim();
            if (lineText.Length > 0)
            {
                builder.AppendLine(lineText);
            }
        }
        return builder.ToString().TrimEnd();
    }

    internal static (uint Width, uint Height) FitDimensions(uint width, uint height, uint maximum)
    {
        if (width == 0 || height == 0 || maximum == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "OCR 图片尺寸必须大于零。");
        var scale = Math.Min(1d, (double)maximum / Math.Max(width, height));
        return (Math.Max(1u, (uint)(width * scale)), Math.Max(1u, (uint)(height * scale)));
    }

    private static OcrEngine? ResolveEngine(string? sourceLanguageTag)
    {
        var normalized = LanguageCatalog.Normalize(sourceLanguageTag);
        if (normalized != LanguageCatalog.Auto)
        {
            var preferred = TryCreateForTag(normalized);
            if (preferred is not null)
            {
                return preferred;
            }
            throw new InvalidOperationException(
                $"系统未安装所选原文语言（{normalized}）的 OCR 语言包。请安装对应的光学字符识别功能，或改选原文语言；未使用其他语言代替识别。");
        }

        // Fall back to the user's profile languages, then to anything installed.
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0
                ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
                : null);
    }

    private static OcrEngine? TryCreateForTag(string tag)
    {
        try
        {
            var language = new Language(tag);
            if (OcrEngine.IsLanguageSupported(language))
            {
                return OcrEngine.TryCreateFromLanguage(language);
            }
        }
        catch (ArgumentException)
        {
            // Not a well-formed BCP-47 tag for this machine; fall through.
        }

        // `zh-CN` will not match an installed `zh-Hans-CN` pack by exact tag, so
        // also accept any installed recognizer sharing the primary subtag.
        var primary = tag.Split('-')[0];
        var chineseScript = tag is "zh-CN" ? "zh-Hans" : tag is "zh-TW" ? "zh-Hant" : null;
        var match = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(language =>
            chineseScript is not null
                ? language.LanguageTag.Equals(chineseScript, StringComparison.OrdinalIgnoreCase) ||
                  language.LanguageTag.StartsWith(chineseScript + "-", StringComparison.OrdinalIgnoreCase)
                : language.LanguageTag.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
                  language.LanguageTag.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase));
        return match is null ? null : OcrEngine.TryCreateFromLanguage(match);
    }
}
