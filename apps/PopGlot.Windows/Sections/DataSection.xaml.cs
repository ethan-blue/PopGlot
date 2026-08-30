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
        _clearHistoryConfirm = ConfirmButton.Attach(ClearHistoryButton, "确认清空？", ClearHistory);
        _clearVocabularyConfirm = ConfirmButton.Attach(ClearVocabularyButton, "确认清空？", ClearVocabulary);
    }

    internal void Initialize(HistoryStore history, VocabularyStore? vocabulary)
    {
        _history = history;
        _vocabulary = vocabulary;
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
        StatusChanged?.Invoke(
            cleared ? "历史记录已清空。" : "清空历史失败：文件正被占用。",
            cleared ? StatusTone.Info : StatusTone.Error);
        DataCleared?.Invoke();
    }

    private void ClearVocabulary()
    {
        if (_vocabulary is null)
        {
            return;
        }
        _vocabulary.Clear();
        StatusChanged?.Invoke("生词本已清空。", StatusTone.Info);
        DataCleared?.Invoke();
    }
}
