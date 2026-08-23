using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopGlot.Windows;

internal sealed record TranslationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string SourceKind,
    string Source,
    string Translation,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms);

internal enum HistoryAddResult
{
    Stored,
    Disabled,
    SkippedSensitiveOrLarge,
    Failed,
}

internal sealed partial class HistoryStore
{
    private const int MaxEntries = 100;
    private const int MaxSourceCharacters = 4_000;
    private const int MaxTranslationCharacters = 8_000;
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    private readonly string _path;

    public HistoryStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot",
            "history.json");
    }

    public IReadOnlyList<TranslationHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes)
            {
                return [];
            }
            var entries = JsonSerializer.Deserialize<List<TranslationHistoryEntry>>(
                File.ReadAllText(_path)) ?? [];
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            return entries
                .Where(entry => entry.CreatedAt >= cutoff)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(MaxEntries)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public HistoryAddResult TryAdd(TranslationHistoryEntry entry, bool enabled)
    {
        if (!enabled)
        {
            return HistoryAddResult.Disabled;
        }
        if (!CanPersist(entry))
        {
            return HistoryAddResult.SkippedSensitiveOrLarge;
        }
        try
        {
            var entries = Load().Prepend(entry).Take(MaxEntries).ToArray();
            Save(entries);
            return HistoryAddResult.Stored;
        }
        catch (IOException)
        {
            return HistoryAddResult.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return HistoryAddResult.Failed;
        }
        catch (InvalidOperationException)
        {
            return HistoryAddResult.Failed;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    internal static bool CanPersist(TranslationHistoryEntry entry)
    {
        if (entry.Source.Length > MaxSourceCharacters ||
            entry.Translation.Length > MaxTranslationCharacters)
        {
            return false;
        }
        var combined = $"{entry.Source}\n{entry.Translation}";
        return !SensitiveContentRegex().IsMatch(combined);
    }

    private void Save(IReadOnlyList<TranslationHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Unable to resolve the PopGlot history directory.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new InvalidOperationException("本地历史超过 2 MiB 上限。");
        }
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    [GeneratedRegex(
        @"(?i)(-----BEGIN [A-Z ]*PRIVATE KEY-----|\bpassword\s*[:=]|\bapi[_-]?key\s*[:=]|\bsk-[a-z0-9_-]{16,}|\bAIza[a-z0-9_-]{16,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveContentRegex();
}
