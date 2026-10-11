using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace PopGlot.Windows.Services;

/// <summary>
/// Timeline points for one user interaction (P02). One enum covers every
/// surface; a point that never fires simply stays absent, and absence reads
/// as null — never as 0 — so "unmeasured" can never masquerade as "instant".
/// </summary>
internal enum TracePoint
{
    // Entry: the stopwatch origin is the input the user made, not the moment
    // a request was sent.
    HotkeyReceived,
    TrayActionReceived,
    DispatcherEntered,

    // Selection pre-read against the real foreground application.
    SelectionStarted,
    SelectionEnded,

    // Window lifecycle. ShowRequested/Loaded are LOGICAL lifecycle points and
    // say nothing about presentation; FirstRenderCallback is the next render
    // OPPORTUNITY after show (CompositionTarget.Rendering), still not the DWM
    // frame. InteractiveReady = first layout done and inputs/events usable.
    WindowConstructionStarted,
    WindowConstructionEnded,
    ShowRequested,
    Loaded,
    FirstRenderCallback,
    InteractiveReady,

    // Local request preparation (config/route/protocol shape).
    PolicyChecked,
    RoutingEnded,
    PromptPrepared,

    // Wire. TransportSubmitted is the C# → transport hand-off; ResponseHeaders
    // and FirstWireEvent come back as Rust-side phase durations (P04) and are
    // recorded as spans, never by subtracting across processes.
    TransportSubmitted,
    ResponseHeaders,
    FirstWireEvent,

    // Stream → UI. FirstVisibleDeltaBuffered is the first VISIBLE delta the
    // pump drained (reasoning/usage never counts); Applied is the dispatcher
    // merge into the surface; RenderCallback is the next render opportunity.
    FirstVisibleDeltaBuffered,
    FirstVisibleDeltaApplied,
    FirstDeltaRenderCallback,

    // Terminal.
    Completed,
    CancelRequested,
    CancelObserved,

    // CLI adapters (P06+; wired when the adapters land).
    CliSpawn,
    CliReady,
    CliAccountChecked,
    CliTurnSubmitted,
}

/// <summary>
/// One interaction's timeline on a single Stopwatch monotonic clock. Cheap by
/// design: a mark is one concurrent-dictionary insert, so marking never
/// gates the hotkey/UI critical path. Rust phases are never subtracted from
/// these ticks — they arrive as durations and are stored in
/// <see cref="ExternalPhaseMs"/>.
/// </summary>
internal sealed class InteractionTrace
{
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly ConcurrentDictionary<TracePoint, long> _points = new();
    private readonly ConcurrentDictionary<string, double> _externalPhaseMs = new();

    /// <summary>Random correlation id shared across App/windows/Coordinator.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Anonymous interaction channel (selection/screenshot/text/summary/wake…).</summary>
    public string Channel { get; }

    /// <summary>Terminal outcome label (completed/failed/cancelled/partial…), set before publishing.</summary>
    public string? Outcome { get; private set; }

    private int _published;

    /// <summary>True exactly once: the first caller wins, later closes are no-ops.</summary>
    public bool TryBeginPublish() => Interlocked.Exchange(ref _published, 1) == 0;

    /// <summary>True when the very first mark already happened before construction helpers run.</summary>
    public bool HasMarks => !_points.IsEmpty;

    public InteractionTrace(string channel) => Channel = channel;

    public void SetOutcome(string outcome) => Outcome = outcome;

    /// <summary>Stops the origin: subclasses of "entry already observed" reuse this.</summary>
    public long OriginTimestamp => _origin;

    public void Mark(TracePoint point) => _points.TryAdd(point, Stopwatch.GetTimestamp());

    public bool IsMarked(TracePoint point) => _points.ContainsKey(point);

    /// <summary>Milliseconds from the trace origin, or null when the point never fired.</summary>
    public double? Milliseconds(TracePoint point) =>
        _points.TryGetValue(point, out var tick)
            ? Stopwatch.GetElapsedTime(_origin, tick).TotalMilliseconds
            : null;

