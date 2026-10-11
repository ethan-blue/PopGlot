using System.Diagnostics;

namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P06 §9.3: owns the CLI subprocess lifecycle for ONE selected connection.
///
/// Ownership contract:
/// - spawns ONLY after <see cref="ExternalRuntimePolicy"/> allowed it at the
///   moment of the spawn, with <see cref="ProcessStartInfo.ArgumentList"/>
///   (never interpolated command lines) and an isolated empty working
///   directory that this host owns;
/// - every prompt submission RE-EVALUATES the policy — a warm process never
///   carries standing permission; a refusal while warm cancels the in-flight
///   turn and reclaims the process;
/// - sessions never share context: each turn carries its own request id and
///   the transport's response matching is id-fenced;
/// - <see cref="Disconnect"/> stops the process THIS host started (never by
///   name, never the user's own CLI sessions);
/// - counters (<see cref="SpawnCount"/>, <see cref="PromptCount"/>) are the
///   assertion surface for the offline negative tests.
/// </summary>
internal sealed class CliRuntimeHost : IDisposable
{
    private readonly string _toolId;
    private readonly EngineExecutionKind _kind;
    private readonly CliConnectionSettings _connection;
    private readonly object _gate = new();
    private JsonLineTransport? _transport;
    private string? _workDirectory;
    private long _spawnCount;
    private long _promptCount;
    private int _disposed;

    public CliRuntimeHost(string toolId, EngineExecutionKind kind, CliConnectionSettings connection)
    {
        _toolId = toolId;
        _kind = kind;
        _connection = connection;
    }

    public long SpawnCount => Volatile.Read(ref _spawnCount);

    public long PromptCount => Volatile.Read(ref _promptCount);

    public bool HasProcess
    {
        get
        {
            lock (_gate)
            {
                return _transport is { IsAlive: true };
            }
        }
    }

    /// <summary>
    /// Ensures a live transport exists for the connection, evaluating the
    /// runtime policy FIRST (no policy, no process) and building the start
    /// info through the per-tool argument whitelist — the only place a
    /// process is ever created.
    /// </summary>
    /// <exception cref="InvalidOperationException">refused by policy or the
    /// executable entry cannot be verified.</exception>
    public JsonLineTransport EnsureTransport(
        Func<CliConnectionSettings, ProcessStartInfo> buildStartInfo)
        => EnsureTransport((connection, _) => buildStartInfo(connection));

    public JsonLineTransport EnsureTransport(
        Func<CliConnectionSettings, string, ProcessStartInfo> buildStartInfo)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        // Policy is evaluated on EVERY path that can reach a spawn or a
        // reuse — including the warm path below.
        var decision = ExternalRuntimePolicy.Evaluate(_kind, _connection);
        if (!decision.Allowed)
        {
            throw new InvalidOperationException(decision.RefusalZh);
        }
        lock (_gate)
        {
            if (_transport is { IsAlive: true })
            {
                return _transport;
            }
        }

        _ = CliDiscovery.TryInspect(_toolId, _connection.ExecutablePath)
            ?? throw new InvalidOperationException(
                "本机工具入口不可用（路径不存在或不可读）；请重新检测或选择入口。");
        var workDirectory = EnsureOwnedWorkDirectory();
        var startInfo = buildStartInfo(_connection, workDirectory);
        // The isolated working directory is host-owned and empty: the tool
        // must never run inside (or see) a real repository by accident.
        startInfo.WorkingDirectory = workDirectory;

        var transport = new JsonLineTransport(
            startInfo,
            terminalReason: $"{_toolId} 进程已退出或通道已关闭。");
        Interlocked.Increment(ref _spawnCount);
        lock (_gate)
        {
            // A concurrent connect may have won; drop the loser exactly once.
            if (_transport is { IsAlive: true })
            {
                transport.Dispose();
                return _transport;
            }
            _transport = transport;
        }
        return transport;
    }

    /// <summary>
    /// Records one prompt submission after the (re-evaluated) policy check.
    /// The counter is the offline-test assertion surface: offline states
    /// must show promptCount == 0.
    /// </summary>
    public void RecordPromptSubmission()
    {
        Interlocked.Increment(ref _promptCount);
    }

    /// <summary>
    /// Cancels the in-flight turn and reclaims the process this host owns.
    /// The honest wording for users: already-submitted requests may still
    /// run on the provider side; this stops PopGlot's channel.
    /// </summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            _transport?.Dispose();
            _transport = null;
        }
    }

    private string EnsureOwnedWorkDirectory()
    {
        lock (_gate)
        {
            if (_workDirectory is not null && Directory.Exists(_workDirectory))
            {
                return _workDirectory;
            }
            var root = Path.Combine(
                StoragePaths.CoreConfigDirectory,
                "cli-run",
                $"{_toolId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            _workDirectory = root;
            return root;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        Disconnect();
        lock (_gate)
        {
            if (_workDirectory is not null)
            {
                try
                {
                    var target = Path.GetFullPath(_workDirectory);
                    var ownedRoot = Path.GetFullPath(Path.Combine(StoragePaths.CoreConfigDirectory, "cli-run")) + Path.DirectorySeparatorChar;
                    if (target.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase))
                        Directory.Delete(target, recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // A busy work dir stays behind; it is empty by contract
                    // and lives under our own config root.
                }
            }
        }
    }
}
