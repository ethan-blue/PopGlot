using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PopGlot.Windows;

/// <summary>One recognition run: engine used, preprocessing applied, outcome.</summary>
internal sealed record OcrRunTrace(
    string EngineTag,
    bool FellBackFromPreferred,
    int ScaleFactor,
    int Attempts,
    double? EstimatedLineHeightPx,
    int RecognizedLines,
    int ElapsedMs);

/// <summary>
/// Offline recognition through the OCR engine shipped with Windows 10/11.
/// </summary>
/// <remarks>
/// Small text needs help: stock Chinese Windows ships only a zh-Hans pack, and
/// that engine returns nothing for 12-16 px Latin text unless the image is
/// upscaled first (measured: 0% at 1x, 56-100% at 3x). Line height is estimated
/// per capture so multi-line regions are handled correctly, and upscaling never
/// touches captures whose lines are already large — enlarging those makes
/// accuracy worse, not better. Chinese results get their engine-inserted
/// inter-character spaces collapsed; a missing language pack falls back to the
/// profile engine instead of failing the whole request.
/// </remarks>
internal static class WindowsOcrService
{
    /// <summary>Lines below this height (px) benefit from upscaling; above it they do not.</summary>
    internal const double SmallLineHeightPx = 28;

    /// <summary>Upscale target line height; the factor is ceil(target/line) clamped to [1,4].</summary>
    internal const double TargetLineHeightPx = 40;

    internal const int MaxScaleFactor = 4;

    private static readonly AsyncLocal<OcrRunTrace?> LastTraceFlow = new();

    /// <summary>Trace of the most recent recognition on this async flow; null before the first run.</summary>
    internal static OcrRunTrace? LastTrace => LastTraceFlow.Value;

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
    /// Recognizes text with preprocessing sized to the actual line height of
    /// the capture, preferring an engine that matches the source language and
    /// falling back to the profile engine when that pack is not installed.
    /// </summary>
    /// <param name="imageBytes">Encoded image (PNG from the capture path).</param>
    /// <param name="sourceLanguageTag">The user's chosen source language, or "auto".</param>
    public static async Task<string> RecognizeTextAsync(
        byte[] imageBytes,
        string? sourceLanguageTag = null,
        CancellationToken cancellationToken = default)
    {
        var (text, _) = await RecognizeTracedAsync(imageBytes, sourceLanguageTag, cancellationToken).ConfigureAwait(false);
        return text;
    }

