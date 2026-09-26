using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PopGlot.Windows.Services;

public sealed record VocabularyWord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Word,
    string Translation,
    string Phonetic,
    string Explanation,
    string SourceLanguage,
    string TargetLanguage,
    List<string> Tags);

/// <summary>Outcome of a wordbook mutation; a star that was not persisted is never reported as success.</summary>
internal enum VocabularySaveStatus
{
    Persisted,
    WriteFailed,
    EntryTooLarge,
    StoreFull,
    FileTooLarge,
    /// <summary>C02: the file on disk could not be safely read, so mutations are refused to protect it.</summary>
    StoreUnreadable,
}

/// <summary>C02: how the last load of the storage file ended.</summary>
internal enum VocabularyLoadState
{
    /// <summary>File read and parsed (or absent — a fresh store).</summary>
    Ok,
    /// <summary>File exceeds MaxFileBytes; never deserialized, never overwritten.</summary>
    TooLarge,
    /// <summary>File locked by another process; contents unknown, mutations blocked.</summary>
    Locked,
    /// <summary>Access denied; contents unknown, mutations blocked.</summary>
    NoAccess,
    /// <summary>Unparseable content; quarantined as .corrupt-*, store continues empty.</summary>
    Corrupt,
}

internal sealed record VocabularySaveResult(bool Persisted, bool Starred, VocabularySaveStatus Status)
{
    public static VocabularySaveResult Saved(bool starred) =>
        new(true, starred, VocabularySaveStatus.Persisted);

    /// <summary>One agreed wording for every star surface, naming the actual reason.</summary>
    public string DescribeFailureZh() => Status switch
    {
        VocabularySaveStatus.EntryTooLarge =>
            "未保存到本机：单条原文/译文最多 8000 字符，请缩短后重试。",
        VocabularySaveStatus.StoreFull =>
            "未保存到本机：生词本已达 10000 条上限，请先导出并清理。",
        VocabularySaveStatus.FileTooLarge =>
            "未保存到本机：生词本文件超过 32MiB 上限，请先导出并清理。",
        VocabularySaveStatus.StoreUnreadable =>
            "未保存到本机：生词本文件无法安全读取（超过 32MiB 上限或被其他程序占用），已进入只读保护，新收藏不会写入。请在本机数据目录中清理该文件后重试。",
        _ => "未保存到本机，请重试。",
    };
}

/// <summary>
/// Local persistent wordbook / vocabulary store for starred words and translations.
/// Supports Anki TSV export, CSV, and Markdown.
///
/// Star identity is word + language pair (case preserved: <c>Foo</c> and
/// <c>foo</c> stay distinct code identifiers). Mutations commit to disk first
/// and only then swap the in-memory snapshot, so a failed write never shows a
/// fake star; capacity and length limits reject explicitly instead of silently
/// dropping older entries.
/// </summary>
internal sealed class VocabularyStore : IVocabularyRepository
{
    internal const int MaxEntries = 10_000;
    internal const int MaxEntryCharacters = 8_000;
    internal const int MaxFileBytes = 32 * 1024 * 1024;

    // Resolve lazily; a cached path can escape an isolation root when the CLR
    // eagerly runs static field initializers before the test bootstrap.
    private static string DefaultStoragePath => StoragePaths.Vocabulary;

    private readonly string _storagePath;
    private readonly Lock _gate = new();
    private List<VocabularyWord> _words = [];

    private sealed record PersistRequest(string? Json, TaskCompletionSource<bool>? Completion);

    private readonly Channel<PersistRequest> _persistChannel = Channel.CreateUnbounded<PersistRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _writerTask;

    /// <summary>A08: strict UTF-8 — silently-decoded garbage is corruption.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>C02: how the last load ended; drives the read-only protection.</summary>
    public VocabularyLoadState LoadState { get; private set; } = VocabularyLoadState.Ok;

    /// <summary>
    /// A08: true when a corrupt file was copied aside AND the copy was
    /// verified byte-identical, so the original content is recoverable
    /// elsewhere even though the live file stays read-only.
    /// </summary>
    public bool QuarantinedSafely { get; private set; }

