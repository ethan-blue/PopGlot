using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopGlot.Windows.Services;

internal sealed class ProviderProfile
{
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
        TextModel = "gemini-2.0-flash",
        VisionModel = "gemini-2.0-flash",
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
    public int SchemaVersion { get; set; } = 4;
    public string ActiveProfileId { get; set; } = "openai-default";
    public List<ProviderProfile> Profiles { get; set; } = [
        ProviderProfile.CreateOpenAi(),
        ProviderProfile.CreateDeepSeek(),
        ProviderProfile.CreateOllama(),
        ProviderProfile.CreateGemini(),
        ProviderProfile.CreateClaude(),
    ];

    public ProviderProfile GetActiveProfile() =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles[0];
}

internal static class ProfileManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "product-config.json");

    private static CoreProductConfig? _cached;

    public static CoreProductConfig Load()
    {
        if (_cached != null)
        {
            return _cached;
        }
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                _cached = JsonSerializer.Deserialize<CoreProductConfig>(json);
            }
        }
        catch
        {
            // fallback to defaults on corrupt/missing file
        }

        // First run with profiles: adopt the live provider settings as the
        // active profile so the service list reflects what actually runs
        // instead of masking it with factory presets.
        _cached ??= SeedFromLiveSettings();
        _cached ??= new CoreProductConfig();
        return _cached;
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
        _cached = config;
        var dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Same durability contract as the core settings: temp file, flush,
        // atomic replace, previous copy kept as .bak.
        var tempPath = ConfigPath + ".tmp";
        var bakPath = ConfigPath + ".bak";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (File.Exists(ConfigPath))
        {
            File.Copy(ConfigPath, bakPath, overwrite: true);
        }
        File.Move(tempPath, ConfigPath, overwrite: true);
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