    /// <summary>Same recognition, returning the run trace for diagnostics.</summary>
    internal static async Task<(string Text, OcrRunTrace Trace)> RecognizeTracedAsync(
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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var (engine, fellBack) = ResolveEngine(sourceLanguageTag);
        if (engine is null)
        {
            throw new InvalidOperationException(
                "系统未安装可用的 Windows OCR 语言包。请在「Windows 设置 → 时间和语言 → 语言和区域」中为目标语言添加“可选功能 → 光学字符识别”。");
        }

        double? lineHeight = null;
        var (width, height) = (0u, 0u);
        try
        {
            lineHeight = EstimateLineHeightPx(imageBytes, out width, out height);
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        {
            // Analysis is best-effort: an unreadable bitmap runs pass 1 at 1x.
        }

        var attempts = 0;
        var usedScale = 1;

        // Pass 1: original scale.
        var text = await RecognizeCoreAsync(engine, imageBytes, cancellationToken).ConfigureAwait(false);
        attempts++;

        var candidates = ScaleCandidates(lineHeight, width, height);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Empty at 1x: first non-empty upscaled attempt wins.
            foreach (var factor in candidates)
            {
                if (cancellationToken.IsCancellationRequested) break;
                text = await RecognizeCoreAsync(
                    engine, Upscale(imageBytes, factor), cancellationToken).ConfigureAwait(false);
                attempts++;
                usedScale = factor;
                if (!string.IsNullOrWhiteSpace(text)) break;
            }
        }
        else
        {
            // Non-empty at 1x: upscaled runs can still read more (small Latin is
            // often invisible to the engine at 1x-but-not-empty, and ClearType
            // fringing fragments mid-size text). Score by recognized glyph count
            // discounted by fragmentation; the 1x result wins ties so a solid
            // capture is never displaced by noise of similar length.
            var bestText = text;
            var bestScore = Score(text);
            foreach (var factor in candidates)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var attempt = await RecognizeCoreAsync(
                    engine, Upscale(imageBytes, factor), cancellationToken).ConfigureAwait(false);
                attempts++;
                var score = Score(attempt);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestText = attempt;
                    usedScale = factor;
                }
            }
            text = bestText;
        }

        stopwatch.Stop();
        var collapsed = CollapseCjkAdjacentSpaces(text);
        var trace = new OcrRunTrace(
            engine.RecognizerLanguage.LanguageTag,
            fellBack,
            usedScale,
            attempts,
            lineHeight,
            CountLines(collapsed),
            (int)stopwatch.Elapsed.TotalMilliseconds);
        return (collapsed, trace);
    }


    /// <summary>Decodes, scales to OCR limits and runs the engine over one PNG payload.</summary>
    private static async Task<string> RecognizeCoreAsync(OcrEngine engine, byte[] imageBytes, CancellationToken cancellationToken)
    {
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

    private static bool FitsMaxDimension(uint width, uint height, int factor) =>
        Math.Max(width, height) * factor <= OcrEngine.MaxImageDimension;

    /// <summary>
    /// Upscale factors worth trying for a measured line height: large lines get
    /// none (enlarging them hurts), small lines get 2x and 3x — 2x is the sweet
    /// spot for ClearType 12 px text; 3x covers 15-27 px lines. Factors that
    /// would exceed the engine's dimension cap are dropped.
    /// </summary>
    internal static int[] ScaleCandidates(double? estimatedLineHeightPx, uint width, uint height)
    {
        if (estimatedLineHeightPx is null or >= SmallLineHeightPx)
        {
            return [];
        }
        var candidates = new List<int>();
        foreach (var factor in new[] { 2, 3 })
        {
            if (FitsMaxDimension(width, height, factor))
            {
                candidates.Add(factor);
            }
        }
        return candidates.ToArray();
    }

    /// <summary>
    /// Recognition quality score: glyph count discounted by fragmentation —
    /// the share of one/two-character Latin tokens ("T h e requested" reads as
    /// garbage despite many tokens). CJK text without spaces scores full.
    /// </summary>
    internal static double Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 3)
        {
            return GlyphCount(text);
        }
        var fragmented = words.Count(word => word.Length <= 2);
        var fragmentation = (double)fragmented / words.Length;
        return GlyphCount(text) * (1 - fragmentation);
    }


    /// <summary>
    /// Estimates text line height from row contrast bands: rows containing
    /// text have a luminance spread; runs of such rows separated by quiet
    /// gaps are lines. Returns null when no banded structure is found
    /// (photos, blank captures) — those stay at 1x.
    /// </summary>
    internal static double? EstimateLineHeightPx(byte[] png, out uint width, out uint height)
    {
        using var bitmap = new Bitmap(new MemoryStream(png), useIcm: false);
        width = (uint)bitmap.Width;
        height = (uint)bitmap.Height;
        if (width < 6 || height < 6)
        {
            return null;
        }

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        var stride = data.Stride;
        var rowBytes = new byte[stride];
        var runs = new List<int>();
        var runStart = -1;
        var prev = false;
        try
        {
            for (var y = 0; y < data.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + y * stride), rowBytes, 0, stride);
                byte min = 255, max = 0;
                for (var x = 0; x < data.Width; x++)
                {
                    var offset = x * 3;
                    var luminance = (byte)((rowBytes[offset] * 30 + rowBytes[offset + 1] * 59 + rowBytes[offset + 2] * 11) / 100);
                    if (luminance < min) min = luminance;
                    if (luminance > max) max = luminance;
                }
                var isTextRow = max - min > 60;
                if (isTextRow && !prev)
                {
                    runStart = y;
                }
                else if (!isTextRow && prev)
                {
                    var runHeight = y - runStart;
                    if (runHeight >= 4)
                    {
                        runs.Add(runHeight);
                    }
                    runStart = -1;
                }
                prev = isTextRow;
            }
            if (prev && runStart >= 0)
            {
                var runHeight = data.Height - runStart;
                if (runHeight >= 4)
                {
                    runs.Add(runHeight);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (runs.Count == 0)
        {
            return null;
        }
        runs.Sort();
        // Median, not mean: a stray underline or cursor must not drag the
        // estimate up and silently disable upscaling.
        return runs[runs.Count / 2];
    }

    /// <summary>
    /// Removes spaces the engine inserts between CJK characters ("无 法 连
    /// 接" → "无法连接"). Spaces between Latin words, and between Latin and
    /// CJK runs, are kept.
    /// </summary>
    internal static string CollapseCjkAdjacentSpaces(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is ' ' or '　' && i > 0 && i < text.Length - 1)
            {
                var previous = builder.Length > 0 ? builder[builder.Length - 1] : '\0';
                var next = text[i + 1];
                if (IsCjk(previous) && IsCjk(next))
                {
                    continue;
                }
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    internal static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿'      // CJK blocks, Kangxi radicals, kana
            or >= '豈' and <= '﫿'    // CJK compatibility ideographs
            or >= '＀' and <= '￯'    // fullwidth forms
            or >= '　' and <= '〿';   // CJK punctuation

    /// <summary>Non-whitespace character count — the dual-pass tiebreaker.</summary>
    internal static int GlyphCount(string text) => text.Count(ch => !char.IsWhiteSpace(ch));

    private static int CountLines(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split('\n').Length;

    /// <summary>
    /// Engine resolution with fallback: exact tag, then same primary subtag
    /// (zh-CN → zh-Hans-*), then the user profile, then any installed pack.
    /// A missing preferred pack degrades to a fallback engine instead of
    /// throwing — a wrong-script engine still reads Latin text; an exception
    /// reads nothing. The bool reports the substitution so callers can
    /// surface it.
    /// </summary>
    private static (OcrEngine? Engine, bool FellBackFromPreferred) ResolveEngine(string? sourceLanguageTag)
    {
        var normalized = LanguageCatalog.Normalize(sourceLanguageTag);
        if (normalized != LanguageCatalog.Auto)
        {
            var preferred = TryCreateForTag(normalized);
            if (preferred is not null)
            {
                return (preferred, false);
            }
        }

        var profile = OcrEngine.TryCreateFromUserProfileLanguages();
        if (profile is not null)
        {
            return (profile, normalized != LanguageCatalog.Auto);
        }
        if (OcrEngine.AvailableRecognizerLanguages.Count > 0)
        {
            var any = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
            return (any, normalized != LanguageCatalog.Auto);
        }
        return (null, false);
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

    /// <summary>High-quality bicubic upscale, re-encoded as PNG for the OCR decoder.</summary>
    private static byte[] Upscale(byte[] png, int factor)
    {
        using var source = new Bitmap(new MemoryStream(png), useIcm: false);
        using var scaled = new Bitmap(source.Width * factor, source.Height * factor, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, scaled.Width, scaled.Height);
        using var stream = new MemoryStream();
        scaled.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
