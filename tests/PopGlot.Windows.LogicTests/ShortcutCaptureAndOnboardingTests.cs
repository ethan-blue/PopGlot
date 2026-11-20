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

internal static class ShortcutCaptureAndOnboardingTests
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

    public static void ValidateAndClampRegionMultiMonitorAndBounds()
    {
        // Multi-monitor virtual desktop spanning left (-1920) and above (-120) primary monitor.
        var virtualBounds = new Rect(-1920, -120, 3840, 1200);

        // Region completely inside negative monitor translates into bitmap-local offset.
        var (ox, oy, w, h) = ScreenCaptureService.ValidateAndClampRegion(
            new Rect(-1800, -100, 240, 180),
            virtualBounds);
        Equal(120, ox, "negative X must translate relative to virtual left");
        Equal(20, oy, "negative Y must translate relative to virtual top");
        Equal(240, w, "width inside bounds must stay intact");
        Equal(180, h, "height inside bounds must stay intact");

        // Region overlapping left/top edge clamps cleanly without losing valid interior area.
        var (clampedX, clampedY, clampedW, clampedH) = ScreenCaptureService.ValidateAndClampRegion(
            new Rect(-1950, -150, 100, 90),
            virtualBounds);
        Equal(0, clampedX, "clamped left edge must align to snapshot offset 0");
        Equal(0, clampedY, "clamped top edge must align to snapshot offset 0");
        Equal(70, clampedW, "clamped width must keep only interior pixels");
        Equal(60, clampedH, "clamped height must keep only interior pixels");

        // Completely outside or tiny after clamping must throw explicit InvalidOperationException.
        var threwTiny = false;
        try
        {
            ScreenCaptureService.ValidateAndClampRegion(new Rect(-1925, 0, 8, 50), virtualBounds);
        }
        catch (InvalidOperationException ex)
        {
            threwTiny = ex.Message.Contains("选区太小", StringComparison.Ordinal);
        }
        True(threwTiny, "region clamped below MinimumSide must throw tiny-selection error");

        var threwLarge = false;
        try
        {
            ScreenCaptureService.ValidateAndClampRegion(
                new Rect(0, 0, 5000, 5000),
                new Rect(0, 0, 6000, 6000));
        }
        catch (InvalidOperationException ex)
        {
            threwLarge = ex.Message.Contains("1600 万像素", StringComparison.Ordinal);
        }
        True(threwLarge, "region over 16MP must throw max-pixels error");
    }

    public static async Task FrozenSnapshotCropPngAndOverlayDisposalAsync()
    {
        // Simulate a frozen virtual desktop with negative origin (-100, -50), size 120x80.
        // Paint left half crimson, right half teal so crop coordinates are pixel-verifiable.
        var bitmap = new Drawing.Bitmap(120, 80, DrawingImaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.FromArgb(255, 220, 40, 40));
            using var tealBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 20, 180, 170));
            g.FillRectangle(tealBrush, 60, 0, 60, 80);
        }

        var preview = new WriteableBitmap(120, 80, 96, 96, PixelFormats.Pbgra32, null);
        preview.Freeze();
        var snapshot = new FrozenDesktopSnapshot(new Rect(-100, -50, 120, 80), bitmap, preview);

        // Crop a 24x18 rectangle entirely inside the teal right half: [-30, -30, 24, 18]
        // maps to bitmap local (70, 20, 24, 18).
        var pngBytes = await ScreenCaptureService.CropPngAsync(snapshot, new Rect(-30, -30, 24, 18));
        True(pngBytes.Length > 8, "cropped PNG must be non-empty");

        using var ms = new MemoryStream(pngBytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        Equal(24, frame.PixelWidth, "cropped PNG width must match selection");
        Equal(18, frame.PixelHeight, "cropped PNG height must match selection");

        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(5, 5, 1, 1), pixel, 4, 0); // BGRA
        Equal((byte)170, pixel[0], "cropped pixel blue channel must match teal region");
        Equal((byte)180, pixel[1], "cropped pixel green channel must match teal region");
        Equal((byte)20, pixel[2], "cropped pixel red channel must match teal region");

        // Wiring a CaptureOverlayWindow with a frozen snapshot populates FrozenDesktopImage
        // and disposes the snapshot bitmap when the overlay closes.
        var overlay = new CaptureOverlayWindow(snapshot);
        var previewImage = Field<System.Windows.Controls.Image>(overlay, "FrozenDesktopImage");
        True(ReferenceEquals(preview, previewImage.Source), "overlay must display frozen snapshot beneath the scrim");
        overlay.Close();

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

    /// <summary>
    /// Real-desktop capture regression. 0.1.39/0.1.40 shipped
    /// <c>Graphics.CopyFromScreen(..., SourceCopy | CaptureBlt)</c>: .NET
    /// Core / .NET 5+ validates the <c>CopyPixelOperation</c> argument and
    /// rejects the combination (the enum is not [Flags]), so
    /// <see cref="ScreenCaptureService.TryCaptureVirtualDesktop"/> always
    /// returned null and <c>CapturePng</c> always threw — every screenshot
    /// failed. Both paths now blit through raw GDI with SRCCOPY|CAPTUREBLT.
    /// The test never captures uncontrolled desktop content: a borderless
    /// topmost window with a deterministic black-on-white pattern first
    /// covers the whole virtual desktop (coverage re-verified from the HWND
    /// rect before capturing), and the assertions then read back those
    /// known pixels — geometry and pixels, not just a PNG signature. The
    /// snapshot is disposed by <c>using</c> even when an assertion throws.
    /// </summary>
    public static void RealDesktopCaptureRegression()
    {
        var bounds = ScreenGeometry.VirtualScreenPixels();
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = bounds.X,
            Top = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Background = System.Windows.Media.Brushes.White,
        };
        // bounds are PHYSICAL pixels; at non-100% DPI the WPF DIP assignment
        // above would size the window wrong, so the HWND is re-stated in
        // physical pixels once the handle exists — the same discipline the
        // capture overlay uses.
        window.SourceInitialized += (_, _) => ScreenGeometry.ResizeToPixels(window, bounds);
        // Left quarter filled black at a known fractional position; the marker
        // is (re)placed in the window's LOCAL DIP space so the fractions hold
        // at every DPI. The exact sample points are recomputed below from the
        // same fractions of the captured bitmap.
        const double MarkerLeftFraction = 0.02, MarkerTopFraction = 0.05;
        const double MarkerWidthFraction = 0.20, MarkerHeightFraction = 0.12;
        var canvas = new System.Windows.Controls.Canvas();
        var marker = new System.Windows.Shapes.Rectangle
        {
            Fill = System.Windows.Media.Brushes.Black,
        };
        canvas.Children.Add(marker);
        canvas.SizeChanged += (_, _) =>
        {
            marker.Width = canvas.ActualWidth * MarkerWidthFraction;
            marker.Height = canvas.ActualHeight * MarkerHeightFraction;
            System.Windows.Controls.Canvas.SetLeft(marker, canvas.ActualWidth * MarkerLeftFraction);
            System.Windows.Controls.Canvas.SetTop(marker, canvas.ActualHeight * MarkerTopFraction);
        };
        window.Content = canvas;
        try
        {
            window.Show();
            PumpDispatcher(TimeSpan.FromMilliseconds(600));

            // Coverage confirmation before any capture: the window's rendered
            // corners must span the virtual desktop (rounding allows 2 px).
            window.UpdateLayout();
            var placedTopLeft = window.PointToScreen(new Point(0, 0));
            var placedBottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            True(Math.Abs(placedTopLeft.X - bounds.X) <= 2 && Math.Abs(placedTopLeft.Y - bounds.Y) <= 2 &&
                Math.Abs(placedBottomRight.X - (bounds.X + bounds.Width)) <= 2 &&
                Math.Abs(placedBottomRight.Y - (bounds.Y + bounds.Height)) <= 2,
                $"controlled window must cover the virtual desktop {bounds}, got " +
                $"{placedTopLeft}..{placedBottomRight}");

            // `using` before any assertion: a failed assert still disposes.
            using var snapshot = ScreenCaptureService.TryCaptureVirtualDesktop();
            True(snapshot is not null,
                "TryCaptureVirtualDesktop must capture the live virtual desktop through the BitBlt SRCCOPY|CAPTUREBLT path");
            if (snapshot is null)
            {
                return;
            }
            Equal((int)bounds.Width, (int)snapshot.VirtualBounds.Width, "snapshot width must equal the virtual screen");
            Equal((int)bounds.Height, (int)snapshot.VirtualBounds.Height, "snapshot height must equal the virtual screen");
            True(snapshot.PreviewSource is not null, "frozen snapshot must expose the frozen preview for the overlay");

            // Pixel readback of the known pattern: the marker must be black
            // and the far background white, at the same fractional offsets.
            var markerPixel = snapshot.Bitmap.GetPixel(
                (int)(snapshot.VirtualBounds.Width * (MarkerLeftFraction + MarkerWidthFraction / 2)),
                (int)(snapshot.VirtualBounds.Height * (MarkerTopFraction + MarkerHeightFraction / 2)));
            True((markerPixel.R + markerPixel.G + markerPixel.B) / 3 < 60,
                $"frozen snapshot must contain the controlled black marker, got {markerPixel}");
            var backgroundPixel = snapshot.Bitmap.GetPixel(
                (int)(snapshot.VirtualBounds.Width * 0.85),
                (int)(snapshot.VirtualBounds.Height * 0.85));
            True((backgroundPixel.R + backgroundPixel.G + backgroundPixel.B) / 3 > 200,
                $"frozen snapshot must contain the controlled white background, got {backgroundPixel}");

            // Crop pipeline (marquee release path): crop the marker itself and
            // decode the pixels, not just the PNG signature.
            var cropRect = new Rect(
                bounds.X + (bounds.Width * MarkerLeftFraction),
                bounds.Y + (bounds.Height * MarkerTopFraction),
                bounds.Width * MarkerWidthFraction,
                bounds.Height * MarkerHeightFraction);
            var png = ScreenCaptureService.CropPng(snapshot, cropRect);
            using (var stream = new MemoryStream(png))
            {
                var frame = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var cropPixel = new byte[4];
                frame.CopyPixels(
                    new Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, 1, 1), cropPixel, 4, 0);
                True(cropPixel[0] < 60 && cropPixel[1] < 60 && cropPixel[2] < 60,
                    $"cropped PNG must decode to the black marker, got B={cropPixel[0]} G={cropPixel[1]} R={cropPixel[2]}");
            }

            // The no-frozen-snapshot fallback (live region capture) must read
            // the same controlled marker through the same GDI path.
            var live = ScreenCaptureService.CapturePng(cropRect);
            using (var stream = new MemoryStream(live))
            {
                var frame = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                var livePixel = new byte[4];
                frame.CopyPixels(
                    new Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, 1, 1), livePixel, 4, 0);
                True(livePixel[0] < 60 && livePixel[1] < 60 && livePixel[2] < 60,
                    $"live CapturePng must decode to the black marker, got B={livePixel[0]} G={livePixel[1]} R={livePixel[2]}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    public static void HotkeyRecorderLiveModifiersAndUnbind()
    {
        Equal("Ctrl + …", HotkeyRecorder.FormatLiveModifiers(ModifierKeys.Control, addedKey: Key.LeftCtrl),
            "holding Ctrl alone should show Ctrl + …");
        Equal("Ctrl + Alt + …", HotkeyRecorder.FormatLiveModifiers(ModifierKeys.Control | ModifierKeys.Alt),
            "holding Ctrl+Alt should show Ctrl + Alt + …");
        Equal("Win + Shift + …", HotkeyRecorder.FormatLiveModifiers(ModifierKeys.Windows | ModifierKeys.Shift),
            "holding Win+Shift should show Win + Shift + …");
        Equal("请按快捷键…", HotkeyRecorder.FormatLiveModifiers(ModifierKeys.None),
            "no active modifiers should show prompt text");
        Equal("请按快捷键…", HotkeyRecorder.FormatLiveModifiers(ModifierKeys.Control, removedKey: Key.LeftCtrl),
            "releasing the last modifier should return prompt text");

        var recorder = new HotkeyRecorder
        {
            BindingValue = HotkeyBinding.SelectionDefault,
        };
        var recordedFired = 0;
        recorder.Recorded += (_, _) => recordedFired++;
        recorder.Unbind();

        True(recorder.BindingValue.IsUnset, "Unbind must set HotkeyBinding.UnsetBinding");
        Equal("未设置", recorder.Content?.ToString(), "Unbind must display 未设置");
        True(!recorder.IsRecording, "Unbind must exit recording mode");
        Equal(1, recordedFired, "Unbind must raise Recorded event so diagnostics and clear buttons sync");
    }

    public static void HotkeyServiceDiagnoseDraftAndOneClickFix()
    {
        // Self-owned hotkey check reports available without invoking system conflict.
        var uniqueBinding = new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModAlt | HotkeyBinding.ModShift, 0x7E); // F15
        True(HotkeyService.IsBindingAvailable(uniqueBinding, new[] { uniqueBinding }),
            "binding owned by current process must count as available");

        // 1. Simulate Win32 external occupation on SelectionHotkey (Ctrl+Alt+W) while Ctrl+Alt+Q is free.
        var occupiedDraft = ShellSettings.Default;
        var issues = HotkeyService.DiagnoseDraft(
            occupiedDraft,
            ownedByApp: Array.Empty<HotkeyBinding>(),
            probeSystem: true,
            probeAvailable: b => !string.Equals(b.DisplayName, occupiedDraft.SelectionHotkey.DisplayName, StringComparison.OrdinalIgnoreCase));

        Equal(1, issues.Count, "exactly one occupied slot should be flagged");
        Equal("Selection", issues[0].SlotKey, "Selection slot should be flagged");
        Equal(ShortcutIssueKind.SystemOccupied, issues[0].Kind, "occupied slot kind should be SystemOccupied");
        True(issues[0].SuggestedBinding is { } suggested &&
             !string.Equals(suggested.DisplayName, occupiedDraft.SelectionHotkey.DisplayName, StringComparison.OrdinalIgnoreCase),
            "diagnostic should suggest a non-conflicting alternative");

        // 2. Duplicate inside app + reserved key detection (both global fixed and interface reserved).
        var duplicateAndReserved = ShellSettings.Default with
        {
            ScreenshotHotkey = ShellSettings.Default.SelectionHotkey,
            QuickSearchHotkey = new HotkeyBinding(HotkeyBinding.ModControl, 0x53), // Ctrl+S (fixed action)
            InterfaceModelHotkey = new HotkeyBinding(HotkeyBinding.ModControl, 0x43), // Ctrl+C (interface reserved)
        };
        var dupIssues = HotkeyService.DiagnoseDraft(
            duplicateAndReserved,
            probeSystem: false);
        True(dupIssues.Any(i => i.Kind == ShortcutIssueKind.Duplicate && i.SlotKey == "Screenshot"),
            "duplicate binding in Screenshot must be flagged as Duplicate");
        True(dupIssues.Any(i => i.Kind == ShortcutIssueKind.Reserved && i.SlotKey == "QuickSearch"),
            "fixed Ctrl+S binding on global action must be flagged as Reserved");
        True(dupIssues.Any(i => i.Kind == ShortcutIssueKind.Reserved && i.SlotKey == "InterfaceModel"),
            "reserved Ctrl+C binding on interface action must be flagged as Reserved");

        // 3. Exhausted candidates gracefully return null.
        True(HotkeyService.FindFirstFreeAlternative(HotkeyAction.TranslateSelection, occupiedDraft, probeAvailable: _ => false) is null,
            "when all candidate shortcuts are occupied, FindFirstFreeAlternative must return null");

        // 4. ShortcutsSection inline diagnostic bar & one-click fix button integration.
        var section = new ShortcutsSection();
        section.SelectionHotkey.BindingValue = occupiedDraft.SelectionHotkey;
        section.ScreenshotHotkey.BindingValue = occupiedDraft.ScreenshotHotkey;
        section.QuickSearchHotkey.BindingValue = occupiedDraft.QuickSearchHotkey;
        section.CloseHotkey.BindingValue = occupiedDraft.CloseHotkey;
        section.ShowWindowHotkey.BindingValue = occupiedDraft.ShowWindowHotkey;

        section.RefreshDiagnostics(
            occupiedDraft,
            ownedByApp: Array.Empty<HotkeyBinding>(),
            probeSystem: true,
            showCleanFeedback: false,
            probeAvailable: b => !string.Equals(b.DisplayName, occupiedDraft.SelectionHotkey.DisplayName, StringComparison.OrdinalIgnoreCase));

        var diagBar = Field<Border>(section, "ShortcutDiagnosticBar");
        var fixBtn = Field<Button>(section, "ShortcutFixButton");
        Equal(Visibility.Visible, diagBar.Visibility, "diagnostic bar must appear when a conflict exists");
        Equal(Visibility.Visible, fixBtn.Visibility, "one-click fix button must appear when an alternative exists");

        var editedRaised = false;
        section.ShortcutEdited += (_, _) => editedRaised = true;
        fixBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        True(editedRaised, "clicking one-click fix must raise ShortcutEdited");
        True(!string.Equals(section.SelectionHotkey.BindingValue.DisplayName, occupiedDraft.SelectionHotkey.DisplayName, StringComparison.OrdinalIgnoreCase),
            "clicking one-click fix must replace the conflicting SelectionHotkey with the free alternative");

        // Clean probe shows reassuring clean feedback.
        var fixedDraft = occupiedDraft with { SelectionHotkey = section.SelectionHotkey.BindingValue };
        section.RefreshDiagnostics(
            fixedDraft,
            ownedByApp: Array.Empty<HotkeyBinding>(),
            probeSystem: true,
            showCleanFeedback: true,
            probeAvailable: _ => true);
        Equal(Visibility.Visible, diagBar.Visibility, "manual probe with zero issues should display clean feedback");
        Equal(Visibility.Collapsed, fixBtn.Visibility, "clean state must hide one-click fix button");
    }

    public static void FirstRunWindowPreservesExistingSettingsOnReopen()
    {
        var previous = ShellSettingsStore.Load();
        try
        {
            var custom = previous with
            {
                HasCompletedOnboarding = true,
                FreeEngineConsent = FreeEngineConsent.Denied,
                HistoryEnabled = false,
                StartWithWindows = false,
                IssueUploadConsent = true,
                SelectionHotkey = new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x51), // Ctrl+Shift+Q
                ScreenshotHotkey = new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x53), // Ctrl+Shift+S
                QuickSearchHotkey = new HotkeyBinding(HotkeyBinding.ModAlt, 0x4B), // Alt+K
            };
            ShellSettingsStore.Save(custom);

            var window = new FirstRunWindow();
            var selectionText = Field<TextBlock>(window, "SelectionShortcutText");
            var screenshotText = Field<TextBlock>(window, "ScreenshotShortcutText");
            var quickSearchText = Field<TextBlock>(window, "QuickSearchShortcutText");
            var freeToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "FreeEngineToggle");
            var historyToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "HistoryToggle");
            var startupToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "StartupToggle");
            var diagToggle = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "DiagnosticsToggle");

            True(selectionText.Text.Contains(custom.SelectionHotkey.DisplayName, StringComparison.Ordinal),
                "reopened onboarding must display the user's current SelectionHotkey");
            True(screenshotText.Text.Contains(custom.ScreenshotHotkey.DisplayName, StringComparison.Ordinal),
                "reopened onboarding must display the user's current ScreenshotHotkey");
            True(quickSearchText.Text.Contains(custom.QuickSearchHotkey.DisplayName, StringComparison.Ordinal),
                "reopened onboarding must display the user's current QuickSearchHotkey");
            Equal(false, freeToggle.IsChecked == true, "reopened onboarding must reflect saved FreeEngineConsent=Denied");
            Equal(false, historyToggle.IsChecked == true, "reopened onboarding must reflect saved HistoryEnabled=false");
            Equal(false, startupToggle.IsChecked == true, "reopened onboarding must reflect saved StartWithWindows=false");
            Equal(true, diagToggle.IsChecked == true, "reopened onboarding must reflect saved IssueUploadConsent=true");

            window.Close();

            var reloaded = ShellSettingsStore.Load();
            Equal(FreeEngineConsent.Denied, reloaded.FreeEngineConsent, "opening/closing onboarding again must not reset FreeEngineConsent");
            Equal(false, reloaded.HistoryEnabled, "opening/closing onboarding again must not reset HistoryEnabled");
        }
        finally
        {
            ShellSettingsStore.Save(previous);
        }
    }

    public static void StandaloneFunctionAndPrintScreenHotkeys()
    {
        var f8 = HotkeyBinding.Parse("F8", HotkeyBinding.UnsetBinding);
        True(!f8.IsUnset, "F8 should parse as standalone hotkey");
        Equal(0u, f8.Modifiers, "standalone F8 must have no modifiers");
        Equal(0x77u, f8.VirtualKey, "F8 keycode must be 0x77");
        Equal("F8", f8.DisplayName, "standalone F8 display name must be F8");
        True(f8.IsValid, "standalone F8 must be valid");

        var prtSc = HotkeyBinding.Parse("PrtSc", HotkeyBinding.UnsetBinding);
        True(!prtSc.IsUnset, "PrtSc alias should parse");
        Equal(0u, prtSc.Modifiers, "standalone PrintScreen must have no modifiers");
        Equal(0x2Cu, prtSc.VirtualKey, "PrintScreen keycode must be VK_SNAPSHOT (0x2C)");
        Equal("PrintScreen", prtSc.DisplayName, "PrintScreen canonical display name");
        True(prtSc.IsValid, "standalone PrintScreen must be valid");

        True(HotkeyBinding.Parse("A", HotkeyBinding.UnsetBinding).IsUnset,
            "bare letter A without modifiers must still be rejected");

        var settingsWithStandalone = ShellSettings.Default with
        {
            ScreenshotHotkey = prtSc,
            QuickSearchHotkey = f8,
        };
        True(settingsWithStandalone.ValidateHotkeys() is null,
            "standalone PrintScreen/F8 hotkeys must pass ShellSettings.ValidateHotkeys");

        // PrintScreen is delivered on KeyUp on Windows: verify HotkeyRecorder captures Key.Snapshot on PreviewKeyUp.
        using var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("hotkey_test") { Width = 1, Height = 1 });
        var recorder = new HotkeyRecorder { BindingValue = HotkeyBinding.ScreenshotDefault };
        var recordedFired = 0;
        recorder.Recorded += (_, _) => recordedFired++;
        typeof(HotkeyRecorder).GetMethod("OnClick", NonPublicInstance)!.Invoke(recorder, null);
        True(recorder.IsRecording, "OnClick must enter recording state");

        var keyUp = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Snapshot)
        {
            RoutedEvent = UIElement.PreviewKeyUpEvent,
        };
        recorder.RaiseEvent(keyUp);

        True(!recorder.IsRecording, "PrintScreen KeyUp must exit recording state");
        Equal(0x2Cu, recorder.BindingValue.VirtualKey, "PrintScreen KeyUp must commit VK_SNAPSHOT");
        Equal("PrintScreen", recorder.Content?.ToString(), "recorder must display PrintScreen");
        Equal(1, recordedFired, "PrintScreen KeyUp must raise Recorded event");
    }

    public static void DiagnosticsPrioritizesEditedSlotExpandsHiddenAndUsesFallbackPool()
    {
        // 1. preferredFixSlotKey moves the newly edited slot ahead of earlier slots on duplicate collision.
        var duplicate = ShellSettings.Default with
        {
            SelectionHotkey = ShellSettings.Default.ScreenshotHotkey,
        };
        var prioritized = HotkeyService.DiagnoseDraft(
            duplicate,
            probeSystem: false,
            preferredFixSlotKey: "Selection");
        True(prioritized.Count >= 1, "duplicate collision must produce diagnostics");
        Equal("Selection", prioritized[0].SlotKey,
            "preferredFixSlotKey must prioritize fixing the slot the user just edited");
        True(prioritized[0].SuggestedBinding is not null, "prioritized slot must carry a suggestion");

        // 2. Fallback candidate pool still finds an available combination when curated presets are all taken.
        var curatedSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Ctrl+Alt+W", "Ctrl+Alt+E", "Ctrl+Alt+R", "Ctrl+Alt+S", "Ctrl+Alt+D",
            "Ctrl+Shift+W", "Ctrl+Shift+S", "Alt+W", "Alt+S",
        };
        var fallback = HotkeyService.FindFirstFreeAlternative(
            HotkeyAction.TranslateSelection,
            ShellSettings.Default,
            probeAvailable: b => !curatedSelection.Contains(b.DisplayName));
        True(fallback is not null && !curatedSelection.Contains(fallback.DisplayName),
            "fallback pool must supply a free binding when curated presets are all occupied");

        // 3. Interface slot duplicates suggest a free interface shortcut.
        var interfaceCollision = ShellSettings.Default with
        {
            InterfaceStyleHotkey = ShellSettings.Default.InterfaceModelHotkey,
        };
        var ifaceIssues = HotkeyService.DiagnoseDraft(
            interfaceCollision,
            probeSystem: false,
            preferredFixSlotKey: "InterfaceStyle");
        Equal("InterfaceStyle", ifaceIssues[0].SlotKey, "edited interface slot should be prioritized");
        True(ifaceIssues[0].SuggestedBinding is not null,
            "interface duplicate must suggest an available interface combination");

        // 4. Hidden MoreShortcutsExpander auto-expands when an issue belongs to a collapsed slot.
        var section = new ShortcutsSection();
        var expander = Field<Expander>(section, "MoreShortcutsExpander");
        Equal(false, expander.IsExpanded, "MoreShortcutsExpander starts collapsed by default");
        var hiddenConflict = ShellSettings.Default with
        {
            ShowWindowHotkey = ShellSettings.Default.SelectionHotkey,
        };
        section.RefreshDiagnostics(hiddenConflict, probeSystem: false);
        Equal(true, expander.IsExpanded,
            "RefreshDiagnostics must auto-expand MoreShortcutsExpander when a hidden slot has a conflict");
    }

    public static void SettingsWindowRevertClearsShortcutConflictDiagnostics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"popglot_revert_diag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        SettingsWindow? window = null;
        try
        {
            var history = new HistoryStore(Path.Combine(tempDir, "history.json"));
            window = new SettingsWindow(ShellSettings.Default, history);
            var navShortcuts = Field<RadioButton>(window, "NavShortcuts");
            navShortcuts.IsChecked = true;

            var section = Field<ShortcutsSection>(window, "ShortcutsSection");
            var diagBar = Field<Border>(section, "ShortcutDiagnosticBar");
            var revertBtn = Field<Button>(window, "RevertButton");

            // Create a duplicate shortcut on SelectionHotkey and raise Recorded as the UI does.
            section.SelectionHotkey.BindingValue = ShellSettings.Default.ScreenshotHotkey;
            section.SelectionHotkey.RaiseEvent(new RoutedEventArgs(HotkeyRecorder.RecordedEvent, section.SelectionHotkey));

            Equal(Visibility.Visible, diagBar.Visibility,
                "introducing a duplicate shortcut in SettingsWindow must show ShortcutDiagnosticBar");

            revertBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Equal(Visibility.Collapsed, diagBar.Visibility,
                "clicking RevertButton must clear stale ShortcutDiagnosticBar conflicts");
            Equal(ShellSettings.Default.SelectionHotkey.DisplayName,
                section.SelectionHotkey.BindingValue.DisplayName,
                "clicking RevertButton must restore original SelectionHotkey");
        }
        finally
        {
            window?.Close();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    public static void ScreenshotSuggestionsPreferAltFreeBindings()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ShellSettings.Default.ScreenshotHotkey.DisplayName,
        };

        var expectedAltFreeSequence = new[]
        {
            "Ctrl+Shift+Space",
            "F1",
            "F2",
            "Ctrl+Shift+X",
            "Ctrl+Shift+A",
            "Ctrl+Shift+S",
            "PrintScreen",
        };

        foreach (var expected in expectedAltFreeSequence)
        {
            var suggested = HotkeyService.FindFirstFreeAlternative(
                HotkeyAction.CaptureScreen,
                ShellSettings.Default,
                probeAvailable: b => !blocked.Contains(b.DisplayName));
            True(suggested is not null, $"expected Alt-free candidate {expected} when prior options are blocked");
            Equal(expected, suggested!.DisplayName, "screenshot suggestions must rank Alt-free bindings first");
            True((suggested.Modifiers & HotkeyBinding.ModAlt) == 0,
                $"suggested screenshot binding {suggested.DisplayName} must not require Alt");
            blocked.Add(suggested.DisplayName);
        }

        // Only after all Alt-free screenshot candidates are taken does it fall back to Alt+A.
        var fallbackAfterAltFree = HotkeyService.FindFirstFreeAlternative(
            HotkeyAction.CaptureScreen,
            ShellSettings.Default,
            probeAvailable: b => !blocked.Contains(b.DisplayName));
        Equal("Alt+A", fallbackAfterAltFree?.DisplayName,
            "Alt+A should only appear after all Alt-free screenshot candidates are exhausted");
    }

    public static void ShortcutsSectionClearButtonsPreserveAlignmentAndScreenshotHelpTip()
    {
        var section = new ShortcutsSection();
        section.SelectionHotkey.BindingValue = HotkeyBinding.SelectionDefault;
        section.ScreenshotHotkey.BindingValue = HotkeyBinding.ScreenshotDefault;
        section.FixedRegionHotkey.BindingValue = HotkeyBinding.FixedRegionDefault;
        section.QuickSearchHotkey.BindingValue = HotkeyBinding.QuickSearchDefault;

        // Unbind Selection only; its clear button must become Hidden (preserving the 32px slot)
        // rather than Collapsed (which shifts the recorder right).
        section.SelectionHotkey.Unbind();
        section.RefreshDiagnostics(ShellSettings.Default with
        {
            SelectionHotkey = HotkeyBinding.UnsetBinding,
        }, probeSystem: false);

        var clearSelection = Field<Button>(section, "ClearSelectionButton");
        var clearScreenshot = Field<Button>(section, "ClearScreenshotButton");
        var clearFixedRegion = Field<Button>(section, "ClearFixedRegionButton");
        var clearQuickSearch = Field<Button>(section, "ClearQuickSearchButton");
        Equal(Visibility.Hidden, clearSelection.Visibility,
            "unset primary shortcut row must keep Hidden clear button so column alignment does not shift");
        Equal(false, clearSelection.IsHitTestVisible, "hidden clear button must not intercept clicks");
        Equal(Visibility.Visible, clearScreenshot.Visibility, "set Screenshot row must show clear button");
        Equal(Visibility.Visible, clearFixedRegion.Visibility, "set FixedRegion row must show clear button");
        Equal(Visibility.Visible, clearQuickSearch.Visibility, "set QuickSearch row must show clear button");

        section.Measure(new Size(520, 600));
        section.Arrange(new Rect(0, 0, 520, 600));
        section.UpdateLayout();

        var selX = section.SelectionHotkey.TranslatePoint(new Point(0, 0), section).X;
        var shotX = section.ScreenshotHotkey.TranslatePoint(new Point(0, 0), section).X;
        var fixX = section.FixedRegionHotkey.TranslatePoint(new Point(0, 0), section).X;
        var qsX = section.QuickSearchHotkey.TranslatePoint(new Point(0, 0), section).X;
        True(Math.Abs(selX - shotX) < 0.5 && Math.Abs(shotX - fixX) < 0.5 && Math.Abs(fixX - qsX) < 0.5,
            $"all four primary HotkeyRecorders must stay vertically aligned (sel={selX}, shot={shotX}, fix={fixX}, qs={qsX})");

        var shotTip = section.ScreenshotHotkey.ToolTip?.ToString() ?? string.Empty;
        True(shotTip.Contains("F1", StringComparison.Ordinal) &&
             shotTip.Contains("PrintScreen", StringComparison.Ordinal) &&
             shotTip.Contains("Alt", StringComparison.Ordinal),
            "ScreenshotHotkeyRecorder must preserve hover-capture Alt-free guidance across RefreshDiagnostics");

        // Ensure the ContentTemplate TextBlock does not shadow HotkeyRecorder.ToolTip
        // (which holds conflict details and screenshot hover-capture guidance).
        static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }
                var nested = FindVisualDescendant<T>(child);
                if (nested is not null)
                {
                    return nested;
                }
            }
            return null;
        }
        var innerText = FindVisualDescendant<TextBlock>(section.ScreenshotHotkey);
        True(innerText is not null && innerText.ToolTip is null,
            "ShortcutRecorder ContentTemplate TextBlock must not set its own ToolTip and shadow HotkeyRecorder.ToolTip");

        var overlay = new CaptureOverlayWindow(null);
        try
        {
            var frozenImage = Field<System.Windows.Controls.Image>(overlay, "FrozenDesktopImage");
            Equal(true, frozenImage.SnapsToDevicePixels, "FrozenDesktopImage must set SnapsToDevicePixels=True");
            Equal(true, frozenImage.UseLayoutRounding, "FrozenDesktopImage must set UseLayoutRounding=True");
        }
        finally
        {
            overlay.Close();
        }
    }

    public static void HotkeyRecorderInvalidPromptFitsAndDwmFlushFallbacksCleanly()
    {
        using var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("recorder_width_test") { Width = 1, Height = 1 });
        var recorder = new HotkeyRecorder
        {
            Width = 170,
            FontSize = 12,
            Padding = new Thickness(8, 0, 8, 0),
            BindingValue = HotkeyBinding.SelectionDefault,
        };
        typeof(HotkeyRecorder).GetMethod("OnClick", NonPublicInstance)!.Invoke(recorder, null);
        var keyDown = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.A)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        };
        recorder.RaiseEvent(keyDown);

        True(recorder.IsRecording, "bare letter key must keep recorder in recording mode");
        var promptText = recorder.Content?.ToString() ?? string.Empty;
        Equal("需 Ctrl/Alt/Win 或 F1–F12", promptText, "invalid key prompt must use concise non-truncating wording");

        var formatted = new FormattedText(
            promptText,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(recorder.FontFamily, recorder.FontStyle, recorder.FontWeight, recorder.FontStretch),
            recorder.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(recorder).PixelsPerDip);
        // 170 total button width - 16 horizontal padding - 2 border thickness = 152 DIP content slot.
        True(formatted.Width <= 152.0,
            $"invalid key prompt '{promptText}' width ({formatted.Width:F1} DIP) must fit inside 152 DIP inner width without ellipsis");

        Equal(true, App.FlushCompositionFrameForCapture(() => 0),
            "DwmFlush returning S_OK (0) must report flush succeeded");
        Equal(false, App.FlushCompositionFrameForCapture(() => unchecked((int)0x80263001)),
            "DwmFlush failure HRESULT must fall back gracefully and return false");
        Equal(false, App.FlushCompositionFrameForCapture(() => throw new DllNotFoundException("dwmapi")),
            "DwmFlush exception must fall back gracefully without throwing");
    }
}
