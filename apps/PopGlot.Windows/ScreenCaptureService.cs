using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace PopGlot.Windows;

internal static class ScreenCaptureService
{
    private const long MaxPixels = 16_000_000;
    private const int MaxEncodedBytes = 8 * 1024 * 1024;

    public static byte[] CapturePng(Rect pixelBounds)
    {
        var x = checked((int)Math.Floor(pixelBounds.X));
        var y = checked((int)Math.Floor(pixelBounds.Y));
        var width = checked((int)Math.Ceiling(pixelBounds.Width));
        var height = checked((int)Math.Ceiling(pixelBounds.Height));
        if (width < 6 || height < 6 || (long)width * height > MaxPixels)
        {
            throw new InvalidOperationException("截图区域无效或超过 1600 万像素上限。");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        if (stream.Length > MaxEncodedBytes)
        {
            throw new InvalidOperationException("截图编码后超过 8 MiB，请缩小选区。");
        }
        return stream.ToArray();
    }
}
