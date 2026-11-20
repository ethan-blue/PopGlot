using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PopGlot.Windows;

/// <summary>
/// Non-activating, click-through screen outline overlay used to flash or preview a
/// fixed capture region without stealing focus from fullscreen/borderless games or apps.
/// </summary>
public partial class RegionHighlightWindow : Window
{
    internal const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;

    private const double FramePadDip = 4.0;
    private const double LabelBandDip = 28.0;

    private readonly DispatcherTimer _dismissTimer;
    private bool _showLabel;
    private bool _labelInsideFrame;
    private TimeSpan _fadeDuration = TimeSpan.FromMilliseconds(170);
    private bool _closing;
    private bool _dismissed;
    private int _dpiGeneration;

    public RegionHighlightWindow()
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += (_, _) => BeginFadeOutAndClose();
    }

    internal Rect TargetPixelRect { get; private set; }
    internal bool IsDismissed => _dismissed;

    /// <summary>
    /// Shows a brief ~350ms outline flash around <paramref name="pixelRect"/> without
    /// taking focus or blocking pointer clicks.
    /// </summary>
    internal void ShowFlash(Rect pixelRect, TimeSpan? holdDuration = null)
    {
        ConfigureAndShow(
            pixelRect,
            label: null,
            hold: holdDuration ?? TimeSpan.FromMilliseconds(180),
            fade: TimeSpan.FromMilliseconds(170));
    }

    /// <summary>
    /// Shows a ~2.4s preview outline with a region badge around <paramref name="pixelRect"/>.
    /// </summary>
    internal void ShowPreview(Rect pixelRect, string label, TimeSpan? holdDuration = null)
    {
        ConfigureAndShow(
            pixelRect,
            label: label,
            hold: holdDuration ?? TimeSpan.FromMilliseconds(2150),
            fade: TimeSpan.FromMilliseconds(250));
    }

    /// <summary>
    /// Immediately hides and closes this highlight so it is never captured into a screenshot.
    /// </summary>
    internal void DismissImmediately()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(DismissImmediately);
            return;
        }
        if (_dismissed)
        {
            return;
        }
        _dismissed = true;
        _closing = true;
        _dismissTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        try
        {
            if (IsVisible)
            {
                Hide();
            }
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing or closed.
        }
    }

    internal static long ReadExtendedWindowStyle(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        return handle == 0 ? 0 : (long)NativeMethods.GetWindowLongPtr(handle, GwlExStyle);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            try
            {
                var exStyle = (long)NativeMethods.GetWindowLongPtr(handle, GwlExStyle);
                var updated = exStyle | WsExNoActivate | WsExTransparent | WsExToolWindow;
                _ = NativeMethods.SetWindowLongPtr(handle, GwlExStyle, (nint)updated);
            }
            catch
            {
                // Best effort Win32 extended styles.
            }
        }
        ApplyPixelBounds();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_closing || _dismissed)
        {
            return;
        }
        var generation = ++_dpiGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (generation != _dpiGeneration || _closing || _dismissed)
            {
                return;
            }
            ApplyPixelBounds();
        }));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _dismissed = true;
        _dismissTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        base.OnClosing(e);
    }

    private void ConfigureAndShow(Rect pixelRect, string? label, TimeSpan hold, TimeSpan fade)
    {
        TargetPixelRect = pixelRect;
        _fadeDuration = fade;
        _showLabel = !string.IsNullOrWhiteSpace(label);

        var center = new Point(pixelRect.X + (pixelRect.Width / 2), pixelRect.Y + (pixelRect.Height / 2));
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(center);
        var scaleY = scale.Y > 0 ? scale.Y : 1.0;
        var virtualBounds = ScreenGeometry.VirtualScreenPixels();

        // Place the preview badge above the outline when headroom exists; near the
        // top screen edge tuck it inside the top-left corner so it never clips.
        _labelInsideFrame = _showLabel && (pixelRect.Y - virtualBounds.Top) < (LabelBandDip + FramePadDip) * scaleY;

        if (_showLabel)
        {
            PreviewLabelText.Text = label!;
            PreviewBadge.Visibility = Visibility.Visible;
            if (_labelInsideFrame)
            {
                HighlightFrame.Margin = new Thickness(FramePadDip);
                PreviewBadge.Margin = new Thickness(FramePadDip + 8, FramePadDip + 8, FramePadDip + 8, 0);
            }
            else
            {
                HighlightFrame.Margin = new Thickness(FramePadDip, LabelBandDip, FramePadDip, FramePadDip);
                PreviewBadge.Margin = new Thickness(FramePadDip, 0, FramePadDip, 0);
            }
        }
        else
        {
            PreviewBadge.Visibility = Visibility.Collapsed;
            HighlightFrame.Margin = new Thickness(FramePadDip);
        }

        Opacity = 0.96;
        Show();
        ApplyPixelBounds();

        _dismissTimer.Stop();
        _dismissTimer.Interval = hold;
        _dismissTimer.Start();
    }

    private void ApplyPixelBounds()
    {
        if (TargetPixelRect.IsEmpty || TargetPixelRect.Width <= 0 || TargetPixelRect.Height <= 0)
        {
            return;
        }
        var center = new Point(
            TargetPixelRect.X + (TargetPixelRect.Width / 2),
            TargetPixelRect.Y + (TargetPixelRect.Height / 2));
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(center);
        var scaleX = scale.X > 0 ? scale.X : 1.0;
        var scaleY = scale.Y > 0 ? scale.Y : 1.0;

        var topPadDip = _showLabel && !_labelInsideFrame ? LabelBandDip : FramePadDip;
        var windowRectPixels = new Rect(
            TargetPixelRect.X - (FramePadDip * scaleX),
            TargetPixelRect.Y - (topPadDip * scaleY),
            TargetPixelRect.Width + (FramePadDip * 2 * scaleX),
            TargetPixelRect.Height + ((topPadDip + FramePadDip) * scaleY));

        ScreenGeometry.ResizeToPixels(this, windowRectPixels);
    }

    private void BeginFadeOutAndClose()
    {
        _dismissTimer.Stop();
        if (_closing || _dismissed)
        {
            return;
        }
        _closing = true;

        var fadeOut = new DoubleAnimation(Opacity, 0.0, _fadeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            _dismissed = true;
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // Already closed.
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static partial int GetWindowLong32(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

        internal static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

        internal static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, (int)dwNewLong);
    }
}
