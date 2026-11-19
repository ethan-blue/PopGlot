using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PopGlot.Windows.Services;

/// <summary>Tri-state capability. Never guessed from a model name.</summary>
internal enum CapabilityState
{
    Supported,
    Unsupported,
    Unknown,
}

/// <summary>
/// One model as the provider's catalog declares it. A catalog that carries no
/// input-modality information yields <see cref="CapabilityState.Unknown"/> —
/// the UI must then ask the user to confirm or verify before treating the
/// model as vision-capable.
/// </summary>
internal sealed record ModelDescriptor(
    string Id,
    CapabilityState TextGeneration,
    CapabilityState VisionInput,
    string CapabilitySource);

internal sealed record ModelCatalogResult(
    IReadOnlyList<ModelDescriptor> Models,
    Uri Endpoint,
    long ElapsedMs,
    string ProviderKind);

internal sealed record FreeModelProviderPreset(
    string Id,
    string DisplayName,
    string Description,
    string BaseUrl,
    ProviderType ProviderType,
    string TextEndpoint,
    string DefaultModel,
    bool RequiresApiKey,
    string FreeTierHint,
    string? EnvVarName,
    bool IsLocalRuntime,
    bool IsDomestic);

internal sealed record DiscoveredModelSource(
    string Id,
    string DisplayName,
    string BaseUrl,
    ProviderType ProviderType,
    string TextEndpoint,
    bool IsLocalRuntime,
    bool IsReachable,
    bool HasEnvKey,
    string? EnvVarName,
    IReadOnlyList<string> DiscoveredModels,
    long ElapsedMs,
    string StatusSummary);

internal sealed record LocalAndFreeDiscoveryResult(
    IReadOnlyList<DiscoveredModelSource> LocalRuntimes,
    IReadOnlyList<DiscoveredModelSource> EnvConfiguredSources,
    IReadOnlyList<FreeModelProviderPreset> CuratedPresets);

/// <summary>Loads the models exposed by the provider draft without saving it.</summary>
internal static class ModelCatalogService
{
    private const long MaxResponseBytes = 1_048_576;
    private static readonly TimeSpan LocalProbeTimeout = TimeSpan.FromMilliseconds(600);

    public static IReadOnlyList<FreeModelProviderPreset> CuratedFreePresets { get; } =
    [
        new(
            Id: "siliconflow",
            DisplayName: "SiliconFlow（硅基流动）",
            Description: "国内直连开源模型平台，注册含免费额度",
            BaseUrl: "https://api.siliconflow.cn/v1",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "Qwen/Qwen2.5-7B-Instruct",
            RequiresApiKey: true,
            FreeTierHint: "部分小参数开源模型永久免费，国内直连低延迟",
            EnvVarName: "SILICONFLOW_API_KEY",
            IsLocalRuntime: false,
            IsDomestic: true),
        new(
            Id: "groq",
            DisplayName: "Groq",
            Description: "低延迟推理服务，提供每日免费额度",
            BaseUrl: "https://api.groq.com/openai/v1",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "llama-3.1-8b-instant",
            RequiresApiKey: true,
            FreeTierHint: "免费开发者额度，适合实时输入翻译",
            EnvVarName: "GROQ_API_KEY",
            IsLocalRuntime: false,
            IsDomestic: false),
        new(
            Id: "openrouter",
            DisplayName: "OpenRouter",
            Description: "模型聚合平台，支持带 :free 后缀的免费模型",
            BaseUrl: "https://openrouter.ai/api/v1",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "meta-llama/llama-3.2-3b-instruct:free",
            RequiresApiKey: true,
            FreeTierHint: "内置多款 :free 免费模型",
            EnvVarName: "OPENROUTER_API_KEY",
            IsLocalRuntime: false,
            IsDomestic: false),
        new(
            Id: "zhipu",
            DisplayName: "智谱 GLM",
            Description: "国内直连，glm-4-flash 长期免费调用",
            BaseUrl: "https://open.bigmodel.cn/api/paas/v4",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "glm-4-flash",
            RequiresApiKey: true,
            FreeTierHint: "glm-4-flash 免费，无需代理直连",
            EnvVarName: "ZHIPU_API_KEY",
            IsLocalRuntime: false,
            IsDomestic: true),
        new(
            Id: "ollama",
            DisplayName: "Ollama（本地）",
            Description: "本机运行开源模型，离线免费无密钥",
            BaseUrl: "http://localhost:11434/v1",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "",
            RequiresApiKey: false,
            FreeTierHint: "本机离线运行，零成本",
            EnvVarName: null,
            IsLocalRuntime: true,
            IsDomestic: true),
        new(
            Id: "lm-studio",
            DisplayName: "LM Studio（本地）",
            Description: "本机桌面端模型服务，离线免费无密钥",
            BaseUrl: "http://localhost:1234/v1",
            ProviderType: ProviderType.OpenAiCompatible,
            TextEndpoint: "/chat/completions",
            DefaultModel: "",
            RequiresApiKey: false,
            FreeTierHint: "本机离线运行，零成本",
            EnvVarName: null,
            IsLocalRuntime: true,
            IsDomestic: true),
    ];

