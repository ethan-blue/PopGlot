using System.Drawing.Drawing2D;

using System.Runtime.InteropServices;

using System.Windows;

using PopGlot.Windows.Services;



namespace PopGlot.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\PopGlot.SingleInstance";
    private const string ShowWindowSignalName = @"Local\PopGlot.ShowWindow";

    private readonly HistoryStore _history = new();

    private readonly VocabularyStore _vocabulary = new();

    private readonly ClipboardSelectionService _selectionService =

        new(new WindowsSelectionClipboardAdapter());



    private Mutex? _instanceMutex;

    private EventWaitHandle? _showSignal;

    private CancellationTokenSource? _signalListener;

    private Forms.NotifyIcon? _trayIcon;

    private Forms.ContextMenuStrip? _trayMenu;

    private Drawing.Icon? _trayIconImage;

    private HotkeyService? _hotkeys;

    private Window? _hotkeyOwner;

    private MainWindow? _mainWindow;

    /// <summary>
    /// Dedup state for hotkey-failure surfacing: one failure cycle (first
    /// failure until recovery) balloons at most once, a changed detail only
    /// refreshes the status surfaces, and recovery re-arms notification.
    /// </summary>
    private readonly HotkeyFailureCoordinator _hotkeyFailure = new();

    /// <summary>
    /// Set by the RegistrationFailed handler for the duration of the CURRENT
    /// registration attempt, so <see cref="TryApplyShellSettings"/> can tell
    /// a service-reported failure (the event already carried the honest
    /// combined detail) from one only the App can report. UI-thread only:
    /// two attempts can never interleave.
    /// </summary>
    private bool _hotkeyFailureReportedThisAttempt;

    private SettingsWindow? _settingsWindow;

    private TranslationPanelWindow? _activePanel;

    private CaptureOverlayWindow? _activeOverlay;

    private QuickSearchWindow? _activeQuickSearch;

    internal static SessionStore SharedSessionStore { get; } = new();

    /// <summary>
    /// N01: a session-store rejection that no visible surface can report
    /// (the window is already closing or hidden) is recorded in the bounded
    /// diagnostics log. C03 keeps entries structured — the reason travels
    /// only as the exception message/context, never as free-form log text.
    /// </summary>
    internal static void LogSessionStoreRejection(string? rejectionReason) =>
        DiagnosticsLog.Log(
            new InvalidOperationException(
                $"SessionStore.TryStore rejected snapshot: {rejectionReason ?? "unspecified"}"),
            DiagnosticsLog.DiagnosticsStage.Translation);

    private ShellSettings _shellSettings = ShellSettings.Default;

    private static (string markerPath, string dataDir)? ReadSmokeMarkerPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--smoke-startup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var markerPath = args[i + 1];
            var dataDir = i + 2 < args.Length && !args[i + 2].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 2]
                : Path.Combine(Path.GetTempPath(), $"popglot-smoke-{Guid.NewGuid():N}");
            return (markerPath, dataDir);
        }
        return null;
    }

    /// <summary>
    /// Executes the real startup sequence against an isolated data directory
    /// and records when the tray became usable. Deliberately runs the same
    /// steps as a normal launch — this is a measurement mode, not a shortcut.
    /// </summary>
    private void RunStartupSmoke(string markerPath, string dataDir)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? failure = null;
        var hotkeysRegistered = false;
        try
        {
            // C09: the smoke run is a measurement instance — the data root
            // override alone is not isolation, so credentials also stay in
            // memory and the registry self-heal never runs here.
            StoragePaths.RootOverride = dataDir;
            CredentialStore.OverrideVault = new Services.MemoryCredentialVault();
            _shellSettings = ShellSettingsStore.Load();
            ThemeService.Apply(_shellSettings.Theme);
            CoreBridge.Initialize();
            TtsService.CleanupStaleTempFiles();
            _hotkeyOwner = CreateHotkeyOwnerWindow();
            _hotkeys = new HotkeyService(_hotkeyOwner);
            _hotkeys.Pressed += (_, action) => HandleHotkey(action);
            // C09: production readiness includes the hotkeys ACTUALLY being
            // registered — creating the service object is not enough. The
            // marker records the real registration outcome.
            hotkeysRegistered = _hotkeys.TryRegisterAll(_shellSettings.Hotkeys, out var hotkeyConflict);
            CreateTrayIcon();
            if (!hotkeysRegistered)
            {
                failure = $"hotkey registration failed: {hotkeyConflict ?? "unknown conflict"}";
            }
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }
        stopwatch.Stop();

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                trayAvailableMs = stopwatch.ElapsedMilliseconds,
                hotkeysRegistered,
                failure,
                startedUtc = DateTimeOffset.UtcNow,
                version = typeof(App).Assembly.GetName().Version?.ToString(),
            });
            File.WriteAllText(markerPath, payload);
        }
        catch
        {
            // A marker write failure must not keep the process alive.
        }
        Shutdown(failure is null ? 0 : 1);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- Performance smoke mode (T15): `--smoke-startup <marker> [dataDir]` ----
        // Runs the REAL startup steps — settings, native core, hotkeys, tray —
        // against an explicitly given data directory, records the elapsed
        // milliseconds to the marker file, and exits. It never touches the
        // user's real configuration; normal launches never see this path.
        var smoke = ReadSmokeMarkerPath(e.Args);
        if (smoke is not null)
        {
            RunStartupSmoke(smoke.Value.markerPath, smoke.Value.dataDir);
            return;
        }

        // C07: a sign-in auto-start passes --background. It behaves like a
        // normal launch (tray + hotkeys, no main window) but never steals
        // focus with dialogs — failures land in diagnostics for the next
        // launch to surface.
        var backgroundStart = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);

        // C09 measurement fixture: a harness can pin the entire data root so
        // memory/CPU sampling never touches the real user profile. A data
        // root alone is NOT full isolation: credentials stay in memory and
        // the registry self-heal is skipped under the override. Normal
        // launches never set POPGLOT_DATA_ROOT and are unaffected.
        var dataRootOverride = Environment.GetEnvironmentVariable("POPGLOT_DATA_ROOT");
        var measurementIsolation = !string.IsNullOrEmpty(dataRootOverride);
        if (measurementIsolation)
        {
            StoragePaths.RootOverride = dataRootOverride;
            CredentialStore.OverrideVault = new Services.MemoryCredentialVault();
        }

        // C06 crash barriers: classified, recoverable exceptions are logged
        // and throttled so a transient Win32/IO failure cannot take the tray
        // app down. Unknown exceptions or an exception storm trip a fuse:
        // hotkeys stop, new work is cancelled and the user gets an explicit
        // restart/exit choice instead of a silently broken app.
        DispatcherUnhandledException += (_, args) =>
        {
            var eventId = DiagnosticsLog.Log(args.Exception);
            try
            {
                CoreBridge.CancelActiveRequest();
            }
            catch
            {
            }
            args.Handled = true;
            if (_degraded || !IsRecoverableException(args.Exception) ||
                RecordHandledExceptionAndCheckStorm())
            {
                EnterDegradedMode(eventId);
                return;
            }
            TryNotifyCrash(eventId);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            DiagnosticsLog.Log(args.Exception);
        };

        if (!ClaimSingleInstance())
        {
            // Another copy owns the hotkeys; hand the request over and leave
            // quietly instead of failing to register and looking broken.
            SignalExistingInstance();
            Shutdown();
            return;
        }

        try
        {
            _shellSettings = ShellSettingsStore.Load();
            ThemeService.Apply(_shellSettings.Theme);
            CoreBridge.Initialize();
            // C01: the send boundary re-reads the live core settings before
            // every real HTTP send, so a revoked consent or a flipped
            // offline/safe-mode switch stops even in-flight fallback sends.
            OutboundPolicy.LiveSettingsLoader = CoreBridge.GetSettings;
            TtsService.CleanupStaleTempFiles();
            DiagnosticsLog.CleanupIfStale(StoragePaths.Logs, force: true);
            AnnounceStartupNotice();

            // C07 self-heal: when auto-start is enabled, repair a stale Run
            // path or a missing entry. An OS-level (Task Manager) disable is
            // never overridden here — settings surfaces it to the user.
            if (_shellSettings.StartWithWindows && !measurementIsolation)
            {
                // C09: a measurement instance never touches the real Run
                // entry — the registry boundary is verified per resource.
                StartupRegistration.EnsureRegistered();
            }
            // Free-engine first-use authorization lives in
            // 「设置 → 隐私与数据」— no runtime popup interrupts translation.
            // OutboundPolicy fails closed until the user allows it there.

            // Startup only creates the tray, theme, and a hidden message
            // window for the hotkeys; the heavy MainWindow is built on first
            // use so cold-start reaches a usable tray as fast as possible.
            _hotkeyOwner = CreateHotkeyOwnerWindow();
            _hotkeys = new HotkeyService(_hotkeyOwner);
            _hotkeys.Pressed += (_, action) => HandleHotkey(action);
            _hotkeys.RegistrationFailed += (_, conflict) =>
            {
                // Mark the attempt BEFORE surfacing: TryApplyShellSettings
                // continues synchronously below this event on the same
                // thread and must not re-report what the event already
                // carried (a shorter re-report would only downgrade the
                // combined detail on the status surfaces).
                _hotkeyFailureReportedThisAttempt = true;
                OnHotkeyFailure(conflict ?? "未知快捷键");
            };
            _hotkeys.RegistrationRestored += (_, _) => OnHotkeyRestored();

            CreateTrayIcon();
            // A02: the failure marker is consumed only after the tray exists,
            // so the balloon can actually be shown before the marker is gone.
            AnnounceBackgroundStartupFailureIfAny();
            StartShowWindowListener();

            var shellOutcome = TryApplyShellSettings(_shellSettings);
            if (!shellOutcome.Applied)
            {
                // Hotkey failures were already surfaced inside
                // TryApplyShellSettings through the failure coordinator —
                // with the specific conflict detail and the once-per-cycle
                // dedup. Only a failure shape that never reached the
                // coordinator (the persisted set itself being invalid) is
                // reported here; the old unconditional generic balloon would
                // have doubled every startup conflict.
                if (shellOutcome.Failure == ShellApplyFailureKind.InvalidHotkeys)
                {
                    OnHotkeyFailure(shellOutcome.ConflictDetail ?? "快捷键配置无效");
                }
                if (!backgroundStart)
                {
                    ShowMainWindow();
                }
            }

            // V05: the restart parent waits on THIS attempt's unique event;
            // it is signalled only after the hotkeys actually registered and
            // the tray/listener are live — the real production boundary.
            var readyEventName = Environment.GetEnvironmentVariable("POPGLOT_READY_EVENT");
            if (shellOutcome.Applied && !string.IsNullOrEmpty(readyEventName))
            {
                try
                {
                    // V05: the PARENT pre-created and holds this event; the
                    // child only OPENS it and sets the signal. Signalling
                    // here happens after the hotkeys registered and the
                    // tray/listener are live - the production-ready boundary.
                    EventWaitHandle.OpenExisting(readyEventName).Set();
                }
                catch
                {
                }
            }

            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                ShowSettings();
            }

        }
        catch (Exception exception)
        {
            if (backgroundStart)
            {
                // C07: a login auto-start must not block the session with a
                // modal dialog. Write the failure where the next launch
                // surfaces it, and keep a structured crash entry.
                DiagnosticsLog.Log(exception, DiagnosticsLog.DiagnosticsStage.Startup);
                try
                {
                    Directory.CreateDirectory(StoragePaths.Logs);
                    File.WriteAllText(
                        Path.Combine(StoragePaths.Logs, "startup-failure.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 后台启动失败：{exception.GetType().Name}（详见 crash 日志）");
                }
                catch
                {
                }
                Shutdown(1);
                return;
            }
            MessageBox.Show(
                $"PopGlot 启动失败：{exception.Message}",
                "PopGlot",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// C07: a failed --background start leaves a marker; the first successful
    /// launch announces it as a balloon and clears it.
    /// </summary>
    private void AnnounceBackgroundStartupFailureIfAny()
    {
        try
        {
            var marker = Path.Combine(StoragePaths.Logs, "startup-failure.txt");
            if (!File.Exists(marker))
            {
                return;
            }
            File.Delete(marker);
            Notify(
                "上次后台启动失败",
                "登录自启上次未完成，诊断已写入日志目录；本次启动正常。",
                Forms.ToolTipIcon.Warning);
        }
        catch
        {
        }
    }

    /// <summary>
    /// C06 classification: only these expected, recoverable failure shapes
    /// may be swallowed by the global barrier. Anything else (null refs,
    /// access violations, unknown custom throws) means the app state cannot
    /// be trusted and must degrade to the restart/exit path.
    /// </summary>
    internal static bool IsRecoverableException(Exception exception) => exception switch
    {
        IOException or UnauthorizedAccessException or System.Security.SecurityException => true,
        TimeoutException => true,
        System.ComponentModel.Win32Exception => true,
        System.Text.Json.JsonException => true,
        OperationCanceledException or TaskCanceledException => true,
        // A10: a generic InvalidOperationException is NOT classified — our
        // fail-closed errors are converted to results at their own boundaries;
        // one reaching the dispatcher means an unknown state and fuses.
        _ => false,
    };

    // Crash-notify throttling: an exception storm (e.g. a layout bug firing on
    // every dispatcher frame) must not turn the tray into a balloon machine that
    // livelocks the UI. Log everything to disk; balloon at most once per window,
    // and trip the degraded-mode fuse after five exceptions in one window.
    private DateTime _crashNotifyWindowUtc = DateTime.UtcNow;
    private int _crashNotificationsInWindow;
    private int _crashSuppressedCount;
    private bool _crashNotificationShown;
    private DateTime _exceptionWindowUtc = DateTime.UtcNow;
    private int _handledExceptionCount;
    private bool _degraded;
    private bool _exitInProgress;
    private bool _storesFlushedForExit;

    private bool RecordHandledExceptionAndCheckStorm()
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _exceptionWindowUtc > TimeSpan.FromSeconds(15))
        {
            _exceptionWindowUtc = nowUtc;
            _handledExceptionCount = 0;
        }
        _handledExceptionCount++;
        return _handledExceptionCount >= 5;
    }

    /// <summary>
    /// C06 degraded mode: the unknown/storm fuse. Global hotkeys stop, the
    /// active request is cancelled, and the user chooses between an
    /// immediate restart and a clean exit — the app never silently keeps
    /// running in a state it cannot trust.
    /// </summary>
    private void EnterDegradedMode(string eventId)
    {
        if (_degraded)
        {
            return;
        }
        _degraded = true;
        // A10: every request entry point refuses new work from now on.
        RuntimeGate.NewWorkAllowed = false;
        try
        {
            _hotkeys?.SetSuspended(true);
        }
        catch
        {
        }
        try
        {
            CoreBridge.CancelActiveRequest();
        }
        catch
        {
        }
        MessageBoxResult choice;
        try
        {
            choice = MessageBox.Show(
                $"PopGlot 遇到无法自动恢复的问题，已暂停快捷键与新任务。（事件 {eventId}）\n\n要立即重启 PopGlot 吗？选择「否」将直接退出。",
                "PopGlot",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
        }
        catch
        {
            choice = MessageBoxResult.No;
        }
        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
            else
            {
                ExitApplication();
            }
        }
        catch
        {
            Shutdown(1);
        }
    }

    /// <summary>
    /// V05 restart entry: an observable, bounded, duplicate-guarded handover.
    /// A failed launch is reported to the user with a retry decision and a
    /// safe exit — the old app never silently disappears after swallowing a
    /// failed spawn.
    /// PERF-IO-06: executes handover in background task to prevent UI freeze and "Not Responding" dialogs.
    /// </summary>
    private async void RestartApplication()
    {
        if (!RestartHandover.TryBegin())
        {
            // A handover is already in flight; a duplicate request must not
            // spawn a second new process or double-close the old instance.
            return;
        }
        try
        {
            var result = await Task.Run(() => RestartHandover.Run(
                maxAttempts: 3,
                cleanupOldInstance: () =>
                {
                    var cleaned = false;
                    Dispatcher.Invoke(() => cleaned = CleanupForHandover());
                    return cleaned;
                },
                releaseMutex: () =>
                {
                    try
                    {
                        _instanceMutex?.Dispose();
                        _instanceMutex = null;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                },
                launchNew: () => LaunchReplacementWithReadiness(),
                askRetryAfterFailure: attempt =>
                {
                    var retry = false;
                    Dispatcher.Invoke(() =>
                    {
                        retry = MessageBox.Show(
                            $"第 {attempt} 次重启失败：新进程未能启动（5 秒内无确认）。要再试一次吗？\n\n选择「否」将安全退出 PopGlot；故障保护保持，新任务已暂停。",
                            "PopGlot 重启",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Error) == MessageBoxResult.Yes;
                    });
                    return retry;
                }));
            if (!result.Launched)
            {
                // Observable failure: re-claim the mutex so the exiting old
                // instance keeps single-instance protection, tell the user
                // the real outcome, then take the safe exit path.
                try
                {
                    _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _);
                }
                catch
                {
                }
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        MessageBox.Show(
                            $"重启未完成（{result.LastErrorZh}，共尝试 {result.AttemptsUsed} 次）。PopGlot 将安全退出；故障保护下新任务已暂停。",
                            "PopGlot 重启",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    catch
                    {
                    }
                });
            }
        }
        finally
        {
            RestartHandover.End();
        }
        ExitApplication();
    }

    private bool CleanupForHandover()
    {
        var ok = true;
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            _hotkeyOwner?.Close();
            _hotkeyOwner = null;
        }
        catch
        {
            ok = false;
        }
        try
        {
            // V05: the readiness probe watches the show-window signal name;
            // the old instance must release it first or the probe would
            // confirm against OUR own handle.
            _signalListener?.Cancel();
            _signalListener?.Dispose();
            _signalListener = null;
            _showSignal?.Dispose();
            _showSignal = null;
        }
        catch
        {
            ok = false;
        }
        try
        {
            DestroyActivePanel();
            CloseActiveOverlay();
            if (_activeQuickSearch is { } quickSearch)
            {
                quickSearch.ForceClose = true;
                quickSearch.Close();
                _activeQuickSearch = null;
            }
        }
        catch
        {
            ok = false;
        }
        try
        {
            TtsService.Stop();
        }
        catch
        {
            ok = false;
        }
        return ok;
    }

    /// <summary>
    /// V05: one restart attempt through the adapter-driven readiness
    /// launcher. Each attempt gets a UNIQUE readiness event name passed to
    /// the child via POPGLOT_READY_EVENT; the child signals it at the
    /// production-ready boundary (hotkeys registered + tray + listener).
    /// </summary>
    private Services.LaunchOutcome LaunchReplacementWithReadiness()
    {
        var readyEventName = Services.RestartHandover.NewReadyEventName();
        // V05: the parent PRE-CREATES and holds the attempt's event, so a
        // child that signals immediately can never be missed and the signal
        // STATE (not object existence) is what confirms readiness. The
        // handle is released when this attempt's scope ends.
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        return Services.RestartHandover.LaunchAndWaitReady(
            readyEventName,
            spawnTimeoutMs: 5000,
            readyTimeoutMs: 10000,
            spawn: () =>
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return null;
                }
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                startInfo.EnvironmentVariables["POPGLOT_READY_EVENT"] = readyEventName;
                return System.Diagnostics.Process.Start(startInfo);
            },
            hasExited: process => ((System.Diagnostics.Process)process).HasExited,
            exitCodeOf: process => ((System.Diagnostics.Process)process).ExitCode,
            terminateAndConfirm: process =>
            {
                var proc = (System.Diagnostics.Process)process;
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                    }
                }
                catch (Exception exception)
                {
                    return $"Kill 失败：{exception.GetType().Name}";
                }
                if (!proc.WaitForExit(5000))
                {
                    return "退出确认超时（5 秒）";
                }
                return null;
            },
            waitReadySignal: timeoutMs => readyEvent.WaitOne(timeoutMs));
    }

    private void TryNotifyCrash(string eventId)
    {
        try
        {
            // The exception is already durable in diagnostics. Repeating a
            // balloon for a dispatcher loop makes the UI problem worse, so
            // show one sanitized notice per process and keep later events quiet.
            if (_crashNotificationShown)
            {
                _crashSuppressedCount++;
                return;
            }
            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _crashNotifyWindowUtc > TimeSpan.FromSeconds(15))
            {
                _crashNotifyWindowUtc = nowUtc;
                _crashNotificationsInWindow = 0;
            }
            if (++_crashNotificationsInWindow > 1)
            {
                _crashSuppressedCount++;
                return;
            }
            var suffix = _crashSuppressedCount > 0
                ? $"（已拦截并记录，另静默拦截 {_crashSuppressedCount} 次）"
                : "（已拦截并记录，程序继续运行）";
            _crashSuppressedCount = 0;
            _crashNotificationShown = true;
            // Controlled copy only: the balloon never carries exception text,
            // which can contain headers, keys, URL queries or user content.
            Notify("PopGlot 遇到问题", $"{DiagnosticsLog.CrashSummary(eventId)}{suffix}", Forms.ToolTipIcon.Error);
        }
        catch (Exception)
        {
            // Nothing left to report through — never crash from the crash handler.
        }
    }

    /// <summary>
    /// An invisible window that only receives WM_HOTKEY. Registering global
    /// hotkeys must not require constructing the full settings window.
    /// </summary>
    private static Window CreateHotkeyOwnerWindow() => new()
    {
        ShowInTaskbar = false,
        ShowActivated = false,
        WindowStyle = WindowStyle.None,
        Focusable = false,
        Width = 0,
        Height = 0,
        Title = "PopGlot Hotkeys",
    };

    /// <summary>Builds the main window on first use, not at startup.</summary>
    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }
        _mainWindow = new MainWindow(_shellSettings, _history, _vocabulary)
        {
            OpenSettings = () => ShowSettings(),
            OpenAddEngineFlow = () => ShowSettings(startAddEngineFlow: true),
            NotifyTray = (title, message) => Notify(title, message, Forms.ToolTipIcon.Info),
            RequestExit = () => ExitApplication(),
        };
        // A startup hotkey failure happens before this window exists. The
        // coordinator kept the specific detail; paint it into the resident
        // Hotkey channel now that the status line can actually hold it.
        if (_hotkeyFailure.FailureActive)
        {
            _mainWindow.ShowShortcutConflict(_hotkeyFailure.ActiveDetail!);
        }
        return _mainWindow;
    }

    /// <summary>
    /// The settings window exists at most once; reopening activates the
    /// existing instance so drafts are never silently duplicated.
    /// </summary>
    private SettingsWindow EnsureSettingsWindow()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            return _settingsWindow;
        }
        var window = new SettingsWindow(_shellSettings, _history, _vocabulary)
        {
            ApplyShellSettings = TryApplyShellSettings,
            SetHotkeysSuspended = suspended => _hotkeys?.SetSuspended(suspended),
        };
        window.LocalDataCleared += () => _mainWindow?.ReloadHistory();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        };
        _settingsWindow = window;
        return window;
    }

    private void ShowSettings(bool startAddEngineFlow = false)
    {
        // A transient translation/capture surface must not remain behind a
        // newly opened settings window. Settings belongs to the main window,
        // so taskbar, activation and closing behaviour stay coherent.
        CloseActivePanel();
        CloseActiveOverlay();
        ShowMainWindow();
        var window = EnsureSettingsWindow();
        if (window.Owner is null && _mainWindow is not null)
        {
            window.Owner = _mainWindow;
        }
        // Same one-shot first-show convergence as the main window.
        WindowPositioner.ConvergeFirstShow(window);
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Activate();
        window.Focus();
        if (startAddEngineFlow)
        {
            window.ShowProviderAddFlow();
        }
    }

    // ================= Single instance =================

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // If the mutex cannot be inspected, running is still better than
            // refusing to start.
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowSignalName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // The first instance may be running as a different user; nothing
            // useful to do beyond exiting.
        }
    }

    /// <summary>Brings the window up when a second launch asks for it.</summary>
    private void StartShowWindowListener()
    {
        try
        {
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowSignalName);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var listener = new CancellationTokenSource();
        _signalListener = listener;
        var signal = _showSignal;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { signal, listener.Token.WaitHandle };
            while (!listener.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) != 0)
                {
                    return;
                }
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }, CancellationToken.None);
    }

    // ================= Hotkeys =================

    private void HandleHotkey(HotkeyAction action)
    {
        if (!RuntimeGate.NewWorkAllowed)
        {
            return;
        }
        switch (action)
        {
            case HotkeyAction.TranslateSelection:
                _ = BeginSelectionTranslationAsync();
                break;
            case HotkeyAction.CaptureScreen:
                BeginCapture();
                break;
            case HotkeyAction.ClosePanel:
                // A05: the close hotkey is a user intent — cancel the
                // request and hide, identical to X/Alt+F4.
                _activePanel?.CloseAsUserIntent();
                CloseActiveOverlay();
                break;
            case HotkeyAction.ShowWindow:
                ShowMainWindow();
                break;
            case HotkeyAction.QuickSearch:
                ShowQuickSearch();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    /// <summary>
    /// Bounded budget for the hotkey selection pre-read. ClipboardSelectionService
    /// already self-bounds the copy observation and every clipboard STA call at
    /// 1s, so this outer net adds NO wait on any healthy path (the worst normal
    /// case, modifier release + capture + observation, stays under ~1.6s). It
    /// only stops a pathological COM-retry storm from leaving the hotkey
    /// silently dead, while still letting the service's own 1s "no selection"
    /// verdict win so an empty selection degrades quietly as before.
    /// </summary>
    internal static readonly TimeSpan SelectionPrereadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The honest notice shown when the pre-read budget expires: the user gets
    /// the recoverable 极速查词 surface with an explanation instead of a
    /// hotkey that appears to do nothing.
    /// </summary>
    internal const string SelectionPrereadTimeoutNotice =
        "未能及时读取选区（目标应用或剪贴板未响应）；已切换到极速查词，可直接粘贴或输入后按 Enter 翻译。";

    /// <summary>Outcome of the bounded hotkey pre-read.</summary>
    internal sealed record SelectionPrereadResult(string? Text, Exception? Failure, bool TimedOut)
    {
        public bool Succeeded => !TimedOut && Failure is null && !string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>
    /// Races the selection read against a bounded budget. The token also rides
    /// INTO the read, so a timed-out attempt aborts at its next checkpoint —
    /// before the synthetic Ctrl+C when the capture is still pending — while
    /// its own finally still restores the clipboard. The attempt is never
    /// retried here, so the target application never receives a second
    /// synthetic copy keystroke.
    /// </summary>
    internal static async Task<SelectionPrereadResult> ReadSelectionPrereadAsync(
        ClipboardSelectionService selectionService,
        nint targetWindow,
        TimeSpan? timeoutOverride = null)
    {
        using var bounded = new CancellationTokenSource(timeoutOverride ?? SelectionPrereadTimeout);
        var readTask = selectionService.ReadSelectionAsync(bounded.Token, targetWindow);
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.InfiniteTimeSpan, bounded.Token));
        if (completed != readTask)
        {
            // Budget exhausted: take the recoverable path immediately. The
            // abandoned attempt concludes on its own internal bounds; observe
            // it so no late failure can escape as unobserved.
            _ = readTask.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new SelectionPrereadResult(null, null, TimedOut: true);
        }
        try
        {
            return new SelectionPrereadResult(await readTask, null, TimedOut: false);
        }
        catch (Exception exception)
        {
            return new SelectionPrereadResult(null, exception, TimedOut: false);
        }
    }

    private async Task BeginSelectionTranslationAsync()
    {
        var targetWindow = NativeMethods.GetForegroundWindow();
        var outcome = await ReadSelectionPrereadAsync(_selectionService, targetWindow);
        if (outcome.TimedOut)
        {
            // Bounded pre-read: recoverable — land in quick search with an
            // understandable notice instead of a silently dead hotkey. The
            // notice is STATE-DRIVEN (QuickSearchState.PendingNotice): the
            // first-show's queued Loaded sync re-applies it, and the next
            // real query clears it — a raw footer write here would lose the
            // race against Loaded on a freshly created window.
            ShowQuickSearch();
            _activeQuickSearch?.PostPendingNotice(SelectionPrereadTimeoutNotice);
            return;
        }
        if (outcome.Failure is InvalidOperationException noSelection && IsNoSelectionException(noSelection))
        {
            // 空选区：静默降级极速查词，不加打扰。
            ShowQuickSearch();
            return;
        }
        if (outcome.Failure is OperationCanceledException)
        {
            return;
        }
        if (outcome.Failure is not null)
        {
            var errorPanel = CreatePanel(CursorAnchorPixels());
            errorPanel.Show();
            errorPanel.ShowImmediateFailure(outcome.Failure.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(outcome.Text))
        {
            ShowQuickSearch();
            return;
        }

        var panel = CreatePanel(CursorAnchorPixels());
        panel.Show();
        await panel.StartSelectionAsync(
            new ClipboardSelectionService(new PreloadedSelectionClipboardAdapter(outcome.Text)),
            targetWindow);
    }

    internal static bool IsNoSelectionException(InvalidOperationException? ex) =>
        ex?.Message is { } msg &&
        (msg.Contains("未检测到可复制的选中文本", StringComparison.Ordinal) ||
         msg.Contains("选区没有可用文本", StringComparison.Ordinal));

    private sealed class PreloadedSelectionClipboardAdapter : ISelectionClipboardAdapter
    {
        private readonly string _text;
        private uint _seq = 1;

        public PreloadedSelectionClipboardAdapter(string text) => _text = text;

        public uint SequenceNumber => _seq;

        public Task<IClipboardSnapshot> CaptureAsync() =>
            Task.FromResult<IClipboardSnapshot>(new NoopSnapshot());

        public Task SendCopyAsync(CancellationToken cancellationToken)
        {
            _seq++;
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync() => Task.FromResult<string?>(_text);

        public Task RestoreAsync(IClipboardSnapshot snapshot) => Task.CompletedTask;

        private sealed class NoopSnapshot : IClipboardSnapshot
        {
            public void Dispose() { }
        }
    }

    private void BeginCapture(bool ocrOnly = false)

    {

        Dispatcher.Invoke(() =>

        {

            DestroyActivePanel();

            CloseActiveOverlay();



            var overlay = new CaptureOverlayWindow();

            if (ocrOnly)

            {

                overlay.SetOcrOnlyMode(true);

            }

            _activeOverlay = overlay;

            overlay.Closed += (_, _) =>

            {

                if (ReferenceEquals(_activeOverlay, overlay))

                {

                    _activeOverlay = null;

                }

            };

            overlay.Captured += async (_, capture) =>

            {

                var panel = CreatePanel(capture.PixelBounds);

                panel.Show();

                if (capture.IsOcrOnly)

                {

                    await panel.StartScreenshotOcrAsync(capture.Png);

                }

                else

                {

                    await panel.StartScreenshotAsync(capture.Png);

                }

            };

            overlay.Failed += (_, message) =>

            {

                var panel = CreatePanel(ScreenGeometry.WorkAreaForPixel(ScreenGeometry.CursorPixels()));

                panel.Show();

                panel.ShowImmediateFailure(message);

            };

            overlay.Show();

            overlay.Activate();

        });

    }

    private TranslationPanelWindow CreatePanel(Rect anchorPixels)

    {

        DestroyActivePanel();

        _panelLastUsedUtc = DateTime.UtcNow;

        var panel = new TranslationPanelWindow(

            anchorPixels,

            _history,

            () => _shellSettings,

            () => ShowSettings(),

            OpenInMainWindow,

            _vocabulary);

        _activePanel = panel;

        panel.Closed += (_, _) =>

        {

            if (ReferenceEquals(_activePanel, panel))

            {

                _activePanel = null;

            }

        };

        return panel;

    }

    /// <summary>
    /// "Expand in main window" carries the finished session instead of
    /// re-translating: source, language pair, the existing translation and
    /// the session's REAL state — an unfinished result must never be
    /// upgraded to Completed by the expansion.
    /// </summary>
    private void OpenInMainWindow(
        string source, string? targetLang, string? sourceLang, string? translation,
        TranslationSessionState sessionState)

    {

        ShowMainWindow();

        _mainWindow?.FocusTranslate(source, targetLang, sourceLang, translation, sessionState);

    }



    private void ShowQuickSearch()

    {

        Dispatcher.Invoke(() =>

        {

            if (_activeQuickSearch is not null && _activeQuickSearch.IsLoaded)

            {

                // C05: the instance may be hidden (X/Esc/focus-loss); Show
                // restores it with its previous session intact.
                _activeQuickSearch.Show();

                _activeQuickSearch.Activate();
                _activeQuickSearch.Focus();
                _quickSearchLastUsedUtc = DateTime.UtcNow;

                return;

            }

            _quickSearchLastUsedUtc = DateTime.UtcNow;
            _activeQuickSearch = new QuickSearchWindow(_history, _vocabulary);

            _activeQuickSearch.Closed += (_, _) => _activeQuickSearch = null;

            _activeQuickSearch.Show();

            _activeQuickSearch.Activate();
            _activeQuickSearch.Focus();

        });

    }

    private void CloseActivePanel()
    {
        if (_activePanel is { } panel)
        {
            var session = panel.CreateSessionSnapshot();
            if (session is not null && !SharedSessionStore.TryStore(session, out var rejection))
            {
                // The panel hides right after; nothing can show the reason.
                LogSessionStoreRejection(rejection);
            }
            panel.Hide();
        }
        // 面板隐藏后把截图/渲染留下的临时页换出，避免常驻内存随使用次数
        // 只增不减。真实分配不受影响，需要时系统会自动调页回来。
        TrimWorkingSet();
    }

    /// <summary>
    /// C05: real destruction for session replacement — a new translation
    /// supersedes the old one, so its surface finally closes.
    /// N01: captures a snapshot into the session store before closing.
    /// </summary>
    private void DestroyActivePanel()
    {
        if (_activePanel is { } panel)
        {
            var session = panel.CreateSessionSnapshot();
            if (session is not null && !SharedSessionStore.TryStore(session, out var rejection))
            {
                // The panel is force-closed right after; the rejection can
                // only be recorded.
                LogSessionStoreRejection(rejection);
            }
            panel.ForceClose = true;
            panel.Close();
        }
        _activePanel = null;
    }

    private DateTime _panelLastUsedUtc = DateTime.MinValue;
    private DateTime _quickSearchLastUsedUtc = DateTime.MinValue;

    /// <summary>
    /// C05/A06/N01: bring back the most recently USED hidden surface without
    /// resending anything — the window instance kept its session, or the in-memory
    /// session store restores the snapshot. ZERO RESEND GUARANTEE.
    /// </summary>
    private void RestoreRecentSurface()
    {
        var panelUsable = _activePanel is { IsLoaded: true };
        var quickUsable = _activeQuickSearch is { IsLoaded: true };
        if (panelUsable || quickUsable)
        {
            var restorePanel = panelUsable &&
                (!quickUsable || _panelLastUsedUtc >= _quickSearchLastUsedUtc);
            if (restorePanel)
            {
                _activePanel!.Show();
                _activePanel.Activate();
                _panelLastUsedUtc = DateTime.UtcNow;
            }
            else
            {
                _activeQuickSearch!.Show();
                _activeQuickSearch.Activate();
                _quickSearchLastUsedUtc = DateTime.UtcNow;
            }
            return;
        }

        // N01: neither window is active in memory -> restore from session store
        var stored = SharedSessionStore.PopRecent();
        if (stored is not null)
        {
            RestoreStoredSession(stored);
            return;
        }

        Notify(
            "暂无可恢复的翻译",
            "会话已结束；按划词/截图快捷键开始新翻译。",
            Forms.ToolTipIcon.Info);
    }

    private void RestoreStoredSession(StoredSession session)
    {
        var panel = CreatePanel(CursorAnchorPixels());
        panel.RestoreSession(session);
        panel.Show();
        panel.Activate();
        _panelLastUsedUtc = DateTime.UtcNow;
    }

    private void CloseActiveOverlay()
    {
        _activeOverlay?.Close();
        _activeOverlay = null;
    }

    /// <summary>Surfaces the core's one-shot startup notice as a tray balloon.</summary>
    private void AnnounceStartupNotice()
    {
        var notice = CoreBridge.TakeStartupNotice();
        if (string.IsNullOrWhiteSpace(notice))
        {
            return;
        }
        Notify("配置已重置", notice, Forms.ToolTipIcon.Warning);
    }

    /// <summary>Where the popup should appear, in physical pixels.</summary>
    /// <remarks>
    /// The text caret is a better anchor than the mouse when the user selected
    /// with the keyboard, so it is preferred when the foreground app exposes it.
    /// </remarks>
    private static Rect CursorAnchorPixels()
    {
        var point = TryGetCaretPixels() ?? ScreenGeometry.CursorPixels();
        return new Rect(point.X - 2, point.Y - 2, 4, 4);
    }

    private static Point? TryGetCaretPixels()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == 0)
        {
            return null;
        }
        var thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new NativeGuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<NativeGuiThreadInfo>(),
        };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref info) || info.CaretWindow == 0)
        {
            return null;
        }
        var point = new NativePoint(info.CaretRect.Left, info.CaretRect.Bottom);
        return NativeMethods.ClientToScreen(info.CaretWindow, ref point)
            ? new Point(point.X, point.Y)
            : null;
    }

    /// <summary>
    /// Applies a settings snapshot: registers the hotkey set atomically and,
    /// on success, adopts theme/tray/engine surfaces. Returns a typed
    /// outcome so callers (settings save, startup) can tell a settings
    /// PROBE failure (candidate lost, previous set live again) apart from a
    /// real global failure — by evidence, not by guessing.
    /// </summary>
    private ShellApplyOutcome TryApplyShellSettings(ShellSettings settings)
    {
        var validationError = settings.ValidateHotkeys();
        if (validationError is not null)
        {
            return ShellApplyOutcome.Failed(ShellApplyFailureKind.InvalidHotkeys, validationError);
        }
        if (_hotkeys is null)
        {
            return ShellApplyOutcome.Failed(ShellApplyFailureKind.HotkeyUnavailable, "快捷键服务未就绪");
        }

        _hotkeyFailureReportedThisAttempt = false;
        if (!_hotkeys.TryRegisterAll(settings.Hotkeys, out var conflict))
        {
            // Route by evidence, never by cycle state: a probe failure with
            // a healthy previous set stays settings-inline only, and a
            // global failure is reported exactly ONCE per attempt — by the
            // service event when the restore failed too, otherwise here.
            // Even inside an ALREADY OPEN failure cycle the App must report:
            // the coordinator keeps the single balloon per cycle, collapses
            // an identical detail to a silent no-op, and turns a CHANGED
            // detail into a status-only refresh — without this report the
            // second conflict would leave the stale first detail on the
            // workbench footer and in the tray for the whole cycle.
            var route = ShellApplyFailureRouting.Classify(
                _hotkeys.IsFullyAvailable, _hotkeyFailureReportedThisAttempt);
            if (route == ShellApplyFailureRoute.ProbeInlineOnly)
            {
                // Probe failure: the candidate lost, but the previous set is
                // fully live again. The settings window reports this inline;
                // NO global unavailable state, NO balloon and NO workbench
                // transient may be raised for it.
                return ShellApplyOutcome.Failed(
                    ShellApplyFailureKind.HotkeyConflictRestored, conflict);
            }
            if (route == ShellApplyFailureRoute.AppMustReport)
            {
                // Real global failure nobody reported yet. The service event
                // could not have fired — e.g. the previous set is empty, so
                // its restore trivially "succeeded" (startup, or a set that
                // was already fully dead).
                OnHotkeyFailure(conflict ?? "未知快捷键");
            }
            return ShellApplyOutcome.Failed(ShellApplyFailureKind.HotkeyUnavailable, conflict);
        }

        _shellSettings = settings;
        ThemeService.Apply(settings.Theme);
        // Full success closes any active failure cycle: surfaces clear and a
        // future failure may notify again. Idempotent when nothing failed.
        OnHotkeyRestored();
        UpdateTrayTooltip();
        // The workbench footer always shows what actually runs right now.
        _mainWindow?.RefreshEngineStatus();
        // Settings saved → the main window's close button must re-decide
        // between 关闭到托盘 / 退出 PopGlot from the persisted preference.
        _mainWindow?.RefreshCloseButtonForTraySetting();
        return ShellApplyOutcome.Ok();
    }

    /// <summary>
    /// A global hotkey failure was observed (service event, a startup
    /// registration that left zero live hotkeys, or a settings save that
    /// failed with nobody else reporting — open cycle or not). Balloons once
    /// per failure cycle, keeps the workbench footer's resident Hotkey
    /// channel current and stops the tray from claiming shortcuts that are
    /// dead; repeats of the same detail are collapsed by the coordinator.
    /// </summary>
    private void OnHotkeyFailure(string detail)
    {
        var decision = _hotkeyFailure.ReportFailure(detail);
        if (decision == HotkeyFailureDecision.None)
        {
            return;
        }
        if (decision == HotkeyFailureDecision.Balloon)
        {
            Notify(
                "快捷键注册失败",
                $"{detail}。请在「设置 → 快捷键」中更换组合。",
                Forms.ToolTipIcon.Warning);
        }
        // Same cycle with a changed detail: no new balloon, but every
        // surface must describe the CURRENT failure, not the stale one.
        _mainWindow?.ShowShortcutConflict(detail);
        UpdateTrayTooltip();
    }

    /// <summary>
    /// The hotkey set is fully live again. Closes the failure cycle exactly
    /// once, clears the resident footer channel (falling back to
    /// EngineConfig/Ready) and restores the tray's shortcut claims.
    /// </summary>
    private void OnHotkeyRestored()
    {
        if (!_hotkeyFailure.ReportRecovery())
        {
            return;
        }
        _mainWindow?.ClearShortcutConflict();
        UpdateTrayTooltip();
    }

    // ================= Tray =================

    private void CreateTrayIcon()

    {

        _trayMenu = new Forms.ContextMenuStrip

        {

            Renderer = new Forms.ToolStripProfessionalRenderer(),

            ShowImageMargin = false,

        };

        _trayMenu.Items.Add("打开 PopGlot", null, (_, _) => ShowMainWindow());

        var quickSearchItem = _trayMenu.Items.Add("极速查词", null, (_, _) => ShowQuickSearch());

        _trayMenu.Items.Add("恢复最近翻译", null, (_, _) => RestoreRecentSurface());

        var sessionsSubMenu = new Forms.ToolStripMenuItem("暂存的翻译（最多5条）");
        _trayMenu.Items.Add(sessionsSubMenu);

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var selectionItem = _trayMenu.Items.Add("翻译选中文字", null, (_, _) => _ = BeginSelectionTranslationAsync());

        var captureItem = _trayMenu.Items.Add("截图翻译", null, (_, _) => BeginCapture(ocrOnly: false));

        var ocrItem = _trayMenu.Items.Add("截图提取文本 (OCR)", null, (_, _) => BeginCapture(ocrOnly: true));

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());

        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());



        _trayMenu.Opening += (_, _) =>

        {

            // A live hotkey failure must stop the menu from advertising
            // shortcuts that will not fire; the actions themselves stay
            // usable (they run from the click, not the hotkey).
            var hotkeysUnavailable = _hotkeyFailure.FailureActive;

            quickSearchItem.Text = hotkeysUnavailable
                ? "极速查词（快捷键不可用）"
                : $"极速查词\t{_shellSettings.QuickSearchHotkey.DisplayName}";

            selectionItem.Text = hotkeysUnavailable

                ? "翻译选中文字（快捷键不可用）"

                : $"翻译选中文字\t{_shellSettings.SelectionHotkey.DisplayName}";

            captureItem.Text = hotkeysUnavailable

                ? "截图翻译（快捷键不可用）"

                : $"截图翻译\t{_shellSettings.ScreenshotHotkey.DisplayName}";

            ocrItem.Text = $"截图提取文本 (OCR)\tShift + 截图";

            sessionsSubMenu.DropDownItems.Clear();
            var sessions = SharedSessionStore.GetAll();
            if (sessions.Count == 0)
            {
                var emptyItem = new Forms.ToolStripMenuItem("(暂无暂存会话)") { Enabled = false };
                sessionsSubMenu.DropDownItems.Add(emptyItem);
            }
            else
            {
                foreach (var s in sessions)
                {
                    var item = new Forms.ToolStripMenuItem(s.Summary())
                    {
                        Tag = s
                    };
                    item.Click += (_, _) => RestoreStoredSession(s);
                    sessionsSubMenu.DropDownItems.Add(item);
                }
                sessionsSubMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
                var clearItem = new Forms.ToolStripMenuItem("清空暂存的翻译");
                clearItem.Click += (_, _) =>
                {
                    SharedSessionStore.Clear();
                    Notify("暂存的翻译已清空", "所有内存暂存会话已清除。", Forms.ToolTipIcon.Info);
                };
                sessionsSubMenu.DropDownItems.Add(clearItem);
            }
        };



        _trayIconImage = LoadAppIconFromResource();

        _trayIcon = new Forms.NotifyIcon

        {

            Text = "PopGlot",

            Icon = _trayIconImage,

            ContextMenuStrip = _trayMenu,

            Visible = true,

        };

        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        UpdateTrayTooltip();

    }

    /// <summary>
    /// Loads the shipped multi-size icon from the embedded resources instead
    /// of drawing a placeholder at runtime.
    /// </summary>
    private static Drawing.Icon LoadAppIconFromResource()
    {
        try
        {
            // GetResourceStream THROWS when the resource is missing (it does
            // not return null): a partial or mixed-file install must degrade
            // to the system icon, not fail the whole startup over an icon.
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/PopGlot-v5.ico"));
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UriFormatException)
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }
        // NotifyIcon.Text is capped at 63 characters; longer text throws.
        // While a hotkey failure is active the tooltip must NOT claim
        // shortcuts that are dead; the menu text below follows the same rule.
        var text = _hotkeyFailure.FailureActive
            ? "PopGlot · 全局快捷键不可用 — 在「设置 → 快捷键」更换组合"
            : $"PopGlot · {_shellSettings.SelectionHotkey.DisplayName} 划词 · " +
              $"{_shellSettings.ScreenshotHotkey.DisplayName} 截图";
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
    {
        if (_trayIcon is null)
        {
            return;
        }
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(5000);
    }

    private void ShowMainWindow()
    {
        var mainWindow = EnsureMainWindow();
        // One-shot first-show convergence: a small work area must not push the
        // caption/footer off screen. Later resizes/maximizes are never touched.
        WindowPositioner.ConvergeFirstShow(mainWindow);
        mainWindow.ReloadHistory();
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }
        mainWindow.Activate();
        mainWindow.Focus();
    }

    private async void ExitApplication()
    {
        if (_exitInProgress)
        {
            return;
        }
        _exitInProgress = true;
        RuntimeGate.NewWorkAllowed = false;

        // Stop producers first. The durable queues are drained only after no
        // window can enqueue another history/vocabulary mutation.
        DestroyActivePanel();
        CloseActiveOverlay();
        if (_activeQuickSearch is { } quickSearch)
        {
            // A06: without ForceClose the quick search would CANCEL this
            // shutdown (its OnClosing hides instead of closing) and the
            // process would hang.
            quickSearch.ForceClose = true;
            quickSearch.Close();
            _activeQuickSearch = null;
        }
        _hotkeys?.Dispose();
        _hotkeys = null;
        _hotkeyOwner?.Close();
        _hotkeyOwner = null;
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }
        if (_settingsWindow is not null)
        {
            // 退出路径必须绕过未保存草稿守卫：OnClosing 取消关闭会与
            // Shutdown 纠缠导致退出卡死（设置页开着自定义模型时最明显）。
            _settingsWindow.ForceClose = true;
            _settingsWindow.Close();
            _settingsWindow = null;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
        SharedSessionStore.Clear();

        try
        {
            // Disk stalls must not freeze the dispatcher or trigger Windows'
            // "not responding" UI. Each queue remains bounded, but the wait
            // happens off the UI thread and only once for a normal exit.
            await Task.Run(() =>
            {
                _vocabulary.Flush();
                _history.Flush();
                DiagnosticsLog.Flush();
            });
            _storesFlushedForExit = true;
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Startup failure / OS shutdown can bypass ExitApplication. Keep one
        // synchronous fallback for those paths, but never double-flush a normal
        // user exit that already drained the stores in the background.
        if (!_storesFlushedForExit)
        {
            _vocabulary.Flush();
            _history.Flush();
            DiagnosticsLog.Flush();
        }
        _signalListener?.Cancel();
        _signalListener?.Dispose();
        _showSignal?.Dispose();
        _hotkeys?.Dispose();
        TtsService.Stop();
        SharedSessionStore.Clear();
        if (_trayIcon is not null)
        {
            // Hide first: a disposed NotifyIcon can otherwise leave a ghost icon
            // in the tray until the user hovers over it.
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconImage?.Dispose();
        _trayMenu?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyIcon(nint icon);

        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetGUIThreadInfo(uint threadId, ref NativeGuiThreadInfo info);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClientToScreen(nint window, ref NativePoint point);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);

        [LibraryImport("kernel32.dll")]
        internal static partial nint GetCurrentProcess();
    }

    /// <summary>
    /// 把进程工作集交还给系统（-1,-1 语义）。不改变已分配内存，只把暂不
    /// 使用的页换出；常驻托盘、面板关闭后调用可显著降低任务管理器里的
    /// 内存读数，需要时系统会自动把页调回。
    /// </summary>
    internal static void TrimWorkingSet()
    {
        try
        {
            _ = NativeMethods.SetProcessWorkingSetSize(
                NativeMethods.GetCurrentProcess(), (nint)(-1), (nint)(-1));
        }
        catch (Exception)
        {
            // Best effort only; failure to trim must never affect UX.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint ActiveWindow;
        public nint FocusWindow;
        public nint CaptureWindow;
        public nint MenuOwnerWindow;
        public nint MoveSizeWindow;
        public nint CaretWindow;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
