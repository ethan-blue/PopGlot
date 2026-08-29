using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PopGlot.Windows.Services;

internal sealed record ModelCatalogResult(
    IReadOnlyList<string> Models,
    Uri Endpoint,
    long ElapsedMs);

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
            throw new InvalidOperationException("网络访问未启用；未发送模型列表请求。");
        }
        if (string.IsNullOrWhiteSpace(apiKey) && !settings.TargetsLocalRuntime)
        {
            throw new InvalidOperationException("请先填写 API Key，再获取该服务可用的模型。");
        }

        var endpoint = BuildModelsUri(settings.ApiBaseUrl, settings.ProviderType);
        using var handler = testHandler is null ? CreateHandler(settings.AllowInsecureTls) : null;
        using var client = new HttpClient(testHandler ?? handler!, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyHeaders(request, settings, apiKey);

        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeFailure(response.StatusCode));
        }

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException("模型列表响应过大，已停止读取。");
        }
        await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var models = ParseModels(json, settings.ProviderType);
        if (models.Count == 0)
        {
            throw new InvalidOperationException("服务返回成功，但没有找到可用于生成内容的模型。");
        }

        return new ModelCatalogResult(
            models,
            endpoint,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal static Uri BuildModelsUri(string baseUrl, ProviderType providerType)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new InvalidOperationException("请求地址无效，请先检查 API Base URL。");
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new InvalidOperationException("API Base URL 不能包含凭据、查询参数或片段。");
        }
        var isLocal = ProviderSettings.IsLocalBaseUrl(baseUrl);
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(isLocal && string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("API Base URL 必须使用 HTTPS；本地或局域网服务允许 HTTP。");
        }

        var basePath = parsed.AbsolutePath.TrimEnd('/');
        var (versionPath, query) = providerType switch
        {
            ProviderType.GeminiGenerateContent => ("/v1beta", "?pageSize=1000"),
            ProviderType.AnthropicMessages => ("/v1", "?limit=1000"),
            _ => (string.Empty, string.Empty),
        };
        var path = providerType switch
        {
            ProviderType.OpenAiCompatible or ProviderType.OpenAiResponses =>
                EndsWithSegment(basePath, "models") ? basePath : basePath + "/models",
            _ when EndsWithSegment(basePath, "models") => basePath,
            _ when basePath.EndsWith(versionPath, StringComparison.OrdinalIgnoreCase) => basePath + "/models",
            _ => basePath + versionPath + "/models",
        };

        var builder = new UriBuilder(parsed)
        {
            Path = path,
            Query = query.TrimStart('?'),
        };
        return builder.Uri;
    }

    internal static IReadOnlyList<string> ParseModels(string json, ProviderType providerType)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement items;
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
        }
        else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            items = data;
        }
        else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            items = models;
        }
        else
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddModel(result, item.GetString());
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object ||
                (providerType == ProviderType.GeminiGenerateContent && !SupportsGenerateContent(item)))
            {
                continue;
            }
            if (item.TryGetProperty("id", out var id))
            {
                AddModel(result, id.GetString());
            }
            else if (item.TryGetProperty("name", out var name))
            {
                AddModel(result, name.GetString());
            }
        }
        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(1_000).ToArray();
    }

    private static SocketsHttpHandler CreateHandler(bool allowInsecureTls)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (allowInsecureTls)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        return handler;
    }

    private static void ApplyHeaders(HttpRequestMessage request, ProviderSettings settings, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            switch (settings.ProviderType)
            {
                case ProviderType.GeminiGenerateContent:
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
                    break;
                case ProviderType.AnthropicMessages:
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
                    request.Headers.TryAddWithoutValidation(
                        "anthropic-version",
                        string.IsNullOrWhiteSpace(settings.AnthropicVersion)
                            ? "2023-06-01"
                            : settings.AnthropicVersion.Trim());
                    break;
                default:
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    break;
            }
        }

        foreach (var (name, value) in settings.ExtraHeaders)
        {
            if (IsReservedHeader(name))
            {
                continue;
            }
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static bool SupportsGenerateContent(JsonElement item)
    {
        foreach (var property in new[] { "supportedGenerationMethods", "supportedActions" })
        {
            if (!item.TryGetProperty(property, out var methods) || methods.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            return methods.EnumerateArray().Any(value =>
                string.Equals(value.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase));
        }
        return true;
    }

    private static void AddModel(HashSet<string> result, string? value)
    {
        var model = value?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }
        if (model.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            model = model["models/".Length..];
        }
        if (model.Length <= 200)
        {
            result.Add(model);
        }
    }

    private static bool EndsWithSegment(string path, string segment) =>
        path.Equals('/' + segment, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith('/' + segment, StringComparison.OrdinalIgnoreCase);

    private static bool IsReservedHeader(string name) => name.Trim().ToLowerInvariant() is
        "authorization" or "x-api-key" or "x-goog-api-key" or "anthropic-version" or
        "content-length" or "host";

    private static string DescribeFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "获取模型失败：API Key 无效或没有读取模型列表的权限。",
        HttpStatusCode.NotFound =>
            "获取模型失败：服务没有提供模型列表接口，请检查 Base URL。",
        (HttpStatusCode)429 => "获取模型过于频繁，请稍后重试。",
        _ => $"获取模型失败（HTTP {(int)statusCode}）。",
    };
}
