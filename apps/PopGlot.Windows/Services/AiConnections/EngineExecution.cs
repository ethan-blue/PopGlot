namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P05: HOW a text route executes. <see cref="ProviderType"/> stays an HTTP
/// protocol; this is the orthogonal execution dimension — an HTTP-protocol
/// profile can execute through the native API stack or through a local AI
/// tool's process protocol. Serialized as stable strings, never numbers.
/// </summary>
internal enum EngineExecutionKind
{
    /// <summary>The existing HTTP transport stack (default; every legacy profile).</summary>
    NativeApi,

    /// <summary>Codex CLI app-server subprocess (JSON-RPC over stdio).</summary>
    CodexAppServer,

    /// <summary>oh-my-pi RPC subprocess (JSONL over stdio).</summary>
    OmpRpc,

    /// <summary>
    /// A value this build does not know. The profile stays loadable but is
    /// NEVER executable — an unknown backend must not silently fall back to
    /// NativeApi (or the free engine) and send.
    /// </summary>
    Unknown,
}

/// <summary>
/// Stable serialization strings. Only these round-trip; anything else reads
/// back as <see cref="EngineExecutionKind.Unknown"/> and stays unsupported.
/// </summary>
internal static class EngineExecutionKindNames
{
    public const string NativeApi = "native-api";
    public const string CodexAppServer = "codex-app-server";
    public const string OmpRpc = "omp-rpc";

    public static EngineExecutionKind Parse(string? value) => value switch
    {
        NativeApi => EngineExecutionKind.NativeApi,
        CodexAppServer => EngineExecutionKind.CodexAppServer,
        OmpRpc => EngineExecutionKind.OmpRpc,
        _ => EngineExecutionKind.Unknown,
    };

    public static string Format(EngineExecutionKind kind) => kind switch
    {
        EngineExecutionKind.NativeApi => NativeApi,
        EngineExecutionKind.CodexAppServer => CodexAppServer,
        EngineExecutionKind.OmpRpc => OmpRpc,
        // Unknown serializes explicitly as unknown — a loadable, visibly
        // unsupported state, never a silent rewrite to native-api.
        _ => "unknown",
    };
}

/// <summary>
/// Connection details for a CLI-backed engine. Deliberately minimal: the
/// executable path (user-verified), the version the protocol was validated
/// against, and the model/upstream selection. No arbitrary arguments, no
/// environment passthrough, no credentials — authentication is owned by the
/// tool itself.
/// </summary>
internal sealed class CliConnectionSettings
{
    public CliConnectionSettings()
    {
    }

    public CliConnectionSettings(CliConnectionSettings source)
    {
        ExecutablePath = source.ExecutablePath;
        ExpectedVersion = source.ExpectedVersion;
        ModelId = source.ModelId;
        UpstreamProviderId = source.UpstreamProviderId;
        Enabled = source.Enabled;
    }

    public CliConnectionSettings Clone() => new(this);

    /// <summary>Verified executable entry (resolved by CliDiscovery, never a raw PATH guess).</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>The version whose protocol/schema the adapter was validated against.</summary>
    public string ExpectedVersion { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    /// <summary>OMP only: which upstream provider account this connection routes through.</summary>
    public string? UpstreamProviderId { get; set; }

    /// <summary>
    /// A connection exists only after the user explicitly created it. A
    /// disabled or missing connection makes the profile unexecutable — never
    /// a silent fallback to another engine.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Typed execution facts about a provider profile, derived once and consumed
/// by every routing surface (coordinator, quick switcher, summary capability,
/// screenshot routing). Routing never infers the backend from labels, file
/// existence or <c>IsLocal</c>.
/// </summary>
internal sealed record EngineExecutionFacts(
    EngineExecutionKind ExecutionKind,
    CliConnectionSettings? CliConnection,
    bool TextCapable,
    bool VisionCapable,
    bool NetworkRoute)
{
    /// <summary>
    /// True when this profile executes through a local AI tool subprocess.
    /// A subprocess is NOT local inference: the model may still be remote,
    /// so privacy copy must never call these engines 本机模型.
    /// </summary>
    public bool IsCliExecution => ExecutionKind is
        EngineExecutionKind.CodexAppServer or EngineExecutionKind.OmpRpc;

    /// <summary>
    /// Supported adapters. Per-version verification, connection state and
    /// network permission are checked separately before process launch.
    /// </summary>
    public static bool AdapterEnabledFor(EngineExecutionKind kind) =>
        kind is EngineExecutionKind.CodexAppServer or EngineExecutionKind.OmpRpc;
}
