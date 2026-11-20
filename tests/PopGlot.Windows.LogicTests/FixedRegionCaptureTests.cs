using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopGlot.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace PopGlot.Windows.LogicTests;

internal static class FixedRegionCaptureTests
{
    private const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected <{expected}>, got <{actual}>.");
        }
    }

    private static T Field<T>(object target, string name) where T : class =>
        (T)(target.GetType().GetField(name, NonPublicInstance)?.GetValue(target)
            ?? throw new InvalidOperationException($"Missing field {name} on {target.GetType().Name}."));

    private static void PumpDispatcher(TimeSpan duration)
    {
        var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    public static void FixedRegionDiagnosticsAndAltFreeSuggestions()
    {
        // 1. Alt-free candidate ranking for HotkeyAction.CaptureFixedRegion
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ShellSettings.Default.FixedRegionHotkey.DisplayName,
        };

        var expectedAltFreeSequence = new[]
        {
            "Ctrl+Shift+E",
            "Ctrl+Shift+2",
            "Ctrl+Shift+3",
            "F7",
            "F8",
            "Ctrl+Shift+B",
            "Ctrl+Shift+J",
            "Ctrl+Shift+K",
        };

        foreach (var expected in expectedAltFreeSequence)
        {
            var suggested = HotkeyService.FindFirstFreeAlternative(
                HotkeyAction.CaptureFixedRegion,
                ShellSettings.Default,
                probeAvailable: b => !blocked.Contains(b.DisplayName));
            True(suggested is not null, $"expected Alt-free fixed-region candidate {expected}");
            Equal(expected, suggested!.DisplayName, "fixed-region suggestions must rank Alt-free bindings first");
            Equal(0u, suggested.Modifiers & HotkeyBinding.ModAlt,
                $"suggested fixed-region binding {suggested.DisplayName} must not use Alt");
            blocked.Add(suggested.DisplayName);
        }

        var fallbackAfterAltFree = HotkeyService.FindFirstFreeAlternative(
            HotkeyAction.CaptureFixedRegion,
            ShellSettings.Default,
            probeAvailable: b => !blocked.Contains(b.DisplayName));
        Equal("Ctrl+Alt+G", fallbackAfterAltFree?.DisplayName,
            "Ctrl+Alt+G fallback should appear only after all Alt-free fixed-region candidates are taken");

        // 2. DiagnoseDraft occupation, duplicate, and preferredFixSlotKey on "FixedRegion"
        var occupiedIssues = HotkeyService.DiagnoseDraft(
            ShellSettings.Default,
            ownedByApp: Array.Empty<HotkeyBinding>(),
            probeSystem: true,
            probeAvailable: b => !string.Equals(b.DisplayName, HotkeyBinding.FixedRegionDefault.DisplayName, StringComparison.OrdinalIgnoreCase));
        Equal(1, occupiedIssues.Count, "occupied FixedRegionHotkey must produce one diagnostic issue");
        Equal("FixedRegion", occupiedIssues[0].SlotKey, "occupied slot key must be FixedRegion");
        Equal(ShortcutIssueKind.SystemOccupied, occupiedIssues[0].Kind, "occupied kind must be SystemOccupied");
        Equal("Ctrl+Shift+E", occupiedIssues[0].SuggestedBinding?.DisplayName,
            "occupied FixedRegionHotkey must suggest first Alt-free candidate Ctrl+Shift+E");

        var duplicateDraft = ShellSettings.Default with
        {
            FixedRegionHotkey = ShellSettings.Default.SelectionHotkey,
        };
        var duplicateIssues = HotkeyService.DiagnoseDraft(
            duplicateDraft,
            probeSystem: false,
            preferredFixSlotKey: "FixedRegion");
        True(duplicateIssues.Count >= 2, "duplicate FixedRegionHotkey must flag both colliding slots");
        Equal("FixedRegion", duplicateIssues[0].SlotKey,
            "preferredFixSlotKey='FixedRegion' must prioritize the FixedRegion slot first");
        Equal(ShortcutIssueKind.Duplicate, duplicateIssues[0].Kind,
            "prioritized colliding slot must be flagged as Duplicate");
    }

    public static void ShortcutsSectionFixedRegionUiAlignmentAndSettingsIntegration()
    {
        Program.EnsureApplication();

        var section = new ShortcutsSection();
        section.SelectionHotkey.BindingValue = HotkeyBinding.SelectionDefault;
        section.ScreenshotHotkey.BindingValue = HotkeyBinding.ScreenshotDefault;
        section.FixedRegionHotkey.BindingValue = HotkeyBinding.FixedRegionDefault;
        section.QuickSearchHotkey.BindingValue = HotkeyBinding.QuickSearchDefault;
        section.SyncClearButtons();

        // 1. Verify P0 #3 fix: all 4 clear buttons use closed filled IconClose (not stroked 0-area IconCaptionClose)
        var iconClose = Application.Current?.TryFindResource("IconClose") as Geometry;
        var iconCaptionClose = Application.Current?.TryFindResource("IconCaptionClose") as Geometry;
        True(iconClose is not null, "IconClose resource must exist");
        foreach (var btnName in new[] { "ClearSelectionButton", "ClearScreenshotButton", "ClearFixedRegionButton", "ClearQuickSearchButton" })
        {
            var btn = Field<Button>(section, btnName);
            var icon = Ui.GetIcon(btn);
            True(ReferenceEquals(icon, iconClose) && !ReferenceEquals(icon, iconCaptionClose),
                $"{btnName} must use filled IconClose rather than stroked IconCaptionClose");
        }

        // 2. Verify Unbind -> ClearFixedRegionButton becomes Hidden (preserving column alignment)
        var clearFixedBtn = Field<Button>(section, "ClearFixedRegionButton");
        Equal(Visibility.Visible, clearFixedBtn.Visibility, "bound FixedRegionHotkey must show ClearFixedRegionButton");
        clearFixedBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(section.FixedRegionHotkey.BindingValue.IsUnset, "clicking ClearFixedRegionButton must unbind FixedRegionHotkey");
        Equal(Visibility.Hidden, clearFixedBtn.Visibility, "unbound FixedRegionHotkey must keep ClearFixedRegionButton Hidden");
        Equal(false, clearFixedBtn.IsHitTestVisible, "Hidden ClearFixedRegionButton must not be hit-testable");

        section.ResetDefaults();
        Equal(HotkeyBinding.FixedRegionDefault, section.FixedRegionHotkey.BindingValue,
            "ResetDefaults must restore FixedRegionHotkey to Ctrl+Shift+G");
        Equal(Visibility.Visible, clearFixedBtn.Visibility, "ResetDefaults must restore ClearFixedRegionButton visibility");

        // 3. Verify FixedRegionCard states: unset, in-bounds saved, out-of-bounds warning, and events
        var summaryText = Field<TextBlock>(section, "FixedRegionSummaryText");
        var selectBtn = Field<Button>(section, "SelectFixedRegionButton");
        var previewBtn = Field<Button>(section, "PreviewFixedRegionButton");
        var clearPresetBtn = Field<Button>(section, "ClearFixedRegionPresetButton");
        var presetRow = Field<Grid>(section, "FixedRegionPresetRow");
        var presetCombo = Field<ComboBox>(section, "FixedRegionPresetCombo");
        var addPresetBtn = Field<Button>(section, "AddFixedRegionPresetButton");

        section.RefreshFixedRegionState(ShellSettings.Default, new Rect(0, 0, 1920, 1080));
        Equal("固定区域：未设置", summaryText.Text, "unset state must display 固定区域：未设置");
        Equal(false, previewBtn.IsEnabled, "unset state must disable Preview button");
        Equal(false, clearPresetBtn.IsEnabled, "unset state must disable Clear button");
        Equal(Visibility.Collapsed, presetRow.Visibility, "unset state must collapse preset row");

        var p1 = new FixedCaptureRegion("p1", "默认区域", 320, 780, 1280, 240, 0, 0, 1920, 1080);
        var p2 = new FixedCaptureRegion("p2", "副屏字幕", -1600, 700, 1000, 200, -1920, 0, 3840, 1080);
        var savedSettings = ShellSettings.Default with
        {
            FixedCaptureRegions = new FixedRegionPresetList([p1, p2]),
            ActiveFixedRegionId = "p1",
        };
        section.RefreshFixedRegionState(savedSettings, new Rect(0, 0, 1920, 1080));
        Equal("固定区域：1280×240 @ (320, 780)", summaryText.Text, "in-bounds region must show pixel summary");
        Equal(true, previewBtn.IsEnabled, "in-bounds region must enable Preview button");
        Equal(true, clearPresetBtn.IsEnabled, "saved region must enable Clear button");
        Equal(Visibility.Visible, presetRow.Visibility, "saved presets must show preset row");
        Equal(2, presetCombo.Items.Count, "preset combo must list all saved presets");

        // Out-of-bounds state when secondary monitor is disconnected
        var oobSettings = savedSettings with { ActiveFixedRegionId = "p2" };
        section.RefreshFixedRegionState(oobSettings, new Rect(0, 0, 1920, 1080));
        True(summaryText.Text.Contains("超出当前屏幕", StringComparison.Ordinal),
            $"out-of-bounds region must warn user in summary: {summaryText.Text}");
        Equal(false, previewBtn.IsEnabled, "out-of-bounds region must disable Preview button");
        Equal(true, clearPresetBtn.IsEnabled, "out-of-bounds region must still allow Clear");

        // Verify all 4 management events fire cleanly
        bool? selectCreateNew = null;
        var previewFired = false;
        var clearFired = false;
        string? switchedId = null;
        section.SelectFixedRegionRequested += (_, isNew) => selectCreateNew = isNew;
        section.PreviewFixedRegionRequested += (_, _) => previewFired = true;
        section.ClearFixedRegionRequested += (_, _) => clearFired = true;
        section.SwitchFixedRegionRequested += (_, id) => switchedId = id;

        section.RefreshFixedRegionState(savedSettings, new Rect(-1920, 0, 3840, 1080));
        selectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(false, selectCreateNew, "SelectFixedRegionButton must request createNew=false");
        addPresetBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(true, selectCreateNew, "AddFixedRegionPresetButton must request createNew=true");
        previewBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(previewFired, "PreviewFixedRegionButton must fire PreviewFixedRegionRequested");
        clearPresetBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(clearFired, "ClearFixedRegionPresetButton must fire ClearFixedRegionRequested");
        presetCombo.SelectedIndex = 1;
        Equal("p2", switchedId, "changing FixedRegionPresetCombo selection must fire SwitchFixedRegionRequested");

        // 4. SettingsWindow integration: Save_Click preserves disk FixedCaptureRegions while committing FixedRegionHotkey
        var dir = Path.Combine(TestIsolation.Root, $"fixed-region-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var originalStartupSeam = StartupRegistration.TrySetOverride;
        SettingsWindow? window = null;
        try
        {
            StartupRegistration.TrySetOverride = _ => true;
            CoreBridge.Initialize();
            ShellSettingsStore.Save(savedSettings);

            window = new SettingsWindow(savedSettings, new HistoryStore(Path.Combine(dir, "history.json")))
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                ApplyShellSettings = _ => ShellApplyOutcome.Ok(),
            };
            window.Show();
            window.ShowPage("Shortcuts");
            PumpDispatcher(TimeSpan.FromMilliseconds(40));
            window.UpdateLayout();

            True(window.SettingsScroll.ScrollableHeight < 1,
                $"Shortcuts page with saved FixedRegionCard must fit default viewport without scrolling (got ScrollableHeight={window.SettingsScroll.ScrollableHeight:F1})");
            Equal(SettingsEditState.Clean, window.EditState, "loaded SettingsWindow must start Clean");

            // Simulate external immediate save of a new active preset on disk while SettingsWindow is open
            var externalDiskUpdate = savedSettings with { ActiveFixedRegionId = "p2" };
            ShellSettingsStore.Save(externalDiskUpdate);

            // User edits FixedRegionHotkey in the form and saves
            var newHotkey = HotkeyBinding.Parse("Ctrl+Shift+E", HotkeyBinding.FixedRegionDefault);
            window.ShortcutsSection.FixedRegionHotkey.BindingValue = newHotkey;
            window.ShortcutsSection.FixedRegionHotkey.RaiseEvent(
                new RoutedEventArgs(HotkeyRecorder.RecordedEvent, window.ShortcutsSection.FixedRegionHotkey));
            Equal(SettingsEditState.Dirty, window.EditState, "editing FixedRegionHotkey must mark SettingsWindow Dirty");

            typeof(SettingsWindow).GetMethod("Save_Click", NonPublicInstance)!
                .Invoke(window, [window.SaveButton, new RoutedEventArgs()]);
            PumpDispatcher(TimeSpan.FromMilliseconds(120));

            var afterSave = ShellSettingsStore.Load();
            Equal(newHotkey, afterSave.FixedRegionHotkey, "Save_Click must persist new FixedRegionHotkey");
            Equal(2, afterSave.FixedCaptureRegions.Count, "Save_Click must preserve disk FixedCaptureRegions");
            Equal("p2", afterSave.ActiveFixedRegionId, "Save_Click must preserve latest disk ActiveFixedRegionId");
        }
        finally
        {
            StartupRegistration.TrySetOverride = originalStartupSeam;
            if (window is not null)
            {
                window.ForceClose = true;
                window.Close();
            }
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    public static void CaptureOverlayFixedRegionModeAndSaveToggle()
    {
        Program.EnsureApplication();

        var bitmap = new Drawing.Bitmap(64, 48, DrawingImaging.PixelFormat.Format32bppPArgb);
        var preview = new WriteableBitmap(64, 48, 96, 96, PixelFormats.Pbgra32, null);
        preview.Freeze();
        var snapshot = new FrozenDesktopSnapshot(new Rect(0, 0, 64, 48), bitmap, preview);

        var overlay = new CaptureOverlayWindow(snapshot);
        try
        {
            // 1. Normal ScreenshotTranslate mode: Key.F toggles IsSaveAsFixedRegionToggled
            Equal(CaptureOverlayMode.ScreenshotTranslate, overlay.Mode, "default overlay mode must be ScreenshotTranslate");
            Equal(false, overlay.IsSaveAsFixedRegionToggled, "save-as-fixed toggle must start OFF");
            var saveFixedGroup = Field<StackPanel>(overlay, "SaveFixedHintGroup");
            var saveFixedHintText = Field<TextBlock>(overlay, "SaveFixedHintText");
            Equal(Visibility.Visible, saveFixedGroup.Visibility, "ScreenshotTranslate mode must show SaveFixedHintGroup");

            using var source = new System.Windows.Interop.HwndSource(
                new System.Windows.Interop.HwndSourceParameters("overlay_key_test") { Width = 1, Height = 1 });
            var keyFDown = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F)
            {
                RoutedEvent = UIElement.KeyDownEvent,
            };
            overlay.RaiseEvent(keyFDown);
            Equal(true, overlay.IsSaveAsFixedRegionToggled, "pressing F in ScreenshotTranslate mode must toggle save-as-fixed ON");
            Equal("将存为固定区", saveFixedHintText.Text, "toggling F ON must update hint chip text to 将存为固定区");

            var keyFDownAgain = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F)
            {
                RoutedEvent = UIElement.KeyDownEvent,
            };
            overlay.RaiseEvent(keyFDownAgain);
            Equal(false, overlay.IsSaveAsFixedRegionToggled, "pressing F again must toggle save-as-fixed OFF");
            Equal("存为固定区", saveFixedHintText.Text, "toggling F OFF must restore hint chip text");

            // 2. SelectFixedRegion mode: configures hint, collapses Shift/SaveFixed hint groups, draws PreviousRegionOutline
            var prevRect = new Rect(100, 120, 400, 140);
            overlay.ConfigureMode(
                CaptureOverlayMode.SelectFixedRegion,
                previousFixedRegionPixels: prevRect,
                customHint: FixedRegionResolver.OutOfBoundsStatusHint,
                skipPngEncoding: true);
            Equal(CaptureOverlayMode.SelectFixedRegion, overlay.Mode, "overlay mode must switch to SelectFixedRegion");
            var hintTitle = Field<TextBlock>(overlay, "HintTitle");
            var shiftGroup = Field<StackPanel>(overlay, "ShiftHintGroup");
            Equal(FixedRegionResolver.OutOfBoundsStatusHint, hintTitle.Text, "custom out-of-bounds hint must populate HintTitle");
            Equal(Visibility.Collapsed, shiftGroup.Visibility, "SelectFixedRegion mode must hide ShiftHintGroup");
            Equal(Visibility.Collapsed, saveFixedGroup.Visibility, "SelectFixedRegion mode must hide SaveFixedHintGroup");

            overlay.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(30));
            True(overlay.HasPreviousRegionOutline, "SelectFixedRegion mode with previous rect must display PreviousRegionOutline");
        }
        finally
        {
            overlay.Close();
        }

        var disposed = false;
        try
        {
            _ = snapshot.Bitmap;
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }
        True(disposed, "closing CaptureOverlayWindow must dispose FrozenDesktopSnapshot");
    }

    public static void RegionHighlightWindowNonActivatingAndClickThrough()
    {
        Program.EnsureApplication();

        var highlight = new RegionHighlightWindow();
        try
        {
            Equal(false, highlight.ShowActivated, "RegionHighlightWindow must set ShowActivated=False");
            Equal(false, highlight.IsHitTestVisible, "RegionHighlightWindow must set IsHitTestVisible=False");
            Equal(false, highlight.Focusable, "RegionHighlightWindow must set Focusable=False");
            Equal(false, highlight.ShowInTaskbar, "RegionHighlightWindow must set ShowInTaskbar=False");
            Equal(true, highlight.Topmost, "RegionHighlightWindow must set Topmost=True");

            var targetRect = new Rect(120, 160, 480, 140);
            highlight.ShowPreview(targetRect, "默认区域 · 480×140 px", holdDuration: TimeSpan.FromSeconds(5));
            PumpDispatcher(TimeSpan.FromMilliseconds(40));

            Equal(targetRect, highlight.TargetPixelRect, "TargetPixelRect must record the highlighted screen region");
            var previewBadge = Field<Border>(highlight, "PreviewBadge");
            var previewText = Field<TextBlock>(highlight, "PreviewLabelText");
            Equal(Visibility.Visible, previewBadge.Visibility, "ShowPreview must display PreviewBadge");
            Equal("默认区域 · 480×140 px", previewText.Text, "PreviewLabelText must match preview label");

            var exStyle = RegionHighlightWindow.ReadExtendedWindowStyle(highlight);
            True((exStyle & RegionHighlightWindow.WsExNoActivate) != 0,
                $"RegionHighlightWindow HWND must carry WS_EX_NOACTIVATE (0x{exStyle:X})");
            True((exStyle & RegionHighlightWindow.WsExTransparent) != 0,
                $"RegionHighlightWindow HWND must carry WS_EX_TRANSPARENT for click-through (0x{exStyle:X})");
            True((exStyle & RegionHighlightWindow.WsExToolWindow) != 0,
                $"RegionHighlightWindow HWND must carry WS_EX_TOOLWINDOW (0x{exStyle:X})");

            highlight.DismissImmediately();
            True(highlight.IsDismissed, "DismissImmediately must mark RegionHighlightWindow dismissed and close it");
        }
        finally
        {
            highlight.DismissImmediately();
        }
    }

    public static async Task TranslationPanelFixedRegionSessionAndOutboundPolicyAsync()
    {
        Program.EnsureApplication();

        var dir = Path.Combine(TestIsolation.Root, $"fixed-region-panel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var history = new HistoryStore(Path.Combine(dir, "history.json"));
        var vocab = new VocabularyStore(Path.Combine(dir, "vocab.json"));

        var region = new FixedCaptureRegion("reg1", "对话框", 320, 780, 1280, 220, 0, 0, 1920, 1080);
        var fakePng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };

        // 1. Offline SafeDevMode + LocalOcr policy enforcement: zero network send, honest failure when offline
        var executor = new FixedRegionPolicyStubExecutor();
        var coordinator = new TranslationCoordinator(history, vocab, executor);

        var panel = new TranslationPanelWindow(
            region.PixelRect,
            history,
            () => ShellSettings.Default,
            coordinator: coordinator);
        try
        {
            var recaptureCount = 0;
            var reselectCount = 0;
            panel.RecaptureFixedRegionRequested = () => recaptureCount++;
            panel.ReselectFixedRegionRequested = () => reselectCount++;

            // StartFixedRegionScreenshotAsync with stealFocus: false must NOT arm _readyForKeyboard (no focus steal!)
            executor.Settings = executor.Settings with
            {
                SafeDevMode = false,
                NetworkEnabled = true,
                Mode = TranslationMode.LocalOcr,
            };
            await panel.StartFixedRegionScreenshotAsync(
                fakePng,
                region,
                wasClamped: true,
                stealFocus: false);

            True(panel.IsFixedRegionSession, "panel must enter fixed-region session");
            Equal(false, panel.IsReadyForKeyboard, "stealFocus:false must not call Activate/Focus or latch keyboard focus");

            var fixedBar = Field<Border>(panel, "FixedRegionBar");
            var fixedMeta = Field<TextBlock>(panel, "FixedRegionMetaText");
            var recaptureBtn = Field<Button>(panel, "RecaptureFixedRegionButton");
            var reselectBtn = Field<Button>(panel, "ReselectFixedRegionButton");
            Equal(Visibility.Visible, fixedBar.Visibility, "FixedRegionBar must be visible during fixed-region session");
            True(fixedMeta.Text.Contains("对话框", StringComparison.Ordinal) &&
                 fixedMeta.Text.Contains("1280×220", StringComparison.Ordinal) &&
                 fixedMeta.Text.Contains(FixedRegionResolver.ClampedStatusHint, StringComparison.Ordinal),
                $"FixedRegionMetaText must include region name, size, and clamp notice: {fixedMeta.Text}");

            recaptureBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, recaptureCount, "clicking 重译此区 must invoke RecaptureFixedRegionRequested");
            reselectBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, reselectCount, "clicking 重选区域 must invoke ReselectFixedRegionRequested");

            Equal(1, executor.OcrCallCount, "LocalOcr mode must run local OCR");
            Equal(1, executor.TextStreamCallCount, "LocalOcr mode must stream recognized text through text route");
            Equal(0, executor.VisionStreamCallCount, "LocalOcr mode must never upload image to vision stream");

            // Image-free StoredSession snapshot contract (G05/V2)
            var snapshot = panel.CreateSessionSnapshot();
            True(snapshot is not null, "completed fixed-region session must produce StoredSession snapshot");
            Equal(SessionOrigin.ScreenshotVision, snapshot!.Origin, "fixed-region snapshot must map to ScreenshotVision");
            Equal("勇者：前方就是魔王城！", snapshot.SourceText, "snapshot must retain recognized OCR source text");

            // P0 #2 verification: MergeLines_Click hint must name InterfaceKeys.TranslateKeyName (e.g. Ctrl+Enter), not plain Enter
            var sourceBox = Field<TextBox>(panel, "SourceInputBox");
            sourceBox.Text = "第一行\n第二行";
            typeof(TranslationPanelWindow).GetMethod("MergeLines_Click", NonPublicInstance)!
                .Invoke(panel, [panel, new RoutedEventArgs()]);
            True(panel.StatusTextBlock.Text.Contains(InterfaceKeys.TranslateKeyName, StringComparison.Ordinal),
                $"MergeLines_Click status hint must name {InterfaceKeys.TranslateKeyName}, got: {panel.StatusTextBlock.Text}");

            // 2. SafeDevMode = true (offline security gate) blocks remote text/vision execution
            executor.ResetCounts();
            executor.Settings = executor.Settings with
            {
                SafeDevMode = true,
                NetworkEnabled = true,
                Mode = TranslationMode.VisionDirect,
            };
            await panel.StartFixedRegionScreenshotAsync(fakePng, region, wasClamped: false, stealFocus: false);
            Equal(0, executor.VisionStreamCallCount, "SafeDevMode=true must block remote VisionDirect upload");
            Equal(0, executor.TextStreamCallCount, "SafeDevMode=true must block remote text stream");
            Equal(TranslationSessionState.Failed, panel.CurrentSessionState, "blocked offline fixed-region capture must report Failed state");
        }
        finally
        {
            panel.ForceClose = true;
            panel.Close();
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    public static void FixedRegionLiveBitBltEndToEndRegression()
    {
        Program.EnsureApplication();

        var virtualBounds = ScreenGeometry.VirtualScreenPixels();
        var winLeft = virtualBounds.X + 80;
        var winTop = virtualBounds.Y + 80;
        const int winWidth = 240;
        const int winHeight = 120;
        var targetRect = new Rect(winLeft, winTop, winWidth, winHeight);

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = winLeft,
            Top = winTop,
            Width = winWidth,
            Height = winHeight,
            Background = new SolidColorBrush(Color.FromRgb(24, 160, 88)), // deterministic emerald green
        };
        window.SourceInitialized += (_, _) => ScreenGeometry.ResizeToPixels(window, targetRect);

        try
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(350));
            window.UpdateLayout();

            True(ScreenGeometry.TryGetWindowRectPixels(window, out var hwndRect),
                "ScreenGeometry.TryGetWindowRectPixels must read visible window rect");
            True(Math.Abs(hwndRect.X - targetRect.X) <= 2 &&
                 Math.Abs(hwndRect.Y - targetRect.Y) <= 2 &&
                 Math.Abs(hwndRect.Width - targetRect.Width) <= 2 &&
                 Math.Abs(hwndRect.Height - targetRect.Height) <= 2,
                $"TryGetWindowRectPixels must match ResizeToPixels rect ({targetRect}), got {hwndRect}");

            var savedRegion = FixedRegionResolver.CreateFromSelection(
                new Rect(hwndRect.X + 20, hwndRect.Y + 20, 120, 60),
                virtualBounds,
                id: "live-e2e",
                name: "测试固定区");
            var resolved = FixedRegionResolver.Resolve(savedRegion, virtualBounds);
            Equal(FixedRegionResolutionStatus.Exact, resolved.Status, "live saved region must resolve as Exact");

            var png = ScreenCaptureService.CapturePng(resolved.PixelRect);
            using var ms = new MemoryStream(png);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Equal(120, frame.PixelWidth, "captured fixed-region PNG width must match resolved PixelRect 1:1");
            Equal(60, frame.PixelHeight, "captured fixed-region PNG height must match resolved PixelRect 1:1");

            var pixel = new byte[4]; // BGRA
            frame.CopyPixels(new Int32Rect(60, 30, 1, 1), pixel, 4, 0);
            True(Math.Abs(pixel[0] - 88) <= 12 &&
                 Math.Abs(pixel[1] - 160) <= 12 &&
                 Math.Abs(pixel[2] - 24) <= 12,
                $"captured fixed-region pixel must match controlled window color (R=24,G=160,B=88), got R={pixel[2]},G={pixel[1]},B={pixel[0]}");
        }
        finally
        {
            window.Close();
        }
    }

    public static void FixedRegionSnapshotCropAndPixelReadback()
    {
        Program.EnsureApplication();

        // Simulate a 2-monitor virtual desktop with negative left monitor (-1920, 0, 3840x1080)
        // painted onto a deterministic snapshot around (-200, 100, 400x300).
        var virtualBounds = new Rect(-200, 100, 400, 300);
        var bitmap = new Drawing.Bitmap(400, 300, DrawingImaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.FromArgb(255, 30, 30, 40));
            using var emerald = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 24, 160, 88));
            g.FillRectangle(emerald, 100, 80, 160, 90);
        }

        var preview = new WriteableBitmap(400, 300, 96, 96, PixelFormats.Pbgra32, null);
        preview.Freeze();
        using var snapshot = new FrozenDesktopSnapshot(virtualBounds, bitmap, preview);

        var selected = FixedRegionResolver.CreateFromSelection(
            new Rect(-100, 180, 160, 90),
            virtualBounds,
            id: "snap-e2e",
            name: "字幕区");
        var resolved = FixedRegionResolver.Resolve(selected, virtualBounds);
        Equal(FixedRegionResolutionStatus.Exact, resolved.Status, "snapshot region must resolve Exact");

        var png = ScreenCaptureService.CropPng(snapshot, resolved.PixelRect);
        using var ms = new MemoryStream(png);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Equal(160, frame.PixelWidth, "cropped fixed-region width must match 1:1");
        Equal(90, frame.PixelHeight, "cropped fixed-region height must match 1:1");

        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(80, 45, 1, 1), pixel, 4, 0);
        Equal((byte)88, pixel[0], "cropped pixel blue channel must match emerald region");
        Equal((byte)160, pixel[1], "cropped pixel green channel must match emerald region");
        Equal((byte)24, pixel[2], "cropped pixel red channel must match emerald region");
    }

    private sealed class FixedRegionPolicyStubExecutor : ITranslationExecutor
    {
        public ProviderSettings Settings { get; set; } = new(
            SchemaVersion: 1,
            ProviderType: ProviderType.OpenAiCompatible,
            ApiBaseUrl: "https://api.example.com/v1",
            TextEndpoint: "/chat/completions",
            VisionEndpoint: "/chat/completions",
            TextModel: "stub-text-model",
            VisionModel: "stub-vision-model",
            ExtraHeaders: new Dictionary<string, string>(),
            AnthropicVersion: "2023-06-01",
            SupportsText: true,
            SupportsVision: true,
            NetworkEnabled: true,
            Mode: TranslationMode.LocalOcr,
            AllowImageUploadInAuto: false,
            SafeDevMode: false,
            AllowLanEndpoints: false,
            AllowInsecureTls: false,
            ApiKeyConfigured: true,
            SourceLanguage: "ja",
            TargetLanguage: "zh-CN",
            IncludeExplanation: false,
            ProtectCodeTokens: false);

        public int OcrCallCount { get; private set; }
        public int TextStreamCallCount { get; private set; }
        public int VisionStreamCallCount { get; private set; }

        public void ResetCounts()
        {
            OcrCallCount = 0;
            TextStreamCallCount = 0;
            VisionStreamCallCount = 0;
        }

        public ProviderSettings GetSettings() => Settings;

        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes()
        {
            var textProfile = new ProviderProfile
            {
                Id = "stub-text",
                Name = "Stub Text",
                ProviderType = ProviderType.OpenAiCompatible,
                ApiBaseUrl = Settings.ApiBaseUrl,
                TextEndpoint = "/chat/completions",
                VisionEndpoint = "/chat/completions",
                TextModel = Settings.TextModel,
                VisionModel = Settings.VisionModel,
                SupportsText = true,
                SupportsVision = true,
                CredentialTarget = "stub-cred",
                IsLocal = false,
            };
            var visionProfile = new ProviderProfile
            {
                Id = "stub-vision",
                Name = "Stub Vision",
                ProviderType = ProviderType.OpenAiCompatible,
                ApiBaseUrl = Settings.ApiBaseUrl,
                TextEndpoint = "/chat/completions",
                VisionEndpoint = "/chat/completions",
                TextModel = Settings.TextModel,
                VisionModel = Settings.VisionModel,
                SupportsText = true,
                SupportsVision = true,
                CredentialTarget = "stub-cred",
                IsLocal = false,
            };
            return (new ProviderRoute(textProfile, "stub-cred"), new ProviderRoute(visionProfile, "stub-cred"));
        }

        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable)
        {
            if (settings.SafeDevMode || !settings.NetworkEnabled)
            {
                return new ResolvedRoute(
                    Text: null,
                    Vision: null,
                    ScreenshotPipeline.Unavailable,
                    MayUploadImage: false,
                    "安全离线模式或网络关闭已阻止外部连接");
            }
            var (text, vision) = ResolveRoutes();
            if (settings.Mode == TranslationMode.VisionDirect)
            {
                return new ResolvedRoute(
                    text,
                    vision,
                    ScreenshotPipeline.VisionDirect,
                    MayUploadImage: true,
                    "视觉直译");
            }
            return new ResolvedRoute(
                text,
                null,
                ScreenshotPipeline.LocalOcr,
                MayUploadImage: false,
                "本地 OCR + 文本翻译");
        }

        public string? LoadApiKey(string target) => "stub-api-key";

        public bool IsOcrSupported => true;

        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default)
        {
            OcrCallCount++;
            return Task.FromResult("勇者：前方就是魔王城！");
        }

        public TranslationStreamSession StreamText(
            string? apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken) =>
            CreateCompletedStream(source, isVision: false, sessionId, epoch);

        public TranslationStreamSession StreamTextDraft(
            ProviderSettings draftSettings,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken) =>
            CreateCompletedStream(source, isVision: false, sessionId, epoch);

        public TranslationStreamSession StreamVisionDraft(
            ProviderSettings draftSettings,
            string textApiKey,
            string visionApiKey,
            byte[] image,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken) =>
            CreateCompletedStream("（视觉直译）", isVision: true, sessionId, epoch);

        public Task<TranslationResponse> TranslateFreeAsync(
            string source,
            string sourceLang,
            string targetLang,
            FreeEngineAuthorization authorization,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Stub executor does not use free engine.");

        private TranslationStreamSession CreateCompletedStream(string source, bool isVision, string sessionId, long epoch)
        {
            if (isVision)
            {
                VisionStreamCallCount++;
            }
            else
            {
                TextStreamCallCount++;
            }
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            const string translated = "Hero: The Demon King's castle lies ahead!";
            buffer.TryAppend(translated);
            buffer.Complete();
            var response = new TranslationResponse(
                new TranslationResult(
                    translated,
                    string.Empty,
                    source,
                    [],
                    []),
                new ProviderDiagnostics(
                    sessionId,
                    ProviderType.OpenAiCompatible,
                    Settings.ApiBaseUrl,
                    1,
                    200,
                    25));
            return new TranslationStreamSession(buffer, Task.FromResult(response));
        }
    }
}
