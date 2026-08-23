using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using TextDataFormat = System.Windows.TextDataFormat;

namespace PopGlot.Windows;

internal interface IClipboardSnapshot : IDisposable;

internal interface ISelectionClipboardAdapter
{
    uint SequenceNumber { get; }
    IClipboardSnapshot Capture();
    void SendCopy();
    string? ReadText();
    void Restore(IClipboardSnapshot snapshot);
}

internal sealed class ClipboardSelectionService
{
    internal const int MaxSelectedCharacters = 64 * 1024;
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(450);
    private readonly ISelectionClipboardAdapter _clipboard;

    public ClipboardSelectionService(ISelectionClipboardAdapter clipboard)
    {
        _clipboard = clipboard;
    }

    public async Task<string> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = _clipboard.Capture();
        var sequenceBeforeCopy = _clipboard.SequenceNumber;
        uint? copiedSequence = null;
        var cancellationRequested = false;
        try
        {
            _clipboard.SendCopy();
            var deadline = DateTime.UtcNow + CopyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_clipboard.SequenceNumber != sequenceBeforeCopy)
                {
                    copiedSequence = _clipboard.SequenceNumber;
                    break;
                }
                cancellationRequested |= cancellationToken.IsCancellationRequested;
                // Once Ctrl+C was sent, finish observing the bounded clipboard
                // transaction even after cancellation so a delayed copy cannot
                // escape restoration.
                await Task.Delay(15);
            }

            if (copiedSequence is null)
            {
                if (cancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new InvalidOperationException("未检测到可复制的选中文本。请先选中文字，再按划词快捷键。");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var text = _clipboard.ReadText()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("选区没有可用文本，或当前应用禁止复制。");
            }
            if (Encoding.UTF8.GetByteCount(text) > MaxSelectedCharacters)
            {
                throw new InvalidOperationException("选中文本超过 64 KiB，请缩小选区。");
            }
            if (text.Contains('\0'))
            {
                throw new InvalidOperationException("选中文本包含不支持的 NUL 字符。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return text;
        }
        finally
        {
            // Restore only if our synthetic copy is still the newest clipboard
            // write. A real user write during the transaction always wins.
            if (copiedSequence is not null && _clipboard.SequenceNumber == copiedSequence.Value)
            {
                _clipboard.Restore(snapshot);
            }
        }
    }
}

internal sealed partial class WindowsSelectionClipboardAdapter : ISelectionClipboardAdapter
{
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const int ClipboardAttempts = 6;

    internal static int InputStructureSize => Marshal.SizeOf<NativeInput>();

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public IClipboardSnapshot Capture() => RetryClipboard(() => ClipboardSnapshot.Capture());

    public void SendCopy()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0),
            KeyboardInput(VkC, 0),
            KeyboardInput(VkC, KeyeventfKeyup),
            KeyboardInput(VkControl, KeyeventfKeyup),
        };
        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            InputStructureSize);
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法向当前应用发送复制快捷键。");
        }
    }

    public string? ReadText() => RetryClipboard(() =>
        Clipboard.ContainsText(TextDataFormat.UnicodeText)
            ? Clipboard.GetText(TextDataFormat.UnicodeText)
            : null);

    public void Restore(IClipboardSnapshot snapshot)
    {
        if (snapshot is not ClipboardSnapshot clipboardSnapshot)
        {
            throw new ArgumentException("Unsupported clipboard snapshot.", nameof(snapshot));
        }
        RetryClipboard(() =>
        {
            clipboardSnapshot.Restore();
            return true;
        });
    }

    private static T RetryClipboard<T>(Func<T> operation)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                return operation();
            }
            catch (COMException exception)
            {
                lastError = exception;
                Thread.Sleep(12 * (attempt + 1));
            }
        }
        throw new InvalidOperationException("剪贴板正被其他应用占用，请稍后重试。", lastError);
    }

    private static NativeInput KeyboardInput(ushort virtualKey, uint flags) => new()
    {
        Type = 1,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = flags,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeHardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeHardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial uint GetClipboardSequenceNumber();

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
    }
}

internal sealed class ClipboardSnapshot : IClipboardSnapshot
{
    private readonly IReadOnlyList<(string Format, object Data)> _formats;
    private bool _disposed;

    private ClipboardSnapshot(IReadOnlyList<(string Format, object Data)> formats)
    {
        _formats = formats;
    }

    public static ClipboardSnapshot Capture()
    {
        var source = Clipboard.GetDataObject();
        if (source is null)
        {
            return new ClipboardSnapshot([]);
        }

        var formats = new List<(string Format, object Data)>();
        foreach (var format in source.GetFormats(autoConvert: false))
        {
            var data = source.GetData(format, autoConvert: false);
            if (data is null)
            {
                continue;
            }
            formats.Add((format, CloneClipboardValue(data, format)));
        }
        return new ClipboardSnapshot(formats);
    }

    public void Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_formats.Count == 0)
        {
            Clipboard.Clear();
            return;
        }
        var restored = new DataObject();
        foreach (var (format, data) in _formats)
        {
            restored.SetData(format, data, autoConvert: false);
        }
        Clipboard.SetDataObject(restored, copy: true);
    }

    private static object CloneClipboardValue(object data, string format) => data switch
    {
        string text => text,
        string[] paths => paths.ToArray(),
        byte[] bytes => bytes.ToArray(),
        MemoryStream stream => new MemoryStream(stream.ToArray(), writable: false),
        Bitmap bitmap => bitmap.Clone(),
        BitmapSource image => CloneBitmapSource(image),
        _ when data.GetType().IsValueType => data,
        _ => throw new InvalidOperationException(
            $"剪贴板包含暂不支持安全复制的格式“{format}”；为保护原内容，本次划词已取消。"),
    };

    private static BitmapSource CloneBitmapSource(BitmapSource source)
    {
        var clone = new WriteableBitmap(source);
        clone.Freeze();
        return clone;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        foreach (var (_, data) in _formats)
        {
            if (data is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _disposed = true;
    }
}
