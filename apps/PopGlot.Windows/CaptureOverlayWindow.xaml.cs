using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace PopGlot.Windows;

internal sealed record ScreenSelection(Rect DisplayBounds, Rect PixelBounds);

public partial class CaptureOverlayWindow : Window
{
    private Point? _start;

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    internal event EventHandler<ScreenSelection>? SelectionCompleted;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        CaptureMouse();
        InitialShade.Visibility = Visibility.Collapsed;
        SetShadeVisibility(Visibility.Visible);
        SelectionBorder.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        UpdateSelection(_start.Value);
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_start is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(e.GetPosition(this));
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null)
        {
            return;
        }
        var local = Normalize(_start.Value, e.GetPosition(this));
        var screenTopLeft = PointToScreen(local.TopLeft);
        var screenBottomRight = PointToScreen(local.BottomRight);
        _start = null;
        ReleaseMouseCapture();
        Close();
        if (local.Width >= 6 && local.Height >= 6)
        {
            SelectionCompleted?.Invoke(
                this,
                new ScreenSelection(
                    new Rect(local.X + Left, local.Y + Top, local.Width, local.Height),
                    Normalize(screenTopLeft, screenBottomRight)));
        }
    }

    private void UpdateSelection(Point current)
    {
        if (_start is null)
        {
            return;
        }
        var rect = Normalize(_start.Value, current);
        Place(SelectionBorder, rect.X, rect.Y, rect.Width, rect.Height);
        Place(ShadeTop, 0, 0, ActualWidth, rect.Top);
        Place(ShadeLeft, 0, rect.Top, rect.Left, rect.Height);
        Place(ShadeRight, rect.Right, rect.Top, Math.Max(0, ActualWidth - rect.Right), rect.Height);
        Place(ShadeBottom, 0, rect.Bottom, ActualWidth, Math.Max(0, ActualHeight - rect.Bottom));
        Canvas.SetLeft(SizeBadge, Math.Min(rect.X, Math.Max(0, ActualWidth - 104)));
        Canvas.SetTop(SizeBadge, rect.Bottom + 8 <= ActualHeight - 34
            ? rect.Bottom + 8
            : Math.Max(0, rect.Top - 32));
        SizeText.Text = $"{rect.Width:0} × {rect.Height:0}";
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private void SetShadeVisibility(Visibility visibility)
    {
        ShadeTop.Visibility = visibility;
        ShadeLeft.Visibility = visibility;
        ShadeRight.Visibility = visibility;
        ShadeBottom.Visibility = visibility;
    }

    internal static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
