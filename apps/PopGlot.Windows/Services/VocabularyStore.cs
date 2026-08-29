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

/// <summary>
/// Local persistent wordbook / vocabulary store for starred words and translations.
/// Supports Anki TSV export, CSV, and Markdown.
/// </summary>
internal sealed class VocabularyStore : IVocabularyRepository
{
    private static readonly string StoragePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "vocabulary.json");

    private readonly Lock _gate = new();
    private readonly List<VocabularyWord> _words = [];

    public VocabularyStore()
    {
        Load();
    }

    public IReadOnlyList<VocabularyWord> GetAll()
    {
        lock (_gate)
        {
            return _words.OrderByDescending(w => w.CreatedAt).ToList();
        }
    }

    public bool IsStarred(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        lock (_gate)
        {
            return _words.Any(w => string.Equals(w.Word.Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool ToggleStar(
        string word,
        string translation,
        string phonetic = "",
        string explanation = "",
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        List<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var trimmed = word.Trim();

        lock (_gate)
        {
            var existing = _words.FirstOrDefault(w => string.Equals(w.Word, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _words.Remove(existing);
                Save();
                return false; // Unstarred
            }

            var entry = new VocabularyWord(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                trimmed,
                translation?.Trim() ?? string.Empty,
                phonetic?.Trim() ?? string.Empty,
                explanation?.Trim() ?? string.Empty,
                sourceLang,
                targetLang,
                tags ?? []);

            _words.Insert(0, entry);
            Save();
            return true; // Starred
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var removed = _words.RemoveAll(w => w.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _words.Clear();
            Save();
        }
    }

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

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string EscapeAnki(string text) =>
        text.Replace("\t", " ").Replace("\n", "<br>").Replace("\"", "&quot;");

    private void Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return;
            var json = File.ReadAllText(StoragePath, Encoding.UTF8);
            var items = JsonSerializer.Deserialize<List<VocabularyWord>>(json);
            if (items is not null)
            {
                lock (_gate)
                {
                    _words.Clear();
                    _words.AddRange(items);
                }
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_words, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StoragePath, json, Encoding.UTF8);
        }
        catch { }
    }
}
