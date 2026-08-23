using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace PopGlot.Windows;

internal static class FreeTranslateService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    // Google rate-limits these undocumented endpoints per IP; after a 429 we
    // back off for a minute instead of hammering them on every retry.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(1);
    private const int CacheCapacity = 128;

    private static readonly ConcurrentDictionary<string, TranslationResponse> Cache = new();
    private static long _rateLimitedUntilTicks;

    private sealed record FreeEndpoint(
        string Host,
        Func<string, string, string, string> BuildUrl,
        Func<string, (string Translated, string Phonetic)> Parse);

    private static readonly FreeEndpoint[] Endpoints =
    [
        new FreeEndpoint(
            "translate.googleapis.com",
            static (sl, tl, q) =>
                $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&dt=bd&dt=rm&q={HttpUtility.UrlEncode(q)}",
            ParseGtxSingle),
        new FreeEndpoint(
            "clients5.google.com",
            static (sl, tl, q) =>
                $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={sl}&tl={tl}&q={HttpUtility.UrlEncode(q)}",
            ParseDictChromeEx),
    ];

    public static async Task<TranslationResponse> TranslateAsync(
        string text,
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Translation source text cannot be empty.", nameof(text));
        }

        var sl = NormalizeLanguageCode(sourceLang);
        var tl = NormalizeLanguageCode(targetLang);

        var cacheKey = $"{sl}|{tl}|{trimmed}";
        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _rateLimitedUntilTicks))
        {
            throw RateLimitedError(inCooldown: true);
        }

        var started = Stopwatch.GetTimestamp();
        Exception? lastError = null;

        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, endpoint.BuildUrl(sl, tl, trimmed));
                request.Headers.Add(
                    "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                using var response = await HttpClient.SendAsync(request, cancellationToken);

                if ((int)response.StatusCode == 429)
                {
                    Interlocked.Exchange(
                        ref _rateLimitedUntilTicks,
                        DateTime.UtcNow.Add(RateLimitCooldown).Ticks);
                    lastError = RateLimitedError(inCooldown: false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    lastError = new InvalidOperationException(
                        $"免费翻译服务不可用（HTTP {(int)response.StatusCode}）；可在设置中配置模型服务以获得稳定翻译。");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var (translated, phonetic) = endpoint.Parse(json);
                if (string.IsNullOrWhiteSpace(translated))
                {
                    lastError = new InvalidOperationException(
                        "免费翻译服务返回了无法解析的响应，请重试。");
                    continue;
                }

                var elapsedMs = (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var result = new TranslationResponse(
                    new TranslationResult(
                        translated,
                        phonetic,
                        "由内置免费网页引擎提供",
                        [],
                        []),
                    new ProviderDiagnostics(
                        "free-web",
                        ProviderType.OpenAiCompatible,
                        endpoint.Host,
                        1,
                        (ushort)response.StatusCode,
                        elapsedMs));
                AddToCache(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = new InvalidOperationException(
                    $"免费翻译服务暂时无法访问（{exception.Message}）；请检查网络后重试。");
            }
        }

        throw lastError ?? new InvalidOperationException("免费翻译服务不可用。");
    }

    private static (string Translated, string Phonetic) ParseGtxSingle(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return (string.Empty, string.Empty);
        }

        var sentences = root[0];
        if (sentences.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        var sb = new System.Text.StringBuilder();
        var phonetic = string.Empty;
        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.ValueKind != JsonValueKind.Array || sentence.GetArrayLength() == 0)
            {
                continue;
            }
            var segment = sentence[0].GetString();
            if (!string.IsNullOrEmpty(segment))
            {
                sb.Append(segment);
            }
            if (sentence.GetArrayLength() > 3 && sentence[3].ValueKind == JsonValueKind.String)
            {
                phonetic = sentence[3].GetString() ?? string.Empty;
            }
        }
        return (sb.ToString(), phonetic);
    }

    private static (string Translated, string Phonetic) ParseDictChromeEx(string json)
    {
        // Shape: [["译文","检测语言"], ...] with one pair per text segment.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        var sb = new System.Text.StringBuilder();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Array &&
                element.GetArrayLength() > 0 &&
                element[0].ValueKind == JsonValueKind.String)
            {
                sb.Append(element[0].GetString());
            }
        }
        return (sb.ToString(), string.Empty);
    }

    private static void AddToCache(string key, TranslationResponse value)
    {
        if (Cache.Count >= CacheCapacity)
        {
            Cache.Clear();
        }
        Cache[key] = value;
    }

    private static InvalidOperationException RateLimitedError(bool inCooldown) => new(
        inCooldown
            ? "免费翻译接口刚刚被限流（HTTP 429），一分钟内暂不自动重试；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。"
            : "免费翻译接口被限流（HTTP 429，本机 IP 已被暂时限制）；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。");

    private static string NormalizeLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "auto" or "自动" or "自动检测" => "auto",
        "zh" or "zh-cn" or "zh-hans" or "中文" or "汉语" => "zh-CN",
        "zh-tw" or "zh-hant" or "繁体中文" => "zh-TW",
        "en" or "en-us" or "en-gb" or "英语" or "英文" => "en",
        "ja" or "日语" or "日文" => "ja",
        "ko" or "韩语" or "韩文" => "ko",
        "fr" or "法语" or "法文" => "fr",
        "de" or "德语" or "德文" => "de",
        "es" or "西班牙语" => "es",
        "ru" or "俄语" => "ru",
        _ => code
    };
}
