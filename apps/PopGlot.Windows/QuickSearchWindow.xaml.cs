using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

public partial class QuickSearchWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore _vocabulary;
    private readonly TranslationCoordinator _coordinator;
    private readonly QuickSearchState _state = new();
    private CancellationTokenSource? _cts;
    private bool _isClosed;

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
            var settings = CoreBridge.GetSettings();
            LangBadge.Text =
                $"{LanguageCatalog.DisplayName(settings.SourceLanguage)} → " +
                $"{LanguageCatalog.DisplayName(settings.TargetLanguage)}";
            SyncUiWithState();
        };

        Closed += (_, _) => OnClosedCleanup();
    }

    internal QuickSearchState State => _state;
    internal TextBox StreamBox => ResultStreamBox;
    internal RichTextBox RichBox => ResultRichBox;
    internal TextBlock FooterStatusBlock => FooterStatus;
    internal TextBlock StreamIndicatorBlock => StreamIndicator;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isClosed) return;

        var text = SearchBox.Text;
        if (text.Trim() != _state.CurrentQuery)
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;

            _state.OnQueryTextChanged(text);
            SyncUiWithState();
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
        if (_isClosed) return;

        var text = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        _state.StartNewSearch(text);
        var epoch = _state.CurrentEpoch;
        SyncUiWithState();

        var progress = new Progress<TranslationStreamUpdate>(update =>
        {
            if (_isClosed) return;
            if (_state.OnStreamUpdate(update, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        });

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
                token,
                onStageChanged: stage =>
                {
                    if (_isClosed) return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosed) return;
                        if (_state.OnStageChanged(stage, epoch, SearchBox.Text.Trim()))
                        {
                            SyncUiWithState();
                        }
                    });
                },
                progress: progress,
                epoch: epoch);

            if (_isClosed || token.IsCancellationRequested) return;

            if (_state.OnSessionCompleted(session, epoch, SearchBox.Text.Trim()))
            {
                if (_state.IsRichBoxVisible && !string.IsNullOrWhiteSpace(_state.FinalRenderedText))
                {
                    try
                    {
                        MarkdownPresenter.RenderToFlowDocument(
                            ResultRichBox.Document,
                            _state.FinalRenderedText,
                            Application.Current?.Resources ?? Resources);
                    }
                    catch
                    {
                        ResultRichBox.Visibility = Visibility.Collapsed;
                        ResultStreamBox.Visibility = Visibility.Visible;
                    }
                }
                SyncUiWithState();
            }
        }
        catch (OperationCanceledException)
        {
            if (_isClosed) return;
            if (_state.OnCancelled(epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            if (_state.OnException(ex, epoch, SearchBox.Text.Trim()))
            {
                SyncUiWithState();
            }
        }
    }

    private void SyncUiWithState()
    {
        if (_isClosed) return;

        ResultContainer.Visibility = _state.IsResultVisible ? Visibility.Visible : Visibility.Collapsed;
        ResultStreamBox.Visibility = _state.IsStreamLayerVisible ? Visibility.Visible : Visibility.Collapsed;
        ResultStreamBox.Text = _state.AccumulatedText;
        if (_state.IsStreamLayerVisible)
        {
            ResultStreamBox.CaretIndex = ResultStreamBox.Text.Length;
            ResultStreamBox.ScrollToEnd();
        }

        ResultRichBox.Visibility = _state.IsRichBoxVisible ? Visibility.Visible : Visibility.Collapsed;
        StreamIndicator.Visibility = _state.IsStreamIndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        IncompleteBadge.Visibility = _state.IsIncompleteBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.Visibility = _state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

        CopyButton.IsEnabled = _state.CanCopy;
        SpeakButton.IsEnabled = _state.CanSpeak;
        StarButton.IsEnabled = _state.CanStar;

        FooterStatus.Text = _state.StatusText;

        if (!string.IsNullOrWhiteSpace(_state.Phonetic))
        {
            PhoneticLabel.Text = $"[{_state.Phonetic}]";
            PhoneticLabel.Visibility = Visibility.Visible;
        }
        else
        {
            PhoneticLabel.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrWhiteSpace(_state.Explanation))
        {
            ExplanationLabel.Text = _state.Explanation;
            ExplanationCard.Visibility = Visibility.Visible;
        }
        else
        {
            ExplanationCard.Visibility = Visibility.Collapsed;
        }

        UpdateStarButton();
    }

    private void SpeakCurrent()
    {
        if (_isClosed || !_state.CanSpeak) return;

        var textToSpeak = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)
            ? _state.FinalRenderedText
            : !string.IsNullOrWhiteSpace(_state.AccumulatedText)
                ? _state.AccumulatedText
                : SearchBox.Text.Trim();

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
        if (_isClosed || !_state.CanCopy) return;

        var textToCopy = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)
            ? _state.FinalRenderedText
            : _state.AccumulatedText;

        if (!string.IsNullOrWhiteSpace(textToCopy))
        {
            try
            {
                Clipboard.SetText(textToCopy);
                FooterStatus.Text = "已复制译文到剪贴板";
            }
            catch { }
        }
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || !_state.CanStar) return;

        var word = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(word)) return;

        var targetText = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)
            ? _state.FinalRenderedText
            : _state.AccumulatedText;

        if (string.IsNullOrWhiteSpace(targetText)) return;

        var isStarred = _vocabulary.ToggleStar(
            word,
            targetText,
            _state.Phonetic ?? "",
            _state.Explanation ?? "");

        UpdateStarButton();
        FooterStatus.Text = isStarred ? "★ 已添加到生词本" : "已从生词本移除";
    }

    private void UpdateStarButton()
    {
        var word = SearchBox.Text.Trim();
        var starred = !string.IsNullOrWhiteSpace(word) && _vocabulary.IsStarred(word);
        StarIcon.Fill = (Brush)FindResource(starred ? "AccentBrush" : "TextSecondaryBrush");
        StarButton.ToolTip = starred ? "从生词本移除" : "加入生词本 (Anki)";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        OnClosedCleanup();
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnClosedCleanup();
            Close();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        OnClosedCleanup();
        Close();
    }

    private void OnClosedCleanup()
    {
        if (_isClosed) return;
        _isClosed = true;
        _state.OnClose();
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
        _cts = null;
    }
}
