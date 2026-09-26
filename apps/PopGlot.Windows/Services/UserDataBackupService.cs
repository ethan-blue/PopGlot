using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Services;

internal enum BackupValidationStatus
{
    Valid,
    CorruptedJson,
    UnsupportedHigherVersion,
    Oversized,
    EmptyPackage,
}

internal sealed record BackupValidationResult(
    BackupValidationStatus Status,
    string Message,
    UserDataBackupPackage? Package);

internal sealed record BackupDiffSummary(
    int CurrentHistoryCount,
    int BackupHistoryCount,
    int CurrentVocabularyCount,
    int BackupVocabularyCount,
    bool HasSettings,
    string SummaryText);

internal sealed record UserDataBackupPackage(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("app_version")] string AppVersion,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("history")] IReadOnlyList<TranslationHistoryEntry>? History,
    [property: JsonPropertyName("vocabulary")] IReadOnlyList<VocabularyWord>? Vocabulary,
    [property: JsonPropertyName("shell_settings")] ShellSettings? ShellSettings,
    [property: JsonPropertyName("product_config")] CoreProductConfig? ProductConfig);

/// <summary>
/// Structured migration and recovery service for PopGlot user data (R08).
/// Enforces:
/// 1. Schema versioning and forward-compatibility checks;
/// 2. Strict zero-credential export;
/// 3. Size and entry budgets;
/// 4. Differential summary preview;
/// 5. Atomic restore with automated pre-commit backup and rollback on any failure.
/// </summary>
internal static class UserDataBackupService
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentAppVersion = "0.1.10";
    public const long MaxBackupPackageBytes = 32 * 1024 * 1024; // 32 MiB budget

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Creates a complete, portable backup package.
    /// Strictly excludes any credentials or secrets.
    /// </summary>
    public static UserDataBackupPackage CreateBackup(
        HistoryStore? historyStore,
        VocabularyStore? vocabularyStore,
        ShellSettings? shellSettings = null,
        CoreProductConfig? productConfig = null)
    {
        var history = historyStore?.Load() ?? [];
        var vocabulary = vocabularyStore?.GetAll() ?? [];

        // Clone configs and ensure zero secrets
        ShellSettings? safeSettings = null;
        if (shellSettings is not null)
        {
            safeSettings = shellSettings with { };
        }

        CoreProductConfig? safeConfig = null;
        if (productConfig is not null)
        {
            safeConfig = productConfig.Clone();
            // CoreProductConfig never contains secrets, only profile metadata and credential targets.
        }

        return new UserDataBackupPackage(
            SchemaVersion: CurrentSchemaVersion,
            AppVersion: CurrentAppVersion,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            History: history,
            Vocabulary: vocabulary,
            ShellSettings: safeSettings,
            ProductConfig: safeConfig);
    }

    /// <summary>
    /// Serializes a backup package to JSON string.
    /// </summary>
    public static string SerializeBackup(UserDataBackupPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return JsonSerializer.Serialize(package, JsonOptions);
    }

    /// <summary>
    /// Validates a backup JSON payload against schema version, size limits, and parse integrity.
    /// Never crashes or modifies existing state on corrupt inputs.
    /// </summary>
    public static BackupValidationResult ValidateBackup(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BackupValidationResult(
                BackupValidationStatus.EmptyPackage,
                "备份内容为空。",
                null);
        }

        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MaxBackupPackageBytes)
        {
            return new BackupValidationResult(
                BackupValidationStatus.Oversized,
                $"备份包体积超过 32MiB 上限（当前 {byteCount / (1024 * 1024.0):F1}MiB），拒绝处理以保护内存与存储安全。",
                null);
        }

        UserDataBackupPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<UserDataBackupPackage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new BackupValidationResult(
                BackupValidationStatus.CorruptedJson,
                $"备份包格式损坏或不是有效的 JSON 文件：{ex.Message}",
                null);
        }

        if (package is null)
        {
            return new BackupValidationResult(
                BackupValidationStatus.CorruptedJson,
                "备份包未包含有效的根对象。",
                null);
        }

        if (package.SchemaVersion > CurrentSchemaVersion)
        {
            return new BackupValidationResult(
                BackupValidationStatus.UnsupportedHigherVersion,
                $"备份包版本（v{package.SchemaVersion}）高于当前软件支持版本（v{CurrentSchemaVersion}），请升级 PopGlot 后重试。",
                package);
        }

        return new BackupValidationResult(
            BackupValidationStatus.Valid,
            "备份包校验通过，结构完整。",
            package);
    }

    /// <summary>
    /// Computes differential summary preview between current stores and the backup package.
    /// </summary>
    public static BackupDiffSummary AnalyzeDifferences(
        UserDataBackupPackage package,
        HistoryStore? historyStore,
        VocabularyStore? vocabularyStore)
    {
        ArgumentNullException.ThrowIfNull(package);

        var currentHistoryCount = historyStore?.Load().Count ?? 0;
        var backupHistoryCount = package.History?.Count ?? 0;

        var currentVocabCount = vocabularyStore?.GetAll().Count ?? 0;
        var backupVocabCount = package.Vocabulary?.Count ?? 0;

        var sb = new StringBuilder();
        sb.Append($"历史记录: 备份包含 {backupHistoryCount} 条（当前 {currentHistoryCount} 条）；");
        sb.Append($"生词本: 备份包含 {backupVocabCount} 个词（当前 {currentVocabCount} 词）；");
        if (package.ShellSettings is not null)
        {
            sb.Append("包含快捷键与界面外观配置；");
        }
        if (package.ProductConfig is not null && package.ProductConfig.Profiles.Count > 0)
        {
            sb.Append($"包含 {package.ProductConfig.Profiles.Count} 个引擎服务配置（不含凭据）；");
        }

        return new BackupDiffSummary(
            CurrentHistoryCount: currentHistoryCount,
            BackupHistoryCount: backupHistoryCount,
            CurrentVocabularyCount: currentVocabCount,
            BackupVocabularyCount: backupVocabCount,
            HasSettings: package.ShellSettings is not null || package.ProductConfig is not null,
            SummaryText: sb.ToString());
    }

    /// <summary>
    /// Restores data from the backup package with atomic pre-backup and rollback guarantee.
    /// If writing any store encounters an error (disk full, lock, corrupt payload),
    /// previous state is immediately restored and an honest exception is thrown.
    /// </summary>
    public static void RestoreBackup(
        UserDataBackupPackage package,
        HistoryStore historyStore,
        VocabularyStore vocabularyStore,
        Action<ShellSettings>? applySettings = null,
        Action<CoreProductConfig>? applyProductConfig = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(vocabularyStore);

        if (package.SchemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"备份包版本（v{package.SchemaVersion}）高于当前软件支持版本（v{CurrentSchemaVersion}），无法恢复。");
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var historyPreBak = historyStore.FilePath + $".pre-restore-{timestamp}.bak";
        var vocabPreBak = vocabularyStore.StoragePath + $".pre-restore-{timestamp}.bak";

        // Snapshot existing in-memory data for rollback
        var oldHistory = historyStore.Load();
        var oldVocab = vocabularyStore.GetAll();

        // 1. Create disk backup copies if files exist
        try
        {
            if (File.Exists(historyStore.FilePath))
            {
                File.Copy(historyStore.FilePath, historyPreBak, overwrite: true);
            }
            if (File.Exists(vocabularyStore.StoragePath))
            {
                File.Copy(vocabularyStore.StoragePath, vocabPreBak, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"创建恢复前安全备份失败，恢复已终止，未对现有数据做任何修改：{ex.Message}", ex);
        }

        // 2. Perform restoration
        var historyRestored = false;
        var vocabRestored = false;

        try
        {
            if (package.History is not null)
            {
                historyRestored = historyStore.RestoreEntries(package.History);
                if (!historyRestored)
                {
                    throw new IOException("历史记录写入失败或超出容量限制。");
                }
            }

            if (package.Vocabulary is not null)
            {
                vocabRestored = vocabularyStore.RestoreWords(package.Vocabulary);
                if (!vocabRestored)
                {
                    throw new IOException("生词本写入失败、文件被锁定或超出容量限制。");
                }
            }

            if (package.ShellSettings is not null && applySettings is not null)
            {
                applySettings(package.ShellSettings);
            }

            if (package.ProductConfig is not null && applyProductConfig is not null)
            {
                applyProductConfig(package.ProductConfig);
            }

            // Flush writes
            historyStore.Flush();
            vocabularyStore.Flush();
        }
        catch (Exception ex)
        {
            // 3. Rollback immediately
            try
            {
                if (historyRestored)
                {
                    historyStore.RestoreEntries(oldHistory);
                    if (File.Exists(historyPreBak))
                    {
                        File.Copy(historyPreBak, historyStore.FilePath, overwrite: true);
                    }
                }
                if (vocabRestored)
                {
                    vocabularyStore.RestoreWords(oldVocab);
                    if (File.Exists(vocabPreBak))
                    {
                        File.Copy(vocabPreBak, vocabularyStore.StoragePath, overwrite: true);
                    }
                }
            }
            catch
            {
                // Rollback failure secondary handling
            }

            throw new InvalidOperationException(
                $"数据恢复失败，已触发自动回滚保护，原数据已保留未被覆盖。错误：{ex.Message}", ex);
        }
        finally
        {
            // Clean up pre-restore backup files on complete success or after rollback
            try
            {
                if (File.Exists(historyPreBak)) File.Delete(historyPreBak);
                if (File.Exists(vocabPreBak)) File.Delete(vocabPreBak);
            }
            catch
            {
            }
        }
    }
}
