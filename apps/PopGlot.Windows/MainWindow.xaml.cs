using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// The main window is a work surface only: the translate workbench and the
/// library. All settings — including the save bar — live in SettingsWindow;
/// the footer is a quiet status line plus the entry to settings.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore? _vocabulary;
    private readonly TranslationCoordinator _coordinator;

    internal MainWindow(ShellSettings shellSettings, HistoryStore history, VocabularyStore? vocabulary = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _coordinator = new TranslationCoordinator(history, vocabulary);

        InitializeComponent();

        TranslateSection.Initialize(_coordinator, vocabulary);
        LibrarySection.Initialize(history, vocabulary);

        // Wire up cross-section events.
        LibrarySection.LoadToTranslate += OnLoadToTranslate;
        LibrarySection.StatusChanged += SetStatus;

        // 状态行的瞬时回落定时器（见 SetStatus / SetResidentStatus）。
        _transientRevertTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = RevertDelay(StatusTone.Info),
        };
        _transientRevertTimer.Tick += (_, _) =>
        {
            _transientRevertTimer.Stop();
            _transientStatus = null;
            PaintResidentStatus();
        };

        RefreshEngineStatus();
        RefreshShellStatusForConfig();
        RefreshEngineStatusOnActivated();
        // The close button's name/tooltip depend on the persisted
        // close-to-tray preference; paint the authoritative value once up
        // front (and again on every settings save via App.TryApplyShellSettings).
        RefreshCloseButtonForTraySetting();

        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += (_, _) => ThemeService.ApplyWindowChrome(this);
        StateChanged += (_, _) => UpdateMaximizeButtonGlyph();
        // E3-F12/F14: one stable Normal-state DIP size, seeded from the XAML
        // Width/Height. PerMonitorV2 round trips used to leave the window at
        // 747x560 (1120x760 shrank by 1/1.5) because nothing remembered the
        // DIP size the user actually had.
        _normalSizeTracker = new StableNormalSizeTracker(new Size(Width, Height));
        SizeChanged += (_, _) => ObserveResizeForStableSize();
        Loaded += (_, _) =>
        {
            ApplyResponsiveBreakpoints(RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : Width);
            TranslateSection.CompleteOnboarding = CompleteOnboarding;
            TryBeginFirstRunOnboarding();
        };
    }

    /// <summary>Entry point into SettingsWindow, wired by App.</summary>
    internal Action? OpenSettings { get; set; }

    // ================= Stable Normal size across DPI changes =================

    private StableNormalSizeTracker? _normalSizeTracker;
    private int _dpiGeneration;

    /// <summary>
    /// E3-F13/F14: a WM_DPICHANGED transition must never decide the window's
    /// size. WPF resizes per the OS-suggested rect, and the diagnostic evidence
    /// showed a 1120x760 window landing at 747x560 after a 150%→100% round
    /// trip. We therefore remember ONE stable Normal-state DIP size and, after
    /// the transition settles (Dispatcher ApplicationIdle), restore it and
    /// re-clamp into the target monitor's work area. Maximized/minimized
    /// states are never touched, and resizes that happen inside a transition
    /// can never pollute the stable value (see
    /// <see cref="StableNormalSizeTracker"/>). No size is ever persisted.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _normalSizeTracker?.BeginDpiTransition();
        var generation = ++_dpiGeneration;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (generation != _dpiGeneration || _normalSizeTracker is null)
            {
                return; // a newer DPI change owns the restore now
            }
            RestoreStableNormalSizeAfterDpiChange();
        }));
    }

    private void RestoreStableNormalSizeAfterDpiChange()
    {
        var tracker = _normalSizeTracker;
        if (tracker is null)
        {
            return;
        }
        var stable = tracker.EndDpiTransition();
        // Maximized/minimized: never rewrite the window, the stable DIP size
        // stays ready for when the user returns to Normal.
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        var restored = RestoredNormalRect(
            new Rect(Left, Top, ActualWidth, ActualHeight),
            stable,
            CurrentMonitorWorkAreaDip(),
            new Size(MinWidth, MinHeight));
        Width = restored.Width;
        Height = restored.Height;
        Left = restored.Left;
        Top = restored.Top;
        // The transition closed at the top, so the restore's own SizeChanged
        // events land after it — but they only ever restate the values written
        // here. This explicit observation covers the one real change: a
        // work-area clamp that had to shrink the window becomes the new stable
        // normal size (the monitor cannot fit more).
        _ = tracker.TryObserveResize(new Size(restored.Width, restored.Height), isNormalState: true);
    }

    private void ObserveResizeForStableSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        _ = _normalSizeTracker?.TryObserveResize(
            new Size(ActualWidth, ActualHeight),
            isNormalState: WindowState == WindowState.Normal);
    }

    private Rect CurrentMonitorWorkAreaDip()
    {
        var windowScale = ScreenGeometry.ScaleOf(this);
        var scaleX = windowScale.X > 0 ? windowScale.X : 1.0;
        var scaleY = windowScale.Y > 0 ? windowScale.Y : 1.0;
        var topLeftPixels = new Point(
            (double.IsFinite(Left) ? Left : 0) * scaleX,
            (double.IsFinite(Top) ? Top : 0) * scaleY);
        // Target-monitor scale — never a transitioning window's stale DPI.
        var scale = ScreenGeometry.ScaleOfMonitorAtPixel(topLeftPixels);
        var targetScaleX = scale.X > 0 ? scale.X : scaleX;
        var targetScaleY = scale.Y > 0 ? scale.Y : scaleY;
        var workPixels = ScreenGeometry.WorkAreaForPixel(topLeftPixels);
        return new Rect(
            ScreenGeometry.PixelToDip(workPixels.Left, targetScaleX),
            ScreenGeometry.PixelToDip(workPixels.Top, targetScaleY),
            ScreenGeometry.PixelToDip(workPixels.Width, targetScaleX),
            ScreenGeometry.PixelToDip(workPixels.Height, targetScaleY));
    }

    /// <summary>
    /// Pure F14 fix: the restored Normal rect keeps the stable DIP size at the
    /// current position, clamped into the target monitor's work area.
    /// </summary>
    internal static Rect RestoredNormalRect(Rect currentDip, Size stableDip, Rect workAreaDip, Size minSize) =>
        WindowPositioner.ClampToWorkArea(
            new Rect(currentDip.Location, stableDip), workAreaDip, minSize);

    /// <summary>
    /// Direct route from the 添加翻译引擎 call to action: opens settings
    /// on the engine page inside the add-engine flow. Wired by App.
    /// </summary>
    internal Action? OpenAddEngineFlow { get; set; }

    /// <summary>Tray balloon used for the one-time close-to-tray hint.</summary>
    internal Action<string, string>? NotifyTray { get; set; }

    /// <summary>Invoked when the window is closed and user chose not to minimize to tray.</summary>
    internal Action? RequestExit { get; set; }

    internal bool AllowClose { get; set; }

    private bool _closeHintShown;
    private bool _isSwitchingEngine = false;

    // ================= Window Caption Controls =================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonGlyph();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    // The sidebar entry is the one settings entry of the main window; the
    // old footer duplicate was removed so "打开设置" names exactly one control.
    private void NavSettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSettings?.Invoke();

    private void UpdateMaximizeButtonGlyph()
    {
        if (MaximizeBtn is null) return;
        var maximized = WindowState == WindowState.Maximized;
        var label = maximized ? "向下还原" : "最大化";
        Ui.SetIcon(
            MaximizeBtn,
            (Geometry)FindResource(maximized ? "IconCaptionRestore" : "IconCaptionMax"));
        // Glyph, tooltip AND accessibility name must agree in all three channels.
        MaximizeBtn.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MaximizeBtn, label);
    }

    /// <summary>
    /// Close-button truth follows the persisted CloseMainWindowToTray setting:
    /// with tray residency the button means「关闭到托盘」; without it the same
    /// button exits the app, so it must say「退出 PopGlot」. Called at
    /// construction and from App.TryApplyShellSettings after every settings
    /// save, so a flip in 设置 → 通用 repaints without reopening the window.
    /// </summary>
    internal void RefreshCloseButtonForTraySetting()
    {
        if (CloseBtn is null)
        {
            return;
        }
        bool closeToTray;
        try
        {
            closeToTray = ShellSettingsStore.Load().CloseMainWindowToTray;
        }
        catch (Exception)
        {
            return; // unreadable settings: keep the last known-good label
        }
        var label = closeToTray ? EngineWording.CloseToTrayAction : EngineWording.ExitAppAction;
        System.Windows.Automation.AutomationProperties.SetName(CloseBtn, label);
        CloseBtn.ToolTip = label;
    }

    // ================= Engine status footer =================

    private int _refreshStatusGate;

    private void RefreshEngineStatusOnActivated() =>
        Activated += async (_, _) =>
        {
            if (Interlocked.CompareExchange(ref _refreshStatusGate, 1, 0) != 0)
            {
                return;
            }
            try
            {
                await RefreshEngineStatusAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshStatusGate, 0);
            }
        };

    internal async Task RefreshEngineStatusAsync()
    {
        try
        {
            // Execute credential (CredRead) and store reads off the UI thread (C11 zero-disk activation).
            var statusData = await Task.Run(() =>
            {
                var settings = CoreBridge.GetSettings();
                var activeProfile = ProfileManager.Load().TryGetActiveProfile();
                var hasKey = activeProfile is not null &&
                    CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(activeProfile));
                var consent = ShellSettingsStore.Load().FreeEngineConsent;
                var hasUserEngine = ProfileManager.HasConfiguredUserEngine();
                return (settings, activeProfile, hasKey, consent, hasUserEngine);
            });

            var (settings, activeProfile, hasKey, consent, hasUserEngine) = statusData;
            var (_, tone) = DescribeEngine(settings, hasKey, consent);
            EngineSummary.Text = UsesFreeEngine(settings, hasKey, consent)
                ? EngineWording.ActiveFreeEngineName()
                : activeProfile is not null
                    ? activeProfile.Name
                    : DescribeEngine(settings, hasKey, consent).Summary;
            EngineDot.SetResourceReference(Border.BackgroundProperty, tone switch
            {
                StatusTone.Error => "DangerBrush",
                StatusTone.Warning => "WarningBrush",
                StatusTone.Info => "TextSecondaryBrush",
                _ => "SuccessBrush",
            });

            TranslateSection.RefreshAfterSettingsChanged();

            // 配置/隐私事实走类型化常驻通道：不再用 Text.Contains 猜测当前
            // 状态，也不在每次窗口激活时把瞬时消息砸成「就绪」。
            RefreshShellStatusForConfig();
        }
        catch (Exception)
        {
            // Profile/shell stores unavailable: keep the current status text
            // rather than crash on window activation.
        }
    }

    // ================= Typed status footer =================

    /// <summary>
    /// 状态行的常驻通道（类型化来源 + 优先级）。EngineConfig 承载配置与
    /// 隐私事实（未配置引擎 / 当前使用内置免费引擎），优先级高于 Ready。
    /// Hotkey 承载全局快捷键真实失效的事实，优先级最高——它是用户最需要
    /// 解释的「为什么快捷键没反应」。瞬时消息（复制成功、已载入记录、切
    /// 换完成等）有自己的展示窗口，到期后回到这里——因此永远不会永久覆
    /// 盖配置/隐私/快捷键警告。
    /// </summary>
    private enum StatusChannel
    {
        Ready = 0,
        EngineConfig = 1,
        Hotkey = 2,
    }

    private sealed record StatusEntry(string Message, StatusTone Tone);

    private readonly Dictionary<StatusChannel, StatusEntry> _residentStatuses = new();
    private StatusEntry? _transientStatus;
    private readonly System.Windows.Threading.DispatcherTimer _transientRevertTimer;

    /// <summary>Transient messages hold the floor by tone: errors linger
    /// longer, confirmations clear quickly.</summary>
    private static TimeSpan RevertDelay(StatusTone tone) => tone switch
    {
        StatusTone.Error => TimeSpan.FromSeconds(10),
        StatusTone.Warning => TimeSpan.FromSeconds(6),
        _ => TimeSpan.FromSeconds(4),
    };

    /// <summary>
    /// 工作台/引擎操作的瞬时消息：立即展示，按语气定时回落到常驻状态。
    /// 常驻的配置/隐私警告不会被替换或丢失。
    /// </summary>
    private void SetStatus(string message, StatusTone tone)
    {
        _transientStatus = new StatusEntry(message, tone);
        _transientRevertTimer.Stop();
        _transientRevertTimer.Interval = RevertDelay(tone);
        _transientRevertTimer.Start();
        PaintStatus(message, tone);
    }

    /// <summary>
    /// Writes a persistent channel. By default it repaints immediately —
    /// a config fact outranks any momentary toast; pass
    /// <paramref name="deferToTransient"/> for downgrades (e.g. back to
    /// 就绪) so an in-flight message can finish its window first.
    /// </summary>
    private void SetResidentStatus(StatusChannel channel, string message, StatusTone tone, bool deferToTransient = false)
    {
        _residentStatuses[channel] = new StatusEntry(message, tone);
        if (deferToTransient && _transientStatus is not null && _transientRevertTimer.IsEnabled)
        {
            return; // 瞬时消息保持展示权，回落时自然显示新的常驻底座。
        }
        _transientStatus = null;
        _transientRevertTimer.Stop();
        PaintResidentStatus();
    }

    /// <summary>Paints the highest-priority resident channel (Hotkey over
    /// EngineConfig over Ready), falling back to the neutral ready state.</summary>
    private void PaintResidentStatus()
    {
        if (_residentStatuses.TryGetValue(StatusChannel.Hotkey, out var hotkey))
        {
            PaintStatus(hotkey.Message, hotkey.Tone);
            return;
        }
        if (_residentStatuses.TryGetValue(StatusChannel.EngineConfig, out var config))
        {
            PaintStatus(config.Message, config.Tone);
            return;
        }
        var ready = _residentStatuses.TryGetValue(StatusChannel.Ready, out var r)
            ? r
            : new StatusEntry("就绪", StatusTone.Success);
        PaintStatus(ready.Message, ready.Tone);
    }

    /// <summary>
    /// Removes a resident channel and repaints the next-highest priority
    /// one immediately (e.g. Hotkey cleared → back to EngineConfig/Ready).
    /// No-op when the channel was not set, so a duplicate clear can never
    /// stomp an unrelated status that was set in between.
    /// </summary>
    private void ClearResidentStatus(StatusChannel channel)
    {
        if (_residentStatuses.Remove(channel))
        {
            PaintResidentStatus();
        }
    }

    private void PaintStatus(string message, StatusTone tone)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextSecondaryBrush",
        });
        // Info covers the idle/ready state and neutral confirmations; a gray
        // dot reads as "disabled" — the accent reads as "alive" instead.
        StatusDot.SetResourceReference(Border.BackgroundProperty, tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "AccentBrush",
        });
    }

    /// <summary>
    /// The resident config/privacy channel must always describe the live
    /// engine configuration: with no configured user engine it says so (or
    /// names the allowed free fallback); once an engine exists it downgrades
    /// to the neutral ready state. Typed channels replace the former
    /// Text.Contains sniffing.
    /// </summary>
    private void RefreshShellStatusForConfig()
    {
        try
        {
            if (!ProfileManager.HasConfiguredUserEngine())
            {
                if (ShellSettingsStore.Load().FreeEngineConsent == FreeEngineConsent.Allowed)
                {
                    SetResidentStatus(
                        StatusChannel.EngineConfig,
                        $"当前使用{EngineWording.FreePublicTranslationName} — 可添加自己的翻译引擎",
                        StatusTone.Info);
                }
                else
                {
                    SetResidentStatus(
                        StatusChannel.EngineConfig,
                        "尚未配置翻译引擎 — 点击「添加翻译引擎」开始",
                        StatusTone.Warning);
                }
            }
            else
            {
                SetResidentStatus(StatusChannel.EngineConfig, "就绪", StatusTone.Success, deferToTransient: true);
            }
        }
        catch (Exception)
        {
            // Profile/shell stores unavailable: keep the current status text
            // rather than crash on window activation.
        }
    }

    /// <summary>
    /// Quiet picture of what actually runs right now, in the footer. The
    /// resident summary is exactly three things: the status dot, the short
    /// name of the current text engine, and the expand chevron. Privacy and
    /// health details live in tooltips and the expanded menu.
    /// </summary>
    internal void RefreshEngineStatus()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            // 以实际生效的引擎配置为准（快速切换器可选「内置免费引擎」，
            // 此时即使凭据库里有残留 key 也不算配置了模型服务）。
            var activeProfile = ProfileManager.Load().TryGetActiveProfile();
            var hasKey = activeProfile is not null &&
                CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(activeProfile));
            var consent = ShellSettingsStore.Load().FreeEngineConsent;
            var (_, tone) = DescribeEngine(settings, hasKey, consent);
            EngineSummary.Text = UsesFreeEngine(settings, hasKey, consent)
                ? EngineWording.ActiveFreeEngineName()
                : activeProfile is not null
                    ? activeProfile.Name
                    : DescribeEngine(settings, hasKey, consent).Summary;
            EngineDot.SetResourceReference(Border.BackgroundProperty, tone switch
            {
                StatusTone.Error => "DangerBrush",
                StatusTone.Warning => "WarningBrush",
                StatusTone.Info => "TextSecondaryBrush",
                _ => "SuccessBrush",
            });
        }
        catch (Exception)
        {
            // The credential vault (Win32Exception) and profile store can
            // both fail transiently; the footer must degrade quietly on
            // every alt-tab instead of crashing the window.
            EngineSummary.Text = "配置不可用";
        }
        // The style selector's capability (supported vs free-engine vs
        // unknown) follows the same engine picture as the footer; repaint it
        // on every engine-state refresh so it never claims the wrong route.
        TranslateSection.RefreshStyleSelector();
    }

    private static bool UsesFreeEngine(ProviderSettings settings, bool hasKey, FreeEngineConsent consent) =>
        !settings.SafeDevMode &&
        settings.NetworkEnabled &&
        !hasKey &&
        !settings.TargetsLocalRuntime &&
        consent != FreeEngineConsent.Denied;

    /// <summary>
    /// Probes the free engine and paints the result into the footer. Only a
    /// user-initiated click reaches here, and the click must pass the same
    /// outbound authorization and network gates as a translation; force only
    /// refreshes the health cache, it never bypasses the gates.
    /// </summary>
    private async Task UpdateFreeEngineHealthAsync(bool force)
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            if (!OutboundPolicy.AllowsFreeEngine(settings, out var gateDenial, out var authorization))
            {
                PaintFreeEngineNotProbed(gateDenial);
                return;
            }

            var paintsFooter = IsActiveRouteFreeEngine();
            if (force && paintsFooter)
            {
                EngineSummary.Text = $"{EngineWording.ActiveFreeEngineName()} · 检测中…";
            }
            var health = await FreeTranslateService.GetHealthAsync(force, authorization);
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                return; // window may be gone; RefreshEngineStatus will repaint next time
            }
            if (!paintsFooter)
            {
                return;
            }
            if (health.Ok)
            {
                EngineSummary.Text = $"{EngineWording.ActiveFreeEngineName()}可用 · {health.LatencyMs} ms";
                EngineDot.SetResourceReference(Border.BackgroundProperty, "SuccessBrush");
                EngineHealthButton.ToolTip = $"{EngineWording.ActiveFreeEngineName()}可用 · 点击重新检测";
            }
            else
            {
                EngineSummary.Text = $"{EngineWording.ActiveFreeEngineName()}不可用";
                EngineDot.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
                EngineHealthButton.ToolTip =
                    $"{EngineWording.ActiveFreeEngineName()}不可用：{health.Error} · 可在引擎菜单改选另一条免费引擎";
            }
        }
        catch (Exception)
        {
            // Probe failures already land in health.Error; never crash the footer.
        }
    }

    /// <summary>Footer state for “we have not verified reachability”.</summary>
    private void PaintFreeEngineNotProbed(TranslationError? denial)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            return; // window may be gone; RefreshEngineStatus will repaint next time
        }
        EngineSummary.Text = $"{EngineWording.ActiveFreeEngineName()}未检测";
        EngineDot.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
        EngineHealthButton.ToolTip = denial is null
            ? $"{EngineWording.ActiveFreeEngineName()}未检测 · 点击重新检测"
            : $"未检测：{denial.Message}";
    }

    private bool IsActiveRouteFreeEngine()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            var activeProfile = ProfileManager.Load().TryGetActiveProfile();
            var hasKey = activeProfile is not null &&
                CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(activeProfile));
            var consent = ShellSettingsStore.Load().FreeEngineConsent;
            return UsesFreeEngine(settings, hasKey, consent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void EngineHealthButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        // Opening the menu is a LOCAL read of cached state only. It must
        // never probe the network — reachability is refreshed exclusively
        // by the explicit「重新检测免费引擎」menu item.
        ShowEngineSwitchMenu(button);
    }

    /// <summary>
    /// 右下角快速切换器：点击即列出已配置的文字/图片引擎，选中立即生效，
    /// 不再需要绕进设置页。免费引擎是兜底线路，只展示状态不可选。
    /// 构建与弹出分离：构建只读本地缓存状态，弹出负责放置与打开。
    /// </summary>
    private void ShowEngineSwitchMenu(Button anchor)
    {
        if (_isSwitchingEngine)
        {
            SetStatus("正在切换引擎，请稍候…", StatusTone.Info);
            return;
        }

        ContextMenu menu;
        try
        {
            menu = BuildEngineSwitchMenu();
        }
        catch (Exception exception)
        {
            SetStatus($"无法加载引擎列表：{exception.Message}", StatusTone.Error);
            return;
        }

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementRectangle = new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Builds the switcher menu purely from local cached state. It performs
    /// NO network access — reachability strings come from the last cached
    /// health probe, and probing happens only via the explicit menu item.
    /// Internal so tests can assert the current-route marker is unique.
    /// </summary>
    internal ContextMenu BuildEngineSwitchMenu()
    {
        var config = ProfileManager.Load();
        var menu = new ContextMenu();
        var activeId = config.TryGetActiveProfile()?.Id;
        var currentVisionId = config.VisionProfileId;

        menu.Items.Add(MakeMenuHeader("文字引擎"));
        var textProfiles = config.Profiles.Where(p => p.SupportsText).ToList();
        if (textProfiles.Count == 0)
        {
            menu.Items.Add(MakeDisabledItem("尚未配置引擎"));
        }
        foreach (var profile in textProfiles)
        {
            var id = profile.Id;
            var isActive = profile.Id == activeId;
            var item = new MenuItem
            {
                Header = $"{profile.Name} · {profile.TextModel}",
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = isActive ? MakeActiveCheck() : null,
            };
            item.Click += (_, _) => SwitchTextEngine(id);
            menu.Items.Add(item);
        }

        // 两条公共文字线路，用户显式选一条。失败不会悄悄改发到另一条。
        // 探测结果按线路分开记；没探测过就显示「未检测」。
        var selectedFree = ShellSettingsStore.Load().FreeEngineProvider;
        foreach (var provider in new[] { FreeEngineProvider.Google, FreeEngineProvider.MyMemory })
        {
            var chosen = provider;
            var freeActive = config.PreferFreeEngine && selectedFree == chosen;
            var freeState = FreeHealthLabel(chosen);
            var item = new MenuItem
            {
                Header = $"{EngineWording.NameFor(chosen)} · {freeState} · 仅文字",
                FontWeight = freeActive ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = freeActive ? MakeActiveCheck() : null,
                ToolTip = chosen == FreeEngineProvider.MyMemory
                    ? "Google 线路不可用时改用这一条。文本发往 api.mymemory.translated.net，不发截图。"
                    : "文本发往 translate.googleapis.com，不发截图。",
            };
            item.Click += async (_, _) => await SwitchToFreeEngineAsync(chosen);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuHeader("图片引擎"));
        var followActive = string.IsNullOrEmpty(currentVisionId);
        var visionFollow = new MenuItem
        {
            Header = "跟随文字引擎",
            FontWeight = followActive ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = followActive ? MakeActiveCheck() : null,
        };
        visionFollow.Click += (_, _) => SwitchVisionEngine(null);
        menu.Items.Add(visionFollow);
        foreach (var profile in config.Profiles.Where(p =>
                     p.SupportsVision && !string.IsNullOrWhiteSpace(p.VisionModel)))
        {
            var id = profile.Id;
            var isActive = profile.Id == currentVisionId;
            var item = new MenuItem
            {
                Header = $"{profile.Name} · {profile.VisionModel}",
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = isActive ? MakeActiveCheck() : null,
            };
            item.Click += (_, _) => SwitchVisionEngine(id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "管理引擎…" };
        manage.Click += (_, _) => OpenSettings?.Invoke();
        menu.Items.Add(manage);
        var reprobe = new MenuItem { Header = $"重新检测{EngineWording.ActiveFreeEngineName()}" };
        reprobe.Click += async (_, _) => await UpdateFreeEngineHealthAsync(force: true);
        menu.Items.Add(reprobe);

        return menu;
    }

    private static TextBlock MakeActiveCheck()
    {
        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return check;
    }

    private static System.Windows.Controls.MenuItem MakeMenuHeader(string text)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = text,
            IsEnabled = true,
            Focusable = false,
            IsHitTestVisible = false,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11.5,
        };
        item.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "TextTertiaryBrush");
        return item;
    }

    private static System.Windows.Controls.MenuItem MakeDisabledItem(string text) => new()
    {
        Header = text,
        IsEnabled = false,
    };

    private async void SwitchTextEngine(string profileId)
    {
        if (_isSwitchingEngine) return;
        _isSwitchingEngine = true;
        try
        {
            SetStatus("正在切换文字引擎…", StatusTone.Info);
            // 持久化走后台：写盘（含杀软扫描）在 UI 线程上会造成窗口卡死。
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchActiveProfile(profileId, out var message);
                return (success, message);
            });
            SetStatus(ok ? "已切换文字引擎，即时生效。" : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            // async void: an escapee here would kill the process via the
            // dispatcher; the switcher must degrade to a status line.
            SetStatus($"切换文字引擎失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _isSwitchingEngine = false;
        }
    }

    private async void SwitchVisionEngine(string? profileId)
    {
        if (_isSwitchingEngine) return;
        _isSwitchingEngine = true;
        try
        {
            SetStatus("正在切换图片引擎…", StatusTone.Info);
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchVisionProfile(profileId, out var message);
                return (success, message);
            });
            SetStatus(
                ok
                    ? (profileId is null ? "图片引擎已改为跟随文字引擎。" : "已切换图片引擎，即时生效。")
                    : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            SetStatus($"切换图片引擎失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _isSwitchingEngine = false;
        }
    }

    private static string FreeHealthLabel(FreeEngineProvider provider)
    {
        if (!FreeTranslateService.HasHealthResultFor(provider))
        {
            return "未检测";
        }

        var health = FreeTranslateService.LastHealthFor(provider);
        return health.Ok ? $"可用 · {health.LatencyMs} ms" : "当前不可用";
    }

    private async Task SwitchToFreeEngineAsync(FreeEngineProvider provider)
    {
        if (_isSwitchingEngine) return;
        _isSwitchingEngine = true;
        var name = EngineWording.NameFor(provider);
        try
        {
            SetStatus($"正在切换到{name}…", StatusTone.Info);
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchToFreeEngine(provider, out var message);
                return (success, message);
            });
            SetStatus(
                ok ? $"已切换到{name}（仅文字翻译）。" : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            // Invoked from an async-void menu lambda: must not throw upward.
            SetStatus($"切换到免费引擎失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _isSwitchingEngine = false;
        }
    }

    private static (string Summary, StatusTone Tone) DescribeEngine(
        ProviderSettings settings, bool hasKey, FreeEngineConsent consent)
    {
        if (settings.SafeDevMode)
        {
            return (EngineWording.SafeOfflineModeName, StatusTone.Warning);
        }
        if (!settings.NetworkEnabled)
        {
            return ("网络翻译已关闭", StatusTone.Warning);
        }
        if (!hasKey && !settings.TargetsLocalRuntime)
        {
            return consent == FreeEngineConsent.Denied
                ? ($"{EngineWording.FreeEngineName}已关闭，且未配置翻译引擎", StatusTone.Warning)
                : (EngineWording.ActiveFreeEngineName(), StatusTone.Info);
        }
        return (string.IsNullOrWhiteSpace(settings.TextModel)
            ? "未填写文本模型"
            : settings.TextModel, StatusTone.Success);
    }


    // ================= Cross-section event handlers =================

    /// <summary>
    /// Library → workbench hand-off. History/vocabulary entries are finished
    /// translations, so the session state is always Completed: Markdown,
    /// empty state and result actions are decided by
    /// <see cref="TranslateSection.ApplyState"/>, never by direct control
    /// writes here (which also bypassed the language-change suspension and
    /// persisted the entry's pair as the global default).
    /// </summary>
    private void OnLoadToTranslate(
        string source, string translation, string? explanation,
        string? sourceLang, string? targetLang, string? badge)
    {
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
        TranslateSection.FocusTranslate(
            initialText: source,
            targetLang: targetLang,
            sourceLang: sourceLang,
            existingTranslation: translation,
            storedState: TranslationSessionState.Completed,
            explanation: explanation,
            badge: badge ?? "已载入");
        SetStatus("已载入记录。", StatusTone.Info);
    }

    // ================= Navigation =================

    /// <summary>
    /// WIN-12 & V2 §10 (G07): RootGrid's client width is the ONE authoritative
    /// responsive source. Breakpoints:
    /// - < 720 DIP: Fold sidebar to compact 48 DIP icon mode, vertically stack input and result panes.
    /// - 720–959 DIP: Standard workstation mode with 168 DIP sidebar and equal 1:1 dual panes.
    /// - >= 960 DIP: Enhanced wide dual-column workstation mode with expanded reading quota (1:1.25).
    /// The former ContentGrid second driver is gone: two reporters racing on
    /// the same state produced conflicting compact flags at window edges.
    /// </summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var clientWidth = e.NewSize.Width;
        ApplyResponsiveBreakpoints(clientWidth);
    }

    private void ApplyResponsiveBreakpoints(double clientWidth)
    {
        var foldSidebar = clientWidth < 720;
        SetSidebarCompact(foldSidebar);

        TranslateSection.SetStacked(clientWidth < 720);
        TranslateSection.SetCompact(clientWidth < 880);
        TranslateSection.SetWide(clientWidth >= 960);
    }

    private void SetSidebarCompact(bool compact)
    {
        if (SidebarColumn is null) return;
        SidebarColumn.Width = new GridLength(compact ? 48 : 168);
        if (SidebarGrid is not null)
        {
            SidebarGrid.Margin = compact ? new Thickness(4, 0, 4, 12) : new Thickness(10, 0, 10, 12);
        }
        if (BrandText is not null)
        {
            BrandText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
        if (NavGroupHeader is not null)
        {
            NavGroupHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
        if (NavTranslate is not null)
        {
            NavTranslate.Content = compact ? null : "翻译";
            NavTranslate.ToolTip = "翻译";
        }
        if (NavLibrary is not null)
        {
            NavLibrary.Content = compact ? null : "资料库";
            NavLibrary.ToolTip = "资料库";
        }
        if (NavSettingsButton is not null)
        {
            NavSettingsButton.Content = compact ? null : "设置";
            NavSettingsButton.ToolTip = "设置（引擎、快捷键、隐私）";
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ShowSection((sender as RadioButton)?.Tag as string);
    }

    private void ShowSection(string? tag)
    {
        TranslateSection.Visibility = Visibility.Collapsed;
        LibrarySection.Visibility = Visibility.Collapsed;

        if (tag == "Library")
        {
            LibrarySection.ReloadHistory();
            LibrarySection.ReloadVocabulary();
            LibrarySection.Visibility = Visibility.Visible;
            // 进入资料页且有数据时自动选中最新一条，详情栏立即有内容。
            LibrarySection.SelectLatestRowOrNothing();
        }
        else
        {
            TranslateSection.Visibility = Visibility.Visible;
        }
    }

    // ================= Public API for App.xaml.cs =================

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null,
        TranslationSessionState? storedState = null,
        string? explanation = null,
        string? badge = null)
    {
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
        TranslateSection.FocusTranslate(
            initialText, targetLang, sourceLang, existingTranslation,
            storedState, explanation, badge);
    }

    internal void ReloadHistory() => LibrarySection.ReloadHistory();

    /// <summary>
    /// 常驻 Hotkey 通道（Set）：全局快捷键真实失效时，状态行持续解释具体
    /// 原因；瞬时消息可以临时占用展示权，但到期后仍回到这条故障提示，直
    /// 到恢复后被 <see cref="ClearShortcutConflict"/> 清除。同一失效周期内
    /// 详情变化时重复调用只会刷新文案。
    /// </summary>
    internal void ShowShortcutConflict(string conflict) =>
        SetResidentStatus(
            StatusChannel.Hotkey,
            $"快捷键注册失败 — {conflict}。可在「设置 → 快捷键」更换组合。",
            StatusTone.Error);

    /// <summary>
    /// 常驻 Hotkey 通道（Clear）：快捷键恢复后清除故障提示，状态行立即回
    /// 落到 EngineConfig/Ready，且不影响其他通道的优先级。
    /// </summary>
    internal void ClearShortcutConflict() => ClearResidentStatus(StatusChannel.Hotkey);

    // ================= First-run onboarding =================
    //
    // 非阻断最小闭环：主窗口首次加载时（且从未完成引导、尚未配置任何
    // 引擎）在工作台内展开三步引导条；跳过/完成即持久化
    // HasCompletedOnboarding。「设置 → 通用 → 重新显示引导」通过
    // RestartOnboarding 复位并重新展开。绝不使用模态弹窗。

    private void TryBeginFirstRunOnboarding()
    {
        try
        {
            var settings = ShellSettingsStore.Load();
            if (settings.HasCompletedOnboarding)
            {
                return;
            }
            if (ProfileManager.HasConfiguredUserEngine())
            {
                // 引擎已在位说明上手早已完成：顺带把丢失/回退的闸门补成
                // true，而不是给一个能用的安装再弹引导。
                PersistOnboardingComplete(settings);
                return;
            }
            TranslateSection.BeginOnboarding();
        }
        catch (Exception)
        {
            // 引导是增值体验；任何读取失败都不能影响窗口加载。
        }
    }

    private void CompleteOnboarding()
    {
        TranslateSection.EndOnboarding();
        try
        {
            PersistOnboardingComplete(ShellSettingsStore.Load());
        }
        catch (Exception)
        {
            // Best-effort persistence; the guide simply may reappear once.
        }
    }

    private static void PersistOnboardingComplete(ShellSettings settings)
    {
        try
        {
            ShellSettingsStore.Save(settings with { HasCompletedOnboarding = true });
        }
        catch (Exception)
        {
            // 引导状态保存失败不打断任何主流程。
        }
    }

    /// <summary>设置 → 通用 的「重新显示引导」入口：复位闸门并回到工作台
    /// 重新展开引导条（本会话立即生效，重新持久化为未完成）。</summary>
    internal void RestartOnboarding()
    {
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
        TranslateSection.BeginOnboarding();
        SetStatus("已重新显示新手引导", StatusTone.Info);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            var closeToTray = ShellSettingsStore.Load().CloseMainWindowToTray;
            if (!closeToTray)
            {
                RequestExit?.Invoke();
                return;
            }

            e.Cancel = true;
            Hide();
            // 主窗口驻留托盘后释放工作集：隐藏状态下不需要保住渲染页，
            // 任务管理器中的内存随之回落，重新显示时自动调回。
            App.TrimWorkingSet();
            // The "still running" balloon shows at most once per install:
            // in-memory for this run, persisted so later runs never nag.
            if (!_closeHintShown && !ShellSettingsStore.Load().CloseHintShown)
            {
                _closeHintShown = true;
                NotifyTray?.Invoke(
                    "PopGlot 还在运行",
                    "已最小化到托盘；划词与截图翻译仍可用，托盘右键退出。");
                try
                {
                    var settings = ShellSettingsStore.Load();
                    ShellSettingsStore.Save(settings with { CloseHintShown = true });
                }
                catch (Exception)
                {
                    // Best-effort persistence; the in-memory flag still gates this run.
                }
            }
        }
        base.OnClosing(e);
    }
}

