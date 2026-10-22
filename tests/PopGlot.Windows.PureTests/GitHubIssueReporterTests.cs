using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.IO;
using PopGlot.Windows;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.PureTests;

/// <summary>
/// 0.1.25 自我迭代：GitHub Issue 诊断上报的纯逻辑测试。
///
/// Registration (Main in Program.cs):
///   await RunAsync("github issue reporter upload, redaction and consent", RunGitHubIssueReporterTests);
///
/// Everything network-shaped goes through GitHubIssueReporter.TestClientFactory
/// with an in-memory handler; the PAT rides the pure host's in-memory vault
/// (asserted before any test runs — the real Windows vault must never be touched).
/// </summary>
internal static partial class Program
{
    private const string TestPat = "ghp_puretests0123456789abcdefghijklmnop";

    internal static async Task RunGitHubIssueReporterTests()
    {
        True(CredentialStore.OverrideVault is not null,
            "the pure host must install the in-memory vault before reporter tests");
        try
        {
            Run("github reporter redacts tokens, url queries and caps the body", GitHubReporterRedaction);
            Run("issue upload consent defaults off on old files and round-trips", IssueUploadConsentSettingsRoundTrip);
            await RunAsync("github reporter sends the sanitized issue json with PAT auth", GitHubReporterJsonShapeAndAuth);
            await RunAsync("github reporter retries once without labels on 422", GitHubReporterLabelFallback);
            await RunAsync("github reporter errors never leak the PAT", GitHubReporterErrorsNeverLeakPat);
        }
        finally
        {
            GitHubIssueReporter.TestClientFactory = null;
            try { CredentialStore.DeleteApiKey(GitHubIssueReporter.CredentialTarget); } catch { }
        }
    }

    private static void GitHubReporterRedaction()
    {
        var redacted = GitHubIssueReporter.Redact(
            "token ghp_shorttoken1234567890abcd and github_pat_1_ABCDEFGHIJKLMNOPQRS1234 " +
            "and sk-live-abcdefghijklmnop1234 plus https://api.example.test/v1?key=SECRET");
        True(!redacted.Contains("ghp_shorttoken", StringComparison.Ordinal), "ghp_ tokens must be swept");
        True(!redacted.Contains("github_pat_", StringComparison.Ordinal), "github_pat_ tokens must be swept");
        True(!redacted.Contains("sk-live-abcdefghijklmnop", StringComparison.Ordinal), "sk- tokens must be swept");
        True(!redacted.Contains("SECRET", StringComparison.Ordinal), "url queries must lose their query string");
        True(redacted.Contains("[redacted-token]", StringComparison.Ordinal), "a redaction marker must appear");

        var huge = new string('x', GitHubIssueReporter.MaxBodyCharacters + 5_000);
        var capped = GitHubIssueReporter.CapBody(huge);
        Equal(GitHubIssueReporter.MaxBodyCharacters, capped.Length, "the body must be hard-capped at 24k characters");
        True(capped.EndsWith('…'), "the cap must end with an explicit truncation marker");

        // Multi-line input keeps its shape: redaction is per line, not one
        // 400-character blob.
        var shaped = GitHubIssueReporter.Redact("line one\npassword=hunter2secret");
        True(shaped.Contains("line one", StringComparison.Ordinal), "plain lines survive");
        True(!shaped.Contains("hunter2secret", StringComparison.Ordinal), "key/value secrets are redacted per line");
    }

