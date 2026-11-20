using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Threading;

namespace PopGlot.Windows;

internal enum CaptureOverlayMode
{
    ScreenshotTranslate,
    OcrOnly,
    SelectFixedRegion,
}

/// <summary>Result of a completed screen selection, in physical pixels.</summary>
internal sealed record ScreenCapture(
    Rect PixelBounds,
    byte[] Png,
    bool IsOcrOnly = false,
    bool SaveAsFixedRegion = false);

/// <summary>
/// Full-desktop marquee for picking a region to translate, extract text, or save as a fixed capture region.
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private Point? _dragStart;
    private bool _completed;
    private bool _closing;
    private CaptureOverlayMode _mode = CaptureOverlayMode.ScreenshotTranslate;
    private bool _forceOcrMode;
    private bool _skipPngEncoding;
    private bool _saveAsFixedToggle;
    private Rect? _previousFixedRegionPixels;
    private long _lastBadgeUpdate;
    private int _lastPixelWidth = -1;
    private int _lastPixelHeight = -1;
    private bool? _dragHintOcr;
    private bool? _dragHintSaveFixed;
    private Point _workOrigin;
    private double _workLocalWidth;
    private double _workLocalHeight;
    private int _dpiGeneration;
    private FrozenDesktopSnapshot? _frozenSnapshot;
    private readonly DispatcherTimer _tinySelectionTimer;

    public CaptureOverlayWindow() : this(null)
    {
    }

    internal CaptureOverlayWindow(FrozenDesktopSnapshot? frozenSnapshot)
    {
        InitializeComponent();
        _frozenSnapshot = frozenSnapshot;
        if (frozenSnapshot?.PreviewSource is { } preview)
        {
            FrozenDesktopImage.Source = preview;
            FrozenDesktopImage.Visibility = Visibility.Visible;
        }
        // 选区过小的提醒只强调一小段时间，然后自动恢复常规尺寸徽标。
        _tinySelectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _tinySelectionTimer.Tick += (_, _) => RestoreBadgeAfterTinyWarning();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    internal CaptureOverlayMode Mode => _mode;
    internal bool IsSaveAsFixedRegionToggled => _saveAsFixedToggle;
    internal bool HasPreviousRegionOutline => PreviousRegionOutline.Visibility == Visibility.Visible;

    internal void SetOcrOnlyMode(bool ocrOnly)
    {
        ConfigureMode(ocrOnly ? CaptureOverlayMode.OcrOnly : CaptureOverlayMode.ScreenshotTranslate);
    }

    internal void ConfigureMode(
        CaptureOverlayMode mode,
        Rect? previousFixedRegionPixels = null,
        string? customHint = null,
        bool skipPngEncoding = false)
    {
        _mode = mode;
        _forceOcrMode = mode == CaptureOverlayMode.OcrOnly;
        _skipPngEncoding = mode == CaptureOverlayMode.SelectFixedRegion && skipPngEncoding;
        _previousFixedRegionPixels = mode == CaptureOverlayMode.SelectFixedRegion ? previousFixedRegionPixels : null;
        _saveAsFixedToggle = false;
        _dragHintOcr = null;
        _dragHintSaveFixed = null;

        if (HintTitle is not null)
        {
            HintTitle.Text = !string.IsNullOrWhiteSpace(customHint)
                ? customHint.Trim()
                : mode switch
                {
                    CaptureOverlayMode.OcrOnly => "框选文本区域",
                    CaptureOverlayMode.SelectFixedRegion => "框选固定区域",
                    _ => "框选翻译区域",
                };
        }
        if (ShiftHintGroup is not null)
        {
            ShiftHintGroup.Visibility = mode == CaptureOverlayMode.ScreenshotTranslate
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (SaveFixedHintGroup is not null)
        {
            SaveFixedHintGroup.Visibility = mode == CaptureOverlayMode.ScreenshotTranslate
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        RefreshSaveFixedHintVisual();
        HintDetail.Text = "取消";
        UpdatePreviousRegionOutline();
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

    /// <summary>
    /// WPF answers a WM_DPICHANGED by resizing the HWND to the OS-suggested
    /// rect. An overlay created small on one monitor and then stretched
    /// across a mixed-DPI desktop gets its desktop-wide coverage undone that
    /// way — the dim shades stop matching the physical screens even though
    /// the selection coordinates stay valid. Once the transition settles the
    /// coverage is restated; never a mid-transition reposition (same
    /// discipline as the main window's stable-size restore).
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_closing || _completed)
        {
            return;
        }
        var generation = ++_dpiGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (generation != _dpiGeneration || _closing || _completed)
            {
                return;
            }
            ScreenGeometry.ResizeToPixels(this, ScreenGeometry.VirtualScreenPixels());
            UpdatePreviousRegionOutline();
            if (_dragStart is null)
            {
                // The rescaled local space moved the cached work-area origin
                // that centers the hint chip.
                PositionHintNearCursor();
                UpdateCrosshair(Mouse.GetPosition(this));
            }
        }));
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
        PositionHintNearCursor();
        UpdatePreviousRegionOutline();
        UpdateCrosshair(Mouse.GetPosition(this));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        DismissTinySelectionWarning();
        _dragStart = e.GetPosition(this);
        CaptureMouse();
        ShadeFull.Visibility = Visibility.Collapsed;
        HintChip.Visibility = Visibility.Collapsed;
        CrossHorizontal.Visibility = Visibility.Collapsed;
        CrossVertical.Visibility = Visibility.Collapsed;
        if (CrosshairReticle is not null) CrosshairReticle.Visibility = Visibility.Collapsed;
        if (CrosshairDot is not null) CrosshairDot.Visibility = Visibility.Collapsed;
        SetShadeVisibility(Visibility.Visible);
        SelectionBorder.Visibility = Visibility.Visible;
        SizeBadge.Visibility = Visibility.Visible;
        // 拖选期间的实时状态提示：Shift / Ctrl / F 决定松开后的去向。
        _dragHintOcr = null;
        _dragHintSaveFixed = null;
        DragHintChip.Visibility = Visibility.Visible;
        UpdateDragHint();
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
            UpdateDragHint();
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
        DragHintChip.Visibility = Visibility.Collapsed;

        // Both corners go through PointToScreen so the rectangle lands in real
        // desktop pixels regardless of which monitor (and scale) it spans.
        var pixelRect = Normalize(PointToScreen(start), PointToScreen(end));
        if (pixelRect.Width < 6 || pixelRect.Height < 6)
        {
            // 选区太小不是"取消"：遮罩和选区保持原样，就地提示后允许直接
            // 重新拖选；Esc / 右键仍是显式退出路径。
            ShowTinySelectionWarning();
            return;
        }

        _completed = true;
        var isOcrMode = _mode == CaptureOverlayMode.SelectFixedRegion
            ? false
            : _forceOcrMode || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        var saveAsFixed = IsSaveAsFixedEffective();
        await CaptureAndCloseAsync(pixelRect, isOcrMode, saveAsFixed);
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
            return;
        }
        if (e.Key is Key.F && !e.IsRepeat && _mode == CaptureOverlayMode.ScreenshotTranslate)
        {
            e.Handled = true;
            _saveAsFixedToggle = !_saveAsFixedToggle;
            RefreshSaveFixedHintVisual();
            UpdateDragHint();
            return;
        }
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            RefreshSaveFixedHintVisual();
            UpdateDragHint();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        // Shift / Ctrl 可以在指针完全不动的情况下按下或松开，KeyUp 也要刷新提示。
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl)
        {
            RefreshSaveFixedHintVisual();
            UpdateDragHint();
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
        _tinySelectionTimer.Stop();
        _frozenSnapshot?.Dispose();
        _frozenSnapshot = null;
        FrozenDesktopImage.Source = null;
        base.OnClosing(e);
    }

    private async Task CaptureAndCloseAsync(Rect pixelRect, bool isOcrOnly = false, bool saveAsFixedRegion = false)
    {
        var frozenSnapshot = _frozenSnapshot;
        _frozenSnapshot = null;
        Hide();
        FrozenDesktopImage.Source = null;
        try
        {
            if (_skipPngEncoding)
            {
                frozenSnapshot?.Dispose();
                Close();
                Captured?.Invoke(this, new ScreenCapture(pixelRect, Array.Empty<byte>(), IsOcrOnly: false, SaveAsFixedRegion: true));
                return;
            }

            byte[] png;
            if (frozenSnapshot is not null)
            {
                try
                {
                    png = await ScreenCaptureService.CropPngAsync(frozenSnapshot, pixelRect);
                }
                finally
                {
                    frozenSnapshot.Dispose();
                }
            }
            else
            {
                // Yield through render priority so the hidden overlay is committed
                // before capture. The old fixed 60 ms delay made every screenshot
                // feel sticky even on a fast compositor.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(16);

                png = await ScreenCaptureService.CapturePngAsync(pixelRect);
            }
            Close();
            Captured?.Invoke(this, new ScreenCapture(pixelRect, png, isOcrOnly, saveAsFixedRegion));
        }
        catch (Exception exception)
        {
            frozenSnapshot?.Dispose();
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

        if (CrosshairReticle is not null)
        {
            Canvas.SetLeft(CrosshairReticle, position.X - 13);
            Canvas.SetTop(CrosshairReticle, position.Y - 13);
        }
        if (CrosshairDot is not null)
        {
            Canvas.SetLeft(CrosshairDot, position.X - 2);
            Canvas.SetTop(CrosshairDot, position.Y - 2);
        }
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

    private bool IsSaveAsFixedEffective()
    {
        if (_mode == CaptureOverlayMode.SelectFixedRegion)
        {
            return true;
        }
        if (_mode == CaptureOverlayMode.ScreenshotTranslate)
        {
            return _saveAsFixedToggle ||
                   Keyboard.IsKeyDown(Key.LeftCtrl) ||
                   Keyboard.IsKeyDown(Key.RightCtrl);
        }
        return false;
    }

    private void RefreshSaveFixedHintVisual()
    {
        if (SaveFixedKeycap is null || SaveFixedHintText is null || _mode != CaptureOverlayMode.ScreenshotTranslate)
        {
            return;
        }
        var active = IsSaveAsFixedEffective();
        SaveFixedKeycap.SetResourceReference(
            Border.BackgroundProperty, active ? "AccentSoftBrush" : "SurfaceBrush");
        SaveFixedKeycap.SetResourceReference(
            Border.BorderBrushProperty, active ? "AccentBrush" : "BorderSubtleBrush");
        SaveFixedKeycapText.SetResourceReference(
            TextBlock.ForegroundProperty, active ? "AccentBrush" : "TextPrimaryBrush");
        SaveFixedHintText.SetResourceReference(
            TextBlock.ForegroundProperty, active ? "AccentBrush" : "TextSecondaryBrush");
        SaveFixedHintText.Text = active ? "将存为固定区" : "存为固定区";
    }

    private void UpdatePreviousRegionOutline()
    {
        if (PreviousRegionOutline is null)
        {
            return;
        }
        if (_mode != CaptureOverlayMode.SelectFixedRegion ||
            _previousFixedRegionPixels is not { Width: >= 6, Height: >= 6 } previous)
        {
            PreviousRegionOutline.Visibility = Visibility.Collapsed;
            return;
        }
        if (PresentationSource.FromVisual(this) is null)
        {
            PreviousRegionOutline.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            var topLeft = PointFromScreen(previous.TopLeft);
            var bottomRight = PointFromScreen(previous.BottomRight);
            var local = Normalize(topLeft, bottomRight);
            if (local.Width < 2 || local.Height < 2)
            {
                PreviousRegionOutline.Visibility = Visibility.Collapsed;
                return;
            }
            Place(PreviousRegionOutline, local.X, local.Y, local.Width, local.Height);
            PreviousRegionOutline.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            PreviousRegionOutline.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 拖选进行中的实时去向提示：Shift 按住时松开将提取文字（OCR），Ctrl 或 F
    /// 同时保存为固定区域。只在状态翻转时改文本，避免逐事件重排。
    /// </summary>
    private void UpdateDragHint()
    {
        if (_dragStart is null || DragHintChip.Visibility != Visibility.Visible)
        {
            return;
        }
        if (_mode == CaptureOverlayMode.SelectFixedRegion)
        {
            if (_dragHintOcr == false && _dragHintSaveFixed == true)
            {
                return;
            }
            _dragHintOcr = false;
            _dragHintSaveFixed = true;
            DragHintText.Text = _skipPngEncoding
                ? "松开保存固定区域 · Esc 取消"
                : "松开保存固定区域并翻译 · Esc 取消";
            UpdateHudMode(isOcr: false, saveAsFixed: true);
            Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => CenterChipOnWorkArea(DragHintChip)));
            return;
        }

        var ocr = _forceOcrMode || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        var saveFixed = IsSaveAsFixedEffective();
        if (_dragHintOcr == ocr && _dragHintSaveFixed == saveFixed)
        {
            return;
        }
        _dragHintOcr = ocr;
        _dragHintSaveFixed = saveFixed;
        DragHintText.Text = (ocr, saveFixed) switch
        {
            (true, true) => "松开提取文字并设为固定区 · Esc 取消",
            (false, true) => "松开翻译并设为固定区 · Esc 取消",
            (true, false) => "松开将提取文字（OCR）· Esc 取消",
            _ => "松开开始翻译 · Shift 仅取字 · Ctrl/F 存为固定区",
        };
        UpdateHudMode(ocr, saveFixed);
        // 文本变化会改变气泡宽度：把量宽+居中推迟到布局完成之后的空闲档，
        // 指针移动热路径绝不强制同步布局（框选延迟的旧病根）。
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => CenterChipOnWorkArea(DragHintChip)));
    }

    private void UpdateHudMode(bool isOcr, bool saveAsFixed)
    {
        if (HudModeBadge is not null && HudModeBadge.Visibility == Visibility.Visible && HudModeText is not null)
        {
            HudModeText.Text = _mode == CaptureOverlayMode.SelectFixedRegion
                ? "固定区域"
                : (isOcr, saveAsFixed) switch
                {
                    (true, true) => "OCR + 设为固定区",
                    (false, true) => "翻译 + 设为固定区",
                    (true, false) => "提取文本 (OCR)",
                    _ => "翻译",
                };
        }
    }

    /// <summary>选区过小：就地强调 1.6 秒，然后恢复常规尺寸徽标。</summary>
    private void ShowTinySelectionWarning()
    {
        if (HudDivider is not null) HudDivider.Visibility = Visibility.Collapsed;
        if (HudModeBadge is not null) HudModeBadge.Visibility = Visibility.Collapsed;
        SizeText.Text = "选区太小，请框选文字区域";
        SizeBadge.SetResourceReference(Border.BackgroundProperty, "WarningSoftBrush");
        SizeBadge.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        SizeText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        _tinySelectionTimer.Stop();
        _tinySelectionTimer.Start();
    }

    /// <summary>新一轮拖选开始时立即撤掉提醒并恢复徽标配色。</summary>
    private void DismissTinySelectionWarning()
    {
        if (!_tinySelectionTimer.IsEnabled)
        {
            return;
        }
        RestoreBadgeAfterTinyWarning();
    }

    private void RestoreBadgeAfterTinyWarning()
    {
        _tinySelectionTimer.Stop();
        SizeBadge.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        SizeBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBorderBrush");
        SizeText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        if (HudDivider is not null) HudDivider.Visibility = Visibility.Visible;
        if (HudModeBadge is not null) HudModeBadge.Visibility = Visibility.Visible;
        var ocr = _dragHintOcr ?? (_forceOcrMode || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift));
        var saveFixed = _dragHintSaveFixed ?? IsSaveAsFixedEffective();
        UpdateHudMode(ocr, saveFixed);
        var pixelWidth = _lastPixelWidth;
        var pixelHeight = _lastPixelHeight;
        SizeText.Text = pixelWidth >= 0 ? $"{pixelWidth} × {pixelHeight} px" : string.Empty;
        // 尺寸缓存作废：下一次 UpdateSelection 必须重写文本，防止提醒文案滞留。
        _lastPixelWidth = -1;
        _lastPixelHeight = -1;
    }

    private void PositionHintNearCursor()
    {
        var work = ScreenGeometry.WorkAreaForPixel(ScreenGeometry.CursorPixels());
        var scale = ScreenGeometry.ScaleOf(this);
        // Convert the monitor's work area into this window's coordinate space.
        var origin = PointFromScreen(new Point(work.Left, work.Top));
        _workOrigin = origin;
        _workLocalWidth = work.Width / scale.X;
        _workLocalHeight = work.Height / scale.Y;
        CenterChipOnWorkArea(HintChip, yFraction: 0.12);
    }

    /// <summary>Centres a hint chip inside the cached monitor work area.</summary>
    private void CenterChipOnWorkArea(FrameworkElement chip, double yFraction = 0.12)
    {
        chip.UpdateLayout();
        Canvas.SetLeft(chip, _workOrigin.X + ((_workLocalWidth - chip.ActualWidth) / 2));
        Canvas.SetTop(chip, _workOrigin.Y + (_workLocalHeight * yFraction));
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
}
