using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopGlot.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (!args.Any(a => a is "--public" or "--configured-ai" or "--screenshots"))
        { Console.WriteLine("Explicit opt-in required: --public / --configured-ai / --screenshots. Only fixed synthetic text is sent."); return 2; }
        var originalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PopGlot");
        var shell = ShellSettingsStore.Load();
        if (args.Contains("--direct")) shell = shell with { NetworkProxyMode = NetworkProxyMode.Direct };
        Console.WriteLine($"Network mode: {shell.NetworkProxyMode}");
        var probeUri = new Uri("https://translate.googleapis.com/");
        var systemProxy = System.Net.Http.HttpClient.DefaultProxy;
        Console.WriteLine($"System proxy: bypass={systemProxy.IsBypassed(probeUri)}, returnsDestination={systemProxy.GetProxy(probeUri) == probeUri}");
        ProviderProfile? profile = null;
        string key = "";
        if (args.Contains("--configured-ai"))
        {
            var path = Path.Combine(originalRoot, "product-config.json");
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<CoreProductConfig>(File.ReadAllText(path));
                var candidates = config?.Profiles.Where(p => p.SupportsText && !p.ExecutionFacts.IsManagedExecution).ToArray() ?? [];
                profile = candidates.FirstOrDefault(p => p.Id == config?.ActiveProfileId) ?? candidates.FirstOrDefault();
                if (profile is not null)
                {
                    var target = string.IsNullOrEmpty(profile.CredentialTarget) ? $"PopGlot/provider/{profile.Id}" : profile.CredentialTarget;
                    key = CredentialStore.LoadApiKey(target) ?? "";
                    if (key.Length == 0 && profile.Id == config?.ActiveProfileId)
                        key = CredentialStore.LoadApiKey(CredentialStore.DefaultTargetName) ?? "";
                    SecretRedactor.Register(key);
                }
            }
        }
        var buildVersion = typeof(CoreBridge).Assembly.GetName().Version!.ToString(3);
        var evidence = Path.GetFullPath("artifacts/review-" + buildVersion); Directory.CreateDirectory(evidence);
        var root = Path.Combine(evidence, "live-data-" + Guid.NewGuid().ToString("N"));
        StoragePaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var results = new List<object>();
        var failures = 0;
        try
        {
            CoreBridge.Initialize(root);
            var settings = CoreBridge.GetSettings() with { NetworkEnabled = true, SafeDevMode = false };
            CoreBridge.SaveSettings(settings);
            ShellSettingsStore.Save(shell with { FreeEngineConsent = FreeEngineConsent.Allowed, LiveTranslateEnabled = false });
            OutboundPolicy.LiveSettingsLoader = () => settings;
            var transport = FreeTranslateService.GetActiveHttpClient();
            var wireSends = 0;
            var postSends = 0;
            FreeTranslateService.HttpSenderOverride = (request, ct) =>
            {
                Interlocked.Increment(ref wireSends);
                if (request.Method == System.Net.Http.HttpMethod.Post) Interlocked.Increment(ref postSends);
                return transport.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            };
            if (args.Contains("--public"))
            {
                var providers = new[] { FreeEngineProvider.Google, FreeEngineProvider.Bing, FreeEngineProvider.Youdao, FreeEngineProvider.MyMemory, FreeEngineProvider.Yandex };
                var providerIndex = Array.IndexOf(args, "--providers");
                if (providerIndex >= 0 && providerIndex + 1 < args.Length)
                {
                    var requested = args[providerIndex + 1].Split(',');
                    providers = providers.Where(p => requested.Contains(p.ToString(), StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (providers.Length == 0) throw new ArgumentException("No matching explicitly requested public providers.");
                }
                foreach (var provider in providers)
                {
                    for (var i = 0; i < (provider == FreeEngineProvider.Google ? 3 : 2); i++)
                    {
                        var text = provider == FreeEngineProvider.Youdao ? (i == 0 ? "compiler" : "dependency")
                            : (i == 0 ? "Please restart the computer after installing the update." : "The build failed because a required dependency is missing.");
                        if (provider == FreeEngineProvider.Google && i == 2)
                            text = string.Concat(Enumerable.Repeat("The build failed because a dependency is missing. ", 50));
                        var sendsBefore = wireSends;
                        var postsBefore = postSends;
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(32));
                            var response = FreeTranslateService.TranslateAsync(text, "en", "zh-CN", new FreeEngineAuthorization(settings), ct.Token, provider).GetAwaiter().GetResult();
                            var translated = response.Result.TranslatedText;
                            if (!translated.Any(c => c is >= '\u4e00' and <= '\u9fff') || translated == text)
                                throw new InvalidOperationException("No valid Chinese translation returned");
                            var row = new { route = provider.ToString(), sample = i + 1, ok = true, ms = watch.ElapsedMilliseconds,
                                sourceChars = text.Trim().Length, requests = wireSends - sendsBefore, posts = postSends - postsBefore,
                                translatedChars = translated.Length, translation = translated[..Math.Min(translated.Length, 120)] };
                            results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            var row = new { route = provider.ToString(), sample = i + 1, ok = false, ms = watch.ElapsedMilliseconds, error = SecretRedactor.Redact(ex.Message) };
                            results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row)); break;
                        }
                    }
                }
            }
            if (args.Contains("--configured-ai"))
            {
                if (profile is null) { failures++; Console.WriteLine("AI: NOT VERIFIED — no configured HTTP AI profile"); }
                else
                {
                    try
                    {
                        var draft = profile.ToProviderSettings(settings) with { IncludeExplanation = false };
                        var baseIndex = Array.IndexOf(args, "--ai-base-url");
                        if (baseIndex >= 0 && baseIndex + 1 < args.Length)
                            draft = draft with { ApiBaseUrl = args[baseIndex + 1] };
                        Console.WriteLine($"AI check host: {new Uri(draft.ApiBaseUrl).Host}; credential: {(key.Length > 0 ? "configured" : "absent")}; model: {draft.TextModel}");
                        var proxy = ProxyConfig.FromShellSettings(shell);
                        CoreBridge.ConfigureNetworkProxy(proxy.Mode.ToString().ToLowerInvariant(), proxy.CustomProxyUrl, proxy.BypassLocal);
                        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        var started = Stopwatch.GetTimestamp();
                        var stream = CoreBridge.TranslateTextDraftStreamAsync(draft, key,
                            "The build failed because a required dependency is missing.", "en", "zh-CN", cancellationToken: ct.Token);
                        var response = stream.Completion.GetAwaiter().GetResult();
                        if (string.IsNullOrWhiteSpace(response.Result.TranslatedText) || response.Result.IsPartial)
                            throw new InvalidOperationException("AI returned empty or incomplete translation");
                        var row = new { route = "configured-ai", host = new Uri(profile.ApiBaseUrl).Host, ok = true,
                            firstTextMs = stream.Buffer.GetTtftMilliseconds(started), totalMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            translation = response.Result.TranslatedText, warnings = response.Result.Warnings.Count };
                        results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row)); stream.Buffer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        var row = new { route = "configured-ai", ok = false, error = SecretRedactor.Redact(ex.Message) };
                        results.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
                    }
                }
            }
            if (args.Contains("--screenshots")) Screenshots(evidence);
            var label = (args.Contains("--public") ? "public" : "") + (args.Contains("--configured-ai") ? "-ai" : "") + (args.Contains("--direct") ? "-direct" : "-system");
            if (args.Contains("--providers")) label += "-selected";
            if (results.Count > 0)
                File.WriteAllText(Path.Combine(evidence, "live-results-" + label + ".json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Live checks: {failures} failures. Evidence: {evidence}. No user config was modified.");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            key = "";
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static void Screenshots(string evidence)
    {
        CoreBridge.SaveSettings(CoreBridge.GetSettings() with { NetworkEnabled = false, SafeDevMode = true });
        CredentialStore.OverrideVault = new MemoryCredentialVault();
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        ThemeService.Apply(ThemePreference.Light);
        var section = new ServicesSection();
        var tabs = (TabControl)section.FindName("ServicesTabs");
        // Capture the real painted host, not a transparent UserControl against black alpha.
        var canvas = new Border { Child = section, Padding = new Thickness(16) };
        canvas.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        var window = new Window { Width = 800, Height = 680, Content = canvas, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow };
        window.Show();
        void Save(FrameworkElement view, string name)
        {
            view.UpdateLayout();
            view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(evidence, name + ".png")); png.Save(output);
        }
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab; Save(canvas, tab.Name);
        }
        window.Width = 500;
        tabs.SelectedIndex = 1; Save(canvas, "CustomApiTab-narrow");
        window.Width = 800;
        section.LoadProfileIntoForm(new ProviderProfile
        {
            Id = "review-demo", Name = "自定义兼容 AI（演示配置）", ProviderType = ProviderType.OpenAiCompatible,
            ApiBaseUrl = "https://example.invalid/v1", TextEndpoint = "/chat/completions", TextModel = "your-text-model", SupportsText = true,
        });
        typeof(ServicesSection).GetMethod("ShowEditorForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, [false]);
        var editorTabs = (TabControl)section.FindName("ApiEditorTabs");
        foreach (TabItem tab in editorTabs.Items)
        {
            editorTabs.SelectedItem = tab; Save(canvas, "AI-editor-" + tab.Name);
        }
        window.Close();
        var quick = new QuickSearchWindow(new HistoryStore(), new VocabularyStore());
        quick.Show(); Save(quick, "QuickSearch-compact"); quick.ForceClose = true; quick.Close();
    }
}
