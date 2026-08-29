using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopGlot.Windows.Services;

internal sealed class ProviderProfile
{
    public ProviderProfile()
    {
    }

    /// <summary>Full copy, used by tests and migrations to derive variants.</summary>
    public ProviderProfile(ProviderProfile source)
    {
        Id = source.Id;
        Name = source.Name;
        ProviderType = source.ProviderType;
        ApiBaseUrl = source.ApiBaseUrl;
        TextEndpoint = source.TextEndpoint;
        VisionEndpoint = source.VisionEndpoint;
        TextModel = source.TextModel;
        VisionModel = source.VisionModel;
        ExtraHeaders = new Dictionary<string, string>(source.ExtraHeaders, StringComparer.OrdinalIgnoreCase);
        AnthropicVersion = source.AnthropicVersion;
        SupportsText = source.SupportsText;
        SupportsVision = source.SupportsVision;
        AllowInsecureTls = source.AllowInsecureTls;
        CredentialTarget = source.CredentialTarget;
        IsLocal = source.IsLocal;
    }

    public string Id { get; set; } = "openai-default";
    public string Name { get; set; } = "OpenAI";
    public ProviderType ProviderType { get; set; } = ProviderType.OpenAiCompatible;
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string TextEndpoint { get; set; } = "/chat/completions";
    public string VisionEndpoint { get; set; } = "/chat/completions";
    public string TextModel { get; set; } = "gpt-4o-mini";
    public string VisionModel { get; set; } = "gpt-4o-mini";
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    public string AnthropicVersion { get; set; } = "2023-06-01";
    public bool SupportsText { get; set; } = true;
    public bool SupportsVision { get; set; } = true;
    public bool AllowInsecureTls { get; set; }
    public string CredentialTarget { get; set; } = "PopGlot/provider/openai-default";
    public bool IsLocal { get; set; }

    public static ProviderProfile CreateOpenAi() => new()
    {
        Id = "openai-default",
        Name = "OpenAI",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "https://api.openai.com/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = "gpt-4o-mini",
        VisionModel = "gpt-4o-mini",
        CredentialTarget = "PopGlot/provider/openai-default",
    };

    public static ProviderProfile CreateDeepSeek() => new()
    {
        Id = "deepseek",
        Name = "DeepSeek",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "https://api.deepseek.com/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = "deepseek-chat",
        VisionModel = "",
        SupportsVision = false,
        CredentialTarget = "PopGlot/provider/deepseek",
    };

    public static ProviderProfile CreateOllama() => new()
    {
        Id = "ollama-local",
        Name = "Ollama (本地)",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "http://localhost:11434/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = "qwen2.5:7b",
        VisionModel = "llava:7b",
        CredentialTarget = "PopGlot/provider/ollama-local",
        IsLocal = true,
    };

    public static ProviderProfile CreateGemini() => new()
    {
        Id = "gemini",
        Name = "Google Gemini",
        ProviderType = ProviderType.GeminiGenerateContent,
        ApiBaseUrl = "https://generativelanguage.googleapis.com",
        TextEndpoint = "/v1beta/models/{model}:generateContent",
        VisionEndpoint = "/v1beta/models/{model}:generateContent",
        TextModel = "gemini-3.6-flash",
        VisionModel = "gemini-3.6-flash",
        CredentialTarget = "PopGlot/provider/gemini",
    };

    public static ProviderProfile CreateClaude() => new()
    {
        Id = "claude",
        Name = "Anthropic Claude",
        ProviderType = ProviderType.AnthropicMessages,
        ApiBaseUrl = "https://api.anthropic.com",
        TextEndpoint = "/v1/messages",
        VisionEndpoint = "/v1/messages",
        TextModel = "claude-3-5-sonnet-latest",
        VisionModel = "claude-3-5-sonnet-latest",
        CredentialTarget = "PopGlot/provider/claude",
    };

    public static ProviderProfile CreateZhipu() => new()
    {
        Id = "zhipu",
        Name = "智谱 GLM",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "https://open.bigmodel.cn/api/paas/v4",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = "glm-4-flash",
        VisionModel = "glm-4v-flash",
        CredentialTarget = "PopGlot/provider/zhipu",
    };

    public ProviderSettings ToProviderSettings(ProviderSettings baseSettings) =>
        baseSettings with
        {
            ProviderType = ProviderType,
            ApiBaseUrl = ApiBaseUrl,
            TextEndpoint = TextEndpoint,
            VisionEndpoint = VisionEndpoint,
            TextModel = TextModel,
            VisionModel = VisionModel,
            ExtraHeaders = ExtraHeaders,
            AnthropicVersion = AnthropicVersion,
            SupportsText = SupportsText,
            SupportsVision = SupportsVision,
            AllowInsecureTls = AllowInsecureTls || baseSettings.AllowInsecureTls,
        };
}

