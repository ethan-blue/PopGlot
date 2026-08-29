using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class TranslateSection : System.Windows.Controls.UserControl
{
    private TranslationCoordinator? _coordinator;
    private VocabularyStore? _vocabulary;
    private CancellationTokenSource? _translateOperation;
    private bool _languageChangeSuspended = true;

    public TranslateSection()
    {
        InitializeComponent();
        TranslateSourceLang.ItemsSource = LanguageCatalog.Sources;
        TranslateTargetLang.ItemsSource = LanguageCatalog.Targets;

        // Start from the persisted language pair; both this workbench and the
        // floating panel keep the pair in sync through core settings.
        try
        {
            var stored = CoreBridge.GetSettings();
            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(stored.SourceLanguage);
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(stored.TargetLanguage);
        }
        catch (Exception)
        {
            // Headless/offline contexts still get usable defaults.
            TranslateSourceLang.SelectedIndex = 0;
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget("zh-CN");
        }
        _languageChangeSuspended = false;
    }

    internal void Initialize(TranslationCoordinator coordinator, VocabularyStore? vocabulary)
    {
        _coordinator = coordinator;
        _vocabulary = vocabulary;
    }

    // ================= Public accessors for MainWindow =================

    internal ComboBox SourceLangCombo => TranslateSourceLang;
    internal ComboBox TargetLangCombo => TranslateTargetLang;
    internal TextBox InputBox => TranslateInput;
    internal TextBox ResultBox => TranslateResult;
    internal TextBlock ExplanationText => TranslateExplanation;
    internal ScrollViewer ExplanationBox => TranslateExplanationBox;

    /// <summary>Compact mode drops secondary hints; panes stay side by side.</summary>
    internal void SetCompact(bool compact)
    {
        TranslateShortcutHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    internal TextBlock EngineBadge => TranslateEngineBadge;
    internal TextBlock StatusBlock => TranslateStatus;

    // ================= Event handlers =================

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async void TranslateInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            return;
        }
        e.Handled = true;
        await TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        if (_coordinator is null) return;
        var source = TranslateInput.Text.Trim();
        if (string.IsNullOrEmpty(source))
        {
            TranslateStatus.Text = "请先输入要翻译的内容。";
            return;
        }

        _translateOperation?.Cancel();
        _translateOperation?.Dispose();
        var operation = new CancellationTokenSource();
        _translateOperation = operation;

        var sourceLang = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
        var targetLang = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");

        TranslateButton.IsEnabled = false;
        TranslateProgress.Visibility = Visibility.Visible;
        TranslateStatus.Text = "正在翻译…";
        TranslateEngineBadge.Text = "翻译中";

        try
        {
            var session = await _coordinator.TranslateTextAsync(
                source, sourceLang, targetLang, TranslationInputSource.Manual, operation.Token);

            if (session.IsSuccess)
            {
                TranslateResult.Text = session.TranslatedText;
                TranslateEngineBadge.Text = session.PipelineLabel ?? "已翻译";
                TranslateStatus.Text = session.Stage == TranslationSessionStage.Partial
                    ? $"部分完成 · {session.Timing.TotalElapsedMs} ms · 见下方说明"
                    : $"完成 · {session.Timing.TotalElapsedMs} ms";

                var notes = new List<string>();
                if (!string.IsNullOrWhiteSpace(session.Explanation))
                {
                    notes.Add(session.Explanation.Trim());
                }
                notes.AddRange(session.Warnings);
                TranslateExplanation.Text = string.Join("\n", notes);
                TranslateExplanationBox.Visibility = notes.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            else if (session.Stage == TranslationSessionStage.Cancelled)
            {
                TranslateStatus.Text = "已取消。";
                TranslateEngineBadge.Text = "已取消";
            }
            else
            {
                var message = session.Error?.Message ?? "翻译未完成";
                var suggestion = session.Error?.ActionableSuggestion;
                TranslateEngineBadge.Text = "未完成";
                TranslateStatus.Text = string.IsNullOrWhiteSpace(suggestion)
                    ? message
                    : $"{message} {suggestion}";
                TranslateResult.Text = TranslationPanelWindow.FriendlyError(message);
                TranslateExplanation.Text = string.IsNullOrWhiteSpace(suggestion)
                    ? message
                    : $"{message}\n{suggestion}";
                TranslateExplanationBox.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            TranslateStatus.Text = "已取消。";
            TranslateEngineBadge.Text = "已取消";
        }
        catch (Exception exception)
        {
            TranslateEngineBadge.Text = "未完成";
            TranslateStatus.Text = $"翻译失败：{exception.Message}";
            TranslateResult.Text = TranslationPanelWindow.FriendlyError(exception.Message);
            TranslateExplanation.Text = exception.Message;
            TranslateExplanationBox.Visibility = Visibility.Visible;
        }
        finally
        {
            TranslateButton.IsEnabled = true;
            TranslateProgress.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_translateOperation, operation))
            {
                _translateOperation = null;
            }
            operation.Dispose();
        }
    }

    private void TranslateInput_TextChanged(object sender, TextChangedEventArgs e) =>
        TranslateCounter.Text = $"{TranslateInput.Text.Length} 字符";

    // ================= Language pair =================

    private void SourceLang_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PersistLanguagePair();

    private void TargetLang_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PersistLanguagePair();

    /// <summary>Remembers the pair so the floating panel opens the same way.</summary>
    private void PersistLanguagePair()
    {
        if (_languageChangeSuspended)
        {
            return;
        }
        try
        {
            var settings = CoreBridge.GetSettings();
            var source = Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto);
            var target = Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN");
            if (settings.SourceLanguage == source && settings.TargetLanguage == target)
            {
                return;
            }
            CoreBridge.SaveSettings(settings with
            {
                SourceLanguage = source,
                TargetLanguage = target,
            });
        }
        catch (InvalidOperationException)
        {
            // Remembering the pair is a convenience, never a reason to fail.
        }
    }

    private void TranslateSwap_Click(object sender, RoutedEventArgs e)
    {
        var (source, target) = LanguageCatalog.Swap(
            Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"));

        _languageChangeSuspended = true;
        try
        {
            TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(source);
            TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(target);
        }
        finally
        {
            _languageChangeSuspended = false;
        }
        PersistLanguagePair();

        if (!string.IsNullOrWhiteSpace(TranslateResult.Text))
        {
            TranslateInput.Text = TranslateResult.Text;
            TranslateResult.Clear();
            TranslateExplanationBox.Visibility = Visibility.Collapsed;
        }
    }

    // ================= Result actions =================

    private void TranslateSourceSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(TranslateInput.Text);

    private void TranslateResultSpeak_Click(object sender, RoutedEventArgs e) =>
        SpeakOrStop(TranslateResult.Text);

    private static void SpeakOrStop(string text)
    {
        if (TtsService.IsSpeaking)
        {
            TtsService.Stop();
            return;
        }
        TtsService.Speak(text);
    }

    private void TranslateSourceCopy_Click(object sender, RoutedEventArgs e) => _ = CopySourceToClipboardAsync();

    private void TranslateResultCopy_Click(object sender, RoutedEventArgs e) => _ = CopyResultToClipboardAsync();

    private void TranslateStar_Click(object sender, RoutedEventArgs e)
    {
        if (_vocabulary is null)
        {
            TranslateStatus.Text = "生词本不可用。";
            return;
        }
        var source = TranslateInput.Text.Trim();
        var translation = TranslateResult.Text.Trim();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
        {
            TranslateStatus.Text = "先翻译一段内容再收藏。";
            return;
        }
        var starred = _vocabulary.ToggleStar(
            source, translation,
            string.Empty, string.Empty,
            Helpers.SelectedLanguage(TranslateSourceLang, LanguageCatalog.Auto),
            Helpers.SelectedLanguage(TranslateTargetLang, "zh-CN"));
        TranslateStatus.Text = starred ? "已加入生词本。" : "已从生词本移除。";
    }

    private void TranslateMergeLines_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslateInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var merged = TranslationPanelWindow.MergeHardLineBreaks(text);
        if (merged == text)
        {
            return;
        }
        TranslateInput.Text = merged;
        TranslateStatus.Text = "已合并断行。";
    }

    private async Task CopySourceToClipboardAsync()
    {
        if (await Helpers.CopyToClipboardAsync(TranslateInput.Text))
        {
            TranslateStatus.Text = "已复制原文。";
        }
    }

    private async Task CopyResultToClipboardAsync()
    {
        if (await Helpers.CopyToClipboardAsync(TranslateResult.Text))
        {
            TranslateStatus.Text = "已复制译文。";
        }
    }

    private void TranslateClear_Click(object sender, RoutedEventArgs e)
    {
        _translateOperation?.Cancel();
        TranslateInput.Clear();
        TranslateResult.Clear();
        TranslateExplanationBox.Visibility = Visibility.Collapsed;
        TranslateEngineBadge.Text = "等待输入";
        TranslateStatus.Text = "就绪";
        TranslateInput.Focus();
    }

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null)
    {
        if (!string.IsNullOrWhiteSpace(initialText))
        {
            TranslateInput.Text = initialText;
        }
        _languageChangeSuspended = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceLang))
            {
                TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource(sourceLang);
            }
            if (!string.IsNullOrWhiteSpace(targetLang))
            {
                TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget(targetLang);
            }
        }
        finally
        {
            _languageChangeSuspended = false;
        }
        if (existingTranslation is not null)
        {
            TranslateResult.Text = existingTranslation;
            TranslateEngineBadge.Text = "已展开的译文";
            TranslateStatus.Text = "已从浮窗展开，未重新翻译。";
        }
        TranslateInput.Focus();
        TranslateInput.CaretIndex = TranslateInput.Text.Length;
    }
}
