using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

public partial class QuickSearchWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore _vocabulary;
    private readonly TranslationCoordinator _coordinator;
    private CancellationTokenSource? _cts;
    private string _currentTranslation = string.Empty;
    private string _currentExplanation = string.Empty;
    private string _currentPhonetic = string.Empty;
    private string _lastTranslatedQuery = string.Empty;

    internal QuickSearchWindow(HistoryStore history, VocabularyStore vocabulary)
    {
        _history = history;
        _vocabulary = vocabulary;
        _coordinator = new TranslationCoordinator(_history, _vocabulary);
        InitializeComponent();

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            ThemeService.ApplyWindowChrome(this);
            FooterStatus.Text = "输入文字后按 Enter 翻译";
            var settings = CoreBridge.GetSettings();
            LangBadge.Text =
                $"{LanguageCatalog.DisplayName(settings.SourceLanguage)} → " +
                $"{LanguageCatalog.DisplayName(settings.TargetLanguage)}";
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _cts?.Cancel();
            ResultContainer.Visibility = Visibility.Collapsed;
            SearchProgress.Visibility = Visibility.Collapsed;
            FooterStatus.Text = "输入文字后按 Enter 翻译";
            _lastTranslatedQuery = string.Empty;
            return;
        }

        if (text != _lastTranslatedQuery)
        {
            FooterStatus.Text = "按 Enter 立即翻译 · Shift+Enter 换行";
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return; // Shift+Enter inserts newline
            }
            e.Handled = true;
            await PerformTranslateAsync();
        }
        else if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            SpeakCurrent();
        }
    }

    private async Task PerformTranslateAsync()
    {
        var text = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SearchProgress.Visibility = Visibility.Visible;
        FooterStatus.Text = "正在翻译…";

        try
        {
            var settings = CoreBridge.GetSettings();
            var sourceLang = settings.SourceLanguage ?? LanguageCatalog.Auto;
            var targetLang = settings.TargetLanguage ?? "zh-CN";

            var session = await _coordinator.TranslateTextAsync(
                text,
                sourceLang,
                targetLang,
                TranslationInputSource.QuickSearch,
                token);

            if (token.IsCancellationRequested) return;

            if (session.IsSuccess)
            {
                _lastTranslatedQuery = text;
                _currentTranslation = session.TranslatedText;
                _currentExplanation = session.Explanation;
                _currentPhonetic = session.Phonetic;

                MarkdownPresenter.RenderToFlowDocument(ResultRichBox.Document, _currentTranslation, Application.Current.Resources);
                ResultContainer.Visibility = Visibility.Visible;

                if (!string.IsNullOrWhiteSpace(_currentPhonetic))
                {
                    PhoneticLabel.Text = $"[{_currentPhonetic}]";
                    PhoneticLabel.Visibility = Visibility.Visible;
                }
                else
                {
                    PhoneticLabel.Visibility = Visibility.Collapsed;
                }

                if (!string.IsNullOrWhiteSpace(_currentExplanation))
                {
                    ExplanationLabel.Text = _currentExplanation;
                    ExplanationCard.Visibility = Visibility.Visible;
                }
                else
                {
                    ExplanationCard.Visibility = Visibility.Collapsed;
                }

                UpdateStarButton();
                var engine = session.PipelineLabel ?? "大模型";
                FooterStatus.Text = $"{engine} · {session.Timing.TotalElapsedMs} ms";
            }
            else
            {
                ResultContainer.Visibility = Visibility.Collapsed;
                FooterStatus.Text = session.Error is not null
                    ? $"{session.Error.Message} {session.Error.ActionableSuggestion}".Trim()
                    : "翻译未完成";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            FooterStatus.Text = $"翻译失败: {ex.Message}";
        }
        finally
        {
            SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void SpeakCurrent()
    {
        var textToSpeak = !string.IsNullOrWhiteSpace(_currentTranslation) ? _currentTranslation : SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(textToSpeak))
        {
            if (TtsService.IsSpeaking)
            {
                TtsService.Stop();
            }
            else
            {
                TtsService.Speak(textToSpeak);
            }
        }
    }

    private void Speak_Click(object sender, RoutedEventArgs e) => SpeakCurrent();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentTranslation))
        {
            try
            {
                Clipboard.SetText(_currentTranslation);
                FooterStatus.Text = "已复制译文到剪贴板";
            }
            catch { }
        }
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        var word = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(word)) return;

        var isStarred = _vocabulary.ToggleStar(
            word,
            _currentTranslation,
            _currentPhonetic,
            _currentExplanation);

        UpdateStarButton();
        FooterStatus.Text = isStarred ? "★ 已添加到生词本" : "已从生词本移除";
    }

    private void UpdateStarButton()
    {
        var word = SearchBox.Text.Trim();
        var starred = _vocabulary.IsStarred(word);
        StarIcon.Fill = (System.Windows.Media.Brush)FindResource(starred ? "AccentBrush" : "TextSecondaryBrush");
        StarButton.ToolTip = starred ? "从生词本移除" : "加入生词本 (Anki)";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Close();
    }
}
