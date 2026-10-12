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
        Console.WriteLine("UI section ready");
        var window = new Window { Content = section, Width = 800, Height = 600, Left = -12000, Top = -12000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None, Background = (Brush)app.Resources["CanvasBrush"] };
        try
        {
            window.Show(); Pump(); Console.WriteLine("UI window ready");
            Button Find(string name) => (Button)section.FindName(name);
            void Click(string name) { Find(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
            Click("ConnectLocalToolHeaderButton");
            var list = (ListBox)section.FindName("CliToolsListBox");
            list.ItemsSource = new[] { new ServicesSection.CliToolRow { ToolId = "codex", ToolName = "Codex CLI", EntryPath = Environment.ProcessPath! } };
            list.SelectedIndex = 0;
            Click("CliConnectButton");
            Console.WriteLine("UI connect submitted");
            Until(() => Find("CliTestButton").IsEnabled, "connection should populate models");
            Click("CliTestButton");
            Console.WriteLine("UI test submitted");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("测试通过"), "UI test translation should pass");
            Click("CliSaveUseButton");
            Console.WriteLine("UI save submitted");
            Until(() => ((TextBlock)section.FindName("CliConnectionStatus")).Text.StartsWith("已保存并切换"), "save/use should persist");
            Program.Assert(ProfileManager.Load().TryGetActiveProfile()?.ExecutionFacts.TextCapable == true, "UI saved an executable CLI profile");
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
        }
        finally { DirectTests.Restore(); window.Close(); Pump(); }
    }
    private static void Until(Func<bool> test, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!test() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(10); }
        if (!test()) throw new InvalidOperationException(error);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