internal sealed class CoreProductConfig
{
    public int SchemaVersion { get; set; } = 5;
    public string ActiveProfileId { get; set; } = string.Empty;
    public string? VisionProfileId { get; set; }
    public List<ProviderProfile> Profiles { get; set; } = [];

    public ProviderProfile GetActiveProfile() =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId)
        ?? Profiles.FirstOrDefault()
        ?? ProviderProfile.CreateOpenAi();
}

/// <summary>
/// Factory provider templates. These are NOT user services: they only seed
/// the add-service flow and never appear in the configured list. A fresh
/// install has an empty ConfiguredServices list.
/// </summary>
internal static class ProviderCatalog
{
    public static IReadOnlyList<ProviderProfile> Templates =>
    [
        ProviderProfile.CreateOpenAi(),
        ProviderProfile.CreateDeepSeek(),
        ProviderProfile.CreateOllama(),
        ProviderProfile.CreateGemini(),
        ProviderProfile.CreateClaude(),
        ProviderProfile.CreateZhipu(),
    ];

    public static ProviderProfile? Find(string id) =>
        Templates.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// True when the profile is byte-for-byte a factory template: same id and
    /// the same visible fields. Such entries only existed because older
    /// builds seeded them; user-configured ones (renamed, re-modelled, with a
    /// key, or edited) never match.
    /// </summary>
    public static bool IsPristineTemplate(ProviderProfile profile)
    {
        var template = Find(profile.Id);
        if (template is null)
        {
            return false;
        }
        return string.Equals(profile.Name, template.Name, StringComparison.Ordinal) &&
            profile.ProviderType == template.ProviderType &&
            string.Equals(profile.ApiBaseUrl, template.ApiBaseUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.TextEndpoint, template.TextEndpoint, StringComparison.Ordinal) &&
            string.Equals(profile.VisionEndpoint, template.VisionEndpoint, StringComparison.Ordinal) &&
            string.Equals(profile.TextModel, template.TextModel, StringComparison.Ordinal) &&
            string.Equals(profile.VisionModel, template.VisionModel, StringComparison.Ordinal) &&
            profile.SupportsText == template.SupportsText &&
            profile.SupportsVision == template.SupportsVision;
    }
}