    internal static async Task<LocalAndFreeDiscoveryResult> DiscoverLocalAndFreeSourcesAsync(
        CancellationToken cancellationToken = default,
        HttpMessageHandler? testHandler = null,
        Func<string, string?>? envReader = null,
        string? ompDbPath = null)
    {
        var localTargets = new (string Id, string Name, string BaseUrl)[]
        {
            ("ollama", "Ollama（本地）", "http://localhost:11434/v1"),
            ("lm-studio", "LM Studio（本地）", "http://localhost:1234/v1"),
            ("litellm", "LiteLLM Proxy（本地）", "http://localhost:4000"),
        };

        var probeTasks = localTargets.Select(target =>
            ProbeLocalRuntimeAsync(target.Id, target.Name, target.BaseUrl, cancellationToken, testHandler));
        var localResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);

        var readEnv = envReader ?? Environment.GetEnvironmentVariable;
        var ompAvailable = ReadAvailableOmpApiKeyProviders(ompDbPath);
        var envDefinitions = new (string Id, string Name, string BaseUrl, ProviderType Type, string Endpoint, string EnvVar, string? OmpProvider)[]
        {
            ("siliconflow", "SiliconFlow（硅基流动）", "https://api.siliconflow.cn/v1", ProviderType.OpenAiCompatible, "/chat/completions", "SILICONFLOW_API_KEY", null),
            ("groq", "Groq", "https://api.groq.com/openai/v1", ProviderType.OpenAiCompatible, "/chat/completions", "GROQ_API_KEY", null),
            ("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ProviderType.OpenAiCompatible, "/chat/completions", "OPENROUTER_API_KEY", null),
            ("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", ProviderType.OpenAiCompatible, "/chat/completions", "ZHIPU_API_KEY", "zhipu-coding-plan"),
            ("deepseek", "DeepSeek", "https://api.deepseek.com/v1", ProviderType.OpenAiCompatible, "/chat/completions", "DEEPSEEK_API_KEY", "deepseek"),
            ("openai", "OpenAI", "https://api.openai.com/v1", ProviderType.OpenAiCompatible, "/chat/completions", "OPENAI_API_KEY", "openai"),
            ("gemini", "Google Gemini", "https://generativelanguage.googleapis.com", ProviderType.GeminiGenerateContent, "/v1beta/models", "GEMINI_API_KEY", null),
        };

        var envSources = new List<DiscoveredModelSource>();
        foreach (var def in envDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasEnv = !string.IsNullOrWhiteSpace(readEnv(def.EnvVar));
            var hasOmp = def.OmpProvider is not null && ompAvailable.Contains(def.OmpProvider);
            if (!hasEnv && !hasOmp)
            {
                continue;
            }
            var hint = hasEnv && hasOmp
                ? $"检测到环境变量 {def.EnvVar} 与本机 CLI 凭据"
                : hasEnv
                    ? $"检测到环境变量 {def.EnvVar}"
                    : "检测到本机 OMP 已配置密钥";
            envSources.Add(new DiscoveredModelSource(
                Id: def.Id,
                DisplayName: def.Name,
                BaseUrl: def.BaseUrl,
                ProviderType: def.Type,
                TextEndpoint: def.Endpoint,
                IsLocalRuntime: false,
                IsReachable: true,
                HasEnvKey: true,
                EnvVarName: hasEnv ? def.EnvVar : null,
                DiscoveredModels: [],
                ElapsedMs: 0,
                StatusSummary: hint));
        }

        return new LocalAndFreeDiscoveryResult(localResults, envSources, CuratedFreePresets);
    }

