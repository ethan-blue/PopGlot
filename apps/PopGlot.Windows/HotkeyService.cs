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
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private IReadOnlyDictionary<HotkeyAction, HotkeyBinding> _current =
        new Dictionary<HotkeyAction, HotkeyBinding>();
    private bool _suspended;
    private bool _disposed;

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

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> CurrentHotkeys => _current;
    public bool IsSuspended => _suspended;
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
            return true;
        }

        UnregisterAll();
        _current = previous;
        if (!RegisterSet(previous, out var restoreConflict))
        {
            _suspended = true;
            var detail = $"{conflict}。且恢复原快捷键也失败（{restoreConflict}）";
            RegistrationFailed?.Invoke(this, detail);
            return false;
        }
        _suspended = false;
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
            RegistrationFailed?.Invoke(this, conflict ?? "快捷键恢复失败");
            return false;
        }

        _suspended = false;
        conflict = null;
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
                // Roll back any partially registered hotkeys from this attempt
                foreach (var regId in newlyRegistered)
                {
                    NativeMethods.UnregisterHotKey(_source.Handle, regId);
                    _registered.Remove(regId);
                }
                conflict =
                    $"{ShellSettings.ActionName(action)}：{binding.DisplayName} 已被其他程序占用";
                return false;
            }
            _registered[id] = action;
            newlyRegistered.Add(id);
            id++;
        }
        conflict = null;
        return true;
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
