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

    public ProviderProfile Clone() => new(this);

    public string Id { get; set; } = "openai-default";
    public string Name { get; set; } = "OpenAI";
    public ProviderType ProviderType { get; set; } = ProviderType.OpenAiCompatible;
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string TextEndpoint { get; set; } = "/chat/completions";
    public string VisionEndpoint { get; set; } = "/chat/completions";
    public string TextModel { get; set; } = string.Empty;
    public string VisionModel { get; set; } = string.Empty;
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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
        TextModel = string.Empty,
        VisionModel = string.Empty,
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

/// One fully resolved runtime route. A route carries the provider's complete
/// connection details plus the credential target that holds its key, so text
/// and vision can run against entirely different services.
internal sealed record ProviderRoute(ProviderProfile Profile, string CredentialTarget);

internal enum ScreenshotPipeline
{
    Unavailable,
    LocalOcr,
    VisionDirect,
    /// 视觉模型识别截图文字，译文由文本模型流式生成。
    VisionOcr,
}

/// <summary>
/// Authoritative, fully resolved runtime route shared by execution and every
/// route preview. It describes configured providers and the screenshot path
/// that is usable under the current privacy policy; it never contains keys.
/// </summary>
internal sealed record ResolvedRoute(
    ProviderRoute? Text,
    ProviderRoute? Vision,
    ScreenshotPipeline ScreenshotPipeline,
    bool MayUploadImage,
    string ExplanationZh);

internal sealed class CoreProductConfig
{
    public CoreProductConfig()
    {
    }

    public CoreProductConfig(CoreProductConfig source)
    {
        SchemaVersion = source.SchemaVersion;
        ActiveProfileId = source.ActiveProfileId;
        VisionProfileId = source.VisionProfileId;
        PreferFreeEngine = source.PreferFreeEngine;
        Profiles = source.Profiles.Select(p => new ProviderProfile(p)).ToList();
    }

    public CoreProductConfig Clone() => new(this);

    public int SchemaVersion { get; set; } = 6;
    public string ActiveProfileId { get; set; } = string.Empty;
    public string? VisionProfileId { get; set; }

    /// <summary>
    /// 用户在快速切换器里显式选择了内置免费引擎作为文字线路：即使配置了
    /// 其它服务，文字翻译也走免费引擎（仅文字；截图的视觉线路不受影响）。
    /// </summary>
    public bool PreferFreeEngine { get; set; }
    public List<ProviderProfile> Profiles { get; set; } = [];

    /// <summary>
    /// The configured default text service, or null when nothing is
    /// configured. Never invents a provider: an empty config means no
    /// provider, no model and no capability.
    /// </summary>
    public ProviderProfile? TryGetActiveProfile() =>
        PreferFreeEngine
            ? null
            : Profiles.FirstOrDefault(p => p.Id == ActiveProfileId && p.SupportsText) ??
              Profiles.FirstOrDefault(p => p.SupportsText);

    /// <summary>
    /// The explicit default vision service, or the text service when vision
    /// is configured to follow it. Empty configuration still resolves null.
    /// </summary>
    public ProviderProfile? TryGetVisionProfile() =>
        string.IsNullOrEmpty(VisionProfileId)
            ? TryGetActiveProfile() is { SupportsVision: true } active ? active : null
            : Profiles.FirstOrDefault(p => p.Id == VisionProfileId);
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
            ModelIsFactoryDefault(profile.Id, profile.TextModel, vision: false) &&
            ModelIsFactoryDefault(profile.Id, profile.VisionModel, vision: true) &&
            profile.SupportsText == template.SupportsText &&
            profile.SupportsVision == template.SupportsVision;
    }

    private static bool ModelIsFactoryDefault(string id, string model, bool vision)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return true;
        }
        var legacy = (id, vision) switch
        {
            ("openai-default", _) => "gpt-4o-mini",
            ("deepseek", false) => "deepseek-chat",
            ("ollama-local", false) => "qwen2.5:7b",
            ("ollama-local", true) => "llava:7b",
            ("gemini", _) => "gemini-3.6-flash",
            ("claude", _) => "claude-3-5-sonnet-latest",
            ("zhipu", false) => "glm-4-flash",
            ("zhipu", true) => "glm-4v-flash",
            _ => string.Empty,
        };
        return string.Equals(model, legacy, StringComparison.Ordinal);
    }
}

