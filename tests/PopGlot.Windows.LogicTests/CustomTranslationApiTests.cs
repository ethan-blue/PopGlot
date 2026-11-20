using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static class CustomTranslationApiTests
{
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class NullResultSystemProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => false;
    }

    public static void SystemProxyNullResultRemainsDirect()
    {
        var original = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new NullResultSystemProxy();
            using var handler = ProxyHttpClientFactory.CreateHandler(ProxyConfig.Default, TimeSpan.FromSeconds(2));
            Assert(handler.Proxy!.GetProxy(new Uri("https://example.com/translate")) is null,
                "a system DIRECT/null decision must not become a CONNECT to the recipient itself");
        }
        finally { HttpClient.DefaultProxy = original; }
    }

    public static void TemplatesAreEscapedAndSecretsStayBoundToEndpoint()
    {
        var options = new CustomTranslationApiOptions(Endpoint: "https://example.com/translate");
        CustomTranslationApi.Validate(options);
        const string text = "literal {apiKey} {source} \"quote\" \\ newline\n中文";
        var body = CustomTranslationApi.BuildBody(options, text, "en", "zh", "test-fixture-key");
        using var document = JsonDocument.Parse(body);
        Assert(document.RootElement.GetProperty("q").GetString() == text, "JSON expansion must preserve quotes, slashes and literal placeholder-like source text");
        Assert(!body.Contains("test-fixture-key"), "source text must not be re-expanded into secret");
        Assert(options.CredentialTarget != (options with { Endpoint = "https://other.example/translate" }).CredentialTarget,
            "changing recipient must not inherit another recipient's credential");
        Assert(CustomTranslationApi.ParseResponse("{\"data\":[{\"a/b\":\"你好\"}]}", "/data/0/a~1b") == "你好", "JSON pointer supports arrays and escaping");
        foreach (var url in new[] { "http://example.com/t", "https://user:pass@example.com/t", "https://example.com/t?key=secret" })
        {
            try { CustomTranslationApi.Validate(options with { Endpoint = url }); throw new Exception("unsafe URL accepted"); }
            catch (ArgumentException) { }
        }
        foreach (var response in new[] { "{}", "{\"translatedText\":null}", "{\"translatedText\":\"\"}", "{\"translatedText\":42}" })
        {
            try { CustomTranslationApi.ParseResponse(response, "/translatedText"); throw new Exception("non-translation accepted"); }
            catch (InvalidOperationException) { }
        }
    }

    public static async Task GoogleLongTextUsesOneBodyRequestWithoutUrlText()
    {
        var oldSender = FreeTranslateService.HttpSenderOverride;
        var oldEndpoints = FreeTranslateService.EndpointsOverride;
        var oldLoader = OutboundPolicy.SettingsLoader;
        var oldLive = OutboundPolicy.LiveSettingsLoader;
        var source = string.Concat(Enumerable.Repeat("The build failed because a dependency is missing. ", 50)).TrimEnd();
        var sends = 0;
        try
        {
            var settings = CoreBridge.GetSettings() with { NetworkEnabled = true, SafeDevMode = false };
            OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };
            OutboundPolicy.LiveSettingsLoader = () => settings;
            FreeTranslateService.ResetTransportStateForTest(); FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.EndpointsOverride = null;
            FreeTranslateService.HttpSenderOverride = async (request, ct) =>
            {
                sends++;
                Assert(request.Method == HttpMethod.Post && request.RequestUri!.Host == "translate.google.com",
                    "long Google text uses the same recipient's POST endpoint");
                Assert(!request.RequestUri!.Query.Contains("q="), "source must not be stored in the request URL");
                var form = await request.Content!.ReadAsStringAsync(ct);
                Assert(System.Web.HttpUtility.ParseQueryString(form)["q"] == source, "POST body must preserve the entire source");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[[[\"已经整段翻译\"]]]") };
            };
            Assert(!FreeTranslateService.NeedsBatching(source, FreeEngineProvider.Google, out _), "Google long content must not be split by the old GET URL budget");
            var response = await FreeTranslateService.TranslateAsync(source, "en", "zh-CN", new FreeEngineAuthorization(settings), provider: FreeEngineProvider.Google);
            Assert(sends == 1 && response.Result.TranslatedText == "已经整段翻译", "one logical translation must require one wire send, not dozens");

            // Reproduce the live sequence: primary gets rate limited on a short
            // query, secondary succeeds, then the user submits a long article.
            FreeTranslateService.HttpSenderOverride = (request, _) => Task.FromResult(
                request.RequestUri!.Host != "clients5.google.com"
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"备用线路\"]") });
            var warmed = await FreeTranslateService.TranslateAsync("warm secondary after rate limit", "en", "zh-CN",
                new FreeEngineAuthorization(settings), provider: FreeEngineProvider.Google);
            Assert(warmed.Result.TranslatedText == "备用线路", "flat secondary response must be parsed and primary cooled");
            var secondarySource = source + " Another final sentence.";
            sends = 0;
            FreeTranslateService.HttpSenderOverride = async (request, ct) =>
            {
                sends++;
                Assert(request.Method == HttpMethod.Post && request.RequestUri!.Host == "clients5.google.com",
                    "long content must use the healthy secondary host's POST, not its bounded GET");
                var form = await request.Content!.ReadAsStringAsync(ct);
                Assert(System.Web.HttpUtility.ParseQueryString(form)["q"] == secondarySource, "secondary POST preserves all source text");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[\"备用完整长文\"]") };
            };
            var secondary = await FreeTranslateService.TranslateAsync(secondarySource, "en", "zh-CN",
                new FreeEngineAuthorization(settings), provider: FreeEngineProvider.Google);
            Assert(sends == 1 && secondary.Result.TranslatedText == "备用完整长文", "cooled primary must not break long translation or increase wire sends");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = oldSender; FreeTranslateService.EndpointsOverride = oldEndpoints;
            OutboundPolicy.SettingsLoader = oldLoader; OutboundPolicy.LiveSettingsLoader = oldLive;
            FreeTranslateService.ResetTransportStateForTest(); FreeTranslateService.ResetRateLimitStateForTest();
        }
    }

    public static async Task RealHttpRoundTripHonorsPolicyAndConfiguration()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new CustomTranslationApiOptions(Endpoint: $"http://127.0.0.1:{port}/translate");
        var oldSender = FreeTranslateService.HttpSenderOverride;
        var oldOptions = CustomTranslationApi.OptionsLoaderForTest;
        var oldShell = OutboundPolicy.SettingsLoader;
        var oldLive = OutboundPolicy.LiveSettingsLoader;
        var oldClient = FreeTranslateService.HttpClientOverrideForTest;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        string received = "";
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var length = 0;
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
            var chars = new char[length];
            var read = 0;
            while (read < length) { var count = await reader.ReadAsync(chars.AsMemory(read)); if (count == 0) break; read += count; }
            received = new string(chars, 0, read);
            var payload = Encoding.UTF8.GetBytes("{\"translatedText\":\"你好，世界\"}");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header); await stream.WriteAsync(payload);
        });
        try
        {
            var settings = CoreBridge.GetSettings() with { NetworkEnabled = true, SafeDevMode = false };
            OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };
            OutboundPolicy.LiveSettingsLoader = () => settings;
            CustomTranslationApi.OptionsLoaderForTest = () => options;
            FreeTranslateService.HttpClientOverrideForTest = client;
            var sends = 0;
            FreeTranslateService.HttpSenderOverride = (request, ct) =>
            {
                Assert(request.RequestUri!.IsLoopback, "test must not send public traffic");
                Interlocked.Increment(ref sends);
                return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            };
            var quota = FreeTranslateService.QuotaRemaining;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var response = await FreeTranslateService.TranslateAsync("Hello world", "en", "zh", new FreeEngineAuthorization(settings), timeout.Token, FreeEngineProvider.Custom);
            await server.WaitAsync(timeout.Token);
            Assert(response.Result.TranslatedText == "你好，世界" && sends == 1, "real JSON POST roundtrip must produce translation once");
            using var sent = JsonDocument.Parse(received);
            Assert(sent.RootElement.GetProperty("q").GetString() == "Hello world", "wire body must match the source");
            Assert(FreeTranslateService.QuotaRemaining == quota, "private endpoint must not consume public provider quota");
            OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied };
            try { await FreeTranslateService.TranslateAsync("Blocked", "en", "zh", new FreeEngineAuthorization(settings), timeout.Token, FreeEngineProvider.Custom); }
            catch (InvalidOperationException) { }
            Assert(sends == 1, "revoked consent must prevent a second transport submission");
        }
        finally
        {
            listener.Stop();
            FreeTranslateService.HttpSenderOverride = oldSender; FreeTranslateService.HttpClientOverrideForTest = oldClient;
            CustomTranslationApi.OptionsLoaderForTest = oldOptions; OutboundPolicy.SettingsLoader = oldShell; OutboundPolicy.LiveSettingsLoader = oldLive;
        }
    }

    public static void AutoBatchingUsesResolvedRouteAndAuthorizationStaysFrozen()
    {
        FreeTranslateService.ResetTransportStateForTest(); FreeTranslateService.ResetRateLimitStateForTest(); FreeTranslateService.ResetHealthCacheForTest();
        var text = string.Concat(Enumerable.Repeat("This is a sentence. ", 40));
        Assert(text.Length < 1000, "fixture is below Bing single-request limit");
        Assert(!FreeTranslateService.NeedsBatching(text, FreeEngineProvider.Bing, out _), "a resolved Bing route must not apply Google's small GET budget");
        Assert(FreeTranslateService.SelectAutoProvider(text) != FreeEngineProvider.Bing,
            "Auto must not select the experimental Bing route without positive evidence");
        var auth = new FreeEngineAuthorization(CoreBridge.GetSettings()).ForProvider(FreeEngineProvider.Bing, true);
        Assert(auth.ResolvedProvider == FreeEngineProvider.Bing && auth.WasAutoSelected, "recipient and Auto provenance must survive across segments");
        Assert(QuickSearchLivePolicy.EvaluateFreeRouteGuards("hello", FreeEngineProvider.Custom, 0).Allowed,
            "custom endpoints must not inherit public provider quotas");
    }

    public static void ServiceTabsAndApiEditorAreSeparateAndNarrowLayoutWorks()
    {
        Program.EnsureApplication();
        var section = new ServicesSection();
        var tabs = (TabControl)section.FindName("ServicesTabs");
        Assert(tabs.Items.Count == 3, "public, custom JSON and AI need independent tabs");
        foreach (TabItem tab in tabs.Items)
        {
            tabs.SelectedItem = tab;
            section.Measure(new System.Windows.Size(480, 580)); section.Arrange(new Rect(0, 0, 480, 580)); section.UpdateLayout();
            Assert(tab.IsSelected && tab.IsEnabled, "each service category must remain independently accessible");
        }
        var editor = (CustomTranslationApiEditor)section.FindName("CustomApiEditor");
        Assert(editor.FindName("TestButton") is Button && editor.FindName("TemplateInput") is TextBox,
            "custom page needs real editable template and test action");
        Assert(((TabControl)section.FindName("ApiEditorTabs")).Items.Count == 3,
            "AI connection, model and advanced settings must not be one long page");
    }
}
