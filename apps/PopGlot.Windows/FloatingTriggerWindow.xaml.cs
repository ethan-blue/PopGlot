using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PopGlot.Windows;

/// <summary>
/// A sleek, subtle floating icon that appears near the cursor when text is selected.
/// Clicking it instantly pops up the translation panel.
/// </summary>
public partial class FloatingTriggerWindow : Window
{
    /// <summary>Placement offsets: the trigger floats just above-right of the cursor.</summary>
    private const double CursorOffsetX = 10;
    private const double CursorOffsetY = 36;
    private const double MinScreenMargin = 10;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly Point _screenPos;
    private readonly Action _onTrigger;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly EventHandler _themeChangedHandler;

    private readonly ScaleTransform _buttonScale = new(1.0, 1.0);
    private readonly TranslateTransform _buttonTranslate = new(0, 0);

    private bool _isHovered;
    private bool _isClosing;
    // DPI transitions reposition exactly once, at ApplicationIdle.
    private bool _dpiRepositionPending;

    public FloatingTriggerWindow(Point screenPos, Action onTrigger)
    {
        _screenPos = screenPos;
        _onTrigger = onTrigger;
        InitializeComponent();

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_buttonScale);
        transformGroup.Children.Add(_buttonTranslate);
        ButtonSurface.RenderTransform = transformGroup;

        _themeChangedHandler = (_, _) =>
        {
            if (Dispatcher.CheckAccess())
            {
                ThemeService.ApplyWindowChrome(this);
            }
            else
            {
                _ = Dispatcher.BeginInvoke(() => ThemeService.ApplyWindowChrome(this));
            }
        };
        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += _themeChangedHandler;
        Closed += (_, _) => ThemeService.ThemeChanged -= _themeChangedHandler;

        // Same single source as every later reposition: the shared
        // RepositionNearAnchor (target-monitor scale, one landing).
        RepositionNearAnchor();

        _autoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _autoHideTimer.Tick += (_, _) =>
        {
            if (!_isHovered)
            {
                FadeOutAndClose();
            }
        };

        Loaded += (_, _) =>
        {
            RepositionNearAnchor();

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var scaleIn = new DoubleAnimation(0.75, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
            };
            var slideIn = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            BeginAnimation(OpacityProperty, fadeIn);
            _buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
            _buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
            _buttonTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);

