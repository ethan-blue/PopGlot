namespace PopGlot.Windows.Services;

/// <summary>
/// A10: the fuse for new work. When the global exception barrier degrades
/// the app (unknown exception or exception storm), every request entry point
/// checks this gate and refuses — refusing work is not left to the hotkeys
/// or to individual buttons remembering to ask.
/// </summary>
internal static class RuntimeGate
{
    /// <summary>False while the app is in degraded (fuse-tripped) mode.</summary>
    public static bool NewWorkAllowed { get; set; } = true;

    /// <summary>The one refusal reason every entry point shows.</summary>
    public const string RefusalZh = "PopGlot 遇到故障保护，已暂停新任务；请从托盘退出并重新打开。";
}
