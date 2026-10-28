using System.IO;
using System.Windows;
using PopGlot.Windows;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    /// <summary>
    /// Regression 1: When allowDisableOccupiedDefaults=true (startup/upgrade),
    /// a factory default global hotkey conflicting with a user-customized interface
    /// shortcut is safely disabled, while preserving user customization and persisting
    /// with an accurate reason hint. Manual save rejects the conflict without deleting anything.
    /// Does not register or compete for global default keys.
    /// </summary>
    private static void UpgradeDefaultGlobalConflictsWithUserInterfaceCustomSafelyDisablesDefault()
    {
        EnsureApplication();
        var targetWindow = new Window();
        using var service = new HotkeyService(targetWindow);

        var tempDir = Path.Combine(Path.GetTempPath(), $"popglot-upgrade-iface-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "windows-shell.json");

        try
        {
            // Simulate an older version's config file where user customized InterfaceWorkbenchHotkey
            // to Win+Alt+P (which in the new version is the factory default ShowWindowHotkey).
            // All other global keys are unset so no system keys are registered or competed for.
            var userCustomBinding = HotkeyBinding.ShowWindowDefault; // Win+Alt+P
            File.WriteAllText(settingsPath, $$"""
                {
                    "InterfaceWorkbenchHotkey": "{{userCustomBinding.Serialize()}}",
                    "SelectionHotkey": "none",
                    "ScreenshotHotkey": "none",
                    "CloseHotkey": "none",
                    "QuickSearchHotkey": "none"
                }
                """);

            ShellSettingsStore.InvalidateCache();
            var loadedSettings = ShellSettingsStore.Load(settingsPath);
            Equal(userCustomBinding, loadedSettings.InterfaceWorkbenchHotkey, "old custom interface shortcut loaded");
            Equal(HotkeyBinding.ShowWindowDefault, loadedSettings.ShowWindowHotkey, "upgrade supplied factory default ShowWindow");

            // 1. Manual save in Settings (allowDisableOccupiedDefaults = false) must REJECT the conflict
            // and must NOT auto-disable or auto-delete anything.
            var manualValidationError = loadedSettings.ValidateHotkeys();
            True(manualValidationError is not null, "manual save must reject conflict between default and custom shortcut");
            True(manualValidationError!.Contains("被重复用于"), $"validation error should name duplicate: {manualValidationError}");

            // 2. Startup registration (disableOccupiedDefaults = true) must prioritize custom intent:
            // factory default ShowWindow is safely disabled, user's InterfaceWorkbench is kept.
            var success = service.TryRegisterSafeDefaults(
                loadedSettings,
                out var updatedSettings,
                out var conflict,
                out var disabledDefaults,
                out var persistenceError,
                disableOccupiedDefaults: true,
                saveSettings: s => ShellSettingsStore.Save(s, settingsPath));

            True(success, $"safe registration must succeed: {conflict}");
            True(persistenceError is null, $"disk save must succeed: {persistenceError}");
            Equal(1, disabledDefaults.Count, "exactly one default shortcut should be disabled");

            var disabledItem = disabledDefaults[0];
            Equal(HotkeyAction.ShowWindow, disabledItem.Action, "ShowWindow factory default must be disabled");
            Equal(HotkeyBinding.ShowWindowDefault, disabledItem.Binding);
            True(updatedSettings.ShowWindowHotkey.IsUnset, "ShowWindow must be unset in updatedSettings");
            Equal(userCustomBinding, updatedSettings.InterfaceWorkbenchHotkey, "user customized interface shortcut must be preserved");

            // 3. Accurate reason hint must name conflict with custom shortcut, NOT "已被系统或其他程序占用"
            True(!string.IsNullOrWhiteSpace(disabledItem.ConflictReason), "disabled item must have accurate reason");
            True(disabledItem.ConflictReason!.Contains("打开工作台") || disabledItem.ConflictReason.Contains("自定义快捷键"),
                $"reason should specify custom shortcut conflict: {disabledItem.ConflictReason}");

            var feedback = HotkeyService.DescribeDisabledDefaultsFeedback(disabledDefaults, persistenceError);
            True(feedback.Contains("打开主窗口") && feedback.Contains("与自定义快捷键") && feedback.Contains("已自动停用"),
                $"feedback notice must accurately describe the custom conflict: {feedback}");
            True(!feedback.Contains("已被系统或其他程序占用"), "accurate reason must not claim system occupation");

            // 4. Persistence check: disk has ShowWindowHotkey: "none"
            var diskContent = File.ReadAllText(settingsPath);
            True(diskContent.Contains("\"ShowWindowHotkey\": \"none\""),
                "the disabled default must be persisted as 'none'");

            // 5. Reload from disk preserves unset state and user custom binding
            ShellSettingsStore.InvalidateCache();
            var reloaded = ShellSettingsStore.Load(settingsPath);
            True(reloaded.ShowWindowHotkey.IsUnset, "reloaded ShowWindow must stay unset");
            Equal(userCustomBinding, reloaded.InterfaceWorkbenchHotkey, "reloaded custom interface shortcut must remain intact");
        }
        finally
        {
            service.Dispose();
            targetWindow.Close();
            ShellSettingsStore.InvalidateCache();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Regression 2: When a factory default global hotkey conflicts with a user-customized
    /// global hotkey, the custom global hotkey is preserved and the factory default is disabled.
    /// Manual save rejects the conflict without modifying configuration.
    /// </summary>
    private static void UpgradeDefaultGlobalConflictsWithUserCustomGlobalSafelyDisablesDefault()
    {
        var customBinding = HotkeyBinding.QuickSearchDefault; // Ctrl+Alt+Q

        // User customized SelectionHotkey to QuickSearchDefault.
        // QuickSearchHotkey is at factory default QuickSearchDefault.
        var initialSettings = ShellSettings.Default with
        {
            SelectionHotkey = customBinding, // user customized
            QuickSearchHotkey = customBinding, // factory default
        };

        // 1. Manual save mode (allowDisableOccupiedDefaults = false):
        // ValidateHotkeys must reject the duplicate without disabling anything.
        var manualValidationError = initialSettings.ValidateHotkeys();
        True(manualValidationError is not null, "manual save must reject duplicate between default and custom global shortcut");
        True(manualValidationError!.Contains("被重复用于"), $"validation should report duplicate: {manualValidationError}");
        True(!initialSettings.QuickSearchHotkey.IsUnset, "manual save must not auto-disable default");
        Equal(customBinding, initialSettings.SelectionHotkey, "manual save must not alter custom shortcut");

        // 2. Startup mode (allowDisableOccupiedDefaults = true):
        // DisableFactoryDefaultsConflictingWithCustom must disable the factory default QuickSearchHotkey,
        // preserve the user's custom SelectionHotkey, and record an accurate conflict reason.
        var disabledList = new List<DisabledDefaultHotkey>();
        var resolvedSettings = initialSettings.DisableFactoryDefaultsConflictingWithCustom(disabledList);

        True(resolvedSettings.QuickSearchHotkey.IsUnset, "factory default QuickSearchHotkey must be safely unset");
        Equal(customBinding, resolvedSettings.SelectionHotkey, "user customized SelectionHotkey must be preserved");
        Equal(1, disabledList.Count, "exactly one default shortcut should be recorded as disabled");

        var disabledItem = disabledList[0];
        Equal(HotkeyAction.QuickSearch, disabledItem.Action);
        Equal(customBinding, disabledItem.Binding);
        True(disabledItem.ConflictReason is not null && disabledItem.ConflictReason.Contains("划词翻译"),
            $"conflict reason must name the conflicting custom global shortcut: {disabledItem.ConflictReason}");

        // Feedback must state accurate reason
        var feedback = HotkeyService.DescribeDisabledDefaultsFeedback(disabledList);
        True(feedback.Contains("极速查词") && feedback.Contains("划词翻译") && feedback.Contains("已自动停用"),
            $"feedback notice must accurately describe the conflict: {feedback}");
        True(!feedback.Contains("已被系统或其他程序占用"), "accurate reason must not claim system occupation");

        // After disabling conflicting default, resolved settings must pass hotkey validation
        True(resolvedSettings.ValidateHotkeys() is null, "resolved settings must now be valid for registration");
    }

    /// <summary>
    /// Regression 3: When BOTH conflicting shortcuts are user-customized, neither is
    /// auto-disabled or auto-deleted; validation continues to report conflict and registration fails.
    /// Also verifies rollback contract: safe registration fails, leaves disk untouched, rolls back state.
    /// </summary>
    private static void BothCustomizedShortcutsConflictContinuesToReportErrorAndNeverAutoDeletes()
    {
        EnsureApplication();
        var targetWindow = new Window();
        using var service = new HotkeyService(targetWindow);

        var tempDir = Path.Combine(Path.GetTempPath(), $"popglot-both-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "windows-shell.json");

        try
        {
            var customCombination = new HotkeyBinding(
                HotkeyBinding.ModControl | HotkeyBinding.ModAlt | HotkeyBinding.ModShift, 0x7E); // F15

            // User customized BOTH SelectionHotkey and InterfaceWorkbenchHotkey to F15
            var conflictingSettings = ShellSettings.Default with
            {
                SelectionHotkey = customCombination, // customized
                ScreenshotHotkey = HotkeyBinding.UnsetBinding,
                CloseHotkey = HotkeyBinding.UnsetBinding,
                ShowWindowHotkey = HotkeyBinding.UnsetBinding,
                QuickSearchHotkey = HotkeyBinding.UnsetBinding,
                InterfaceWorkbenchHotkey = customCombination, // customized
            };
            // Initial save without validation
            File.WriteAllText(settingsPath, "{ \"test\": true }");

            // Even when disableOccupiedDefaults = true, safe defaults must NOT auto-delete custom shortcuts!
            var candidate = conflictingSettings.DisableFactoryDefaultsConflictingWithCustom();
            Equal(conflictingSettings, candidate, "neither customized shortcut should be disabled");

            var success = service.TryRegisterSafeDefaults(
                conflictingSettings,
                out var updatedSettings,
                out var conflict,
                out var disabledDefaults,
                out var persistenceError,
                disableOccupiedDefaults: true,
                saveSettings: s => ShellSettingsStore.Save(s, settingsPath));

            True(!success, "both-customized conflict must fail registration");
            Equal(0, disabledDefaults.Count, "no shortcuts should be reported as disabled defaults");
            Equal(conflictingSettings, updatedSettings, "updatedSettings must match original settings (rollback)");
            True(conflict is not null && conflict.Contains("被重复用于"),
                $"conflict message must describe the duplicate error: {conflict}");

            // Verify disk was NOT overwritten by safe registration
            var diskContent = File.ReadAllText(settingsPath);
            True(diskContent.Contains("\"test\": true"), "disk must remain untouched on failure");
        }
        finally
        {
            service.Dispose();
            targetWindow.Close();
            ShellSettingsStore.InvalidateCache();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
