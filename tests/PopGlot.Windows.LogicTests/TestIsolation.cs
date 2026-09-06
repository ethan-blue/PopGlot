using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using PopGlot.Windows;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// Process-wide isolation bootstrap for the Windows logic tests.
///
/// Installed once at the top of Main, BEFORE any test runs:
///  - every storage path resolves under an isolated temp root (StoragePaths.RootOverride),
///  - the profile store is seeded with a fixed "Demo Text Service" fixture,
///  - history/vocabulary files contain only synthetic entries,
///  - credentials go to an in-memory vault, never Windows Credential Manager,
///  - the free-engine HTTP boundary refuses every non-loopback destination and
///    counts the refusals, so an accidental public send fails the run.
///
/// The bootstrap also snapshots the user's real PopGlot config files before
/// the run and <see cref="VerifyRealFilesUnchanged"/> proves at the end that
/// nothing touched them (hashes only — file contents are never printed).
/// </summary>
internal static class TestIsolation
{
    public static string Root { get; private set; } = string.Empty;
    public static string CoreConfigDirectory => Path.Combine(Root, "core");
    public static string HistoryPath => Path.Combine(Root, "history.json");
    public static string VocabularyPath => Path.Combine(Root, "vocabulary.json");

    /// <summary>Public-network send attempts that the guard refused.</summary>
    public static int BlockedPublicSends => _blockedPublicSends;

    /// <summary>Sends the guard let through because the host was loopback.</summary>
    public static int LoopbackSends => _loopbackSends;

    public static InMemoryCredentialVault Vault => _vault
        ?? throw new InvalidOperationException("Test isolation is not initialized.");

    private static int _blockedPublicSends;
    private static int _loopbackSends;
    private static InMemoryCredentialVault? _vault;
    private static Dictionary<string, string>? _realFileHashes;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        Root = Path.Combine(
            Path.GetTempPath(),
            "popglot-logic-tests",
            $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(CoreConfigDirectory);

        // 1. Every default storage path now lives under the test root.
        StoragePaths.RootOverride = Root;

        // 2. Fixed demo services: the only provider names any rendered surface
        //    can show, whatever services the developer's machine has configured.
        File.WriteAllText(
            Path.Combine(Root, "product-config.json"),
            DemoProductConfig,
            new UTF8Encoding(false));

        // 3. Synthetic history (short + long) and vocabulary fixtures.
        File.WriteAllText(Path.Combine(Root, "history.json"), SyntheticHistory, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Root, "vocabulary.json"), SyntheticVocabulary, new UTF8Encoding(false));

        // 4. Credentials stay in memory.
        _vault = new InMemoryCredentialVault();
        CredentialStore.OverrideVault = _vault;

        // 5. Free-engine traffic must never leave the machine in tests.
        FreeTranslateService.HttpSenderOverride = GuardedSendAsync;

        // 6. Consent persistence lands in the isolated shell settings file.
        OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load(Path.Combine(Root, "windows-shell.json"));
        OutboundPolicy.SettingsSaver = settings => ShellSettingsStore.Save(settings, Path.Combine(Root, "windows-shell.json"));

        // 7. Bind the native core to the isolation directory. Production data
        //    under %LOCALAPPDATA%\PopGlot is never read or written.
        CoreBridge.Initialize(CoreConfigDirectory);

        // 8. Snapshot the real files (hashes only) so the run can prove it
        //    left them untouched.
        _realFileHashes = SnapshotRealFiles();