    internal static string? TryReadEnvApiKeyForImport(string sourceId, Func<string, string?>? envReader = null)
    {
        var envVar = sourceId switch
        {
            "siliconflow" => "SILICONFLOW_API_KEY",
            "groq" => "GROQ_API_KEY",
            "openrouter" => "OPENROUTER_API_KEY",
            "zhipu" => "ZHIPU_API_KEY",
            "deepseek" => "DEEPSEEK_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "gemini" => "GEMINI_API_KEY",
            _ => null,
        };
        if (envVar is null)
        {
            return null;
        }
        var value = (envReader ?? Environment.GetEnvironmentVariable)(envVar);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2400 || value.Any(char.IsControl))
        {
            return null;
        }
        return value.Trim();
    }

    private static async Task<DiscoveredModelSource> ProbeLocalRuntimeAsync(
        string id,
        string displayName,
        string baseUrl,
        CancellationToken cancellationToken,
        HttpMessageHandler? testHandler)
    {
        var uri = CatalogAdapter.BuildUri(baseUrl, "/models");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(LocalProbeTimeout);

            using var ownedClient = testHandler is null
                ? new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false,
                    ConnectTimeout = LocalProbeTimeout,
                    AllowAutoRedirect = false,
                })
                { Timeout = LocalProbeTimeout }
                : new HttpClient(testHandler, disposeHandler: false) { Timeout = LocalProbeTimeout };

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await ownedClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                stopwatch.Stop();
                return new DiscoveredModelSource(
                    id, displayName, baseUrl, ProviderType.OpenAiCompatible, "/chat/completions",
                    IsLocalRuntime: true, IsReachable: false, HasEnvKey: false, EnvVarName: null,
                    DiscoveredModels: [], ElapsedMs: stopwatch.ElapsedMilliseconds,
                    StatusSummary: $"本机端口返回 HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var json = await ReadCappedAsync(stream, MaxResponseBytes, linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var models = new OpenAiCompatibleCatalogAdapter()
                .Parse(json)
                .Select(m => m.Id)
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToList();

            var summary = models.Count > 0
                ? $"已运行 · 发现 {models.Count} 个模型（{stopwatch.ElapsedMilliseconds} ms）"
                : $"已运行 · 暂未拉取到模型（{stopwatch.ElapsedMilliseconds} ms）";

            return new DiscoveredModelSource(
                id, displayName, baseUrl, ProviderType.OpenAiCompatible, "/chat/completions",
                IsLocalRuntime: true, IsReachable: true, HasEnvKey: false, EnvVarName: null,
                DiscoveredModels: models, ElapsedMs: stopwatch.ElapsedMilliseconds,
                StatusSummary: summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return new DiscoveredModelSource(
                id, displayName, baseUrl, ProviderType.OpenAiCompatible, "/chat/completions",
                IsLocalRuntime: true, IsReachable: false, HasEnvKey: false, EnvVarName: null,
                DiscoveredModels: [], ElapsedMs: stopwatch.ElapsedMilliseconds,
                StatusSummary: "未检测到运行中的服务");
        }
    }

    private static HashSet<string> ReadAvailableOmpApiKeyProviders(string? ompDbPath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var agent = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
            var database = ompDbPath ?? Path.Combine(
                string.IsNullOrWhiteSpace(agent)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent")
                    : agent,
                "agent.db");
            if (!File.Exists(database))
            {
                return result;
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(database),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT provider FROM auth_credentials WHERE credential_type = 'api_key' AND disabled_cause IS NULL LIMIT 16";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    var name = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(name);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never surface OMP DB errors during discovery.
        }
        return result;
    }

    internal static async Task<ModelCatalogResult> FetchAsync(
        ProviderSettings settings,
        string apiKey,
        CancellationToken cancellationToken = default,
        HttpMessageHandler? testHandler = null)
    {
        if (settings.SafeDevMode)
        {
            throw new InvalidOperationException("安全离线模式已开启；未发送模型列表请求。");
        }
        if (!settings.NetworkEnabled)
        {
            throw new InvalidOperationException("网络翻译未启用；未发送模型列表请求。");
        }

        var adapter = CatalogAdapter.For(settings);
        var (uri, request) = adapter.BuildRequest(settings, apiKey);
        var stopwatch = Stopwatch.StartNew();
        using var ownedClient = testHandler is not null
            ? new HttpClient(testHandler) { Timeout = TimeSpan.FromSeconds(15) }
            : null;
        var httpClient = ownedClient ?? ProxyHttpClientFactory.GetClient(
            ProxyConfig.FromCurrentSettings(),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15));
        // ResponseHeadersRead: the body (possibly gzip/brotli decompressed by
        // the handler) must never be buffered wholesale by HttpClient — the
        // 1 MiB cap is enforced below on the decoded stream itself, with or
        // without a Content-Length header.
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("密钥无效或没有权限（HTTP 401），无法读取模型列表。");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("服务拒绝了模型列表请求（HTTP 403）。");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("模型列表请求被限流（HTTP 429），请稍后再试。");
        }
        response.EnsureSuccessStatusCode();

        // Header-level fast rejection; a lying or absent Content-Length is
        // still bounded by the streamed read below.
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException("模型列表响应超过 1 MiB 上限，已拒绝处理。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var json = await ReadCappedAsync(stream, MaxResponseBytes, cancellationToken);
        stopwatch.Stop();

        var models = adapter.Parse(json);
        return new ModelCatalogResult(
            models,
            uri,
            stopwatch.ElapsedMilliseconds,
            adapter.Kind);
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> decompressed bytes from the
    /// stream; a single byte more is rejected, so a server cannot expand a
    /// small (or chunked / length-less) response past the 1 MiB budget.
    /// </summary>
    private static async Task<string> ReadCappedAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        using var payload = new MemoryStream();
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("模型列表响应超过 1 MiB 上限，已拒绝处理。");
            }
            payload.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
    }

    internal static Uri BuildModelsUri(string baseUrl, ProviderType providerType)
    {
        var settings = CoreBridge.GetSettings() with
        {
            ProviderType = providerType,
            ApiBaseUrl = baseUrl,
        };
        return CatalogAdapter.For(settings).BuildRequest(settings, string.Empty).Endpoint;
    }

    internal static IReadOnlyList<string> ParseModels(string json, ProviderType providerType)
    {
        var settings = CoreBridge.GetSettings() with { ProviderType = providerType };
        return CatalogAdapter.For(settings).Parse(json)
            .Select(model => model.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Protocol-specific catalog access. Each adapter owns the list URL, the
/// auth headers and the response shape; none of them invent capabilities.
/// </summary>
internal interface ICatalogAdapter
{
    string Kind { get; }

    (Uri Endpoint, HttpRequestMessage Request) BuildRequest(
        ProviderSettings settings, string apiKey);

    IReadOnlyList<ModelDescriptor> Parse(string json);
}

internal static class CatalogAdapter
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "cookie",
        "set-cookie",
        "x-api-key",
        "api-key",
        "x-goog-api-key",
    };

    internal static bool IsSensitiveHeader(string headerName) =>
        !string.IsNullOrWhiteSpace(headerName) && SensitiveHeaders.Contains(headerName.Trim());

    internal static void ApplySafeExtraHeaders(
        HttpRequestMessage request,
        IEnumerable<KeyValuePair<string, string>> extraHeaders)
    {
        foreach (var header in extraHeaders)
        {
            if (string.IsNullOrWhiteSpace(header.Key) || IsSensitiveHeader(header.Key))
            {
                continue;
            }
            request.Headers.TryAddWithoutValidation(header.Key.Trim(), header.Value);
        }
    }

    internal static ICatalogAdapter For(ProviderSettings settings) => settings.ProviderType switch
    {
        ProviderType.GeminiGenerateContent => new GeminiCatalogAdapter(),
        ProviderType.AnthropicMessages => new AnthropicCatalogAdapter(),
        _ => new OpenAiCompatibleCatalogAdapter(),
    };

    internal static Uri BuildUri(string baseUrl, string path, string? query = null)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        var full = trimmed + (path.StartsWith('/') ? path : "/" + path) + (query ?? string.Empty);
        var uri = new Uri(full, UriKind.Absolute);
        if (uri.Scheme == Uri.UriSchemeHttp && !ProviderSettings.IsLocalBaseUrl(uri.GetLeftPart(UriPartial.Authority)))
        {
            throw new InvalidOperationException("公网模型目录必须使用 HTTPS。");
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("模型目录只支持 HTTP(S) 地址。");
        }
        return uri;
    }

    internal static string AppendPathOnce(string baseUrl, string versionPrefix, string suffix)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed.EndsWith(versionPrefix, StringComparison.OrdinalIgnoreCase)
            ? suffix
            : versionPrefix + suffix;
    }
}

