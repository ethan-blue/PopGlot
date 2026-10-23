using System.IO;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;
using System.Windows.Input;

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

    private static void ProductEngineSourceSelectionRequiresConnect()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var section = new ServicesSection();
        try
        {
            section.BeginAddEngineFlow();
            section.CliToolsListBox.ItemsSource = new[]
            {
                new ServicesSection.CliToolRow { ToolId = CliDiscovery.CodexToolId, ToolName = "Codex", EntryPath = "missing-consent-fixture.exe" },
            };
            section.CliToolsListBox.SelectedIndex = 0;
            Equal(Visibility.Visible, section.CliConnectionsPanel.Visibility);
            True(section.CliConnectButton.IsEnabled && section.CliCancelButton.Visibility == Visibility.Collapsed,
                "selecting a discovered executable must not start it before explicit connect");
            True(!section.IsEditorDirty, "choosing a source establishes a clean editor, like an API preset");
            section.BeginDraftGuard("return", section.ShowOverview);
            True(!section.IsEditorOpen, "a source selection without connection must return without an unsaved guard");
        }
        finally
        {
            ProfileManager.ResetForTests();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void ProductEngineCombosAndSourceListForwardClosedWheel()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var section = new ServicesSection();
        var host = new Window { Content = section, Width = 960, Height = 520, ShowActivated = false, Left = -12000, Top = -12000 };
        try
        {
            host.Show();
            section.BeginAddEngineFlow();
            section.CliToolsListBox.ItemsSource = new[]
            {
                new ServicesSection.CliToolRow { ToolId = CliDiscovery.CodexToolId, ToolName = "Codex", EntryPath = "wheel-fixture.exe" },
            };
            FlushDispatcher(3);
            void Wheel(UIElement target) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            section.EditorScroll.ScrollToTop();
            FlushDispatcher(2);
            Wheel(section.CliToolsListBox);
            FlushDispatcher(2);
            True(section.EditorScroll.VerticalOffset > 0,
                "the source ListBox must forward wheel input despite its internal disabled ScrollViewer");
            section.CliToolsListBox.SelectedIndex = 0;
            section.CliToolConnectionPanel.MinHeight = 900;
            section.CliModelCombo.ItemsSource = Enumerable.Range(0, 100).Select(i => new CliModel($"m{i}", $"model {i}")).ToArray();
            section.CliModelCombo.SelectedIndex = 0;
            FlushDispatcher(2);
            section.EditorScroll.ScrollToTop();
            FlushDispatcher(2);
            Wheel(section.CliModelCombo);
            FlushDispatcher(2);
            Equal(0, section.CliModelCombo.SelectedIndex, "a closed combo must not change the saved model through wheel input");
            True(section.EditorScroll.VerticalOffset > 0, "a closed model combo scrolls the editor");
            section.EditorScroll.ScrollToTop();
            FlushDispatcher(2);
            section.CliModelCombo.IsDropDownOpen = true;
            FlushDispatcher(3);
            var dropdown = (ScrollViewer)section.CliModelCombo.Template.FindName("DropDownScrollViewer", section.CliModelCombo);
            var outerOffset = section.EditorScroll.VerticalOffset;
            Wheel(section.CliModelCombo);
            FlushDispatcher(2);
            Equal(outerOffset, section.EditorScroll.VerticalOffset, "an open model popup owns wheel scrolling");
            True(dropdown.VerticalOffset > 0, "the open popup actually scrolls its model list");
        }
        finally
        {
            host.Close();
            ProfileManager.ResetForTests();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void ProductOmpAuthenticatedModelsKeepNewLoginReachable()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var section = new ServicesSection();
        try
        {
            OmpRpcBackend.LoginProvider[] providers =
            [
                new("signed-in", "Existing", true, true),
                new("signed-out", "Other", true, false),
                new("unavailable", "Unavailable", false, false),
            ];
            CliModel[] models = [new("same-id", "Existing", Provider: "signed-in"),
                new("same-id", "Other", Provider: "signed-out"), new("unknown", "Unknown", Provider: "missing")];
            var authenticated = ServicesSection.AuthenticatedOmpModels(models, providers);
            Equal(1, authenticated.Count, "models require explicit provider authentication, not merely catalog presence");
            Equal("signed-in", authenticated[0].Provider!);
            section.SetOmpProviders(providers);
            Equal(1, section.OmpLoginProviderCombo.Items.Count);
            True(section.OmpAddProviderButton.IsEnabled, "filtering logged-in accounts must preserve a discoverable second-login action");
            section.OmpAddProviderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, section.OmpLoginProviderCombo.Items.Count);
            Equal("signed-out", ((OmpRpcBackend.LoginProvider)section.OmpLoginProviderCombo.SelectedItem).Id);
            section.SetOmpProviders([new("signed-out", "Other", true, false)]);
            Equal(0, ServicesSection.AuthenticatedOmpModels(models, [new("signed-out", "Other", true, false)]).Count);
            Equal("signed-out", ((OmpRpcBackend.LoginProvider)section.OmpLoginProviderCombo.SelectedItem).Id,
                "first login stays reachable when no provider is authenticated");
        }
        finally
        {
            section.OmpLoginProviderCombo.IsDropDownOpen = false;
            ProfileManager.ResetForTests();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task ProductSavedDirectListTestStaleAndCancel()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var section = new ServicesSection();
        var host = new Window { Content = section, Width = 960, Height = 700, ShowActivated = false, Left = -12000, Top = -12000 };
        try
        {
            host.Show();
            foreach (var cancel in new[] { false, true })
            {
                var profile = new ProviderProfile { Id = "direct-list-fixture", Name = "Direct fixture",
                    ExecutionKind = EngineExecutionKindNames.ChatGptDirect, TextModel = "saved-model",
                    DirectConnection = new() { AccountId = "isolated-fixture-account", ModelId = "saved-model", Enabled = true } };
                ProfileManager.Save(new CoreProductConfig { Profiles = [profile] });
                var pending = new TaskCompletionSource<TranslationResponse>();
                var sends = 0;
                CancellationToken requestToken = default;
                section.SavedConnectionTestExecutor = (captured, ct) =>
                {
                    sends++;
                    Equal("saved-model", captured.DirectConnection!.ModelId);
                    requestToken = ct;
                    return pending.Task;
                };
                var button = new Button { Tag = profile.Id };
                void Test() => typeof(ServicesSection).GetMethod("TestProfile_Click",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(section, [button, new RoutedEventArgs()]);
                Test();
                Test();
                Equal(1, sends, "list tests must use the saved direct connection once, without navigating to an editor");
                True(!section.IsEditorOpen && !button.IsEnabled, "a list test stays on the list while its request is pending");
                if (cancel)
                {
                    section.Visibility = Visibility.Collapsed;
                    True(requestToken.IsCancellationRequested, "leaving the surface cancels its pending request");
                }
                else
                {
                    var changed = ProfileManager.Load();
                    changed.Profiles[0].DirectConnection!.ModelId = "changed-model";
                    changed.Profiles[0].TextModel = "changed-model";
                    ProfileManager.Save(changed);
                }
                pending.SetResult(new TranslationResponse(new TranslationResult("你好", "", "", [], []),
                    new ProviderDiagnostics("direct-list", ProviderType.OpenAiResponses, "isolated-fixture", 1, 200, 1)));
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!button.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(1);
                True(ProfileManager.Load().Profiles[0].LastTestOutcome is null,
                    "stale or cancelled direct list results must not write success evidence");
                True(button.IsEnabled, "the completed list action becomes retryable");
            }
        }
        finally
        {
            host.Close();
            ProfileManager.ResetForTests();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