    /// <summary>
    /// True when the file holds (or may hold) data this process could not
    /// read — including corrupt files, whose default fate is read-only
    /// (A08). Mutations stay blocked so a later save can never replace the
    /// unreadable original with an empty or one-entry snapshot.
    /// </summary>
    private bool LoadBlocked =>
        LoadState is VocabularyLoadState.TooLarge or VocabularyLoadState.Locked
            or VocabularyLoadState.NoAccess or VocabularyLoadState.Corrupt;

    public VocabularyStore(string? customPath = null)
    {
        _storagePath = customPath ?? DefaultStoragePath;
        Load();
        _writerTask = Task.Run(ProcessPersistenceQueueAsync);
    }

    public string? LastPersistError { get; private set; }

    private async Task ProcessPersistenceQueueAsync()
    {
        var reader = _persistChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var request))
                {
                    var ok = true;
                    if (request.Json is not null)
                    {
                        ok = WriteSnapshotToDisk(request.Json);
                    }
                    request.Completion?.TrySetResult(ok);
                }
            }
        }
        catch
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
        if (_persistChannel.Writer.TryWrite(new PersistRequest(null, tcs)))
        {
            tcs.Task.Wait(timeoutMs);
        }
    }

    internal string StoragePath => _storagePath;

    public IReadOnlyList<VocabularyWord> GetAll()
    {
        lock (_gate)
        {
            return _words.OrderByDescending(w => w.CreatedAt).ToList();
        }
    }

    public bool IsStarred(string word, string sourceLang = "auto", string targetLang = "zh-CN")
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        lock (_gate)
        {
            return _words.Any(w => SameIdentity(w, word.Trim(), sourceLang, targetLang));
        }
    }

    public VocabularySaveResult ToggleStar(
        string word,
        string translation,
        string phonetic = "",
        string explanation = "",
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        List<string>? tags = null)
    {
        return ToggleStarAsync(word, translation, phonetic, explanation, sourceLang, targetLang, tags)
            .GetAwaiter().GetResult();
    }

    public async Task<VocabularySaveResult> ToggleStarAsync(
        string word,
        string translation,
        string phonetic = "",
        string explanation = "",
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        List<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return new VocabularySaveResult(false, false, VocabularySaveStatus.WriteFailed);
        }
        var trimmed = word.Trim();

        bool willBeStarred;
        List<VocabularyWord> next;
        List<VocabularyWord> previous;
        string json;
        TaskCompletionSource<bool> tcs;

        lock (_gate)
        {
            // C02: an unreadable file must never be replaced by a snapshot.
            if (LoadBlocked)
            {
                return new VocabularySaveResult(false, false, VocabularySaveStatus.StoreUnreadable);
            }
            var existing = _words.FirstOrDefault(w => SameIdentity(w, trimmed, sourceLang, targetLang));
            if (existing is not null)
            {
                willBeStarred = false;
                next = _words.Where(w => !ReferenceEquals(w, existing)).ToList();
            }
            else
            {
                willBeStarred = true;
                if (trimmed.Length > MaxEntryCharacters || (translation?.Length ?? 0) > MaxEntryCharacters)
                {
                    return new VocabularySaveResult(false, false, VocabularySaveStatus.EntryTooLarge);
                }
                if (_words.Count >= MaxEntries)
                {
                    // Never silently drop the oldest entry to make room.
                    return new VocabularySaveResult(false, false, VocabularySaveStatus.StoreFull);
                }
                next =
                [
                    new VocabularyWord(
                        Guid.NewGuid(),
                        DateTimeOffset.UtcNow,
                        trimmed,
                        translation?.Trim() ?? string.Empty,
                        phonetic?.Trim() ?? string.Empty,
                        explanation?.Trim() ?? string.Empty,
                        sourceLang,
                        targetLang,
                        tags ?? []),
                    .. _words,
                ];
            }

            try
            {
                json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                {
                    return new VocabularySaveResult(false, !willBeStarred, VocabularySaveStatus.FileTooLarge);
                }
            }
            catch
            {
                return new VocabularySaveResult(false, !willBeStarred, VocabularySaveStatus.WriteFailed);
            }

            previous = _words;
            _words = next;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _persistChannel.Writer.TryWrite(new PersistRequest(json, tcs));
        }

        var written = await tcs.Task.ConfigureAwait(false);
        if (!written)
        {
            lock (_gate)
            {
                _words = previous;
            }
            return new VocabularySaveResult(false, !willBeStarred, VocabularySaveStatus.WriteFailed);
        }

        return VocabularySaveResult.Saved(willBeStarred);
    }

    public bool Remove(Guid id)
    {
        TaskCompletionSource<bool> tcs;
        List<VocabularyWord> previous;
        lock (_gate)
        {
            // C02: refuse to persist while the on-disk file is unreadable.
            if (LoadBlocked)
            {
                return false;
            }
            if (_words.All(w => w.Id != id))
            {
                return true; // Nothing to remove; the requested state already holds.
            }
            var next = _words.Where(w => w.Id != id).ToList();
            string json;
            try
            {
                json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
                if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
            previous = _words;
            _words = next;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _persistChannel.Writer.TryWrite(new PersistRequest(json, tcs));
        }
        var written = tcs.Task.GetAwaiter().GetResult();
        if (!written)
        {
            lock (_gate)
            {
                _words = previous;
            }
            return false;
        }
        return true;
    }

    public bool Clear()
    {
        TaskCompletionSource<bool> tcs;
        List<VocabularyWord> previous;
        lock (_gate)
        {
            // C02: clearing must never be the operation that destroys an
            // unreadable file full of unknown entries.
            if (LoadBlocked)
            {
                return false;
            }
            var next = new List<VocabularyWord>();
            var json = JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true });
            previous = _words;
            _words = next;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _persistChannel.Writer.TryWrite(new PersistRequest(json, tcs));
        }
        var written = tcs.Task.GetAwaiter().GetResult();
        if (!written)
        {
            lock (_gate)
            {
                _words = previous;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Replaces the current wordbook entries with the supplied words,
    /// after checking entry limits and budgets. Queues persistence snapshot.
    /// Returns true when successfully committed to disk.
    /// </summary>
    public bool RestoreWords(IEnumerable<VocabularyWord> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var valid = new List<VocabularyWord>();
        foreach (var word in words)
        {
            if (word is null) continue;
            if (string.IsNullOrWhiteSpace(word.Word)) continue;
            if (word.Word.Length > MaxEntryCharacters || (word.Translation?.Length ?? 0) > MaxEntryCharacters) continue;
            valid.Add(word);
        }
        var capped = valid.Take(MaxEntries).ToList();

        TaskCompletionSource<bool> tcs;
        List<VocabularyWord> previous;
        lock (_gate)
        {
            if (LoadBlocked)
            {
                return false;
            }
            var json = JsonSerializer.Serialize(capped, new JsonSerializerOptions { WriteIndented = true });
            if (StrictUtf8.GetByteCount(json) > MaxFileBytes)
            {
                return false;
            }
            previous = _words;
            _words = capped;
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _persistChannel.Writer.TryWrite(new PersistRequest(json, tcs));
        }
        var written = tcs.Task.GetAwaiter().GetResult();
        if (!written)
        {
            lock (_gate)
            {
                _words = previous;
            }
            return false;
        }
        return true;
    }

    /// <summary>Word equality is case-sensitive (code identifiers); language tags compare loosely.</summary>
    private static bool SameIdentity(VocabularyWord entry, string word, string sourceLang, string targetLang) =>
        string.Equals(entry.Word, word, StringComparison.Ordinal) &&
        string.Equals(entry.SourceLanguage, sourceLang, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.TargetLanguage, targetLang, StringComparison.OrdinalIgnoreCase);

    public string ExportToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,CreatedAt,Word,Translation,Phonetic,Explanation,SourceLanguage,TargetLanguage,Tags");
        lock (_gate)
        {
            foreach (var w in _words)
            {
                var tags = string.Join(";", w.Tags);
                sb.AppendLine($"{w.Id},{w.CreatedAt:O},{CsvEscape(w.Word)},{CsvEscape(w.Translation)},{CsvEscape(w.Phonetic)},{CsvEscape(w.Explanation)},{CsvEscape(w.SourceLanguage)},{CsvEscape(w.TargetLanguage)},{CsvEscape(tags)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Exports to Anki TSV format (Front, Back, Phonetic, Explanation, Tags)</summary>
    public string ExportToAnkiTsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:true");
        sb.AppendLine("#tags column:5");

        lock (_gate)
        {
            foreach (var w in _words)
            {
                var front = EscapeAnki(w.Word);
                var back = EscapeAnki(w.Translation);
                var phonetic = EscapeAnki(string.IsNullOrEmpty(w.Phonetic) ? "" : $"[{w.Phonetic}]");
                var note = EscapeAnki(w.Explanation);
                var tags = string.Join(" ", w.Tags.Select(t => t.Replace(" ", "_")));
                sb.AppendLine($"{front}\t{back}\t{phonetic}\t{note}\t{tags}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Exports to readable Markdown table</summary>
    public string ExportToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# PopGlot 生词本与收藏夹\n");
        sb.AppendLine("| 原文 | 译文 | 音标 | 解释 / 笔记 | 收藏时间 |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");

        lock (_gate)
        {
            foreach (var w in _words)
            {
                var word = w.Word.Replace("|", "\\|").Replace("\n", " ");
                var trans = w.Translation.Replace("|", "\\|").Replace("\n", " ");
                var phon = string.IsNullOrEmpty(w.Phonetic) ? "-" : $"[{w.Phonetic}]";
                var exp = string.IsNullOrEmpty(w.Explanation) ? "-" : w.Explanation.Replace("|", "\\|").Replace("\n", " ");
                var time = w.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                sb.AppendLine($"| **{word}** | {trans} | {phon} | {exp} | {time} |");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// CSV quoting plus spreadsheet-formula protection: a field whose first
    /// meaningful character (skipping leading controls/spaces) is =, +, - or @
    /// gains a leading apostrophe, the standard Excel neutralizer. Default
    /// exports are safe; a raw mode would have to be a deliberate opt-in.
    /// </summary>
    private static string CsvEscape(string? value)
    {
        var text = value ?? string.Empty;
        // The neutralizer goes INSIDE the quoted field: Excel treats a
        // leading apostrophe as text-marker, and quoting stays structurally
        // correct (an apostrophe outside the quotes would shift the columns).
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

    /// <summary>Anki headers declare #html:true, so HTML-significant characters must be escaped.</summary>
    private static string EscapeAnki(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\t", " ")
            .Replace("\n", "<br>")
            .Replace("\"", "&quot;");

    private void Load()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;
            // A08: ONE handle, cumulatively bounded — a stat-then-read race
            // (file growing between check and read) can no longer pull an
            // unbounded payload into memory, and strict UTF-8 decoding means
            // silently-decoded garbage counts as corruption.
            var payload = ReadBounded(_storagePath, MaxFileBytes);
            if (payload is null)
            {
                LoadState = VocabularyLoadState.TooLarge;
                return;
            }
            // System.Text.Json accepts a UTF-8 BOM when parsing bytes, but not
            // a decoded U+FEFF at the start of a string. Older PopGlot files
            // were commonly written with this standard BOM, so strip exactly
            // that byte prefix before strict UTF-8 decoding.
            var offset = payload.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
            var json = StrictUtf8.GetString(payload, offset, payload.Length - offset);
            using var document = JsonDocument.Parse(json);
            List<VocabularyWord>? items = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => JsonSerializer.Deserialize<List<VocabularyWord>>(json),
                // Older builds persisted the first starred entry as one JSON
                // object. That is valid user data, not corruption. The next
                // real mutation will migrate it atomically to the array format.
                JsonValueKind.Object =>
                    JsonSerializer.Deserialize<VocabularyWord>(json) is { } legacy
                        ? [legacy]
                        : null,
                _ => throw new JsonException("Vocabulary root must be an array or a legacy word object."),
            };
            if (items is null)
            {
                throw new JsonException("Vocabulary payload could not be deserialized.");
            }
            // A JSON array may contain null entries; they are not data.
            _words = items.Where(item => item is not null).ToList();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The file could not be READ, but it may still hold valid data
            // (locked by a sync client, permission denied). Mutations are
            // blocked so the unreadable original is never overwritten.
            LoadState = exception is IOException
                ? VocabularyLoadState.Locked
                : VocabularyLoadState.NoAccess;
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Log(exception, DiagnosticsLog.DiagnosticsStage.Vocabulary);
            // A08: corrupt content defaults to read-only. The original is
            // quarantined and the copy VERIFIED before anything may treat
            // the store as empty-and-writable again (via RetryLoad plus an
            // explicit user choice to start fresh).
            LoadState = VocabularyLoadState.Corrupt;
            QuarantinedSafely = TryQuarantine();
        }
    }

    /// <summary>
    /// A08: a single-handle read with a hard cumulative cap. Returns null
    /// when the file exceeds the cap — before or during the read — so the
    /// payload can never exceed the budget.
    /// </summary>
    private static byte[]? ReadBounded(string path, long cap)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > cap)
        {
            return null;
        }
        var payload = new byte[stream.Length];
        var total = 0;
        int read;
        while (total < payload.Length &&
               (read = stream.Read(payload, total, (int)Math.Min(payload.Length - total, 64 * 1024))) > 0)
        {
            total += read;
            if (total > cap)
            {
                return null;
            }
        }
        if (total < payload.Length)
        {
            Array.Resize(ref payload, total);
        }
        return payload;
    }

    /// <summary>
    /// A08: copies the unreadable file aside and verifies the copy byte for
    /// byte (hash) before claiming the content is preserved anywhere.
    /// </summary>
    private bool TryQuarantine()
    {
        try
        {
            if (!File.Exists(_storagePath))
            {
                return false;
            }
            string originalHash;
            using (var original = File.OpenRead(_storagePath))
            {
                originalHash = Convert.ToHexString(SHA256.HashData(original));
            }

            // One verified copy is enough for one unchanged payload. Without
            // this check every application start created another identical
            // .corrupt file and eventually filled the data directory.
            var directory = Path.GetDirectoryName(_storagePath) ?? ".";
            var prefix = Path.GetFileName(_storagePath) + ".corrupt-";
            foreach (var existing in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                try
                {
                    using var candidate = File.OpenRead(existing);
                    if (Convert.ToHexString(SHA256.HashData(candidate)) == originalHash)
                    {
                        return true;
                    }
                }
                catch
                {
                    // An unreadable old copy is not evidence; create a new one.
                }
            }

            var corruptPath = $"{_storagePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            File.Copy(_storagePath, corruptPath, overwrite: true);
            string backupHash;
            using (var backup = File.OpenRead(corruptPath))
            {
                backupHash = Convert.ToHexString(SHA256.HashData(backup));
            }
            return originalHash == backupHash;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A08/V04: re-runs the load in a scratch instance and commits the
    /// snapshot ONLY when it reads healthy, clearing the read-only state. A
    /// failed retry changes nothing on disk, keeps the previously valid
    /// in-memory snapshot and the failure reason visible — it never blanks
    /// the content the user is looking at.
    /// </summary>
    public bool RetryLoad()
    {
        Flush();
        lock (_gate)
        {
            var fresh = new VocabularyStore(_storagePath);
            if (fresh.LoadState == VocabularyLoadState.Ok)
            {
                LoadState = VocabularyLoadState.Ok;
                QuarantinedSafely = false;
                _words = fresh.GetAll().ToList();
                return true;
            }
            // V04: keep the previous valid snapshot; only the state metadata
            // follows the fresh read so the UI shows the current reason.
            LoadState = fresh.LoadState;
            QuarantinedSafely = fresh.QuarantinedSafely;
            return false;
        }
    }

    /// <summary>
    /// Writes the snapshot atomically (temp file → flush → replace) on the background single-writer task.
    /// </summary>
    private bool WriteSnapshotToDisk(string json)
    {
        var tempPath = string.Empty;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var bakPath = _storagePath + ".bak";
            tempPath = Path.Combine(dir ?? ".", $"vocabulary.{Guid.NewGuid():N}.tmp");

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_storagePath))
            {
                File.Copy(_storagePath, bakPath, overwrite: true);
            }
            File.Move(tempPath, _storagePath, overwrite: true);
            LastPersistError = null;
            return true;
        }
        catch (Exception exception)
        {
            LastPersistError = exception.Message;
            if (!string.IsNullOrEmpty(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            return false;
        }
    }
}
