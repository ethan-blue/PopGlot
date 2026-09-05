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
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Ctrl+P: speak. Ctrl+R stays reserved for retry semantics
            // elsewhere (translation panel), one key one meaning.
            e.Handled = true;
            SpeakCurrent();
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            Copy_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanStar)
        {
            e.Handled = true;
            Star_Click(this, new RoutedEventArgs());
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
        if (_state.IsStreamLayerVisible)
        {
            // Stick-to-bottom: follow the stream only while the reader sits at
            // the bottom, so scrolling up to re-read is never overridden.
            var stickToBottom = Ui.IsScrolledToBottom(Ui.FindScrollViewer(ResultStreamBox));
            // Append-only when the accumulated text simply grew: wholesale
            // reassignment re-layouts the whole box on every pump tick and
            // makes the window edge flicker.
            var accumulated = _state.AccumulatedText;
            if (accumulated.StartsWith(ResultStreamBox.Text, StringComparison.Ordinal))
            {
                ResultStreamBox.AppendText(accumulated[ResultStreamBox.Text.Length..]);
            }
            else
            {
                ResultStreamBox.Text = accumulated;
            }
            if (stickToBottom)
            {
                ResultStreamBox.ScrollToEnd();
            }
        }

        ResultRichBox.Visibility = _state.IsRichBoxVisible ? Visibility.Visible : Visibility.Collapsed;
        StreamIndicator.Visibility = _state.IsStreamIndicatorVisible ? Visibility.Visible : Visibility.Collapsed;
        IncompleteBadge.Visibility = _state.IsIncompleteBadgeVisible ? Visibility.Visible : Visibility.Collapsed;
        SearchProgress.Visibility = _state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;

        CopyButton.IsEnabled = _state.CanCopy;
        SpeakButton.IsEnabled = _state.CanSpeak;
        StarButton.IsEnabled = _state.CanStar;

        FooterStatus.Text = _state.StatusText;

        if (_state.Stage == QuickSearchUiStage.Failed)

        {

            FooterStatus.Foreground = (Brush)FindResource("DangerBrush");

        }

        else if (_state.Stage is QuickSearchUiStage.Cancelled or QuickSearchUiStage.Partial)

        {

            FooterStatus.Foreground = (Brush)FindResource("WarningBrush");

        }

        else

        {

            FooterStatus.Foreground = (Brush)FindResource("TextSecondaryBrush");

        }



        if (_state.IsIncompleteBadgeVisible)

        {

            IncompleteBadge.Text = _state.Stage == QuickSearchUiStage.Cancelled ? "已取消" : "未完成";

            IncompleteBadge.Foreground = (Brush)FindResource("WarningBrush");

        }



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

            var clean = MarkdownPresenter.ToPlainText(textToSpeak);

            if (TtsService.IsSpeaking)

            {

                TtsService.Stop();

            }

            else

            {

                TtsService.Speak(clean);

            }

        }

    }



    private void Speak_Click(object sender, RoutedEventArgs e) => SpeakCurrent();



    private async void Copy_Click(object sender, RoutedEventArgs e)

    {

        if (_isClosed || !_state.CanCopy) return;



        var textToCopy = !string.IsNullOrWhiteSpace(_state.FinalRenderedText)

            ? _state.FinalRenderedText

            : _state.AccumulatedText;



        if (!string.IsNullOrWhiteSpace(textToCopy))

        {

            var clean = MarkdownPresenter.ToPlainText(textToCopy);

            // Hardened write: a raw Clipboard.SetText on the UI thread freezes

            // the whole window while another app holds the clipboard open.

            FooterStatus.Text = await PopGlot.Windows.Sections.Helpers.CopyToClipboardAsync(clean)

                ? "已复制译文到剪贴板"

                : "剪贴板被其他应用占用，未复制";

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



        var cleanTarget = MarkdownPresenter.ToPlainText(targetText);

        var isStarred = _vocabulary.ToggleStar(

            word,

            cleanTarget,

            _state.Phonetic ?? "",

            _state.Explanation ?? "");



        UpdateStarButton();
        FooterStatus.Text = isStarred ? "已加入生词本" : "已从生词本移除";
    }

    private void UpdateStarButton()
    {
        var word = SearchBox.Text.Trim();
        var starred = !string.IsNullOrWhiteSpace(word) && _vocabulary.IsStarred(word);
        StarIcon.Fill = (Brush)FindResource(starred ? "AccentBrush" : "TextSecondaryBrush");
        StarButton.ToolTip = starred ? "从生词本移除" : "收藏到生词本";
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
        else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanStar)
        {
            e.Handled = true;
            Star_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanSpeak)
        {
            e.Handled = true;
            SpeakCurrent();
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _state.CanCopy)

        {

            // Keep the search editor's native copy behavior. Copying the full

            // translation from the keyboard uses Ctrl+Shift+C instead.

            if (SearchBox.IsKeyboardFocusWithin)

            {

                return;

            }

            e.Handled = true;

            Copy_Click(this, new RoutedEventArgs());

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