            _autoHideTimer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            try
            {
                var exStyle = NativeMethods.GetWindowLongPtr(handle, GWL_EXSTYLE);
                _ = NativeMethods.SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)((long)exStyle | WS_EX_NOACTIVATE));
            }
            catch
            {
                // Best effort non-activating window style
            }
        }
        RepositionNearAnchor();
    }

    /// <summary>
    /// E3 DPI: one reposition at ApplicationIdle after a WM_DPICHANGED — the
    /// window's own scale is stale mid-transition, so the reposition waits for
    /// the settle point and runs through the single shared writer.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_isClosing || _dpiRepositionPending)
        {
            return;
        }
        _dpiRepositionPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _dpiRepositionPending = false;
            if (_isClosing)
            {
                return;
            }
            RepositionNearAnchor();
        }));
    }

    private void RepositionNearAnchor()
    {
        var workArea = ScreenGeometry.WorkAreaForPixel(_screenPos);
        // Target-monitor scale from the anchor point — never the window's own
        // (mid-transition stale) DPI.
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(_screenPos);
        var scaleX = scale.X > 0 ? scale.X : 1.0;
        var scaleY = scale.Y > 0 ? scale.Y : 1.0;

        var workLeftDip = ScreenGeometry.PixelToDip(workArea.Left, scaleX);
        var workTopDip = ScreenGeometry.PixelToDip(workArea.Top, scaleY);
        var workRightDip = ScreenGeometry.PixelToDip(workArea.Right, scaleX);
        var workBottomDip = ScreenGeometry.PixelToDip(workArea.Bottom, scaleY);

        var cursorXDip = ScreenGeometry.PixelToDip(_screenPos.X, scaleX);
        var cursorYDip = ScreenGeometry.PixelToDip(_screenPos.Y, scaleY);

        var targetLeft = cursorXDip + CursorOffsetX;
        var targetTop = cursorYDip - CursorOffsetY;

        var widthDip = Width > 0 ? Width : 48;
        var heightDip = Height > 0 ? Height : 48;

        var leftDip = Math.Clamp(targetLeft, workLeftDip + MinScreenMargin, Math.Max(workLeftDip + MinScreenMargin, workRightDip - widthDip - MinScreenMargin));
        var topDip = Math.Clamp(targetTop, workTopDip + MinScreenMargin, Math.Max(workTopDip + MinScreenMargin, workBottomDip - heightDip - MinScreenMargin));

        // ONE authoritative landing: the HWND move; WPF re-derives Left/Top
        // from WM_WINDOWPOSCHANGED. Writing Left/Top and then MoveToPixels
        // drove the same spot through two unit systems.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            // Pre-show: no HWND yet, the WPF properties carry the position.
            Left = leftDip;
            Top = topDip;
            return;
        }
        ScreenGeometry.MoveToPixels(this, new Point(leftDip * scaleX, topDip * scaleY));
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_isClosing) return;
        _autoHideTimer.Stop();

        // Subtle tactile press compression
        var pressAnim = new DoubleAnimation(_buttonScale.ScaleX, 0.93, TimeSpan.FromMilliseconds(45))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        pressAnim.Completed += (_, _) =>
        {
            _onTrigger?.Invoke();
            FadeOutAndClose();
        };
        _buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, pressAnim);
        _buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, pressAnim);
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        FadeOutAndClose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FadeOutAndClose();
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        AnimateHover(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        AnimateHover(false);
        _autoHideTimer.Interval = TimeSpan.FromSeconds(1.5);
        _autoHideTimer.Start();
    }

    private void AnimateHover(bool isHovered)
    {
        if (_isClosing) return;

        var targetScale = isHovered ? 1.08 : 1.0;
        var duration = TimeSpan.FromMilliseconds(isHovered ? 140 : 180);
        var scaleAnim = new DoubleAnimation(_buttonScale.ScaleX, targetScale, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        _buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);

        var glowAnim = new DoubleAnimation(GlowHalo.Opacity, isHovered ? 0.85 : 0.45, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        GlowHalo.BeginAnimation(OpacityProperty, glowAnim);

        if (ButtonSurface.Effect is DropShadowEffect shadowEffect)
        {
            var shadowAnim = new DoubleAnimation(shadowEffect.Opacity, isHovered ? 0.40 : 0.28, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            shadowEffect.BeginAnimation(DropShadowEffect.OpacityProperty, shadowAnim);
        }

        if (isHovered)
        {
            ButtonSurface.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            ButtonSurface.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            TranslateIcon.SetResourceReference(Shape.FillProperty, "AccentHoverBrush");
        }
        else
        {
            ButtonSurface.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
            ButtonSurface.SetResourceReference(Border.BorderBrushProperty, "AccentBorderBrush");
            TranslateIcon.SetResourceReference(Shape.FillProperty, "AccentBrush");
        }
    }

    public void FadeOutAndClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(FadeOutAndClose);
            return;
        }
        if (_isClosing) return;
        _isClosing = true;
        _autoHideTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var scaleOut = new DoubleAnimation(_buttonScale.ScaleX, 0.85, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var slideOut = new DoubleAnimation(_buttonTranslate.Y, 3, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        fadeOut.Completed += (_, _) =>
        {
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
            }
        };

        BeginAnimation(OpacityProperty, fadeOut);
        _buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        _buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);
        _buttonTranslate.BeginAnimation(TranslateTransform.YProperty, slideOut);
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
