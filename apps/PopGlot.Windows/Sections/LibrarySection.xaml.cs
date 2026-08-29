using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

/// <summary>One row of the unified library list (history or vocabulary).</summary>
internal sealed record LibraryRow(
    Guid Id,
    string Title,
    string Detail,
    string Timestamp,
    string Kind,
    string LanguagePair,
    string Source,
    string Translation,
    string Explanation,
    TranslationHistoryEntry? History,
    VocabularyWord? Word);

public partial class LibrarySection : System.Windows.Controls.UserControl
{
    private enum LibraryMode
    {
        History,
        Vocabulary,
    }

    private HistoryStore _history = null!;
    private VocabularyStore? _vocabulary;
    private ConfirmButton? _clearCurrentConfirm;
    private LibraryMode _mode = LibraryMode.History;
    private IReadOnlyList<TranslationHistoryEntry> _allHistory = [];
    private IReadOnlyList<VocabularyWord> _allVocabulary = [];

    /// <summary>Raised when the user wants to load an entry into the workbench.</summary>
    internal event Action<string, string, string?, string?, string?, string?>? LoadToTranslate;

    /// <summary>Raised when the section needs to show a status message in the footer.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    public LibrarySection()
    {
        InitializeComponent();
        _clearCurrentConfirm = ConfirmButton.Attach(ClearCurrentButton, "确认清空？", ClearCurrent);
    }

    /// <summary>Compact mode narrows the list column; layout stays master–detail.</summary>
    internal void SetCompact(bool compact)
    {
        ListColumn.Width = new GridLength(compact ? 280 : 340);
    }

    internal void Initialize(HistoryStore history, VocabularyStore? vocabulary)
    {
        _history = history;
        _vocabulary = vocabulary;
    }

    // ================= Loading =================

    internal void ReloadHistory()
    {
        try
        {
            _allHistory = _history.Load();
            if (_mode == LibraryMode.History)
            {
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"加载历史记录失败：{exception.Message}", StatusTone.Error);
        }
    }

    internal void ReloadVocabulary()
    {
        if (_vocabulary is null) return;
        try
        {
            _allVocabulary = _vocabulary.GetAll();
            if (_mode == LibraryMode.Vocabulary)
            {
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"加载生词本失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ================= Unified list =================

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _mode = ModeVocabulary.IsChecked == true ? LibraryMode.Vocabulary : LibraryMode.History;
        if (_mode == LibraryMode.Vocabulary)
        {
            ReloadVocabulary();
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = LibrarySearchBox?.Text?.Trim() ?? string.Empty;
        var rows = _mode == LibraryMode.History
            ? BuildHistoryRows(query)
            : BuildVocabularyRows(query);

        LibraryListBox.ItemsSource = rows;
        LibraryListBox.SelectedIndex = -1;
        LibraryEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryCountText.Text = string.IsNullOrEmpty(query)
            ? (_mode == LibraryMode.History
                ? $"{_allHistory.Count} 条记录"
                : $"{_allVocabulary.Count} 个生词")
            : $"匹配 {rows.Count} / {(_mode == LibraryMode.History ? _allHistory.Count : _allVocabulary.Count)}";

        if (_mode == LibraryMode.History)
        {
            LibraryEmptyTitle.Text = "暂无历史记录";
            LibraryEmptyHint.Text = "使用划词/截图快捷键，或在翻译工作台输入，记录会自动保存在本机。";
        }
        else
        {
            LibraryEmptyTitle.Text = "生词本还是空的";
            LibraryEmptyHint.Text = "在翻译浮窗或查词栏点击「收藏」，即可把单词/句子收进生词本。";
        }

        ShowDetail(null);
    }

    private List<LibraryRow> BuildHistoryRows(string query)
    {
        var filtered = string.IsNullOrEmpty(query)
            ? _allHistory
            : _allHistory.Where(entry =>
                entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase));
        return filtered.Select(entry => new LibraryRow(
            entry.Id,
            entry.Source,
            entry.Translation,
            entry.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            entry.SourceKind,
            $"{LanguageCatalog.DisplayName(entry.SourceLanguage)} → {LanguageCatalog.DisplayName(entry.TargetLanguage)}",
            entry.Source,
            entry.Translation,
            entry.Explanation,
            entry,
            null)).ToList();
    }

    private List<LibraryRow> BuildVocabularyRows(string query)
    {
        var filtered = string.IsNullOrEmpty(query)
            ? _allVocabulary
            : _allVocabulary.Where(entry =>
                entry.Word.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase));
        return filtered.Select(word => new LibraryRow(
            word.Id,
            word.Word,
            word.Translation,
            word.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
            string.IsNullOrWhiteSpace(word.Phonetic) ? "生词" : $"[{word.Phonetic}]",
            $"{LanguageCatalog.DisplayName(word.SourceLanguage)} → {LanguageCatalog.DisplayName(word.TargetLanguage)}",
            word.Word,
            word.Translation,
            word.Explanation,
            null,
            word)).ToList();
    }

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ================= Detail =================

    private LibraryRow? SelectedRow() => LibraryListBox.SelectedItem as LibraryRow;

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(SelectedRow());

    private void ShowDetail(LibraryRow? row)
    {
        if (row is null)
        {
            DetailEmpty.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            DetailContent.Visibility = Visibility.Collapsed;
            return;
        }
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        DetailContent.Visibility = Visibility.Visible;

        DetailKindText.Text = _mode == LibraryMode.History ? (row.Kind == string.Empty ? "历史" : row.Kind) : "生词";
        DetailMetaText.Text = $"{row.LanguagePair} · {row.Timestamp}" +
            (_mode == LibraryMode.Vocabulary && row.Kind != "生词" ? $" · {row.Kind}" : string.Empty);
        DetailSourceText.Text = row.Source;
        DetailTranslationText.Text = row.Translation;
        DetailExplanationText.Text = row.Explanation;
        DetailExplanationText.Visibility = string.IsNullOrWhiteSpace(row.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void LibraryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LoadSelected();
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            DeleteSelected();
        }
    }

    private void LibraryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => LoadSelected();

    // ================= Detail actions =================

    private void LoadSelected_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        var row = SelectedRow();
        if (row is null)
        {
            return;
        }
        LoadToTranslate?.Invoke(
            row.Source, row.Translation, row.Explanation,
            row.History?.SourceLanguage ?? row.Word?.SourceLanguage,
            row.History?.TargetLanguage ?? row.Word?.TargetLanguage,
            _mode == LibraryMode.History ? "历史记录" : "生词本");
    }