/// <summary>
/// The ONE stable Normal-state DIP size of the main window (E3-F12/F14), pure
/// logic so the generation/flag rules are unit-testable without a second
/// monitor: a WM_DPICHANGED transition opens; only a user resize in Normal
/// state outside any transition may rewrite the stable value; closing the
/// transition happens when the idle restore actually applied. Maximized and
/// minimized phases are never observations.
/// </summary>
internal sealed class StableNormalSizeTracker(Size initialSize)
{
    public Size StableSize { get; private set; } = initialSize;

    public bool InDpiTransition { get; private set; }

    public void BeginDpiTransition() => InDpiTransition = true;

    /// <summary>
    /// SizeChanged observation. Returns false (stable size untouched) when the
    /// resize happened inside a DPI transition — WPF's OS-suggested-rect resize
    /// and the restore's own writes must never be mistaken for user intent —
    /// or when the window is not in the Normal state (maximize/minimize).
    /// </summary>
    public bool TryObserveResize(Size newSize, bool isNormalState)
    {
        if (InDpiTransition || !isNormalState ||
            !double.IsFinite(newSize.Width) || !double.IsFinite(newSize.Height) ||
            newSize.Width <= 0 || newSize.Height <= 0)
        {
            return false;
        }
        StableSize = newSize;
        return true;
    }

