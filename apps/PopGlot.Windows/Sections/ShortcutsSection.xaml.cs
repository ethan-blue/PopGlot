
using System.Windows;
using System.Windows.Controls;

namespace PopGlot.Windows.Sections;

public partial class ShortcutsSection : System.Windows.Controls.UserControl
{
    private (string SlotKey, HotkeyBinding Suggested)? _pendingFix;
    private string? _lastEditedSlotKey;

    public ShortcutsSection()
    {
        InitializeComponent();
        foreach (var (slotKey, recorder) in SlotRecorders())
        {
            recorder.Recorded += (_, _) =>
            {
                _lastEditedSlotKey = slotKey;
                SyncClearButtons();
            };
        }
    }

    internal void ClearLastEditedSlot() => _lastEditedSlotKey = null;

    private static bool IsPrimarySlot(string slotKey) =>
        slotKey is "Selection" or "Screenshot" or "QuickSearch";

    internal void SetCompact(bool stacked)
    {
        ShortcutGutter.Width = new System.Windows.GridLength(stacked ? 0 : 24);
        ShortcutRightColumn.Width = stacked
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(InterfaceShortcutGroup, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(InterfaceShortcutGroup, stacked ? 1 : 0);
        InterfaceShortcutGroup.Margin = new System.Windows.Thickness(0, stacked ? 12 : 0, 0, 0);
    }
    internal System.Windows.Controls.Expander MoreExpander => MoreShortcutsExpander;

    // ================= Public accessors for MainWindow =================

    internal HotkeyRecorder SelectionHotkey => SelectionHotkeyRecorder;
    internal HotkeyRecorder ScreenshotHotkey => ScreenshotHotkeyRecorder;
    internal HotkeyRecorder QuickSearchHotkey => QuickSearchHotkeyRecorder;
    internal HotkeyRecorder CloseHotkey => CloseHotkeyRecorder;
    internal HotkeyRecorder ShowWindowHotkey => ShowWindowHotkeyRecorder;
    internal HotkeyRecorder InterfaceWorkbenchHotkey => InterfaceWorkbenchHotkeyRecorder;
    internal HotkeyRecorder InterfaceLibraryHotkey => InterfaceLibraryHotkeyRecorder;
    internal HotkeyRecorder InterfaceSettingsHotkey => InterfaceSettingsHotkeyRecorder;
    internal HotkeyRecorder InterfaceTranslateHotkey => InterfaceTranslateHotkeyRecorder;
    internal HotkeyRecorder InterfaceSpeakHotkey => InterfaceSpeakHotkeyRecorder;
    internal HotkeyRecorder InterfaceModelHotkey => InterfaceModelHotkeyRecorder;
    internal HotkeyRecorder InterfaceStyleHotkey => InterfaceStyleHotkeyRecorder;
    internal HotkeyRecorder InterfaceSwapHotkey => InterfaceSwapHotkeyRecorder;

    internal event System.EventHandler? DefaultsReset;
    internal event System.EventHandler? ProbeRequested;
    internal event System.EventHandler? ShortcutEdited;

    internal void SyncClearButtons()
    {
        static void Sync(Button button, HotkeyRecorder recorder)
        {
            var isSet = recorder.BindingValue is { IsUnset: false };
            button.Visibility = isSet ? Visibility.Visible : Visibility.Hidden;
            button.IsHitTestVisible = isSet;
        }

        Sync(ClearSelectionButton, SelectionHotkey);
        Sync(ClearScreenshotButton, ScreenshotHotkey);
        Sync(ClearQuickSearchButton, QuickSearchHotkey);
    }

    internal void RefreshDiagnostics(
        ShellSettings draft,
        IReadOnlyCollection<HotkeyBinding>? ownedByApp = null,
        bool probeSystem = true,
        bool showCleanFeedback = false,
        Func<HotkeyBinding, bool>? probeAvailable = null)
    {
        SyncClearButtons();

        var issues = HotkeyService.DiagnoseDraft(
            draft,
            ownedByApp,
            probeSystem,
            probeAvailable,
            preferredFixSlotKey: _lastEditedSlotKey);
        var bySlot = issues
            .GroupBy(i => i.SlotKey)
            .ToDictionary(g => g.Key, g => g.First().Message);

        foreach (var (slotKey, recorder) in SlotRecorders())
        {
            recorder.SetConflictState(bySlot.TryGetValue(slotKey, out var msg) ? msg : null);
        }

        if (issues.Any(i => !IsPrimarySlot(i.SlotKey)))
        {
            MoreShortcutsExpander.IsExpanded = true;
        }

        if (issues.Count == 0)
        {
            _pendingFix = null;
            ShortcutFixButton.Visibility = Visibility.Collapsed;
            if (showCleanFeedback)
            {
                ShortcutDiagnosticBar.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                ShortcutDiagnosticBar.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
                ShortcutDiagnosticText.Text = "所有快捷键均可用，暂无冲突或占用。";
                ShortcutDiagnosticBar.Visibility = Visibility.Visible;
            }
            else
            {
                ShortcutDiagnosticBar.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var primary = issues[0];
        ShortcutDiagnosticBar.SetResourceReference(Border.BackgroundProperty, "WarningSoftBrush");
        ShortcutDiagnosticBar.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        ShortcutDiagnosticText.Text = issues.Count > 1
            ? $"{primary.Message}（另有 {issues.Count - 1} 项冲突，悬停红框可查看）"
            : primary.Message;

        if (primary.SuggestedBinding is { } suggestion)
        {
            _pendingFix = (primary.SlotKey, suggestion);
            ShortcutFixButton.Content = $"改用 {suggestion.DisplayName}";
            ShortcutFixButton.ToolTip = $"将「{primary.ActionLabel}」设为当前空闲的 {suggestion.DisplayName}";
            ShortcutFixButton.Visibility = Visibility.Visible;
        }
        else
        {
            _pendingFix = null;
            ShortcutFixButton.Visibility = Visibility.Collapsed;
        }
        ShortcutDiagnosticBar.Visibility = Visibility.Visible;
    }

    private IEnumerable<(string SlotKey, HotkeyRecorder Recorder)> SlotRecorders()
    {
        yield return ("Selection", SelectionHotkeyRecorder);
        yield return ("Screenshot", ScreenshotHotkeyRecorder);
        yield return ("QuickSearch", QuickSearchHotkeyRecorder);
        yield return ("ShowWindow", ShowWindowHotkeyRecorder);
        yield return ("Close", CloseHotkeyRecorder);
        yield return ("InterfaceModel", InterfaceModelHotkeyRecorder);
        yield return ("InterfaceStyle", InterfaceStyleHotkeyRecorder);
        yield return ("InterfaceSwap", InterfaceSwapHotkeyRecorder);
        yield return ("InterfaceTranslate", InterfaceTranslateHotkeyRecorder);
        yield return ("InterfaceSpeak", InterfaceSpeakHotkeyRecorder);
        yield return ("InterfaceWorkbench", InterfaceWorkbenchHotkeyRecorder);
        yield return ("InterfaceLibrary", InterfaceLibraryHotkeyRecorder);
        yield return ("InterfaceSettings", InterfaceSettingsHotkeyRecorder);
    }

    /// <summary>Resets all shortcut fields to their factory defaults.</summary>
    internal void ResetDefaults()
    {
        SelectionHotkey.BindingValue = HotkeyBinding.SelectionDefault;
        ScreenshotHotkey.BindingValue = HotkeyBinding.ScreenshotDefault;
        QuickSearchHotkey.BindingValue = HotkeyBinding.QuickSearchDefault;
        CloseHotkey.BindingValue = HotkeyBinding.CloseDefault;
        ShowWindowHotkey.BindingValue = HotkeyBinding.ShowWindowDefault;
        InterfaceModelHotkey.BindingValue = HotkeyBinding.InterfaceModelDefault;
        InterfaceStyleHotkey.BindingValue = HotkeyBinding.InterfaceStyleDefault;
        InterfaceSwapHotkey.BindingValue = HotkeyBinding.InterfaceSwapDefault;
        InterfaceWorkbenchHotkey.BindingValue = HotkeyBinding.InterfaceWorkbenchDefault;
        InterfaceLibraryHotkey.BindingValue = HotkeyBinding.InterfaceLibraryDefault;
        InterfaceSettingsHotkey.BindingValue = HotkeyBinding.InterfaceSettingsDefault;
        InterfaceTranslateHotkey.BindingValue = HotkeyBinding.InterfaceTranslateDefault;
        InterfaceSpeakHotkey.BindingValue = HotkeyBinding.InterfaceSpeakDefault;
        _lastEditedSlotKey = null;
        SyncClearButtons();
        DefaultsReset?.Invoke(this, System.EventArgs.Empty);
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e) =>
        ResetDefaults();

    private void ProbeAvailabilityButton_Click(object sender, RoutedEventArgs e) =>
        ProbeRequested?.Invoke(this, System.EventArgs.Empty);

    private void ClearShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
        {
            return;
        }
        var target = slot switch
        {
            "Selection" => SelectionHotkeyRecorder,
            "Screenshot" => ScreenshotHotkeyRecorder,
            "QuickSearch" => QuickSearchHotkeyRecorder,
            _ => null,
        };
        if (target is null)
        {
            return;
        }
        target.Unbind();
        ShortcutEdited?.Invoke(this, System.EventArgs.Empty);
    }

    private void ShortcutFixButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingFix is not { } fix)
        {
            return;
        }
        var target = SlotRecorders().FirstOrDefault(pair => pair.SlotKey == fix.SlotKey).Recorder;
        if (target is null)
        {
            return;
        }
        target.BindingValue = fix.Suggested;
        SyncClearButtons();
        target.RaiseEvent(new RoutedEventArgs(HotkeyRecorder.RecordedEvent, target));
        ShortcutEdited?.Invoke(this, System.EventArgs.Empty);
    }
}
