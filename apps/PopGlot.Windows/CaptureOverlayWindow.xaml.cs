using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace PopGlot.Windows;

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

    public event EventHandler<Rect>? SelectionCompleted;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        CaptureMouse();
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
        _start = null;
        ReleaseMouseCapture();
        Close();
        if (local.Width >= 6 && local.Height >= 6)
        {
            SelectionCompleted?.Invoke(
                this,
                new Rect(local.X + Left, local.Y + Top, local.Width, local.Height));
        }
    }

    private void UpdateSelection(Point current)
    {
        if (_start is null)
        {
            return;
        }
        var rect = Normalize(_start.Value, current);
        Canvas.SetLeft(SelectionBorder, rect.X);
        Canvas.SetTop(SelectionBorder, rect.Y);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        Canvas.SetLeft(SizeBadge, rect.X);
        Canvas.SetTop(SizeBadge, Math.Max(0, rect.Bottom + 8));
        SizeText.Text = $"{rect.Width:0} × {rect.Height:0}";
    }

    private static Rect Normalize(Point first, Point second) => new(
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
