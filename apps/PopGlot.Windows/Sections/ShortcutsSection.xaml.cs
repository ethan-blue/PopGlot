
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class ShortcutsSection : System.Windows.Controls.UserControl
{
    private (string SlotKey, HotkeyBinding Suggested)? _pendingFix;
    private string? _lastEditedSlotKey;
    private bool _syncingFixedRegionCombo;

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
        slotKey is "Selection" or "Screenshot" or "FixedRegion" or "QuickSearch";

    internal void SetCompact(bool stacked)
    {
        ShortcutGutter.Width = new System.Windows.GridLength(stacked ? 0 : 24);
        ShortcutRightColumn.Width = stacked
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        System.Windows.Controls.Grid.SetColumn(InterfaceShortcutGroup, stacked ? 0 : 2);
        System.Windows.Controls.Grid.SetRow(InterfaceShortcutGroup, stacked ? 1 : 0);
        InterfaceShortcutGroup.Margin = new System.Windows.Thickness(0, stacked ? 12 : 0, 0, 0);

        FixedRegionActionsColumn.Width = stacked
            ? new System.Windows.GridLength(0)
            : System.Windows.GridLength.Auto;
        System.Windows.Controls.Grid.SetRow(FixedRegionActionsPanel, stacked ? 1 : 0);
        System.Windows.Controls.Grid.SetColumn(FixedRegionActionsPanel, stacked ? 0 : 1);
        FixedRegionActionsPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FixedRegionActionsPanel.Margin = new System.Windows.Thickness(0, stacked ? 6 : 0, 0, 0);
    }
    internal System.Windows.Controls.Expander MoreExpander => MoreShortcutsExpander;

    // ================= Public accessors for MainWindow =================

    internal HotkeyRecorder SelectionHotkey => SelectionHotkeyRecorder;
    internal HotkeyRecorder ScreenshotHotkey => ScreenshotHotkeyRecorder;
    internal HotkeyRecorder FixedRegionHotkey => FixedRegionHotkeyRecorder;
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
    internal event System.EventHandler<bool>? SelectFixedRegionRequested;
    internal event System.EventHandler? PreviewFixedRegionRequested;
    internal event System.EventHandler? ClearFixedRegionRequested;
    internal event System.EventHandler<string>? SwitchFixedRegionRequested;

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
        Sync(ClearFixedRegionButton, FixedRegionHotkey);
        Sync(ClearQuickSearchButton, QuickSearchHotkey);
    }

    internal void RefreshFixedRegionState(ShellSettings settings, Rect? virtualBoundsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var regions = settings.FixedCaptureRegions;
        var active = settings.ActiveFixedRegion;

        if (active is null)
        {
            FixedRegionStatusDot.SetResourceReference(Border.BackgroundProperty, "TextTertiaryBrush");
            FixedRegionSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            FixedRegionSummaryText.Text = "固定区域：未设置";
            PreviewFixedRegionButton.IsEnabled = false;
            PreviewFixedRegionButton.ToolTip = "尚未设置固定区域";
            ClearFixedRegionPresetButton.IsEnabled = false;
            FixedRegionPresetRow.Visibility = Visibility.Collapsed;
            _syncingFixedRegionCombo = true;
            try
            {
                FixedRegionPresetCombo.Items.Clear();
            }
            finally
            {
                _syncingFixedRegionCombo = false;
            }
            return;
        }

        var virtualBounds = virtualBoundsOverride ?? ScreenGeometry.VirtualScreenPixels();
        var resolution = FixedRegionResolver.Resolve(active, virtualBounds);
        var boundsSummary = active.FormatBoundsSummary();

        switch (resolution.Status)
        {
            case FixedRegionResolutionStatus.Exact:
                FixedRegionStatusDot.SetResourceReference(Border.BackgroundProperty, "SuccessBrush");
                FixedRegionSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                FixedRegionSummaryText.Text = $"固定区域：{boundsSummary}";
                PreviewFixedRegionButton.IsEnabled = true;
                PreviewFixedRegionButton.ToolTip = "在屏幕上高亮显示当前固定区域位置";
                break;

            case FixedRegionResolutionStatus.Clamped:
                FixedRegionStatusDot.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
                FixedRegionSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                FixedRegionSummaryText.Text = $"固定区域：{boundsSummary}（将适配当前屏幕）";
                PreviewFixedRegionButton.IsEnabled = true;
                PreviewFixedRegionButton.ToolTip = "在屏幕上预览适配后的固定区域位置";
                break;

            default:
                FixedRegionStatusDot.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
                FixedRegionSummaryText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                FixedRegionSummaryText.Text = $"固定区域：{boundsSummary}（超出当前屏幕，请重选）";
                PreviewFixedRegionButton.IsEnabled = false;
                PreviewFixedRegionButton.ToolTip = "当前区域超出屏幕范围，请重新框选";
                break;
        }

        ClearFixedRegionPresetButton.IsEnabled = true;
        FixedRegionPresetRow.Visibility = Visibility.Visible;

        _syncingFixedRegionCombo = true;
        try
        {
            FixedRegionPresetCombo.Items.Clear();
            ComboBoxItem? activeItem = null;
            for (var i = 0; i < regions.Count; i++)
            {
                var preset = regions[i];
                var item = new ComboBoxItem
                {
                    Content = $"{preset.Name} ({preset.FormatCompactDimensions()})",
                    Tag = preset.Id,
                };
                FixedRegionPresetCombo.Items.Add(item);
                if (string.Equals(preset.Id, active.Id, StringComparison.OrdinalIgnoreCase))
                {
                    activeItem = item;
                }
            }
            FixedRegionPresetCombo.SelectedItem = activeItem ?? (FixedRegionPresetCombo.Items.Count > 0 ? FixedRegionPresetCombo.Items[0] : null);
        }
        finally
        {
            _syncingFixedRegionCombo = false;
        }

        var canAddMore = regions.Count < FixedCaptureRegion.MaxPresetCount;
        AddFixedRegionPresetButton.IsEnabled = canAddMore;
        AddFixedRegionPresetButton.ToolTip = canAddMore
            ? $"新增一个固定区域预设（已保存 {regions.Count}/{FixedCaptureRegion.MaxPresetCount}）"
            : $"已达预设数量上限（最多 {FixedCaptureRegion.MaxPresetCount} 个）";
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
            ? $"{primary.Message}（另有 {issues.Count - 1} 项冲突，悬停高亮项可查看）"
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
        yield return ("FixedRegion", FixedRegionHotkeyRecorder);
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
        FixedRegionHotkey.BindingValue = HotkeyBinding.FixedRegionDefault;
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

    private void SelectFixedRegionButton_Click(object sender, RoutedEventArgs e) =>
        SelectFixedRegionRequested?.Invoke(this, false);

    private void AddFixedRegionPresetButton_Click(object sender, RoutedEventArgs e) =>
        SelectFixedRegionRequested?.Invoke(this, true);

    private void PreviewFixedRegionButton_Click(object sender, RoutedEventArgs e) =>
        PreviewFixedRegionRequested?.Invoke(this, System.EventArgs.Empty);

    private void ClearFixedRegionPresetButton_Click(object sender, RoutedEventArgs e) =>
        ClearFixedRegionRequested?.Invoke(this, System.EventArgs.Empty);

    private void FixedRegionPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFixedRegionCombo)
        {
            return;
        }
        if (FixedRegionPresetCombo.SelectedItem is ComboBoxItem { Tag: string regionId } &&
            !string.IsNullOrWhiteSpace(regionId))
        {
            SwitchFixedRegionRequested?.Invoke(this, regionId);
        }
    }

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
            "FixedRegion" => FixedRegionHotkeyRecorder,
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
