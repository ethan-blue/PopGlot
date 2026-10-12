using System.Diagnostics;
using System.Text.Json;

namespace PopGlot.Windows.Services.AiConnections;

internal static class CliLauncher
{
    // Reference release numbers, not JSONL protocol versions. OMP is probed at runtime.
    public static string ReferenceToolVersion(EngineExecutionKind kind) => kind switch
    { EngineExecutionKind.CodexAppServer => "0.150.1", EngineExecutionKind.OmpRpc => "18.2.11", _ => "" };
    // Resolve npm shims to the packaged native executable; never run a shell.
    public static string Resolve(string toolId, string entry)
    {
        var full = Path.GetFullPath(entry);
        if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
        if (toolId == CliDiscovery.CodexToolId)
        {
            var directory = Path.GetDirectoryName(full)!;
            var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
            var triple = architecture == "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
            var package = Path.Combine(directory, "node_modules", "@openai", "codex");
            foreach (var path in new[]
            {
                Path.Combine(package, "node_modules", "@openai", $"codex-win32-{architecture}", "vendor", triple, "bin", "codex.exe"),
                Path.Combine(directory, "node_modules", "@openai", $"codex-win32-{architecture}", "vendor", triple, "bin", "codex.exe"),
                Path.Combine(package, "vendor", triple, "bin", "codex.exe"),
            }) if (File.Exists(path)) return path;
        }
        throw new InvalidOperationException("无法解析工具的原生程序入口；请使用 codex.exe 或 omp.exe 安装版后重新检测。");
    }

    public static async Task<string> VerifyVersionAsync(string toolId, CliConnectionSettings connection, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Resolve(toolId, connection.ExecutablePath))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        info.ArgumentList.Add("--version");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法检查工具版本。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(toolId == CliDiscovery.OmpToolId ? 3 : 10));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)).Trim();
            _ = await stderr.ConfigureAwait(false);
            var version = System.Text.RegularExpressions.Regex.Match(text, @"(?:^|\s|/)v?(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)*)(?:$|\s)").Groups["version"].Value;
            if (toolId == CliDiscovery.OmpToolId)
            {
                // OMP version is diagnostics, not a protocol compatibility decision.
                connection.ExpectedVersion = string.IsNullOrEmpty(version) ? "unknown" : version;
                return connection.ExpectedVersion;
            }
            var expected = ReferenceToolVersion(EngineExecutionKind.CodexAppServer);
            if (process.ExitCode != 0 || version != expected || (!string.IsNullOrWhiteSpace(connection.ExpectedVersion) && connection.ExpectedVersion != version))
                throw new InvalidOperationException($"当前适配器支持 {toolId} {expected}；检测到的版本不匹配，请更新工具或 PopGlot 后重试。");
            connection.ExpectedVersion = expected;
            return expected;
        }
        catch (OperationCanceledException) when (toolId == CliDiscovery.OmpToolId && !ct.IsCancellationRequested)
        {
            connection.ExpectedVersion = "unknown";
            return connection.ExpectedVersion;
        }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    public static void RequirePolicy(EngineExecutionKind kind, CliConnectionSettings connection)
    {
        var decision = ExternalRuntimePolicy.Evaluate(kind, connection);
        if (!decision.Allowed) throw new InvalidOperationException(decision.RefusalZh);
    }
}

internal sealed record CliModel(string Id, string Name, string? Provider = null, bool IsDefault = false)
{
    public string DisplayName => Provider is null ? Name : $"{Provider} / {Name}";
    public override string ToString() => DisplayName;
}

internal interface ICliBackend : IDisposable
{
    Task<CliAccountStatus> CheckAccountAsync(CancellationToken ct);
    Task<IReadOnlyList<CliModel>> ListModelsAsync(CancellationToken ct);
    Task RunAsync(string instructions, string payload, Action<string> delta, CancellationToken ct, Action? promptSubmitted = null);
}

internal static class CliWire
{
    public static long? ResponseId(string line, bool omp = false)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (omp && (!root.TryGetProperty("type", out var type) || type.GetString() != "response")) return null;
        if (!omp && !root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)) return null;
        if (!root.TryGetProperty("id", out var id)) return null;
        return id.ValueKind == JsonValueKind.Number ? id.GetInt64() : long.TryParse(id.GetString(), out var n) ? n : null;
    }

    public static JsonElement Result(string line, bool omp = false)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _) || (omp && (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())))
            // Tool errors may contain request text or credentials. Keep the wire payload out of UI/logs.
            throw new InvalidOperationException("本机工具拒绝了请求。请检查工具登录、模型权限、额度及网络配置后重试。");
        return root.TryGetProperty(omp ? "data" : "result", out var data) ? data.Clone() : default;
    }
}
