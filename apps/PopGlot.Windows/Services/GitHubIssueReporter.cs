using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopGlot.Windows.Services;

/// <summary>
/// 自我迭代：opt-in GitHub Issue 诊断上报（0.1.25）。
///
/// Privacy contract, enforced structurally:
///   - default OFF — nothing leaves the machine until the user checks
///     「GitHub 问题上报」consent AND pastes a PAT;
///   - the PAT lives only in the Windows credential vault under
///     <see cref="CredentialTarget"/> and is never written to settings,
///     logs, report bodies or error strings;
///   - the report is allowlist-shaped: assembly/informational version, OS,
///     .NET runtime and the already-sanitized structured crash entry
///     (<see cref="DiagnosticsLog"/> writes type/stage/HResult/event/frames
///     only). Translation text, screenshots, query-string URLs and
///     credentials never enter the body in the first place, and the whole
///     body is re-run through the redaction battery plus a ghp_/github_pat_/
///     sk- token sweep before send;
///   - the body is hard-capped at <see cref="MaxBodyCharacters"/>.
/// </summary>
internal static partial class GitHubIssueReporter
{
    /// <summary>Windows Credential Manager target holding the user-pasted PAT.</summary>
    internal const string CredentialTarget = "PopGlot/telemetry/github";

    internal const string IssuesEndpoint = "https://api.github.com/repos/ethan-blue/PopGlot/issues";

    /// <summary>GitHub REST API version header value.</summary>
    internal const string GitHubApiVersion = "2022-11-28";

    /// <summary>Label attached to uploaded diagnostics issues.</summary>
    internal const string DiagnosticsLabel = "diagnostics";

    /// <summary>Hard cap for the issue body, applied after redaction.</summary>
    internal const int MaxBodyCharacters = 24_000;

    /// <summary>
    /// Test seam: when installed, every upload goes through this factory
    /// instead of the shared production client. Pure tests inject an
    /// <see cref="HttpClient"/> backed by an in-memory handler; callers own
    /// the clients they create, so the returned instance is never disposed
    /// here.
    /// </summary>
    internal static Func<HttpClient>? TestClientFactory { get; set; }

    // One static client for production: no socket exhaustion across crash
    // uploads and test sends; it carries no per-request state.
    private static readonly HttpClient SharedClient = new();

    // Once-per-session guard for the crash upload path (Interlocked).
    private static int _crashUploadAttempted;

    /// <summary>Assembly version, three components ("0.1.25").</summary>
    internal static string AssemblyVersionString =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Full informational version, including the "+commit" suffix when present.</summary>
    internal static string InformationalVersionString =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? AssemblyVersionString;