    private static void IssueUploadConsentSettingsRoundTrip()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"popglot-pure-issue-upload-{Guid.NewGuid():N}.json");
        try
        {
            // An old settings file (pre-0.1.25) has no IssueUploadConsent
            // field at all — it must load as false, never granted implicitly.
            File.WriteAllText(path, "{\"SchemaVersion\":3}", Encoding.UTF8);
            ShellSettingsStore.InvalidateCache();
            Equal(false, ShellSettingsStore.Load(path).IssueUploadConsent,
                "old files without the field must load with consent OFF");

            ShellSettingsStore.Update(current => current with { IssueUploadConsent = true }, path);
            var persisted = File.ReadAllText(path, Encoding.UTF8);
            True(persisted.Contains("\"IssueUploadConsent\": true", StringComparison.Ordinal),
                "saving must serialize the consent like its siblings");
            Equal(true, ShellSettingsStore.Load(path).IssueUploadConsent,
                "a saved consent must round-trip back as true");

            ShellSettingsStore.Update(current => current with { IssueUploadConsent = false }, path);
            Equal(false, ShellSettingsStore.Load(path).IssueUploadConsent,
                "revoking the consent must round-trip back as false");
        }
        finally
        {
            ShellSettingsStore.InvalidateCache();
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task GitHubReporterJsonShapeAndAuth()
    {
        CredentialStore.SaveApiKey(TestPat, GitHubIssueReporter.CredentialTarget);
        var handler = new CapturingHandler(_ => JsonResponse(201,
            "{\"html_url\":\"https://github.com/ethan-blue/PopGlot/issues/7\",\"number\":7}"));
        GitHubIssueReporter.TestClientFactory = () => new HttpClient(handler);

        var (ok, url, error) = await GitHubIssueReporter.UploadAsync(
            "[test] 连通性", "body with ghp_leakedtoken1234567890abcdef");

        True(ok, $"upload must succeed, error was: {error}");
        Equal("https://github.com/ethan-blue/PopGlot/issues/7", url, "the html_url must be parsed out");
        Equal(1, handler.Captured.Count, "exactly one request for a 201 response");

        var request = handler.Captured[0]!;
        Equal(GitHubIssueReporter.IssuesEndpoint, request.Url, "must POST to the issues endpoint");
        Equal("POST", request.Method, "issue creation is a POST");
        Equal("Bearer " + TestPat, request.Authorization, "auth must be the vault PAT as a bearer token");
        True(request.UserAgent.StartsWith("PopGlot/", StringComparison.Ordinal),
            $"user agent must carry the product name, got: {request.UserAgent}");
        Equal(GitHubIssueReporter.GitHubApiVersion, request.ApiVersion, "the pinned api-version header must be sent");

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Equal(JsonValueKind.Object, root.ValueKind, "the payload must be a JSON object");
        Equal("[test] 连通性", root.GetProperty("title").GetString(), "the title must pass through");
        True(!request.Body.Contains("ghp_leakedtoken", StringComparison.Ordinal),
            "the raw token must be swept from the body before send");
        Equal("diagnostics", root.GetProperty("labels")[0].GetString(),
            "the labels array must carry exactly the diagnostics label");
        Equal(1, root.GetProperty("labels").GetArrayLength(), "labels must have one entry");
        True(request.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true,
            "the request must be JSON");
    }

    private static async Task GitHubReporterLabelFallback()
    {
        CredentialStore.SaveApiKey(TestPat, GitHubIssueReporter.CredentialTarget);
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(422, "{\"message\":\"Validation Failed\"}")
                : JsonResponse(201, "{\"html_url\":\"https://github.com/ethan-blue/PopGlot/issues/8\"}");
        });
        GitHubIssueReporter.TestClientFactory = () => new HttpClient(handler);

        var (ok, url, error) = await GitHubIssueReporter.UploadAsync("[crash] Test 0.1.25", "body");

        True(ok, $"the label-less retry must succeed, error was: {error}");
        Equal("https://github.com/ethan-blue/PopGlot/issues/8", url, "the retry's issue url must surface");
        Equal(2, handler.Captured.Count, "exactly one labeled attempt plus one retry");

        using var first = JsonDocument.Parse(handler.Captured[0]!.Body);
        using var second = JsonDocument.Parse(handler.Captured[1]!.Body);
        True(!second.RootElement.TryGetProperty("labels", out _),
            "the retry must omit the labels property entirely");
    }

    private static async Task GitHubReporterErrorsNeverLeakPat()
    {
        CredentialStore.SaveApiKey(TestPat, GitHubIssueReporter.CredentialTarget);

        // A transport failure whose message embeds the PAT: the surfaced
        // error must still be PAT-free.
        var throwing = new CapturingHandler(_ => throw new HttpRequestException(
            "connect failed for Bearer " + TestPat));
        GitHubIssueReporter.TestClientFactory = () => new HttpClient(throwing);
        var (ok, url, error) = await GitHubIssueReporter.UploadAsync("title", "body");
        True(!ok, "a throwing transport must fail");
        True(url is null, "a failed upload has no issue url");
        True(error is not null && !error.Contains(TestPat, StringComparison.Ordinal),
            $"the error must never contain the PAT, got: {error}");

        // A GitHub error payload echoing the token gets the same sweep.
        var echoing = new CapturingHandler(_ => JsonResponse(401,
            "{\"message\":\"Bad credentials for " + TestPat + "\"}"));
        GitHubIssueReporter.TestClientFactory = () => new HttpClient(echoing);
        (ok, _, error) = await GitHubIssueReporter.UploadAsync("title", "body");
        True(!ok, "a 401 must fail");
        True(error is not null && error.Contains("401", StringComparison.Ordinal),
            "the surfaced error states the status");
        True(error is not null && !error.Contains(TestPat, StringComparison.Ordinal),
            $"the github error message must be swept, got: {error}");

        // No PAT configured at all: fail fast without touching the network.
        CredentialStore.DeleteApiKey(GitHubIssueReporter.CredentialTarget);
        var refusing = new CapturingHandler(_ => throw new InvalidOperationException(
            "纯逻辑宿主拒绝一切真实网络发送；这不是服务故障。"));
        GitHubIssueReporter.TestClientFactory = () => new HttpClient(refusing);
        (ok, url, error) = await GitHubIssueReporter.UploadAsync("title", "body");
        True(!ok, "missing PAT must fail before any send");
        True(url is null && error is not null, "the missing-PAT failure is reported, not thrown");
        Equal(0, refusing.Captured.Count, "no request may leave without a PAT");
    }

    // ================= In-memory capture plumbing =================

    private sealed record CapturedRequest(
        string Url,
        string Method,
        string? Authorization,
        string UserAgent,
        string? ApiVersion,
        string? ContentType,
        string Body);

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Captured { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(new CapturedRequest(
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Method.Method,
                request.Headers.TryGetValues("Authorization", out var authorization)
                    ? authorization.FirstOrDefault()
                    : null,
                request.Headers.TryGetValues("User-Agent", out var userAgent)
                    ? userAgent.FirstOrDefault() ?? string.Empty
                    : string.Empty,
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var apiVersion)
                    ? apiVersion.FirstOrDefault()
                    : null,
                request.Content?.Headers.ContentType?.MediaType,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage JsonResponse(int status, string json) => new((HttpStatusCode)status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
