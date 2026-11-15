using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PopGlot.Windows;

/// <summary>
/// Full virtual-desktop snapshot taken before the capture overlay appears,
/// keeping hover tooltips, popups, and menus frozen in place during selection.
/// </summary>
internal sealed class FrozenDesktopSnapshot : IDisposable
{
    private Drawing.Bitmap? _bitmap;

    public FrozenDesktopSnapshot(Rect virtualBounds, Drawing.Bitmap bitmap, BitmapSource? previewSource)
    {
        VirtualBounds = virtualBounds;
        _bitmap = bitmap;
        PreviewSource = previewSource;
    }

    public Rect VirtualBounds { get; }
    public BitmapSource? PreviewSource { get; }

    internal Drawing.Bitmap Bitmap =>
        _bitmap ?? throw new ObjectDisposedException(nameof(FrozenDesktopSnapshot));

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}

/// <summary>
/// Copies a region of the screen into an in-memory PNG.
/// </summary>
/// <remarks>
/// The rectangle is always in physical pixels; the process is
/// per-monitor DPI aware, so <c>CopyFromScreen</c> maps one-to-one onto the
/// virtual desktop with no scaling applied by the OS.
/// </remarks>
internal static class ScreenCaptureService
{
    private const long MaxPixels = 16_000_000;
    private const int MaxEncodedBytes = 8 * 1024 * 1024;
    private const int MinimumSide = 6;

    /// <summary>
    /// Captures the entire virtual desktop synchronously into memory so hover
    /// popups, tooltips, and menus stay visible once the overlay takes focus.
    /// Returns null when screen capture is unavailable (e.g., locked desktop).
    /// </summary>
    public static FrozenDesktopSnapshot? TryCaptureVirtualDesktop()
    {
        try
        {
            var bounds = ScreenGeometry.VirtualScreenPixels();
            var x = SafeToInt(Math.Floor(bounds.X));
            var y = SafeToInt(Math.Floor(bounds.Y));
            var width = SafeToInt(Math.Ceiling(bounds.Width));
            var height = SafeToInt(Math.Ceiling(bounds.Height));
            if (width < MinimumSide || height < MinimumSide)
            {
                return null;
            }

            var bitmap = new Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                using (var graphics = Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        x, y, 0, 0,
                        new Drawing.Size(width, height),
                        Drawing.CopyPixelOperation.SourceCopy | Drawing.CopyPixelOperation.CaptureBlt);
                }
                var preview = CreateFrozenPreview(bitmap, width, height);
                return new FrozenDesktopSnapshot(new Rect(x, y, width, height), bitmap, preview);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch
        {
            return null;
        }
    }

    public static Task<byte[]> CropPngAsync(FrozenDesktopSnapshot snapshot, Rect pixelBounds) =>
        Task.Run(() => CropPng(snapshot, pixelBounds));

    public static byte[] CropPng(FrozenDesktopSnapshot snapshot, Rect pixelBounds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (srcX, srcY, width, height) = ValidateAndClampRegion(pixelBounds, snapshot.VirtualBounds);
        var srcRect = new Drawing.Rectangle(srcX, srcY, width, height);
        using var cropped = snapshot.Bitmap.Clone(srcRect, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        return EncodePng(cropped);
    }

    public static Task<byte[]> CapturePngAsync(Rect pixelBounds) =>
        Task.Run(() => CapturePng(pixelBounds));

    public static byte[] CapturePng(Rect pixelBounds)
    {
        var (x, y, width, height) = ValidateRegion(pixelBounds);

        using var bitmap = new Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                x, y, 0, 0,
                new Drawing.Size(width, height),
                Drawing.CopyPixelOperation.SourceCopy | Drawing.CopyPixelOperation.CaptureBlt);
        }

        return EncodePng(bitmap);
    }

    internal static (int X, int Y, int Width, int Height) ValidateRegion(Rect pixelBounds)
    {
        var x = SafeToInt(Math.Floor(pixelBounds.X));
        var y = SafeToInt(Math.Floor(pixelBounds.Y));
        var width = SafeToInt(Math.Ceiling(pixelBounds.Width));
        var height = SafeToInt(Math.Ceiling(pixelBounds.Height));

        if (width < MinimumSide || height < MinimumSide)
        {
            throw new InvalidOperationException(
                $"选区太小（{width}×{height}）。请拖出至少 {MinimumSide}×{MinimumSide} 像素的区域。");
        }
        if ((long)width * height > MaxPixels)
        {
            throw new InvalidOperationException("截图区域超过 1600 万像素上限，请缩小选区。");
        }

        return (x, y, width, height);
    }

    internal static (int OffsetX, int OffsetY, int Width, int Height) ValidateAndClampRegion(
        Rect pixelBounds,
        Rect containerBounds)
    {
        var x = SafeToInt(Math.Floor(pixelBounds.X));
        var y = SafeToInt(Math.Floor(pixelBounds.Y));
        var width = SafeToInt(Math.Ceiling(pixelBounds.Width));
        var height = SafeToInt(Math.Ceiling(pixelBounds.Height));

        var vx = SafeToInt(Math.Floor(containerBounds.X));
        var vy = SafeToInt(Math.Floor(containerBounds.Y));
        var vw = SafeToInt(Math.Ceiling(containerBounds.Width));
        var vh = SafeToInt(Math.Ceiling(containerBounds.Height));

        var clampedLeft = Math.Max(x, vx);
        var clampedTop = Math.Max(y, vy);
        var clampedRight = Math.Min((long)x + width, (long)vx + vw);
        var clampedBottom = Math.Min((long)y + height, (long)vy + vh);
        var clampedWidth = (int)Math.Max(0, clampedRight - clampedLeft);
        var clampedHeight = (int)Math.Max(0, clampedBottom - clampedTop);

        if (clampedWidth < MinimumSide || clampedHeight < MinimumSide)
        {
            throw new InvalidOperationException(
                $"选区太小（{clampedWidth}×{clampedHeight}）。请拖出至少 {MinimumSide}×{MinimumSide} 像素的区域。");
        }
        if ((long)clampedWidth * clampedHeight > MaxPixels)
        {
            throw new InvalidOperationException("截图区域超过 1600 万像素上限，请缩小选区。");
        }

        return (clampedLeft - vx, clampedTop - vy, clampedWidth, clampedHeight);
    }

    private static BitmapSource CreateFrozenPreview(Drawing.Bitmap bitmap, int width, int height)
    {
        var data = bitmap.LockBits(
            new Drawing.Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            var preview = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32,
                null,
                data.Scan0,
                data.Stride * height,
                data.Stride);
            preview.Freeze();
            return preview;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte[] EncodePng(Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        if (stream.Length > MaxEncodedBytes)
        {
            throw new InvalidOperationException("截图编码后超过 8 MiB，请缩小选区。");
        }
        return stream.ToArray();
    }

    private static int SafeToInt(double value) =>
        double.IsFinite(value)
            ? (int)Math.Clamp(value, int.MinValue, int.MaxValue)
            : throw new InvalidOperationException("截图区域坐标无效。");
}