    /// <summary>
    /// Creates one diagnostics issue. Returns
    /// (ok, html_url, safe-error-without-PAT); never throws.
    /// </summary>
    internal static async Task<(bool Ok, string? IssueUrl, string? Error)> UploadAsync(
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pat = CredentialStore.LoadApiKey(CredentialTarget);
            if (string.IsNullOrWhiteSpace(pat))
            {
                return (false, null, "未配置 GitHub 访问令牌（PopGlot/telemetry/github）。");
            }

            var safeTitle = Redact(title).Trim();
            if (safeTitle.Length == 0)
            {
                safeTitle = "(diagnostics)";
            }
            var safeBody = CapBody(Redact(body));
            if (safeBody.Length == 0)
            {
                safeBody = "(empty report)";
            }

            using var request = BuildIssueRequest(safeTitle, safeBody, pat, includeLabels: true);
            var (status, ok, url, error) = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!ok && status is 404 or 422)
            {
                // A fine-grained PAT without labels/triage permission makes
                // the labeled create fail (422/404); the issue itself is
                // still welcome — retry exactly once without labels.
                using var retry = BuildIssueRequest(safeTitle, safeBody, pat, includeLabels: false);
                (status, ok, url, error) = await SendAsync(retry, cancellationToken).ConfigureAwait(false);
            }
            return (ok, url, error);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "上传已取消。");
        }
        catch (Exception exception)
        {
            return (false, null, SafeError(exception));
        }
    }

    /// <summary>
    /// The crash-report body: version identity, OS/.NET runtime and the
    /// sanitized latest structured crash entry. With <paramref name="crashEntry"/>
    /// null the newest entry is read from <see cref="StoragePaths.Logs"/>;
    /// the App crash hook passes the entry of the exception it just filed so
    /// the queued background write cannot race the upload.
    /// </summary>
    internal static string BuildReport(string? crashEntry = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PopGlot 诊断报告（用户显式授权上传）");
        builder.AppendLine();
        builder.AppendLine($"- 版本: {AssemblyVersionString}");
        builder.AppendLine($"- 构建: {InformationalVersionString}");
        builder.AppendLine($"- 操作系统: {Environment.OSVersion.VersionString}");
        builder.AppendLine($"- 运行时: .NET {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine("最近一次崩溃记录（结构化、已脱敏）:");
        builder.AppendLine(crashEntry ?? ReadLatestCrashEntry() ?? "（本机没有可读取的崩溃记录）");
        builder.AppendLine();
        builder.AppendLine("本报告不含翻译文本、截图、带查询串的地址或任何凭据。");
        return CapBody(Redact(builder.ToString()));
    }

    /// <summary>
    /// The small body the「发送测试诊断」button sends: identity only, no
    /// crash entry, so a connectivity probe uploads the least possible data.
    /// </summary>
    internal static string BuildConnectivityReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("PopGlot GitHub 上报连通性测试（用户点击「发送测试诊断」触发）");
        builder.AppendLine();
        builder.AppendLine($"- 版本: {AssemblyVersionString}");
        builder.AppendLine($"- 构建: {InformationalVersionString}");
        builder.AppendLine($"- 操作系统: {Environment.OSVersion.VersionString}");
        builder.AppendLine($"- 运行时: .NET {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine("本报告不含翻译文本、截图、带查询串的地址或任何凭据。");
        return CapBody(Redact(builder.ToString()));
    }

    /// <summary>
    /// App crash hook: after the local <see cref="DiagnosticsLog"/> write,
    /// if the user consented AND a PAT exists, upload one crash report per
    /// session (Interlocked once-guard), fire-and-forget on a background
    /// task. Every failure is swallowed quietly — this path must never add a
    /// second failure mode to a crashing process.
    /// </summary>
    internal static void TryUploadCrashReportOnce(Exception exception, string? eventId = null)
    {
        if (Interlocked.Exchange(ref _crashUploadAttempted, 1) == 1)
        {
            return;
        }
        try
        {
            if (!ShellSettingsStore.Load().IssueUploadConsent ||
                !CredentialStore.HasApiKey(CredentialTarget))
            {
                return;
            }
            // Reproduce the exact structured entry DiagnosticsLog just queued
            // (same type/stage/HResult/event/frames, still sanitized there).
            var entry = string.IsNullOrEmpty(eventId)
                ? DiagnosticsLog.BuildEntry(exception)
                : DiagnosticsLog.BuildEntry(exception, eventId: eventId);
            var title = $"[crash] {exception.GetType().Name} {AssemblyVersionString}";
            var body = BuildReport(entry);
            _ = Task.Run(async () =>
            {
                try
                {
                    await UploadAsync(title, body).ConfigureAwait(false);
                }
                catch
                {
                    // Quiet by contract.
                }
            });
        }
        catch
        {
            // Quiet by contract.
        }
    }

    /// <summary>
    /// Redaction battery for anything this reporter sends: the ghp_/github_pat_/
    /// sk- token sweep (github_pat_ is not covered by the log's own battery),
    /// then the CrashDiagnostics helpers per line — auth headers, key-shaped
    /// literals, key/value secrets, URL queries, hex blobs, user-profile
    /// paths (each line capped the way the log caps its messages).
    /// </summary>
    internal static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append(DiagnosticsLog.SanitizeForExport(TokenRegex().Replace(line, "[redacted-token]")));
        }
        return builder.ToString();
    }

    /// <summary>Any error text shown to the user: sanitized the same way.</summary>
    internal static string SafeError(Exception exception) =>
        SafeErrorText(string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message);

    internal static string SafeErrorText(string? text)
    {
        var safe = Redact(text).Replace("\n", " ").Trim();
        return safe.Length == 0 ? "未知错误" : safe;
    }

    /// <summary>Hard body cap: at most <see cref="MaxBodyCharacters"/> characters.</summary>
    internal static string CapBody(string body) =>
        body.Length <= MaxBodyCharacters
            ? body
            : body[..(MaxBodyCharacters - 1)] + "…";

    private static HttpRequestMessage BuildIssueRequest(string title, string body, string pat, bool includeLabels)
    {
        var json = includeLabels
            ? JsonSerializer.Serialize(new { title, body, labels = new[] { DiagnosticsLabel } })
            : JsonSerializer.Serialize(new { title, body });
        var request = new HttpRequestMessage(HttpMethod.Post, IssuesEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + pat);
        request.Headers.TryAddWithoutValidation("User-Agent", "PopGlot/" + AssemblyVersionString);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        return request;
    }

    private static async Task<(int Status, bool Ok, string? IssueUrl, string? Error)> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = TestClientFactory is { } factory ? factory() : SharedClient;
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
        {
            return (status, true, await ReadStringPropertyAsync(response, "html_url", cancellationToken).ConfigureAwait(false), null);
        }
        var reason = await ReadStringPropertyAsync(response, "message", cancellationToken).ConfigureAwait(false);
        var safeReason = string.IsNullOrEmpty(reason)
            ? response.ReasonPhrase
            : reason;
        return (status, false, null, SafeErrorText($"GitHub 返回 {status}：{safeReason}"));
    }

    private static async Task<string?> ReadStringPropertyAsync(
        HttpResponseMessage response, string property, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.TryGetProperty(property, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the newest entry from the newest crash-*.log* file in
    /// <see cref="StoragePaths.Logs"/>. Entries start with "[yyyy-MM-dd …" at
    /// column zero; the last one extends to end-of-file. Returns null when
    /// nothing is readable. Content is sanitized by the caller.
    /// </summary>
    internal static string? ReadLatestCrashEntry()
    {
        try
        {
            var directory = StoragePaths.Logs;
            if (!Directory.Exists(directory))
            {
                return null;
            }
            var newest = new DirectoryInfo(directory)
                .EnumerateFiles("crash-*.log*")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            var head = -1;
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                if (lines[index].Length > 0 && lines[index][0] == '[')
                {
                    head = index;
                    break;
                }
            }
            if (head < 0)
            {
                return null;
            }
            return string.Join('\n', lines[head..].Select(line => line.TrimEnd('\r'))).TrimEnd('\n');
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"(?i)\b(?:ghp_[a-z0-9]{16,}|github_pat_[a-z0-9_]{16,}|sk-[a-z0-9_-]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