    /// <summary>
    /// Called from the ApplicationIdle restore: closes the transition and
    /// yields the stable DIP size to apply. Restore writes fired while the
    /// transition was still open are therefore not observations; after this
    /// call user resizes update the stable size again.
    /// </summary>
    public Size EndDpiTransition()
    {
        InDpiTransition = false;
        return StableSize;
    }
}

/// <summary>
/// What the NEXT text translation's route means for prompt personalization.
/// </summary>
internal enum TranslationStyleSupport
{
    /// <summary>A configured provider or local model runs next; the active prompt template applies.</summary>
    Supported,

    /// <summary>
    /// The built-in free engine runs next. It has no personalization of its
    /// own and the source text is never augmented client-side — the selector
    /// must be disabled with an honest explanation instead.
    /// </summary>
    FreeEngine,

    /// <summary>Local state could not be read; keep the selector visible but stay honest.</summary>
    Unknown,
}

/// <summary>
/// The shared global 文字翻译风格 (TEXT-translation style) selector (prompt
/// templates) behind the main workbench, the floating panel and quick search.
/// Lists the built-in 忠实/自然/正式 styles plus the custom list straight from
/// the core's prompt store, switches the active template via CoreBridge, and
/// offers the shared 管理提示词 entry (routed through each surface's existing
/// open-settings callback). By contract a switch only persists: the NEXT
/// request picks it up (the coordinator snapshots the template once per
/// request), nothing in flight is cancelled and nothing is re-translated.
/// Source text is never spliced with style instructions anywhere in this shell.
/// Scope honesty: the template applies to TEXT requests only. 0.1.6 has no
/// vision-prompt support, so a vision-direct screenshot translation never
/// receives it — those sessions say so before and after the run instead of
/// silently claiming support. Screenshot pipelines that OCR first and
/// translate through the text provider honour the template as usual.
/// </summary>
internal static class TranslationStyleMenu
{
    internal const string FaithfulTemplateId = "faithful";
    internal const string NaturalTemplateId = "natural";
    internal const string FormalTemplateId = "formal";

