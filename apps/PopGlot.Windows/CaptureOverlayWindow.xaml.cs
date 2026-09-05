using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Threading;

namespace PopGlot.Windows;

/// <summary>Result of a completed screen selection, in physical pixels.</summary>
internal sealed record ScreenCapture(Rect PixelBounds, byte[] Png, bool IsOcrOnly = false);

/// <summary>
/// Full-desktop marquee for picking a region to translate or extract text.
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private Point? _dragStart;
    private bool _completed;
    private bool _closing;
    private bool _forceOcrMode;
    private long _lastBadgeUpdate;
    private int _lastPixelWidth = -1;
    private int _lastPixelHeight = -1;

    public CaptureOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    internal void SetOcrOnlyMode(bool ocrOnly)
    {
        _forceOcrMode = ocrOnly;
        if (ocrOnly)
        {
            HintDetail.Text = "框选文本区域 · Esc 取消";
        }
    }

    /// <summary>Raised once a region has been captured successfully.</summary>
    internal event EventHandler<ScreenCapture>? Captured;

    /// <summary>Raised when capture was attempted but failed.</summary>
    internal event EventHandler<string>? Failed;

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        // Size the HWND to the whole virtual desktop in physical pixels. Setting
        // Width/Height in WPF units instead leaves gaps on mixed-DPI setups.
        var bounds = ScreenGeometry.VirtualScreenPixels();
        ScreenGeometry.ResizeToPixels(this, bounds);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
        PositionHintNearCursor();
        UpdateCrosshair(Mouse.GetPosition(this));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        CaptureMouse();
        ShadeFull.Visibility = Visibility.Collapsed;
        HintChip.Visibility = Visibility.Collapsed;
        CrossHorizontal.Visibility = Visibility.Collapsed;
        CrossVertical.Visibility = Visibility.Collapsed;
        SetShadeVisibility(Visibility.Visible);
        SelectionBorder.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        SetHandleVisibility(Visibility.Visible);
        UpdateSelection(_dragStart.Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        UpdateCrosshair(position);
        if (_dragStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(position);
        }
    }

    protected override async void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is null || _completed)
        {
            return;
        }

        var start = _dragStart.Value;
        var end = e.GetPosition(this);
        _dragStart = null;
        ReleaseMouseCapture();

        // Both corners go through PointToScreen so the rectangle lands in real
        // desktop pixels regardless of which monitor (and scale) it spans.
        var pixelRect = Normalize(PointToScreen(start), PointToScreen(end));
        if (pixelRect.Width < 6 || pixelRect.Height < 6)
        {
            Close();
            return;
        }

        _completed = true;
        var isOcrMode = _forceOcrMode || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        await CaptureAndCloseAsync(pixelRect, isOcrMode);
    }

    /// <summary>Right-click is the conventional "never mind" for a marquee.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>
    /// Losing activation means something else took the foreground; keeping a
    /// full-screen transparent window alive over it would trap the user.
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Closing raises WM_ACTIVATE itself, and calling Close() again from
        // inside that message throws.
        if (!_completed && !_closing)
        {
            Close();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private async Task CaptureAndCloseAsync(Rect pixelRect, bool isOcrOnly = false)
    {
        Hide();
        try
        {
            // Yield through render priority so the hidden overlay is committed
            // before capture. The old fixed 60 ms delay made every screenshot
            // feel sticky even on a fast compositor.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(16);

            var png = await ScreenCaptureService.CapturePngAsync(pixelRect);
            Close();
            Captured?.Invoke(this, new ScreenCapture(pixelRect, png, isOcrOnly));
        }
        catch (Exception exception)
        {
            Close();
            Failed?.Invoke(this, exception.Message);
        }
    }

    private void UpdateCrosshair(Point position)
    {
        CrossHorizontal.X1 = 0;
        CrossHorizontal.X2 = ActualWidth;
        CrossHorizontal.Y1 = position.Y;
        CrossHorizontal.Y2 = position.Y;
        CrossVertical.Y1 = 0;
        CrossVertical.Y2 = ActualHeight;
        CrossVertical.X1 = position.X;
        CrossVertical.X2 = position.X;
    }

    private void UpdateSelection(Point current)
    {
        if (_dragStart is null)
        {
            return;
        }
        var rect = Normalize(_dragStart.Value, current);
        Place(SelectionBorder, rect.X, rect.Y, rect.Width, rect.Height);
        Place(ShadeTop, 0, 0, ActualWidth, rect.Top);
        Place(ShadeLeft, 0, rect.Top, rect.Left, rect.Height);
        Place(ShadeRight, rect.Right, rect.Top, Math.Max(0, ActualWidth - rect.Right), rect.Height);
        Place(ShadeBottom, 0, rect.Bottom, ActualWidth, Math.Max(0, ActualHeight - rect.Bottom));

        PlaceHandle(HandleTopLeft, rect.Left, rect.Top);
        PlaceHandle(HandleTopRight, rect.Right, rect.Top);
        PlaceHandle(HandleBottomLeft, rect.Left, rect.Bottom);
        PlaceHandle(HandleBottomRight, rect.Right, rect.Bottom);

        // Geometry follows every pointer event. Text/layout is capped to one
        // update per display frame; forcing UpdateLayout on every MouseMove was
        // the primary source of marquee lag.
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastBadgeUpdate, now);
        var scale = ScreenGeometry.ScaleOf(this);
        var pixelWidth = (int)Math.Round(rect.Width * scale.X);
        var pixelHeight = (int)Math.Round(rect.Height * scale.Y);
        if (_lastBadgeUpdate == 0 || elapsed >= TimeSpan.FromMilliseconds(16))
        {
            _lastBadgeUpdate = now;
            if (pixelWidth != _lastPixelWidth || pixelHeight != _lastPixelHeight)
            {
                _lastPixelWidth = pixelWidth;
                _lastPixelHeight = pixelHeight;
                SizeText.Text = $"{pixelWidth} × {pixelHeight} px";
            }
        }

        var badgeWidth = SizeBadge.ActualWidth;
        var badgeHeight = SizeBadge.ActualHeight;
        Canvas.SetLeft(SizeBadge, Math.Clamp(rect.X, 0, Math.Max(0, ActualWidth - badgeWidth)));
        Canvas.SetTop(
            SizeBadge,
            rect.Bottom + 8 + badgeHeight <= ActualHeight
                ? rect.Bottom + 8
                : Math.Max(0, rect.Top - badgeHeight - 8));
    }

    private void PositionHintNearCursor()
    {
        HintChip.UpdateLayout();
        var work = ScreenGeometry.WorkAreaForPixel(ScreenGeometry.CursorPixels());
        var scale = ScreenGeometry.ScaleOf(this);
        // Convert the monitor's work area into this window's coordinate space.
        var origin = PointFromScreen(new Point(work.Left, work.Top));
        var localWidth = work.Width / scale.X;
        var localHeight = work.Height / scale.Y;
        Canvas.SetLeft(HintChip, origin.X + ((localWidth - HintChip.ActualWidth) / 2));
        Canvas.SetTop(HintChip, origin.Y + (localHeight * 0.12));
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static void PlaceHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - (handle.Width / 2));
        Canvas.SetTop(handle, y - (handle.Height / 2));
    }

    private void SetShadeVisibility(Visibility visibility)
    {
        ShadeTop.Visibility = visibility;
        ShadeLeft.Visibility = visibility;
        ShadeRight.Visibility = visibility;
        ShadeBottom.Visibility = visibility;
    }

    private void SetHandleVisibility(Visibility visibility)
    {
        HandleTopLeft.Visibility = visibility;
        HandleTopRight.Visibility = visibility;
        HandleBottomLeft.Visibility = visibility;
        HandleBottomRight.Visibility = visibility;
    }

    internal static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(first.X - second.X),
        Math.Abs(first.Y - second.Y));
}
