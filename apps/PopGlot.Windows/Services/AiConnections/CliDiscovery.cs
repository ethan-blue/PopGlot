namespace PopGlot.Windows.Services.AiConnections;

/// <summary>
/// P05 §8.2: discovers local AI tool entry points — PATH plus a small set of
/// explicit candidate directories — and verifies ONLY executable-file facts
/// (a real entry, size, write time). No version is read here: a version
/// claim requires running the tool, which discovery never does.
///
/// Explicit non-goals (hard boundaries): never scans browser cookies,
/// credential stores, auth.json/agent.db files or any other application's
/// session data; never reports login state from file presence; never
/// executes a same-named script from the current directory (PATH resolution
/// is explicit, not shell interpolation).
/// </summary>
internal static class CliDiscovery
{
    /// <summary>Candidate executable names per tool. Stable ids only.</summary>
    public const string CodexToolId = "codex";
    public const string OmpToolId = "omp";

    private static readonly string[] CodexCandidates = ["codex.exe", "codex.cmd", "codex.ps1"];
    private static readonly string[] OmpCandidates = ["omp.exe", "omp.cmd", "omp.ps1", "omp"];

    /// <summary>
    /// Searches PATH directories and the explicit extra directories for the
    /// tool's candidate entries. Returns every hit with its offline facts;
    /// presence is NOT login state, NOT version compatibility, NOT usability.
    /// </summary>
    public static IReadOnlyList<DiscoveredCliTool> Discover(string toolId, IReadOnlyList<string>? extraDirectories = null)
    {
        var candidates = toolId switch
        {
            CodexToolId => CodexCandidates,
            OmpToolId => OmpCandidates,
            _ => [],
        };
        var searchDirs = new List<string>();
        try
        {
            // PATH split without shell interpolation.
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            searchDirs.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        catch
        {
            // An unreadable PATH just means fewer directories to search.
        }
        if (extraDirectories is not null)
        {
            searchDirs.AddRange(extraDirectories);
        }
        searchDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        searchDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omp"));
        searchDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin"));
        if (Environment.GetEnvironmentVariable("BUN_INSTALL") is { Length: > 0 } bunInstall && Path.IsPathFullyQualified(bunInstall))
            searchDirs.Add(Path.Combine(bunInstall, "bin"));

        var found = new List<DiscoveredCliTool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in searchDirs)
        {
            // A relative PATH component must never discover a tool from the
            // application's current working directory.
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var candidate in candidates)
            {
                string fullPath;
                try
                {
                    fullPath = Path.Combine(directory, candidate);
                }
                catch
                {
                    continue;
                }
                if (!seen.Add(fullPath))
                {
                    continue;
                }
                try { fullPath = CliLauncher.Resolve(toolId, fullPath); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException) { continue; }
                if (found.Any(item => string.Equals(item.ExecutablePath, fullPath, StringComparison.OrdinalIgnoreCase))) continue;
                var tool = TryInspect(toolId, fullPath);
                if (tool is not null)
                {
                    found.Add(tool);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Inspects ONE explicit path (the settings flow pins the user's choice).
    /// A .cmd/.ps1 wrapper is recorded as-is; actually EXECUTING it later
    /// requires the runtime host to resolve it to a real interpreter entry
    /// first — discovery itself never executes anything.
    /// </summary>
    public static DiscoveredCliTool? TryInspect(string toolId, string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }
            var info = new FileInfo(fullPath);
            return new DiscoveredCliTool(
                toolId,
                fullPath,
                ReportedVersion: null,
                FileSizeBytes: info.Length,
                LastWriteUtc: info.LastWriteTimeUtc);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