    private async void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row is null)
        {
            StatusChanged?.Invoke("请先选择一个条目。", StatusTone.Info);
            return;
        }
        if (await Helpers.CopyToClipboardAsync(row.Translation))
        {
            StatusChanged?.Invoke("已复制译文。", StatusTone.Info);
        }
    }

    private void SpeakSelected_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow();
        if (row is null)
        {
            StatusChanged?.Invoke("请先选择一个条目。", StatusTone.Info);
            return;
        }
        TtsService.Speak(_mode == LibraryMode.Vocabulary ? row.Source : row.Translation);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var row = SelectedRow();
        if (row is null)
        {
            StatusChanged?.Invoke("请先选择一个条目。", StatusTone.Info);
            return;
        }
        if (_mode == LibraryMode.History)
        {
            _history.Remove(row.Id);
            ReloadHistory();
            StatusChanged?.Invoke("已删除该条记录。", StatusTone.Info);
        }
        else if (_vocabulary is not null)
        {
            _vocabulary.Remove(row.Id);
            ReloadVocabulary();
            StatusChanged?.Invoke("已从生词本移除该词条。", StatusTone.Info);
        }
    }

    // ================= Export & clear =================

    private void ExportMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        if (_mode == LibraryMode.History)
        {
            menu.Items.Add(MakeMenuItem("导出为 CSV", (_, _) => ExportHistory("csv")));
            menu.Items.Add(MakeMenuItem("导出为 Markdown", (_, _) => ExportHistory("md")));
        }
        else
        {
            menu.Items.Add(MakeMenuItem("导出 Anki 牌组 (.tsv)", (_, _) => ExportVocabulary("anki")));
            menu.Items.Add(MakeMenuItem("导出为 CSV", (_, _) => ExportVocabulary("csv")));
            menu.Items.Add(MakeMenuItem("导出为 Markdown", (_, _) => ExportVocabulary("md")));
        }
        menu.PlacementTarget = ExportMenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MakeMenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private async void ExportHistory(string format)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = System.IO.Path.Combine(
                desktop, $"PopGlot_History_{DateTime.Now:yyyyMMdd_HHmm}.{format}");
            var content = format == "csv" ? _history.ExportToCsv() : _history.ExportToMarkdown();
            await System.IO.File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
            StatusChanged?.Invoke($"已导出历史记录到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"导出历史记录失败：{exception.Message}", StatusTone.Error);
        }
    }

    private async void ExportVocabulary(string format)
    {
        if (_vocabulary is null) return;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var (name, content) = format switch
            {
                "anki" => ($"PopGlot_Anki_Export_{DateTime.Now:yyyyMMdd_HHmm}.tsv", _vocabulary.ExportToAnkiTsv()),
                "md" => ($"PopGlot_Vocabulary_{DateTime.Now:yyyyMMdd_HHmm}.md", _vocabulary.ExportToMarkdown()),
                _ => ($"PopGlot_Vocabulary_{DateTime.Now:yyyyMMdd_HHmm}.csv", _vocabulary.ExportToCsv()),
            };
            var path = System.IO.Path.Combine(desktop, name);
            await System.IO.File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
            StatusChanged?.Invoke($"已导出生词本到桌面：{System.IO.Path.GetFileName(path)}", StatusTone.Success);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"导出生词本失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void ClearCurrent_Click(object sender, RoutedEventArgs e)
    {
        // ConfirmButton has already asked inline (two-step click).
        ClearCurrent();
    }

    private void ClearCurrent()
    {
        if (_mode == LibraryMode.History)
        {
            var cleared = _history.Clear();
            StatusChanged?.Invoke(
                cleared ? "历史记录已清空。" : "清空历史失败：文件正被占用。",
                cleared ? StatusTone.Info : StatusTone.Error);
            ReloadHistory();
        }
        else if (_vocabulary is not null)
        {
            _vocabulary.Clear();
            StatusChanged?.Invoke("生词本已清空。", StatusTone.Info);
            ReloadVocabulary();
        }
    }
}
