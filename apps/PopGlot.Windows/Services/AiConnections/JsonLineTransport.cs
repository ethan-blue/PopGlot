namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P06 §9.3: newline-delimited UTF-8 JSON transport over a child process's
/// stdin/stdout, with stderr consumed in parallel so a chatty tool can never
/// block the real channel into a false timeout.
///
/// Safety contract:
/// - every read/write is bounded (frame bytes, cumulative bytes, queue depth);
///   exceeding a cap fails the transport explicitly — nothing is silently
///   dropped or truncated;
/// - one serialized writer; per-request id → TCS with
///   <c>RunContinuationsAsynchronously</c> so a completion never runs inline
///   on the reader loop;
/// - disconnect (exit, crash, pipe close, dispose) completes every pending
///   request with the transport's terminal reason — no orphaned awaits;
/// - <see cref="Dispose"/> kills only the process THIS transport spawned
///   (never by name).
/// </summary>
internal sealed class JsonLineTransport : IDisposable
{
    /// <summary>Hard cap for ONE framed line. A tool that emits an unbounded
    /// line (e.g. echoing a huge payload) fails instead of growing memory.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    /// <summary>Hard cap for the whole session's stdout. Translation results
    /// are orders of magnitude smaller; exceeding this is a runaway tool.</summary>
    public const long MaxCumulativeBytes = 64L * 1024 * 1024;

    private const int QueueCapacity = 256;

    private readonly System.Diagnostics.Process _process;
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly string _terminalReason;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _eventGate = new();
    private readonly List<System.Threading.Channels.Channel<string>> _events = [];
    private string? _failure;
    private sealed record Pending(Func<string, long?> ReadId, TaskCompletionSource<string> Completion);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Pending> _pending = new();
    private long _nextId;
    private long _cumulativeBytes;
    private int _disposed;

