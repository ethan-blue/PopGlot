using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class DataSection : System.Windows.Controls.UserControl
{
    private HistoryStore _history = null!;
    private VocabularyStore? _vocabulary;
    private Guid? _historyUndoId;
    private Guid? _vocabularyUndoId;
    private readonly System.Windows.Threading.DispatcherTimer _undoTimer;

    /// <summary>Raised when the section needs to show a status message in the footer.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised after history or vocabulary is cleared.</summary>
    internal event Action? DataCleared;

    public DataSection()
    {
        InitializeComponent();
        _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _undoTimer.Tick += (_, _) => RefreshUndoButtons();
        Loaded += (_, _) => { RefreshUndoButtons(); _undoTimer.Start(); };
        Unloaded += (_, _) => _undoTimer.Stop();
    }

    private Func<ShellSettings>? _settingsProvider;
    private Action<ShellSettings>? _settingsApplier;

    internal static Func<string?>? CustomSavePathPicker;
    internal static Func<string?>? CustomOpenPathPicker;

    internal void Initialize(
        HistoryStore history,
        VocabularyStore? vocabulary,
        Func<ShellSettings>? settingsProvider = null,
        Action<ShellSettings>? settingsApplier = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _settingsProvider = settingsProvider;
        _settingsApplier = settingsApplier;
    }

    // ================= Public accessors for MainWindow =================

    internal ToggleButton HistoryEnabled => HistoryEnabledToggle;

    private void RefreshUndoButtons()
    {
        UndoHistoryButton.Visibility = _historyUndoId is { } historyId &&
            LibraryUndoJournal.IsAvailable(historyId, DateTime.UtcNow) ? Visibility.Visible : Visibility.Collapsed;
        UndoVocabularyButton.Visibility = _vocabularyUndoId is { } vocabularyId &&
            LibraryUndoJournal.IsAvailable(vocabularyId, DateTime.UtcNow) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Event handlers =================

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => ClearHistory();

    private void ClearVocabulary_Click(object sender, RoutedEventArgs e) => ClearVocabulary();

    private void ClearHistory()
    {
        if (!_history.TryClear(out var removed))
        {
            UndoHistoryButton.Visibility = Visibility.Collapsed;
            StatusChanged?.Invoke("清空历史失败。请确认文件可写后重试。", StatusTone.Error);
            return;
        }

        if (removed.Count > 0)
        {
            _historyUndoId = LibraryUndoJournal.OfferHistory(removed, DateTime.UtcNow).Id;
            UndoHistoryButton.Visibility = Visibility.Visible;
        }

        StatusChanged?.Invoke("历史记录已清空。", StatusTone.Info);
        DataCleared?.Invoke();
    }

    private void ClearVocabulary()
    {
        if (_vocabulary is null)
        {
            return;
        }

        if (!_vocabulary.TryClear(out var removed))
        {
            UndoVocabularyButton.Visibility = Visibility.Collapsed;
            StatusChanged?.Invoke("清空生词本失败。请确认文件可写后重试。", StatusTone.Error);
            return;
        }

        if (removed.Count > 0)
        {
            _vocabularyUndoId = LibraryUndoJournal.OfferVocabulary(removed, DateTime.UtcNow).Id;
            UndoVocabularyButton.Visibility = Visibility.Visible;
        }

        StatusChanged?.Invoke("生词本已清空。", StatusTone.Info);
        DataCleared?.Invoke();
    }

    private void UndoHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyUndoId is not { } id || LibraryUndoJournal.Take(id, DateTime.UtcNow) is not { } ticket)
        {
            UndoHistoryButton.Visibility = Visibility.Collapsed;
            StatusChanged?.Invoke("撤销已失效。", StatusTone.Warning);
            return;
        }

        UndoHistoryButton.Visibility = Visibility.Collapsed;
        if (!_history.InsertMissing(ticket.HistoryItems, out var inserted, out var remaining))
        {
            _historyUndoId = LibraryUndoJournal.OfferHistory(remaining.Count > 0 ? remaining : ticket.HistoryItems, DateTime.UtcNow).Id;
            UndoHistoryButton.Visibility = Visibility.Visible;
            StatusChanged?.Invoke("撤销没有写回。请确认历史文件可写后重试。", StatusTone.Error);
            return;
        }

        if (remaining.Count > 0)
        {
            _historyUndoId = LibraryUndoJournal.OfferHistory(remaining, DateTime.UtcNow).Id;
            UndoHistoryButton.Visibility = Visibility.Visible;
            StatusChanged?.Invoke($"已恢复 {inserted} 条，{remaining.Count} 条因容量限制待恢复。清理历史后可再次撤销。", StatusTone.Warning);
            DataCleared?.Invoke();
            return;
        }

        if (inserted == 0)
        {
            StatusChanged?.Invoke("这些记录已存在，无需恢复。", StatusTone.Info);
            return;
        }

        StatusChanged?.Invoke("已恢复刚才清空的历史。", StatusTone.Success);
        DataCleared?.Invoke();
    }

    private void UndoVocabulary_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null ||
            _vocabularyUndoId is not { } id ||
            LibraryUndoJournal.Take(id, DateTime.UtcNow) is not { } ticket)
        {
            UndoVocabularyButton.Visibility = Visibility.Collapsed;
            StatusChanged?.Invoke("撤销已失效。", StatusTone.Warning);
            return;
        }

        UndoVocabularyButton.Visibility = Visibility.Collapsed;
        if (!_vocabulary.InsertMissing(ticket.VocabularyItems, out var inserted) || inserted == 0)
        {
            _vocabularyUndoId = LibraryUndoJournal.OfferVocabulary(ticket.VocabularyItems, DateTime.UtcNow).Id;
            UndoVocabularyButton.Visibility = Visibility.Visible;
            StatusChanged?.Invoke("撤销没有写回。请确认生词本文件可写后重试。", StatusTone.Error);
            return;
        }

        StatusChanged?.Invoke("已恢复刚才清空的生词。", StatusTone.Success);
        DataCleared?.Invoke();
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = SafeDiagnosticsExporter.CreatePackage();
            var logsDir = StoragePaths.Logs;
            Directory.CreateDirectory(logsDir);
            var exportFile = Path.Combine(logsDir, $"diagnostics-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            if (SafeDiagnosticsExporter.TryExportToFile(package, exportFile, out var message))
            {
                StatusChanged?.Invoke(message, StatusTone.Info);
            }
            else
            {
                StatusChanged?.Invoke(message, StatusTone.Error);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"导出诊断遇到异常：{ex.Message}", StatusTone.Error);
        }
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? targetPath = null;
            if (CustomSavePathPicker is not null)
            {
                targetPath = CustomSavePathPicker();
            }
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出 PopGlot 数据备份包",
                    Filter = "PopGlot 备份包 (*.popglot-backup.json)|*.popglot-backup.json|JSON 文件 (*.json)|*.json",
                    FileName = $"popglot-backup-{DateTime.Now:yyyyMMdd-HHmm}.popglot-backup.json",
                };
                if (dlg.ShowDialog() == true)
                {
                    targetPath = dlg.FileName;
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var package = UserDataBackupService.CreateBackup(
                _history,
                _vocabulary,
                _settingsProvider?.Invoke(),
                ProfileManager.Load());

            var json = UserDataBackupService.SerializeBackup(package);
            File.WriteAllText(targetPath, json, new System.Text.UTF8Encoding(false));

            StatusChanged?.Invoke(
                $"已成功导出备份包（含 {package.History?.Count ?? 0} 条历史，{package.Vocabulary?.Count ?? 0} 个生词）：{System.IO.Path.GetFileName(targetPath)}",
                StatusTone.Info);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"导出备份失败：{ex.Message}", StatusTone.Error);
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? sourcePath = null;
            if (CustomOpenPathPicker is not null)
            {
                sourcePath = CustomOpenPathPicker();
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择 PopGlot 数据备份包以恢复",
                    Filter = "PopGlot 备份包 (*.popglot-backup.json;*.json)|*.popglot-backup.json;*.json|所有文件 (*.*)|*.*",
                };
                if (dlg.ShowDialog() == true)
                {
                    sourcePath = dlg.FileName;
                }
            }

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            var json = File.ReadAllText(sourcePath);
            var validation = UserDataBackupService.ValidateBackup(json);
            if (validation.Status != BackupValidationStatus.Valid || validation.Package is null)
            {
                StatusChanged?.Invoke($"恢复终止：{validation.Message}", StatusTone.Error);
                return;
            }

            var package = validation.Package;
            var diff = UserDataBackupService.AnalyzeDifferences(package, _history, _vocabulary);

            UserDataBackupService.RestoreBackup(
                package,
                _history,
                _vocabulary ?? new VocabularyStore(),
                _settingsApplier);

            StatusChanged?.Invoke($"数据恢复成功！（{diff.SummaryText}）", StatusTone.Info);
            DataCleared?.Invoke();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(ex.Message, StatusTone.Error);
        }
    }
}
