using System.IO;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static void ProductQuickMenuClosesOnForeignDeactivation()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        QuickSearchWindow? window = null;
        try
        {
            window = new QuickSearchWindow(new HistoryStore(Path.Combine(directory, "history.json")),
                new VocabularyStore(Path.Combine(directory, "vocabulary.json"))) { ForceClose = true };
            window.Show();
            FlushDispatcher(3);
            var selector = (Button)window.FindName("RouteSelectorButton");
            selector.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FlushDispatcher(2);
            var menu = (ContextMenu?)typeof(QuickSearchWindow).GetField("_routeMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
            True(menu is { IsOpen: true }, "setup: the route menu is open");
            typeof(QuickSearchWindow).GetMethod("Window_Deactivated",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [window, EventArgs.Empty]);
            True(menu!.IsOpen == false,
                "deactivation must close the capture-owned menu instead of shielding the window decision");
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void ProductRouteSnapshotIsCachedPerRevision()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        try
        {
            var first = Services.TextRouteMenu.ReadCurrent();
            var second = Services.TextRouteMenu.ReadCurrent();
            True(ReferenceEquals(first.Profile, second.Profile) && first.Label == second.Label,
                "steady-state repaints reuse the cached route snapshot");
            ProfileManager.BumpRevision();
            var third = Services.TextRouteMenu.ReadCurrent();
            True(first.Profile != third.Profile || first.Label == third.Label,
                "a revision bump re-reads the snapshot source");
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void ProductPanelTranslationToggleKeepsUnrelatedProvenance()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        TranslationPanelWindow? panel = null;
        try
        {
            panel = new TranslationPanelWindow(new Rect(0, 0, 10, 10),
                new HistoryStore(Path.Combine(directory, "history.json")), () => ShellSettings.Default)
            { ShowActivated = false, ShowInTaskbar = false };
            panel.Show();
            FlushDispatcher(2);
            var badge = (TextBlock)panel.FindName("EngineBadge")!;
            badge.Text = "失败时已有的徽标";
            ((RadioButton)panel.FindName("ShowTranslationChoice")!).RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            FlushDispatcher(2);
            Equal("失败时已有的徽标", badge.Text,
                "a translation toggle on a panel that never held a summary must not wipe the badge");
        }
        finally
        {
            panel?.Close();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void ProductDirectAddDiscardReturnsToSourcePicker()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var section = new ServicesSection();
        var host = new Window { Content = section, Width = 960, Height = 700,
            ShowActivated = false, Left = -12000, Top = -12000 };
        try
        {
            host.Show();
            FlushDispatcher(2);
            section.BeginAddEngineFlow();
            typeof(ServicesSection).GetMethod("ChatGptSource_Click",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(section, [section, new RoutedEventArgs()]);
            typeof(ServicesSection).GetMethod("MarkEditorDirty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(section, null);
            section.ReloadEditorFromSaved();
            Equal(Visibility.Visible, section.PresetsPanel.Visibility,
                "discarding an add-mode connection draft returns to the source picker");
            Equal(Visibility.Collapsed, section.CliConnectionsPanel.Visibility,
                "the stale connection editor must not survive the discard");
        }
        finally
        {
            host.Close();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