        _initialized = true;
    }

    /// <summary>
    /// Fails unless the isolation overrides are actually installed. Runs as a
    /// regular test so a broken bootstrap is visible in the report.
    /// </summary>
    public static void AssertActive()
    {
        True(StoragePaths.RootOverride is not null, "StoragePaths.RootOverride must be installed");
        True(Root.StartsWith(Path.Combine(Path.GetTempPath(), "popglot-logic-tests"), StringComparison.Ordinal),
            "the isolation root must live under the dedicated temp tree");
        True(ReferenceEquals(CredentialStore.OverrideVault, _vault) && _vault is not null,
            "the credential vault must be the in-memory stub");
        True(FreeTranslateService.HttpSenderOverride is not null,
            "the free-engine HTTP boundary must be guarded");
        True(File.Exists(Path.Combine(Root, "product-config.json")), "the demo config fixture must exist");

        // The demo fixture is what ProfileManager actually resolves.
        var active = ProfileManager.Load().TryGetActiveProfile();
        True(active is not null && active.Name == "Demo Text Service" && active.TextModel == "demo-text-model",
            $"the isolated profile store must resolve Demo Text Service / demo-text-model, got {active?.Name} / {active?.TextModel}");
    }

    /// <summary>
    /// A real running PopGlot instance owns the global hotkeys, reads the
    /// clipboard and answers the single-instance channel. A suite that runs
    /// beside it fails in confusing cascades (observed 2026-09-05: dispatcher
    /// resource exceptions surfacing from Save_Click). Fail fast instead of
    /// leaving a mysterious failure trail.
    /// </summary>
    public static void AssertNoConflictingAppInstance()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("PopGlot");
        True(running.Length == 0,
            $"a real PopGlot instance is running (PID {running.FirstOrDefault()?.Id}); " +
            "it owns the global hotkeys/clipboard/single-instance channel and conflicts with this suite — " +
            "quit it from the tray before running the tests");
    }

    /// <summary>
    /// Proves the run did not create, delete or modify any of the user's real
    /// PopGlot files. Throws with file names only — never contents.
    /// </summary>
    public static void VerifyRealFilesUnchanged()
    {
        if (_realFileHashes is null)
        {
            throw new InvalidOperationException("Test isolation is not initialized.");
        }

        var now = SnapshotRealFiles();
        var problems = new List<string>();
        foreach (var name in _realFileHashes.Keys.Union(now.Keys))
        {
            var before = _realFileHashes.GetValueOrDefault(name);
            var after = now.GetValueOrDefault(name);
            if (before != after)
            {
                problems.Add(before is null ? $"{name} (created by the test run)" : after is null ? $"{name} (deleted)" : $"{name} (modified)");
            }
        }
        True(problems.Count == 0,
            "the test run touched the real user config: " + string.Join("; ", problems));
    }

    private static Dictionary<string, string> SnapshotRealFiles()
    {
        var realDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(realDir))
        {
            return hashes;
        }
        foreach (var file in Directory.EnumerateFiles(realDir, "*", SearchOption.TopDirectoryOnly))
        {
            using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            hashes[Path.GetFileName(file)] = hash;
        }
        return hashes;
    }

    private static async Task<HttpResponseMessage> GuardedSendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        if (IsLoopbackHost(host))
        {
            Interlocked.Increment(ref _loopbackSends);
            throw new InvalidOperationException(
                $"隔离测试拦截了回环请求（{host}）：需要本地 mock 的用例必须安装自己的发送器。");
        }
        Interlocked.Increment(ref _blockedPublicSends);
        throw new InvalidOperationException(
            $"隔离测试禁止公网请求（{host}）；这是网络隔离护栏，不是服务故障。");
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.Ordinal) ||
        host.Equals("[::1]", StringComparison.Ordinal) ||
        host.Equals("::1", StringComparison.Ordinal);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private const string DemoProductConfig = """
        {
          "SchemaVersion": 6,
          "ActiveProfileId": "demo-text",
          "VisionProfileId": null,
          "PreferFreeEngine": false,
          "Profiles": [
            {
              "Id": "demo-text",
              "Name": "Demo Text Service",
              "ProviderType": 0,
              "ApiBaseUrl": "http://127.0.0.1:9/v1",
              "TextEndpoint": "/chat/completions",
              "VisionEndpoint": "/chat/completions",
              "TextModel": "demo-text-model",
              "VisionModel": "demo-vision-model",
              "ExtraHeaders": {},
              "AnthropicVersion": "2023-06-01",
              "SupportsText": true,
              "SupportsVision": true,
              "AllowInsecureTls": false,
              "CredentialTarget": "PopGlot/provider/demo-text",
              "IsLocal": true
            }
          ]
        }
        """;

    private const string SyntheticHistory = """
        [
          {
            "Id": "11111111-1111-1111-1111-111111111111",
            "CreatedAt": "2026-09-05T08:00:00Z",
            "SourceKind": "输入",
            "Source": "demo short source text",
            "Translation": "演示短译文",
            "Explanation": "",
            "ProtectedTerms": [],
            "SourceLanguage": "en",
            "TargetLanguage": "zh-CN"
          },
          {
            "Id": "22222222-2222-2222-2222-222222222222",
            "CreatedAt": "2026-09-05T08:05:00Z",
            "SourceKind": "划词",
            "Source": "demo long source: NullReferenceException occurred while foo_bar_baz attempted to load `config.json`; retry with the correct path and verify the deployment layout before continuing.",
            "Translation": "演示长译文：`foo_bar_baz` 在加载 `config.json` 时发生 NullReferenceException；请使用正确路径重试，并在继续之前核实部署布局。",
            "Explanation": "演示解释文本，用于渲染较长的结果卡片。",
            "ProtectedTerms": ["foo_bar_baz"],
            "SourceLanguage": "en",
            "TargetLanguage": "zh-CN"
          },
          {
            "Id": "33333333-3333-3333-3333-333333333333",
            "CreatedAt": "2026-09-05T08:10:00Z",
            "SourceKind": "截图",
            "Source": "demo screenshot transcription",
            "Translation": "演示截图识别后的译文",
            "Explanation": "",
            "ProtectedTerms": [],
            "SourceLanguage": "auto",
            "TargetLanguage": "zh-CN"
          }
        ]
        """;

    private const string SyntheticVocabulary = """
        [
          {
            "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            "CreatedAt": "2026-09-05T08:00:00Z",
            "Word": "demo_word_one",
            "Translation": "演示词条一",
            "Phonetic": "",
            "Explanation": "合成演示数据",
            "SourceLanguage": "en",
            "TargetLanguage": "zh-CN",
            "Tags": ["demo"]
          },
          {
            "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            "CreatedAt": "2026-09-05T08:01:00Z",
            "Word": "demo_word_two",
            "Translation": "演示词条二",
            "Phonetic": "",
            "Explanation": "",
            "SourceLanguage": "en",
            "TargetLanguage": "zh-CN",
            "Tags": []
          }
        ]
        """;
}

/// <summary>
/// In-memory credential vault used by the isolated test host. Records every
/// operation so tests can audit what touched credentials, and never touches
/// Windows Credential Manager.
/// </summary>
internal sealed class InMemoryCredentialVault : ICredentialVault
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public List<string> Operations { get; } = [];

    public bool HasCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate)
        {
            Operations.Add($"has:{target}");
            return _secrets.ContainsKey(target);
        }
    }

    public string? LoadCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate)
        {
            Operations.Add($"load:{target}");
            return _secrets.GetValueOrDefault(target);
        }
    }

    public void SaveCredential(string secret, string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate)
        {
            Operations.Add($"save:{target}");
            _secrets[target] = secret;
        }
    }

    public void DeleteCredential(string target = "PopGlot/OpenAICompatibleApiKey")
    {
        lock (_gate)
        {
            Operations.Add($"delete:{target}");
            _secrets.Remove(target);
        }
    }
}
