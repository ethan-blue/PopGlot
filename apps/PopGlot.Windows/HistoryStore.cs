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
    string TargetLanguage = "zh-CN",
    // Identity-only prompt template provenance (id/name/revision, never the
    // instruction body). Optional with defaults so existing constructors and
    // snapshots written before these fields existed keep round-tripping.
    string? PromptTemplateId = null,
    string? PromptTemplateName = null,
    ulong? PromptTemplateRevision = null);

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

    private sealed record HistoryPersistRequest(string? Json, bool IsClear, TaskCompletionSource<bool>? Completion, long ClearGeneration);

    // Snapshot bytes are plain UTF-8 with no BOM so the file is byte-stable
    // across writers and parsers never see a stray EF BB BF prefix.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly Lock _gate = new();
    private List<TranslationHistoryEntry> _entries = [];
    private readonly Channel<HistoryPersistRequest> _persistChannel = Channel.CreateUnbounded<HistoryPersistRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    // Guarded by _gate; bumped when a clear is queued so the writer can drop
    // unawaited snapshots the clear makes moot. Read without the lock via
    // Volatile.Read in the single reader loop.
    private long _clearGeneration;
    private readonly Task _writerTask;

    /// <summary>
    /// Path of the most recent corrupt/oversized backup created by a load, if
    /// any. Surfaced so the library can tell the user their data was
    /// quarantined instead of silently replaced.
    /// </summary>
    internal string? LastQuarantinePath { get; private set; }

    /// <summary>
    /// P03: revision of the in-memory data, incremented at every successful
    /// mutation (add / remove / clear / insert-missing / rollback). Consumers
    /// converge on this number instead of rebuilding identical lists; equal
    /// counts with different content can never masquerade as "unchanged".
    /// </summary>
    public long Revision { get; private set; }

    public HistoryStore(string? path = null)
    {
        _path = path ?? StoragePaths.History;
        lock (_gate)
        {
            _entries = LoadUnlocked().ToList();
            Revision = 1;
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
                    // A clear deletes the file, so unawaited snapshots queued
                    // before the newest clear are dead state — writing them
                    // would only delay the awaited delete sitting behind them.
                    // Awaited requests always run: their completion is a
                    // disk-truth promise, never faked.
                    if (!request.IsClear && request.Completion is null &&
                        request.ClearGeneration < Volatile.Read(ref _clearGeneration))
                    {
                        continue;
                    }
                    var ok = true;
                    try
                    {
                        if (request.IsClear)
                        {
                            ok = DeleteDiskFile();
                        }
                        else if (request.Json is not null)
                        {
                            ok = WriteSnapshotToDisk(request.Json);
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        ok = false;
                    }

                    request.Completion?.TrySetResult(ok);
                }
            }
        }
        catch
        {
        }
    }

    private bool DeleteDiskFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool WriteSnapshotToDisk(string json)
    {
        var temporaryPath = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            // Write to the temporary file with exclusive access, flush it all
            // the way to disk, then swap it in atomically — a crash mid-write
            // leaves the previous snapshot intact instead of a torn file.
            var payload = Utf8NoBom.GetBytes(json);
            using (var stream = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            TryDeleteTemp(temporaryPath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteTemp(temporaryPath);
            return false;
        }
    }

    private static void TryDeleteTemp(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
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
        if (_persistChannel.Writer.TryWrite(new HistoryPersistRequest(null, false, tcs, Volatile.Read(ref _clearGeneration))))
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

    /// <summary>Returns the most recent history entries up to count.</summary>
    public IReadOnlyList<TranslationHistoryEntry> GetRecent(int count = 10)
    {
        lock (_gate)
        {
            return _entries.Take(count).ToList();
        }
    }

    /// <summary>Searches recent history entries for source or translation containing query.</summary>
    public IReadOnlyList<TranslationHistoryEntry> Search(string query, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = query.Trim();
        lock (_gate)
        {
            return _entries
                .Where(e => e.Source.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            e.Translation.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Take(maxResults)
                .ToList();
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
                .Where(entry => entry is not null && CanPersist(entry))
                .Where(entry => entry.CreatedAt >= cutoff)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaxEntries)
                .Select(entry => entry with
                {
                    Explanation = entry.Explanation ?? string.Empty,
                    ProtectedTerms = entry.ProtectedTerms ?? [],
                })
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
                Revision++;
                _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, null, _clearGeneration));
                return HistoryAddResult.Stored;
            }
            catch
            {
                return HistoryAddResult.Failed;
            }
        }
    }

    public bool Remove(Guid id) => TryRemove(id, out _);

    /// <summary>
    /// Removes one entry and waits for the disk write. On failure the in-memory
    /// list is restored and <paramref name="removed"/> is null.
    /// </summary>
    public bool TryRemove(Guid id, out TranslationHistoryEntry? removed)
    {
        removed = null;
        TaskCompletionSource<bool> tcs;
        List<TranslationHistoryEntry> previous;
        List<TranslationHistoryEntry> next;
        lock (_gate)
        {
            var hit = _entries.FirstOrDefault(entry => entry.Id == id);
            if (hit is null)
            {
                return false;
            }

            try
            {
                next = _entries.Where(entry => entry.Id != id).ToList();
                var json = JsonSerializer.Serialize(next, JsonOptions);
                previous = _entries;
                _entries = next;
                Revision++;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, tcs, _clearGeneration));
                removed = hit;
            }
            catch
            {
                return false;
            }
        }

        if (!WaitForPersist(tcs))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_entries, next))
                {
                    _entries = previous;
                    Revision++;
                }
            }

            removed = null;
            return false;
        }

        return true;
    }

    internal string FilePath => _path;

    public bool Clear() => TryClear(out _);

    /// <summary>
    /// Clears history and waits for the file delete. A failed delete restores
    /// the previous entries and does not report them as removed.
    /// </summary>
    public bool TryClear(out IReadOnlyList<TranslationHistoryEntry> removed)
    {
        removed = [];
        TaskCompletionSource<bool> tcs;
        List<TranslationHistoryEntry> previous;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return true;
            }

            previous = _entries;
            _entries = [];
            Revision++;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _clearGeneration++;
            _persistChannel.Writer.TryWrite(new HistoryPersistRequest(null, true, tcs, _clearGeneration));
        }

        if (!WaitForPersist(tcs))
        {
            lock (_gate)
            {
                if (_entries.Count == 0)
                {
                    _entries = previous;
                    Revision++;
                }
            }

            return false;
        }

        removed = previous;
        return true;
    }

    /// <summary>
    /// Puts back entries whose ids are absent. Rows already present, including
    /// ones added or edited after the delete, are left as they are.
    /// </summary>
    public bool InsertMissing(IReadOnlyList<TranslationHistoryEntry> items, out int inserted)
        => InsertMissing(items, out inserted, out _);

    public bool InsertMissing(
        IReadOnlyList<TranslationHistoryEntry> items,
        out int inserted,
        out IReadOnlyList<TranslationHistoryEntry> remaining)
    {
        ArgumentNullException.ThrowIfNull(items);
        inserted = 0;
        remaining = [];
        TaskCompletionSource<bool>? tcs = null;
        List<TranslationHistoryEntry>? previous = null;
        List<TranslationHistoryEntry>? next = null;
        var deferred = new List<TranslationHistoryEntry>();
        lock (_gate)
        {
            var ids = _entries.Select(entry => entry.Id).ToHashSet();
            var adding = new List<TranslationHistoryEntry>();
            foreach (var item in items)
            {
                if (item is null || ids.Contains(item.Id))
                {
                    continue;
                }

                if (!CanPersist(item))
                {
                    deferred.Add(item);
                    continue;
                }

                if (_entries.Count + adding.Count >= MaxEntries)
                {
                    deferred.Add(item);
                    continue;
                }

                adding.Add(item);
                ids.Add(item.Id);
            }

            if (adding.Count == 0)
            {
                remaining = deferred;
                return true;
            }

            next = _entries.Concat(adding).OrderByDescending(entry => entry.CreatedAt).ToList();
            string json;
            try
            {
                json = JsonSerializer.Serialize(next, JsonOptions);
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                {
                    remaining = items.Where(item => !_entries.Any(entry => entry.Id == item.Id)).ToArray();
                    return false;
                }
            }
            catch
            {
                remaining = items.Where(item => !_entries.Any(entry => entry.Id == item.Id)).ToArray();
                return false;
            }

            previous = _entries;
            _entries = next;
            Revision++;
            inserted = adding.Count;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, tcs, _clearGeneration));
        }

        if (tcs is null)
        {
            return true;
        }

        if (!WaitForPersist(tcs))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_entries, next))
                {
                    _entries = previous;
                    Revision++;
                }
            }

            inserted = 0;
            remaining = items;
            return false;
        }

        remaining = deferred;
        return true;
    }

    private static bool WaitForPersist(TaskCompletionSource<bool> tcs) =>
        tcs.Task.Wait(TimeSpan.FromSeconds(5)) && tcs.Task.Result;

    /// <summary>
    /// Replaces all history entries with the provided valid entries.
    /// Filters expired, sensitive or oversized entries, and writes snapshot to disk.
    /// </summary>
    public bool RestoreEntries(IEnumerable<TranslationHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        var valid = new List<TranslationHistoryEntry>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.CreatedAt < cutoff)
            {
                continue;
            }
            if (!CanPersist(entry))
            {
                continue;
            }
            valid.Add(entry);
        }
        var next = valid
            .OrderByDescending(e => e.CreatedAt)
            .Take(MaxEntries)
            .ToList();

        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(next, JsonOptions);
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                {
                    return false;
                }
                _entries = next;
                Revision++;
                _persistChannel.Writer.TryWrite(new HistoryPersistRequest(json, false, null, _clearGeneration));
                return true;
            }
            catch
            {
                return false;
            }
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
        if (entry.Source is null || entry.Translation is null ||
            entry.Source.Length > MaxSourceCharacters ||
            entry.Translation.Length > MaxTranslationCharacters)
        {
            return false;
        }
        var sensitive = SensitiveContentRegex();
        if (sensitive.IsMatch(entry.Source) || sensitive.IsMatch(entry.Translation) ||
            (entry.Explanation is not null && sensitive.IsMatch(entry.Explanation)))
        {
            return false;
        }
        if (entry.ProtectedTerms is not null)
        {
            foreach (var term in entry.ProtectedTerms)
            {
                if (term is null || sensitive.IsMatch(term))
                {
                    return false;
                }
            }
        }
        return true;
    }

    [GeneratedRegex(
        @"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\bpassword\s*[:=]|\bapi[_-]?key\s*[:=]|\bsecret\s*[:=]|\bsk-[a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{16,}|\bghp_[a-zA-Z0-9]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveContentRegex();
}
