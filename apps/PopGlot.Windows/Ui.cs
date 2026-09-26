using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PopGlot.Windows;

/// <summary>
/// Attached properties the control templates rely on. They exist so a single
/// styled template can serve every call site instead of each window
/// re-inventing borders, placeholders, and icons inline.
/// </summary>
internal static class Ui
{
    // ---- Placeholder text -------------------------------------------------
    // WPF has no "empty text" trigger, so a tiny behaviour mirrors emptiness
    // into HasText and the template binds a watermark to that.

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(Ui),
            new PropertyMetadata(string.Empty, OnPlaceholderChanged));

    public static string GetPlaceholder(DependencyObject element) =>
        (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) =>
        element.SetValue(PlaceholderProperty, value);

    // Not a read-only attached property: XAML trigger conditions require a
    // public setter, and the templates match on this to show the watermark.
    // Only the handlers below ever write it.
    public static readonly DependencyProperty HasTextProperty =
        DependencyProperty.RegisterAttached(
            "HasText",
            typeof(bool),
            typeof(Ui),
            new PropertyMetadata(false));

    public static bool GetHasText(DependencyObject element) =>
        (bool)element.GetValue(HasTextProperty);

    public static void SetHasText(DependencyObject element, bool value) =>
        element.SetValue(HasTextProperty, value);

    /// <summary>
    /// True when an empty PasswordBox represents a credential already stored
    /// in the vault. The template then renders a primary-colour mask instead
    /// of a grey instructional watermark; no fake password enters the control.
    /// </summary>
    public static readonly DependencyProperty IsCredentialMaskProperty =
        DependencyProperty.RegisterAttached(
            "IsCredentialMask",
            typeof(bool),
            typeof(Ui),
            new FrameworkPropertyMetadata(false));

    public static bool GetIsCredentialMask(DependencyObject element) =>
        (bool)element.GetValue(IsCredentialMaskProperty);

    public static void SetIsCredentialMask(DependencyObject element, bool value) =>
        element.SetValue(IsCredentialMaskProperty, value);

    private static void OnPlaceholderChanged(
        DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        switch (element)
        {
            case TextBox textBox:
                textBox.TextChanged -= OnTextBoxTextChanged;
                textBox.TextChanged += OnTextBoxTextChanged;
                SetHasText(textBox, !string.IsNullOrEmpty(textBox.Text));
                break;
            case PasswordBox passwordBox:
                passwordBox.PasswordChanged -= OnPasswordChanged;
                passwordBox.PasswordChanged += OnPasswordChanged;
                SetHasText(passwordBox, passwordBox.SecurePassword.Length > 0);
                break;
        }
    }

    private static void OnTextBoxTextChanged(object sender, TextChangedEventArgs args)
    {
        var textBox = (TextBox)sender;
        SetHasText(textBox, !string.IsNullOrEmpty(textBox.Text));
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        var passwordBox = (PasswordBox)sender;
        SetHasText(passwordBox, passwordBox.SecurePassword.Length > 0);
    }

    // ---- Template shape knobs --------------------------------------------

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(Ui),
            new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);

    /// <summary>
    /// Visual inset for TextBox/PasswordBox templates. WPF's text view applies
    /// Control.Padding internally, so reusing that property on the template
    /// border double-insets the real caret while the watermark moves once.
    /// Keeping the actual Padding at zero gives both layers one shared inset.
    /// </summary>
    public static readonly DependencyProperty ContentPaddingProperty =
        DependencyProperty.RegisterAttached(
            "ContentPadding",
            typeof(Thickness),
            typeof(Ui),
            new FrameworkPropertyMetadata(new Thickness(0)));

    public static Thickness GetContentPadding(DependencyObject element) =>
        (Thickness)element.GetValue(ContentPaddingProperty);

    public static void SetContentPadding(DependencyObject element, Thickness value) =>
        element.SetValue(ContentPaddingProperty, value);

    /// <summary>Glyph shown ahead of a navigation item's label.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon",
            typeof(Geometry),
            typeof(Ui),
            new FrameworkPropertyMetadata(default(Geometry)));

    public static Geometry? GetIcon(DependencyObject element) =>
        (Geometry?)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) =>
        element.SetValue(IconProperty, value);

    /// <summary>Explicit width and height for template-rendered icon glyphs.</summary>
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.RegisterAttached(
            "IconSize",
            typeof(double),
            typeof(Ui),
            new FrameworkPropertyMetadata(14.0));

    public static double GetIconSize(DependencyObject element) =>
        (double)element.GetValue(IconSizeProperty);

    public static void SetIconSize(DependencyObject element, double value) =>
        element.SetValue(IconSizeProperty, value);

    /// <summary>Secondary line rendered under a settings row's title.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.RegisterAttached(
            "Description",
            typeof(string),
            typeof(Ui),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetDescription(DependencyObject element) =>
        (string)element.GetValue(DescriptionProperty);

    public static void SetDescription(DependencyObject element, string value) =>
        element.SetValue(DescriptionProperty, value);

    // ---- Stick-to-bottom scrolling -----------------------------------------
    // Streaming text must follow the stream only while the reader sits at the
    // bottom. An unconditional ScrollToEnd slides the view down under someone
    // who scrolled up to re-read earlier lines.

    /// <summary>Digs the first scroll host out of a visual tree (TextBox internals).</summary>
    internal static ScrollViewer? FindScrollViewer(DependencyObject? node)
    {
        if (node is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>True when the viewer is at — or has no — scrollable overflow.</summary>
    internal static bool IsScrolledToBottom(ScrollViewer? viewer) =>
        viewer is null ||
        viewer.ScrollableHeight <= 0 ||
        viewer.VerticalOffset >= viewer.ScrollableHeight - 1.0;

    // ---- IME Composition tracking (N02) -----------------------------------
    // InputMethod.GetIsInputMethodEnabled only checks whether an IME is enabled
    // on the control (always true on Windows whenever a CJK input method is
    // installed), which mistakenly blocked Enter on every keystroke.
    // Instead, we track active composition (PreviewTextInputStart / Update /
    // TextInput / LostFocus), reset on Escape (IMEs cancel silently), and
    // check Key.ImeProcessed / ImeProcessedKey.

    public static readonly DependencyProperty IsComposingProperty =
        DependencyProperty.RegisterAttached(
            "IsComposing",
            typeof(bool),
            typeof(Ui),
            new PropertyMetadata(false));

    public static bool GetIsComposing(DependencyObject element) =>
        (bool)element.GetValue(IsComposingProperty);

    public static void SetIsComposing(DependencyObject element, bool value) =>
        element.SetValue(IsComposingProperty, value);

    public static void AttachCompositionTracker(TextBox textBox)
    {
        TextCompositionManager.AddPreviewTextInputStartHandler(textBox, OnCompositionStart);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(textBox, OnCompositionUpdate);
        TextCompositionManager.AddPreviewTextInputHandler(textBox, OnCompositionEnd);
        TextCompositionManager.AddTextInputHandler(textBox, OnCompositionEnd);
        textBox.LostFocus += OnTextBoxLostFocus;
        textBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
    }

    private static void OnCompositionStart(object sender, TextCompositionEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            SetIsComposing(d, true);
        }
    }

    private static void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not DependencyObject d)
        {
            return;
        }
        // Escape cancels an active composition, but several IMEs tear the
        // composition down without raising a composition-end event — the
        // IsComposing flag would stay stuck true and every later Enter be
        // swallowed as "the composition owns it". Reset on Escape (some IMEs
        // surface it as an ImeProcessed Escape) so the next Enter submits.
        // Never mark the key handled: the IME still needs it to cancel.
        if (e.Key == Key.Escape || e.ImeProcessedKey == Key.Escape)
        {
            SetIsComposing(d, false);
        }
    }

    private static void OnCompositionUpdate(object sender, TextCompositionEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            var isComp = e.TextComposition is not null && !string.IsNullOrEmpty(e.TextComposition.CompositionText);
            SetIsComposing(d, isComp);
        }
    }

    private static void OnCompositionEnd(object sender, TextCompositionEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            SetIsComposing(d, false);
        }
    }

    private static void OnTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject d)
        {
            SetIsComposing(d, false);
        }
    }

    /// <summary>
    /// Returns true if the element is currently in an active IME composition
    /// or if the key event represents an IME-processed keystroke.
    /// </summary>
    public static bool IsImeComposing(TextBox? textBox, KeyEventArgs? e = null)
    {
        if (e is not null)
        {
            if (e.Key == Key.ImeProcessed || e.ImeProcessedKey != Key.None)
            {
                return true;
            }
        }
        if (textBox is not null && GetIsComposing(textBox))
        {
            return true;
        }
        return false;
    }
}
