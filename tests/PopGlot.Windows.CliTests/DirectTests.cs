using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PopGlot.Windows;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.CliTests;

internal static class DirectTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static string _nonce = "";
    private static int _refreshes;
    private static string? _scenario;
    private static HttpClient? _client;
    private static TaskCompletionSource? _requestEntered;
    private static (string Token, string Model)? _expectedContext;
    private static string Jwt(string nonce, string audience = "oaiapp_fixture", string subject = "subject-fixture")
    {
        var header = ChatGptOAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" }));
        var payload = ChatGptOAuth.Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = "https://auth.openai.com", aud = audience,
            sub = subject, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), nonce, email = "test@example.invalid" }));
        return header + "." + payload + "." + ChatGptOAuth.Base64Url(Key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI");
            Program.Assert(request.RequestUri?.Host is "auth.openai.com" or "api.openai.com", "never send credentials to another host");
            if (uri.AbsolutePath.EndsWith("jwks.json"))
            {
                var key = Key.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "fixture", alg = "RS256", use = "sig",
                    n = ChatGptOAuth.Base64Url(key.Modulus!), e = ChatGptOAuth.Base64Url(key.Exponent!) } } });
            }
            if (uri.AbsolutePath.EndsWith("oauth/token"))
            {
                var form = await request.Content!.ReadAsStringAsync(ct);
                Program.Assert(form.Contains("client_id=oaiapp_fixture"), "exchange must use issued client id, never dynamic_agent_client");
                if (form.Contains("grant_type=refresh_token")) Interlocked.Increment(ref _refreshes);
                return Json(new { access_token = "fixture-access-" + Guid.NewGuid(), refresh_token = "fixture-refresh-" + Guid.NewGuid(),
                    id_token = Jwt(_nonce), token_type = "Bearer", scope = "openid email offline_access resource.invoke chatgpt.tokens.use.direct", expires_in = 3600 });
            }
            if (uri.AbsolutePath.EndsWith("oauth/revoke")) return new(HttpStatusCode.OK) { Content = new StringContent("") };
            Program.Assert(request.Headers.Authorization?.Scheme == "Bearer", "direct model calls require the selected account token");
            if (_expectedContext is { } context)
                Program.Assert(request.Headers.Authorization?.Parameter == context.Token,
                    "a request must use only its captured account, never another saved registration");
            if (uri.AbsolutePath == "/v1/models") return Json(new { models = new[] { new { slug = "fixture", display_name = "Fixture direct", visibility = "list" } } });
            Program.Assert(uri.AbsolutePath == "/v1/responses", "use public Responses API");
            if (_scenario == "hold") { _requestEntered!.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            var text = await request.Content!.ReadAsStringAsync(ct); using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
            if (_expectedContext is { } inference)
                Program.Assert(root.GetProperty("model").GetString() == inference.Model,
                    "an in-flight request must keep its captured model when the editable profile changes");
            Program.Assert(root.GetProperty("store").GetBoolean() == false && root.GetProperty("stream").GetBoolean(), "direct plan requires no storage and streaming");
            Program.Assert(!root.TryGetProperty("max_output_tokens", out _) && !root.TryGetProperty("temperature", out _), "omit unsupported plan parameters");
            Program.Assert(root.GetProperty("input").ValueKind == JsonValueKind.Array, "plan input is an explicit array");
            var marker = Regex.Match(root.GetProperty("instructions").GetString()!, @"PGMETA-[a-f0-9-]+").Value;
            var output = "你好，世界\n" + marker + "\n{\"explanation\":\"\",\"warnings\":[]}";
            string Event(object o) => "data: " + JsonSerializer.Serialize(o) + "\n\n";
            var events = Event(new { type = "response.created", response = new { id = "resp_fixture" } });
            events += Event(new { type = "response.output_text.delta", response_id = "resp_fixture", delta = output });
            if (_scenario == "late_error") events += Event(new { type = "response.failed", response = new { id = "resp_fixture", status = "failed" } });
            else if (_scenario != "eof") events += Event(new { type = "response.completed", response = new { id = "resp_fixture", status = "completed" } });
            return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
    }
    internal static void Setup()
    {
        _client ??= new HttpClient(new Handler()) { Timeout = Timeout.InfiniteTimeSpan };
        DirectHttp.TestClientFactory = () => _client;
    }
    internal static void Restore() { DirectHttp.TestClientFactory = null; _scenario = null; }
    private static ProviderProfile Profile()
    {
        Setup(); var account = ChatGptAccountStore.SaveValidated("oaiapp_fixture", "subject-fixture", "Fixture account",
            new("fixture-access", "fixture-refresh", Jwt("fixture"), "resource.invoke chatgpt.tokens.use.direct", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")), null);
        return new() { Id = "direct-fixture", Name = "ChatGPT direct fixture", ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
            DirectConnection = new() { AccountId = account.Id, ModelId = "fixture", Enabled = true }, TextModel = "fixture", SupportsVision = false, CredentialTarget = "" };
    }
    internal static ProviderProfile UiProfile() => Profile();
    internal static void HoldUiRequest() { _scenario = "hold"; _requestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    internal static bool UiRequestEntered => _requestEntered?.Task.IsCompleted == true;
    internal static void ReleaseUiScenario() => _scenario = null;
    public static async Task SignInAndValidation()
    {
        Setup(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); Task? callback = null;
        var account = await ChatGptOAuth.SignInAsync(null, timeout.Token, url =>
        {
            var query = new Uri(url).Query[1..].Split('&').Select(s => s.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Program.Assert(query["client_id"] == "dynamic_agent_client" && query["agent_name_hint"] == "PopGlot", "register PopGlot itself");
            Program.Assert(query["code_challenge_method"] == "S256" && query["ext_agent_host_id"].StartsWith("urn:uuid:"), "PKCE and stable opaque host");
            _nonce = query["nonce"];
            callback = Task.Run(async () =>
            {
                using var browser = new HttpClient(); var redirect = query["redirect_uri"];
                using var invalid = await browser.GetAsync(redirect + "?state=wrong&code=fake&client_id=oaiapp_fixture", timeout.Token);
                Program.Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "wrong callback state rejected");
                using var valid = await browser.GetAsync(redirect + "?state=" + query["state"] + "&code=fake&client_id=oaiapp_fixture", timeout.Token);
                Program.Assert(valid.IsSuccessStatusCode, "valid callback accepted");
            });
        });
        await callback!;
        Program.Assert(ChatGptAccountStore.HasCredentials(account.Id), "verified credentials are saved");
        var bytes = File.ReadAllBytes(Path.Combine(StoragePaths.CoreConfigDirectory, "chatgpt-accounts", account.Id + ".credentials"));
        Program.Assert(!Encoding.UTF8.GetString(bytes).Contains("fixture-access"), "credential file encrypted by current Windows user");
        var rejected = false;
        try { await ChatGptOAuth.ValidateIdTokenAsync(Jwt("wrong"), account.ClientId, _nonce, timeout.Token); } catch (InvalidOperationException) { rejected = true; }
        Program.Assert(rejected, "nonce mismatch rejected");
        rejected = false;
        try { await ChatGptOAuth.ValidateIdTokenAsync(Jwt(_nonce, "other-client"), account.ClientId, _nonce, timeout.Token); } catch (InvalidOperationException) { rejected = true; }
        Program.Assert(rejected, "audience mismatch rejected");
        rejected = false; var tampered = Jwt(_nonce).Split('.'); tampered[2] = ChatGptOAuth.Base64Url(new byte[256]);
        try { await ChatGptOAuth.ValidateIdTokenAsync(string.Join('.', tampered), account.ClientId, _nonce, timeout.Token); } catch (InvalidOperationException) { rejected = true; }
        Program.Assert(rejected, "invalid signature rejected");
    }
    public static async Task DirectRoutes()
    {
        var profile = Profile(); ProfileManager.SaveCliProfile(profile, null, true);
        Program.Assert(TranslationStyleMenu.ProbeNextTextRoute() == TranslationStyleSupport.Supported, "direct route does not need an API key");
        using var backend = new ChatGptDirectBackend(profile.DirectConnection!); var models = await backend.ListModelsAsync(CancellationToken.None);
        Program.Assert(models.Count == 1, "account-specific model catalog");
        var coordinator = new TranslationCoordinator(new HistoryStore());
        var session = await coordinator.TranslateTextAsync("Hello world", "en", "zh", TranslationInputSource.Manual);
        Program.Assert(session.Stage == TranslationSessionStage.Completed && session.TextExecutor == TranslationTextExecutor.ChatGptDirect, "main route dispatches direct HTTP without spawning CLI");
        var summary = await coordinator.RunTextTaskAsync("Hello world", "en", "zh", TextTaskKind.Summarize);
        Program.Assert(summary.DirectDiagnostics is not null && summary.CliDiagnostics is null && summary.Diagnostics?.StatusCode == 200, "summary carries real HTTP diagnostics");
        Program.Assert(ProfileManager.TrySwitchActiveProfile(profile.Id, out _), "quick switch supports saved direct login");
    }
    public static async Task AccountAndModelSnapshotIsolation()
    {
        var profile = Profile();
        var alternate = ChatGptAccountStore.SaveValidated("oaiapp_fixture", "alternate-subject", "Alternate fixture",
            new("alternate-access", "alternate-refresh", Jwt("fixture", subject: "alternate-subject"),
                "resource.invoke chatgpt.tokens.use.direct", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N")), null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _expectedContext = ("fixture-access", "fixture");
            var first = CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh-CN", null, deadline.Token);
            profile.DirectConnection!.AccountId = alternate.Id;
            profile.DirectConnection.ModelId = "alternate-model";
            profile.TextModel = "stale-display-model";
            var original = await first.Completion;
            Program.Assert(!original.Result.IsPartial && original.DirectDiagnostics?.ModelId == "fixture",
                "the first response must retain the original account/model snapshot");
            _expectedContext = ("alternate-access", "alternate-model");
            var next = await CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh-CN", null, deadline.Token).Completion;
            Program.Assert(!next.Result.IsPartial && next.DirectDiagnostics?.ModelId == "alternate-model",
                "a subsequent request must use the newly selected account and canonical model");
        }
        finally { _expectedContext = null; }
    }

    public static async Task TerminalFailures()
    {
        var profile = Profile();
        foreach (var scenario in new[] { "late_error", "eof" })
        {
            _scenario = scenario; var rejected = false;
            try { await CliTranslationExecutor.Stream(profile, "Hello", "en", "zh", null, CancellationToken.None).Completion; }
            catch (InvalidOperationException) { rejected = true; }
            Program.Assert(rejected, "partial text without completed inference is not success: " + scenario);
        }
        _scenario = null;
    }
    public static async Task OfflineAndDisconnect()
    {
        var profile = Profile(); var settings = CoreBridge.GetSettings(); var requests = 0;
        DirectHttp.TestClientFactory = () => { requests++; return _client!; };
        CoreBridge.SaveSettings(settings with { NetworkEnabled = false });
        try
        {
            var failed = false;
            try { await CliTranslationExecutor.Stream(profile, "Hello", "en", "zh", null, CancellationToken.None).Completion; }
            catch (InvalidOperationException) { failed = true; }
            Program.Assert(failed && requests == 0, "offline denies before token/model/inference HTTP");
        }
        finally { CoreBridge.SaveSettings(settings); Setup(); }
        ProfileManager.SaveCliProfile(profile, profile.Id, true);
        _scenario = "hold"; _requestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = CliTranslationExecutor.Stream(profile, "Hello", "en", "zh", null, CancellationToken.None);
        await _requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProfileManager.DisableCliProfile(profile.Id);
        var rejected = false;
        try { await stream.Completion; } catch (InvalidOperationException) { rejected = true; }
        Program.Assert(rejected && !ProfileManager.Load().TryGetActiveProfile()!.ExecutionFacts.TextCapable, "direct disconnect disables route and queued work");
        _scenario = null;
    }
    public static async Task RefreshAndLogout()
    {
        var profile = Profile(); var id = profile.DirectConnection!.AccountId;
        var old = ChatGptAccountStore.Load(id); ChatGptAccountStore.ReplaceCredentials(id, old.Revision, old with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var before = _refreshes;
        await Task.WhenAll(ChatGptOAuth.AccessTokenAsync(id, CancellationToken.None), ChatGptOAuth.AccessTokenAsync(id, CancellationToken.None));
        Program.Assert(_refreshes == before + 1, "refresh serialized per registration and replacement reused");
        Program.Assert(ChatGptAccountStore.Load(id).RefreshToken != old.RefreshToken, "save rotating refresh token");
        await ChatGptOAuth.SignOutAsync(id, CancellationToken.None);
        Program.Assert(!ChatGptAccountStore.HasCredentials(id) && ChatGptAccountStore.Get(id).ClientId == "oaiapp_fixture", "logout clears tokens and retains account registration");
    }
    public static Task ImportBoundaries()
    {
        var database = Path.Combine(StoragePaths.CoreConfigDirectory, "omp-import-fixture.db");
        using (var connection = new SqliteConnection("Data Source=" + database))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE auth_credentials (provider TEXT, credential_type TEXT, data TEXT, disabled_cause TEXT);"; command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO auth_credentials VALUES ('deepseek','api_key',$key,NULL),('deepseek','oauth','not-json-never-read',NULL),('other-provider','api_key','not-json-never-read',NULL)";
            command.Parameters.AddWithValue("$key", JsonSerializer.Serialize(new { key = "fixture-import-key" })); command.ExecuteNonQuery();
        }
        var rejected = false; try { OmpApiKeyImporter.Import("deepseek", "fixture", false, database); } catch (InvalidOperationException) { rejected = true; }
        Program.Assert(rejected, "no import without explicit confirmation");
        var profile = OmpApiKeyImporter.Import("deepseek", "fixture", true, database);
        Program.Assert(profile.ResolvedExecutionKind == EngineExecutionKind.NativeApi && CredentialStore.LoadApiKey(profile.CredentialTarget) == "fixture-import-key", "selected API key saved to own vault and native API route");
        Program.Assert(!OmpApiKeyImporter.Supports("openai-codex") && !OmpApiKeyImporter.Supports("claude-code"), "subscription tokens cannot be imported as API keys");
        return Task.CompletedTask;
    }
    public static async Task LiveImportedNative()
    {
        Restore();
        var entry = CliDiscovery.Discover(CliDiscovery.OmpToolId).First();
        using var backend = new OmpRpcBackend(new() { ExecutablePath = entry.ExecutablePath, Enabled = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var models = await backend.ListModelsAsync(timeout.Token);
        var selected = models.First(m => m.Provider == "zhipu-coding-plan" && m.Id == "glm-4.7");
        var profile = OmpApiKeyImporter.Import(selected.Provider!, selected.Id, true);
        var session = await new TranslationCoordinator(new HistoryStore()).TranslateTextAsync("Hello world", "en", "zh", TranslationInputSource.Manual, timeout.Token);
        Program.Assert(session.Stage == TranslationSessionStage.Completed && session.TextExecutor == TranslationTextExecutor.UserProvider,
            "imported key must complete via native HTTP transport; no key or error payload is logged");
        Console.WriteLine($"LIVE imported API: provider={selected.Provider}, model={selected.Id}, completed=true, executor={session.TextExecutor}, firstDelta={session.Timing.FirstDeltaMs} ms");
        Program.Assert(ProfileManager.Load().TryGetActiveProfile()?.Id == profile.Id, "native imported profile active");
    }
}