    /// <summary>
    /// Selector wording while the NEXT text translation is proven to honour
    /// the active style: names the feature 文字翻译风格 and states the one
    /// exception (vision-direct screenshots) so it never over-claims. Single
    /// source: <see cref="ApplyTo"/> always uses this constant, surfaces no
    /// longer pass their own copy.
    /// </summary>
    internal const string SupportedToolTip = "文字翻译风格（下一次文字翻译生效）";

    /// <summary>The exact wording required when the free engine cannot personalize.</summary>
    internal const string FreeEngineToolTip = "此引擎不支持文字翻译风格个性化";

    internal const string UnknownToolTip =
        "暂时无法确认当前引擎是否支持文字翻译风格；若由内置免费引擎完成，所选风格不会应用";

    /// <summary>
    /// 0.1.6 文案减法：免费引擎的事前/事后提示统一为这一行诚实短状态，
    /// 三个表面（工作台/浮窗/极速查词）共用，不再各写一句长解释。
    /// </summary>
    internal const string FreeEngineStyleNotAppliedStatus = "内置免费引擎不支持所选风格，本次未应用";

    /// <summary>
    /// 0.1.6 文案减法：视觉直译的事前/事后提示统一为这一行诚实短状态，
    /// 三个表面共用；只陈述事实，不附带长解释。
    /// </summary>
    internal const string VisionDirectStyleNotAppliedStatus = "截图由视觉模型直译，所选风格未应用";

