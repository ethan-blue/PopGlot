using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PopGlot.Windows.Services;

/// <summary>
/// Stream state lifecycle for translation streaming sessions.
/// </summary>
public enum TranslationStreamState
{
    Active = 0,
    Completed = 1,
    Aborted = 2,
    Reset = 3,
    Disposed = 4,
}

/// <summary>
/// Event types emitted or tracked by the translation streaming buffer.
/// </summary>
public enum TranslationStreamEventType
{
    TextDelta = 0,
    Reset = 1,
    Completed = 2,
    Aborted = 3,
    Error = 4,
}

/// <summary>
/// A lightweight representation of a stream event.
/// </summary>
public readonly record struct TranslationStreamEvent(
    TranslationStreamEventType Type,
    string? Text = null,
    string? ErrorMessage = null,
    long DeltaIndex = 0);

/// <summary>
/// Snapshot result returned when draining pending delta chunks.
/// </summary>
public readonly record struct TranslationStreamDrainBatch(
    string Text,
    long DeltaCount,
    long AccumulatedCharCount,
    long FlushIndex,
    TranslationStreamState State,
    bool HasMore);

/// <summary>
/// Thread-safe, bounded streaming buffer and session event component for translation deltas.
/// <para>
/// Meets spec 2.4 / 2.9:
/// - O(1) synchronous TryAppend / TryAppendUtf8 non-blocking entry points suitable for native callbacks.
/// - Merges consecutive deltas into a pending StringBuilder under short lock; never drops characters silently.
/// - Hard character and byte limits with abort return value for native backpressure signaling.
/// - Consumer can periodically and atomically Drain pending deltas; final drain guarantees zero tail loss.
/// - SessionId, RequestId, and Epoch fencing to prevent stale stream pollution.
/// - High-precision metrics: delta count, char count, byte count, flush count, first-delta timestamp / TTFT calculation.
/// - Idempotent Complete / Abort / Reset / Dispose lifecycle.
/// </para>
/// </summary>
public sealed class TranslationStreamBuffer : IDisposable
{
    public const long DefaultMaxChars = 1_000_000;
    public const long DefaultMaxBytes = 4 * 1024 * 1024; // 4MB

    private readonly object _lock = new();
    private readonly StringBuilder _pendingBuilder;
    private readonly StringBuilder _accumulatedBuilder;
    private string? _accumulatedCache;

    private TranslationStreamState _state = TranslationStreamState.Active;
    private string? _abortReason;

    private long _deltaCount;
    private long _charCount;
    private long _byteCount;
    private long _flushCount;
    private long _firstDeltaTimestampTicks;

    public string SessionId { get; }
    public string RequestId { get; }
    public long Epoch { get; }
    public long MaxChars { get; }
    public long MaxBytes { get; }

    public TranslationStreamBuffer(
        string sessionId,
        string? requestId = null,
        long epoch = 0,
        long maxChars = DefaultMaxChars,
        long maxBytes = DefaultMaxBytes)
    {
        SessionId = sessionId ?? string.Empty;
        RequestId = requestId ?? string.Empty;
        Epoch = epoch;
        MaxChars = maxChars > 0 ? maxChars : DefaultMaxChars;
        MaxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;

        _pendingBuilder = new StringBuilder(256);
        _accumulatedBuilder = new StringBuilder(256);
    }

    public TranslationStreamState State
    {
        get { lock (_lock) return _state; }
    }

    public string? AbortReason
    {
        get { lock (_lock) return _abortReason; }
    }

    public bool IsActive
    {
        get { lock (_lock) return _state == TranslationStreamState.Active; }
    }

    public bool IsCompleted
    {
        get { lock (_lock) return _state == TranslationStreamState.Completed; }
    }

    public bool IsAborted
    {
        get { lock (_lock) return _state == TranslationStreamState.Aborted; }
    }

    public bool IsDisposed
    {
        get { lock (_lock) return _state == TranslationStreamState.Disposed; }
    }

    public long DeltaCount
    {
        get { lock (_lock) return _deltaCount; }
    }

    public long CharCount
    {
        get { lock (_lock) return _charCount; }
    }

    public long ByteCount
    {
        get { lock (_lock) return _byteCount; }
    }

    public long FlushCount
    {
        get { lock (_lock) return _flushCount; }
    }

