using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PopGlot.Windows;

/// <summary>
/// A button that records the next key combination the user presses, with live
/// modifier preview, right-click quick unbind, and inline conflict styling.
/// </summary>
internal sealed class HotkeyRecorder : Button
{
    private const string DefaultHelpTip = "点击后按快捷键（支持组合键，或单按 F1–F12 / PrintScreen）；Esc 取消，退格或右键停用";

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

    private string? _conflictReason;

    public HotkeyRecorder()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Content = "未设置";
        ContextMenu = BuildContextMenu();
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

    public string HelpTip { get; set; } = DefaultHelpTip;

    internal void SetConflictState(string? conflictReason)
    {
        _conflictReason = string.IsNullOrWhiteSpace(conflictReason) ? null : conflictReason;
        var helpTip = string.IsNullOrWhiteSpace(HelpTip) ? DefaultHelpTip : HelpTip;
        if (_conflictReason is not null)
        {
            SetResourceReference(BorderBrushProperty, "WarningBrush");
            ToolTip = $"{_conflictReason}\n{helpTip}";
        }
        else
        {
            SetResourceReference(BorderBrushProperty, "BorderStrongBrush");
            ToolTip = helpTip;
        }
    }

    internal void Unbind()
    {
        IsRecording = false;
        BindingValue = HotkeyBinding.UnsetBinding;
        Content = "未设置";
        RecordingStateChanged?.Invoke(this, false);
        RaiseEvent(new RoutedEventArgs(RecordedEvent, this));
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var clearItem = new System.Windows.Controls.MenuItem { Header = "停用此快捷键" };
        clearItem.Click += (_, _) => Unbind();
        menu.Opened += (_, _) =>
            clearItem.IsEnabled = BindingValue is { IsUnset: false };
        menu.Items.Add(clearItem);
        return menu;
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
        Content = "请按快捷键…";
        RecordingStateChanged?.Invoke(this, true);
        Focus();
        Keyboard.Focus(this);
    }

    private void StopRecording()
    {
        IsRecording = false;
        Content = BindingValue?.DisplayName ?? "未设置";
        RecordingStateChanged?.Invoke(this, false);
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

        var pressed = Keyboard.Modifiers;
        var hasMainModifier = (pressed & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

        // Bare Backspace/Delete unbinds the action: "no shortcut" is a
        // first-class choice for a lightweight tool, not an error state.
        if (!hasMainModifier && key is Key.Back or Key.Delete)
        {
            Unbind();
            return;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyBinding.IsModifierKey(virtualKey))
        {
            Content = FormatLiveModifiers(pressed, addedKey: key);
            return;
        }

        CommitRecordedKey(virtualKey, pressed);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!IsRecording)
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Windows often delivers PrintScreen (VK_SNAPSHOT) only on KeyUp.
        if (key == Key.Snapshot)
        {
            e.Handled = true;
            CommitRecordedKey(0x2C, Keyboard.Modifiers);
            return;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyBinding.IsModifierKey(virtualKey))
        {
            e.Handled = true;
            Content = FormatLiveModifiers(Keyboard.Modifiers, removedKey: key);
            return;
        }

        base.OnPreviewKeyUp(e);
    }

    private void CommitRecordedKey(uint virtualKey, ModifierKeys pressed)
    {
        uint modifiers = 0;
        if ((pressed & ModifierKeys.Control) != 0) modifiers |= HotkeyBinding.ModControl;
        if ((pressed & ModifierKeys.Alt) != 0) modifiers |= HotkeyBinding.ModAlt;
        if ((pressed & ModifierKeys.Shift) != 0) modifiers |= HotkeyBinding.ModShift;
        if ((pressed & ModifierKeys.Windows) != 0) modifiers |= HotkeyBinding.ModWin;

        var candidate = new HotkeyBinding(modifiers, virtualKey);
        if (!candidate.IsValid)
        {
            Content = "需 Ctrl/Alt/Win 或 F1–F12";
            return;
        }

        IsRecording = false;
        BindingValue = candidate;
        Content = candidate.DisplayName;
        RecordingStateChanged?.Invoke(this, false);
        RaiseEvent(new RoutedEventArgs(RecordedEvent, this));
    }

    internal static string FormatLiveModifiers(
        ModifierKeys modifiers,
        Key addedKey = Key.None,
        Key removedKey = Key.None)
    {
        var win = (modifiers & ModifierKeys.Windows) != 0;
        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;

        switch (addedKey)
        {
            case Key.LWin or Key.RWin: win = true; break;
            case Key.LeftCtrl or Key.RightCtrl: ctrl = true; break;
            case Key.LeftAlt or Key.RightAlt: alt = true; break;
            case Key.LeftShift or Key.RightShift: shift = true; break;
        }

        switch (removedKey)
        {
            case Key.LWin or Key.RWin: win = false; break;
            case Key.LeftCtrl or Key.RightCtrl: ctrl = false; break;
            case Key.LeftAlt or Key.RightAlt: alt = false; break;
            case Key.LeftShift or Key.RightShift: shift = false; break;
        }

        var parts = new List<string>(4);
        if (win) parts.Add("Win");
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        return parts.Count == 0
            ? "请按快捷键…"
            : $"{string.Join(" + ", parts)} + …";
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

    internal event EventHandler<bool>? RecordingStateChanged;
}
