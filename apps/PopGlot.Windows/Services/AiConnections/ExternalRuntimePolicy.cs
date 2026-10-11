namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P05 §8.4: the typed gate for external CLI execution. It composes the
/// existing core policies (safe-offline / network-disabled) with the
/// per-connection permission, and it is consulted BEFORE any spawn AND again
/// before EVERY prompt submission — including when a warm process already
/// exists (a warm process never carries a standing permission).
///
/// The honest boundary this class states in its refusals: revoking
/// permission stops NEW submissions and cancels the in-flight turn, but a
/// request already handed to the tool may still run on the provider side —
/// the UI must phrase revocation accordingly, never as "已撤回正在计费的请求".
/// </summary>
internal static class ExternalRuntimePolicy
{
    /// <summary>
    /// The typed decision for one connection at one instant. Callers must
    /// re-run this per submission; caching the value across submissions is
    /// exactly the bug this type exists to prevent.
    /// </summary>
    public readonly record struct Decision(
        bool Allowed,
        string? RefusalZh);

    /// <summary>
    /// Evaluates whether the given CLI connection may execute right now:
    /// the connection must be enabled, its adapter enabled for this build,
    /// the global runtime must allow new work, and the core network policy
    /// must not be in safe-offline/network-disabled (a CLI subprocess routes
    /// content to remote providers; safe-offline must stop it).
    /// </summary>
    public static Decision Evaluate(
        EngineExecutionKind executionKind,
        CliConnectionSettings? connection)
    {
        if (!RuntimeGate.NewWorkAllowed)
        {
            return new Decision(false, RuntimeGate.RefusalZh);
        }
        if (executionKind is not (EngineExecutionKind.CodexAppServer or EngineExecutionKind.OmpRpc))
        {
            return new Decision(false, "该档案不是本机工具连接。");
        }
        if (!EngineExecutionFacts.AdapterEnabledFor(executionKind))
        {
            return new Decision(false, "该本机工具连接尚未在当前版本放行（协议与隔离验证未完成）。");
        }
        if (connection is not { Enabled: true })
        {
            return new Decision(false, "该本机工具连接未启用或未完成配置。");
        }
        if (string.IsNullOrWhiteSpace(connection.ExecutablePath))
        {
            return new Decision(false, "该本机工具连接没有已验证的可执行入口。");
        }
        try
        {
            var settings = CoreBridge.GetSettings();
            if (settings.SafeDevMode || !settings.NetworkEnabled)
            {
                return new Decision(
                    false,
                    "已开启安全离线模式或网络已关闭；本机工具连接同样不会发送任何出网请求。");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException)
        {
            // Core unavailable: no permission can be proven, so refuse.
            return new Decision(false, "无法读取当前网络策略，未启动本机工具。");
        }
        return new Decision(true, null);
    }
}
