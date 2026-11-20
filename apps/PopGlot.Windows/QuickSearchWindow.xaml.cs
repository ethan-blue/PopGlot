using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

public partial class QuickSearchWindow : Window
{
    public const double CompactWindowWidth = 430.0;
    public const double CompactWindowMaxHeight = 380.0;
    public const double ExpandedMinWidth = 480.0;
    public const double ExpandedMaxWidth = 1200.0;
    public const double ExpandedMinHeight = 360.0;
    public const double ExpandedMaxHeight = 1000.0;

    private readonly HistoryStore _history;
    private readonly VocabularyStore _vocabulary;
    private readonly TranslationCoordinator _coordinator;
    private readonly Func<ShellSettings>? _shellSettingsLoader;
    private readonly Action<ShellSettings>? _shellSettingsSaver;
    private readonly EventHandler _themeChangedHandler;
    private readonly Action<string?>? _openSettings;
    private readonly QuickSearchState _state = new();
    private readonly TranslationRoundTripCache _roundTripCache = new();
    private readonly DispatcherTimer _debounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };
    private readonly DispatcherTimer _streamFlushTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60),
    };
    private readonly DispatcherTimer _clampThrottleTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60),
    };
    private readonly DispatcherTimer _expandedSizeSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private readonly DispatcherTimer _breathingDotTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(140),
    };
    // Only injected test stores need polling. Production uses the persistence event,
    // avoiding ten settings-file reads per second while the window is idle.
    private readonly DispatcherTimer _liveSettingsTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };
    private sealed record LiveSettingsKey(bool Enabled, int DelayMs, LiveTranslateScope Scope,
        FreeEngineConsent Consent, FreeEngineProvider Provider, bool IsFreeRoute, bool OutboundAllowed);
    private LiveSettingsKey? _liveSettingsKey;
    private bool _liveQueryPending;
    private long _liveQueryDueMs;
    private CancellationTokenSource? _cts;
    private bool _isClosed;
    private string? _sessionSourceLanguage;
    private string? _sessionTargetLanguage;
    private bool _richRenderFailed;
    private System.Windows.Controls.ContextMenu? _routeMenu;
    private bool _switchingRoute;
    internal Action? RouteChanged { get; set; }
    private QuickSearchWindowMode _windowMode = QuickSearchWindowMode.Compact;
    private bool _applyingWindowMode;
    private bool _expandedSizeDirty;
    private bool _isCompactExplanationExpanded;
    private bool _userMoved;
    private bool _currentResultAutoTriggered;
    private string? _lastLiveQuery;
    private long _lastStreamRenderTicks;
    private long _lastClampTicks;
    private int _breathingStep;
    private DateTime _lastWarmupUtc = DateTime.MinValue;
    internal Func<bool>? IsFreeRouteResolverForTest { get; set; }
    internal ModifierKeys? ModifiersOverrideForTest { get; set; }
    internal QuickSearchWindowMode CurrentWindowMode => _windowMode;
    internal bool IsUserMoved => _userMoved;
    internal bool IsCurrentResultAutoTriggered => _currentResultAutoTriggered;
    // DPI transition fencing: OnDpiChanged only schedules ONE ApplicationIdle
    // reposition; until it ran, every generation bump has a pending settle and
    // the transition-driven SizeChanged/Height events must not re-position or
    // clamp with the window's stale scale.
    private long _dpiGeneration;
    private long _settledDpiGeneration;
    private bool IsInDpiTransition => _dpiGeneration != _settledDpiGeneration;
    // E3: true when the Escape currently travelling down the tunnel belonged
    // to a live IME composition on the search box. Window_KeyDown is a
    // BUBBLING KeyDown handler — by the time it runs, the Ui composition
    // tracker has already reset the IsComposing flag in the preview pass —
    // so this is the only reliable place to sample the fact and let the
    // window handler spare the composition.
    private bool _escapeOwnedByComposition;
    // 免费引擎预提示：在真实进展（流式文本、终态）出现前，不允许被
    // 「正在生成…」等准备态瞬间覆盖。快速搜索只有文字线路，不涉及
    // 截图视觉直译提示。
    private string? _pendingStyleNotice;
    // The XAML binds the speak icon's Fill to the button Foreground.
    // Speaking replaces that expression with a dynamic AccentBrush
    // reference, so the original binding is captured here for the stop path:
    // ClearValue would erase the expression for good and leave the idle
    // icon with no fill (the expression itself is the local value).
    private readonly System.Windows.Data.Binding? _speakIconFillBinding;

    /// <summary>
    /// C05: set by the host when the window must really die (app exit).
    /// Without it, close requests cancel the running request and hide —
    /// the session and any partial result survive.
    /// </summary>
    private Point? _initialCenterPointPixels;
    internal bool ForceClose { get; set; }

    private ShellSettings LoadShellSettings()
    {
        try
        {
            return _shellSettingsLoader is not null
                ? _shellSettingsLoader()
                : ShellSettingsStore.Load();
        }
        catch
        {
            return ShellSettings.Default;
        }
    }

    private void UpdateShellSettings(Func<ShellSettings, ShellSettings> update)
    {
        try
        {
            if (_shellSettingsSaver is not null)
            {
                var updated = update(LoadShellSettings());
                _shellSettingsSaver(updated);
            }
            else
            {
                ShellSettingsStore.Update(update);
            }
        }
        catch
        {
            // Ignore persistence failure on transient UI toggles
        }
    }

    public new void Show()
    {
        SyncWindowModeFromSettings();
        base.Show();
        CenterOnCurrentMonitor();
        FocusSearchBox();
    }

    public new bool Activate()
    {
        CenterOnCurrentMonitor();
        var result = base.Activate();
        FocusSearchBox();
        return result;
    }

    public new void Hide()
    {
        CancelRunningRequest();
        FlushExpandedSizeSaveIfDirty();
        if (_routeMenu is not null) _routeMenu.IsOpen = false;
        // 用户主动收起（Esc 阶梯/失焦）结束本次会话视图的生命周期：一次性
        // 通知（如划词预读取超时落地提示）与仅作解释的风格提示都不得跨
        // Hide 残留到下一次普通打开——准备期关闭后重显尤其不能带回过期的
        // 风格说明。首次 Show→Loaded 不受影响——Loaded 早于任何 Hide 且生
        // 产超时路径是先 Show 再 Post。会话本身（C05）原样保留。
        _state.PostPendingNotice(null);
        _pendingStyleNotice = null;
        StoreSessionSnapshot();
        SyncUiWithState();
        base.Hide();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        FocusSearchBox();
        RefreshRouteSelection();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_isClosed)
        {
            return;
        }
        // Single driver: during the WM_DPICHANGED transition the window's own
        // scale is stale, so re-centering here would fight WPF's suggested-rect
        // resize (the E3 F10 anomaly: a 480 DIP quick search settling 720 px
        // wide on a 100% screen). One idle reposition, once settled, using the
        // TARGET monitor's scale.
        _dpiGeneration++;
        var generation = _dpiGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_isClosed || generation != _dpiGeneration)
            {
                return; // a newer DPI change owns the reposition
            }
            CenterOnCurrentMonitor();
            _settledDpiGeneration = _dpiGeneration;
        }));
    }

    internal QuickSearchWindow(
        HistoryStore history,
        VocabularyStore vocabulary,
        Action<string?>? openSettings = null,
        TranslationCoordinator? coordinator = null,
        Func<ShellSettings>? shellSettingsLoader = null,
        Action<ShellSettings>? shellSettingsSaver = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _openSettings = openSettings;
        _coordinator = coordinator ?? new TranslationCoordinator(_history, _vocabulary);
        _shellSettingsLoader = shellSettingsLoader;
        _shellSettingsSaver = shellSettingsSaver;
        InitializeComponent();
        InterfaceKeys.RefreshHints(this);
        PreviewKeyDown += Window_PreviewShortcutKeyDown;
        _debounceTimer.Tick += DebounceTimer_Tick;
        _liveSettingsTimer.Tick += LiveSettingsTimer_Tick;
        _streamFlushTimer.Tick += StreamFlushTimer_Tick;
        _clampThrottleTimer.Tick += ClampThrottleTimer_Tick;
        _expandedSizeSaveTimer.Tick += ExpandedSizeSaveTimer_Tick;
        _breathingDotTimer.Tick += BreathingDotTimer_Tick;
        CoreBridge.LanguagePairChanged += OnLanguagePairChanged;
        // E3: the sampler must register BEFORE the Ui composition tracker —
        // it reads IsComposing, which the tracker resets on Escape during
        // the same tunnelling pass.
        SearchBox.PreviewKeyDown += OnSearchBoxPreviewKeyDownSample;
        Ui.AttachCompositionTracker(SearchBox);
        _speakIconFillBinding = SpeakIcon.GetBindingExpression(System.Windows.Shapes.Shape.FillProperty)?.ParentBinding;
        RefreshPinTooltip();
        SyncWindowModeFromSettings(isInitial: true);

        GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus == this || e.NewFocus is null)
            {
                FocusSearchBox();
            }
        };

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

        TtsService.SpeakingStateChanged += OnTtsSpeakingStateChanged;

        SourceInitialized += (_, _) => CenterOnCurrentMonitor();
        Loaded += (_, _) =>
        {
            CenterOnCurrentMonitor();
            FocusSearchBox();
            SyncUiWithState();
            UpdateEmptyStateVisuals();
            UpdateStatusDot();
        };
        // The window hides instead of closing; on every re-show the engine
        // (and therefore style support) may have changed.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _liveSettingsKey = ReadLiveSettingsKey();
                ShellSettingsStore.Changed -= OnShellSettingsChanged;
                ShellSettingsStore.Changed += OnShellSettingsChanged;
                if (_shellSettingsLoader is not null) _liveSettingsTimer.Start();
                SyncWindowModeFromSettings();
                CenterOnCurrentMonitor();
                FocusSearchBox();
                // The persisted language pair may have changed on another
                // surface while this window was hidden — the badge must not
                // go stale on a re-show.
                _sessionSourceLanguage = null;
                _sessionTargetLanguage = null;
                RefreshLangBadge();
                RefreshRouteSelection();
                TriggerWarmupIfEligible();
            }
            else
            {
                _liveSettingsTimer.Stop();
                ShellSettingsStore.Changed -= OnShellSettingsChanged;
                CancelRunningRequest();
                FlushExpandedSizeSaveIfDirty();
                StoreSessionSnapshot();
            }
        };
        // During a DPI transition the idle reposition owns the placement;
        // transition-driven resizes must not re-clamp with the stale scale.
        SizeChanged += (_, _) =>
        {
            if (IsInDpiTransition) return;
            if (_windowMode == QuickSearchWindowMode.Expanded && IsLoaded && !_applyingWindowMode)
            {
                ScheduleExpandedSizeSave();
            }
            if (_windowMode == QuickSearchWindowMode.Compact &&
                _state.Stage is QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing)
            {
                ScheduleThrottledClamp();
                return;
            }
            ClampToWorkArea();
        };

        Closed += (_, _) => OnClosedCleanup();
    }

    internal void FocusSearchBox()
    {
        if (_isClosed || SearchBox is null) return;
        FocusManager.SetFocusedElement(this, SearchBox);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible && !_isClosed && SearchBox is not null)
            {
                FocusManager.SetFocusedElement(this, SearchBox);
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        });
    }

    /// <summary>
    /// Posts a ONE-SHOT footer notice (e.g. the hotkey pre-read timeout
    /// landing notice). State-driven, never a raw footer write: the notice
    /// lives in <see cref="QuickSearchState.PendingNotice"/>, every
    /// SyncUiWithState re-applies it — so the queued Loaded sync of a
    /// first-show cannot swallow it — and the next real query/status update
    /// clears it naturally.
    /// </summary>
    internal void PostPendingNotice(string notice)
    {
        if (_isClosed || string.IsNullOrEmpty(notice)) return;
        _state.PostPendingNotice(notice);
        SyncUiWithState();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (IsInDpiTransition)
        {
            // A DPI transition owns the size: its own Height churn must not
            // trigger work-area clamps computed from the stale scale.
            return;
        }
        if (e.Property == HeightProperty && IsLoaded && e.NewValue is double newHeight && double.IsFinite(newHeight))
        {
            if (Math.Abs(newHeight - ActualHeight) > 0.5)
            {
                if (SizeToContent == SizeToContent.Height)
                {
                    SizeToContent = SizeToContent.Manual;
                    UpdateExpandedEmptyGuideVisibility();
                }
                UpdateLayout();
                ClampToWorkArea();
            }
        }
    }

    internal void CenterOnCurrentMonitor()
    {
        if (_isClosed) return;

        UpdateLayout();

        var cursor = ScreenGeometry.CursorPixels();
        var workArea = ScreenGeometry.WorkAreaForPixel(cursor);
        // Target-monitor scale from the target point — never the window's own
        // (mid-transition stale) DPI.
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(cursor);
        var scaleX = scale.X > 0 ? scale.X : 1.0;
        var scaleY = scale.Y > 0 ? scale.Y : 1.0;

        var workAreaHeightDip = workArea.Height / scaleY;
        var workAreaWidthDip = workArea.Width / scaleX;
        var modeMaxCap = _windowMode == QuickSearchWindowMode.Compact
            ? CompactWindowMaxHeight
            : ExpandedMaxHeight;
        var maxAllowedHeight = Math.Min(modeMaxCap, Math.Max(120, workAreaHeightDip - 40));
        MaxHeight = maxAllowedHeight;

        var defaultWidth = _windowMode == QuickSearchWindowMode.Compact
            ? CompactWindowWidth
            : ShellSettings.DefaultQuickSearchExpandedWidth;
        var widthDip = ActualWidth > 0 ? ActualWidth : (double.IsFinite(Width) ? Width : defaultWidth);
        var heightDip = ActualHeight > 0 ? ActualHeight : (double.IsFinite(Height) ? Height : 100);
        if (heightDip > maxAllowedHeight)
        {
            heightDip = maxAllowedHeight;
            if (SizeToContent == SizeToContent.Height)
            {
                SizeToContent = SizeToContent.Manual;
            }
            Height = maxAllowedHeight;
        }

        if (_userMoved && IsLoaded)
        {
            ClampToWorkArea();
            return;
        }

        var xDip = (workArea.Left / scaleX) + Math.Max(0, (workAreaWidthDip - widthDip) / 2);
        var yDip = (workArea.Top / scaleY) + Math.Max(0, (workAreaHeightDip - heightDip) / 2);

        _initialCenterPointPixels = new Point(xDip * scaleX, yDip * scaleY);
        // ONE authoritative landing: move the HWND directly. Writing Left/Top
        // first and then MoveToPixels double-drove the same placement through
        // two unit systems (WPF DIP and physical pixels).
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            // Pre-show: no HWND yet, the WPF properties carry the position.
            Left = xDip;
            Top = yDip;
            return;
        }
        ScreenGeometry.MoveToPixels(this, new Point(xDip * scaleX, yDip * scaleY));
    }

    private void ScheduleThrottledClamp()
    {
        if (_isClosed || !IsLoaded) return;
        var now = Stopwatch.GetTimestamp();
        if (_lastClampTicks == 0 || Stopwatch.GetElapsedTime(_lastClampTicks, now).TotalMilliseconds >= 60)
        {
            _lastClampTicks = now;
            _clampThrottleTimer.Stop();
            ClampToWorkArea();
            return;
        }
        if (!_clampThrottleTimer.IsEnabled)
        {
            _clampThrottleTimer.Start();
        }
    }

    private void ClampThrottleTimer_Tick(object? sender, EventArgs e)
    {
        _clampThrottleTimer.Stop();
        if (_isClosed || !IsLoaded || IsInDpiTransition) return;
        _lastClampTicks = Stopwatch.GetTimestamp();
        ClampToWorkArea();
    }

    private void ClampToWorkArea()
    {
        if (_isClosed || !IsLoaded) return;

        var scale = ScreenGeometry.ScaleOf(this);
        var scaleX = scale.X > 0 ? scale.X : 1.0;
        var scaleY = scale.Y > 0 ? scale.Y : 1.0;

        var windowTopLeftPixels = new Point(
            (double.IsFinite(Left) ? Left : 0) * scaleX,
            (double.IsFinite(Top) ? Top : 0) * scaleY);
        var workAreaPixels = ScreenGeometry.WorkAreaForPixel(
            double.IsFinite(Left) && double.IsFinite(Top) ? windowTopLeftPixels : ScreenGeometry.CursorPixels());

        // The monitor that actually owns the work area decides the scale —
        // never the transitioning window's own reading.
        var monitorScale = ScreenGeometry.ScaleOfMonitorAtPixel(
            new Point(workAreaPixels.Left + workAreaPixels.Width / 2, workAreaPixels.Top + workAreaPixels.Height / 2));
        var monitorScaleX = monitorScale.X > 0 ? monitorScale.X : scaleX;
        var monitorScaleY = monitorScale.Y > 0 ? monitorScale.Y : scaleY;

        var workAreaDip = new Rect(
            ScreenGeometry.PixelToDip(workAreaPixels.Left, monitorScaleX),
            ScreenGeometry.PixelToDip(workAreaPixels.Top, monitorScaleY),
            ScreenGeometry.PixelToDip(workAreaPixels.Width, monitorScaleX),
            ScreenGeometry.PixelToDip(workAreaPixels.Height, monitorScaleY));

        // 1. 先限制有效高度：不同工作区尺寸/DPI下，任何过高窗口先限制有效高度
        const double marginDip = 20.0;
        var maxEffectiveHeight = Math.Max(120.0, workAreaDip.Height - (marginDip * 2));
        if (MaxHeight > maxEffectiveHeight)
        {
            MaxHeight = maxEffectiveHeight;
        }

        var currentHeight = ActualHeight > 0 ? ActualHeight : (double.IsFinite(Height) ? Height : DesiredSize.Height);
        if (currentHeight <= 0) return;

        if (currentHeight > maxEffectiveHeight)
        {
            currentHeight = maxEffectiveHeight;
            if (SizeToContent == SizeToContent.Height)
            {
                SizeToContent = SizeToContent.Manual;
            }
            Height = maxEffectiveHeight;
        }

        var currentWidth = ActualWidth > 0 ? ActualWidth : (double.IsFinite(Width) ? Width : 640.0);

        // 2. 再正确夹逼 Top/Left，保证底部不越界
        var currentLeft = double.IsFinite(Left)
            ? Left
            : workAreaDip.Left + Math.Max(0, (workAreaDip.Width - currentWidth) / 2);
        var currentTop = double.IsFinite(Top)
            ? Top
            : workAreaDip.Top + Math.Max(0, (workAreaDip.Height - currentHeight) / 2);

        var minTop = workAreaDip.Top + marginDip;
        var maxTop = Math.Max(minTop, workAreaDip.Bottom - currentHeight - marginDip);
        var clampedTop = Math.Clamp(currentTop, minTop, maxTop);

        var minLeft = workAreaDip.Left + marginDip;
        var maxLeft = Math.Max(minLeft, workAreaDip.Right - currentWidth - marginDip);
        var clampedLeft = Math.Clamp(currentLeft, minLeft, maxLeft);

        // ONE authoritative landing (MoveToPixels); WPF syncs Left/Top from
        // the HWND move — never write both unit systems for the same spot.
        ScreenGeometry.MoveToPixels(this, new Point(clampedLeft * monitorScaleX, clampedTop * monitorScaleY));
    }

    private sealed record SnapshotDeduplicationKey(
        string SessionId,
        string Query,
        string? ResultText,
        TranslationSessionState State,
        bool IsPartial);

    private SnapshotDeduplicationKey? _lastStoredKey;
    private long _lastSessionEpoch = -1;
    private string _currentSessionId = Guid.NewGuid().ToString("N");

    private string GetSessionId()
    {
        if (_lastSessionEpoch != _state.CurrentEpoch)
        {
            _lastSessionEpoch = _state.CurrentEpoch;
            _currentSessionId = Guid.NewGuid().ToString("N");
        }
        return _currentSessionId;
    }

    private bool HasCurrentResultIdentity => _state.IsResultForCurrentQuery &&
        string.Equals(SearchBox.Text.Trim(), _state.ResultQuery, StringComparison.Ordinal);

    internal QuickSearchState State => _state;
    internal TextBox StreamBox => ResultStreamBox;
    internal RichTextBox RichBox => ResultRichBox;
    internal TextBlock FooterStatusBlock => FooterStatus;
    internal TextBlock StreamIndicatorBlock => StreamIndicator;
    internal Button EnterKeycapButton => SearchEnterKeycap;
    internal TextBox SearchInputBox => SearchBox;
    internal TextBlock IncompleteBadgeBlock => IncompleteBadge;
    internal System.Windows.Shapes.Ellipse StatusDotShape => StatusDot;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            ToggleWindowMode();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var beforeLeft = Left;
            var beforeTop = Top;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Headless or mouse released before DragMove entered modal loop
            }
            if ((double.IsFinite(Left) && double.IsFinite(beforeLeft) && Math.Abs(Left - beforeLeft) > 1.0) ||
                (double.IsFinite(Top) && double.IsFinite(beforeTop) && Math.Abs(Top - beforeTop) > 1.0))
            {
                _userMoved = true;
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isClosed) return;

        UpdateEmptyStateVisuals();
        SearchClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var text = SearchBox.Text;
        _pendingStyleNotice = null;
        _debounceTimer.Stop();

        // If input is cleared, update state immediately without debounce lag
        if (string.IsNullOrWhiteSpace(text))
        {
            _lastLiveQuery = null;
            CancelRunningRequest();
            if (text.Trim() != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        // Invalidate actions synchronously, before any debounce or queued key
        // event can pair this input with the previous result. Keep it visual only.
        CancelRunningRequest();
        _state.OnQueryTextChanged(text, preservePreviousResult: true);
        SyncUiWithState();
        ScheduleLiveQuery(ReadLiveSettingsKey());
    }

    private LiveSettingsKey ReadLiveSettingsKey()
    {
        var shell = LoadShellSettings();
        var core = TryGetCoreSettingsSafely();
        return new LiveSettingsKey(shell.LiveTranslateEnabled,
            ShellSettings.NormalizeLiveTranslateDelayMs(shell.LiveTranslateDelayMs),
            shell.LiveTranslateScope, shell.FreeEngineConsent, shell.FreeEngineProvider,
            ResolveIsActiveRouteFree(core) && shell.FreeEngineProvider != FreeEngineProvider.Custom,
            IsFreeRouteOutboundAllowed(shell, core));
    }

    private static bool AllowsLiveQuery(LiveSettingsKey key) => key.Enabled &&
        RuntimeGate.NewWorkAllowed &&
        (key.Scope != LiveTranslateScope.FreeEngineOnly || key.IsFreeRoute) &&
        (!key.IsFreeRoute || key.OutboundAllowed);

    private void ScheduleLiveQuery(LiveSettingsKey key)
    {
        _liveSettingsKey = key;
        if (_isClosed || !IsVisible || !AllowsLiveQuery(key) ||
            !QuickSearchLivePolicy.MeetsMinimumLength(SearchBox.Text.Trim())) return;
        _liveQueryPending = true;
        _liveQueryDueMs = Environment.TickCount64 + key.DelayMs;
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(key.DelayMs);
        _debounceTimer.Start();
    }

    private void OnShellSettingsChanged()
    {
        if (_isClosed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => LiveSettingsTimer_Tick(this, EventArgs.Empty)));
    }

    private void LiveSettingsTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosed || !IsVisible) return;
        var key = ReadLiveSettingsKey();
        if (key == _liveSettingsKey) return;
        _liveSettingsKey = key;
        StopPendingLiveQuery();
        if (_currentResultAutoTriggered && _cts is { IsCancellationRequested: false })
        {
            CancelRunningRequest();
        }
        // Settings govern automatic work, not a manual request already in flight.
        if (AllowsLiveQuery(key) && (_cts is null || _currentResultAutoTriggered))
        {
            _lastLiveQuery = null;
            ScheduleLiveQuery(key);
        }
    }

    private void StopPendingLiveQuery()
    {
        _liveQueryPending = false;
        _debounceTimer.Stop();
    }

    private void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosed || !IsVisible || !_liveQueryPending)
        {
            StopPendingLiveQuery();
            return;
        }
        LiveSettingsTimer_Tick(sender, e);
        if (!_liveQueryPending) return;
        // A previously queued Tick cannot submit a newer edit before its delay.
        if (Environment.TickCount64 - _liveQueryDueMs < 0) return;
        _debounceTimer.Stop();
        if (Ui.GetIsComposing(SearchBox))
        {
            // Reschedule while IME is actively composing; when composition finishes,
            // either the next TextChanged or this rescheduled tick picks up the final text.
            _debounceTimer.Start();
            return;
        }

        _liveQueryPending = false;
        var text = SearchBox.Text;
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (trimmed != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        var shell = LoadShellSettings();
        if (!shell.LiveTranslateEnabled)
        {
            _lastLiveQuery = null;
            if (trimmed != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        // Do not re-trigger if identical to the currently active/completed query
        if (string.Equals(trimmed, _lastLiveQuery, StringComparison.Ordinal) &&
            string.Equals(trimmed, _state.CurrentQuery, StringComparison.Ordinal) &&
            _state.Stage is QuickSearchUiStage.Completed or QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing)
        {
            return;
        }

        if (!QuickSearchLivePolicy.MeetsMinimumLength(trimmed))
        {
            _lastLiveQuery = null;
            if (trimmed != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        var coreSettings = TryGetCoreSettingsSafely();
        var isFreeRoute = ResolveIsActiveRouteFree(coreSettings);
        if (shell.LiveTranslateScope == LiveTranslateScope.FreeEngineOnly &&
            (!isFreeRoute || shell.FreeEngineProvider == FreeEngineProvider.Custom))
        {
            _lastLiveQuery = null;
            if (trimmed != _state.CurrentQuery)
            {
                _state.OnQueryTextChanged(text);
                SyncUiWithState();
            }
            return;
        }

        if (isFreeRoute)
        {
            if (!IsFreeRouteOutboundAllowed(shell, coreSettings))
            {
                _lastLiveQuery = null;
                if (trimmed != _state.CurrentQuery)
                {
                    _state.OnQueryTextChanged(text);
                    SyncUiWithState();
                }
                return;
            }

            var sourceLang = coreSettings?.SourceLanguage ?? LanguageCatalog.Auto;
            var targetLang = coreSettings?.TargetLanguage ?? "zh-CN";
            if (LanguageCatalog.Normalize(sourceLang) == LanguageCatalog.Auto)
            {
                targetLang = LanguageCatalog.ResolveAutoTarget(trimmed, targetLang);
            }

            var guard = QuickSearchLivePolicy.EvaluateFreeRouteGuards(
                trimmed,
                shell.FreeEngineProvider,
                FreeTranslateService.QuotaRemaining,
                sourceLang,
                targetLang);
            if (!guard.Allowed)
            {
                _lastLiveQuery = null;
                _state.OnQueryTextChanged(text);
                _state.PostPendingNotice(guard.Hint);
                SyncUiWithState();
                return;
            }
        }

        _lastLiveQuery = trimmed;
        _ = ExecuteTranslateAsync(autoTriggered: true);
    }

    private static ProviderSettings? TryGetCoreSettingsSafely()
    {
        try
        {
            return CoreBridge.GetSettings();
        }
        catch
        {
            return null;
        }
    }

    private bool ResolveIsActiveRouteFree(ProviderSettings? coreSettings)
    {
        if (IsFreeRouteResolverForTest is not null)
        {
            return IsFreeRouteResolverForTest();
        }
        try
        {
            if (coreSettings is null) return false;
            var (textRoute, _) = ProfileManager.ResolveRoutes();
            var textApiKey = textRoute is null || textRoute.Profile.ExecutionFacts.IsManagedExecution
                ? null
                : (CredentialStore.HasApiKey(textRoute.CredentialTarget) ? TextRouteMenu.KeyPresent : null);
            var textRuntimeSettings = textRoute?.Profile.ToProviderSettings(coreSettings);
            var capability = TranslationCoordinator.ResolveTextRouteCapability(
                textRuntimeSettings, textApiKey, coreSettings, textRoute?.Profile);
            return !capability.HasConfiguredProvider && !capability.ConfiguredButUnsupported;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFreeRouteOutboundAllowed(ShellSettings shell, ProviderSettings? coreSettings)
    {
        if (!RuntimeGate.NewWorkAllowed || shell.FreeEngineConsent == FreeEngineConsent.Denied)
        {
            return false;
        }
        if (coreSettings is null || coreSettings.SafeDevMode || !coreSettings.NetworkEnabled)
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// E3: samples whether this Escape belongs to a live IME composition on
    /// the search box, and clears the residual composition flag safely (some
    /// IMEs tear the composition down without a composition-end event). The
    /// key itself is never marked handled: the IME still needs it to cancel.
    /// The isolation suite drives the WPF-level events only; real Microsoft
    /// Pinyin / Sogou behaviour stays an explicit E3 manual verification TODO.
    /// </summary>
    private void OnSearchBoxPreviewKeyDownSample(object sender, KeyEventArgs e)
    {
        _escapeOwnedByComposition =
            (e.Key == Key.Escape || e.ImeProcessedKey == Key.Escape) &&
            (Ui.GetIsComposing(SearchBox) || Ui.GetEscapeCancelledComposition(SearchBox));
        if (_escapeOwnedByComposition)
        {
            Ui.SetIsComposing(SearchBox, false);
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => _escapeOwnedByComposition = false));
        }
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = InterfaceKeys.Normalize(e);
        if (key != Key.Enter)
        {
            return;
        }
        var modifiers = ModifiersOverrideForTest ?? Keyboard.Modifiers;
        if (modifiers is ModifierKeys.None or ModifierKeys.Control)
        {
            // IME composition confirm: the composition owns this Enter.
            if (Ui.IsImeComposing(SearchBox, e))
            {
                return;
            }
            e.Handled = true;
            _debounceTimer.Stop();
            _state.OnQueryTextChanged(SearchBox.Text);
            await PerformTranslateAsync();
        }
    }

    private Task PerformTranslateAsync() => ExecuteTranslateAsync(autoTriggered: false);

    private void StreamFlushTimer_Tick(object? sender, EventArgs e)
    {
        _streamFlushTimer.Stop();
        if (_isClosed) return;
        _lastStreamRenderTicks = Stopwatch.GetTimestamp();
        SyncUiWithState();
    }

    private async Task ExecuteTranslateAsync(bool autoTriggered)
    {
        if (_isClosed || (autoTriggered && !IsVisible)) return;
        StopPendingLiveQuery();

        var text = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _currentResultAutoTriggered = autoTriggered;
        _isCompactExplanationExpanded = false;
        _lastStreamRenderTicks = 0;
        _streamFlushTimer.Stop();
        if (!autoTriggered)
        {
            _lastLiveQuery = text;
        }

        // P02: one trace per query interaction. The origin is the dispatcher
        // entry that begins the query (the user pressed Enter), and the
        // session's terminal close publishes it with the full timeline.
        var queryTrace = new Services.InteractionTrace("quicksearch-query");
        queryTrace.Mark(Services.TracePoint.DispatcherEntered);

        _state.StartNewSearch(text, preservePreviousResultForLive: autoTriggered);
        var epoch = _state.CurrentEpoch;
        SyncUiWithState();

        // Honest pre-flight notice when the route cannot be proven to honour
        // the active style: stated BEFORE the request goes out, never blocking
        // it and never cancelling anything already in flight. The pending
        // notice rides on top of the preparing statuses in SyncUiWithState
        // until real progress lands.
        _pendingStyleNotice = null;
        if (TranslationStyleMenu.PreTranslateNotice() is { } styleNotice)
        {
            _pendingStyleNotice = styleNotice;
            FooterStatus.Text = styleNotice;
            RefreshStyleSelector();
        }

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (_isClosed || token.IsCancellationRequested || !ReferenceEquals(_cts, cts)) return;
            if (!_state.OnStreamUpdate(update, SearchBox.Text.Trim()))
            {
                return;
            }
            if (update.Kind == TranslationStreamUpdateKind.Reset)
            {
                _lastStreamRenderTicks = 0;
                _streamFlushTimer.Stop();
                SyncUiWithState();
                return;
            }

            // PERF-LIST-01: 60ms throttle on intermediate stream deltas to avoid layout thrashing.
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(_lastStreamRenderTicks, now).TotalMilliseconds;
            if (_lastStreamRenderTicks != 0 && elapsedMs < 60)
            {
                if (!_streamFlushTimer.IsEnabled)
                {
                    _streamFlushTimer.Start();
                }
                return;
            }
            _lastStreamRenderTicks = now;
            _streamFlushTimer.Stop();
            if (update.Kind == TranslationStreamUpdateKind.Delta &&
                !string.IsNullOrEmpty(update.Delta) &&
                queryTrace is not null &&
                !queryTrace.IsMarked(Services.TracePoint.FirstVisibleDeltaApplied))
            {
                queryTrace.Mark(Services.TracePoint.FirstVisibleDeltaApplied);
                Services.InteractionTraceWindow.MarkAtNextRenderCallback(
                    queryTrace, Services.TracePoint.FirstDeltaRenderCallback);
            }
            SyncUiWithState();
        });

        try
        {
            var settings = CoreBridge.GetSettings();
            var sourceLang = settings.SourceLanguage ?? LanguageCatalog.Auto;
            var targetLang = settings.TargetLanguage ?? "zh-CN";
            if (LanguageCatalog.Normalize(sourceLang) == LanguageCatalog.Auto)
            {
                targetLang = LanguageCatalog.ResolveAutoTarget(text, targetLang);
            }
            _sessionSourceLanguage = sourceLang;
            _sessionTargetLanguage = targetLang;
            RefreshLangBadge();

            var configVersion = ProfileManager.Revision;
            var styleIdentity = TranslationStyleMenu.ActiveIdentity();
            TranslationRoundTripResult? cached = null;
            if (styleIdentity is not null && _roundTripCache.TryGet(
                    text, sourceLang, targetLang, configVersion, styleIdentity, out var cachedResult))
            {
                cached = cachedResult;
            }
            var isCached = cached is not null;
            TranslationSession session;
            if (cached is not null)
            {
                _pendingStyleNotice = null;
                session = cached.CreateSession(
                    text, sourceLang, targetLang, TranslationInputSource.QuickSearch);
            }
            else
            {
                session = await _coordinator.TranslateTextAsync(
                    text,
                    sourceLang,
                    targetLang,
                    TranslationInputSource.QuickSearch,
                    token,
                    onStageChanged: stage =>
                    {
                        if (_isClosed) return;
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (_isClosed || token.IsCancellationRequested || !ReferenceEquals(_cts, cts)) return;
                            if (_state.OnStageChanged(stage, epoch, SearchBox.Text.Trim()))
                            {
                                SyncUiWithState();
                            }
                        });
                    },
                    progress: progress,
                    epoch: epoch,
                    trace: queryTrace,
                    autoTriggered: autoTriggered);
            }

            _streamFlushTimer.Stop();
            if (_isClosed) return;
            if (token.IsCancellationRequested)
            {
                if (_state.OnCancelled(epoch, SearchBox.Text.Trim())) SyncUiWithState();
                return;
            }

            if (isCached
                    ? _state.OnCachedSessionCompleted(session, epoch, SearchBox.Text.Trim())
                    : _state.OnSessionCompleted(session, epoch, SearchBox.Text.Trim()))
            {
                if (!autoTriggered && !isCached && session.IsCleanCompletion && styleIdentity is not null &&
                    configVersion == ProfileManager.Revision &&
                    string.Equals(styleIdentity, TranslationStyleMenu.ActiveIdentity(), StringComparison.Ordinal))
                {
                    _roundTripCache.Set(
                        text, sourceLang, targetLang, session.TranslatedText,
                        TranslationRoundTripCache.ExplanationFor(session), configVersion, styleIdentity,
                        session.PipelineLabel, session.PromptTemplateName);
                }
                _richRenderFailed = false;
                if (_state.IsRichBoxVisible && !string.IsNullOrWhiteSpace(_state.FinalRenderedText))
                {
                    try
                    {
                        MarkdownPresenter.RenderToFlowDocument(
                            ResultRichBox.Document,
                            _state.FinalRenderedText,
                            Application.Current?.Resources ?? Resources,
                            resultActionsEnabled: _state.CanCopy);
                    }
                    catch
                    {
                        _richRenderFailed = true;
                    }
                }
                SyncUiWithState();
                if (_windowMode == QuickSearchWindowMode.Compact && IsLoaded && !IsInDpiTransition)
                {
                    ClampToWorkArea();
                }

                // Durable post-hoc honesty: when the free engine actually ran,
                // the selected style did not apply. Decided by the TYPED
                // prompt-support fact, rendered through the shared short
                // status; never by matching the PipelineLabel display string.
                if (session.PromptSupport == TranslationPromptSupport.NotSupported &&
                    TranslationStyleMenu.TryGetActiveTemplate() is { } activeStyle &&
                    activeStyle.Id != TranslationStyleMenu.FaithfulTemplateId)
                {
                    FooterStatus.Text = TranslationStyleMenu.StyleStatusFor(
                        TranslationPromptSupport.NotSupported);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _streamFlushTimer.Stop();
            if (_isClosed) return;
            if (_state.OnCancelled(epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
        catch (Exception ex)
        {
            _streamFlushTimer.Stop();
            if (_isClosed) return;
            if (_state.OnException(ex, epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private void SyncUiWithState()
    {
        if (_isClosed) return;

        if (_windowMode == QuickSearchWindowMode.Compact &&
            !_state.IsStreamLayerVisible &&
            SizeToContent != SizeToContent.Height)
        {
            SizeToContent = SizeToContent.Height;
            ClearValue(HeightProperty);
        }

        EmptyStateContainer.Visibility = _state.IsResultVisible ? Visibility.Collapsed : Visibility.Visible;
        ResultContainer.Visibility = _state.IsResultVisible ? Visibility.Visible : Visibility.Collapsed;
        ResultContainer.Opacity = _state.IsDimmingPreviousResult ? 0.55 : 1.0;

        if (!_state.IsDimmingPreviousResult)
        {
            ResultStreamBox.Visibility = _state.IsStreamLayerVisible ? Visibility.Visible : Visibility.Collapsed;
            if (_state.IsStreamLayerVisible)
            {
                // Stick-to-bottom: follow the stream only while the reader sits at
                // the bottom, so scrolling up to re-read is never overridden.
                var stickToBottom = Ui.IsScrolledToBottom(ResultScroll);
                // Append-only when the accumulated text simply grew: wholesale
                // reassignment re-layouts the whole box on every pump tick and
                // makes the window edge flicker.
                var accumulated = _state.AccumulatedText;
                if (accumulated.StartsWith(ResultStreamBox.Text, StringComparison.Ordinal))
                {
                    ResultStreamBox.AppendText(accumulated[ResultStreamBox.Text.Length..]);
                }
                else
                {
                    ResultStreamBox.Text = accumulated;
                }
                if (stickToBottom)
                {
                    ResultScroll.ScrollToEnd();
                }
            }

            var usePlainFinal = _state.IsRichBoxVisible && _richRenderFailed;
            ResultRichBox.Visibility = _state.IsRichBoxVisible && !usePlainFinal ? Visibility.Visible : Visibility.Collapsed;
            if (usePlainFinal)
            {
                ResultStreamBox.Text = _state.FinalRenderedText;
                ResultStreamBox.Visibility = Visibility.Visible;
            }
        }
        StreamIndicator.Visibility = _state.IsStreamIndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        IncompleteBadge.Visibility = _state.IsIncompleteBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.Visibility = _state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

        CopyButton.IsEnabled = _state.CanCopy && HasCurrentResultIdentity;
        SpeakButton.IsEnabled = _state.CanSpeak && HasCurrentResultIdentity;
        StarButton.IsEnabled = _state.CanStar && HasCurrentResultIdentity;
        ResultRichBox.IsDocumentEnabled = HasCurrentResultIdentity;

        // 免费引擎预提示在其待决期间压过「正在生成…」这类准备态：一旦真实
        // 文本开始到达或终态（完成/失败/取消）落地，立即让位给真实状态。
        // 明确的优先级与消费规则：错误/超时类一次性通知（PendingNotice）
        // 压过仅作解释的风格提示；一旦它接管，风格提示当场消费——既不被
        // style retirement 分支吞掉，也不在后续 sync 中复活。风格提示自身
        // 仍按原规则在首个真实 delta / 终态时让位并消费。
        if (_state.PendingNotice is not null)
        {
            _pendingStyleNotice = null;
            FooterStatus.Text = _state.PendingNotice;
        }
        else if (_pendingStyleNotice is not null)
        {
            if (string.IsNullOrEmpty(_state.AccumulatedText) &&
                _state.Stage is QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing)
            {
                FooterStatus.Text = _pendingStyleNotice;
            }
            else
            {
                _pendingStyleNotice = null;
                FooterStatus.Text = _state.StatusText;
            }
        }
        else
        {
            // 走到这里 PendingNotice 必为空（上面第一分支已接管并消费风格
            // 提示），直接呈现状态机文案。
            FooterStatus.Text = _state.StatusText;
        }

        // 动态快捷键提示：只承诺当前阶段真正可用的操作（流式仅 Esc 取消、
        // 完成含复制/朗读/收藏、partial 只含复制）。
        AutomationProperties.SetHelpText(SearchBox, _state.HintsText);
        RouteSelectorButton.IsEnabled = !_switchingRoute;

        if (!string.IsNullOrWhiteSpace(_state.RouteLatencyText))
        {
            RouteLatencyText.Text = _state.RouteLatencyText;
            var stagesTip = _state.RouteStagesTooltip;
            var badgeTip = string.IsNullOrWhiteSpace(stagesTip)
                ? _state.RouteLatencyText
                : $"{_state.RouteLatencyText}\n{stagesTip}";
            RouteLatencyBadge.ToolTip = badgeTip;
            RouteLatencyText.ToolTip = badgeTip;
            RouteLatencyBadge.Visibility = Visibility.Visible;
        }
        else
        {
            RouteLatencyText.Text = string.Empty;
            RouteLatencyBadge.ToolTip = null;
            RouteLatencyText.ToolTip = null;
            RouteLatencyBadge.Visibility = Visibility.Collapsed;
        }

        if (_state.PendingNotice is not null && _state.IsPendingNoticeError)
        {
            FooterStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        else if (_state.IsIncompleteBadgeVisible)
        {
            FooterStatus.SetResourceReference(TextBlock.ForegroundProperty,
                _state.IsPartialWarningTone ? "WarningBrush" : "DangerBrush");
        }
        else if (_state.Stage == QuickSearchUiStage.Failed)
        {
            FooterStatus.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        else if (_state.Stage is QuickSearchUiStage.Cancelled or QuickSearchUiStage.Partial)
        {
            FooterStatus.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        }
        else
        {
            FooterStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        }

        if (_state.IsIncompleteBadgeVisible)
        {
            IncompleteBadge.Text = _state.IncompleteBadgeText;
            IncompleteBadge.SetResourceReference(TextBlock.ForegroundProperty,
                _state.IsPartialWarningTone ? "WarningBrush" : "DangerBrush");
        }

        if (!string.IsNullOrWhiteSpace(_state.Phonetic))
        {
            PhoneticLabel.Text = $"[{_state.Phonetic}]";
            PhoneticLabel.Visibility = Visibility.Visible;
        }
        else
        {
            PhoneticLabel.Visibility = Visibility.Collapsed;
        }

        UpdateExplanationVisibility();

        // T9.30：Partial 带错误时也显示错误卡（含「打开设置」恢复入口），
        // 不再只把截断原因塞进页脚。警告色调的截断徽标仍避免与错误卡重复。
        if (_state.Stage is QuickSearchUiStage.Failed or QuickSearchUiStage.Partial &&
            !(_state.IsIncompleteBadgeVisible && _state.IsPartialWarningTone) &&
            !string.IsNullOrWhiteSpace(_state.ErrorMessage))
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorTitle.Text = TranslationPanelWindow.FriendlyError(_state.ErrorMessage);
            ErrorDetail.Text = _state.ErrorMessage;
        }
        else
        {
            ErrorCard.Visibility = Visibility.Collapsed;
        }

        if (_state.Stage is not QuickSearchUiStage.Streaming || string.IsNullOrEmpty(_state.AccumulatedText))
        {
            UpdateStarButton();
        }
        UpdateEmptyStateVisuals();
        UpdateStatusDot();
    }

    private void UpdateExplanationVisibility()
    {
        if (_isClosed || ExplanationCard is null || ExplanationToggleButton is null) return;
        var hasExplanation = !string.IsNullOrWhiteSpace(_state.Explanation);
        if (!hasExplanation)
        {
            ExplanationToggleButton.Visibility = Visibility.Collapsed;
            ExplanationCard.Visibility = Visibility.Collapsed;
            return;
        }

        ExplanationLabel.Text = _state.Explanation;
        if (_windowMode == QuickSearchWindowMode.Compact)
        {
            ExplanationToggleButton.Visibility = Visibility.Visible;
            ExplanationToggleButton.Content = _isCompactExplanationExpanded ? "收起释义 ▴" : "释义详情 ▾";
            ExplanationCard.Visibility = _isCompactExplanationExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ExplanationToggleButton.Visibility = Visibility.Collapsed;
            ExplanationCard.Visibility = Visibility.Visible;
        }
    }

    private void ExplanationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        _isCompactExplanationExpanded = !_isCompactExplanationExpanded;
        UpdateExplanationVisibility();
    }

    private async void SearchEnterKeycap_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        var text = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        _debounceTimer.Stop();
        _state.OnQueryTextChanged(SearchBox.Text);
        await PerformTranslateAsync();
    }

    private void SearchEnterKeycap_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            SearchEnterKeycap_Click(sender, e);
        }
    }

    private void SearchEnterKeycap_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        SearchEnterKeycap_Click(sender, e);

    private void UpdateEmptyStateVisuals()
    {
        if (_isClosed) return;
        var query = SearchBox.Text.Trim();
        var hasText = !string.IsNullOrWhiteSpace(query);
        if (SearchEnterKeycap is not null)
        {
            SearchEnterKeycap.Opacity = hasText ? 1.0 : 0.45;
            SearchEnterKeycap.IsEnabled = hasText;
            System.Windows.Automation.AutomationProperties.SetName(SearchEnterKeycap, hasText ? "立即翻译" : "立即翻译（请先输入文本）");
        }
        UpdateExpandedEmptyGuideVisibility();
        // 0.1.6 空态减法：动态提示条已移除（与页脚动态提示重复），此处不再
        // 有 EmptyStatePrompt 需要刷新。
    }

    private void UpdateExpandedEmptyGuideVisibility()
    {
        if (_isClosed || ExpandedEmptyGuide is null) return;
        ExpandedEmptyGuide.Visibility = _windowMode == QuickSearchWindowMode.Expanded || SizeToContent == SizeToContent.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateStatusDot()
    {
        if (_isClosed || StatusDot is null) return;
        var resourceKey = _state.PendingNotice is not null && _state.IsPendingNoticeError
            ? "DangerBrush"
            : _state.IsIncompleteBadgeVisible
                ? _state.IsPartialWarningTone ? "WarningBrush" : "DangerBrush"
                : _state.Stage switch
                {
                    QuickSearchUiStage.Failed => "DangerBrush",
                    QuickSearchUiStage.Cancelled or QuickSearchUiStage.Partial => "WarningBrush",
                    QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing => "AccentBrush",
                    QuickSearchUiStage.Completed => "SuccessBrush",
                    _ => "TextTertiaryBrush",
                };
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, resourceKey);

        var shouldBreathe = _state.IsDimmingPreviousResult ||
            _state.Stage is QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing;
        if (shouldBreathe)
        {
            if (!_breathingDotTimer.IsEnabled)
            {
                _breathingStep = 0;
                _breathingDotTimer.Start();
            }
        }
        else
        {
            if (_breathingDotTimer.IsEnabled)
            {
                _breathingDotTimer.Stop();
            }
            StatusDot.Opacity = 1.0;
        }
    }

    private void BreathingDotTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosed || StatusDot is null)
        {
            _breathingDotTimer.Stop();
            return;
        }
        _breathingStep = (_breathingStep + 1) % 6;
        StatusDot.Opacity = _breathingStep switch
        {
            0 => 1.0,
            1 => 0.78,
            2 => 0.52,
            3 => 0.36,
            4 => 0.52,
            _ => 0.78,
        };
    }

    // ================= Compact vs Expanded Mode, Pin & Warmup =================

    private void SyncWindowModeFromSettings(bool isInitial = false)
    {
        var shell = LoadShellSettings();
        if (isInitial || _windowMode != shell.QuickSearchWindowMode)
        {
            _windowMode = shell.QuickSearchWindowMode;
            ApplyWindowModeLayout(isInitial);
        }
    }

    internal void SetWindowMode(QuickSearchWindowMode mode, bool persist = true)
    {
        if (_isClosed) return;
        _windowMode = mode;
        ApplyWindowModeLayout(isInitial: false);
        if (persist)
        {
            UpdateShellSettings(current => current with { QuickSearchWindowMode = mode });
        }
    }

    internal void ToggleWindowMode()
    {
        var next = _windowMode == QuickSearchWindowMode.Compact
            ? QuickSearchWindowMode.Expanded
            : QuickSearchWindowMode.Compact;
        SetWindowMode(next, persist: true);
    }

    private void ModeToggle_Click(object sender, RoutedEventArgs e) => ToggleWindowMode();

    private void ApplyWindowModeLayout(bool isInitial)
    {
        if (_isClosed) return;
        _applyingWindowMode = true;
        try
        {
            if (_windowMode == QuickSearchWindowMode.Compact)
            {
                ResizeMode = ResizeMode.NoResize;
                MinWidth = CompactWindowWidth;
                MaxWidth = CompactWindowWidth;
                Width = CompactWindowWidth;
                MinHeight = 52.0;
                MaxHeight = CompactWindowMaxHeight;
                SizeToContent = SizeToContent.Height;
                ClearValue(HeightProperty);

                SearchBox.MaxHeight = 40.0;
                SearchEnterKeycap.Visibility = Visibility.Visible;
                StyleSelectorButton.Visibility = Visibility.Collapsed;
                SettingsButton.Visibility = Visibility.Collapsed;
                OpenInMainButton.Visibility = Visibility.Collapsed;
                CopySourceButton.Visibility = Visibility.Collapsed;

                HeaderBar.Padding = new Thickness(12, 8, 10, 8);
                ResultContainer.Padding = new Thickness(10, 8, 10, 8);
                FooterBar.Padding = new Thickness(12, 5, 12, 5);
                RouteLatencyText.MaxWidth = 132.0;
                RouteSelectorButton.MaxWidth = 108.0;

                if (TryFindResource("IconExpand") is Geometry expandGeo)
                {
                    ModeToggleIcon.Data = expandGeo;
                }
                ModeToggleButton.ToolTip = "切换到大窗模式";
                AutomationProperties.SetName(ModeToggleButton, "切换到大窗模式");
            }
            else
            {
                var shell = LoadShellSettings();
                var targetWidth = ShellSettings.NormalizeQuickSearchExpandedWidth(shell.QuickSearchExpandedWidth);
                var targetHeight = ShellSettings.NormalizeQuickSearchExpandedHeight(shell.QuickSearchExpandedHeight);

                ResizeMode = ResizeMode.CanResize;
                MinWidth = ExpandedMinWidth;
                MaxWidth = ExpandedMaxWidth;
                MinHeight = ExpandedMinHeight;
                MaxHeight = ExpandedMaxHeight;
                SizeToContent = SizeToContent.Manual;
                Width = targetWidth;
                Height = targetHeight;

                SearchBox.MaxHeight = 140.0;
                SearchEnterKeycap.Visibility = Visibility.Visible;
                StyleSelectorButton.Visibility = Visibility.Visible;
                SettingsButton.Visibility = Visibility.Visible;
                OpenInMainButton.Visibility = Visibility.Visible;
                CopySourceButton.Visibility = Visibility.Visible;

                HeaderBar.Padding = new Thickness(16, 12, 16, 12);
                ResultContainer.Padding = new Thickness(16, 14, 16, 14);
                FooterBar.Padding = new Thickness(16, 6, 16, 6);
                RouteLatencyText.MaxWidth = 210.0;
                RouteSelectorButton.MaxWidth = 136.0;

                if (TryFindResource("IconCollapse") is Geometry collapseGeo)
                {
                    ModeToggleIcon.Data = collapseGeo;
                }
                ModeToggleButton.ToolTip = "切换到小窗模式";
                AutomationProperties.SetName(ModeToggleButton, "切换到小窗模式");
            }

            UpdateExpandedEmptyGuideVisibility();
            UpdateExplanationVisibility();
            if (_windowMode == QuickSearchWindowMode.Expanded)
            {
                RefreshStyleSelector();
            }

            if (!isInitial && IsLoaded && !IsInDpiTransition)
            {
                UpdateLayout();
                if (_userMoved)
                {
                    ClampToWorkArea();
                }
                else
                {
                    CenterOnCurrentMonitor();
                }
            }
        }
        finally
        {
            _applyingWindowMode = false;
        }
    }

    private void ScheduleExpandedSizeSave()
    {
        if (_isClosed || _windowMode != QuickSearchWindowMode.Expanded) return;
        _expandedSizeDirty = true;
        _expandedSizeSaveTimer.Stop();
        _expandedSizeSaveTimer.Start();
    }

    private void ExpandedSizeSaveTimer_Tick(object? sender, EventArgs e)
    {
        _expandedSizeSaveTimer.Stop();
        FlushExpandedSizeSaveIfDirty();
    }

    private void FlushExpandedSizeSaveIfDirty()
    {
        _expandedSizeSaveTimer.Stop();
        if (!_expandedSizeDirty || _windowMode != QuickSearchWindowMode.Expanded) return;
        _expandedSizeDirty = false;
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var h = ActualHeight > 0 ? ActualHeight : Height;
        if (!double.IsFinite(w) || !double.IsFinite(h)) return;
        var clampedW = ShellSettings.NormalizeQuickSearchExpandedWidth(w);
        var clampedH = ShellSettings.NormalizeQuickSearchExpandedHeight(h);
        UpdateShellSettings(current => current with
        {
            QuickSearchExpandedWidth = clampedW,
            QuickSearchExpandedHeight = clampedH,
        });
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        var pinned = PinToggle.IsChecked == true;
        if (pinned)
        {
            _currentResultAutoTriggered = false;
        }
        FooterStatus.Text = pinned ? "查词栏已固定" : "查词栏已取消固定";
        RefreshPinTooltip();
    }

    private void RefreshPinTooltip()
    {
        if (PinToggle is null) return;
        var pinned = PinToggle.IsChecked == true;
        var tip = pinned ? "取消固定（当前保持置顶）" : "固定查词栏（失焦不收起）";
        PinToggle.ToolTip = tip;
        AutomationProperties.SetName(PinToggle, tip);
    }

    private void OpenInMain_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        var text = SearchBox.Text.Trim();
        var translation = HasCurrentResultIdentity
            ? (!string.IsNullOrWhiteSpace(_state.FinalRenderedText) ? _state.FinalRenderedText : _state.AccumulatedText)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(translation))
        {
            if (Application.Current is App emptyApp)
            {
                emptyApp.NavigateFromShortcut(InterfaceAction.Workbench);
            }
            return;
        }

        _currentResultAutoTriggered = false;
        var settings = TryGetCoreSettingsSafely();
        var sourceLang = _sessionSourceLanguage ?? settings?.SourceLanguage ?? LanguageCatalog.Auto;
        var targetLang = _sessionTargetLanguage ?? settings?.TargetLanguage ?? "zh-CN";
        var sessionState = (HasCurrentResultIdentity ? _state.Stage : QuickSearchUiStage.Idle) switch
        {
            QuickSearchUiStage.Completed => TranslationSessionState.Completed,
            QuickSearchUiStage.Failed => TranslationSessionState.Failed,
            _ => TranslationSessionState.Cancelled,
        };

        if (Application.Current is App app)
        {
            // Use the app-owned main window, not the first (possibly stale/hidden)
            // MainWindow in WPF's collection.
            app.OpenInMainWindow(text, targetLang, sourceLang, translation, sessionState);
        }
        else
        {
            var mainWindow = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
            mainWindow?.FocusTranslate(text, targetLang, sourceLang, translation, sessionState);
        }
        Hide();
    }

    private void TriggerWarmupIfEligible()
    {
        if (_isClosed || App.SuppressStartupForTests) return;
        var now = DateTime.UtcNow;
        if ((now - _lastWarmupUtc).TotalSeconds < 60) return;

        var shell = LoadShellSettings();
        var coreSettings = TryGetCoreSettingsSafely();
        if (!IsFreeRouteOutboundAllowed(shell, coreSettings)) return;
        if (!ResolveIsActiveRouteFree(coreSettings)) return;

        _lastWarmupUtc = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await FreeTranslateService.WarmupFreeRoutesAsync(shell, default, coreSettings).ConfigureAwait(false);
            }
            catch
            {
                // Prewarm is strictly best-effort and must never surface errors.
            }
        });
    }

    internal void RefreshRouteSelection()
    {
        if (_isClosed || RouteSelectorButton is null) return;
        try
        {
            var route = TextRouteMenu.ReadCurrent();
            RouteSelectorLabel.Text = route.Label;
            var modelHint = InterfaceKeys.ModelHotkey.IsValid
                ? $"点击切换模型 ({InterfaceKeys.ModelHotkey.DisplayName}) · 切换后立即重译"
                : "点击切换模型 · 切换后立即重译";
            RouteSelectorButton.ToolTip = string.IsNullOrWhiteSpace(route.Detail)
                ? modelHint
                : $"{route.Detail}\n{modelHint}";
        }
        catch (Exception)
        {
            RouteSelectorLabel.Text = "配置不可用";
            RouteSelectorButton.ToolTip = "无法读取引擎配置，请打开设置检查。";
        }
        RouteSelectorButton.IsEnabled = !_switchingRoute;
        RefreshStyleSelector();
    }

    internal System.Windows.Controls.ContextMenu BuildTextRouteMenu() =>
        TextRouteMenu.Create(id => _ = SwitchProfileRouteAsync(id), SwitchPublicRouteAsync, () => OpenSettings("Provider"));

    private void RouteSelector_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || !RouteSelectorButton.IsEnabled) return;
        if (_routeMenu is { IsOpen: true })
        {
            _routeMenu.IsOpen = false;
            return;
        }
        try
        {
            var menu = BuildTextRouteMenu();
            _routeMenu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(_routeMenu, menu)) _routeMenu = null;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isClosed && IsVisible && IsActive) SearchBox.Focus();
                }));
            };
            TranslationStyleMenu.Show(RouteSelectorButton, menu);
        }
        catch (Exception)
        {
            _routeMenu = null;
            _state.PostPendingNotice("无法读取引擎配置，请在设置中检查。", error: true);
            SyncUiWithState();
        }
    }

    private Task SwitchProfileRouteAsync(string id) => ChangeTextRouteAsync(() =>
    {
        var ok = ProfileManager.TrySwitchActiveProfile(id, out var error);
        return (ok, error);
    });

    private Task SwitchPublicRouteAsync(FreeEngineProvider provider) => ChangeTextRouteAsync(() =>
    {
        var ok = ProfileManager.TrySwitchToFreeEngine(provider, out var error);
        return (ok, error);
    });

    private async Task ChangeTextRouteAsync(Func<(bool Ok, string Error)> change)
    {
        if (_isClosed || _switchingRoute) return;
        // 与浮窗/工作台同一契约：切换立即生效。流式途中也允许切换——先取消
        // 在途请求（epoch 门会丢弃它的迟到回调），切换成功后用新引擎直接
        // 重译当前输入，绝不要求用户再按一次 Enter。
        _switchingRoute = true;
        RouteSelectorButton.IsEnabled = false;
        CancelRunningRequest();
        FooterStatus.Text = "正在切换…";
        try
        {
            var result = await Task.Run(change);
            if (_isClosed) return;
            if (!result.Ok)
            {
                _state.PostPendingNotice(result.Error, error: true);
                SyncUiWithState();
                return;
            }
            if (IsVisible && !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                // The switch owns only the config save. Translation has its
                // own cancellation/epoch fence and must remain switchable.
                _ = PerformTranslateAsync();
            }
            else
            {
                _state.PostPendingNotice("已切换模型");
                SyncUiWithState();
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                _state.PostPendingNotice($"切换失败：{exception.Message}", error: true);
                SyncUiWithState();
            }
        }
        finally
        {
            _switchingRoute = false;
            RefreshRouteSelection();
            RouteChanged?.Invoke();
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        await PerformTranslateAsync();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        SearchBox.Clear();
    }

    // ================= Language badge =================

    /// <summary>
    /// Repaints the header badge from the persisted pair. Called on Loaded
    /// and on every re-show: quick search keeps no language dropdown of its
    /// own, so the badge must always mirror what the next request will use.
    /// Degrades quietly when local state is unreadable.
    /// </summary>
    private void OnLanguagePairChanged(object? sender, EventArgs e)
    {
        if (_isClosed || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosed) RefreshLangBadge();
        }));
    }

    private void RefreshLangBadge()
    {
        if (_isClosed) return;
        try
        {
            var settings = CoreBridge.GetSettings();
            var source = _sessionSourceLanguage ?? settings.SourceLanguage;
            var target = _sessionTargetLanguage ?? settings.TargetLanguage;
            LangBadge.Text = $"{ShortLanguage(source)} → {ShortLanguage(target)}";
            var tip = $"{LanguageCatalog.DisplayName(source)} → {LanguageCatalog.DisplayName(target)}（点击交换方向）";
            if (LangBadgeButton is not null) LangBadgeButton.ToolTip = tip;
            LangBadge.ToolTip = tip;
        }
        catch (Exception)
        {
            // Headless/offline contexts keep the last badge.
        }
    }

    private static string ShortLanguage(string language) => LanguageCatalog.Normalize(language) switch
    {
        "auto" => "自动",
        "zh-CN" => "简中",
        "zh-TW" => "繁中",
        var tag => LanguageCatalog.DisplayName(tag),
    };

    private async void LangBadgeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        try
        {
            var settings = CoreBridge.GetSettings();
            var currentSource = _sessionSourceLanguage ?? settings.SourceLanguage ?? LanguageCatalog.Auto;
            var currentTarget = _sessionTargetLanguage ?? settings.TargetLanguage ?? "zh-CN";
            var previousTranslation = MarkdownPresenter.ToPlainText(
                _state.FinalRenderedText,
                preserveListStructure: true);
            var (source, target) = LanguageCatalog.Swap(currentSource, currentTarget);
            _sessionSourceLanguage = source;
            _sessionTargetLanguage = target;
            if (!string.IsNullOrWhiteSpace(previousTranslation))
            {
                SearchBox.Text = previousTranslation;
            }
            RefreshLangBadge();
            await CoreBridge.UpdateSettingsAsync(current => current with
            {
                SourceLanguage = source,
                TargetLanguage = target,
            });
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                await PerformTranslateAsync();
            }
        }
        catch
        {
            // Ignore swap error
        }
    }

    // ================= Global translation-style selector =================

    /// <summary>
    /// Repaints the shared style selector from local state. The built-in free
    /// engine disables the selector with the honest wording; an unreadable
    /// local state keeps it visible with an honest caveat instead.
    /// </summary>
    private void RefreshStyleSelector()
    {
        if (_isClosed) return;
        try
        {
            TranslationStyleMenu.ApplyTo(StyleSelectorButton);
            StyleSelectorLabel.Text = TranslationStyleMenu.ActiveLabel();
            if (_windowMode == QuickSearchWindowMode.Compact)
            {
                StyleSelectorButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception)
        {
            // The selector is additive UI; never break quick search over it.
        }
    }

    private void StyleSelector_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        RefreshStyleSelector();
        var menu = TranslationStyleMenu.Build(
            styleChosen: template => _ = ChooseStyleAsync(template),
            reportStatus: message => FooterStatus.Text = message,
            managePrompts: () => OpenSettings("Prompt"));
        if (menu is null) return;
        TranslationStyleMenu.Show(button, menu);
    }

    private async Task ChooseStyleAsync(PromptTemplateDto template)
    {
        // Optimistic label and the shared persist-then-retranslate contract
        // live in TranslationStyleMenu.ChooseStyleAsync; the window-specific
        // guard below stays here: a closed or hidden quick-search bar never
        // fires a retranslation.
        var applied = await TranslationStyleMenu.ChooseStyleAsync(
            template,
            StyleSelectorLabel,
            message => FooterStatus.Text = message,
            () => SearchBox.Text,
            () => !_isClosed && IsVisible ? PerformTranslateAsync() : Task.CompletedTask);
        if (!applied)
        {
            RefreshStyleSelector();
        }
    }

    internal void OpenSettings(string? page = null)
    {
        if (_openSettings is not null)
        {
            _openSettings.Invoke(page);
            return;
        }

        if (Application.Current is not null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is MainWindow main && main.OpenSettings is not null)
                {
                    main.OpenSettings.Invoke(page);
                    return;
                }
            }

            try
            {
                var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault()
                    ?? new SettingsWindow(ShellSettingsStore.Load(), _history, _vocabulary);
                if (page is not null) settingsWindow.ShowPage(page);
                // The XAML is Manual: without convergence this fallback would
                // open at the shell's cascade position with no work-area clamp.
                WindowPositioner.ConvergeFirstShow(settingsWindow);
                settingsWindow.Show();
                settingsWindow.Activate();
            }
            catch (Exception exception)
            {
                FooterStatus.Text = $"无法打开设置：{exception.Message}";
            }
        }
    }



    private void SpeakCurrent()

    {

        if (_isClosed || !_state.CanSpeak || !HasCurrentResultIdentity) return;



        var textToSpeak = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : !string.IsNullOrWhiteSpace(_state.AccumulatedText)

                ? _state.AccumulatedText

                : SearchBox.Text.Trim();



        if (!string.IsNullOrWhiteSpace(textToSpeak))

        {

            var clean = MarkdownPresenter.ToPlainText(textToSpeak);

            if (TtsService.IsSpeaking)

            {

                TtsService.Stop();

            }

            else

            {

                TtsService.Speak(clean, _sessionTargetLanguage ?? CoreBridge.GetSettings().TargetLanguage);

            }

        }

    }



    private void Speak_Click(object sender, RoutedEventArgs e) => SpeakCurrent();



    private async void Copy_Click(object sender, RoutedEventArgs e)

    {

        if (_isClosed || !_state.CanCopy || !HasCurrentResultIdentity) return;
        _currentResultAutoTriggered = false;



        var textToCopy = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : _state.AccumulatedText;



        if (!string.IsNullOrWhiteSpace(textToCopy))

        {

            var clean = MarkdownPresenter.ToPlainText(textToCopy, preserveListStructure: true);

            // Hardened write: a raw Clipboard.SetText on the UI thread freezes

            // the whole window while another app holds the clipboard open.

            FooterStatus.Text = await PopGlot.Windows.Sections.Helpers.CopyToClipboardAsync(clean)

                ? "已复制译文到剪贴板"

                : "剪贴板被其他应用占用，未复制";

        }

    }

    private async void CopySource_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed) return;
        var text = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _currentResultAutoTriggered = false;
        FooterStatus.Text = await PopGlot.Windows.Sections.Helpers.CopyToClipboardAsync(text)
            ? "已复制原文到剪贴板"
            : "剪贴板被其他应用占用，未复制";
    }



    private void Star_Click(object sender, RoutedEventArgs e)

    {

        if (_isClosed || !_state.CanStar || !HasCurrentResultIdentity) return;
        _currentResultAutoTriggered = false;



        var word = SearchBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(word)) return;



        var targetText = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : _state.AccumulatedText;



        if (string.IsNullOrWhiteSpace(targetText)) return;



        var cleanTarget = MarkdownPresenter.ToPlainText(targetText);

        var languageSettings = CoreBridge.GetSettings();

        var result = _vocabulary.ToggleStar(

            word,

            cleanTarget,

            _state.Phonetic ?? "",

            _state.Explanation ?? "",

            _sessionSourceLanguage ?? languageSettings.SourceLanguage ?? LanguageCatalog.Auto,

            _sessionTargetLanguage ?? languageSettings.TargetLanguage ?? "zh-CN");



        UpdateStarButton();
        FooterStatus.Text = result.Persisted
            ? (result.Starred ? "已加入生词本" : "已从生词本移除")
            : result.DescribeFailureZh();
    }

    private void OnTtsSpeakingStateChanged(object? sender, bool isSpeaking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (isSpeaking)
            {
                SpeakIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
                SpeakButton.ToolTip = InterfaceKeys.SpeakHotkey.IsValid
                    ? $"停止朗读 ({InterfaceKeys.SpeakHotkey.DisplayName})"
                    : "停止朗读";
            }
            else
            {
                // Re-apply the captured Foreground binding instead of
                // ClearValue: the binding expression IS the local value, so
                // clearing it would leave the idle icon with no fill at all.
                if (_speakIconFillBinding is not null)
                {
                    SpeakIcon.SetBinding(System.Windows.Shapes.Shape.FillProperty, _speakIconFillBinding);
                }
                SpeakButton.ToolTip = InterfaceKeys.SpeakHotkey.IsValid
                    ? $"朗读译文 ({InterfaceKeys.SpeakHotkey.DisplayName})"
                    : "朗读译文";
            }
            // The accessibility name must follow the ToolTip so screen readers
            // announce the action the click will actually perform right now.
            System.Windows.Automation.AutomationProperties.SetName(
                SpeakButton,
                isSpeaking ? "停止朗读" : "朗读译文");
        });
    }

    private void UpdateStarButton()
    {
        var word = SearchBox.Text.Trim();
        var settings = CoreBridge.GetSettings();
        var starred = !string.IsNullOrWhiteSpace(word) &&
            _vocabulary.IsStarred(
                word,
                _sessionSourceLanguage ?? settings.SourceLanguage ?? LanguageCatalog.Auto,
                _sessionTargetLanguage ?? settings.TargetLanguage ?? "zh-CN");
        StarIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, starred ? "AccentBrush" : "TextSecondaryBrush");
        if (starred)
        {
            StarButton.SetResourceReference(Button.BackgroundProperty, "AccentSoftBrush");
        }
        else
        {
            StarButton.ClearValue(Button.BackgroundProperty);
        }
        StarButton.ToolTip = starred ? "从生词本移除 (Ctrl+S)" : "收藏到生词本 (Ctrl+S)";
        // The static XAML name must follow the dynamic state so screen
        // readers announce the action the click will actually perform.
        System.Windows.Automation.AutomationProperties.SetName(StarButton, (string)StarButton.ToolTip);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Cancels the in-flight request WITHOUT destroying the session state, so
    /// the already-streamed partial stays visible (C05 X/Esc semantics).
    /// </summary>
    private void CancelRunningRequest()
    {
        StopPendingLiveQuery();
        try
        {
            _cts?.Cancel();
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CancelRunningRequest();
        FlushExpandedSizeSaveIfDirty();
        StoreSessionSnapshot();
        if (!ForceClose)
        {
            // C05: X/Alt+F4 cancel the running request, keep the partial and
            // hide. Real destruction happens only on app exit.
            e.Cancel = true;
            // Close-as-hide 与用户 Hide 同属视图生命周期终点：一次性通知
            // （如预读取超时落地提示）与仅作解释的风格提示都不跨收起存活。
            _state.PostPendingNotice(null);
            _pendingStyleNotice = null;
            SyncUiWithState();
            base.Hide();
            return;
        }
        base.OnClosing(e);
    }

    private async void Window_PreviewShortcutKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = ModifiersOverrideForTest ?? Keyboard.Modifiers;
        var action = InterfaceKeys.Resolve(InterfaceKeys.Normalize(e), modifiers);
        switch (action)
        {
            case InterfaceAction.Model when RouteSelectorButton.IsEnabled:
                e.Handled = true;
                RouteSelector_Click(RouteSelectorButton, new RoutedEventArgs());
                break;
            case InterfaceAction.Style when StyleSelectorButton.Visibility == Visibility.Visible && StyleSelectorButton.IsEnabled:
                e.Handled = true;
                StyleSelector_Click(StyleSelectorButton, new RoutedEventArgs());
                break;
            case InterfaceAction.Swap when LangBadgeButton.IsEnabled:
                e.Handled = true;
                LangBadgeButton_Click(LangBadgeButton, new RoutedEventArgs());
                break;
            case InterfaceAction.Translate:
                if (Ui.IsImeComposing(SearchBox, e)) return;
                e.Handled = true;
                _debounceTimer.Stop();
                _state.OnQueryTextChanged(SearchBox.Text);
                await PerformTranslateAsync();
                break;
            case InterfaceAction.Speak when _state.CanSpeak:
                e.Handled = true;
                SpeakCurrent();
                break;
            case InterfaceAction.Settings:
            case InterfaceAction.Workbench:
            case InterfaceAction.Library:
                if (Application.Current is App app)
                {
                    e.Handled = true;
                    app.NavigateFromShortcut(action);
                }
                break;
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = ModifiersOverrideForTest ?? Keyboard.Modifiers;
        if (e.Key == Key.Escape || e.ImeProcessedKey == Key.Escape)
        {
            // E3: a live IME composition owns this Escape — hiding or
            // cancelling the window out from under the composition would
            // strand its residue in the search box. The residue was already
            // cleared safely in the preview pass; leave the key unhandled so
            // the IME can cancel its own composition, and consume the sample
            // so it cannot outlive this key press.
            if (_escapeOwnedByComposition)
            {
                _escapeOwnedByComposition = false;
                return;
            }
            e.Handled = true;
            if (_routeMenu is { IsOpen: true })
            {
                _routeMenu.IsOpen = false;
                return;
            }
            // C05/A06 Esc ladder: ANY active phase cancels first (the live
            // _cts is the truth, not a UI stage snapshot), keeping the
            // partial; an idle/final window hides with its session.
            if (_cts is { IsCancellationRequested: false })
            {
                CancelRunningRequest();
                return;
            }
            Hide();
        }
        else if (e.Key == Key.R && modifiers == ModifierKeys.Control)
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                e.Handled = true;
                _debounceTimer.Stop();
                _state.OnQueryTextChanged(SearchBox.Text);
                await PerformTranslateAsync();
            }
        }
        else if (e.Key == Key.S && modifiers == ModifierKeys.Control && _state.CanStar)
        {
            e.Handled = true;
            Star_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _state.CanCopy)
        {
            e.Handled = true;
            Copy_Click(this, new RoutedEventArgs());
        }

    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // C05/F07: focus loss never destroys the session any more. A
        // same-process surface (IME, context menu, drop-down) is not a real
        // leave at all; a window with a draft, a running task or a result
        // stays visible; only a fresh, empty window hides.
        if (_isClosed || PinToggle?.IsChecked == true)
        {
            return;
        }
        // Foreign activation first closes the capture-owned menu, then the
        // same empty-window decision runs — an open route menu must never
        // leave a topmost frameless window floating over other apps.
        if (_routeMenu is { IsOpen: true })
        {
            _routeMenu.IsOpen = false;
        }
        if (ForegroundBelongsToThisProcess())
        {
            return;
        }
        if (HasContentOrActivity())
        {
            return;
        }
        Hide();
    }

    private bool HasContentOrActivity() =>
        !string.IsNullOrWhiteSpace(SearchBox.Text) ||
        _state.Stage is not QuickSearchUiStage.Idle ||
        _state.IsResultVisible ||
        _state.IsStreamLayerVisible ||
        _state.AccumulatedText.Length > 0;

    private bool ForegroundBelongsToThisProcess()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return false;
        }
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != 0 && foreground == handle)
        {
            return false;
        }
        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        return processId == Environment.ProcessId;
    }

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [System.Runtime.InteropServices.LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);
    }

    internal void StoreSessionSnapshot()
    {
        if (_isClosed || _currentResultAutoTriggered || !_state.IsResultForCurrentQuery) return;

        var query = !string.IsNullOrWhiteSpace(_state.CurrentQuery)
            ? _state.CurrentQuery.Trim()
            : SearchBox?.Text?.Trim() ?? string.Empty;

        var resultText = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)
            ? _state.FinalRenderedText
            : _state.AccumulatedText;

        if (string.IsNullOrEmpty(query) || _state.Stage == QuickSearchUiStage.Idle)
        {
            return;
        }

        var hasResult = !string.IsNullOrWhiteSpace(resultText);
        var (state, isPartial) = _state.Stage switch
        {
            QuickSearchUiStage.Completed => (TranslationSessionState.Completed, false),
            // T9.34：Partial 存「已取消 · 不完整」（与浮窗/工作台一致），
            // 恢复时标不完整而非失败；保留文本、不重发。
            QuickSearchUiStage.Partial => (TranslationSessionState.Cancelled, hasResult),
            QuickSearchUiStage.Failed => (TranslationSessionState.Failed, hasResult),
            _ => (TranslationSessionState.Cancelled, hasResult),
        };

        var sessionId = GetSessionId();
        var key = new SnapshotDeduplicationKey(sessionId, query, hasResult ? resultText : null, state, isPartial);
        if (_lastStoredKey == key)
        {
            return;
        }

        var recent = App.SharedSessionStore.PeekRecent();
        if (recent is not null &&
            recent.Origin == SessionOrigin.QuickSearch &&
            string.Equals(recent.SourceText, query, StringComparison.Ordinal) &&
            string.Equals(recent.ResultText, hasResult ? resultText : null, StringComparison.Ordinal) &&
            recent.State == state &&
            recent.IsPartial == isPartial)
        {
            _lastStoredKey = key;
            return;
        }

        string sourceLang = LanguageCatalog.Auto;
        string targetLang = "zh-CN";
        try
        {
            var settings = CoreBridge.GetSettings();
            sourceLang = _sessionSourceLanguage ?? settings.SourceLanguage ?? LanguageCatalog.Auto;
            targetLang = _sessionTargetLanguage ?? settings.TargetLanguage ?? "zh-CN";
        }
        catch
        {
            // Headless / offline fallback
        }

        var session = StoredSession.Create(
            sessionId: sessionId,
            origin: SessionOrigin.QuickSearch,
            sourceText: query,
            sourceLang: sourceLang,
            targetLang: targetLang,
            engineProfileId: null,
            engineName: null,
            state: state,
            resultText: hasResult ? resultText : null,
            explanationText: _state.Explanation ?? _state.ErrorMessage,
            isPartial: isPartial);

        if (App.SharedSessionStore.TryStore(session, out var rejection))
        {
            _lastStoredKey = key;
        }
        else
        {
            // Called from the close path: no surface remains to show it.
            App.LogSessionStoreRejection(rejection);
        }
    }

    private void OnClosedCleanup()
    {
        if (_isClosed) return;
        FlushExpandedSizeSaveIfDirty();
        StoreSessionSnapshot();
        _isClosed = true;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= DebounceTimer_Tick;
        _liveSettingsTimer.Stop();
        _liveSettingsTimer.Tick -= LiveSettingsTimer_Tick;
        ShellSettingsStore.Changed -= OnShellSettingsChanged;
        _streamFlushTimer.Stop();
        _streamFlushTimer.Tick -= StreamFlushTimer_Tick;
        _clampThrottleTimer.Stop();
        _clampThrottleTimer.Tick -= ClampThrottleTimer_Tick;
        _expandedSizeSaveTimer.Stop();
        _expandedSizeSaveTimer.Tick -= ExpandedSizeSaveTimer_Tick;
        _breathingDotTimer.Stop();
        _breathingDotTimer.Tick -= BreathingDotTimer_Tick;
        CoreBridge.LanguagePairChanged -= OnLanguagePairChanged;
        ThemeService.ThemeChanged -= _themeChangedHandler;
        TtsService.SpeakingStateChanged -= OnTtsSpeakingStateChanged;
        _state.OnClose();
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;
    }
}