    public JsonLineTransport(System.Diagnostics.ProcessStartInfo startInfo, string terminalReason)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        // NOTE: the Standard*Encoding properties are deliberately NOT set.
        // All three directions use the raw BaseStreams with explicit UTF-8
        // framing in this class. (Setting StandardInputEncoding makes .NET
        // pre-initialize the stdin writer, which emits a UTF-8 BOM into the
        // pipe — observed as EF BB BF at the child, corrupting the first
        // frame.)
        _terminalReason = terminalReason;
        _process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动子进程。");
        // Exited may not fire when stdout closes first; the read loops own
        // terminal completion.
        _ = PumpStdoutAsync();
        _ = PumpStderrAsync();
        _ = ObserveExitAsync();
    }

    public int ProcessId => _process.Id;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && _failure is null && !_process.HasExited;

    /// <summary>
    /// Sends one JSON line and awaits the response line carrying the same
    /// <c>id</c> member. The id is assigned here and stamped into the payload
    /// by the caller-supplied stamp callback so each protocol keeps its own
    /// wire shape (JSON-RPC "id", OMP "id", …).
    /// </summary>
    public async Task<string> SendAsync(
        Func<long, string> buildLineWithId,
        Func<string, long?> readIdFromLine,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        if (_failure is not null) throw new InvalidOperationException(_failure);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new Pending(readIdFromLine, completion)))
        {
            throw new InvalidOperationException("请求登记失败。");
        }
        var line = buildLineWithId(id);
        var cancellationRegistration = cancellationToken.Register(() =>
            _pending.TryRemove(id, out _));
        try
        {
            await WriteLineAsync(line, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            cancellationRegistration.Dispose();
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Pushes one line WITHOUT awaiting a matching response (fire
    /// and forget notifications, e.g. cancel/interrupt).</summary>
    public async Task NotifyAsync(string line, CancellationToken cancellationToken) =>
        await WriteLineAsync(line, cancellationToken);

    /// <summary>Test-only diagnostics hook (first-write hex, spawns).</summary>
    internal static Action<string>? Diagnostics;

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        Diagnostics?.Invoke("first-write-hex=" + Convert.ToHexString(payload.AsSpan(0, Math.Min(12, payload.Length))));
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException("单帧超出上限，已拒绝发送。");
        }
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAlive || _failure is not null) throw new InvalidOperationException(_failure ?? _terminalReason);
            await _process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { throw new InvalidOperationException(_terminalReason, ex); }
        finally { _writeGate.Release(); }
    }

    public EventSubscription Subscribe()
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, SingleWriter = false, FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        lock (_eventGate)
        {
            if (_failure is not null) channel.Writer.TryComplete(new InvalidOperationException(_failure));
            else _events.Add(channel);
        }
        return new EventSubscription(this, channel);
    }

    internal sealed class EventSubscription(JsonLineTransport owner, System.Threading.Channels.Channel<string> channel) : IDisposable
    {
        public System.Threading.Channels.ChannelReader<string> Reader => channel.Reader;
        public void Dispose() { lock (owner._eventGate) { owner._events.Remove(channel); channel.Writer.TryComplete(); } }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            var buffer = new byte[16 * 1024];
            var frameBytes = new System.IO.MemoryStream();
            while (!_lifecycle.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _lifecycle.Token);
                if (read == 0)
                {
                    break;
                }
                Interlocked.Add(ref _cumulativeBytes, read);
                if (Volatile.Read(ref _cumulativeBytes) > MaxCumulativeBytes)
                {
                    CompleteAllPending("子进程输出超过累计上限。");
                    break;
                }
                for (var index = 0; index < read; index++)
                {
                    var b = buffer[index];
                    if (b == (byte)'\n')
                    {
                        // Frame boundary. UTF-8 multi-byte sequences never
                        // contain 0x0A, so byte-level framing is exact; the
                        // accumulated frame is decoded (and validated) as a
                        // whole. '\r' before '\n' is protocol whitespace.
                        var payload = frameBytes.ToArray();
                        frameBytes.SetLength(0);
                        if (payload.Length > MaxFrameBytes)
                        {
                            CompleteAllPending("子进程单帧超过上限。");
                            return;
                        }
                        var frame = new System.Text.UTF8Encoding(false, true).GetString(payload);
                        DispatchFrame(frame.TrimEnd('\r'));
                        continue;
                    }
                    frameBytes.WriteByte(b);
                    if (frameBytes.Length > MaxFrameBytes)
                    {
                        CompleteAllPending("子进程单帧超过上限。");
                        return;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException or System.Text.DecoderFallbackException)
        {
            // Terminal: the pipe closed or the transport is shutting down.
        }
        finally
        {
            CompleteAllPending(_terminalReason);
        }
    }

    private void DispatchFrame(string frame)
    {
        if (string.IsNullOrWhiteSpace(frame))
        {
            return;
        }
        try { using var document = System.Text.Json.JsonDocument.Parse(frame); }
        catch (System.Text.Json.JsonException) { CompleteAllPending("本机工具输出了无效的协议数据。"); return; }
        foreach (var pair in _pending)
        {
            if (pair.Value.ReadId(frame) == pair.Key && _pending.TryRemove(pair.Key, out var pending))
            { pending.Completion.TrySetResult(frame); break; }
        }
        lock (_eventGate)
        {
            foreach (var channel in _events.ToArray())
            {
                if (!channel.Writer.TryWrite(frame))
                { CompleteAllPending("本机工具事件队列已满，已停止该连接。"); break; }
            }
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var buffer = new byte[8 * 1024];
            var consumed = 0L;
            var stream = _process.StandardError.BaseStream;
            while (!_lifecycle.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _lifecycle.Token);
                if (read == 0)
                {
                    break;
                }
                // Stderr is drained, capped and discarded: it exists so a
                // chatty tool cannot wedge the pipe. Content never enters
                // translation results or diagnostics payloads.
                consumed += read;
                if (consumed > MaxCumulativeBytes)
                {
                    CompleteAllPending("本机工具错误输出超过上限。");
                    break;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException or System.Text.DecoderFallbackException)
        {
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifecycle.Token);
        }
        catch (OperationCanceledException)
        {
        }
        CompleteAllPending(_terminalReason);
    }

    private void CompleteAllPending(string reason)
    {
        foreach (var pair in _pending)
        {
            pair.Value.Completion.TrySetException(new InvalidOperationException(reason));
        }
        _pending.Clear();
        lock (_eventGate)
        {
            _failure ??= reason;
            foreach (var channel in _events) channel.Writer.TryComplete(new InvalidOperationException(reason));
            _events.Clear();
        }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _lifecycle.Cancel();
        CompleteAllPending(_terminalReason);
        try
        {
            if (!_process.HasExited)
            {
                // We own this process tree (we started it); killing it never
                // matches by name.
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _process.Dispose();
        _lifecycle.Dispose();
    }
}
