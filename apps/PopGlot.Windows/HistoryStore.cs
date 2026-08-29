using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private readonly string _path;
    private readonly Lock _gate = new();

    public HistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot",
            "history.json");
    }

    public IReadOnlyList<TranslationHistoryEntry> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private IReadOnlyList<TranslationHistoryEntry> LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes)
            {
                return [];
            }
            var entries = JsonSerializer.Deserialize<List<TranslationHistoryEntry>>(
                File.ReadAllText(_path), JsonOptions) ?? [];
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            return entries
                .Where(entry => entry.CreatedAt >= cutoff)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
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
                var existing = LoadUnlocked()
                    .Where(item => !(item.Source == entry.Source
                        && item.TargetLanguage == entry.TargetLanguage))
                    .Take(MaxEntries - 1);
                Save([entry, .. existing]);
                return HistoryAddResult.Stored;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
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
                var remaining = LoadUnlocked().Where(entry => entry.Id != id).ToArray();
                Save(remaining);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public bool Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
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
            foreach (var e in LoadUnlocked())
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
            foreach (var e in LoadUnlocked())
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
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return $"\"{value.Replace("\"", "\"\"")}\"";
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

    private void Save(IReadOnlyList<TranslationHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Unable to resolve the PopGlot history directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new InvalidOperationException("本地历史超过大小上限。");
        }
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    [GeneratedRegex(
        @"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\bpassword\s*[:=]|\bapi[_-]?key\s*[:=]|\bsecret\s*[:=]|\bsk-[a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{16,}|\bghp_[a-zA-Z0-9]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveContentRegex();
}
