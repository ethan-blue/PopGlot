using System.IO;
using System.Windows;
using PopGlot.Windows;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void FixedRegionDefaultHotkeyPolicyAndNoConflicts()
    {
        var defaultBinding = HotkeyBinding.FixedRegionDefault;
        Equal("Ctrl+Shift+G", defaultBinding.DisplayName, "fixed region default hotkey must be Ctrl+Shift+G");
        True(defaultBinding.IsValid && !defaultBinding.IsUnset, "fixed region default hotkey must be valid");
        Equal(0u, defaultBinding.Modifiers & HotkeyBinding.ModAlt,
            "fixed region default hotkey must be Alt-free so games and menus do not lose focus");

        True(!InterfaceKeys.IsFixedAction(defaultBinding), "fixed region default must not collide with fixed actions");
        True(!InterfaceKeys.IsReserved(defaultBinding), "fixed region default must not collide with reserved keys");

        var otherDefaults = new[]
        {
            HotkeyBinding.SelectionDefault,
            HotkeyBinding.ScreenshotDefault,
            HotkeyBinding.CloseDefault,
            HotkeyBinding.ShowWindowDefault,
            HotkeyBinding.QuickSearchDefault,
            HotkeyBinding.InterfaceWorkbenchDefault,
            HotkeyBinding.InterfaceLibraryDefault,
            HotkeyBinding.InterfaceSettingsDefault,
            HotkeyBinding.InterfaceTranslateDefault,
            HotkeyBinding.InterfaceSpeakDefault,
            HotkeyBinding.InterfaceModelDefault,
            HotkeyBinding.InterfaceStyleDefault,
            HotkeyBinding.InterfaceSwapDefault,
        };
        foreach (var other in otherDefaults)
        {
            True(!string.Equals(defaultBinding.DisplayName, other.DisplayName, StringComparison.OrdinalIgnoreCase),
                $"FixedRegionDefault must not collide with existing default {other.DisplayName}");
        }

        // Must not collide with any of the Alt-free screenshot candidates checked by ScreenshotSuggestionsPreferAltFreeBindings.
        var screenshotCandidates = new[]
        {
            "Ctrl+Shift+Space", "F1", "F2", "Ctrl+Shift+X", "Ctrl+Shift+A", "Ctrl+Shift+S", "PrintScreen", "Alt+A",
        };
        foreach (var candidate in screenshotCandidates)
        {
            True(!string.Equals(defaultBinding.DisplayName, candidate, StringComparison.OrdinalIgnoreCase),
                $"FixedRegionDefault must not collide with screenshot candidate {candidate}");
        }

        var settings = ShellSettings.Default;
        True(settings.ValidateHotkeys() is null, "ShellSettings.Default with FixedRegionHotkey must validate cleanly");
        Equal(defaultBinding, settings.FixedRegionHotkey, "ShellSettings.Default must expose FixedRegionDefault");
        Equal(defaultBinding, settings.GetHotkey(HotkeyAction.CaptureFixedRegion), "GetHotkey(CaptureFixedRegion) must return FixedRegionHotkey");
        Equal(defaultBinding, ShellSettings.GetDefault(HotkeyAction.CaptureFixedRegion), "GetDefault(CaptureFixedRegion) must return FixedRegionDefault");
        True(settings.IsDefault(HotkeyAction.CaptureFixedRegion), "Default settings must report IsDefault(CaptureFixedRegion)");
        Equal("固定区域翻译", ShellSettings.ActionName(HotkeyAction.CaptureFixedRegion), "ActionName(CaptureFixedRegion) must be 固定区域翻译");
        True(settings.Hotkeys.TryGetValue(HotkeyAction.CaptureFixedRegion, out var active) && active == defaultBinding,
            "ShellSettings.Hotkeys dictionary must include CaptureFixedRegion");
    }

    private static void FixedRegionPersistenceRoundTripAndBackwardCompat(string root)
    {
        var legacyPath = Path.Combine(root, $"fixed-region-legacy-{Guid.NewGuid():N}.json");
        File.WriteAllText(legacyPath, """
            {
                "SchemaVersion": 3,
                "SelectionHotkey": "Ctrl+Alt+W",
                "ScreenshotHotkey": "Ctrl+Alt+Space",
                "CloseHotkey": "Ctrl+Alt+X"
            }
            """);
        ShellSettingsStore.InvalidateCache();
        var migrated = ShellSettingsStore.Load(legacyPath);
        Equal(HotkeyBinding.FixedRegionDefault, migrated.FixedRegionHotkey, "legacy config without FixedRegionHotkey must migrate to FixedRegionDefault");
        Equal(0, migrated.FixedCaptureRegions.Count, "legacy config without FixedCaptureRegions must load empty preset list");
        True(migrated.ActiveFixedRegionId is null && migrated.ActiveFixedRegion is null, "legacy config must have null active fixed region");

        // Multi-preset round-trip with negative secondary monitor coordinates and value equality.
        var preset1 = new FixedCaptureRegion(
            Id: "dialog-main",
            Name: "默认区域",
            X: 320,
            Y: 780,
            Width: 1280,
            Height: 240,
            VirtualScreenX: 0,
            VirtualScreenY: 0,
            VirtualScreenWidth: 1920,
            VirtualScreenHeight: 1080,
            MonitorDeviceName: @"\\.\DISPLAY1",
            MonitorDpiScale: 1.25);
        var preset2 = new FixedCaptureRegion(
            Id: "sub-left-monitor",
            Name: "副屏字幕",
            X: -1600,
            Y: -120,
            Width: 960,
            Height: 180,
            VirtualScreenX: -1920,
            VirtualScreenY: -200,
            VirtualScreenWidth: 3840,
            VirtualScreenHeight: 1280,
            MonitorDeviceName: @"\\.\DISPLAY2",
            MonitorDpiScale: 1.5);

        var roundTripPath = Path.Combine(root, $"fixed-region-rt-{Guid.NewGuid():N}.json");
        var saved = ShellSettings.Default with
        {
            HasCompletedOnboarding = true,
            FixedRegionHotkey = HotkeyBinding.Parse("Ctrl+Shift+E", HotkeyBinding.FixedRegionDefault),
            FixedCaptureRegions = new FixedRegionPresetList([preset1, preset2]),
            ActiveFixedRegionId = preset2.Id,
        };
        ShellSettingsStore.Save(saved, roundTripPath);
        ShellSettingsStore.InvalidateCache();
        var reloaded = ShellSettingsStore.Load(roundTripPath);

        Equal(saved, reloaded, "ShellSettings with FixedRegionPresetList must satisfy record structural equality on reload");
        Equal(2, reloaded.FixedCaptureRegions.Count, "both presets must survive reload");
        Equal(preset2, reloaded.ActiveFixedRegion, "ActiveFixedRegion must resolve ActiveFixedRegionId");
        Equal(-1600, reloaded.ActiveFixedRegion!.X, "negative X coordinate must have zero drift");
        Equal(-120, reloaded.ActiveFixedRegion.Y, "negative Y coordinate must have zero drift");

        // UserDataBackupService direct JsonSerializer round-trip
        var backupHistory = new HistoryStore(Path.Combine(root, $"backup-hist-{Guid.NewGuid():N}.json"));
        var backupVocab = new VocabularyStore(Path.Combine(root, $"backup-vocab-{Guid.NewGuid():N}.json"));
        var package = UserDataBackupService.CreateBackup(backupHistory, backupVocab, saved);
        var backupJson = UserDataBackupService.SerializeBackup(package);
        var validation = UserDataBackupService.ValidateBackup(backupJson);
        Equal(BackupValidationStatus.Valid, validation.Status, $"backup with fixed regions must validate: {validation.Message}");
        ShellSettings? restoredFromBackup = null;
        UserDataBackupService.RestoreBackup(validation.Package!, backupHistory, backupVocab, s => restoredFromBackup = s);
        True(restoredFromBackup is not null, "backup restore must invoke settings applier");
        Equal(saved, restoredFromBackup!, "UserDataBackupService round-trip must preserve FixedCaptureRegions and FixedRegionHotkey");

        // Normalization: invalid geometry dropped, non-finite DPI scale sanitized, duplicate IDs deduplicated,
        // long names capped at 32 chars, preset count capped at 8, missing activeId coalesced to first valid preset.
        var rawRegions = new List<FixedCaptureRegion>
        {
            preset1 with { Id = "tiny", Width = 4, Height = 100 }, // invalid (<6x6)
            preset1 with { Id = "huge", Width = 5000, Height = 4000 }, // invalid (>16MP)
        };
        for (var i = 1; i <= 10; i++)
        {
            rawRegions.Add(preset1 with
            {
                Id = i == 2 ? "p1" : $"p{i}", // i=2 duplicates p1
                Name = new string('区', 40) + i,
                MonitorDpiScale = i % 2 == 0 ? double.NaN : double.PositiveInfinity,
            });
        }
        var (normalizedList, normalizedActive) = ShellSettings.NormalizeFixedRegions(rawRegions, "non-existent-id");
        Equal(FixedCaptureRegion.MaxPresetCount, normalizedList.Count, "NormalizeFixedRegions must cap presets at 8 and drop invalid/duplicate items");
        Equal("p1", normalizedActive, "NormalizeFixedRegions must coalesce unknown ActiveFixedRegionId to first valid preset");
        Equal(FixedCaptureRegion.MaxNameLength, normalizedList[0].Name.Length, "NormalizeFixedRegions must cap preset Name length at 32");
        Equal(1.0, normalizedList[0].MonitorDpiScale, "NormalizeFixedRegions must sanitize non-finite DPI scale to 1.0");

        // DisableFactoryDefaultsConflictingWithCustom: user custom shortcut equal to Ctrl+Shift+G disables FixedRegionDefault
        var customConflict = ShellSettings.Default with
        {
            InterfaceWorkbenchHotkey = HotkeyBinding.FixedRegionDefault,
        };
        var disabled = new List<DisabledDefaultHotkey>();
        var resolved = customConflict.DisableFactoryDefaultsConflictingWithCustom(disabled);
        True(resolved.FixedRegionHotkey.IsUnset, "factory default FixedRegionHotkey conflicting with custom shortcut must be unset");
        Equal(HotkeyBinding.FixedRegionDefault, resolved.InterfaceWorkbenchHotkey, "user customized InterfaceWorkbenchHotkey must be preserved");
        Equal(1, disabled.Count, "disabled default list must record CaptureFixedRegion");
        Equal(HotkeyAction.CaptureFixedRegion, disabled[0].Action, "disabled action must be CaptureFixedRegion");
    }

    private static void FixedRegionResolverMultiMonitorClampAndOutOfBounds()
    {
        var desktop = new Rect(-1920, -200, 3840, 1280);

        // 1. CreateFromSelection snaps fractional physical coordinates matching ScreenCaptureService.ValidateRegion 1:1
        var rawSelection = new Rect(-1500.4, -80.2, 640.3, 180.1);
        var created = FixedRegionResolver.CreateFromSelection(
            rawSelection,
            desktop,
            id: "vn-box",
            name: "  游戏对话框  ",
            monitorDeviceName: @"\\.\DISPLAY2",
            monitorDpiScale: 1.5);
        var validated = ScreenCaptureService.ValidateRegion(rawSelection);
        Equal(validated.X, created.X, "saved X must match ScreenCaptureService.ValidateRegion 1:1");
        Equal(validated.Y, created.Y, "saved Y must match ScreenCaptureService.ValidateRegion 1:1");
        Equal(validated.Width, created.Width, "saved Width must match ScreenCaptureService.ValidateRegion 1:1");
        Equal(validated.Height, created.Height, "saved Height must match ScreenCaptureService.ValidateRegion 1:1");
        Equal("游戏对话框", created.Name, "preset name must be trimmed");

        // 2. Exact resolution on negative secondary monitor & primary monitor
        var exactRes = FixedRegionResolver.Resolve(created, desktop);
        Equal(FixedRegionResolutionStatus.Exact, exactRes.Status, "region inside virtual desktop must resolve as Exact");
        Equal(false, exactRes.WasClamped, "Exact resolution must not be marked clamped");
        Equal(created.PixelRect, exactRes.PixelRect, "Exact resolution must preserve coordinates with 0 px drift");

        // 3. Partial overhang clamps to intersection >= 6x6
        var narrowerDesktop = new Rect(-1200, 0, 3120, 1080);
        var clampedRes = FixedRegionResolver.Resolve(created, narrowerDesktop);
        Equal(FixedRegionResolutionStatus.Clamped, clampedRes.Status, "partially overhanging region must resolve as Clamped");
        Equal(true, clampedRes.WasClamped, "Clamped resolution must set WasClamped=true");
        Equal(new Rect(-1200, 0, 340, 100), clampedRes.PixelRect, "clamped rect must equal intersection with current virtual screen");
        Equal(FixedRegionResolver.ClampedStatusHint, clampedRes.StatusHint, "clamped resolution must carry status hint");

        // 4. Disconnected secondary monitor -> OutOfBounds
        var primaryOnlyDesktop = new Rect(0, 0, 1920, 1080);
        var oobRes = FixedRegionResolver.Resolve(created, primaryOnlyDesktop);
        Equal(FixedRegionResolutionStatus.OutOfBounds, oobRes.Status, "region on disconnected monitor must resolve as OutOfBounds");
        Equal(false, oobRes.CanCapture, "OutOfBounds resolution must refuse capture");
        Equal(FixedRegionResolver.OutOfBoundsStatusHint, oobRes.StatusHint, "OutOfBounds resolution must explain re-selection");

        // 5. OverlapsWindow checks strict positive-area intersection
        var regionRect = new Rect(300, 700, 1200, 220);
        True(FixedRegionResolver.OverlapsWindow(regionRect, new Rect(400, 600, 540, 380)),
            "window intersecting capture region must report overlap");
        True(!FixedRegionResolver.OverlapsWindow(regionRect, new Rect(300, 300, 540, 400)),
            "window touching top edge (Bottom == 700 == region.Top) must not report overlap");
        True(!FixedRegionResolver.OverlapsWindow(regionRect, new Rect(1500, 700, 400, 220)),
            "window touching right edge (Left == 1500 == region.Right) must not report overlap");

        // 6. Preset Upsert / Remove / Select / GenerateNextPresetName
        var list = FixedRegionPresetList.Empty;
        Equal("默认区域", FixedRegionResolver.GenerateNextPresetName(list));
        var (afterFirst, active1) = FixedRegionResolver.Upsert(list, created with { Id = "r1", Name = "默认区域" });
        Equal(1, afterFirst.Count);
        Equal("r1", active1);
        Equal("区域 2", FixedRegionResolver.GenerateNextPresetName(afterFirst));

        var (afterSecond, active2) = FixedRegionResolver.Upsert(afterFirst, created with { Id = "r2", Name = "区域 2", X = 100, Y = 200 });
        Equal(2, afterSecond.Count);
        Equal("r2", active2);

        // In-place update by ID preserves count and updates geometry
        var (afterUpdate, activeUpdated) = FixedRegionResolver.Upsert(afterSecond, created with { Id = "r1", Name = "默认区域", Width = 900 });
        Equal(2, afterUpdate.Count);
        Equal("r1", activeUpdated);
        Equal(900, afterUpdate[0].Width, "Upsert with existing ID must update preset in place");

        Equal("r2", FixedRegionResolver.Select(afterUpdate, "r2"), "Select must find matching preset ID");
        Equal("r1", FixedRegionResolver.Select(afterUpdate, "unknown"), "Select with unknown ID must fall back to first preset");

        var (afterRemove, activeAfterRemove) = FixedRegionResolver.Remove(afterUpdate, "r2", "r2");
        Equal(1, afterRemove.Count);
        Equal("r1", activeAfterRemove, "removing active preset must fall back to remaining preset");

        var (afterClearAll, activeAfterClearAll) = FixedRegionResolver.Remove(afterRemove, "r1", "r1");
        Equal(0, afterClearAll.Count);
        True(activeAfterClearAll is null, "removing last preset must leave ActiveId null");
    }
}