internal static class ProfileManager
{
    private static readonly object Gate = new();

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PopGlot",
        "product-config.json");

    /// <summary>Test seam: redirects the config file (also disables seeding).</summary>
    internal static string? ConfigPathOverride;

    /// <summary>Test seam: clears the process-wide cache between scenarios.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _cached = null;
            ConfigPathOverride = null;
        }
    }

    private static CoreProductConfig? _cached;

    private static string EffectivePath =>
        ConfigPathOverride ?? ConfigPath;

    public static CoreProductConfig Load()
    {
        lock (Gate)
        {
            if (_cached != null)
            {
                return _cached.Clone();
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
                        var originalVersion = config.SchemaVersion;
                        // Schema v4 and older seeded factory templates as if the
                        // user had configured them; migrate them away once.
                        if (config.SchemaVersion < 5)
                        {
                            config = MigrateToV5InMemory(config);
                        }
                        if (config.SchemaVersion < 6)
                        {
                            config = MigrateToV6InMemory(config);
                        }
                        if (originalVersion < 6)
                        {
                            try
                            {
                                SaveLocked(config);
                            }
                            catch
                            {
                                // Disk write failed: original file remains untouched,
                                // in-memory config can continue to be used.
                            }
                        }
                        _cached = config.Clone();
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
            return _cached.Clone();
        }
    }

    /// <summary>
    /// Schema v4 → v5 (in-memory only): factory templates that the user never touched stop
    /// posing as configured services. Anything the user customised — renamed,
    /// re-modelled, holding a key, or an adopted live setting — is preserved.
    /// </summary>
    private static CoreProductConfig MigrateToV5InMemory(CoreProductConfig config)
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

        return new CoreProductConfig
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
    }

    /// <summary>
    /// Schema v5 → v6 (in-memory only): model fields are the single source of truth for route
    /// roles. Older builds could save a VisionModel while SupportsVision was
    /// false, making a visibly configured image model unusable. Preserve every
    /// model and credential, derive both roles, and refresh locality from URL.
    /// A single identical model in both fields is intentionally dual-purpose.
    /// </summary>
    private static CoreProductConfig MigrateToV6InMemory(CoreProductConfig config)
    {
        foreach (var profile in config.Profiles)
        {
            profile.SupportsText = !string.IsNullOrWhiteSpace(profile.TextModel);
            profile.SupportsVision = !string.IsNullOrWhiteSpace(profile.VisionModel);
            profile.IsLocal = ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl);
        }
        config.SchemaVersion = 6;
        if (!config.Profiles.Any(profile =>
                profile.Id == config.ActiveProfileId && profile.SupportsText))
        {
            config.ActiveProfileId = config.Profiles.FirstOrDefault(profile => profile.SupportsText)?.Id
                ?? string.Empty;
        }
        if (config.VisionProfileId is { Length: > 0 } visionId &&
            !config.Profiles.Any(profile => profile.Id == visionId))
        {
            config.VisionProfileId = null;
        }
        return config;
    }

    private static CoreProductConfig? SeedFromLiveSettings()
    {
        try
        {
            var live = CoreBridge.GetSettings();
            // Fresh installs carry no model at all now; anything the user
            // actually configured (any model, any non-default URL, any key)
            // is worth adopting as their first service.
            var unconfigured =
                string.IsNullOrWhiteSpace(live.TextModel) &&
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
                SupportsText = !string.IsNullOrWhiteSpace(live.TextModel),
                SupportsVision = !string.IsNullOrWhiteSpace(live.VisionModel),
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
        lock (Gate)
        {
            SaveLocked(config);
        }
    }

    private static void SaveLocked(CoreProductConfig config)
    {
        var targetPath = EffectivePath;
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Same durability contract as the core settings: random temp file, flush,
        // atomic replace, previous copy kept as .bak. The in-memory cache is
        // updated only AFTER the file replace succeeds, so a failed save can
        // never leave memory and disk disagreeing.
        var tempPath = Path.Combine(
            string.IsNullOrEmpty(dir) ? "." : dir,
            $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        var bakPath = targetPath + ".bak";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(targetPath))
            {
                File.Copy(targetPath, bakPath, overwrite: true);
            }
            File.Move(tempPath, targetPath, overwrite: true);
            _cached = config.Clone();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup of temp file
                }
            }
        }
    }

    /// <summary>
    /// Mirrors the default text profile (plus an optional same-protocol vision
    /// profile) into the core so the running app uses it immediately. Shared
    /// by the settings page and the footer quick switcher so both paths can
    /// never diverge.
    /// </summary>
    public static void ApplyActiveToCore(CoreProductConfig config)
    {
        var current = CoreBridge.GetSettings();
        var textProfile = config.TryGetActiveProfile();
        if (textProfile is null)
        {
            // Nothing configured: keep user preferences but drop every
            // provider detail, so the core can never mistake an empty setup
            // for an OpenAI service.
            CoreBridge.SaveSettings(current with
            {
                ProviderType = ProviderType.OpenAiCompatible,
                ApiBaseUrl = "https://api.openai.com/v1",
                TextEndpoint = "/chat/completions",
                VisionEndpoint = "/chat/completions",
                TextModel = string.Empty,
                VisionModel = string.Empty,
                ExtraHeaders = new Dictionary<string, string>(),
                SupportsText = true,
                SupportsVision = false,
                VisionProvider = null,
            });
            return;
        }

        var settings = textProfile.ToProviderSettings(current);
        var visionProfile = config.TryGetVisionProfile();
        if (visionProfile is not null && visionProfile.SupportsVision &&
            !string.IsNullOrWhiteSpace(visionProfile.VisionModel))
        {
            // Never fold by host/protocol. The selected vision profile is an
            // independent route even when it happens to share the same URL.
            settings = settings with
            {
                SupportsVision = true,
                VisionProvider = new VisionProviderOverride(
                    visionProfile.ProviderType,
                    visionProfile.ApiBaseUrl,
                    visionProfile.VisionEndpoint,
                    visionProfile.VisionModel,
                    visionProfile.ExtraHeaders,
                    visionProfile.AnthropicVersion,
                    visionProfile.AllowInsecureTls),
            };
        }
        else
        {
            settings = settings with { SupportsVision = false, VisionProvider = null };
        }
        CoreBridge.SaveSettings(settings);
    }

    /// <summary>
    /// One-click text engine switch for the footer quick switcher. Validates
    /// readiness (key for cloud services, model and base URL) before saving,
    /// then applies to the core so the next translation uses it.
    /// </summary>
    public static bool TrySwitchActiveProfile(string profileId, out string error)
    {
        var config = Load();
        var profile = config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            error = "该引擎已不存在，请刷新列表。";
            return false;
        }
        if (!profile.SupportsText)
        {
            error = $"「{profile.Name}」未配置文字模型，不能作为文字引擎。";
            return false;
        }
        if (!IsProfileExecutable(profile, out var notReady))
        {
            error = $"无法切换到「{profile.Name}」：{notReady}。";
            return false;
        }
        config.ActiveProfileId = profile.Id;
        config.PreferFreeEngine = false;
        Save(config);
        ApplyActiveToCore(config);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 把内置免费引擎选为当前文字线路（仅文字；截图的视觉线路不变）。
    /// 先按出网策略校验：安全离线/断网/已拒绝授权时明确拒绝。
    /// </summary>
    public static bool TrySwitchToFreeEngine(out string error)
    {
        var settings = CoreBridge.GetSettings();
        if (!OutboundPolicy.AllowsFreeEngine(settings, out var denial))
        {
            error = denial is null
                ? "当前策略不允许使用免费引擎。"
                : $"{denial.Message} {denial.ActionableSuggestion}".Trim();
            return false;
        }
        var config = Load();
        config.PreferFreeEngine = true;
        Save(config);
        ApplyActiveToCore(config);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// One-click vision engine switch. Pass null to make vision follow the
    /// text service again.
    /// </summary>
    public static bool TrySwitchVisionProfile(string? profileId, out string error)
    {
        var config = Load();
        if (profileId is not null)
        {
            var profile = config.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null)
            {
                error = "该引擎已不存在，请刷新列表。";
                return false;
            }
            if (!profile.SupportsVision || string.IsNullOrWhiteSpace(profile.VisionModel))
            {
                error = $"「{profile.Name}」未配置图片模型，不能作为图片引擎。";
                return false;
            }
        }
        config.VisionProfileId = profileId;
        Save(config);
        ApplyActiveToCore(config);
        error = string.Empty;
        return true;
    }

    private static bool IsProfileExecutable(ProviderProfile profile, out string notReady)
    {
        if (string.IsNullOrWhiteSpace(profile.TextModel))
        {
            notReady = "缺少文字模型";
            return false;
        }
        if (string.IsNullOrWhiteSpace(profile.ApiBaseUrl))
        {
            notReady = "缺少 Base URL";
            return false;
        }
        if (!ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl) &&
            !CredentialStore.HasApiKey(profile.CredentialTarget))
        {
            notReady = "尚未配置密钥";
            return false;
        }
        notReady = string.Empty;
        return true;
    }

    /// <summary>
    /// Deletes a profile from the configuration and updates active/vision defaults.
    /// </summary>
    public static bool TryDeleteProfile(
        CoreProductConfig config,
        string profileId,
        out ProviderProfile? deletedProfile,
        out bool wasTextDefault,
        out bool wasVisionDefault)
    {
        deletedProfile = config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (deletedProfile is null)
        {
            wasTextDefault = false;
            wasVisionDefault = false;
            return false;
        }

        wasTextDefault = profileId == config.ActiveProfileId;
        wasVisionDefault = profileId == config.VisionProfileId;
        var nextDefault = config.Profiles.FirstOrDefault(p => p.Id != profileId && p.SupportsText);

        config.Profiles.RemoveAll(p => p.Id == profileId);
        if (wasTextDefault)
        {
            config.ActiveProfileId = nextDefault?.Id ?? string.Empty;
        }
        if (wasVisionDefault)
        {
            config.VisionProfileId = null;
        }
        return true;
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
    /// Resolves the two runtime routes end to end. The text route is the
    /// configured default service; the vision route is the default vision
    /// service only when it is genuinely usable — with its own complete
    /// provider contract, model and credential.
    /// Never fabricates a route from an empty config.
    /// </summary>
    public static (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes()
    {
        var config = Load();
        var textProfile = config.TryGetActiveProfile();
        if (textProfile is null)
        {
            return (null, null);
        }
        var textRoute = new ProviderRoute(textProfile, ResolveCredentialTargetFor(textProfile));

        var visionProfile = config.TryGetVisionProfile();
        if (visionProfile is null || !IsVisionReady(visionProfile))
        {
            return (textRoute, null);
        }
        return (textRoute, new ProviderRoute(visionProfile, ResolveCredentialTargetFor(visionProfile)));
    }

    /// <summary>
    /// Resolves the actual screenshot state machine once. Callers render or
    /// execute this result instead of reimplementing privacy/availability
    /// rules independently.
    /// </summary>
    public static ResolvedRoute ResolveRoute(ProviderSettings settings, bool localOcrAvailable)
    {
        var providers = ResolveRoutes();
        var visionLeavesDevice = providers.Vision is not null &&
            !ProviderSettings.IsLocalBaseUrl(providers.Vision.Profile.ApiBaseUrl);
        var visionUsable = providers.Vision is not null &&
            (!visionLeavesDevice ||
                (settings.NetworkEnabled &&
                 !settings.SafeDevMode &&
                 settings.AllowImageUploadInAuto));

        if (settings.Mode == TranslationMode.LocalOcr)
        {
            return localOcrAvailable
                ? new(providers.Text, providers.Vision, ScreenshotPipeline.LocalOcr, false,
                    "本地 OCR 识别截图，识别出的文字进入统一文字翻译线路。")
                : new(providers.Text, providers.Vision, ScreenshotPipeline.Unavailable, false,
                    "已指定本地 OCR，但系统没有可用的 OCR 语言包。");
        }

        // VisionDirect is an explicit user choice. It must never silently
        // become OCR: either the selected vision profile is executable or the
        // operation is blocked with a precise reason.
        if (settings.Mode == TranslationMode.VisionDirect)
        {
            return visionUsable && providers.Vision is not null
                ? new(providers.Text, providers.Vision, ScreenshotPipeline.VisionDirect,
                    visionLeavesDevice, "已按设置使用所选视觉模型直接识别并翻译截图。")
                : new(providers.Text, providers.Vision, ScreenshotPipeline.Unavailable, false,
                    providers.Vision is null
                        ? "未配置可用的图片服务，请先选择图片模型。"
                        : visionLeavesDevice && !settings.AllowImageUploadInAuto
                            ? "所选图片服务为远程服务，但当前未允许截图离开设备。"
                            : "所选图片服务当前被网络或安全模式阻止。");
        }

        // 视觉识别 + 文本翻译：需要视觉服务可用，且有一条可执行的文字
        // 线路来承接译文。缺任何一段都明确阻断，不静默降级。
        if (settings.Mode == TranslationMode.VisionOcr)
        {
            if (visionUsable && providers.Vision is not null && providers.Text is not null)
            {
                return new(providers.Text, providers.Vision, ScreenshotPipeline.VisionOcr,
                    visionLeavesDevice,
                    visionLeavesDevice
                        ? "已授权视觉模型识别截图文字（截图将上传），译文由文本模型流式生成。"
                        : "本地视觉服务识别截图文字，译文由文本模型流式生成，图片不离开本机。");
            }
            return new(providers.Text, providers.Vision, ScreenshotPipeline.Unavailable, false,
                providers.Vision is null
                    ? "未配置可用的图片服务：视觉识别需要先选择图片模型。"
                    : !visionUsable && visionLeavesDevice && !settings.AllowImageUploadInAuto
                        ? "所选图片服务为远程服务，但当前未允许截图离开设备。"
                        : providers.Text is null
                            ? "视觉识别 + 文本翻译需要同时配置图片模型与文字模型。"
                            : "所选图片服务当前被网络或安全模式阻止。");
        }

        if (settings.Mode == TranslationMode.Auto && localOcrAvailable)
        {
            return new(providers.Text, providers.Vision, ScreenshotPipeline.LocalOcr, false,
                "自动模式优先使用本地 OCR；截图不会上传，识别出的文字进入统一文字翻译线路。");
        }

        if (visionUsable)
        {
            // 自动模式下本地 OCR 不可用：云端视觉服务优先「识别 + 文本
            // 翻译」两段式（译文由文本模型流式输出，通常更快更稳）；本
            // 地视觉服务保持直译，一次调用即可，图片也不离开本机。
            if (visionLeavesDevice && providers.Text is not null && providers.Vision is not null)
            {
                return new(providers.Text, providers.Vision, ScreenshotPipeline.VisionOcr, true,
                    "本地 OCR 不可用；已授权视觉模型识别截图（截图将上传），译文由文本模型生成。");
            }
            return new(providers.Text, providers.Vision, ScreenshotPipeline.VisionDirect, visionLeavesDevice,
                visionLeavesDevice
                    ? "本地 OCR 不可用；已授权回退到独立视觉服务并上传截图。"
                    : "本地 OCR 不可用；将回退到本地视觉服务，图片不离开本机。");
        }

        if (localOcrAvailable)
        {
            return new(providers.Text, providers.Vision, ScreenshotPipeline.LocalOcr, false,
                settings.Mode == TranslationMode.VisionDirect
                    ? "视觉服务未就绪或截图上传未授权，已回退到本地 OCR。"
                    : "使用本地 OCR；截图不会上传。");
        }

        return new(providers.Text, providers.Vision, ScreenshotPipeline.Unavailable, false,
            providers.Vision is null
                ? "本地 OCR 不可用，且没有带模型与凭据的视觉服务。"
                : "本地 OCR 不可用，且截图上传未授权或网络被禁用。");
    }

    /// <summary>
    /// Full vision readiness: a model must be named, and a cloud service must
    /// hold a key. Capability state is layered on by the UI (verified or
    /// declared-unknown) because no catalog guarantees input modality.
    /// </summary>
    public static bool IsVisionReady(ProviderProfile profile)
    {
        if (!profile.SupportsVision || string.IsNullOrWhiteSpace(profile.VisionModel))
        {
            return false;
        }
        if (ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl))
        {
            return true;
        }
        try
        {
            // Readiness and execution must resolve credentials identically.
            // Legacy installs may still hold the active profile's key in the
            // pre-profile target; rejecting vision before that fallback is
            // consulted makes a correctly configured dual-purpose service
            // appear unavailable while text translation still works.
            var resolvedTarget = ResolveCredentialTargetFor(profile);
            return CredentialStore.HasApiKey(resolvedTarget);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Resolves the credential target for a specific profile.</summary>
    public static string ResolveCredentialTargetFor(ProviderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.CredentialTarget))
        {
            return $"PopGlot/provider/{profile.Id}";
        }
        try
        {
            if (CredentialStore.HasApiKey(profile.CredentialTarget))
            {
                return profile.CredentialTarget;
            }
            // A legacy key saved before profiles existed still counts for the
            // currently active profile only.
            var active = Load().TryGetActiveProfile();
            if (active is not null && active.Id == profile.Id &&
                CredentialStore.HasApiKey(CredentialStore.DefaultTargetName))
            {
                return CredentialStore.DefaultTargetName;
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Fall through to the profile's own target.
        }
        return profile.CredentialTarget;
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
            var profile = Load().TryGetActiveProfile();
            if (profile is not null)
            {
                return ResolveCredentialTargetFor(profile);
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
