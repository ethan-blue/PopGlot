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

    private TranslationPanelWindow? _activePanel;

    private CaptureOverlayWindow? _activeOverlay;

    private QuickSearchWindow? _activeQuickSearch;

    private ShellSettings _shellSettings = ShellSettings.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            AnnounceStartupNotice();
            Services.OutboundPolicy.ConsentPrompt = PromptFreeEngineConsent;

            // Startup only creates the tray, theme, and a hidden message
            // window for the hotkeys; the heavy MainWindow is built on first
            // use so cold-start reaches a usable tray as fast as possible.
            _hotkeyOwner = CreateHotkeyOwnerWindow();
            _hotkeys = new HotkeyService(_hotkeyOwner);
            _hotkeys.Pressed += (_, action) => HandleHotkey(action);

            CreateTrayIcon();
            StartShowWindowListener();

            if (!TryApplyShellSettings(_shellSettings))
            {
                // Surface it where the user can actually see it — the settings
                // window is hidden at this point.
                Notify(
                    "快捷键注册失败",
                    "有快捷键被其他程序占用。请在「快捷键与外观」中改成别的组合。",
                    Forms.ToolTipIcon.Warning);
                ShowMainWindow();
            }

            if (e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"PopGlot 启动失败：{exception.Message}",
                "PopGlot",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
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
            ApplyShellSettings = TryApplyShellSettings,
        };
        return _mainWindow;
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
        switch (action)
        {
            case HotkeyAction.TranslateSelection:
                _ = BeginSelectionTranslationAsync();
                break;
            case HotkeyAction.CaptureScreen:
                BeginCapture();
                break;
            case HotkeyAction.ClosePanel:
                CloseActivePanel();
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
        var panel = CreatePanel(CursorAnchorPixels());
        // Shown without activation so the synthesized Ctrl+C still reaches the
        // app the user was reading; the panel takes focus once the text is in.
        panel.Show();
        await panel.StartSelectionAsync(_selectionService);
    }

    private void BeginCapture(bool ocrOnly = false)

    {

        Dispatcher.Invoke(() =>

        {

            CloseActivePanel();

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

        CloseActivePanel();

        var panel = new TranslationPanelWindow(

            anchorPixels,

            _history,

            () => _shellSettings,

            ShowMainWindow,

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

                _activeQuickSearch.Activate();

                return;

            }

            _activeQuickSearch = new QuickSearchWindow(_history, _vocabulary);

            _activeQuickSearch.Closed += (_, _) => _activeQuickSearch = null;

            _activeQuickSearch.Show();

            _activeQuickSearch.Activate();

        });

    }

    private void CloseActivePanel()
    {
        _activePanel?.Close();
        _activePanel = null;
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

    /// <summary>
    /// The first-use consent dialog for the free web engine. Cancel remembers a
    /// refusal; No sends nothing this time and asks again on the next use.
    /// </summary>
    private FreeEngineDecision PromptFreeEngineConsent(string destination)
    {
        return Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "首次使用内置免费引擎。\n\n" +
                $"待翻译的文本将发送到{destination}，" +
                "不会发送截图、API Key 或其他凭据。\n\n" +
                "「是」= 允许并记住　「否」= 仅本次允许　「取消」= 不允许",
                "允许使用内置免费引擎？",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            return result switch
            {
                MessageBoxResult.Yes => FreeEngineDecision.AlwaysAllow,
                MessageBoxResult.No => FreeEngineDecision.AllowOnce,
                _ => FreeEngineDecision.Deny,
            };
        });
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

        _trayMenu.Items.Add("极速查词 (Spotlight)", null, (_, _) => ShowQuickSearch());

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var selectionItem = _trayMenu.Items.Add("翻译选中文字", null, (_, _) => _ = BeginSelectionTranslationAsync());

        var captureItem = _trayMenu.Items.Add("截图翻译", null, (_, _) => BeginCapture(ocrOnly: false));

        var ocrItem = _trayMenu.Items.Add("截图提取文字 (OCR)", null, (_, _) => BeginCapture(ocrOnly: true));

        _trayMenu.Items.Add(new Forms.ToolStripSeparator());

        _trayMenu.Items.Add("设置", null, (_, _) => ShowMainWindow());

        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());



        _trayMenu.Opening += (_, _) =>

        {

            selectionItem.Text = $"翻译选中文字\t{_shellSettings.SelectionHotkey.DisplayName}";

            captureItem.Text = $"截图翻译\t{_shellSettings.ScreenshotHotkey.DisplayName}";

            ocrItem.Text = $"截图提取文字 (OCR)\tShift + 截图";

        };



        _trayIconImage = CreateAppIcon();

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

    private static Drawing.Icon CreateAppIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Drawing.Color.Transparent);

            using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(53, 208, 165));
            using var path = RoundedRectangle(new Drawing.Rectangle(1, 1, 30, 30), 8);
            graphics.FillPath(background, path);

            using var font = new Drawing.Font(
                "Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var textBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(4, 33, 26));
            using var format = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            graphics.DrawString("P", font, textBrush, new Drawing.RectangleF(1, 1, 30, 30), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the icon survives DestroyIcon on the temporary handle.
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(Drawing.Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    private void ExitApplication()
    {
        CloseActivePanel();
        CloseActiveOverlay();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _hotkeyOwner?.Close();
        _hotkeyOwner = null;
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
