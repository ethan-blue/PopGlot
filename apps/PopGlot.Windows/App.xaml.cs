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

    private SettingsWindow? _settingsWindow;

    private TranslationPanelWindow? _activePanel;

    private CaptureOverlayWindow? _activeOverlay;

    private QuickSearchWindow? _activeQuickSearch;

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
                Notify(
                    "快捷键恢复失败",
                    conflict ?? "快捷键被其他程序占用。请在「设置 → 快捷键」中更换组合。",
                    Forms.ToolTipIcon.Warning);
                _mainWindow?.ShowShortcutConflict(conflict ?? "未知快捷键");
            };

            CreateTrayIcon();
            // A02: the failure marker is consumed only after the tray exists,
            // so the balloon can actually be shown before the marker is gone.
            AnnounceBackgroundStartupFailureIfAny();
            StartShowWindowListener();

            var shellApplied = TryApplyShellSettings(_shellSettings);
            if (!shellApplied)
            {
                // Surface it where the user can actually see it — the settings
                // window is hidden at this point.
                Notify(
                    "快捷键注册失败",
                    "快捷键被其他程序占用。请在「设置 → 快捷键」中更换组合。",
                    Forms.ToolTipIcon.Warning);
                if (!backgroundStart)
                {
                    ShowMainWindow();
                }
            }

            // V05: the restart parent waits on THIS attempt's unique event;
            // it is signalled only after the hotkeys actually registered and
            // the tray/listener are live — the real production boundary.
            var readyEventName = Environment.GetEnvironmentVariable("POPGLOT_READY_EVENT");
            if (shellApplied && !string.IsNullOrEmpty(readyEventName))
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
        };
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
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private async Task BeginSelectionTranslationAsync()
    {
        var targetWindow = NativeMethods.GetForegroundWindow();
        var panel = CreatePanel(CursorAnchorPixels());
        // Shown without activation so the synthesized Ctrl+C still reaches the
        // app the user was reading; the panel takes focus once the text is in.
        panel.Show();
        await panel.StartSelectionAsync(_selectionService, targetWindow);
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
    /// re-translating: source, language pair, and the existing translation.
    /// </summary>
    private void OpenInMainWindow(string source, string? targetLang, string? sourceLang, string? translation)

    {

        ShowMainWindow();

        _mainWindow?.FocusTranslate(source, targetLang, sourceLang, translation);

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

                return;

            }

            _quickSearchLastUsedUtc = DateTime.UtcNow;
            _activeQuickSearch = new QuickSearchWindow(_history, _vocabulary);

            _activeQuickSearch.Closed += (_, _) => _activeQuickSearch = null;

            _activeQuickSearch.Show();

            _activeQuickSearch.Activate();

        });

    }

    private void CloseActivePanel()
    {
        // C05: hiding is a visibility change only — the session (partial,
        // scroll, selection) survives and can be restored from the tray, and
        // a running request continues to its deadline. Real destruction
        // happens on session replacement (DestroyActivePanel) or exit.
        _activePanel?.Hide();
        // 面板隐藏后把截图/渲染留下的临时页换出，避免常驻内存随使用次数
        // 只增不减。真实分配不受影响，需要时系统会自动调页回来。
        TrimWorkingSet();
    }

    /// <summary>
    /// C05: real destruction for session replacement — a new translation
    /// supersedes the old one, so its surface finally closes.
    /// </summary>
    private void DestroyActivePanel()
    {
        if (_activePanel is { } panel)
        {
            panel.ForceClose = true;
            panel.Close();
        }
        _activePanel = null;
    }

    private DateTime _panelLastUsedUtc = DateTime.MinValue;
    private DateTime _quickSearchLastUsedUtc = DateTime.MinValue;

    /// <summary>
    /// C05/A06: bring back the most recently USED hidden surface without
    /// resending anything — the window instance kept its session. Recency is
    /// explicit metadata, never a fixed type preference.
    /// </summary>
    private void RestoreRecentSurface()
    {
        var panelUsable = _activePanel is { IsLoaded: true };
        var quickUsable = _activeQuickSearch is { IsLoaded: true };
        if (!panelUsable && !quickUsable)
        {
            Notify(
                "暂无可恢复的翻译",
                "最近的浮窗会话已结束或被新会话替换；按划词/截图快捷键即可开始新翻译。",
                Forms.ToolTipIcon.Info);
            return;
        }
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

    private bool TryApplyShellSettings(ShellSettings settings)
    {
        if (settings.ValidateHotkeys() is not null || _hotkeys is null)
        {
            return false;
        }
        if (!_hotkeys.TryRegisterAll(settings.Hotkeys, out var conflict))
        {
            _mainWindow?.ShowShortcutConflict(conflict ?? "未知快捷键");
            return false;
        }
        _shellSettings = settings;
        ThemeService.Apply(settings.Theme);
        UpdateTrayTooltip();
        // The workbench footer always shows what actually runs right now.
        _mainWindow?.RefreshEngineStatus();
        return true;
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

        _trayMenu.Items.Add("极速查词", null, (_, _) => ShowQuickSearch());

        _trayMenu.Items.Add("恢复最近翻译", null, (_, _) => RestoreRecentSurface());

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var selectionItem = _trayMenu.Items.Add("翻译选中文字", null, (_, _) => _ = BeginSelectionTranslationAsync());

        var captureItem = _trayMenu.Items.Add("截图翻译", null, (_, _) => BeginCapture(ocrOnly: false));

        var ocrItem = _trayMenu.Items.Add("截图提取文本 (OCR)", null, (_, _) => BeginCapture(ocrOnly: true));

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());

        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());



        _trayMenu.Opening += (_, _) =>

        {

            selectionItem.Text = $"翻译选中文字\t{_shellSettings.SelectionHotkey.DisplayName}";

            captureItem.Text = $"截图翻译\t{_shellSettings.ScreenshotHotkey.DisplayName}";

            ocrItem.Text = $"截图提取文本 (OCR)\tShift + 截图";

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
        var text = $"PopGlot · {_shellSettings.SelectionHotkey.DisplayName} 划词 · " +
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
