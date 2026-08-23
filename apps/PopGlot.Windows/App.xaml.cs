using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace PopGlot.Windows;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private HotkeyService? _hotkey;
    private MainWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            CoreBridge.Initialize();
            _settingsWindow = new MainWindow();
            _settingsWindow.ShortcutChanged += (_, shortcut) => RegisterHotkey(shortcut);
            _settingsWindow.Closing += (_, args) =>
            {
                if (!_settingsWindow.AllowClose)
                {
                    args.Cancel = true;
                    _settingsWindow.Hide();
                }
            };

            _hotkey = new HotkeyService(_settingsWindow);
            _hotkey.Pressed += (_, _) => BeginCapture();
            RegisterHotkey(_settingsWindow.CurrentShortcut);
            CreateTrayIcon();
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

    private void CreateTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("截图翻译", null, (_, _) => BeginCapture());
        _trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PopGlot — Ctrl+Alt+Space 截图翻译",
            Icon = SystemIcons.Information,
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => BeginCapture();
        _trayIcon.ShowBalloonTip(
            2500,
            "PopGlot 已就绪",
            "按 Ctrl+Alt+Space 开始截图翻译。当前版本处于安全开发模式，不会发送图片或 API 请求。",
            Forms.ToolTipIcon.Info);
    }

    private void RegisterHotkey(ShortcutOption shortcut)
    {
        if (_hotkey is null)
        {
            return;
        }

        if (!_hotkey.Register(shortcut))
        {
            _settingsWindow?.ShowShortcutConflict(shortcut.DisplayName);
        }
        else if (_trayIcon is not null)
        {
            _trayIcon.Text = $"PopGlot — {shortcut.DisplayName} 截图翻译";
        }
    }

    private void BeginCapture()
    {
        Current.Dispatcher.Invoke(() =>
        {
            var overlay = new CaptureOverlayWindow();
            overlay.SelectionCompleted += (_, selection) => ShowTranslationPanel(selection);
            overlay.Show();
            overlay.Activate();
        });
    }

    private static void ShowTranslationPanel(Rect selection)
    {
        var panel = new TranslationPanelWindow(selection);
        panel.Show();
        _ = panel.RunSafePreviewAsync();
    }

    private void ShowSettings()
    {
        _settingsWindow?.Show();
        _settingsWindow?.Activate();
    }

    private void ExitApplication()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (_settingsWindow is not null)
        {
            _settingsWindow.AllowClose = true;
            _settingsWindow.Close();
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        base.OnExit(e);
    }
}