    /// <summary>
    /// A phase duration measured OUTSIDE this process's clock (e.g. Rust
    /// transport phases). Stored under a stable key; never mixed into the
    /// local tick math.
    /// </summary>
    public void RecordExternalPhaseMs(string phase, double milliseconds) =>
        _externalPhaseMs[phase] = milliseconds;

    public IReadOnlyDictionary<string, double> ExternalPhases => _externalPhaseMs;

    /// <summary>Serialize to the anonymous, structure-only diagnostic shape:
    /// point names, millisecond offsets, channel, external phases. No text,
    /// no URLs, no headers, no account data, ever.</summary>
    public Dictionary<string, object?> ToDiagnosticShape()
    {
        var points = new Dictionary<string, double>(_points.Count);
        foreach (var (point, tick) in _points)
        {
            points[point.ToString()] =
                Math.Round(Stopwatch.GetElapsedTime(_origin, tick).TotalMilliseconds, 2);
        }
        var shape = new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["channel"] = Channel,
            ["outcome"] = Outcome,
            ["points"] = points,
        };
        if (_externalPhaseMs.IsEmpty)
        {
            return shape;
        }
        var phases = new Dictionary<string, double>(_externalPhaseMs.Count);
        foreach (var (phase, ms) in _externalPhaseMs)
        {
            phases[phase] = Math.Round(ms, 2);
        }
        shape["externalPhasesMs"] = phases;
        return shape;
    }
}

/// <summary>
/// Window lifecycle marks for an <see cref="InteractionTrace"/>. All helpers
/// are one-shot per trace and self-detach; none of them runs user work.
/// </summary>
internal static class InteractionTraceWindow
{
    /// <summary>
    /// Marks <paramref name="point"/> at the NEXT CompositionTarget.Rendering
    /// callback after this call — the render OPPORTUNITY, not the DWM frame.
    /// Honest by naming: nothing here claims the frame was presented.
    /// </summary>
    public static void MarkAtNextRenderCallback(InteractionTrace trace, TracePoint point)
    {
        if (trace.IsMarked(point))
        {
            return;
        }
        EventHandler onRendering = null!;
        onRendering = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= onRendering;
            trace.Mark(point);
        };
        System.Windows.Media.CompositionTarget.Rendering += onRendering;
    }

    /// <summary>See <see cref="MarkAtNextRenderCallback"/>; the window-show
    /// render opportunity.</summary>
    public static void MarkNextRenderCallback(InteractionTrace trace) =>
        MarkAtNextRenderCallback(trace, TracePoint.FirstRenderCallback);

    /// <summary>
    /// Marks <see cref="TracePoint.Loaded"/> (logical lifecycle; only fires
    /// on the FIRST show of a window — a re-Show never re-raises Loaded).
    /// </summary>
    public static void ObserveFirstLoad(InteractionTrace trace, System.Windows.Window window)
    {
        if (trace.IsMarked(TracePoint.Loaded))
        {
            return;
        }
        System.Windows.RoutedEventHandler onLoaded = null!;
        onLoaded = (_, _) =>
        {
            window.Loaded -= onLoaded;
            trace.Mark(TracePoint.Loaded);
        };
        window.Loaded += onLoaded;
    }

    /// <summary>
    /// Marks <see cref="TracePoint.InteractiveReady"/> at the first dispatcher
    /// pass at Background priority after Show — i.e., after layout, Loaded and
    /// the render queue have been serviced, and BEFORE history lists, model
    /// catalogs or any network health work. Defined, reproducible, and never
    /// presented as the DWM frame time. <paramref name="onReady"/> runs once
    /// after the mark, on the dispatcher thread, for wake traces that publish
    /// exactly when they are complete.
    /// </summary>
    public static void MarkInteractiveReadyAfterShow(
        InteractionTrace trace,
        System.Windows.Window window,
        Action<InteractionTrace>? onReady = null)
    {
        if (trace.IsMarked(TracePoint.InteractiveReady))
        {
            return;
        }
        window.Dispatcher.BeginInvoke(
            () =>
            {
                if (!trace.IsMarked(TracePoint.InteractiveReady))
                {
                    trace.Mark(TracePoint.InteractiveReady);
                }
                onReady?.Invoke(trace);
            },
            System.Windows.Threading.DispatcherPriority.Background);
    }
}

