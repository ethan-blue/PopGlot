using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;

namespace PopGlot.Windows;

public partial class App : System.Windows.Application
{
    private readonly HistoryStore _history = new();
    private readonly ClipboardSelectionService _selectionService =
        new(new WindowsSelectionClipboardAdapter());
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _trayIconImage;
    private HotkeyService? _hotkeys;
    private MainWindow? _settingsWindow;
    private TranslationPanelWindow? _activePanel;
    private ShellSettings _shellSettings = ShellSettings.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _shellSettings = ShellSettingsStore.Load();
            ThemeService.Apply(_shellSettings.Theme);
            CoreBridge.Initialize();
            _settingsWindow = new MainWindow(_shellSettings, _history)
            {
                ApplyShellSettings = TryApplyShellSettings,
            };
            _settingsWindow.Closing += (_, args) =>
            {
                if (!_settingsWindow.AllowClose)
                {
                    args.Cancel = true;
                    _settingsWindow.Hide();
                }
            };

            _hotkeys = new HotkeyService(_settingsWindow);
            _hotkeys.Pressed += (_, action) => HandleHotkey(action);
            CreateTrayIcon();
            var hotkeysReady = TryApplyShellSettings(_shellSettings);
            if (hotkeysReady && e.Args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
            {
                ShowSettings();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"PopGlot 启动失败：{exception.Message}",
                "PopGlot",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

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
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private async Task BeginSelectionTranslationAsync()
    {
        var panel = CreatePanel(CursorAnchor());
        panel.Show();
        await panel.StartSelectionAsync(_selectionService);
    }

    private void BeginCapture()
    {
        Current.Dispatcher.Invoke(() =>
        {
            CloseActivePanel();
            var overlay = new CaptureOverlayWindow();
            overlay.SelectionCompleted += async (_, selection) =>
            {
                try
                {
                    // Let the transparent overlay leave the compositor before capture.
                    await Task.Delay(45);
                    var image = ScreenCaptureService.CapturePng(selection.PixelBounds);
                    var panel = CreatePanel(selection.DisplayBounds);
                    panel.Show();
                    await panel.StartScreenshotAsync(image);
                }
                catch (Exception exception)
                {
                    var panel = CreatePanel(selection.DisplayBounds);
                    panel.Show();
                    panel.ShowImmediateFailure(exception.Message);
                }
            };
            overlay.Show();
            overlay.Activate();
        });
    }

    private TranslationPanelWindow CreatePanel(Rect anchor)
    {
        CloseActivePanel();
        var panel = new TranslationPanelWindow(anchor, _history, () => _shellSettings);
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

    private void CloseActivePanel()
    {
        _activePanel?.Close();
        _activePanel = null;
    }

    private Rect CursorAnchor()
    {
        var devicePoint = TryGetCaretPoint() ?? Forms.Cursor.Position;
        var point = new Point(devicePoint.X, devicePoint.Y);
        if (_settingsWindow is not null &&
            PresentationSource.FromVisual(_settingsWindow)?.CompositionTarget is { } target)
        {
            point = target.TransformFromDevice.Transform(point);
        }
        return new Rect(point.X - 2, point.Y - 2, 4, 4);
    }

    private static System.Drawing.Point? TryGetCaretPoint()
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
            ? new System.Drawing.Point(point.X, point.Y)
            : null;
    }

    private bool TryApplyShellSettings(ShellSettings settings)
    {
        var validationError = settings.ValidateHotkeys();
        if (validationError is not null || _hotkeys is null)
        {
            return false;
        }
        if (!_hotkeys.TryRegisterAll(settings.Hotkeys, out var conflict))
        {
            _settingsWindow?.ShowShortcutConflict(conflict ?? "未知快捷键");
            return false;
        }
        _shellSettings = settings;
        ThemeService.Apply(settings.Theme);
        UpdateTrayText();
        return true;
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(),
        };
        _trayMenu.Items.Add("打开翻译窗口", null, (_, _) => ShowTranslateWindow());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("翻译选中文字 (Ctrl+Alt+W)", null, (_, _) => _ = BeginSelectionTranslationAsync());
        _trayMenu.Items.Add("截图翻译 (Ctrl+Alt+Space)", null, (_, _) => BeginCapture());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("偏好设置与历史", null, (_, _) => ShowSettings());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出 PopGlot", null, (_, _) => ExitApplication());

        _trayIconImage = CreateAppIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PopGlot - 桌面翻译工具",
            Icon = _trayIconImage,
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowTranslateWindow();
        UpdateTrayText();
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(22, 28, 37));
        using var accent = new SolidBrush(Color.FromArgb(89, 211, 177));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        graphics.FillEllipse(accent, 5, 5, 22, 22);
        using var font = new Font("Segoe UI", 13, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(10, 34, 28));
        graphics.DrawString("P", font, textBrush, 10, 8);
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private void UpdateTrayText()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Text = $"PopGlot · {_shellSettings.SelectionShortcut.DisplayName} 划词";
        }
    }

    private void ShowTranslateWindow()
    {
        _settingsWindow?.Show();
        _settingsWindow?.Activate();
    }

    private void ShowSettings()
    {
        _settingsWindow?.ReloadHistory();
        _settingsWindow?.Show();
        _settingsWindow?.Activate();
    }

    private void ExitApplication()
    {
        CloseActivePanel();
        _hotkeys?.Dispose();
        _hotkeys = null;
        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowClose = true;
            _settingsWindow.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
        _trayMenu?.Dispose();
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
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
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
