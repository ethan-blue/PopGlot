using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal enum ShortcutIssueKind
{
    Invalid,
    Reserved,
    Duplicate,
    SystemOccupied,
}

internal sealed record ShortcutIssue(
    string SlotKey,
    string ActionLabel,
    HotkeyAction? GlobalAction,
    HotkeyBinding Binding,
    ShortcutIssueKind Kind,
    string Message,
    HotkeyBinding? SuggestedBinding = null);

internal readonly record struct HotkeyRegistrationFailure(
    HotkeyAction Action,
    HotkeyBinding Binding,
    int Win32Error)
{
    public bool IsOccupation => Win32Error == HotkeyService.ErrorHotkeyAlreadyRegistered;
}

/// <summary>
/// Registers the process-wide hotkeys and reports precisely which one Windows
/// refused, so the settings page can point at the offending row instead of
/// silently leaving the app without shortcuts.
/// </summary>
internal sealed partial class HotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x5047;
    private const int WmHotkey = 0x0312;

    /// <summary>Win32 ERROR_HOTKEY_ALREADY_REGISTERED: another window owns the combination.</summary>
    internal const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private IReadOnlyDictionary<HotkeyAction, HotkeyBinding> _current =
        new Dictionary<HotkeyAction, HotkeyBinding>();
    private bool _suspended;
    private bool _disposed;

    /// <summary>
    /// True from the moment a registration attempt leaves the process
    /// WITHOUT its desired hotkey set (the rollback restore failed, or a
    /// resume after suspension failed) until the full set is live again.
    /// A settings probe whose previous set restored cleanly never enters
    /// this state.
    /// </summary>
    private bool _degraded;

    public HotkeyService(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("Unable to attach the PopGlot hotkey window hook.");
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler<HotkeyAction>? Pressed;
    public event EventHandler<string>? RegistrationFailed;

    /// <summary>
    /// Fires exactly once when a previously failed/degraded state (failed
    /// restore, failed resume) reaches a FULL recovery — the complete
    /// desired set is registered and dispatchable again. Never fires for
    /// ordinary re-registrations that never lost availability.
    /// </summary>
    public event EventHandler? RegistrationRestored;

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> CurrentHotkeys => _current;
    public bool IsSuspended => _suspended;

    /// <summary>True while a registration failure left the process without its hotkeys.</summary>
    public bool IsDegraded => _degraded;

    /// <summary>
    /// True only when the full desired set is registered and dispatchable
    /// right now. False while deliberately suspended, degraded after a
    /// failed registration/restore, or before the first success. Callers
    /// use this to tell a settings PROBE failure (candidate lost, previous
    /// set live again → true) apart from a real global failure (→ false).
    /// </summary>
    public bool IsFullyAvailable => !_suspended && !_degraded && _registered.Count > 0;

    public IReadOnlyDictionary<int, HotkeyAction> RegisteredHotkeys => _registered;

    /// <summary>
    /// Applies a whole set atomically: on any failure the previously working
    /// set is restored so the user is never left with no shortcuts at all.
    /// </summary>
    public bool TryRegisterAll(
        IReadOnlyDictionary<HotkeyAction, HotkeyBinding> hotkeys,
        out string? conflict) =>
        TryRegisterAll(hotkeys, out conflict, out _);

    public bool TryRegisterAll(
        IReadOnlyDictionary<HotkeyAction, HotkeyBinding> hotkeys,
        out string? conflict,
        out HotkeyRegistrationFailure? failure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(hotkeys);

        var previous = _current;
        UnregisterAll();
        if (RegisterSet(hotkeys, out conflict, out failure))
        {
            _current = new Dictionary<HotkeyAction, HotkeyBinding>(hotkeys);
            _suspended = false;
            ExitDegradedState();
            return true;
        }

        UnregisterAll();
        _current = previous;
        if (!RegisterSet(previous, out var restoreConflict, out _))
        {
            _suspended = true;
            _degraded = true;
            var detail = $"{conflict}。且恢复原快捷键也失败（{restoreConflict}）";
            RegistrationFailed?.Invoke(this, detail);
            return false;
        }
        _suspended = false;
        // The candidate lost, but the previous desired set is fully live
        // again — a probe failure, not a global one. If the service WAS
        // degraded before this call (e.g. a failed resume), the successful
        // restore of the desired set IS the full recovery.
        ExitDegradedState();
        return false;
    }

    /// <summary>
    /// Attempts to register the hotkeys from <paramref name="settings"/>.
    /// If registering a factory default hotkey fails due to system occupation
    /// (ERROR_HOTKEY_ALREADY_REGISTERED), the conflicting default item is safely
    /// disabled (set to <see cref="HotkeyBinding.UnsetBinding"/>) and persisted to disk,
    /// and registration retries with the remaining set so that non-conflicting hotkeys
    /// remain functional and subsequent startups/upgrades will not resurrect the conflicting default.
    /// User-customized hotkeys and non-occupation failures are never automatically disabled.
    /// </summary>
    public bool TryRegisterSafeDefaults(
        ShellSettings settings,
        out ShellSettings updatedSettings,
        out string? conflict,
        out IReadOnlyList<DisabledDefaultHotkey> disabledDefaults,
        bool disableOccupiedDefaults = true) =>
        TryRegisterSafeDefaults(
            settings,
            out updatedSettings,
            out conflict,
            out disabledDefaults,
            out _,
            disableOccupiedDefaults,
            saveSettings: null);

    public bool TryRegisterSafeDefaults(
        ShellSettings settings,
        out ShellSettings updatedSettings,
        out string? conflict,
        out IReadOnlyList<DisabledDefaultHotkey> disabledDefaults,
        out string? persistenceError,
        bool disableOccupiedDefaults = true,
        Action<ShellSettings>? saveSettings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        var current = settings;
        var disabled = new List<DisabledDefaultHotkey>();
        persistenceError = null;

        if (disableOccupiedDefaults)
        {
            current = current.DisableFactoryDefaultsConflictingWithCustom(disabled);
        }

        var validationError = current.ValidateHotkeys();
        if (validationError is not null)
        {
            updatedSettings = settings;
            disabledDefaults = Array.Empty<DisabledDefaultHotkey>();
            conflict = validationError;
            return false;
        }

        while (true)
        {
            if (TryRegisterAll(current.Hotkeys, out conflict, out var failure))
            {
                if (disabled.Count > 0)
                {
                    try
                    {
                        (saveSettings ?? (s => ShellSettingsStore.Save(s)))(current);
                    }
                    catch (Exception ex)
                    {
                        persistenceError = ex.Message;
                    }
                }

                updatedSettings = current;
                disabledDefaults = disabled;
                return true;
            }

            if (disableOccupiedDefaults &&
                failure is not null &&
                failure.Value.IsOccupation &&
                current.IsDefault(failure.Value.Action))
            {
                var action = failure.Value.Action;
                var binding = failure.Value.Binding;
                disabled.Add(new DisabledDefaultHotkey(action, binding));

                current = current.WithHotkey(action, HotkeyBinding.UnsetBinding);
                continue;
            }

            // Registration failed (e.g. customized hotkey conflict or non-occupation failure).
            // Intermediate default disabling must be rolled back: no changes persisted to disk,
            // no disabled defaults reported, and updatedSettings matches the original settings
            // so it stays consistent with App._shellSettings.
            updatedSettings = settings;
            disabledDefaults = Array.Empty<DisabledDefaultHotkey>();
            return false;
        }
    }

    internal static string DescribeDisabledDefaultsFeedback(
        IReadOnlyList<DisabledDefaultHotkey> disabled,
        string? persistenceError = null)
    {
        if (disabled.Count == 0)
        {
            return string.Empty;
        }
        string baseNotice;
        if (disabled.Count == 1)
        {
            var item = disabled[0];
            var reason = !string.IsNullOrWhiteSpace(item.ConflictReason)
                ? item.ConflictReason
                : "已被系统或其他程序占用";
            baseNotice = $"{ShellSettings.ActionName(item.Action)}默认快捷键（{item.Binding.DisplayName}）{reason}，已自动停用以避免冲突。可在设置中重新绑定。";
        }
        else
        {
            if (disabled.All(d => string.IsNullOrWhiteSpace(d.ConflictReason)))
            {
                var items = string.Join("、", disabled.Select(d => $"{ShellSettings.ActionName(d.Action)}（{d.Binding.DisplayName}）"));
                baseNotice = $"{items}默认快捷键已被系统或其他程序占用，已自动停用以避免冲突。可在设置中重新绑定。";
            }
            else if (disabled.All(d => !string.IsNullOrWhiteSpace(d.ConflictReason)))
            {
                var items = string.Join("、", disabled.Select(d => $"{ShellSettings.ActionName(d.Action)}（{d.Binding.DisplayName}）"));
                baseNotice = $"{items}默认快捷键与自定义快捷键冲突，已自动停用以避免冲突。可在设置中重新绑定。";
            }
            else
            {
                var details = disabled.Select(d =>
                    $"{ShellSettings.ActionName(d.Action)}（{d.Binding.DisplayName}：{(!string.IsNullOrWhiteSpace(d.ConflictReason) ? d.ConflictReason : "已被系统或其他程序占用")}）");
                baseNotice = $"{string.Join("；", details)}，已自动停用以避免冲突。可在设置中重新绑定。";
            }
        }

        if (!string.IsNullOrWhiteSpace(persistenceError))
        {
            baseNotice += $" 注意：配置保存失败（{persistenceError}），下次启动时可能会恢复，请检查文件权限或在设置中手动停用。";
        }

        return baseNotice;
    }

    internal static string DescribeDisabledDefaultsFeedback(
        IReadOnlyList<(HotkeyAction Action, HotkeyBinding Binding)> disabled,
        string? persistenceError = null) =>
        DescribeDisabledDefaultsFeedback(
            disabled.Select(d => new DisabledDefaultHotkey(d.Action, d.Binding)).ToList(),
            persistenceError);

    /// <summary>
    /// Temporarily releases process-wide shortcuts while a recorder is
    /// listening. Otherwise pressing the old shortcut also dispatches its
    /// action (selection translation synthesizes Ctrl+C) before the recorder
    /// can accept the same combination.
    /// </summary>
    public bool SetSuspended(bool suspended, out string? conflict)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (suspended)
        {
            UnregisterAll();
            _suspended = true;
            conflict = null;
            return true;
        }

        if (!_suspended && _registered.Count > 0)
        {
            conflict = null;
            return true;
        }

        UnregisterAll();
        if (!RegisterSet(_current, out conflict, out _))
        {
            _suspended = true;
            _degraded = true;
            RegistrationFailed?.Invoke(this, conflict ?? "快捷键恢复失败");
            return false;
        }

        _suspended = false;
        conflict = null;
        ExitDegradedState();
        return true;
    }

    public void SetSuspended(bool suspended) => SetSuspended(suspended, out _);

    private bool RegisterSet(
        IReadOnlyDictionary<HotkeyAction, HotkeyBinding> hotkeys,
        out string? conflict,
        out HotkeyRegistrationFailure? failure)
    {
        var id = FirstHotkeyId;
        var newlyRegistered = new List<int>();
        foreach (var (action, binding) in hotkeys)
        {
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    id,
                    binding.Modifiers | NativeMethods.ModNoRepeat,
                    binding.VirtualKey))
            {
                // Read the failure reason BEFORE any other native call can
                // clobber the thread's last-error value.
                var win32Error = Marshal.GetLastWin32Error();
                // Roll back any partially registered hotkeys from this attempt
                foreach (var regId in newlyRegistered)
                {
                    NativeMethods.UnregisterHotKey(_source.Handle, regId);
                    _registered.Remove(regId);
                }
                conflict = DescribeRegistrationFailure(action, binding, win32Error);
                failure = new HotkeyRegistrationFailure(action, binding, win32Error);
                return false;
            }
            _registered[id] = action;
            newlyRegistered.Add(id);
            id++;
        }
        conflict = null;
        failure = null;
        return true;
    }

    /// <summary>
    /// Human-facing shortcut failure. Internal Win32 codes stay in the
    /// diagnostic layer; this text only names the affected action and the
    /// next fact a non-technical user can act on.
    /// </summary>
    internal static string DescribeRegistrationFailure(
        HotkeyAction action, HotkeyBinding binding, int win32Error) =>
        win32Error == ErrorHotkeyAlreadyRegistered
            ? $"{ShellSettings.ActionName(action)}：{binding.DisplayName} 可能已被其他程序占用"
            : $"{ShellSettings.ActionName(action)}：{binding.DisplayName} 暂时无法使用";

    private static readonly HotkeyBinding[] SelectionCandidates =
    [
        new(HotkeyBinding.ModAlt, 0x51), // Alt+Q
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x44), // Ctrl+Shift+D
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x44), // Ctrl+Alt+D
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x54), // Ctrl+Alt+T
        new(HotkeyBinding.ModAlt, 0x45), // Alt+E
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x45), // Ctrl+Alt+E
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x46), // Ctrl+Alt+F
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x57), // Ctrl+Shift+W
    ];

    private static readonly HotkeyBinding[] ScreenshotCandidates =
    [
        // Prefer Alt-free combinations first: pressing Alt dismisses Win32/browser
        // hover menus and tooltips before the second key completes.
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x20), // Ctrl+Shift+Space
        new(0, 0x70), // F1
        new(0, 0x71), // F2
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x58), // Ctrl+Shift+X
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x41), // Ctrl+Shift+A
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x53), // Ctrl+Shift+S
        new(0, 0x2C), // PrintScreen
        new(HotkeyBinding.ModAlt, 0x41), // Alt+A
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x41), // Ctrl+Alt+A
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x53), // Ctrl+Alt+S
        new(HotkeyBinding.ModAlt, 0x53), // Alt+S
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x43), // Ctrl+Alt+C
    ];

    private static readonly HotkeyBinding[] FixedRegionCandidates =
    [
        // Prefer Alt-free combinations first so games, subtitles, and hover
        // overlays never lose focus or activate menu bars before capture.
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x45), // Ctrl+Shift+E
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x32), // Ctrl+Shift+2
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x33), // Ctrl+Shift+3
        new(0, 0x76), // F7
        new(0, 0x77), // F8
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x42), // Ctrl+Shift+B
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x4A), // Ctrl+Shift+J
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x4B), // Ctrl+Shift+K
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x47), // Ctrl+Alt+G
    ];

    private static readonly HotkeyBinding[] QuickSearchCandidates =
    [
        new(HotkeyBinding.ModAlt, 0x20), // Alt+Space
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x51), // Ctrl+Shift+Q
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x47), // Ctrl+Alt+G
        new(HotkeyBinding.ModAlt, 0x44), // Alt+D
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x4B), // Ctrl+Alt+K
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x4A), // Ctrl+Alt+J
    ];

    private static readonly HotkeyBinding[] ShowWindowCandidates =
    [
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x4D), // Ctrl+Alt+M
        new(HotkeyBinding.ModControl | HotkeyBinding.ModShift, 0x4F), // Ctrl+Shift+O
        new(HotkeyBinding.ModAlt, 0x4F), // Alt+O
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x48), // Ctrl+Alt+H
    ];

    private static readonly HotkeyBinding[] ClosePanelCandidates =
    [
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x5A), // Ctrl+Alt+Z
        new(HotkeyBinding.ModAlt, 0x58), // Alt+X
        new(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, 0x42), // Ctrl+Alt+B
    ];

    private static IEnumerable<HotkeyBinding> FallbackGlobalCandidates()
    {
        for (uint vk = 0x70; vk <= 0x7A; vk++) // F1..F11 (avoid F12 which is debugger-reserved on Windows)
        {
            yield return new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, vk);
        }
        for (uint vk = 0x70; vk <= 0x7A; vk++)
        {
            yield return new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModShift, vk);
        }
        for (uint vk = 0x31; vk <= 0x39; vk++) // 1..9
        {
            yield return new HotkeyBinding(HotkeyBinding.ModControl | HotkeyBinding.ModAlt, vk);
        }
    }

    private static IEnumerable<HotkeyBinding> InterfaceCandidates()
    {
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x4D); // Ctrl+M
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x54); // Ctrl+T
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x4C); // Ctrl+L
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x50); // Ctrl+P
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x4B); // Ctrl+K
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x4A); // Ctrl+J
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x47); // Ctrl+G
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x45); // Ctrl+E
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x44); // Ctrl+D
        yield return new HotkeyBinding(HotkeyBinding.ModControl, 0x0D); // Ctrl+Enter
        for (uint vk = 0x31; vk <= 0x39; vk++)
        {
            yield return new HotkeyBinding(HotkeyBinding.ModControl, vk);
        }
        for (uint vk = 0x70; vk <= 0x7A; vk++)
        {
            yield return new HotkeyBinding(HotkeyBinding.ModControl, vk);
        }
    }

    /// <summary>
    /// Probes whether a global shortcut is currently available on Windows.
    /// Bindings already owned by this process (<paramref name="ownedByApp"/>)
    /// count as available so PopGlot does not flag its own active hotkeys.
    /// </summary>
    internal static bool IsBindingAvailable(
        HotkeyBinding binding,
        IReadOnlyCollection<HotkeyBinding>? ownedByApp = null)
    {
        if (!binding.IsValid)
        {
            return false;
        }
        if (ownedByApp is not null && ownedByApp.Any(owned =>
                !owned.IsUnset &&
                string.Equals(owned.DisplayName, binding.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        const int probeId = 0x50FE;
        var parameters = new HwndSourceParameters("PopGlotHotkeyProbe")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3), // HWND_MESSAGE: hidden message-only window
        };
        using var probe = new HwndSource(parameters);
        if (probe.Handle == 0)
        {
            return true;
        }
        if (NativeMethods.RegisterHotKey(probe.Handle, probeId, binding.Modifiers | NativeMethods.ModNoRepeat, binding.VirtualKey))
        {
            NativeMethods.UnregisterHotKey(probe.Handle, probeId);
            return true;
        }
        return Marshal.GetLastWin32Error() != ErrorHotkeyAlreadyRegistered;
    }

    internal static HotkeyBinding? FindFirstFreeAlternative(
        HotkeyAction action,
        ShellSettings draft,
        IReadOnlyCollection<HotkeyBinding>? ownedByApp = null,
        Func<HotkeyBinding, bool>? probeAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var usedNames = CollectAllBindingNames(draft);
        var primary = action switch
        {
            HotkeyAction.TranslateSelection => SelectionCandidates,
            HotkeyAction.CaptureScreen => ScreenshotCandidates,
            HotkeyAction.CaptureFixedRegion => FixedRegionCandidates,
            HotkeyAction.QuickSearch => QuickSearchCandidates,
            HotkeyAction.ShowWindow => ShowWindowCandidates,
            HotkeyAction.ClosePanel => ClosePanelCandidates,
            _ => SelectionCandidates,
        };
        var checker = probeAvailable ?? (b => IsBindingAvailable(b, ownedByApp));
        foreach (var candidate in primary.Concat(FallbackGlobalCandidates()))
        {
            if (!candidate.IsValid ||
                InterfaceKeys.IsFixedAction(candidate) ||
                usedNames.Contains(candidate.DisplayName))
            {
                continue;
            }
            if (checker(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    internal static HotkeyBinding? FindFirstFreeInterfaceAlternative(ShellSettings draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var usedNames = CollectAllBindingNames(draft);
        foreach (var candidate in InterfaceCandidates())
        {
            if (!candidate.IsValid ||
                InterfaceKeys.IsReserved(candidate) ||
                usedNames.Contains(candidate.DisplayName))
            {
                continue;
            }
            return candidate;
        }
        return null;
    }

    internal static IReadOnlyList<ShortcutIssue> DiagnoseDraft(
        ShellSettings draft,
        IReadOnlyCollection<HotkeyBinding>? ownedByApp = null,
        bool probeSystem = true,
        Func<HotkeyBinding, bool>? probeAvailable = null,
        string? preferredFixSlotKey = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<ShortcutIssue>();
        var checker = probeAvailable ?? (b => IsBindingAvailable(b, ownedByApp));

        var slots = new (string Key, string Label, HotkeyAction? GlobalAction, HotkeyBinding Binding)[]
        {
            ("Selection", ShellSettings.ActionName(HotkeyAction.TranslateSelection), HotkeyAction.TranslateSelection, draft.SelectionHotkey),
            ("Screenshot", ShellSettings.ActionName(HotkeyAction.CaptureScreen), HotkeyAction.CaptureScreen, draft.ScreenshotHotkey),
            ("FixedRegion", ShellSettings.ActionName(HotkeyAction.CaptureFixedRegion), HotkeyAction.CaptureFixedRegion, draft.FixedRegionHotkey),
            ("QuickSearch", ShellSettings.ActionName(HotkeyAction.QuickSearch), HotkeyAction.QuickSearch, draft.QuickSearchHotkey),
            ("ShowWindow", ShellSettings.ActionName(HotkeyAction.ShowWindow), HotkeyAction.ShowWindow, draft.ShowWindowHotkey),
            ("Close", ShellSettings.ActionName(HotkeyAction.ClosePanel), HotkeyAction.ClosePanel, draft.CloseHotkey),
            ("InterfaceModel", "切换模型", null, draft.InterfaceModelHotkey),
            ("InterfaceStyle", "切换风格", null, draft.InterfaceStyleHotkey),
            ("InterfaceSwap", "互换语言", null, draft.InterfaceSwapHotkey),
            ("InterfaceTranslate", "发起翻译", null, draft.InterfaceTranslateHotkey),
            ("InterfaceSpeak", "朗读译文", null, draft.InterfaceSpeakHotkey),
            ("InterfaceWorkbench", "打开工作台", null, draft.InterfaceWorkbenchHotkey),
            ("InterfaceLibrary", "打开资料库", null, draft.InterfaceLibraryHotkey),
            ("InterfaceSettings", "打开设置", null, draft.InterfaceSettingsHotkey),
        };

        var activeSlots = slots.Where(s => !s.Binding.IsUnset).ToList();
        var counts = activeSlots
            .GroupBy(s => s.Binding.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Label).ToList(), StringComparer.OrdinalIgnoreCase);

        HotkeyBinding? SuggestFor((string Key, string Label, HotkeyAction? GlobalAction, HotkeyBinding Binding) slot) =>
            slot.GlobalAction is { } ga
                ? FindFirstFreeAlternative(ga, draft, ownedByApp, checker)
                : FindFirstFreeInterfaceAlternative(draft);

        foreach (var slot in activeSlots)
        {
            if (!slot.Binding.IsValid)
            {
                issues.Add(new ShortcutIssue(
                    slot.Key,
                    slot.Label,
                    slot.GlobalAction,
                    slot.Binding,
                    ShortcutIssueKind.Invalid,
                    $"{slot.Label}的「{slot.Binding.DisplayName}」无效，需包含 Ctrl、Alt、Win，或使用 F1–F12 / PrintScreen",
                    SuggestFor(slot)));
                continue;
            }

            if (counts.TryGetValue(slot.Binding.DisplayName, out var dupLabels) && dupLabels.Count > 1)
            {
                var others = string.Join("、", dupLabels.Where(l => l != slot.Label));
                issues.Add(new ShortcutIssue(
                    slot.Key,
                    slot.Label,
                    slot.GlobalAction,
                    slot.Binding,
                    ShortcutIssueKind.Duplicate,
                    $"{slot.Binding.DisplayName} 与「{others}」重复",
                    SuggestFor(slot)));
                continue;
            }

            var isReserved = slot.GlobalAction is not null
                ? InterfaceKeys.IsFixedAction(slot.Binding)
                : InterfaceKeys.IsReserved(slot.Binding);
            if (isReserved)
            {
                issues.Add(new ShortcutIssue(
                    slot.Key,
                    slot.Label,
                    slot.GlobalAction,
                    slot.Binding,
                    ShortcutIssueKind.Reserved,
                    $"{slot.Label}的「{slot.Binding.DisplayName}」与系统编辑或保留快捷键冲突",
                    SuggestFor(slot)));
                continue;
            }

            if (probeSystem && slot.GlobalAction is { } action && !checker(slot.Binding))
            {
                var suggestion = FindFirstFreeAlternative(action, draft, ownedByApp, checker);
                var message = suggestion is not null
                    ? $"「{slot.Label}」的 {slot.Binding.DisplayName} 已被其他软件占用"
                    : $"「{slot.Label}」的 {slot.Binding.DisplayName} 已被其他软件占用，建议更换组合";
                issues.Add(new ShortcutIssue(
                    slot.Key,
                    slot.Label,
                    action,
                    slot.Binding,
                    ShortcutIssueKind.SystemOccupied,
                    message,
                    suggestion));
            }
        }

        if (!string.IsNullOrEmpty(preferredFixSlotKey) && issues.Count > 1)
        {
            var preferredIndex = issues.FindIndex(i => i.SlotKey == preferredFixSlotKey);
            if (preferredIndex > 0)
            {
                var preferred = issues[preferredIndex];
                issues.RemoveAt(preferredIndex);
                issues.Insert(0, preferred);
            }
        }

        return issues;
    }

    private static HashSet<string> CollectAllBindingNames(ShellSettings draft)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(HotkeyBinding binding)
        {
            if (!binding.IsUnset && binding.IsValid)
            {
                names.Add(binding.DisplayName);
            }
        }
        Add(draft.SelectionHotkey);
        Add(draft.ScreenshotHotkey);
        Add(draft.FixedRegionHotkey);
        Add(draft.QuickSearchHotkey);
        Add(draft.ShowWindowHotkey);
        Add(draft.CloseHotkey);
        Add(draft.InterfaceModelHotkey);
        Add(draft.InterfaceStyleHotkey);
        Add(draft.InterfaceSwapHotkey);
        Add(draft.InterfaceTranslateHotkey);
        Add(draft.InterfaceSpeakHotkey);
        Add(draft.InterfaceWorkbenchHotkey);
        Add(draft.InterfaceLibraryHotkey);
        Add(draft.InterfaceSettingsHotkey);
        return names;
    }

    /// <summary>
    /// Leaves the degraded state when a full set is live again, firing
    /// <see cref="RegistrationRestored"/> exactly once per degraded period.
    /// </summary>
    private void ExitDegradedState()
    {
        if (!_degraded)
        {
            return;
        }
        _degraded = false;
        RegistrationRestored?.Invoke(this, EventArgs.Empty);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!_suspended && message == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }
        return 0;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        }
        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        UnregisterAll();
        _source.RemoveHook(WindowMessageHook);
        _disposed = true;
    }

    private static partial class NativeMethods
    {
        internal const uint ModNoRepeat = 0x4000;

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterHotKey(nint hwnd, int id);
    }
}