    /// <summary>
    /// Compact label for the selector and menu items: short names for the
    /// three built-ins, the template's own name for the custom list.
    /// </summary>
    internal static string ShortLabel(PromptTemplateDto template) => template.Id switch
    {
        FaithfulTemplateId => "忠实",
        NaturalTemplateId => "自然",
        FormalTemplateId => "正式",
        _ => string.IsNullOrWhiteSpace(template.Name) ? template.Id : template.Name,
    };

    /// <summary>The active template as the core resolves it (falls back to faithful); null when unreadable.</summary>
    internal static PromptTemplateDto? TryGetActiveTemplate()
    {
        try
        {
            return CoreBridge.GetActivePromptTemplate();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Read-only local probe of the route the next text translation takes,
    /// sharing the coordinator's own provider decision (including the legacy
    /// global TargetsLocalRuntime fallback) via
    /// <see cref="TranslationCoordinator.ResolveTextProviderCapability"/> so
    /// the two can never drift: a legacy local runtime must keep the selector
    /// enabled instead of being mistaken for the free engine. Zero network;
    /// Unknown means local state could not be read this instant.
    /// </summary>
    internal static TranslationStyleSupport ProbeNextTextRoute()
    {
        try
        {
            // 与 coordinator 真实请求完全相同的解析链：ProfileManager.ResolveRoutes
            // → 按 route 的凭据目标读 key → 共享谓词判定。
            var settings = CoreBridge.GetSettings();
            var (textRoute, _) = ProfileManager.ResolveRoutes();
            var textApiKey = textRoute is null
                ? null
                : CredentialStore.LoadApiKey(textRoute.CredentialTarget);
            var textRuntimeSettings = textRoute?.Profile.ToProviderSettings(settings);
            return TranslationCoordinator.ResolveTextProviderCapability(textRuntimeSettings, textApiKey, settings)
                .HasConfiguredProvider
                ? TranslationStyleSupport.Supported
                : TranslationStyleSupport.FreeEngine;
        }
        catch (Exception)
        {
            return TranslationStyleSupport.Unknown;
        }
    }

    /// <summary>
    /// Read-only local probe of the route the NEXT screenshot translation
    /// resolves to, through the very same decision the coordinator executes
    /// (<see cref="ProfileManager.ResolveRoute"/> → Rust SelectRoute): zero
    /// network, no request is started. Unavailable (route unreadable or no
    /// pipeline) means the caller stays silent rather than over-claiming.
    /// </summary>
    internal static ScreenshotPipeline ProbeNextScreenshotPipeline()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            return ProfileManager.ResolveRoute(settings, WindowsOcrService.IsSupported).ScreenshotPipeline;
        }
        catch (Exception)
        {
            return ScreenshotPipeline.Unavailable;
        }
    }

