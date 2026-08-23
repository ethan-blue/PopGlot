using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PopGlot.Windows;

internal sealed partial class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5047;
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public HotkeyService(Window owner)
    {
        var handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle)
            ?? throw new InvalidOperationException("Unable to attach the PopGlot hotkey window hook.");
        _source.AddHook(WindowMessageHook);
    }

    public event EventHandler? Pressed;

    public bool Register(ShortcutOption shortcut)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _registered = NativeMethods.RegisterHotKey(
            _source.Handle,
            HotkeyId,
            shortcut.Modifiers | NativeMethods.ModNoRepeat,
            shortcut.VirtualKey);
        return _registered;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        }
        _source.RemoveHook(WindowMessageHook);
        _registered = false;
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
