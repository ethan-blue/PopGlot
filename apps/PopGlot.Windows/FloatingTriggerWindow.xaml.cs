using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PopGlot.Windows;

/// <summary>
/// A sleek, subtle floating icon that appears near the cursor when text is selected.
/// Clicking it instantly pops up the translation panel.
/// </summary>
public partial class FloatingTriggerWindow : Window
{
    private readonly Action _onTrigger;
    private readonly DispatcherTimer _autoHideTimer;
    private bool _isHovered;
    private bool _isClosing;

    public FloatingTriggerWindow(Point screenPos, Action onTrigger)
    {
        _onTrigger = onTrigger;
        InitializeComponent();

        // Position slightly above and to the right of the cursor
        Left = screenPos.X + 10;
        Top = Math.Max(10, screenPos.Y - 36);

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
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(OpacityProperty, fadeIn);
            _autoHideTimer.Start();
        };
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _autoHideTimer.Stop();
        _onTrigger?.Invoke();
        FadeOutAndClose();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _isHovered = true;
        ButtonSurface.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _isHovered = false;
        ButtonSurface.Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush");
        _autoHideTimer.Interval = TimeSpan.FromSeconds(1.5);
        _autoHideTimer.Start();
    }

    public void FadeOutAndClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        _autoHideTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