internal static class ProfileManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "product-config.json");

    /// <summary>Test seam: redirects the config file (also disables seeding).</summary>
    internal static string? ConfigPathOverride;

    /// <summary>Test seam: clears the process-wide cache between scenarios.</summary>
    internal static void ResetForTests()
    {
        _cached = null;
        ConfigPathOverride = null;
    }

    private static CoreProductConfig? _cached;

    private static string EffectivePath =>
        ConfigPathOverride ?? ConfigPath;

    public static CoreProductConfig Load()
    {
        if (_cached != null)
        {
            return _cached;
        }
        var path = EffectivePath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<CoreProductConfig>(json);
                if (config is not null)
                {
                    // Schema v4 and older seeded factory templates as if the
                    // user had configured them; migrate them away once.
                    if (config.SchemaVersion < 5)
                    {
                        config = MigrateToV5(config, path);
                    }
                    _cached = config;
                }
            }
        }
        catch
        {
            // fallback to defaults on corrupt/missing file
        }

        // First run with profiles: adopt the live provider settings as the
        // active profile so the service list reflects what actually runs
        // instead of masking it with factory presets. Never runs for tests
        // using the override path.
        if (_cached is null && ConfigPathOverride is null)
        {
            _cached = SeedFromLiveSettings();
        }
        _cached ??= new CoreProductConfig();
        return _cached;
    }

    /// <summary>
    /// Schema v4 → v5: factory templates that the user never touched stop
    /// posing as configured services. Anything the user customised — renamed,
    /// re-modelled, holding a key, or an adopted live setting — is preserved.
    /// The pre-migration file is kept as .bak by the normal save path.
    /// </summary>
    private static CoreProductConfig MigrateToV5(CoreProductConfig config, string path)
    {
        var kept = new List<ProviderProfile>();
        foreach (var profile in config.Profiles)
        {
            var hasKey = false;
            try
            {
                hasKey = !string.IsNullOrWhiteSpace(profile.CredentialTarget) &&
                    CredentialStore.HasApiKey(profile.CredentialTarget);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Vault unavailable: keep the profile rather than risk loss.
                hasKey = true;
            }

            if (!ProviderCatalog.IsPristineTemplate(profile) || hasKey)
            {
                kept.Add(profile);
            }
        }

        var migrated = new CoreProductConfig
        {
            SchemaVersion = 5,
            Profiles = kept,
            ActiveProfileId = kept.Any(p => p.Id == config.ActiveProfileId)
                ? config.ActiveProfileId
                : (kept.FirstOrDefault()?.Id ?? string.Empty),
            VisionProfileId = kept.Any(p => p.Id == config.VisionProfileId)
                ? config.VisionProfileId
                : null,
        };
        try
        {
            // Writes the migrated schema and rotates the old file into .bak.
            _cached = null;
            Save(migrated);
        }
        catch (Exception)
        {
            // Disk write failed: still return the migrated view so the UI
            // reflects reality; the original file stays untouched on disk.
        }
        return migrated;
    }

    private static CoreProductConfig? SeedFromLiveSettings()
    {
        try
        {
            var live = CoreBridge.GetSettings();
            var unconfigured =
                string.Equals(live.ApiBaseUrl, "https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(live.TextModel, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase) &&
                !CredentialStore.HasApiKey(CredentialStore.DefaultTargetName);
            if (unconfigured)
            {
                // A fresh install has nothing to preserve; presets are more
                // useful than an empty echo of the defaults.
                return null;
            }

            var profile = new ProviderProfile
            {
                Id = "default",
                Name = string.IsNullOrWhiteSpace(live.TextModel) ? "我的服务" : live.TextModel,
                ProviderType = live.ProviderType,
                ApiBaseUrl = live.ApiBaseUrl,
                TextEndpoint = live.TextEndpoint,
                VisionEndpoint = live.VisionEndpoint,
                TextModel = live.TextModel,
                VisionModel = live.VisionModel,
                ExtraHeaders = new Dictionary<string, string>(
                    live.ExtraHeaders.ToDictionary(pair => pair.Key, pair => pair.Value),
                    StringComparer.OrdinalIgnoreCase),
                AnthropicVersion = live.AnthropicVersion,
                SupportsText = live.SupportsText,
                SupportsVision = live.SupportsVision,
                AllowInsecureTls = live.AllowInsecureTls,
                CredentialTarget = "PopGlot/provider/default",
                IsLocal = live.TargetsLocalRuntime,
            };
            return new CoreProductConfig
            {
                ActiveProfileId = profile.Id,
                Profiles = [profile],
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public static void Save(CoreProductConfig config)
    {
        var dir = Path.GetDirectoryName(EffectivePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Same durability contract as the core settings: temp file, flush,
        // atomic replace, previous copy kept as .bak. The in-memory cache is
        // updated only AFTER the file replace succeeds, so a failed save can
        // never leave memory and disk disagreeing.
        var tempPath = EffectivePath + ".tmp";
        var bakPath = EffectivePath + ".bak";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (File.Exists(EffectivePath))
        {
            File.Copy(EffectivePath, bakPath, overwrite: true);
        }
        File.Move(tempPath, EffectivePath, overwrite: true);
        _cached = config;
    }

    /// <summary>
    /// Decides the final profile id and the credential target for a save
    /// BEFORE any key is written. A new profile mints its own per-profile
    /// target; editing keeps the existing profile's own target — so a
    /// DeepSeek/Gemini/Claude key can never land in the OpenAI default slot.
    /// </summary>
    public static (string ProfileId, string CredentialTarget) ResolveSaveTarget(
        CoreProductConfig config, string? editingProfileId)
    {
        if (editingProfileId is null)
        {
            var mintedId = $"p-{Guid.NewGuid().ToString("N")[..10]}";
            return (mintedId, $"PopGlot/provider/{mintedId}");
        }

        var existing = config.Profiles.FirstOrDefault(profile => profile.Id == editingProfileId);
        if (existing is null)
        {
            return (editingProfileId, $"PopGlot/provider/{editingProfileId}");
        }
        return (existing.Id,
            string.IsNullOrWhiteSpace(existing.CredentialTarget)
                ? $"PopGlot/provider/{existing.Id}"
                : existing.CredentialTarget);
    }

    /// <summary>
    /// Resolves where the active profile's API key lives. A key saved before
    /// profiles existed stays readable at the legacy target until the user
    /// edits the key, so switching to profiles never silently drops it.
    /// </summary>
    public static string ResolveActiveCredentialTarget()
    {
        try
        {
            var profile = Load().GetActiveProfile();
            if (!string.IsNullOrWhiteSpace(profile.CredentialTarget))
            {
                if (CredentialStore.HasApiKey(profile.CredentialTarget))
                {
                    return profile.CredentialTarget;
                }
                if (CredentialStore.HasApiKey(CredentialStore.DefaultTargetName))
                {
                    return CredentialStore.DefaultTargetName;
                }
                return profile.CredentialTarget;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Fall through to the legacy target.
        }
        return CredentialStore.DefaultTargetName;
    }
}
