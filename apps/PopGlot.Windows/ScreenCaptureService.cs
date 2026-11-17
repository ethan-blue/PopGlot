using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
internal static partial class ScreenCaptureService
{
    private const long MaxPixels = 16_000_000;
    private const int MaxEncodedBytes = 8 * 1024 * 1024;
    private const int MinimumSide = 6;

    private const int RasterOperationSourceCopy = 0x00CC0020;
    private const int RasterOperationCaptureBlt = 0x40000000;

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

            var bitmap = BlitScreenRegion(x, y, width, height);
            try
            {
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

        using var bitmap = BlitScreenRegion(x, y, width, height);
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

    /// <summary>
    /// Blits a physical-pixel screen region into a fresh bitmap with
    /// layered-window (CAPTUREBLT) semantics so hover tooltips, menus, and
    /// popups frozen mid-interaction are part of the capture.
    /// </summary>
    /// <remarks>
    /// <c>Graphics.CopyFromScreen</c> validates its operation argument and
    /// rejects the <c>SourceCopy | CaptureBlt</c> combination (the enum is
    /// not marked [Flags]), so both capture paths threw
    /// <c>InvalidEnumArgumentException</c> on .NET Core / .NET 5+ and every
    /// screenshot failed. Raw <c>BitBlt</c> keeps the layered-window
    /// semantics; every GDI handle is released even when a step fails.
    /// </remarks>
    private static Drawing.Bitmap BlitScreenRegion(int x, int y, int width, int height)
    {
        var screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            throw new InvalidOperationException("无法访问屏幕设备上下文，截图失败。");
        }
        try
        {
            var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero)
            {
                throw new InvalidOperationException("无法创建截图内存设备上下文。");
            }
            try
            {
                var compatibleBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
                if (compatibleBitmap == nint.Zero)
                {
                    throw new OutOfMemoryException($"截图位图分配失败（{width}×{height}）。");
                }
                Drawing.Bitmap bitmap;
                var previousObject = nint.Zero;
                try
                {
                    previousObject = NativeMethods.SelectObject(memoryDc, compatibleBitmap);
                    // SelectObject signals failure with NULL (or HGDI_ERROR for
                    // region objects); blitting into an unselected bitmap would
                    // read stale memory, and re-selecting the bogus return
                    // value would poison the DC — bail out to the cleanup path.
                    if (previousObject == nint.Zero || previousObject == new nint(-1))
                    {
                        previousObject = nint.Zero;
                        throw new InvalidOperationException("截图位图无法选入设备上下文。");
                    }
                    if (!NativeMethods.BitBlt(
                            memoryDc, 0, 0, width, height,
                            screenDc, x, y,
                            RasterOperationSourceCopy | RasterOperationCaptureBlt))
                    {
                        throw new InvalidOperationException(
                            $"屏幕捕获失败（GDI 错误 {Marshal.GetLastWin32Error()}）。");
                    }
                    // FromHbitmap copies the pixels into a GDI+ owned bitmap so
                    // the GDI side can be released immediately.
                    bitmap = Drawing.Image.FromHbitmap(compatibleBitmap);
                }
                finally
                {
                    if (previousObject != nint.Zero)
                    {
                        NativeMethods.SelectObject(memoryDc, previousObject);
                    }
                    NativeMethods.DeleteObject(compatibleBitmap);
                }
                return bitmap;
            }
            finally
            {
                NativeMethods.DeleteDC(memoryDc);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
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

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint GetDC(nint hWnd);

        [LibraryImport("user32.dll")]
        internal static partial int ReleaseDC(nint hWnd, nint hDC);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateCompatibleDC(nint hdc);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteDC(nint hdc);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint CreateCompatibleBitmap(nint hdc, int width, int height);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeleteObject(nint hObject);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        internal static partial nint SelectObject(nint hdc, nint hObject);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool BitBlt(
            nint hdcDestination,
            int xDestination,
            int yDestination,
            int width,
            int height,
            nint hdcSource,
            int xSource,
            int ySource,
            int rasterOperation);
    }
}
