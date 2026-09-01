using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace PopGlot.Windows;

internal interface IClipboardSnapshot : IDisposable;

internal interface ISelectionClipboardAdapter
{
    uint SequenceNumber { get; }
    Task<IClipboardSnapshot> CaptureAsync();
    Task SendCopyAsync();
    Task<string?> ReadTextAsync();
    Task RestoreAsync(IClipboardSnapshot snapshot);
}

internal sealed class ClipboardSelectionService
{
    internal const int MaxSelectedCharacters = 64 * 1024;
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(1000);
    private readonly ISelectionClipboardAdapter _clipboard;

    public ClipboardSelectionService(ISelectionClipboardAdapter clipboard)
    {
        _clipboard = clipboard;
    }

    public async Task<string> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = await _clipboard.CaptureAsync();
        var sequenceBeforeCopy = _clipboard.SequenceNumber;
        uint? copiedSequence = null;
        var cancellationRequested = false;
        try
        {
            await _clipboard.SendCopyAsync();
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

            var text = (await _clipboard.ReadTextAsync())?.Trim();
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
                await _clipboard.RestoreAsync(snapshot);
            }
        }
    }
}

internal sealed partial class WindowsSelectionClipboardAdapter : ISelectionClipboardAdapter
{
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkC = 0x43;
    private const int ClipboardAttempts = 6;

    private static readonly TimeSpan ClipboardOperationTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly SemaphoreSlim ClipboardWorkerGate = new(1, 1);

    internal static int InputStructureSize => Marshal.SizeOf<NativeInput>();

    public uint SequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    public Task<IClipboardSnapshot> CaptureAsync() => RetryClipboardAsync(() =>
        RunInStaAsync<IClipboardSnapshot>(ClipboardSnapshot.Capture, ClipboardOperationTimeout));

    public async Task SendCopyAsync()
    {
        await WaitForModifiersReleasedAsync();
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

    /// <summary>
    /// The user may still be physically holding Ctrl/Alt from the hotkey that
    /// triggered this copy. Sending C while they are held produces Ctrl+Alt+C,
    /// which most applications ignore, so wait briefly for the modifiers to
    /// come up before synthesizing Ctrl+C. Awaits instead of sleeping so the
    /// UI thread stays responsive during the wait.
    /// </summary>
    private static async Task WaitForModifiersReleasedAsync()
    {
        var deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline)
        {
            var pressed =
                (NativeMethods.GetAsyncKeyState(VkShift) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(VkControl) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(VkMenu) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(VkLWin) & 0x8000) != 0 ||
                (NativeMethods.GetAsyncKeyState(VkRWin) & 0x8000) != 0;
            if (!pressed)
            {
                return;
            }
            await Task.Delay(10);
        }
    }

    public Task<string?> ReadTextAsync() => RetryClipboardAsync(() =>
        RunInStaAsync(() =>
            Clipboard.ContainsText(TextDataFormat.UnicodeText)
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : null,
            ClipboardOperationTimeout));

    public Task RestoreAsync(IClipboardSnapshot snapshot)
    {
        if (snapshot is not ClipboardSnapshot clipboardSnapshot)
        {
            throw new ArgumentException("Unsupported clipboard snapshot.", nameof(snapshot));
        }
        return RetryClipboardAsync(() => RunInStaAsync(() =>
        {
            clipboardSnapshot.Restore();
            return true;
        }, ClipboardOperationTimeout));
    }

    internal static async Task<T> RunInStaAsync<T>(Func<T> action, TimeSpan timeout)
    {
        // A timed-out OLE call cannot be cancelled safely in-process. Keep the
        // gate owned by the worker until the native call really returns so
        // repeated hotkeys can never accumulate abandoned STA threads.
        if (!await ClipboardWorkerGate.WaitAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false))
        {
            throw new InvalidOperationException("上一次剪贴板操作仍未响应，已取消本次划词。请关闭占用剪贴板的程序后重试。");
        }

        var workerStarted = false;
        try
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // If the caller has already timed out, still observe a later worker
            // fault so an abandoned OLE provider cannot surface as an
            // UnobservedTaskException during finalization.
            _ = tcs.Task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    ClipboardWorkerGate.Release();
                }
            })
            {
                IsBackground = true,
                Name = "PopGlot-Clipboard-Worker",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            workerStarted = true;

            using var cts = new CancellationTokenSource();
            var delayTask = Task.Delay(timeout, cts.Token);
            var completedTask = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);

            if (completedTask == tcs.Task)
            {
                cts.Cancel();
                return await tcs.Task.ConfigureAwait(false);
            }

            throw new TimeoutException($"剪贴板操作在 {timeout.TotalMilliseconds}ms 内未响应。可能由于其他应用（如远程桌面或 Office 延迟渲染）未响应。");
        }
        finally
        {
            // Once started, only the worker may release the gate. Releasing it
            // on caller timeout would permit another permanently blocked STA.
            if (!workerStarted)
            {
                ClipboardWorkerGate.Release();
            }
        }
    }

    private static async Task<T> RetryClipboardAsync<T>(Func<Task<T>> operation)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < ClipboardAttempts; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Never retry on hard timeout: fail immediately to avoid stacking hung threads
                throw;
            }
            catch (Exception exception) when (
                exception is COMException or ExternalException)
            {
                lastError = exception;
                await Task.Delay(15 * (attempt + 1)).ConfigureAwait(false);
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

        [LibraryImport("user32.dll")]
        internal static partial short GetAsyncKeyState(int virtualKey);

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
        // Fail-closed: Never swallow exceptions when inspecting the clipboard.
        // If GetDataObject or GetFormats fails, throw to abort selection translation
        // BEFORE sending synthetic Ctrl+C. Returning an empty snapshot on failure
        // would cause Restore() to call Clipboard.Clear(), wiping the user's data!
        var source = Clipboard.GetDataObject();
        if (source is null)
        {
            return new ClipboardSnapshot([]);
        }

        var formatNames = source.GetFormats(autoConvert: false);
        if (formatNames is null || formatNames.Length == 0)
        {
            return new ClipboardSnapshot([]);
        }

        var formats = new List<(string Format, object Data)>(formatNames.Length);
        foreach (var format in formatNames)
        {
            var data = source.GetData(format, autoConvert: false);
            if (data is null)
            {
                throw new InvalidOperationException(
                    $"剪贴板格式“{format}”无法完整读取；为保护原内容，本次划词已取消。");
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
        Drawing.Bitmap bitmap => bitmap.Clone(),
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
