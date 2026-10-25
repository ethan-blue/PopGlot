using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using PopGlot.Windows.Services;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static ShellSettings RemappedInterfaceSettings() => ShellSettings.Default with
    {
        InterfaceModelHotkey = new(HotkeyBinding.ModAlt, 0x4D),
        InterfaceStyleHotkey = new(HotkeyBinding.ModAlt, 0x54),
        InterfaceSwapHotkey = new(HotkeyBinding.ModAlt, 0x4C),
        InterfaceWorkbenchHotkey = new(HotkeyBinding.ModAlt, 0x31),
        InterfaceLibraryHotkey = new(HotkeyBinding.ModAlt, 0x32),
        InterfaceSettingsHotkey = new(HotkeyBinding.ModAlt, 0xBC),
        InterfaceTranslateHotkey = new(HotkeyBinding.ModAlt, 0x0D),
        InterfaceSpeakHotkey = new(HotkeyBinding.ModAlt, 0x50),
    };

    private static void InterfaceShortcutsPersistAndRejectConflicts()
    {
        var directory = Path.Combine(TestIsolation.Root, $"interface-shortcuts-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = RemappedInterfaceSettings();
            ShellSettingsStore.Save(settings, path);
            ShellSettingsStore.InvalidateCache();
            Equal(settings, ShellSettingsStore.Load(path), "every interface binding must survive disk reload");
            var originalBytes = File.ReadAllBytes(path);
            var invalidSets = new List<ShellSettings>
            {
                settings with { InterfaceModelHotkey = settings.InterfaceLibraryHotkey },
                settings with { InterfaceWorkbenchHotkey = settings.SelectionHotkey },
                ShellSettings.Default with { InterfaceModelHotkey = HotkeyBinding.InterfaceWorkbenchDefault },
            };
            foreach (var combination in new[]
            {
                "Ctrl+R", "Ctrl+S", "Ctrl+Shift+C", "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X",
                "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+Left", "Ctrl+Shift+Right", "Ctrl+Back",
                "Ctrl+Delete", "Ctrl+Home", "Ctrl+End", "Ctrl+Insert", "Ctrl+Esc", "Ctrl+Space",
                "Ctrl+B", "Ctrl+I", "Ctrl+U", "Alt+Back", "Alt+Shift+Back",
            })
                invalidSets.Add(settings with
                {
                    InterfaceTranslateHotkey = HotkeyBinding.Parse(combination, HotkeyBinding.InterfaceTranslateDefault),
                });
            foreach (var invalid in invalidSets)
            {
                True(invalid.ValidateHotkeys() is not null, "conflicting bindings must fail form validation");
                var rejected = false;
                try { ShellSettingsStore.Save(invalid, path); }
                catch (InvalidOperationException) { rejected = true; }
                True(rejected, "the persistence boundary must reject a conflicting binding");
                True(originalBytes.SequenceEqual(File.ReadAllBytes(path)), "rejection must happen before writing");
                True(!File.Exists(path + ".tmp"), "rejection must not leave a temporary settings file");
            }
            File.WriteAllText(path, "{\"SchemaVersion\":3}");
            ShellSettingsStore.InvalidateCache();
            var upgraded = ShellSettingsStore.Load(path);
            Equal(HotkeyBinding.InterfaceWorkbenchDefault, upgraded.InterfaceWorkbenchHotkey);
            Equal(HotkeyBinding.InterfaceLibraryDefault, upgraded.InterfaceLibraryHotkey);
            Equal(HotkeyBinding.InterfaceSettingsDefault, upgraded.InterfaceSettingsHotkey);
            Equal(HotkeyBinding.InterfaceTranslateDefault, upgraded.InterfaceTranslateHotkey);
            Equal(HotkeyBinding.InterfaceSpeakDefault, upgraded.InterfaceSpeakHotkey);
        }
        finally
        {
            ShellSettingsStore.InvalidateCache();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void RemappedShortcutsDispatchAndKeepDefaultsInert()
    {
        EnsureApplication();
        var previous = InterfaceKeys.Snapshot;
        var directory = Path.Combine(TestIsolation.Root, $"shortcut-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        MainWindow? window = null;
        try
        {
            var settings = RemappedInterfaceSettings();
            var path = Path.Combine(directory, "settings.json");
            ShellSettingsStore.Save(settings, path);
            ShellSettingsStore.InvalidateCache();
            InterfaceKeys.Apply(ShellSettingsStore.Load(path));
            window = new MainWindow(settings, new HistoryStore(Path.Combine(directory, "history.json")))
            {
                AllowClose = true,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            var openedSettings = 0;
            window.OpenSettings = _ => openedSettings++;
            window.Show();
            window.Dispatcher.Invoke(() =>
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                foreach (var (key, action) in new[]
                {
                    (Key.M, InterfaceAction.Model), (Key.T, InterfaceAction.Style), (Key.L, InterfaceAction.Swap),
                    (Key.D1, InterfaceAction.Workbench), (Key.D2, InterfaceAction.Library),
                    (Key.OemComma, InterfaceAction.Settings), (Key.Enter, InterfaceAction.Translate), (Key.P, InterfaceAction.Speak),
                })
                {
                    Equal(action, InterfaceKeys.Resolve(key, ModifierKeys.Alt));
                    Equal(InterfaceAction.None, InterfaceKeys.Resolve(key, ModifierKeys.Control), "the old default must be inert");
                    Equal(InterfaceAction.None, InterfaceKeys.Resolve(key, ModifierKeys.Alt | ModifierKeys.Shift),
                        "an unspecified extra modifier must not trigger an action");
                    Equal(InterfaceAction.None, InterfaceKeys.Resolve(key, ModifierKeys.None), "ordinary typing stays native");
                }
                True(window.DispatchShortcut(Key.D2, ModifierKeys.Alt), "saved library shortcut must dispatch");
                Equal(Visibility.Visible, window.LibrarySection.Visibility);
                True(window.DispatchShortcut(Key.D1, ModifierKeys.Alt), "saved workbench shortcut must dispatch");
                Equal(Visibility.Visible, window.TranslateSection.Visibility);
                True(!window.DispatchShortcut(Key.D2, ModifierKeys.Control), "old navigation must no longer dispatch");
                Equal(Visibility.Visible, window.TranslateSection.Visibility);
                True(window.DispatchShortcut(Key.OemComma, ModifierKeys.Alt), "saved settings shortcut must dispatch");
                Equal(1, openedSettings);
                True(!window.DispatchShortcut(Key.OemComma, ModifierKeys.Control), "old settings shortcut must be inert");
                Equal(1, openedSettings);

                var source = PresentationSource.FromVisual(window)
                    ?? throw new InvalidOperationException("shown shortcut test window requires a presentation source");
                var altKey = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.OemComma);
                typeof(KeyEventArgs).GetMethod("MarkSystem", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(altKey, null);
                Equal(Key.System, altKey.Key);
                True(window.DispatchShortcut(InterfaceKeys.Normalize(altKey), ModifierKeys.Alt),
                    "WPF SystemKey must normalize to the saved Alt combination");
                Equal(2, openedSettings);

                InterfaceKeys.Apply(settings with { InterfaceSettingsHotkey = new(HotkeyBinding.ModControl, 0x31) });
                True(window.DispatchShortcut(Key.D1, ModifierKeys.Control), "a remap may take over a former navigation default");
                Equal(3, openedSettings, "the saved action must win over old navigation dispatch");
                InterfaceKeys.Apply(settings); // Rollback restores the previously applied snapshot without disk access.
                True(!window.DispatchShortcut(Key.D1, ModifierKeys.Control));
                True(window.DispatchShortcut(Key.OemComma, ModifierKeys.Alt));
                Equal(4, openedSettings);
            });
        }
        finally
        {
            window?.Close();
            InterfaceKeys.Apply(previous);
            ShellSettingsStore.InvalidateCache();
            Directory.Delete(directory, true);
        }
    }

    private static void ShortcutRowsStayReadableAcrossSettingsResize()
    {
        EnsureApplication();
        var path = Path.Combine(TestIsolation.Root, $"shortcut-layout-{Guid.NewGuid():N}.json");
        var settings = ShellSettings.Default with
        {
            SelectionHotkey = new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt | HotkeyBinding.ModShift | HotkeyBinding.ModWin, 0xA6),
        };
        var window = new SettingsWindow(settings, new HistoryStore(path))
        {
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        try
        {
            window.Show();
            window.ShowPage("Shortcuts");
            var section = window.ShortcutsSection;
            var normalWidth = window.Width;
            var normalHeight = window.Height;
            // Verify that the default fool-proof view (common shortcuts only) fits without any scrolling
            FlushDispatcher(2);
            window.UpdateLayout();
            True(window.SettingsScroll.ScrollableHeight < 1, "default common shortcuts fit the settings viewport without scrolling");

            // Expand more shortcuts to test all 13 bindings across sizes
            section.MoreExpander.IsExpanded = true;
            FlushDispatcher(2);
            window.UpdateLayout();
            var recorders = FindLogicalDescendants<HotkeyRecorder>(section).ToArray();
            var bindings = recorders.Select(recorder => recorder.BindingValue).ToArray();
            foreach (var (width, height) in new[]
            {
                (normalWidth, normalHeight),
                (window.MinWidth, window.MinHeight),
                (normalWidth, normalHeight),
            })
            {
                window.Width = width;
                window.Height = height;
                FlushDispatcher(2);
                window.UpdateLayout();
                True(section.SelectionHotkey.IsVisible, "shortcut recording stays available after resizing");
                for (var index = 0; index < recorders.Length; index++)
                {
                    var recorder = recorders[index];
                    var row = (Grid)recorder.Parent;
                    var label = row.Children.OfType<TextBlock>().Single();
                    var labelBounds = label.TransformToAncestor(row).TransformBounds(new Rect(label.RenderSize));
                    var keyBounds = recorder.TransformToAncestor(row).TransformBounds(new Rect(recorder.RenderSize));
                    True(labelBounds.Right <= keyBounds.Left + 0.5, "shortcut label cannot cover its recorder");
                    var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                        new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                        label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                    True(label.ActualWidth + 0.5 >= text.Width, "the action name must fit without ellipsis");
                    Equal(bindings[index], recorder.BindingValue, "resizing cannot change a shortcut");
                }
                if (width == normalWidth && !section.MoreExpander.IsExpanded)
                    True(window.SettingsScroll.ScrollableHeight < 1, "default shortcuts fit the settings viewport");
            }
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
