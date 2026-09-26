using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PopGlot.Windows;

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
        out string? conflict)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(hotkeys);

        var previous = _current;
        UnregisterAll();
        if (RegisterSet(hotkeys, out conflict))
        {
            _current = new Dictionary<HotkeyAction, HotkeyBinding>(hotkeys);
            _suspended = false;
            ExitDegradedState();
            return true;
        }

        UnregisterAll();
        _current = previous;
        if (!RegisterSet(previous, out var restoreConflict))
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
        if (!RegisterSet(_current, out conflict))
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
        out string? conflict)
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
                return false;
            }
            _registered[id] = action;
            newlyRegistered.Add(id);
            id++;
        }
        conflict = null;
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
