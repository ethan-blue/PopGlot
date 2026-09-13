using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PopGlot.Windows;

/// <summary>
/// Bounded, allowlisted crash diagnostics — the single writing path for the
/// global crash barriers.
///
/// C03: entries are structured, never message dumps. Only the exception type,
/// a controlled stage, the HResult code, a random correlation id and stack
/// frames (user paths redacted) are written. Arbitrary exception messages
/// never reach disk at all: a blacklist cannot know every secret shape, so no
/// free-form text is logged in the first place. Files rotate at 1 MiB, the
/// whole directory is capped at 10 MiB and 7 days, and no method here may
/// ever throw.
/// </summary>
internal static partial class DiagnosticsLog
{
    internal const int MaxMessageCharacters = 400;
    internal const int MaxStackLines = 24;
    internal const long MaxFileBytes = 1024 * 1024;
    internal const long MaxTotalBytes = 10 * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly Lock CleanupGate = new();
    private static DateTime _lastCleanupUtc = DateTime.MinValue;

    /// <summary>Controlled pipeline stages; never free-form caller text.</summary>
    internal enum DiagnosticsStage
    {
        Unknown,
        Startup,
        Hotkey,
        Clipboard,
        Selection,
        Capture,
        Ocr,
        Translation,
        Streaming,
        Tts,
        History,
        Vocabulary,
        Settings,
        Tray,
        Export,
    }

    private sealed record LogQueueItem(
        string? Directory,
        string? Entry,
        TaskCompletionSource<bool>? FlushTcs);

    private static readonly BlockingCollection<LogQueueItem> WriteQueue = new(new ConcurrentQueue<LogQueueItem>(), 1024);
    private static readonly Thread WriterThread;

