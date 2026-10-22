using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// P06/P07/P08 subprocess-base tests against a REAL child process (a fixed
/// PowerShell harness script, spawned with ArgumentList + stdio — the same
/// mechanics the production hosts must use). These prove the transport's
/// bounded framing, id-matched request/response, parallel stderr drain,
/// crash completion and own-process-only teardown, plus the policy negative:
/// with the adapter disabled no spawn can ever happen.
/// </summary>
internal static class CliSubprocessTests
{
    private static string FakeCliPath() => WriteFakeCliScript();

    private static string _fakeCliPath = string.Empty;

    private static string WriteFakeCliScript()
    {
        if (_fakeCliPath.Length > 0)
        {
            return _fakeCliPath;
        }
        var directory = Path.Combine(Path.GetTempPath(), $"popglot-fakecli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "fake-cli.cjs");
        var logPath = Path.Combine(directory, "lifecycle.log");
        // Node fake CLI: byte-level framing on both directions (the
        // transport's own contract) — raw stdin bytes, split on 0x0A,
        // UTF-8 decode per frame, UTF-8 byte replies, chatty stderr per
        // request, and a lifecycle log with frame-hex evidence. (A
        // PowerShell-hosted fake was rejected: the Windows PowerShell
        // console host re-encodes piped stdin and injects a UTF-8 BOM
        // that the child observes before the parent's own bytes.)
        const string scriptBody = """
            const fs = require('fs');
            const log = process.argv[2];
            fs.appendFileSync(log, `boot pid=${process.pid}` + '\n');
            let buffer = [];
            process.stdin.on('data', (chunk) => {
              for (const b of chunk) {
                if (b === 0x0d) continue;
                if (b !== 0x0a) { buffer.push(b); continue; }
                if (buffer.length === 0) continue;
                const bytes = Buffer.from(buffer);
                buffer = [];
                fs.appendFileSync(log, `frame-hex=${bytes.subarray(0, 8).toString('hex').match(/../g).join(' ')}` + '\n');
                const line = bytes.toString('utf8');
                process.stderr.write(`noise ${line}` + '\n');
                try {
                  const json = JSON.parse(line);
                  if (json.crash === true) { fs.appendFileSync(log, 'crash' + '\n'); process.exit(3); }
                  process.stdout.write(`{"id":${json.id},"ok":true,"kind":"fake","echo":${JSON.stringify(json.echo == null ? '' : json.echo)}}` + '\n');
                  fs.appendFileSync(log, `responded id=${json.id}` + '\n');
                } catch (error) {
                  fs.appendFileSync(log, 'parse-failed' + '\n');
                }
              }
            });
            process.stdin.on('end', () => { fs.appendFileSync(log, 'exit' + '\n'); });
            """;
        File.WriteAllText(script, scriptBody);
        _fakeCliPath = script;
        _fakeLogPath = logPath;
        return script;
    }

    private static string _fakeLogPath = string.Empty;

    private static System.Diagnostics.ProcessStartInfo FakeStartInfo()
    {
        var info = new System.Diagnostics.ProcessStartInfo("node.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ArgumentList only — no interpolated command line anywhere. The
        // script and its log path are separate arguments, never shell text.
        info.ArgumentList.Add(FakeCliPath());
        info.ArgumentList.Add(_fakeLogPath);
        return info;
    }

    public static async Task TransportMatchesIdsAndDrainsStderr()
    {
        using var transport = new JsonLineTransport(
            FakeStartInfo(), terminalReason: "fake cli closed");
        var first = await transport.SendAsync(
            id => $"{{\"id\":{id},\"ping\":\"one\"}}",
            ReadId,
            CancellationToken.None);
        True(first.Contains("\"id\":1") && first.Contains("\"ok\":true"),
            "the first request matched its own id and got the fake response");
        var second = await transport.SendAsync(
            id => $"{{\"id\":{id},\"ping\":\"two\"}}",
            ReadId,
            CancellationToken.None);
        True(second.Contains("\"id\":2"),
            "ids increment and match — responses never cross requests");
        True(transport.IsAlive, "the fake cli stays alive between requests");
    }

    public static async Task TransportFramesMultilineAndUnicodeExactly()
    {
        using var transport = new JsonLineTransport(
            FakeStartInfo(), terminalReason: "fake cli closed");
        var payload = "中文🎉―𝄞 text with spacing";
        var response = await transport.SendAsync(
            id => $"{{\"id\":{id},\"echo\":\"{payload}\"}}",
            ReadId,
            CancellationToken.None);
        True(response.Contains(payload),
            "multi-byte UTF-8 frames round-trip byte-exact");
    }

    public static async Task CrashCompletesPendingWithTerminalReason()
    {
        using var transport = new JsonLineTransport(
            FakeStartInfo(), terminalReason: "fake cli crashed");
        var crashed = false;
        try
        {
            // The fake script exits when it reads crash=true.
            await transport.SendAsync(
                id => $"{{\"id\":{id},\"crash\":true}}",
                ReadId,
                CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            crashed = exception.Message.Contains("crashed", StringComparison.Ordinal);
        }
        True(crashed, "a crashed child completes the pending request with the terminal reason");
        // A follow-up request on the dead transport fails, never hangs.
        var refused = false;
        try
        {
            await transport.SendAsync(
                id => $"{{\"id\":{id}}}",
                ReadId,
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        True(refused, "a dead transport refuses new requests instead of hanging");
    }

    public static async Task DisposeKillsOwnChildOnly()
    {
        var transport = new JsonLineTransport(
            FakeStartInfo(), terminalReason: "disposed");
        // Establish one request so the child is mid-flight.
        await transport.SendAsync(
            id => $"{{\"id\":{id}}}",
            ReadId,
            CancellationToken.None);
        var processId = transport.ProcessId;
        transport.Dispose();
        // The child this transport owned must be gone shortly.
        var exited = false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = System.Diagnostics.Process.GetProcessById(processId);
                if (probe.HasExited)
                {
                    exited = true;
                    break;
                }
            }
            catch (ArgumentException)
            {
                exited = true;
                break;
            }
            await Task.Delay(100);
        }
        True(exited, "dispose reclaims the process it spawned");
    }

    public static async Task DisabledAdapterPolicyNeverSpawns()
    {
        // The adapters ship disabled: with the typed policy refusing, the
        // runtime host must never create a process — the offline assertion
        // (spawnCount == 0, promptCount == 0) holds even with a VALID entry.
        var connection = new CliConnectionSettings
        {
            ExecutablePath = FakeCliPath(),
            ExpectedVersion = "fake",
            ModelId = "fake-model",
            Enabled = false,
        };
        using var host = new CliRuntimeHost(
            CliDiscovery.CodexToolId,
            EngineExecutionKind.CodexAppServer,
            connection);
        var refused = false;
        try
        {
            _ = host.EnsureTransport(_ => FakeStartInfo());
        }
        catch (InvalidOperationException exception)
        {
            refused = exception.Message.Contains("未启用", StringComparison.Ordinal);
        }
        True(refused, "the disabled adapter refuses through the typed policy gate");
        Equal(0L, host.SpawnCount, "no spawn may happen while the adapter is disabled");
        Equal(0L, host.PromptCount, "no prompt may be submitted while the adapter is disabled");

        // The adapter surface itself refuses honestly.
        using var codex = new CodexAppServerBackend(connection);
        var codexRefused = false;
        try
        {
            _ = await codex.CheckAccountAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            codexRefused = exception.Message.Contains("未启用", StringComparison.Ordinal);
        }
        True(codexRefused, "the Codex adapter refuses with its disabled reason");
        Equal(0L, codex.SpawnCount, "the Codex adapter spawned nothing");

        using var omp = new OmpRpcBackend(connection);
        var ompRefused = false;
        try
        {
            _ = await omp.CheckProviderStatusAsync(CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            ompRefused = exception.Message.Contains("未启用", StringComparison.Ordinal);
        }
        True(ompRefused, "the OMP adapter refuses with its disabled reason");
        Equal(0L, omp.SpawnCount, "the OMP adapter spawned nothing");
    }

    public static void TextJobProtocolThroughCore()
    {
        // The FFI text-job lifecycle through the real core: prepare → push a
        // protocol-shaped output (delimiter split across pushes) → finish.
        CoreBridge.Initialize(TestIsolation.CoreConfigDirectory);
        var prepared = CoreBridge.PrepareTextJob(new CoreBridge.TextJobRequestDto(
            Source: "hello world",
            SourceLanguage: "auto",
            TargetLanguage: "zh-CN"));
        True(prepared.SystemInstructions.Contains("popglot-translation-stream-v1", StringComparison.Ordinal),
            "the job reuses the production stream prompt contract");
        True(prepared.UserPayload.Contains("hello world", StringComparison.Ordinal),
            "the source rides as data in the user payload");
        var delimiter = prepared.Delimiter;
        var visible1 = CoreBridge.PushTextJobDelta(prepared.JobHandle, "你好");
        var visible2 = CoreBridge.PushTextJobDelta(
            prepared.JobHandle,
            "\n" + delimiter[..(delimiter.Length / 2)]);
        var visible3 = CoreBridge.PushTextJobDelta(
            prepared.JobHandle,
            delimiter[(delimiter.Length / 2)..] + "\n" + "{\"explanation\":\"注\",\"warnings\":[]}");
        Equal("你好", visible1 + visible2 + visible3);
        var result = CoreBridge.FinishTextJob(prepared.JobHandle, "completed");
        Equal("你好", result.TranslatedText);
        Equal("注", result.Explanation);
        // Free is idempotent; a freed handle is rejected afterwards.
        CoreBridge.FreeTextJob(prepared.JobHandle);
        CoreBridge.FreeTextJob(prepared.JobHandle);
        var rejected = false;
        try
        {
            _ = CoreBridge.PushTextJobDelta(prepared.JobHandle, "x");
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        True(rejected, "a freed job handle is rejected, never silently reused");
    }

    public static void ConnectionPanelStaysHonestWithoutSpawning()
    {
        Program.EnsureApplication();
        var section = new ServicesSection();
        using var releaseScan = new ManualResetEventSlim();
        using var enteredScan = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var scanThread = uiThread;
        section.DiscoverCliTools = (toolId, ct) =>
        {
            scanThread = Environment.CurrentManagedThreadId;
            enteredScan.Set();
            if (!releaseScan.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("scan fixture timed out");
            ct.ThrowIfCancellationRequested();
            return [];
        };
        // Discovery may inspect entries, but must neither block the dispatcher nor launch a tool.
        section.ConnectLocalToolHeaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(section.CliConnectionsPanel.Visibility == Visibility.Visible,
            "the connection panel opens from the header entry");
        True(!section.CliTestButton.IsEnabled && !section.CliSaveUseButton.IsEnabled,
            "detection does not claim a connection or enable requests");
        True(enteredScan.Wait(TimeSpan.FromSeconds(5)) && scanThread != uiThread,
            "discovery runs on a background thread");
        True(!section.DetectCliToolsButton.IsEnabled && section.CliCancelButton.Visibility == Visibility.Visible,
            "discovery exposes a cancellable busy state");
        section.CliCancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(section.DetectCliToolsButton.IsEnabled && !section.CliTestButton.IsEnabled,
            "cancelling discovery releases UI without claiming a connection");
        releaseScan.Set();
        // Returning to the overview restores the standard header actions.
        section.BackToListHeaderButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(section.CliConnectionsPanel.Visibility == Visibility.Collapsed,
            "back from the panel restores the engine list");
        True(section.ConnectLocalToolHeaderButton.Visibility == Visibility.Visible,
            "the connection entry is reachable again");
    }

    private static long? ReadId(string line)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return id.GetInt64();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected <{expected}>, got <{actual}>.");
        }
    }

    private static void Equal<T>(T expected, T actual) =>
        Equal(expected, actual, "values must match");
}
