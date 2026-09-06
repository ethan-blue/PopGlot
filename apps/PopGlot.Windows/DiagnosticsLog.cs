using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PopGlot.Windows;

/// <summary>
/// Bounded, sanitized crash diagnostics — the single writing path for the
/// global crash barriers.
///
/// Exception messages can carry Authorization headers, key-shaped literals or
/// request URLs whose query string is the user's text; nothing raw reaches the
/// log file or the tray balloon. Files rotate at 1 MiB, the whole directory is
/// capped at 10 MiB and 7 days, and no method here may ever throw.
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

    /// <summary>Writes one sanitized crash entry to <see cref="StoragePaths.Logs"/>.</summary>
    public static void Log(Exception exception)
    {
        try
        {
            var directory = StoragePaths.Logs;
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            RotateIfNeeded(file);
            File.AppendAllText(file, BuildEntry(exception), Encoding.UTF8);
            CleanupIfStale(directory);
        }
        catch
        {
            // Diagnostics must never take the process down.
        }
    }

    /// <summary>
    /// The tray-facing summary: sanitized and short. Balloons are glanceable
    /// messages, not exception dumps.
    /// </summary>
    public static string CrashSummary(Exception exception)
    {
        var sanitized = Sanitize(exception.Message);
        const int balloonLimit = 160;
        return sanitized.Length > balloonLimit ? sanitized[..balloonLimit] + "…" : sanitized;
    }

    /// <summary>Redacts auth headers, key-shaped literals, URL queries and long hex blobs.</summary>
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
        result = HexBlobRegex().Replace(result, "[redacted-blob]");
        result = UrlQueryRegex().Replace(result, "${base}?…");
        if (result.Length > MaxMessageCharacters)
        {
            result = result[..MaxMessageCharacters] + "…";
        }
        return result;
    }

    internal static string BuildEntry(Exception exception)
    {
        var builder = new StringBuilder();
        builder.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().Name}: ")
            .AppendLine(Sanitize(exception.Message));
        var stack = exception.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            var kept = 0;
            foreach (var rawLine in stack.Split('\n'))
            {
                if (kept >= MaxStackLines)
                {
                    builder.AppendLine("  …");
                    break;
                }
                var line = rawLine.TrimEnd('\r');
                if (line.Length > 0)
                {
                    builder.Append("  ").AppendLine(Sanitize(line));
                    kept++;
                }
            }
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

    [GeneratedRegex(
        @"(?i)bearer\s+[a-z0-9._~+/=-]{8,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(
        @"(?i)\b(sk-[a-z0-9_-]{16,}|AIza[a-z0-9_-]{16,}|ghp_[a-z0-9]{16,}|xox[bpars]-[a-z0-9-]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyShapedRegex();

    [GeneratedRegex(
        @"(?i)(?<prefix>authorization\s*[:=]\s*)(?<scheme>bearer|basic|digest|token)?\s*[a-z0-9._~+/=-]{12,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"\b[a-f0-9]{40,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexBlobRegex();

    [GeneratedRegex(@"(?<base>https?://[^\s?""']+)\?[^\s""']+")]
    private static partial Regex UrlQueryRegex();
}