/// <summary>
/// Bounded collector for interaction timelines (P02). In-memory ring always
/// available for diagnostics; durable JSONL recording is OFF by default and
/// enabled explicitly (measurement mode / POPGLOT_TRACE=1 debug). Publishing
/// hands off to a background writer so no hotkey or UI-thread path ever
/// waits on disk.
/// </summary>
internal static class InteractionTraceCollector
{
    private const int RingCapacity = 128;
    private const int WriterQueueCapacity = 256;

    private static readonly ConcurrentQueue<InteractionTrace> Ring = new();
    private static int _ringCount;
    private static Channel<(string Line, long OriginTicks)>? _writerChannel;
    private static string? _outputPath;
    private static int _enabled;
    private static readonly object EnableGate = new();

    /// <summary>True when durable JSONL recording is active.</summary>
    public static bool Enabled => Volatile.Read(ref _enabled) == 1;

    /// <summary>
    /// Starts recording traces to <paramref name="outputPath"/> on a single
    /// background worker. Idempotent; safe to call before any trace exists.
    /// The path is provided by the measurement harness / debug mode, never
    /// derived from user content.
    /// </summary>
    public static void Enable(string outputPath)
    {
        lock (EnableGate)
        {
            if (Enabled)
            {
                return;
            }
            _outputPath = outputPath;
            _writerChannel = Channel.CreateBounded<(string, long)>(
                new BoundedChannelOptions(WriterQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                });
            var worker = new Thread(WriteLoop)
            {
                IsBackground = true,
                Name = "PopGlot.TraceWriter",
            };
            worker.Start();
            Volatile.Write(ref _enabled, 1);
        }
    }

    /// <summary>Latest traces, oldest first, bounded to the ring capacity.</summary>
    public static IReadOnlyList<InteractionTrace> Recent()
    {
        var snapshot = Ring.ToArray();
        return snapshot;
    }

    /// <summary>
    /// Hands a finished trace to the ring and (when enabled) the background
    /// writer. Non-blocking; a full writer queue drops the OLDEST line rather
    /// than stalling the caller, and recording that drop would itself require
    /// the queue, so drops are observable only as fewer lines in the file.
    /// </summary>
    public static void Publish(InteractionTrace trace)
    {
        if (!trace.TryBeginPublish())
        {
            return;
        }
        Ring.Enqueue(trace);
        if (Interlocked.Increment(ref _ringCount) > RingCapacity)
        {
            Ring.TryDequeue(out _);
            Interlocked.Decrement(ref _ringCount);
        }
        if (!Enabled)
        {
            return;
        }
        var line = JsonSerializer.Serialize(trace.ToDiagnosticShape());
        _writerChannel?.Writer.TryWrite((line, trace.OriginTimestamp));
    }

    /// <summary>Flushes queued lines; called once from app exit paths.</summary>
    public static void Flush(TimeSpan wait)
    {
        if (!Enabled || _writerChannel is null)
        {
            return;
        }
        _writerChannel.Writer.TryComplete();
        _writerChannel.Reader.Completion.Wait(wait);
    }

    private static void WriteLoop()
    {
        var channel = _writerChannel!;
        var path = _outputPath!;
        try
        {
            using var writer = new StreamWriter(path, append: true);
            // WaitToReadAsync returns false once the channel is completed by
            // Flush — the loop ends gracefully instead of throwing.
            while (channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (channel.Reader.TryRead(out var item))
                {
                    writer.WriteLine(item.Line);
                }
                writer.Flush();
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ObjectDisposedException
                                              or System.Threading.Channels.ChannelClosedException)
        {
            // A failed diagnostics sink must never take the app down; disable
            // silently — the in-memory ring still serves diagnostics.
            Volatile.Write(ref _enabled, 0);
        }
    }
}