/// <summary>
/// OpenAI-compatible catalog: GET {base}/models with a bearer token. The
/// response carries ids only — no input modality — so every model reports
/// Unknown vision capability.
/// </summary>
internal sealed class OpenAiCompatibleCatalogAdapter : ICatalogAdapter
{
    public string Kind => "OpenAI 兼容";

    public (Uri Endpoint, HttpRequestMessage Request) BuildRequest(
        ProviderSettings settings, string apiKey)
    {
        var uri = CatalogAdapter.BuildUri(settings.ApiBaseUrl, "/models");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        CatalogAdapter.ApplySafeExtraHeaders(request, settings.ExtraHeaders);
        return (uri, request);
    }

    public IReadOnlyList<ModelDescriptor> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.GetProperty("data");
        var models = new List<ModelDescriptor>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                models.Add(new ModelDescriptor(
                    id, CapabilityState.Unknown, CapabilityState.Unknown, $"{Kind} /models（无模态信息）"));
            }
        }
        return models;
    }
}

/// <summary>
/// Gemini catalog: GET {base}/v1beta/models with the key as a query
/// parameter. supportedGenerationMethods says which actions exist, not which
/// input modalities a model accepts, so vision stays Unknown.
/// </summary>
internal sealed class GeminiCatalogAdapter : ICatalogAdapter
{
    public string Kind => "Gemini";

