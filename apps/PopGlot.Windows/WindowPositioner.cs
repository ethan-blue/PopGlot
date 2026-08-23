using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace PopGlot.Windows;

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
        return new Point(
            Math.Clamp(anchor.Right + Gap, workArea.Left + Edge, workArea.Right - window.Width - Edge),
            Math.Clamp(anchor.Top, workArea.Top + Edge, workArea.Bottom - window.Height - Edge));
    }
}

internal static class ScreenWorkArea
{
    public static Rect ForAnchor(Rect anchor)
    {
        var center = new System.Drawing.Point(
            checked((int)Math.Round(anchor.Left + anchor.Width / 2)),
            checked((int)Math.Round(anchor.Top + anchor.Height / 2)));
        var screen = System.Windows.Forms.Screen.FromPoint(center);
        if (screen.Primary)
        {
            return SystemParameters.WorkArea;
        }
        var work = screen.WorkingArea;
        return new Rect(work.X, work.Y, work.Width, work.Height);
    }
}
