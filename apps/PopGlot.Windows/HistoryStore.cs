using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal sealed record TranslationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string SourceKind,
    string Source,
    string Translation,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    string SourceLanguage = "auto",
    string TargetLanguage = "zh-CN");

internal enum HistoryAddResult
{
    Stored,
    Disabled,
    SkippedSensitiveOrLarge,
    Failed,
}

internal sealed partial class HistoryStore : IHistoryRepository
{
    private const int MaxEntries = 200;
    private const int MaxSourceCharacters = 4_000;
    private const int MaxTranslationCharacters = 8_000;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record HistoryPersistRequest(string? Json, bool IsClear, TaskCompletionSource<bool>? Completion);

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<TranslationHistoryEntry> _entries = [];
    private readonly Channel<HistoryPersistRequest> _persistChannel = Channel.CreateUnbounded<HistoryPersistRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    /// <summary>
    /// Path of the most recent corrupt/oversized backup created by a load, if
    /// any. Surfaced so the library can tell the user their data was
    /// quarantined instead of silently replaced.
    /// </summary>
    internal string? LastQuarantinePath { get; private set; }

    public HistoryStore(string? path = null)
    {
        _path = path ?? StoragePaths.History;
        lock (_gate)
        {
            _entries = LoadUnlocked().ToList();
        }
        _writerTask = Task.Run(ProcessPersistenceQueueAsync);
    }

    private async Task ProcessPersistenceQueueAsync()
    {
        var reader = _persistChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var request))
                {
                    if (request.IsClear)
                    {
                        DeleteDiskFile();
                    }
                    else if (request.Json is not null)
                    {
                        WriteSnapshotToDisk(request.Json);
                    }
                    request.Completion?.TrySetResult(true);
                }
            }
        }
        catch
        {
        }
    }

    private void DeleteDiskFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteSnapshotToDisk(string json)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Blocks until all pending writes in the background persistence queue
    /// are committed to disk. Safe for testing and application exit.
    /// </summary>
    public void Flush(int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_persistChannel.Writer.TryWrite(new HistoryPersistRequest(null, false, tcs)))
        {
            tcs.Task.Wait(timeoutMs);
        }
    }

    public IReadOnlyList<TranslationHistoryEntry> Load()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    private IReadOnlyList<TranslationHistoryEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }
            if (new FileInfo(_path).Length > MaxFileBytes)
            {
                // Oversized is treated as unreadable, but the bytes are the
                // user's history: quarantine before a later save replaces them.
                QuarantineUnlocked();
                return [];
            }
            var entries = JsonSerializer.Deserialize<List<TranslationHistoryEntry>>(
                File.ReadAllText(_path), JsonOptions) ?? [];
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            return entries
                .Where(entry => entry is not null)
                .Where(entry => entry.CreatedAt >= cutoff)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt file preservation: back it up so the next save cannot
            // destroy the only copy of the user's history.
            QuarantineUnlocked();
            return [];
        }
    }

    private void QuarantineUnlocked()
    {
        try
        {
            if (File.Exists(_path))
            {
                var quarantinePath = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                File.Copy(_path, quarantinePath, overwrite: true);
                LastQuarantinePath = quarantinePath;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public HistoryAddResult TryAdd(TranslationHistoryEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!enabled)
        {
            return HistoryAddResult.Disabled;
        }
        if (!CanPersist(entry))
        {
            return HistoryAddResult.SkippedSensitiveOrLarge;
        }

        lock (_gate)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow - MaxAge;
                var existing = _entries
                    .Where(item => item.CreatedAt >= cutoff
                        && !(item.Source == entry.Source && item.TargetLanguage == entry.TargetLanguage))
                    .Take(MaxEntries - 1);
                var next = new List<TranslationHistoryEntry> { entry };
                next.AddRange(existing);

                var json = JsonSerializer.Serialize(next, JsonOptions);
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                {
                    return HistoryAddResult.Failed;
                }

                _entries = next;
                _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, null));
                return HistoryAddResult.Stored;
            }
            catch
            {
                return HistoryAddResult.Failed;
            }
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            try
            {
                var remaining = _entries.Where(entry => entry.Id != id).ToList();
                var json = JsonSerializer.Serialize(remaining, JsonOptions);
                _entries = remaining;
                _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, null));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            _entries = [];
            _persistChannel.Writer.TryWrite(new HistoryPersistRequest(null, true, null));
            return true;
        }
    }

    public string ExportToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,CreatedAt,SourceKind,SourceLanguage,TargetLanguage,Source,Translation,Explanation");
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                sb.AppendLine($"{e.Id},{e.CreatedAt:O},{CsvEscape(e.SourceKind)},{CsvEscape(e.SourceLanguage)},{CsvEscape(e.TargetLanguage)},{CsvEscape(e.Source)},{CsvEscape(e.Translation)},{CsvEscape(e.Explanation)}");
            }
        }
        return sb.ToString();
    }

    public string ExportToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PopGlot 翻译历史记录\n");
        sb.AppendLine("| 时间 | 方式 | 语言对 | 原文 | 译文 |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                var time = e.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                var pair = $"{e.SourceLanguage} → {e.TargetLanguage}";
                var src = e.Source.Replace("|", "\\|").Replace("\n", " ");
                var tr = e.Translation.Replace("|", "\\|").Replace("\n", " ");
                sb.AppendLine($"| {time} | {e.SourceKind} | {pair} | {src} | {tr} |");
            }
        }
        return sb.ToString();
    }

    private static string CsvEscape(string? value)
    {
        var text = value ?? string.Empty;
        // Same spreadsheet-formula contract as the vocabulary export: a field
        // starting with =,+,-,@ (after leading controls/spaces) gains an
        // in-quote apostrophe so Excel never executes it as a formula.
        var safe = IsFormulaPrefixed(text) ? "'" + text : text;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static bool IsFormulaPrefixed(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsControl(ch) || ch == ' ')
            {
                continue;
            }
            return ch is '=' or '+' or '-' or '@';
        }
        return false;
    }

    internal static bool CanPersist(TranslationHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Source.Length > MaxSourceCharacters ||
            entry.Translation.Length > MaxTranslationCharacters)
        {
            return false;
        }
        return !SensitiveContentRegex().IsMatch($"{entry.Source}\n{entry.Translation}");
    }

    [GeneratedRegex(
        @"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\bpassword\s*[:=]|\bapi[_-]?key\s*[:=]|\bsecret\s*[:=]|\bsk-[a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{16,}|\bghp_[a-zA-Z0-9]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveContentRegex();
}
