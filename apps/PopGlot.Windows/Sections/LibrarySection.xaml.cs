using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

/// <summary>One row of the library list (history record or vocabulary word).</summary>
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
    VocabularyWord? Word)
{
    /// <summary>
    /// List summaries must stay one line. History sources often carry
    /// newlines (OCR output, pasted paragraphs) which a plain TextBlock
    /// happily renders as multiple lines — flatten and trim them here.
    /// </summary>
    public string TitleOneLine => OneLine(Title);

    public string DetailOneLine => OneLine(MarkdownPresenter.ToPlainText(Detail));

    private static string OneLine(string text)
    {
        var flattened = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        while (flattened.Contains("  ", StringComparison.Ordinal))
        {
            flattened = flattened.Replace("  ", " ", StringComparison.Ordinal);
        }
        return flattened.Trim();
    }
}

public partial class LibrarySection : System.Windows.Controls.UserControl
{
    private enum LibraryMode
    {
        History,
        Vocabulary,
    }

    private HistoryStore _history = null!;
    private VocabularyStore? _vocabulary;
    private LibraryMode _mode = LibraryMode.History;
    private IReadOnlyList<TranslationHistoryEntry> _allHistory = [];
    private IReadOnlyList<VocabularyWord> _allVocabulary = [];
    private readonly ObservableCollection<LibraryRow> _allRows = [];
    private readonly ICollectionView _rowsView;

    private Guid? _undoTicketId;
    private string? _exportPath;
    private bool _narrowLibrary;
    private bool _narrowShowDetail;
    private readonly System.Windows.Threading.DispatcherTimer _undoTimer;

    /// <summary>Raised when the user wants to load an entry into the workbench.</summary>
    internal event Action<string, string, string?, string?, string?, string?>? LoadToTranslate;

    /// <summary>Raised when the section needs to show a status message in the footer.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    public LibrarySection()
    {
        InitializeComponent();
        _rowsView = CollectionViewSource.GetDefaultView(_allRows);
        _rowsView.Filter = FilterRow;
        LibraryListBox.ItemsSource = _rowsView;
        _undoTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _undoTimer.Tick += (_, _) => RefreshUndoBar();
    }

    private bool FilterRow(object item)
    {
        if (item is not LibraryRow row)
        {
            return false;
        }
        var query = LibrarySearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }
        return row.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               row.Translation.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    internal void Initialize(HistoryStore history, VocabularyStore? vocabulary)
    {
        _history = history;
        _vocabulary = vocabulary;
    }

    // ================= Loading =================

    private bool _corruptHistoryReported;
    private bool _vocabularyLoadReported;

    /// <summary>V04: the real retry entry — visible whenever the vocabulary
    /// file is in a non-Ok load state; retrying commits only a healthy read.</summary>
    internal Button RetryVocabulary => RetryVocabularyButton;

