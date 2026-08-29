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
            return true;
        }

        UnregisterAll();
        _ = RegisterSet(previous, out _);
        _current = previous;
        return false;
    }

    private bool RegisterSet(
        IReadOnlyDictionary<HotkeyAction, HotkeyBinding> hotkeys,
        out string? conflict)
    {
        var id = FirstHotkeyId;
        foreach (var (action, binding) in hotkeys)
        {
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    id,
                    binding.Modifiers | NativeMethods.ModNoRepeat,
                    binding.VirtualKey))
            {
                conflict =
                    $"{ShellSettings.ActionName(action)}：{binding.DisplayName} 已被其他程序占用";
                return false;
            }
            _registered[id] = action;
            id++;
        }
        conflict = null;
        return true;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
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
