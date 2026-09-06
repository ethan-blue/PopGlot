using System.IO;
using System.Text;
using System.Text.Json;

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

    private static readonly string DefaultStoragePath = StoragePaths.Vocabulary;

    private readonly string _storagePath;
    private readonly Lock _gate = new();
    private List<VocabularyWord> _words = [];

    public VocabularyStore(string? customPath = null)
    {
        _storagePath = customPath ?? DefaultStoragePath;
        Load();
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
        if (string.IsNullOrWhiteSpace(word))
        {
            return new VocabularySaveResult(false, false, VocabularySaveStatus.WriteFailed);
        }
        var trimmed = word.Trim();

        bool willBeStarred;
        List<VocabularyWord> next;
        // The whole read → persist → commit runs under the gate: writing the
        // file outside the lock made two concurrent toggles build from the
        // same base and silently drop each other's word.
        lock (_gate)
        {
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

            if (!TryPersist(next, out var status))
            {
                // Disk refused: the in-memory snapshot keeps the previous
                // state, so the UI cannot show a star that was never saved.
                return new VocabularySaveResult(false, !willBeStarred, status);
            }

            _words = next;
        }
        return VocabularySaveResult.Saved(willBeStarred);
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (_words.All(w => w.Id != id))
            {
                return true; // Nothing to remove; the requested state already holds.
            }
            var next = _words.Where(w => w.Id != id).ToList();
            if (!TryPersist(next, out _))
            {
                return false;
            }
            _words = next;
            return true;
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            if (!TryPersist([], out _))
            {
                return false;
            }
            _words = [];
            return true;
        }
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
            var json = File.ReadAllText(_storagePath, Encoding.UTF8);
            var items = JsonSerializer.Deserialize<List<VocabularyWord>>(json);
            if (items is not null)
            {
                // A JSON array may contain null entries; they are not data.
                _words = items.Where(item => item is not null).ToList();
            }
        }
        catch (Exception)
        {
            // Corrupt file preservation: quarantine bad file so user data is not lost.
            try
            {
                if (File.Exists(_storagePath))
                {
                    var corruptPath = $"{_storagePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                    File.Copy(_storagePath, corruptPath, overwrite: true);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Writes the snapshot atomically (temp file → flush → replace) and returns
    /// whether the disk actually accepted it. Called with the caller's lock
    /// held; never mutates <see cref="_words"/> itself.
    /// </summary>
    private bool TryPersist(List<VocabularyWord> snapshot, out VocabularySaveStatus status)
    {
        status = VocabularySaveStatus.Persisted;
        var tempPath = string.Empty;
        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaxFileBytes)
            {
                status = VocabularySaveStatus.FileTooLarge;
                return false;
            }

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
            return true;
        }
        catch
        {
            if (!string.IsNullOrEmpty(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            status = VocabularySaveStatus.WriteFailed;
            return false;
        }
    }
}