    public (Uri Endpoint, HttpRequestMessage Request) BuildRequest(
        ProviderSettings settings, string apiKey)
    {
        var path = CatalogAdapter.AppendPathOnce(settings.ApiBaseUrl, "/v1beta", "/models");
        var uri = CatalogAdapter.BuildUri(settings.ApiBaseUrl, path, "?pageSize=1000");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        }
        CatalogAdapter.ApplySafeExtraHeaders(request, settings.ExtraHeaders);
        return (uri, request);
    }

    public IReadOnlyList<ModelDescriptor> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<ModelDescriptor>();
        foreach (var item in document.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }
            var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
            var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("supportedGenerationMethods", out var methods))
            {
                foreach (var method in methods.EnumerateArray())
                {
                    if (method.GetString() is { } value)
                    {
                        actions.Add(value);
                    }
                }
            }
            // generateContent presence proves the model exists for generation;
            // it says nothing about image inputs, so vision stays Unknown.
            var text = actions.Count == 0 || actions.Contains("generateContent")
                ? CapabilityState.Unknown
                : CapabilityState.Unsupported;
            if (text != CapabilityState.Unsupported)
            {
                models.Add(new ModelDescriptor(id, text, CapabilityState.Unknown, $"{Kind} supportedGenerationMethods"));
            }
        }
        return models;
    }
}

/// <summary>
/// Anthropic catalog: GET {base}/v1/models with x-api-key and the
/// anthropic-version header. The response carries display metadata only.
/// </summary>
internal sealed class AnthropicCatalogAdapter : ICatalogAdapter
{
    public string Kind => "Anthropic";

    public (Uri Endpoint, HttpRequestMessage Request) BuildRequest(
        ProviderSettings settings, string apiKey)
    {
        var path = CatalogAdapter.AppendPathOnce(settings.ApiBaseUrl, "/v1", "/models");
        var uri = CatalogAdapter.BuildUri(settings.ApiBaseUrl, path, "?limit=1000");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        }
        request.Headers.TryAddWithoutValidation(
            "anthropic-version",
            string.IsNullOrWhiteSpace(settings.AnthropicVersion) ? "2023-06-01" : settings.AnthropicVersion);
        CatalogAdapter.ApplySafeExtraHeaders(request, settings.ExtraHeaders);
        return (uri, request);
    }

    public IReadOnlyList<ModelDescriptor> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<ModelDescriptor>();
        foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                models.Add(new ModelDescriptor(
                    id, CapabilityState.Unknown, CapabilityState.Unknown, $"{Kind} /v1/models（无模态信息）"));
            }
        }
        return models;
    }
}