    static DiagnosticsLog()
    {
        WriterThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "PopGlot.DiagnosticsWriter",
        };
        WriterThread.Start();
    }

    private static void ProcessQueue()
    {
        foreach (var item in WriteQueue.GetConsumingEnumerable())
        {
            try
            {
                if (item.FlushTcs is not null)
                {
                    item.FlushTcs.TrySetResult(true);
                    continue;
                }

                if (!string.IsNullOrEmpty(item.Directory) && !string.IsNullOrEmpty(item.Entry))
                {
                    Directory.CreateDirectory(item.Directory);
                    var file = Path.Combine(item.Directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
                    RotateIfNeeded(file);
                    File.AppendAllText(file, item.Entry, Encoding.UTF8);
                    CleanupIfStale(item.Directory);
                }
            }
            catch
            {
                // Background diagnostics write must never throw or crash the thread.
            }
        }
    }

    /// <summary>
    /// Writes one structured crash entry to <see cref="StoragePaths.Logs"/> via a background
    /// queue and returns the correlation id it was filed under (empty string on failure).
    /// </summary>
    public static string Log(Exception exception, DiagnosticsStage stage = DiagnosticsStage.Unknown)
    {
        try
        {
            var eventId = NewEventId();
            var directory = StoragePaths.Logs;
            var entry = BuildEntry(exception, stage, eventId);
            if (!WriteQueue.TryAdd(new LogQueueItem(directory, entry, null), 100))
            {
                return string.Empty;
            }
            return eventId;
        }
        catch
        {
            // Diagnostics must never take the process down.
            return string.Empty;
        }
    }

    /// <summary>
    /// Test seam & shutdown helper: blocks until all currently queued log writes are flushed to disk.
    /// </summary>
    internal static void Flush(int timeoutMs = 5000)
    {
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (WriteQueue.TryAdd(new LogQueueItem(null, null, tcs), 500))
            {
                tcs.Task.Wait(TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs)));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// The tray-facing summary: controlled copy plus the correlation id of the
    /// filed entry. Exception text never reaches the balloon, so nothing the
    /// user can read (or support can match) carries secret-shaped content.
    /// </summary>
    public static string CrashSummary(string eventId) =>
        string.IsNullOrEmpty(eventId)
            ? "出现未处理问题，未能写入诊断记录"
            : $"出现未处理问题（事件 {eventId}）";

    /// <summary>
    /// Redacts auth headers, key-shaped literals, key/value secrets, URL
    /// queries, hex blobs and Windows user-profile paths. Used for the
    /// structured remnants (stack frames) and as the re-scan battery for any
    /// text leaving the machine.
    /// </summary>
    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        // Header rule first: it consumes scheme + token together so neither
        // part of the credential survives.
        var result = AuthorizationRegex().Replace(text, "${prefix}${scheme} [redacted]");
        result = BearerRegex().Replace(result, "[redacted-token]");
        result = KeyShapedRegex().Replace(result, "[redacted-key]");
        result = KeyValueSecretRegex().Replace(result, "${label}[redacted-secret]");
        result = HexBlobRegex().Replace(result, "[redacted-blob]");
        result = UrlQueryRegex().Replace(result, "${base}?…");
        result = UserPathRegex().Replace(result, "${prefix}[user]");
        if (result.Length > MaxMessageCharacters)
        {
            result = result[..MaxMessageCharacters] + "…";
        }
        return result;
    }

    /// <summary>
    /// C03 export boundary: any text about to leave the machine (log viewer,
    /// support export) runs the battery again, no matter which component
    /// produced it. This is defense in depth — the allowlist above already
    /// keeps free-form text out of the log in the first place.
    /// </summary>
    public static string SanitizeForExport(string? text) => Sanitize(text);

    internal static string BuildEntry(
        Exception exception,
        DiagnosticsStage stage = DiagnosticsStage.Unknown,
        string? eventId = null)
    {
        eventId ??= NewEventId();
        var builder = new StringBuilder();
        builder.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().Name}")
            .Append($"  stage={stage.ToString().ToLowerInvariant()}")
            .Append($" code=0x{exception.HResult:X8}")
            .Append($" event={eventId}")
            .AppendLine();
        // V01: free stack-trace text is omitted by default. Frames come only
        // from the runtime's structured API — a method identity is written
        // only when the runtime itself resolves it, so identifier-shaped
        // free text (e.g. "at synthetic_secret_123") in a rewritten stack
        // can never enter the log.
        var structured = new System.Diagnostics.StackTrace(exception, fNeedFileInfo: false);
        var kept = 0;
        foreach (var frame in structured.GetFrames() ?? Array.Empty<System.Diagnostics.StackFrame>())
        {
            if (kept >= MaxStackLines)
            {
                builder.AppendLine("  …");
                break;
            }
            if (frame.GetMethod() is not { } method)
            {
                continue;
            }
            var declaring = method.DeclaringType?.FullName;
            var name = string.IsNullOrEmpty(declaring) ? method.Name : $"{declaring}.{method.Name}";
            if (name.Length > 160)
            {
                name = name[..160];
            }
            builder.Append("  at ").AppendLine(name);
            kept++;
        }
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>Rotates a log file that reached the per-file cap.</summary>
    internal static void RotateIfNeeded(string file)
    {
        if (!File.Exists(file) || new FileInfo(file).Length < MaxFileBytes)
        {
            return;
        }
        var rotated = $"{file}.{DateTime.Now:HHmmss}.rot";
        File.Move(file, rotated, overwrite: true);
    }

    /// <summary>
    /// Retention and total-size bounds. Rate-limited internally so an
    /// exception storm cannot turn every crash into a directory scan.
    /// </summary>
    internal static void CleanupIfStale(string directory, bool force = false)
    {
        lock (CleanupGate)
        {
            var nowUtc = DateTime.UtcNow;
            if (!force && nowUtc - _lastCleanupUtc < TimeSpan.FromMinutes(1))
            {
                return;
            }
            _lastCleanupUtc = nowUtc;
        }

        try
        {
            var cutoffUtc = DateTime.UtcNow - Retention;
            // Only files this app actually owns are ever touched.
            var files = Directory.EnumerateFiles(directory, "crash-*.log*")
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.LastWriteTimeUtc)
                .ToList();
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc < cutoffUtc)
                {
                    file.Delete();
                }
            }

            var total = files.Where(f => f.LastWriteTimeUtc >= cutoffUtc).Sum(f => f.Length);
            foreach (var file in files.Where(f => f.LastWriteTimeUtc >= cutoffUtc).ToList())
            {
                if (total <= MaxTotalBytes)
                {
                    break;
                }
                total -= file.Length;
                file.Delete();
            }
        }
        catch
        {
            // Bounded diagnostics must never throw.
        }
    }

    private static string NewEventId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    [GeneratedRegex(
        @"(?i)bearer\s+[a-z0-9._~+/=-]{8,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(
        @"(?i)\b(sk-[a-z0-9_-]{16,}|AIza[a-z0-9_-]{16,}|ghp_[a-z0-9]{16,}|xox[bpars]-[a-z0-9-]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyShapedRegex();

    [GeneratedRegex(
        @"(?i)\b(?<label>api[-_ ]?key|token|secret|password|passwd|pwd)\s*[=:：]\s*""?[a-z0-9._~+/=-]{6,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>authorization\s*[:=]\s*)(?<scheme>bearer|basic|digest|token)?\s*[a-z0-9._~+/=-]{12,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"\b[a-f0-9]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexBlobRegex();

    [GeneratedRegex(@"(?<base>https?://[^\s?""']+)\?[^\s""']+")]
    private static partial Regex UrlQueryRegex();

    /// <summary>C:\Users\name, \\Users\name, /Users/name — the account name never survives.</summary>
    [GeneratedRegex(
        @"(?i)(?<prefix>(?:[a-z]:)?[\\/]+(?:users|benutzer|utilisateurs|usuarios)[\\/]+)[^\\\r\n""/]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserPathRegex();
}
