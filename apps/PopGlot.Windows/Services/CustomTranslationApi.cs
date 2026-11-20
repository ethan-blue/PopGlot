using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PopGlot.Windows.Services;

/// <summary>A user-selected JSON translation endpoint, not an AI/chat protocol.</summary>
internal sealed record CustomTranslationApiOptions(
    string Name = "自定义翻译 API",
    string Endpoint = "",
    string RequestTemplate = "{\"q\":\"{text}\",\"source\":\"{sourceBase}\",\"target\":\"{targetBase}\",\"format\":\"text\"}",
    string ResponsePointer = "/translatedText",
    string ApiKeyHeader = "Authorization",
    string ApiKeyPrefix = "Bearer ")
{
    internal string CredentialTarget => "PopGlot/custom-translation/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Endpoint.Trim())))[..24];
}

internal static class CustomTranslationApi
{
    private static string ConfigPath => Path.Combine(StoragePaths.CoreConfigDirectory, "custom-translation-api.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static Func<CustomTranslationApiOptions>? OptionsLoaderForTest { get; set; }
    internal static CustomTranslationApiOptions Load() => OptionsLoaderForTest?.Invoke() ??
        (File.Exists(ConfigPath)
            ? JsonSerializer.Deserialize<CustomTranslationApiOptions>(File.ReadAllText(ConfigPath))
                ?? new CustomTranslationApiOptions()
            : new CustomTranslationApiOptions());

    internal static void Save(CustomTranslationApiOptions options)
    {
        Validate(options);
        Directory.CreateDirectory(StoragePaths.CoreConfigDirectory);
        var temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, options, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, ConfigPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static void Validate(CustomTranslationApiOptions options)
    {
        if (options.Name is null || options.Endpoint is null || options.RequestTemplate is null ||
            options.ResponsePointer is null || options.ApiKeyHeader is null || options.ApiKeyPrefix is null)
            throw new ArgumentException("自定义接口配置字段不能为 null。");
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请填写 HTTPS 完整请求地址；仅本机回环地址允许 HTTP，不允许 URL 用户名密码或片段。");
        if (options.Endpoint.Length > 2048 || options.RequestTemplate.Length > 32768 || options.ResponsePointer.Length > 1024)
            throw new ArgumentException("自定义接口配置超出长度限制。");
        if (!string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("请将请求参数放在 JSON 请求体中，不要在 URL 查询参数中存放密钥或正文。");
        if (JsonNode.Parse(options.RequestTemplate) is not JsonObject)
            throw new ArgumentException("请求模板必须是 JSON 对象。");
        if (!options.RequestTemplate.Contains("{text}", StringComparison.Ordinal))
            throw new ArgumentException("请求模板必须包含 {text}，否则不会发送待译文本。");
        if (options.ResponsePointer.Length > 0 && !options.ResponsePointer.StartsWith('/'))
            throw new ArgumentException("响应路径使用 JSON Pointer，例如 /translatedText 或 /data/0/text。");
        var header = options.ApiKeyHeader;
        if (header.Length > 128 || header.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')) ||
            header.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            header.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
            header.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
            header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
            options.ApiKeyPrefix.Contains('\r') || options.ApiKeyPrefix.Contains('\n'))
            throw new ArgumentException("密钥请求头或前缀无效。使用 Authorization / Bearer 或 X-Api-Key 等字段。");
    }

    internal static string BuildBody(CustomTranslationApiOptions options, string text, string source, string target, string key)
    {
        var body = JsonNode.Parse(options.RequestTemplate)!;
        // Single-pass expansion: user text containing {apiKey} must remain literal,
        // never be expanded a second time into a credential.
        string Replace(string value) => System.Text.RegularExpressions.Regex.Replace(value,
            @"\{(text|source|target|sourceBase|targetBase|sourceUpper|targetUpper|apiKey)\}", match => match.Groups[1].Value switch
            {
                "text" => text, "source" => source, "target" => target, "apiKey" => key,
                "sourceBase" => source.Split('-')[0], "targetBase" => target.Split('-')[0],
                "sourceUpper" => source.Split('-')[0].ToUpperInvariant(),
                "targetUpper" => target.Split('-')[0].ToUpperInvariant(), _ => match.Value,
            });
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Value is JsonValue v && v.TryGetValue<string>(out var s)) obj[pair.Key] = Replace(s);
                    else Visit(pair.Value);
                }
            }
            else if (node is JsonArray arr)
            {
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = Replace(s);
                    else Visit(arr[i]);
                }
            }
        }
        Visit(body);
        return body.ToJsonString();
    }

    internal static string ParseResponse(string json, string pointer)
    {
        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement;
        foreach (var part in pointer.Split('/').Skip(1))
        {
            var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property)) value = property;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(key, out var i) && i >= 0 && i < value.GetArrayLength()) value = value[i];
            else throw new InvalidOperationException("响应中没有配置的译文字段，请检查 JSON Pointer。");
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("译文字段为空或不是字符串；未把 HTTP 成功当成翻译成功。");
        return value.GetString()!;
    }

    internal static async Task<TranslationResponse> TranslateAsync(string text, string source, string target,
        FreeEngineAuthorization authorization, CancellationToken cancellationToken,
        CustomTranslationApiOptions? optionsOverride = null, string? keyOverride = null)
    {
        var options = optionsOverride ?? Load();
        Validate(options);
        var key = keyOverride ?? CredentialStore.LoadApiKey(options.CredentialTarget) ?? string.Empty;
        SecretRedactor.Register(key);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new StringContent(BuildBody(options, text, source, target, key), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(options.ApiKeyHeader))
            request.Headers.Add(options.ApiKeyHeader, options.ApiKeyPrefix + key);
        if (!authorization.TryClaimSend(out var refusal)) throw new InvalidOperationException(refusal);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var client = FreeTranslateService.GetActiveHttpClient();
        using var response = FreeTranslateService.HttpSenderOverride is { } sender
            ? await sender(request, cancellationToken).ConfigureAwait(false)
            : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"自定义翻译 API 返回 HTTP {(int)response.StatusCode}；请检查地址、密钥和服务状态。");
        // Keep responses bounded even without Content-Length, and include body reads in the request cancellation budget.
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > FreeTranslateService.MaxResponseBytes)
                throw new InvalidOperationException("自定义翻译响应超过 4 MiB，已停止读取。");
            output.Write(buffer, 0, count);
        }
        var translated = ParseResponse(Encoding.UTF8.GetString(output.ToArray()), options.ResponsePointer);
        return new TranslationResponse(new TranslationResult(translated, "", "", [], []),
            new ProviderDiagnostics("custom-translation", ProviderType.OpenAiCompatible,
                new Uri(options.Endpoint).Host, 1, (ushort)response.StatusCode,
                (ulong)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            OverrideEngineLabel: string.IsNullOrWhiteSpace(options.Name) ? "自定义翻译 API" : options.Name);
    }
}
