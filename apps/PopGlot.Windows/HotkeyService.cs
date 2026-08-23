using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PopGlot.Windows;

internal sealed partial class HotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x5047;
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private IReadOnlyDictionary<HotkeyAction, ShortcutOption> _current =
        new Dictionary<HotkeyAction, ShortcutOption>();
    private bool _disposed;

    public HotkeyService(Window owner)
    {
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("Unable to attach the PopGlot hotkey window hook.");
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler<HotkeyAction>? Pressed;

    public bool TryRegisterAll(
        IReadOnlyDictionary<HotkeyAction, ShortcutOption> hotkeys,
        out string? conflict)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        conflict = null;
        var previous = _current;
        UnregisterAll();
        if (RegisterSet(hotkeys, out conflict))
        {
            _current = new Dictionary<HotkeyAction, ShortcutOption>(hotkeys);
            return true;
        }

        UnregisterAll();
        _ = RegisterSet(previous, out _);
        _current = previous;
        return false;
    }

    private bool RegisterSet(
        IReadOnlyDictionary<HotkeyAction, ShortcutOption> hotkeys,
        out string? conflict)
    {
        var id = FirstHotkeyId;
        foreach (var (action, shortcut) in hotkeys)
        {
            if (!NativeMethods.RegisterHotKey(
                    _source.Handle,
                    id,
                    shortcut.Modifiers | NativeMethods.ModNoRepeat,
                    shortcut.VirtualKey))
            {
                conflict = shortcut.DisplayName;
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
