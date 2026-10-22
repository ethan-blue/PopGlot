using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PopGlot.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.CliTests;

internal static class UiScenario
{
    public static void Run(string root)
    {
        Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", "codex");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = System.Xml.Linq.XDocument.Load("apps/PopGlot.Windows/App.xaml");
        var resources = document.Root!.Elements().Single().Elements().Single();
        foreach (var element in resources.Descendants())
            if (element.Attribute("Source") is { } source && source.Value == "Themes/Controls.xaml")
                source.Value = "/PopGlot;component/Themes/Controls.xaml";
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        Console.WriteLine("UI resources ready");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var section = new ServicesSection();
        section.DiscoverCliTools = (_, _) => [];
        Console.WriteLine("UI section ready");
        var window = new Window { Content = section, Width = 800, Height = 600, Left = -12000, Top = -12000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = (Brush)app.Resources["CanvasBrush"] };
        try
        {
            window.Show(); Pump(); Console.WriteLine("UI window ready");
            Button Find(string name) => (Button)section.FindName(name);
            void Click(string name) { Find(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            Click("ConnectLocalToolHeaderButton");
            Until(() => Find("DetectCliToolsButton").IsEnabled, "initial discovery completes off the UI thread");
            var list = (ListBox)section.FindName("CliToolsListBox");
            list.ItemsSource = new[] { new ServicesSection.CliToolRow { ToolId = "codex", ToolName = "Codex CLI", EntryPath = Environment.ProcessPath! } };
            list.SelectedIndex = 0;
            Click("CliConnectButton");
            Console.WriteLine("UI connect submitted");
            Until(() => Find("CliTestButton").IsEnabled, "connection should populate models", () =>
                $"status={((TextBlock)section.FindName("CliConnectionStatus")).Text}; " +
                $"models={((ComboBox)section.FindName("CliModelCombo")).Items.Count}; " +
                $"selected={((ComboBox)section.FindName("CliModelCombo")).SelectedItem}; " +
                $"connectEnabled={Find("CliConnectButton").IsEnabled}; cancelVisible={Find("CliCancelButton").Visibility}");
            Program.Assert(((ComboBox)section.FindName("CliModelCombo")).SelectedItem is CliModel { Id: "fixture", Provider: null },
                "Codex restores its saved model by ID; an OMP-only provider field cannot invalidate a Codex catalog match");
            Click("CliTestButton");
            Console.WriteLine("UI test submitted");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("测试通过"), "UI test translation should pass");
            Click("CliSaveUseButton");
            Console.WriteLine("UI save submitted");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("已保存并切换"), "save/use should persist");
            Program.Assert(ProfileManager.Load().TryGetActiveProfile()?.ExecutionFacts.TextCapable == true, "UI saved an executable CLI profile");
            var savedCli = ProfileManager.Load().TryGetActiveProfile()!;
            var profileCount = ProfileManager.Load().Profiles.Count;
            typeof(ServicesSection).GetMethod("OpenProfileInEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(section, new object[] { savedCli.Id });
            Program.Assert(((ComboBox)section.FindName("CliModelCombo")).SelectedItem is CliModel { Id: "fixture" } &&
                !Find("CliSaveUseButton").IsEnabled, "saved CLI model echoes without inventing a live connection");
            Click("DetectCliToolsButton");
            Until(() => Find("DetectCliToolsButton").IsEnabled, "rescan completes");
            Program.Assert(list.SelectedItem is ServicesSection.CliToolRow { StateBadge: "已保存入口" } &&
                ((ComboBox)section.FindName("CliModelCombo")).SelectedItem is CliModel { Id: "fixture" },
                "saved entry outside discovery paths remains selected and its model survives rescan");
            Click("CliConnectButton");
            Until(() => Find("CliSaveUseButton").IsEnabled, "saved CLI reconnects");
            Click("CliSaveUseButton");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("已保存并切换"), "reconnected CLI saves");
            var updatedCli = ProfileManager.Load().Profiles.Single(p => p.Id == savedCli.Id);
            Program.Assert(ProfileManager.Load().Profiles.Count == profileCount && updatedCli.LastTestFingerprint == savedCli.LastTestFingerprint,
                "reconnect/save edits the same profile and retains only unchanged verification evidence");
            var output = Path.GetFullPath("artifacts/cli-repair/connection-panel.png");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(output)) encoder.Save(stream);
            Console.WriteLine($"UI snapshot: {output}");
            Click("CliDisconnectButton");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("已断开"), "disconnect revokes saved connection");
            Program.Assert(!Find("CliSaveUseButton").IsEnabled && ProfileManager.Load().TryGetActiveProfile()?.ExecutionFacts.TextCapable == false,
                "disconnected UI and saved profile must both be disabled");
            var directProfile = DirectTests.UiProfile();
            typeof(ServicesSection).GetMethod("OpenDirectProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(section, new object[] { directProfile });
            Program.Assert(!Find("ChatGptTestButton").IsEnabled && !Find("ChatGptSaveUseButton").IsEnabled,
                "restored direct model is not a fresh account catalog");
            Click("ChatGptRefreshButton");
            Until(() => Find("ChatGptTestButton").IsEnabled, "direct model list should enable testing");
            DirectTests.HoldUiRequest(); Click("ChatGptTestButton");
            Until(() => DirectTests.UiRequestEntered, "direct UI request must reach the pending boundary");
            Program.Assert(Find("ChatGptCancelButton").Visibility == Visibility.Visible, "cancel is visible beside direct login/model actions");
            Click("ChatGptCancelButton");
            Until(() => ((TextBlock)section.FindName("ChatGptStatus")).Text == "操作已取消。" && Find("ChatGptTestButton").IsEnabled,
                "cancel must clear waiting state without saving a success");
            DirectTests.ReleaseUiScenario();
            Click("ChatGptTestButton");
            Until(() => ((TextBlock)section.FindName("ChatGptStatus")).Text.StartsWith("直连测试通过"), "direct UI translation must pass");
            Click("ChatGptSaveUseButton");
            Until(() => ((TextBlock)section.FindName("ChatGptStatus")).Text.StartsWith("已保存并切换"), "direct UI save/use must persist");
            Program.Assert(ProfileManager.Load().TryGetActiveProfile()?.ResolvedExecutionKind == EngineExecutionKind.ChatGptDirect,
                "direct UI must activate the HTTP route");
            window.UpdateLayout();
            var directBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            directBitmap.Render(window); var directEncoder = new PngBitmapEncoder(); directEncoder.Frames.Add(BitmapFrame.Create(directBitmap));
            using (var stream = File.Create(Path.GetFullPath("artifacts/cli-repair/direct-panel.png"))) directEncoder.Save(stream);
            Click("ChatGptDisconnectButton");
            Until(() => ((TextBlock)section.FindName("ChatGptStatus")).Text.StartsWith("已断开"), "direct UI disconnect must complete");
            Program.Assert(ProfileManager.Load().TryGetActiveProfile()?.ExecutionFacts.TextCapable == false, "direct route disabled after disconnect");
            DirectTests.Restore();
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_PROTOCOL", "omp");
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", "login_input");
            list.ItemsSource = new[] { new ServicesSection.CliToolRow { ToolId = "omp", ToolName = "oh-my-pi", EntryPath = Environment.ProcessPath! } };
            list.SelectedIndex = 0; Click("CliConnectButton");
            Until(() => Find("OmpLoginButton").IsEnabled, "OMP login provider list must be populated");
            var browserOpened = false;
            section.OmpOpenBrowser = _ => browserOpened = true;
            Click("OmpLoginButton");
            Until(() => ((StackPanel)section.FindName("OmpCallbackPanel")).Visibility == Visibility.Visible, "OMP callback entry must appear");
            Program.Assert(browserOpened && Find("CliCancelButton").Visibility == Visibility.Visible, "browser authorization and visible cancel");
            ((TextBox)section.FindName("OmpCallbackInput")).Text = "fixture-oauth-code";
            Click("OmpSubmitCallbackButton");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("OMP 已确认登录"), "OMP final login and model refresh must complete");
            Program.Assert(((TextBox)section.FindName("OmpCallbackInput")).Text.Length == 0, "callback input cleared after completion");
            window.UpdateLayout();
            var ompBitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            ompBitmap.Render(window); var ompEncoder = new PngBitmapEncoder(); ompEncoder.Frames.Add(BitmapFrame.Create(ompBitmap));
            using (var stream = File.Create(Path.GetFullPath("artifacts/cli-repair/omp-login-panel.png"))) ompEncoder.Save(stream);
            Click("OmpLoginButton");
            Until(() => ((StackPanel)section.FindName("OmpCallbackPanel")).Visibility == Visibility.Visible, "second login awaits callback");
            Click("CliCancelButton");
            Program.Assert(((StackPanel)section.FindName("OmpCallbackPanel")).Visibility == Visibility.Collapsed && Find("OmpLoginButton").IsEnabled,
                "cancel hides callback and releases UI; old completion cannot overwrite it");
            Environment.SetEnvironmentVariable("POPGLOT_FAKE_SCENARIO", null);
            VerifyLanguageSync(window, root);
            VerifyApiEditor(section);
        }
        finally { DirectTests.Restore(); window.Close(); Pump(); }
    }
    private static void VerifyApiEditor(ServicesSection section)
    {
        section.BeginAddEngineFlow();
        ((Button)section.FindName("CustomPresetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)section.FindName("ServiceNameTextBox")).Text = "API edit regression";
        var address = (TextBox)section.FindName("BaseUrlTextBox");
        address.Text = "http://127.0.0.1:11434/v1";
        var model = (ComboBox)section.FindName("TextModelCombo"); model.Text = "saved-model";
        var pendingCatalog = new TaskCompletionSource<ModelCatalogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        section.ModelCatalogExecutor = (_, _, _) => pendingCatalog.Task;
        ((Button)section.FindName("FetchModelsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        address.Text = "http://127.0.0.1:1234/v1";
        pendingCatalog.SetResult(new(new[] { new ModelDescriptor("old-host-model", CapabilityState.Unknown, CapabilityState.Unknown, "fixture") },
            new Uri("http://127.0.0.1:11434/v1/models"), 1, "fixture"));
        Until(() => ((Button)section.FindName("FetchModelsButton")).IsEnabled, "stale model request releases its busy state");
        Program.Assert(model.Items.Count == 0 && model.Text == "saved-model", "old-address model results cannot populate the new draft");
        var count = ProfileManager.Load().Profiles.Count;
        var firstSave = section.TrySaveServiceAsync();
        var duplicateSave = section.TrySaveServiceAsync();
        Program.Assert(duplicateSave.IsCompletedSuccessfully && !duplicateSave.Result, "double-save is rejected while the first commit is pending");
        Until(() => firstSave.IsCompleted, "API draft commit completes");
        Program.Assert(firstSave.Result && ProfileManager.Load().Profiles.Count == count + 1, "API save commits once without a successful health test");
        var saved = ProfileManager.Load().Profiles.Single(p => p.Name == "API edit regression");
        typeof(ServicesSection).GetMethod("OpenProfileInEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(section, new object[] { saved.Id });
        Program.Assert(address.Text == saved.ApiBaseUrl && model.Text == saved.TextModel &&
            ((StackPanel)section.FindName("BaseUrlPanel")).Visibility == Visibility.Visible, "saved API address and model remain editable");
    }

    private static void VerifyLanguageSync(Window host, string root)
    {
        var original = CoreBridge.GetSettings();
        var workbench = new TranslateSection();
        var panel = new TranslationPanelWindow(new Rect(0, 0, 1, 1),
            new HistoryStore(Path.Combine(root, "language-sync-history.json")), () => ShellSettings.Default)
        { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Content = workbench;
            panel.Show(); Pump();
            var panelSource = (ComboBox)panel.FindName("SourceLangCombo");
            var panelTarget = (ComboBox)panel.FindName("TargetLangCombo");
            panelSource.SelectedItem = LanguageCatalog.ResolveSource("ja");
            panelTarget.SelectedItem = LanguageCatalog.ResolveTarget("en");
            Until(() => CoreBridge.GetSettings().TargetLanguage == "en" &&
                (workbench.SourceLangCombo.SelectedItem as LanguageOption)?.Tag == "ja" &&
                (workbench.TargetLangCombo.SelectedItem as LanguageOption)?.Tag == "en",
                "floating panel pair must reach the already loaded workbench");
            workbench.TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget("zh-CN");
            Until(() => (panelTarget.SelectedItem as LanguageOption)?.Tag == "zh-CN" &&
                CoreBridge.GetSettings().TargetLanguage == "zh-CN",
                "workbench pair must reach the existing floating panel and global quick-search settings");

            // A language change must clear the stored reading as well as the
            // visible box; toggling back to translation cannot resurrect it.
            var reading = (ReadingModeState)typeof(TranslationPanelWindow).GetField("_reading",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
            reading.CaptureTranslation("old result", "old note");
            panelTarget.SelectedItem = LanguageCatalog.ResolveTarget("en");
            Until(() => CoreBridge.GetSettings().TargetLanguage == "en", "new pair persists");
            Program.Assert(reading.TranslationText.Length == 0 && reading.TranslationNote.Length == 0,
                "changing language must invalidate hidden translation and explanation too");
            Console.WriteLine("UI language synchronization and stale-reading regression passed");
        }
        finally
        {
            panel.Close();
            host.Content = null;
            Pump();
            CoreBridge.SaveSettings(original);
        }
    }

    private static void Until(Func<bool> test, string error, Func<string>? detail = null)
    {
        var watch = Stopwatch.StartNew();
        while (!test() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(10); }
        if (!test()) throw new InvalidOperationException(detail is null ? error : $"{error}: {detail()}");
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
