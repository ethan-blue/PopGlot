using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

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

/// <summary>Loads the models exposed by the provider draft without saving it.</summary>
internal static class ModelCatalogService
{
    private const long MaxResponseBytes = 1_048_576;

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
        using var httpClient = testHandler is null ? new HttpClient() : new HttpClient(testHandler);
        httpClient.Timeout = TimeSpan.FromSeconds(15);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        stopwatch.Stop();

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

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException("模型列表响应超过 1 MiB 上限，已拒绝处理。");
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (json.Length > MaxResponseBytes)
        {
            throw new InvalidOperationException("模型列表响应超过 1 MiB 上限，已拒绝处理。");
        }

        var models = adapter.Parse(json);
        return new ModelCatalogResult(
            models,
            uri,
            stopwatch.ElapsedMilliseconds,
            adapter.Kind);
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
        foreach (var header in settings.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
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
        foreach (var header in settings.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
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
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation(
            "anthropic-version",
            string.IsNullOrWhiteSpace(settings.AnthropicVersion) ? "2023-06-01" : settings.AnthropicVersion);
        foreach (var header in settings.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
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
