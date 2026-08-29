using System.Windows;
using System.Windows.Input;

namespace PopGlot.Windows;

/// <summary>
/// A button that records the next key combination the user presses.
/// </summary>
/// <remarks>
/// Replaces the fixed drop-down of six preset combinations. If another app owns
/// a shortcut, the user can now simply pick a different one instead of being
/// stuck with whatever the list happened to offer.
/// </remarks>
internal sealed class HotkeyRecorder : Button
{
    public static readonly DependencyProperty BindingValueProperty =
        DependencyProperty.Register(
            nameof(BindingValue),
            typeof(HotkeyBinding),
            typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBindingValueChanged));

    private static readonly DependencyPropertyKey IsRecordingPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsRecording),
            typeof(bool),
            typeof(HotkeyRecorder),
            new PropertyMetadata(false));

    /// <summary>True while waiting for the user to press a combination.</summary>
    public static readonly DependencyProperty IsRecordingProperty =
        IsRecordingPropertyKey.DependencyProperty;

    public HotkeyRecorder()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Content = HotkeyBinding.SelectionDefault.DisplayName;
    }

    public HotkeyBinding? BindingValue
    {
        get => (HotkeyBinding?)GetValue(BindingValueProperty);
        set => SetValue(BindingValueProperty, value);
    }

    public bool IsRecording
    {
        get => (bool)GetValue(IsRecordingProperty);
        private set => SetValue(IsRecordingPropertyKey, value);
    }

    private static void OnBindingValueChanged(
        DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        if (element is HotkeyRecorder recorder && !recorder.IsRecording)
        {
            recorder.Content = (args.NewValue as HotkeyBinding)?.DisplayName ?? "未设置";
        }
    }

    protected override void OnClick()
    {
        base.OnClick();
        StartRecording();
    }

    private void StartRecording()
    {
        IsRecording = true;
        Content = "按下组合键…";
        Focus();
        Keyboard.Focus(this);
    }

    private void StopRecording()
    {
        IsRecording = false;
        Content = BindingValue?.DisplayName ?? "未设置";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsRecording)
        {
            StopRecording();
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // Alt-based combinations arrive as Key.System with the real key in
        // SystemKey; without this, every Alt shortcut records as "Alt+System".
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyBinding.IsModifierKey(virtualKey))
        {
            // Still holding modifiers; wait for the real key.
            return;
        }

        uint modifiers = 0;
        var pressed = Keyboard.Modifiers;
        if ((pressed & ModifierKeys.Control) != 0) modifiers |= HotkeyBinding.ModControl;
        if ((pressed & ModifierKeys.Alt) != 0) modifiers |= HotkeyBinding.ModAlt;
        if ((pressed & ModifierKeys.Shift) != 0) modifiers |= HotkeyBinding.ModShift;
        if ((pressed & ModifierKeys.Windows) != 0) modifiers |= HotkeyBinding.ModWin;

        var candidate = new HotkeyBinding(modifiers, virtualKey);
        if (!candidate.IsValid)
        {
            Content = "需要 Ctrl / Alt / Win";
            return;
        }

        IsRecording = false;
        BindingValue = candidate;
        Content = candidate.DisplayName;
        RaiseEvent(new RoutedEventArgs(RecordedEvent, this));
    }

    public static readonly RoutedEvent RecordedEvent = EventManager.RegisterRoutedEvent(
        nameof(Recorded),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(HotkeyRecorder));

    public event RoutedEventHandler Recorded
    {
        add => AddHandler(RecordedEvent, value);
        remove => RemoveHandler(RecordedEvent, value);
    }
}
