using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PopGlot.Windows;

/// <summary>
/// Chooses where a popup sits relative to what triggered it.
/// </summary>
/// <remarks>
/// Every value here is in physical pixels. The previous version mixed WPF
/// device-independent units (<c>SystemParameters.WorkArea</c>) with raw device
/// coordinates from Win32, which placed the panel off-target on any secondary
/// or scaled monitor.
/// </remarks>
internal static class WindowPositioner
{
    private const double Gap = 14;
    private const double Edge = 12;

    public static Point NearAnchor(Rect anchor, Size window, Rect workArea)
    {
        var candidates = new[]
        {
            new Point(anchor.Right + Gap, anchor.Top),
            new Point(anchor.Left, anchor.Bottom + Gap),
            new Point(anchor.Left - window.Width - Gap, anchor.Top),
            new Point(anchor.Left, anchor.Top - window.Height - Gap),
        };
        foreach (var candidate in candidates)
        {
            if (candidate.X >= workArea.Left + Edge &&
                candidate.Y >= workArea.Top + Edge &&
                candidate.X + window.Width <= workArea.Right - Edge &&
                candidate.Y + window.Height <= workArea.Bottom - Edge)
            {
                return candidate;
            }
        }

        // Nothing fits cleanly: clamp inside the work area. Math.Clamp throws
        // when the window is larger than the monitor, so order the bounds.
        return new Point(
            ClampToRange(anchor.Right + Gap, workArea.Left + Edge, workArea.Right - window.Width - Edge),
            ClampToRange(anchor.Top, workArea.Top + Edge, workArea.Bottom - window.Height - Edge));
    }

    private static double ClampToRange(double value, double low, double high) =>
        high < low ? low : Math.Clamp(value, low, high);
}

/// <summary>
/// Monitor geometry and DPI helpers, all in physical pixels.
/// </summary>
internal static partial class ScreenGeometry
{
    /// <summary>Work area (excluding the taskbar) of the monitor holding a point.</summary>
    public static Rect WorkAreaForPixel(Point devicePoint)
    {
        var screen = Forms.Screen.FromPoint(
            new Drawing.Point(SafeRound(devicePoint.X), SafeRound(devicePoint.Y)));
        var work = screen.WorkingArea;
        return new Rect(work.X, work.Y, work.Width, work.Height);
    }

    public static Rect WorkAreaForAnchor(Rect anchorPixels) =>
        WorkAreaForPixel(new Point(
            anchorPixels.Left + (anchorPixels.Width / 2),
            anchorPixels.Top + (anchorPixels.Height / 2)));

    /// <summary>Whole virtual desktop in physical pixels.</summary>
    public static Rect VirtualScreenPixels()
    {
        var bounds = Forms.SystemInformation.VirtualScreen;
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    public static Point CursorPixels()
    {
        var position = Forms.Cursor.Position;
        return new Point(position.X, position.Y);
    }

    /// <summary>DPI scale currently applied to a window's visual tree.</summary>
    public static (double X, double Y) ScaleOf(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>
    /// Moves a window to an exact physical-pixel position.
    /// </summary>
    /// <remarks>
    /// Assigning <c>Window.Left</c>/<c>Top</c> goes through WPF's own unit
    /// conversion, which is not reliable across monitors with different scale
    /// factors. Positioning the HWND directly is exact everywhere.
    /// </remarks>
    public static void MoveToPixels(Window window, Point topLeft)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }
        const uint SwpNoSize = 0x0001;
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;
        _ = NativeMethods.SetWindowPos(
            handle,
            0,
            SafeRound(topLeft.X),
            SafeRound(topLeft.Y),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Sizes and positions a window to an exact physical-pixel rect.</summary>
    public static void ResizeToPixels(Window window, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;
        _ = NativeMethods.SetWindowPos(
            handle,
            0,
            SafeRound(bounds.X),
            SafeRound(bounds.Y),
            SafeRound(bounds.Width),
            SafeRound(bounds.Height),
            SwpNoZOrder | SwpNoActivate);
    }

    private static int SafeRound(double value) =>
        double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, int.MinValue, int.MaxValue)) : 0;

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
