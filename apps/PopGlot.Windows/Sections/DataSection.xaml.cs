using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class DataSection : System.Windows.Controls.UserControl
{
    private HistoryStore _history = null!;
    private VocabularyStore? _vocabulary;
    private ConfirmButton? _clearHistoryConfirm;
    private ConfirmButton? _clearVocabularyConfirm;

    /// <summary>Raised when the section needs to show a status message in the footer.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised after history or vocabulary is cleared.</summary>
    internal event Action? DataCleared;

    public DataSection()
    {
        InitializeComponent();
        _clearHistoryConfirm = ConfirmButton.Attach(
            ClearHistoryButton,
            () => $"将清空 {HistoryCount()} 条历史记录",
            ClearHistory);
        _clearVocabularyConfirm = ConfirmButton.Attach(
            ClearVocabularyButton,
            () => $"将清空 {VocabularyCount()} 个生词",
            ClearVocabulary);
    }

    private int HistoryCount()
    {
        try
        {
            return _history?.Load().Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private int VocabularyCount()
    {
        try
        {
            return _vocabulary?.GetAll().Count ?? 0;
        }
        catch
        {
            return 0;
        }
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

    // ================= Event handlers =================

    // The destructive actions run only through ConfirmButton's two-step click;
    // wiring the buttons' Click here would wipe on the first click.

    private void ClearHistory()
    {
        // The ConfirmButton wrapper already asked inline (two-step click).
        var cleared = _history.Clear();
        if (cleared)
        {
            App.SharedSessionStore.Clear();
        }
        StatusChanged?.Invoke(
            cleared ? "历史记录与暂存会话已清空。" : "清空历史失败：文件正被占用。",
            cleared ? StatusTone.Info : StatusTone.Error);
        DataCleared?.Invoke();
    }

    private void ClearVocabulary()
    {
        if (_vocabulary is null)
        {
            return;
        }
        var cleared = _vocabulary.Clear();
        StatusChanged?.Invoke(
            cleared ? "生词本已清空。" : "清空生词本失败：文件正被占用。",
            cleared ? StatusTone.Info : StatusTone.Error);
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
