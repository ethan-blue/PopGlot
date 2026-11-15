using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static void ConfirmButtonArmedDisarmEscAndTimeout()
    {
        EnsureApplication();
        var fired = 0;
        var button = new Button { Content = "删除", Width = 80, Padding = new Thickness(12, 6, 12, 6) };
        var confirm = ConfirmButton.Attach(button, "确认删除？", () => fired++);

        var window = new Window { Content = button, Width = 300, Height = 200, ShowInTaskbar = false };
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-confirm-test") { Width = 8, Height = 8 });
        try
        {
            window.Show();
            FlushDispatcher(2);

            // 1. Initial state
            True(!confirm.IsArmed, "button starts disarmed");
            Equal("删除", button.Content as string);

            // 2. First click arms
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(confirm.IsArmed, "first click arms confirm button");
            Equal("确认删除？", button.Content as string);
            Equal(0, fired, "first click does not fire action");

            // 3. Esc disarms
            var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent,
            };
            button.RaiseEvent(esc);
            FlushDispatcher(2);
            True(!confirm.IsArmed, "Esc disarms armed confirm button");
            Equal("删除", button.Content as string);
            True(esc.Handled, "Esc was handled by confirm button");

            // 4. LostKeyboardFocus must NOT accidentally cancel double confirm
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(confirm.IsArmed, "click re-arms");
            button.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, button, null)
            {
                RoutedEvent = UIElement.LostKeyboardFocusEvent,
            });
            FlushDispatcher(2);
            True(confirm.IsArmed, "losing keyboard focus must NOT disarm confirm button (preserves double click)");
            Equal("确认删除？", button.Content as string);

            // 5. Arming another button disarms previous
            var button2 = new Button { Content = "清空" };
            var confirm2 = ConfirmButton.Attach(button2, "确认清空？", () => { });
            button2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(!confirm.IsArmed, "arming button 2 disarms button 1");
            True(confirm2.IsArmed, "button 2 is armed");

            // 6. ConfirmButton.DisarmCurrent() disarms button 2
            ConfirmButton.DisarmCurrent();
            FlushDispatcher(2);
            True(!confirm2.IsArmed, "DisarmCurrent disarms active button");

            // 7. Two clicks in sequence fires action
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(confirm.IsArmed, "first click arms");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(!confirm.IsArmed, "second click disarms");
            Equal(1, fired, "second click fires action");
            Equal("删除", button.Content as string);

            // 8. 3-second timeout automatically disarms
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(2);
            True(confirm.IsArmed, "button is re-armed for timeout test");
            Equal("确认删除？", button.Content as string);
            SpinUntil(() => !confirm.IsArmed, "confirm button must automatically disarm after 3s timeout");
            True(!confirm.IsArmed, "confirm button is disarmed after 3s timeout");
            Equal("删除", button.Content as string, "original caption restored after timeout");
        }
        finally
        {
            try { source.Dispose(); } catch { }
            try { window.Close(); } catch { }
            FlushDispatcher(2);
        }
    }

    private static void ConfirmButtonPreservesPropertySemantics()
    {
        EnsureApplication();

        // Case 1: Button with local Padding="12,6", local Width=80, local MinWidth=40
        var b1 = new Button
        {
            Content = "删除",
            Width = 80,
            MinWidth = 40,
            Padding = new Thickness(12, 6, 12, 6),
        };
        var c1 = ConfirmButton.Attach(b1, "确认删除？", () => { });
        b1.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        FlushDispatcher(2);
        True(c1.IsArmed);
        Equal("确认删除？", b1.Content as string);
        c1.Disarm();
        FlushDispatcher(2);
        True(!c1.IsArmed);
        Equal(80.0, b1.Width, "local Width 80 preserved");
        Equal(40.0, b1.MinWidth, "local MinWidth 40 preserved");
        Equal(new Thickness(12, 6, 12, 6), b1.Padding, "local Padding (12,6,12,6) preserved");
        Equal("删除", b1.Content as string, "local Content preserved");

        // Case 2: Button with narrow local padding (2,2)
        var b2 = new Button { Content = "图标", Padding = new Thickness(2, 2, 2, 2) };
        var c2 = ConfirmButton.Attach(b2, "确认？", () => { });
        b2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        FlushDispatcher(2);
        True(c2.IsArmed);
        Equal(new Thickness(10, 0, 10, 0), b2.Padding, "narrow padding expanded during arm");
        c2.Disarm();
        FlushDispatcher(2);
        Equal(new Thickness(2, 2, 2, 2), b2.Padding, "original local narrow padding restored");

        // Case 3: Button with no local padding (style / unsetValue)
        var b3 = new Button { Content = "样式按钮" };
        var c3 = ConfirmButton.Attach(b3, "确认？", () => { });
        b3.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        FlushDispatcher(2);
        True(c3.IsArmed);
        c3.Disarm();
        FlushDispatcher(2);
        Equal(DependencyProperty.UnsetValue, b3.ReadLocalValue(Control.PaddingProperty),
            "unvalued padding returns to UnsetValue instead of getting polluted with local value");
        Equal(DependencyProperty.UnsetValue, b3.ReadLocalValue(FrameworkElement.WidthProperty),
            "unvalued width returns to UnsetValue");

        // Case 4: Button with Binding on WidthProperty
        var b4 = new Button();
        var binding = new Binding("Tag") { Source = b4 };
        BindingOperations.SetBinding(b4, FrameworkElement.WidthProperty, binding);
        True(BindingOperations.GetBindingBase(b4, FrameworkElement.WidthProperty) != null, "binding is attached");
        var c4 = ConfirmButton.Attach(b4, "确认？", () => { });
        b4.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        FlushDispatcher(2);
        c4.Disarm();
        FlushDispatcher(2);
        True(BindingOperations.GetBindingBase(b4, FrameworkElement.WidthProperty) != null,
            "binding is restored after disarm");
    }

    private static void LibrarySectionModeSwitchDisarmsConfirm()
    {
        EnsureApplication();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-lib-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var history = new HistoryStore(Path.Combine(dir, "history.json"));
            history.TryAdd(Entry("h1", "译文1"), enabled: true);
            history.TryAdd(Entry("h2", "译文2"), enabled: true);

            var vocab = new VocabularyStore(Path.Combine(dir, "vocabulary.json"));
            vocab.ToggleStar("v1", "生词1", "", "", "en", "zh-CN");
            vocab.ToggleStar("v2", "生词2", "", "", "en", "zh-CN");

            var section = new LibrarySection();
            section.Initialize(history, vocab);
            section.ReloadHistory();

            var window = new Window { Content = section, Width = 980, Height = 700, ShowInTaskbar = false };
            try
            {
                window.Show();
                FlushDispatcher(3);

                // 1. In History mode, arm the clear button
                section.ClearCurrentButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushDispatcher(2);
                Equal("确认清空？", section.ClearCurrentButton.Content as string);

                // 2. Switch to Vocabulary mode
                section.ModeVocabulary.IsChecked = true;
                FlushDispatcher(3);

                // 3. Confirm button must be disarmed
                True(section.ClearCurrentButton.Content is null,
                    "switching mode disarms confirm button (no armed caption)");

                // 4. Clicking ClearCurrentButton once must ONLY arm for vocabulary, NOT wipe vocabulary!
                section.ClearCurrentButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                FlushDispatcher(2);
                Equal(2, vocab.GetAll().Count, "first click in vocabulary mode must not wipe vocabulary");
                Equal("确认清空？", section.ClearCurrentButton.Content as string, "it only armed for vocabulary");

                // 5. Switching back to History disarms again
                section.ModeHistory.IsChecked = true;
                FlushDispatcher(3);
                True(section.ClearCurrentButton.Content is null,
                    "switching back to history disarms confirm button again");
                Equal(2, history.Load().Count, "history is untouched");
            }
            finally
            {
                try { window.Close(); } catch { }
                FlushDispatcher(2);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void PrivacySectionFreeEngineGatingAndStateRefresh()
    {
        EnsureApplication();
        var originalShell = ShellSettingsStore.Load();
        try
        {
            var section = new PrivacySection();
            var window = new Window { Content = section, Width = 800, Height = 600, ShowInTaskbar = false };
            try
            {
                window.Show();
                FlushDispatcher(2);

                // 1. SafeMode gating on FreeEngineConsentToggle
                section.SafeMode.IsChecked = true;
                section.UpdateSafeModeGating();
                True(!((ToggleButton)section.FindName("FreeEngineConsentToggle")).IsEnabled,
                    "Safe mode disables FreeEngineConsentToggle");

                section.SafeMode.IsChecked = false;
                section.UpdateSafeModeGating();
                True(((ToggleButton)section.FindName("FreeEngineConsentToggle")).IsEnabled,
                    "Safe mode off re-enables FreeEngineConsentToggle");

                // 2. RefreshFreeEngineState sets toggle and status text accurately
                var freeToggle = (ToggleButton)section.FindName("FreeEngineConsentToggle");
                var freeStateText = (TextBlock)section.FindName("FreeEngineConsentState");

                section.SetShellSettings(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied });
                section.RefreshFreeEngineState();
                Equal(false, freeToggle.IsChecked, "RefreshFreeEngineState loads Denied as false");
                True(freeStateText.Text.Contains("已关闭"), "FreeEngineConsentState indicates closed");

                section.SetShellSettings(ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed });
                section.RefreshFreeEngineState();
                Equal(true, freeToggle.IsChecked, "RefreshFreeEngineState loads Allowed as true");
                True(freeStateText.Text.Contains("已开启"), "FreeEngineConsentState indicates open");

                // 3. RefreshCloudSpeechState does NOT overwrite FreeEngineConsentToggle
                section.SetShellSettings(ShellSettings.Default with
                {
                    FreeEngineConsent = FreeEngineConsent.Allowed,
                    CloudSpeechEnabled = false
                });
                section.RefreshFreeEngineState();
                Equal(true, freeToggle.IsChecked);

                section.SetShellSettings(ShellSettings.Default with
                {
                    FreeEngineConsent = FreeEngineConsent.Denied,
                    CloudSpeechEnabled = true
                });
                section.RefreshCloudSpeechState();
                Equal(true, freeToggle.IsChecked,
                    "RefreshCloudSpeechState must not mutate FreeEngineConsentToggle");

                // 4. User interaction toggle updates shell settings and text
                freeToggle.IsChecked = false;
                FlushDispatcher(2);
                Equal(FreeEngineConsent.Denied, ShellSettingsStore.Load().FreeEngineConsent,
                    "Unchecking toggle persists Denied to shell settings");
                freeToggle.IsChecked = true;
                FlushDispatcher(2);
                Equal(FreeEngineConsent.Allowed, ShellSettingsStore.Load().FreeEngineConsent,
                    "Checking toggle persists Allowed to shell settings");
            }
            finally
            {
                try { window.Close(); } catch { }
                FlushDispatcher(2);
            }
        }
        finally
        {
            try
            {
                ShellSettingsStore.Save(originalShell);
                ShellSettingsStore.InvalidateCache();
            }
            catch { }
        }
    }

    private static void ServicesSectionInlineRowDeleteDoubleConfirmWorks()
    {
        EnsureApplication();
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        Window? window = null;
        try
        {
            var suffix = Guid.NewGuid().ToString("N");

            var api1 = ProviderProfile.CreateOpenAi();
            api1.Id = "row-del-1-" + suffix;
            api1.Name = "Row Del Test 1";
            api1.ApiBaseUrl = "http://127.0.0.1:11434/v1";
            api1.TextModel = "fixture";
            api1.CredentialTarget = "";
            api1.SupportsText = true;
            api1.SupportsVision = false;
            ProfileManager.SaveApiProfile(api1, null);

            var api2 = ProviderProfile.CreateOpenAi();
            api2.Id = "row-del-2-" + suffix;
            api2.Name = "Row Del Test 2";
            api2.ApiBaseUrl = "http://127.0.0.1:11434/v1";
            api2.TextModel = "fixture";
            api2.CredentialTarget = "";
            api2.SupportsText = true;
            api2.SupportsVision = false;
            ProfileManager.SaveApiProfile(api2, null);

            var section = new ServicesSection();
            window = new Window { Content = section, Width = 1040, Height = 760, ShowInTaskbar = false };
            window.Show();
            FlushDispatcher(3);
            section.RefreshProfilesList();
            FlushDispatcher(3);
            window.UpdateLayout();

            // Find delete button for api1
            var button1 = FindVisualChildren<Button>(section).FirstOrDefault(candidate =>
                candidate.Tag?.ToString() == api1.Id && AutomationProperties.GetName(candidate) == "删除引擎");
            True(button1 is not null, "row delete button for api1 must exist");
            if (!button1!.IsLoaded)
            {
                button1.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                FlushDispatcher(2);
            }

            // First click on row 1 delete button arms it
            button1.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FlushDispatcher(2);
            Equal("确认删除？", button1.Content as string, "first click arms row delete button");
            True(ProfileManager.Load().Profiles.Any(p => p.Id == api1.Id), "first click must not delete profile");

            // Switching to another profile or selecting another item disarms it
            var button2 = FindVisualChildren<Button>(section).FirstOrDefault(candidate =>
                candidate.Tag?.ToString() == api2.Id && AutomationProperties.GetName(candidate) == "删除引擎");
            True(button2 is not null, "row delete button for api2 must exist");
            if (!button2!.IsLoaded)
            {
                button2.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                FlushDispatcher(2);
            }

            // Click row 2 delete button: should disarm row 1 and arm row 2
            button2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FlushDispatcher(2);
            True(button1.Content is not string s || s != "确认删除？", "arming row 2 disarms row 1 delete button");
            Equal("确认删除？", button2.Content as string, "row 2 delete button is armed");

            // Second click on row 2 delete button must delete api2!
            button2.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            FlushDispatcher(3);
            SpinUntil(() => ProfileManager.Load().Profiles.All(p => p.Id != api2.Id),
                "second click on row delete button must delete profile");
            FlushDispatcher(3);
        }
        finally
        {
            try { window?.Close(); } catch { }
            FlushDispatcher(2);
            try { ProfileManager.SaveAsync(originalConfig).GetAwaiter().GetResult(); } catch { }
            try { CoreBridge.SaveSettings(originalSettings); } catch { }
        }
    }
}
