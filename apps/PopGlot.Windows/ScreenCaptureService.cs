using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace PopGlot.Windows;

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

    public static byte[] CapturePng(Rect pixelBounds)
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

        using var bitmap = new Drawing.Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Drawing.Size(width, height));
        }

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