    public bool HasFirstDelta
    {
        get { lock (_lock) return _firstDeltaTimestampTicks > 0; }
    }

    public long FirstDeltaTimestampTicks
    {
        get { lock (_lock) return _firstDeltaTimestampTicks; }
    }

    public int PendingCharCount
    {
        get { lock (_lock) return _pendingBuilder.Length; }
    }

    public bool HasPending
    {
        get { lock (_lock) return _pendingBuilder.Length > 0; }
    }

    /// <summary>
    /// Checks whether this buffer matches the specified session and epoch fence.
    /// </summary>
    public bool IsSessionMatch(string sessionId, long epoch)
    {
        return string.Equals(SessionId, sessionId, StringComparison.Ordinal) && Epoch == epoch;
    }

    /// <summary>
    /// Synchronously appends string delta. Returns false if stream is closed or hard limits are exceeded.
    /// Non-blocking, O(1) lock duration, safe for native callback invocation.
    /// </summary>
    public bool TryAppend(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            lock (_lock)
            {
                return _state == TranslationStreamState.Active;
            }
        }

        return TryAppend(text.AsSpan());
    }

    /// <summary>
    /// Synchronously appends character span. Returns false if stream is closed or hard limits are exceeded.
    /// Non-blocking, zero-allocation, O(1) lock duration.
    /// </summary>
    public bool TryAppend(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            lock (_lock)
            {
                return _state == TranslationStreamState.Active;
            }
        }

        int byteCount = Encoding.UTF8.GetByteCount(chars);
        return TryAppendCore(chars, byteCount);
    }

    /// <summary>
    /// Synchronously appends UTF-8 bytes span directly. Returns false if stream is closed or hard limits are exceeded.
    /// Non-blocking, stackalloc-optimized, O(1) lock duration.
    /// </summary>
    public bool TryAppendUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            lock (_lock)
            {
                return _state == TranslationStreamState.Active;
            }
        }

        int charCount = Encoding.UTF8.GetCharCount(utf8);
        if (charCount <= 256)
        {
            Span<char> chars = stackalloc char[charCount];
            Encoding.UTF8.GetChars(utf8, chars);
            return TryAppendCore(chars, utf8.Length);
        }
        else
        {
            char[] rented = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                int written = Encoding.UTF8.GetChars(utf8, rented);
                return TryAppendCore(rented.AsSpan(0, written), utf8.Length);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// Synchronously appends UTF-8 bytes from native unmanaged pointer.
    /// Safe for direct FFI callback usage.
    /// </summary>
    public unsafe bool TryAppendUtf8(IntPtr utf8Ptr, int byteLen)
    {
        if (utf8Ptr == IntPtr.Zero || byteLen <= 0)
        {
            lock (_lock)
            {
                return _state == TranslationStreamState.Active;
            }
        }

        var span = new ReadOnlySpan<byte>((void*)utf8Ptr, byteLen);
        return TryAppendUtf8(span);
    }

    private bool TryAppendCore(ReadOnlySpan<char> chars, int byteCount)
    {
        lock (_lock)
        {
            if (_state != TranslationStreamState.Active)
            {
                return false;
            }

            if (_charCount + chars.Length > MaxChars || _byteCount + byteCount > MaxBytes)
            {
                _state = TranslationStreamState.Aborted;
                _abortReason = $"Hard limit exceeded (maxChars={MaxChars}, maxBytes={MaxBytes})";
                return false;
            }

            if (_firstDeltaTimestampTicks == 0)
            {
                _firstDeltaTimestampTicks = Stopwatch.GetTimestamp();
            }

            _pendingBuilder.Append(chars);
            _accumulatedBuilder.Append(chars);
            // Invalidate the lazy snapshot so GetAccumulatedText rebuilds it.
            _accumulatedCache = null;
            _deltaCount++;
            _charCount += chars.Length;
            _byteCount += byteCount;
            return true;
        }
    }

    /// <summary>
    /// Atomically drains and clears all pending text accumulated since last drain.
    /// Returns string.Empty if no new text is pending.
    /// </summary>
    public string DrainText()
    {
        lock (_lock)
        {
            if (_pendingBuilder.Length == 0)
            {
                return string.Empty;
            }

            string text = _pendingBuilder.ToString();
            _pendingBuilder.Clear();
            _flushCount++;
            return text;
        }
    }

    /// <summary>
    /// Tries to drain pending text atomically. Returns true if pending text was present.
    /// </summary>
    public bool TryDrain(out string text)
    {
        lock (_lock)
        {
            if (_pendingBuilder.Length == 0)
            {
                text = string.Empty;
                return false;
            }

            text = _pendingBuilder.ToString();
            _pendingBuilder.Clear();
            _flushCount++;
            return true;
        }
    }

    /// <summary>
    /// Drains pending text and returns full batch metadata.
    /// </summary>
    public TranslationStreamDrainBatch DrainBatch()
    {
        lock (_lock)
        {
            string text = _pendingBuilder.Length > 0 ? _pendingBuilder.ToString() : string.Empty;
            if (text.Length > 0)
            {
                _pendingBuilder.Clear();
                _flushCount++;
            }

            return new TranslationStreamDrainBatch(
                Text: text,
                DeltaCount: _deltaCount,
                AccumulatedCharCount: _charCount,
                FlushIndex: _flushCount,
                State: _state,
                HasMore: _state == TranslationStreamState.Active || _pendingBuilder.Length > 0);
        }
    }

    /// <summary>
    /// Returns the full accumulated text received so far across all deltas in this session.
    /// </summary>
    public string GetAccumulatedText()
    {
        lock (_lock)
        {
            // The 40ms pump calls this every tick even when nothing new
            // arrived; rebuild only after an append instead of copying the
            // full accumulated string (up to MaxChars) under the lock.
            return _accumulatedCache ??= _accumulatedBuilder.ToString();
        }
    }

    /// <summary>
    /// Calculates Time-To-First-Token (TTFT) duration from caller's request start timestamp (Stopwatch ticks).
    /// Returns null if no delta has arrived yet.
    /// </summary>
    public TimeSpan? GetTtft(long startTimestampTicks)
    {
        lock (_lock)
        {
            if (_firstDeltaTimestampTicks <= 0 || startTimestampTicks <= 0)
            {
                return null;
            }

            if (_firstDeltaTimestampTicks < startTimestampTicks)
            {
                return TimeSpan.Zero;
            }

            return Stopwatch.GetElapsedTime(startTimestampTicks, _firstDeltaTimestampTicks);
        }
    }

    /// <summary>
    /// Calculates Time-To-First-Token (TTFT) in milliseconds from caller's request start timestamp.
    /// </summary>
    public double? GetTtftMilliseconds(long startTimestampTicks)
    {
        lock (_lock)
        {
            if (_firstDeltaTimestampTicks <= 0 || startTimestampTicks <= 0)
            {
                return null;
            }

            if (_firstDeltaTimestampTicks < startTimestampTicks)
            {
                return 0.0;
            }

            return (_firstDeltaTimestampTicks - startTimestampTicks) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// Marks the stream as successfully completed.
    /// Idempotent. Un-drained pending deltas remain intact for final drain.
    /// </summary>
    public bool Complete()
    {
        lock (_lock)
        {
            if (_state == TranslationStreamState.Active)
            {
                _state = TranslationStreamState.Completed;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Marks the stream as aborted with an optional reason.
    /// Idempotent. Pending deltas are retained for partial inspection.
    /// </summary>
    public bool Abort(string? reason = null)
    {
        lock (_lock)
        {
            if (_state is TranslationStreamState.Active or TranslationStreamState.Reset)
            {
                _state = TranslationStreamState.Aborted;
                _abortReason = reason ?? "Stream aborted";
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Resets the buffer back to Active state and clears all buffers and metrics.
    /// Used for clean state reuse during fallback before delta emission.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_state == TranslationStreamState.Disposed)
            {
                return;
            }

            _state = TranslationStreamState.Active;
            _abortReason = null;
            _pendingBuilder.Clear();
            _accumulatedBuilder.Clear();
            _accumulatedCache = null;
            _deltaCount = 0;
            _charCount = 0;
            _byteCount = 0;
            _flushCount = 0;
            _firstDeltaTimestampTicks = 0;
        }
    }

    /// <summary>
    /// Disposes and cleans up the buffer. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_state == TranslationStreamState.Disposed)
            {
                return;
            }

            _state = TranslationStreamState.Disposed;
            _pendingBuilder.Clear();
            _accumulatedBuilder.Clear();
            _accumulatedCache = null;
        }
    }
}