    /// <summary>
    /// Honest pre-flight notice for the vision-direct screenshot route, where
    /// the active TEXT style cannot take part. Null — and therefore no claim
    /// at all — when the resolved pipeline is not vision-direct (OCR-based
    /// pipelines translate through the text provider and honour the style),
    /// or when the faithful default is active so nothing is at stake. The
    /// notice never blocks, reroutes or re-sends anything.
    /// </summary>
    internal static string? PreScreenshotNotice()
    {
        if (ProbeNextScreenshotPipeline() != ScreenshotPipeline.VisionDirect)
        {
            return null;
        }
        if (TryGetActiveTemplate() is not { } template ||
            template.Id == FaithfulTemplateId)
        {
            return null;
        }
        return VisionDirectStyleNotAppliedStatus;
    }

    /// <summary>
    /// True when the session's pipeline label says the vision model itself
    /// produced the translation (vision-direct). The labels must stay in sync
    /// with TranslationCoordinator's screenshot pipeline; every other label
    /// (本地 OCR, 视觉识别 + 文本模型 …) went through the text provider, where
    /// the style applies normally.
    /// </summary>
    internal static bool IsVisionDirectPipeline(string? pipelineLabel) =>
        pipelineLabel is "本地视觉模型" or "视觉模型 · 独立服务";

