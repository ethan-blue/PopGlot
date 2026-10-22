using System.IO;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static void ProductWorkbenchEditsInvalidateResult()
    {
        EnsureApplication();
        var (section, dir) = NewIsolatedTranslateSection();
        try
        {
            section.FocusTranslate("original source", "zh-CN", "en", "已完成的译文", TranslationSessionState.Completed);
            Equal(TranslateUiPhase.Completed, section.CurrentState.Phase);
            True(section.StarButton.IsEnabled, "a completed restored translation can be saved");
            var priorEpoch = section.CurrentEpoch;
            section.InputBox.Text = "different source";
            True(section.CurrentEpoch > priorEpoch, "editing source must invalidate old callbacks");
            Equal(string.Empty, section.CurrentState.FinalText);
            Equal(string.Empty, section.ResultBox.Text);
            True(!section.StarButton.IsEnabled, "old translation must not be saved under new source");
        }
        finally
        {
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void ProductWorkbenchPartialAllowsManualCopyOnly()
    {
        EnsureApplication();
        var (section, dir) = NewIsolatedTranslateSection();
        try
        {
            section.FocusTranslate("original source", "zh-CN", "en", "已收到的部分译文", TranslationSessionState.Cancelled);
            Equal(TranslateUiPhase.Partial, section.CurrentState.Phase);
            True(((Button)section.FindName("TranslateResultCopyButton")).IsEnabled,
                "cancelled visible text remains manually copyable");
            True(!((Button)section.FindName("TranslateResultSpeakButton")).IsEnabled && !section.StarButton.IsEnabled,
                "copy access must not open speech or vocabulary side effects for partial text");
        }
        finally
        {
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void ProductFailedSaveRestoresSavedQuickSearchHotkey()
    {
        EnsureApplication();
        var original = ShellSettingsStore.Load();
        var saved = ShellSettings.Default with { QuickSearchHotkey = HotkeyBinding.Parse("Ctrl+Alt+H", HotkeyBinding.QuickSearchDefault) };
        var candidate = HotkeyBinding.Parse("Ctrl+Shift+Y", HotkeyBinding.QuickSearchDefault);
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        SettingsWindow? window = null;
        FileStream? locked = null;
        var registrations = new List<HotkeyBinding>();
        try
        {
            ShellSettingsStore.Save(saved);
            window = new SettingsWindow(saved, new HistoryStore(Path.Combine(dir, "history.json")))
            {
                ForceClose = true,
                ApplyShellSettings = settings =>
                {
                    registrations.Add(settings.QuickSearchHotkey);
                    if (registrations.Count == 1)
                        locked = new FileStream(StoragePaths.ShellSettings, FileMode.Open, FileAccess.Read, FileShare.None);
                    return ShellApplyOutcome.Ok();
                },
            };
            window.ShortcutsSection.QuickSearchHotkey.BindingValue = candidate;
            window.GeneralSection.AutoCopy.IsChecked = !saved.CopyTranslationAutomatically;
            Equal(SettingsEditState.Dirty, window.EditState, "the save scenario requires an edited form");
            typeof(SettingsWindow).GetMethod("Save_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            try
            {
                SpinUntil(() => window.EditState == SettingsEditState.Dirty && registrations.Count == 2,
                    "a blocked shell write must return to a retryable draft after rolling back hotkeys");
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidOperationException(
                    $"Rollback did not settle: state={window.EditState}, registrations={registrations.Count}, status={window.StatusTextBlock.Text}", exception);
            }
            Equal(candidate, registrations[0]);
            Equal(saved.QuickSearchHotkey, registrations[1],
                "rollback must restore the saved shortcut, not the recorder's unsaved candidate");
            locked!.Dispose();
            locked = null;
            Equal(saved.QuickSearchHotkey, ShellSettingsStore.Load().QuickSearchHotkey);
        }
        finally
        {
            locked?.Dispose();
            window?.Close();
            ShellSettingsStore.Save(original);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void ProductEmptyWorkbenchActionsFitMinimumWindow()
    {
        EnsureApplication();
        var root = Path.Combine(Path.GetTempPath(), $"popglot-empty-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var shell = ShellSettingsStore.Load();
        MainWindow? window = null;
        try
        {
            ProfileManager.ResetForTests();
            ProfileManager.ConfigPathOverride = Path.Combine(root, "product-config.json");
            ProfileManager.Save(new CoreProductConfig { Profiles = [] });
            ShellSettingsStore.Update(current => current with
            {
                HasCompletedOnboarding = true,
                FreeEngineConsent = FreeEngineConsent.Denied,
            });
            window = new MainWindow(ShellSettingsStore.Load(), new HistoryStore(Path.Combine(root, "history.json")))
            {
                AllowClose = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -12000,
                Top = -12000,
            };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            FlushDispatcher(2);
            window.UpdateLayout();
            var section = window.TranslateSection;
            var action = (Button)section.FindName("GoToSettingsButton");
            var bounds = action.TransformToAncestor(section.ResultScroll).TransformBounds(new Rect(action.RenderSize));
            True(action.IsVisible && action.IsEnabled && action.ActualHeight > 0,
                "first-use setup must remain actionable in the minimum workbench");
            True(bounds.Top >= 0 && bounds.Bottom <= section.ResultScroll.ActualHeight + 0.5 &&
                bounds.Left >= 0 && bounds.Right <= section.ResultScroll.ActualWidth + 0.5,
                "the setup action must fit the visible viewport without requiring a hidden initial scroll");
        }
        finally
        {
            window?.Close();
            ShellSettingsStore.Save(shell);
            ProfileManager.ResetForTests();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
