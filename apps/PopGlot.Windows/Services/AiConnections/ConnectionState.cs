namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P05 §8.2: the typed connection states. Each state has an honest meaning —
/// no state ever claims "已登录" or "请求可用" from a PATH hit alone: only a
/// user-driven successful test or a completed real request may mark
/// <see cref="RequestVerified"/>.
/// </summary>
internal enum CliConnectionState
{
    /// <summary>未发现：PATH 与候选目录都没有可验证的入口。</summary>
    NotFound,

    /// <summary>未连接：检测到工具，未启动任何进程。</summary>
    Disconnected,

    /// <summary>启动/检查中。</summary>
    Starting,

    /// <summary>需登录：工具报告未登录或凭据过期。</summary>
    NeedsLogin,

    /// <summary>状态已识别：账号/版本状态读取成功，请求能力仍未验证。</summary>
    StatusIdentified,

    /// <summary>待请求验证：已选择模型，尚未完成一次真实请求。</summary>
    PendingRequestVerification,

    /// <summary>可用：一次真实请求已完成（用户主动测试或真实翻译）。</summary>
    RequestVerified,

    /// <summary>凭据过期（上次可用后失效）。</summary>
    Expired,

    /// <summary>限流：工具明确报告 rate limit。</summary>
    RateLimited,

    /// <summary>版本不兼容：安装版本不在已验证范围。</summary>
    VersionIncompatible,

    /// <summary>该工具/版本的最低隔离要求无法满足，连接不可用。</summary>
    IsolationRequirementsUnmet,
}

/// <summary>One tool's discovered, verified entry point (offline facts only).</summary>
internal sealed record DiscoveredCliTool(
    string ToolId,
    string ExecutablePath,
    string? ReportedVersion,
    long FileSizeBytes,
    DateTimeOffset LastWriteUtc);

/// <summary>账号状态查询的脱敏结果：只说类别与显示名，绝不携带 Token/邮箱完整值。</summary>
internal sealed record CliAccountStatus(
    CliConnectionState State,
    string? AccountDisplayName,
    string? DetailZh);