    internal void UpdateVocabularyRetryAffordance()
    {
        if (_vocabulary is null)
        {
            return;
        }
        RetryVocabularyButton.Visibility = _vocabulary.LoadState == Services.VocabularyLoadState.Ok
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RetryVocabularyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null)
        {
            return;
        }
        var retried = _vocabulary.RetryLoad();
        ReloadVocabulary();
        ApplyFilter();
        UpdateVocabularyRetryAffordance();
        StatusChanged?.Invoke(
            retried
                ? "生词本已重新加载成功。"
                : "生词本重试仍未成功；现有列表内容保持不变。",
            retried ? StatusTone.Success : StatusTone.Warning);
    }

    internal void ReloadHistory()
    {
        try
        {
            _allHistory = _history.Load();
            if (_mode == LibraryMode.History)
            {
                SyncCurrentRows();
                ApplyFilter();
            }
            // A quarantined history file is user data we refuse to destroy:
            // say so once, with the backup name, instead of silently showing
            // an empty list.
            if (!_corruptHistoryReported && _history.LastQuarantinePath is { } backup)
            {
                _corruptHistoryReported = true;
                StatusChanged?.Invoke(
                    $"历史记录文件损坏，已备份为 {System.IO.Path.GetFileName(backup)}，原文件未被删除。",
                    StatusTone.Warning);
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
                SyncCurrentRows();
                ApplyFilter();
            }
            // C02: an unreadable wordbook is protected, not silently emptied —
            // say so once, with where the file lives, instead of a bare list.
            UpdateVocabularyRetryAffordance();
            if (!_vocabularyLoadReported && _vocabulary.LoadState is not Services.VocabularyLoadState.Ok)
            {
                _vocabularyLoadReported = true;
                StatusChanged?.Invoke(_vocabulary.LoadState switch
                {
                    Services.VocabularyLoadState.TooLarge =>
                        $"生词本文件超过 32MiB 上限，未加载，已进入只读保护；原文件保留在 {StoragePaths.CoreConfigDirectory}。",
                    Services.VocabularyLoadState.Locked =>
                        $"生词本文件被其他程序占用，未加载，已进入只读保护；原文件保留在 {StoragePaths.CoreConfigDirectory}。",
                    Services.VocabularyLoadState.NoAccess =>
                        $"生词本文件没有读取权限，未加载，已进入只读保护；原文件保留在 {StoragePaths.CoreConfigDirectory}。",
                    _ =>
                        $"生词本文件已损坏并隔离备份，当前从空白开始；原文件保留在 {StoragePaths.CoreConfigDirectory}。",
                }, StatusTone.Warning);
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"加载生词本失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ================= List / filter =================

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        RefreshUndoBar();
        _mode = ModeVocabulary.IsChecked == true ? LibraryMode.Vocabulary : LibraryMode.History;
        if (_mode == LibraryMode.Vocabulary)
        {
            ReloadVocabulary();
        }
        else
        {
            ReloadHistory();
        }
        SyncCurrentRows();
        ApplyFilter();
        SelectLatestRowOrNothing();
    }

    private void SyncCurrentRows()
    {
        var targetRows = _mode == LibraryMode.History
            ? _allHistory.Select(BuildHistoryRow).ToList()
            : _allVocabulary.Select(BuildVocabularyRow).ToList();

        if (_allRows.Count == targetRows.Count &&
            _allRows.Zip(targetRows).All(p => p.First.Id == p.Second.Id && p.First.Timestamp == p.Second.Timestamp))
        {
            return;
        }

        _allRows.Clear();
        foreach (var r in targetRows)
        {
            _allRows.Add(r);
        }
    }

    private static LibraryRow BuildHistoryRow(TranslationHistoryEntry entry) => new(
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
        null);

    private static LibraryRow BuildVocabularyRow(VocabularyWord word) => new(
        word.Id,
        word.Word,
        word.Translation,
        word.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
        "生词",
        word.Phonetic is { Length: > 0 } phonetic
            ? $"{phonetic} · {LanguageCatalog.DisplayName(word.SourceLanguage)} → {LanguageCatalog.DisplayName(word.TargetLanguage)}"
            : $"{LanguageCatalog.DisplayName(word.SourceLanguage)} → {LanguageCatalog.DisplayName(word.TargetLanguage)}",
        word.Word,
        word.Translation,
        word.Explanation,
        null,
        word);

    private void ApplyFilter()
    {
        _rowsView.Refresh();
        var query = LibrarySearchBox?.Text?.Trim() ?? string.Empty;
        var visibleCount = _rowsView.Cast<LibraryRow>().Count();
        var totalCount = _mode == LibraryMode.History ? _allHistory.Count : _allVocabulary.Count;

        // These commands act on the whole selected collection, not the filter.
        ClearCurrentButton.IsEnabled = totalCount > 0;
        ExportMenuButton.IsEnabled = totalCount > 0;

        LibraryEmptyText.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryCountText.Text = string.IsNullOrEmpty(query)
            ? (_mode == LibraryMode.History
                ? $"{_allHistory.Count} 条记录"
                : $"{_allVocabulary.Count} 个生词")
            : $"匹配 {visibleCount} / {totalCount}";

        if (!string.IsNullOrEmpty(query))
        {
            InlineRetryVocabularyButton.Visibility = Visibility.Collapsed;
            LibraryEmptyTitle.Text = "未找到匹配结果";
            LibraryEmptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            LibraryEmptyHint.Text = "请尝试更换关键词";
        }
        else if (_mode == LibraryMode.History)
        {
            InlineRetryVocabularyButton.Visibility = Visibility.Collapsed;
            LibraryEmptyTitle.Text = "暂无历史记录";
            LibraryEmptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            LibraryEmptyHint.Text = "划词、截图或工作台输入后自动保存";
        }
        else if (_vocabulary is not null && _vocabulary.LoadState != Services.VocabularyLoadState.Ok)
        {
            InlineRetryVocabularyButton.Visibility = Visibility.Visible;
            LibraryEmptyTitle.Text = "生词本加载受阻";
            LibraryEmptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            LibraryEmptyHint.Text = _vocabulary.LoadState switch
            {
                Services.VocabularyLoadState.TooLarge => "生词本文件超过 32MiB 上限，已进入只读保护模式。",
                Services.VocabularyLoadState.Locked => "生词本文件被其他程序占用，已进入只读保护模式。",
                Services.VocabularyLoadState.NoAccess => "生词本文件没有读取权限，已进入只读保护模式。",
                _ => "生词本文件损坏并已隔离备份，当前无法写入。",
            };
        }
        else
        {
            InlineRetryVocabularyButton.Visibility = Visibility.Collapsed;
            LibraryEmptyTitle.Text = "生词本还是空的";
            LibraryEmptyTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            LibraryEmptyHint.Text = "在浮窗或查词栏点击「收藏」";
        }
    }

    private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ================= Detail pane =================

    private LibraryRow? SelectedRow() => LibraryListBox.SelectedItem as LibraryRow;

    private void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRow() is { } row)
        {
            if (_narrowLibrary)
            {
                _narrowShowDetail = true;
            }
            ShowDetail(row);
            ApplyLibraryLayout();
        }
        else
        {
            _narrowShowDetail = false;
            DetailPlaceholder.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Collapsed;
            ApplyLibraryLayout();
        }
    }

    private void ShowDetail(LibraryRow row)
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        DetailContent.ScrollToTop();

        DetailKind.Text = $"{row.Kind} · {row.Timestamp}";
        DetailLanguagePair.Text = row.LanguagePair;
        DetailSource.Text = row.Source;
        try
        {
            MarkdownPresenter.RenderToFlowDocument(DetailTranslation.Document, row.Translation,
                Application.Current?.Resources ?? Resources);
        }
        catch
        {
            DetailTranslation.Document.Blocks.Clear();
            DetailTranslation.Document.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run(row.Translation)));
        }
        DetailExplanation.Text = row.Explanation;
        DetailExplanation.Visibility = string.IsNullOrWhiteSpace(row.Explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void LibraryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (SelectedRow() is { } row)
            {
                LoadRow(row);
            }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (SelectedRow() is { } row)
            {
                DeleteRowNow(row);
            }
        }
    }

    // ================= Detail actions =================

    private void CardSpeak_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is { } row)
        {
            TtsService.Speak(
                _mode == LibraryMode.Vocabulary ? row.Source : MarkdownPresenter.ToPlainText(row.Translation),
                _mode == LibraryMode.Vocabulary
                    ? row.Word?.SourceLanguage ?? "auto"
                    : row.History?.TargetLanguage ?? "zh-CN");
        }
    }

    private async void CardCopy_Click(object sender, RoutedEventArgs e)
    {
        // Same shared formatter as the translate surfaces: the stored raw
        // text leaves as the agreed plain text, not damaged or raw markup.
        if (SelectedRow() is { } row &&
            await Helpers.CopyToClipboardAsync(MarkdownPresenter.ToPlainText(row.Translation, preserveListStructure: true)))
        {
            StatusChanged?.Invoke("已复制译文。", StatusTone.Info);
        }
    }

    private void CardLoad_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is { } row)
        {
            LoadRow(row);
        }
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is { } row)
        {
            DeleteRowNow(row);
        }
    }

    private void DeleteRowNow(LibraryRow row)
    {
        var currentIndex = LibraryListBox.SelectedIndex;
        if (_mode == LibraryMode.History)
        {
            if (!_history.TryRemove(row.Id, out var removed) || removed is null)
            {
                StatusChanged?.Invoke("没有删除。请确认历史文件可写后重试。", StatusTone.Error);
                return;
            }

            var ticket = LibraryUndoJournal.OfferHistory([removed], DateTime.UtcNow);
            ShowUndo(ticket, "已删除 1 条历史。");
            ReloadHistory();
            SelectAdjacentAfterDelete(currentIndex);
            StatusChanged?.Invoke("已删除该条记录。", StatusTone.Info);
        }
        else if (_vocabulary is not null)
        {
            if (!_vocabulary.TryTake(row.Id, out var removed) || removed is null)
            {
                StatusChanged?.Invoke("没有删除。请确认生词本文件可写后重试。", StatusTone.Error);
                return;
            }

            var ticket = LibraryUndoJournal.OfferVocabulary([removed], DateTime.UtcNow);
            ShowUndo(ticket, "已删除 1 条生词。");
            ReloadVocabulary();
            SelectAdjacentAfterDelete(currentIndex);
            StatusChanged?.Invoke("已从生词本移除该词条。", StatusTone.Info);
        }
    }

    private void SelectAdjacentAfterDelete(int previousIndex)
    {
        if (LibraryListBox.Items.Count == 0)
        {
            LibraryListBox.SelectedIndex = -1;
            return;
        }
        var nextIndex = Math.Clamp(previousIndex, 0, LibraryListBox.Items.Count - 1);
        LibraryListBox.SelectedIndex = nextIndex;
    }

    /// <summary>
    /// 进入资料库（或切换历史/生词）且有数据时自动选中最新一条 — 两个
    /// 数据源都按时间倒序，索引 0 即最新。空仓或已有选中时不动，占位
    /// 提示与用户当前的浏览位置都不会被打扰。
    /// </summary>
    internal void SelectLatestRowOrNothing()
    {
        if (LibraryListBox.Items.Count == 0 || LibraryListBox.SelectedIndex >= 0)
        {
            return;
        }
        LibraryListBox.SelectedIndex = 0;
        LibraryListBox.ScrollIntoView(LibraryListBox.SelectedItem);
    }

    private void LoadRow(LibraryRow row) =>
        LoadToTranslate?.Invoke(
            row.Source, row.Translation, row.Explanation,
            row.History?.SourceLanguage ?? row.Word?.SourceLanguage,
            row.History?.TargetLanguage ?? row.Word?.TargetLanguage,
            _mode == LibraryMode.History ? "历史记录" : "生词本");

    // ================= Export & clear =================

    private void ClearCurrent_Click(object sender, RoutedEventArgs e) => ClearCurrent();

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
        menu.HorizontalOffset = -10;
        menu.VerticalOffset = -2;
        menu.IsOpen = true;
    }

    private static System.Windows.Controls.MenuItem MakeMenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void ExportHistory(string format)
    {
        try
        {
            var content = format == "csv" ? _history.ExportToCsv() : _history.ExportToMarkdown();
            var written = ExportFileWriter.WriteExclusive(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "PopGlot_History",
                format,
                content,
                DateTime.Now);
            ShowExport(written.FullPath);
            StatusChanged?.Invoke("已导出。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            HideExport();
            StatusChanged?.Invoke(ExportFileWriter.DescribeWriteFailure(exception), StatusTone.Error);
        }
    }

    private void ExportVocabulary(string format)
    {
        if (_vocabulary is null)
        {
            return;
        }

        try
        {
            var (stem, extension, content) = format switch
            {
                "anki" => ("PopGlot_Anki_Export", "tsv", _vocabulary.ExportToAnkiTsv()),
                "md" => ("PopGlot_Vocabulary", "md", _vocabulary.ExportToMarkdown()),
                _ => ("PopGlot_Vocabulary", "csv", _vocabulary.ExportToCsv()),
            };
            var written = ExportFileWriter.WriteExclusive(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                stem,
                extension,
                content,
                DateTime.Now);
            ShowExport(written.FullPath);
            StatusChanged?.Invoke("已导出。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            HideExport();
            StatusChanged?.Invoke(ExportFileWriter.DescribeWriteFailure(exception), StatusTone.Error);
        }
    }

    private void ClearCurrent()
    {
        if (_mode == LibraryMode.History)
        {
            if (!_history.TryClear(out var removed))
            {
                StatusChanged?.Invoke("清空历史失败。请确认文件可写后重试。", StatusTone.Error);
                return;
            }

            if (removed.Count > 0)
            {
                var ticket = LibraryUndoJournal.OfferHistory(removed, DateTime.UtcNow);
                ShowUndo(ticket, $"已清空 {removed.Count} 条历史。");
            }

            StatusChanged?.Invoke("历史记录已清空。", StatusTone.Info);
            ReloadHistory();
        }
        else if (_vocabulary is not null)
        {
            if (!_vocabulary.TryClear(out var removed))
            {
                StatusChanged?.Invoke("清空生词本失败。请确认文件可写后重试。", StatusTone.Error);
                return;
            }

            if (removed.Count > 0)
            {
                var ticket = LibraryUndoJournal.OfferVocabulary(removed, DateTime.UtcNow);
                ShowUndo(ticket, $"已清空 {removed.Count} 条生词。");
            }

            StatusChanged?.Invoke("生词本已清空。", StatusTone.Info);
            ReloadVocabulary();
        }
    }

    private void ShowUndo(LibraryUndoTicket ticket, string text)
    {
        _undoTicketId = ticket.Id;
        LibraryUndoText.Text = text;
        LibraryUndoBar.Visibility = Visibility.Visible;
        _undoTimer.Start();
    }

    private void RefreshUndoBar()
    {
        if (LibraryUndoBar is null)
        {
            return;
        }

        var current = _undoTicketId is { } id
            ? LibraryUndoJournal.Current(DateTime.UtcNow)
            : null;
        if (current is null || _undoTicketId != current.Id)
        {
            _undoTicketId = null;
            LibraryUndoBar.Visibility = Visibility.Collapsed;
            _undoTimer.Stop();
        }
    }

    private void LibraryUndo_Click(object sender, RoutedEventArgs e)
    {
        if (_undoTicketId is not { } id)
        {
            RefreshUndoBar();
            return;
        }

        var ticket = LibraryUndoJournal.Take(id, DateTime.UtcNow);
        RefreshUndoBar();
        if (ticket is null)
        {
            StatusChanged?.Invoke("撤销已失效。", StatusTone.Warning);
            return;
        }

        if (ticket.Surface == LibrarySurface.History)
        {
            if (!_history.InsertMissing(ticket.HistoryItems, out var inserted) || inserted == 0)
            {
                var again = LibraryUndoJournal.OfferHistory(ticket.HistoryItems, DateTime.UtcNow);
                ShowUndo(again, "撤销没有写回。");
                StatusChanged?.Invoke("撤销没有写回。请确认历史文件可写后重试。", StatusTone.Error);
                return;
            }

            ReloadHistory();
            StatusChanged?.Invoke("已恢复刚才删除的历史。", StatusTone.Success);
            return;
        }

        if (_vocabulary is null ||
            !_vocabulary.InsertMissing(ticket.VocabularyItems, out var restored) ||
            restored == 0)
        {
            var again = LibraryUndoJournal.OfferVocabulary(ticket.VocabularyItems, DateTime.UtcNow);
            ShowUndo(again, "撤销没有写回。");
            StatusChanged?.Invoke("撤销没有写回。请确认生词本文件可写后重试。", StatusTone.Error);
            return;
        }

        ReloadVocabulary();
        StatusChanged?.Invoke("已恢复刚才删除的生词。", StatusTone.Success);
    }

    private void LibrarySplitGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLibraryLayout();

    private void LibraryBack_Click(object sender, RoutedEventArgs e)
    {
        _narrowShowDetail = false;
        ApplyLibraryLayout();
        if (LibraryListBox.SelectedItem is not null)
        {
            LibraryListBox.ScrollIntoView(LibraryListBox.SelectedItem);
        }
    }

    /// <summary>
    /// Wide windows keep the 320 DIP list. Narrow windows show either the list
    /// or the detail, with a way back that keeps the selection.
    /// </summary>
    private void ApplyLibraryLayout()
    {
        if (LibrarySplitGrid is null || ListColumn is null || DetailColumn is null)
        {
            return;
        }

        var width = LibrarySplitGrid.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _narrowLibrary = width < 560;
        if (!_narrowLibrary)
        {
            _narrowShowDetail = false;
            ListPane.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            LibrarySplitter.Visibility = Visibility.Visible;
            LibraryBackButton.Visibility = Visibility.Collapsed;
            ListColumn.Width = new GridLength(320);
            ListColumn.MinWidth = 220;
            SplitterColumn.Width = new GridLength(8);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        LibrarySplitter.Visibility = Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(0);
        ListColumn.MinWidth = 0;
        if (_narrowShowDetail && LibraryListBox.SelectedItem is not null)
        {
            ListPane.Visibility = Visibility.Collapsed;
            DetailPane.Visibility = Visibility.Visible;
            ListColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            LibraryBackButton.Visibility = Visibility.Visible;
            return;
        }

        ListPane.Visibility = Visibility.Visible;
        DetailPane.Visibility = Visibility.Collapsed;
        ListColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(0);
        LibraryBackButton.Visibility = Visibility.Collapsed;
    }

    private void ShowExport(string path)
    {
        _exportPath = path;
        LibraryExportBar.Visibility = Visibility.Visible;
    }

    private void HideExport()
    {
        _exportPath = null;
        if (LibraryExportBar is not null)
        {
            LibraryExportBar.Visibility = Visibility.Collapsed;
        }
    }

    private void DismissExport_Click(object sender, RoutedEventArgs e) => HideExport();

    private void OpenExportFile_Click(object sender, RoutedEventArgs e) => OpenExport(file: true);

    private void OpenExportFolder_Click(object sender, RoutedEventArgs e) => OpenExport(file: false);

    private void OpenExport(bool file)
    {
        if (string.IsNullOrWhiteSpace(_exportPath) || !System.IO.File.Exists(_exportPath))
        {
            HideExport();
            StatusChanged?.Invoke("找不到刚导出的文件。", StatusTone.Warning);
            return;
        }

        try
        {
            var target = file ? _exportPath : System.IO.Path.GetDirectoryName(_exportPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = target ?? _exportPath,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            StatusChanged?.Invoke("无法打开导出的文件。", StatusTone.Error);
        }
    }
}