    /// <summary>
    /// Builds the shared menu purely from local prompt-store state — no
    /// network, no translation request. Built-in styles first, the custom
    /// list after, then the manage entry. Returns null (status already
    /// reported) when the store is unreadable.
    /// </summary>
    internal static ContextMenu? Build(
        Action<PromptTemplateDto> styleChosen,
        Action<string> reportStatus,
        Action managePrompts)
    {
        IReadOnlyList<PromptTemplateDto> templates;
        string activeId;
        try
        {
            templates = CoreBridge.ListPromptTemplates();
            activeId = CoreBridge.GetActivePromptTemplate().Id;
        }
        catch (Exception exception)
        {
            reportStatus($"无法读取翻译风格列表：{exception.Message}");
            return null;
        }

        var menu = new ContextMenu();
        menu.Items.Add(MakeHeader("文字翻译风格"));
        var builtInCount = 0;
        foreach (var template in templates.Where(t => t.Enabled && t.IsBuiltIn))
        {
            menu.Items.Add(MakeStyleItem(template, activeId, styleChosen));
            builtInCount++;
        }

        var custom = templates.Where(t => t.Enabled && !t.IsBuiltIn).ToList();
        if (custom.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeHeader("自定义"));
            foreach (var template in custom)
            {
                menu.Items.Add(MakeStyleItem(template, activeId, styleChosen));
            }
        }

