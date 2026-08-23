using System.IO;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PopGlot.Windows;

internal static class WindowsOcrService
{
    public static bool IsSupported => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static async Task<string> RecognizeTextAsync(byte[] imageBytes, string? preferredLanguage = null)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
        {
            throw new ArgumentException("Image bytes cannot be empty.", nameof(imageBytes));
        }

        OcrEngine? engine = null;

        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            try
            {
                var lang = new Language(preferredLanguage);
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    engine = OcrEngine.TryCreateFromLanguage(lang);
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();

        if (engine is null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
        {
            engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        }

        if (engine is null)
        {
            throw new InvalidOperationException("系统未安装可用的 Windows OCR 语言包。请在 Windows 设置 -> 时间和语言 -> 语言中安装包含 OCR 的语言包。");
        }

        using var memoryStream = new InMemoryRandomAccessStream();
        using (var dataWriter = new DataWriter(memoryStream))
        {
            dataWriter.WriteBytes(imageBytes);
            await dataWriter.StoreAsync();
            await dataWriter.FlushAsync();
            dataWriter.DetachStream();
        }

        memoryStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(memoryStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(softwareBitmap);
        if (result is null || result.Lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var lineText = line.Text.Trim();
            if (!string.IsNullOrEmpty(lineText))
            {
                sb.AppendLine(lineText);
            }
        }

        return sb.ToString().Trim();
    }
}