        if (builtInCount == 0 && custom.Count == 0)
        {
            menu.Items.Add(MakeDisabledItem("暂无可用风格"));
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "管理提示词…" };
        manage.Click += (_, _) => managePrompts();
        menu.Items.Add(manage);
        return menu;
    }

    /// <summary>Anchors the shared menu to a surface's selector button and opens it.</summary>
    internal static void Show(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementRectangle = new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Persists a style switch off the UI thread and reports the outcome.
    /// Returns false on failure so the caller can repaint the authoritative
    /// state. Only the next request is affected, by contract.
    /// </summary>
    internal static async Task<bool> SwitchActiveTemplateAsync(
        string? templateId,
        Action<string> reportStatus)
    {
        try
        {
            await CoreBridge.SetActivePromptTemplateAsync(templateId);
            ReportOnUi(reportStatus, "已切换文字翻译风格，下一次文字翻译时生效。");
            return true;
        }
        catch (Exception exception)
        {
            ReportOnUi(reportStatus, $"切换文字翻译风格失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Honest notice for the moment a translation is triggered. Null when the
    /// route is proven to honour the active template, or when the default
    /// faithful style is active so nothing is at stake. FreeEngine states the
    /// shared one-line short status; Unknown says exactly what cannot be
    /// promised. The notice never blocks the translation and never cancels
    /// anything.
    /// </summary>
    internal static string? PreTranslateNotice()
    {
        var probe = ProbeNextTextRoute();
        if (probe == TranslationStyleSupport.Supported)
        {
            return null;
        }
        if (TryGetActiveTemplate() is not { } template ||
            template.Id == FaithfulTemplateId)
        {
            return null;
        }
        return probe == TranslationStyleSupport.FreeEngine
            ? FreeEngineStyleNotAppliedStatus
            : $"{UnknownToolTip}。";
    }

    /// <summary>
    /// Pure typed mapping from a session's <see cref="TranslationPromptSupport"/>
    /// fact to the shared short status line — the display counterpart of the
    /// coordinator's own route marking. Empty string means there is nothing to
    /// claim. Callers must branch on the typed support (executor/pipeline
    /// kind), never on display strings, and use this only to render.
    /// </summary>
    internal static string StyleStatusFor(TranslationPromptSupport support) => support switch
    {
        TranslationPromptSupport.NotSupported => FreeEngineStyleNotAppliedStatus,
        TranslationPromptSupport.NotApplicable => VisionDirectStyleNotAppliedStatus,
        TranslationPromptSupport.Unknown => $"{UnknownToolTip}。",
        // Applied: the style took part — no disclaimer. Pending: no text stage
        // resolved yet — no claim either way.
        _ => string.Empty,
    };

    /// <summary>
    /// Applies the probed capability to a surface's selector button: grayed
    /// with the exact honest wording on the free engine, visible with an
    /// honest caveat when unreadable, normal otherwise. The tooltip comes
    /// from the single <see cref="SupportedToolTip"/> source — surfaces can
    /// no longer pass drifting copies. The tooltip is also mirrored into
    /// automation HelpText so screen readers get the same truth; buttons set
    /// ToolTipService.ShowOnDisabled so the grayed state still explains
    /// itself. Returns the probe.
    /// </summary>
    internal static TranslationStyleSupport ApplyTo(Button selectorButton)
    {
        var probe = ProbeNextTextRoute();
        var message = probe switch
        {
            TranslationStyleSupport.FreeEngine => FreeEngineToolTip,
            TranslationStyleSupport.Unknown => UnknownToolTip,
            _ => SupportedToolTip,
        };
        selectorButton.IsEnabled = probe != TranslationStyleSupport.FreeEngine;
        selectorButton.ToolTip = message;
        System.Windows.Automation.AutomationProperties.SetHelpText(selectorButton, message);
        return probe;
    }

    /// <summary>Active-style short label for a selector, with a neutral fallback.</summary>
    internal static string ActiveLabel()
    {
        var active = TryGetActiveTemplate();
        return active is null ? "风格" : ShortLabel(active);
    }

    private static MenuItem MakeStyleItem(
        PromptTemplateDto template,
        string activeId,
        Action<PromptTemplateDto> styleChosen)
    {
        var id = template.Id;
        var isActive = string.Equals(id, activeId, StringComparison.Ordinal);
        var item = new MenuItem
        {
            Header = ShortLabel(template),
            ToolTip = string.IsNullOrWhiteSpace(template.Description) ? null : template.Description,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = isActive ? MakeActiveCheck() : null,
        };
        item.Click += (_, _) =>
        {
            if (!isActive)
            {
                styleChosen(template);
            }
        };
        return item;
    }

    private static TextBlock MakeActiveCheck()
    {
        var check = new TextBlock
        {
            Text = "✓",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
        };
        check.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return check;
    }

    private static MenuItem MakeHeader(string text)
    {
        var item = new MenuItem
        {
            Header = text,
            IsEnabled = true,
            Focusable = false,
            IsHitTestVisible = false,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 4),
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextTertiaryBrush");
        return item;
    }

    private static MenuItem MakeDisabledItem(string text) => new()
    {
        Header = text,
        IsEnabled = false,
    };

    /// <summary>Status reports land on the caller's UI thread from any context.</summary>
    private static void ReportOnUi(Action<string> reportStatus, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => reportStatus(message));
        }
        else
        {
            reportStatus(message);
        }
    }
}
