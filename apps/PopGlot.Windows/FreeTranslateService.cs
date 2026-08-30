using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace PopGlot.Windows;

/// <summary>
/// Zero-configuration fallback so the app translates something useful before
/// the user has any API key.
/// </summary>
internal static class FreeTranslateService
{
    /// <summary>Request id stamped on results from this engine.</summary>
    public const string RequestId = "free-web";

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    // These undocumented endpoints rate-limit per IP; after a 429 we back off
    // instead of hammering them on every retry.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(1);
    private const int CacheCapacity = 256;
    private const int MaxSourceCharacters = 5_000;

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
        if (trimmed.Length > MaxSourceCharacters)
        {
            throw new InvalidOperationException(
                $"内置免费引擎单次最多翻译 {MaxSourceCharacters} 个字符。请缩短选区，或在设置中配置自己的模型服务。");
        }

        var sl = LanguageCatalog.Normalize(sourceLang);
        var tl = LanguageCatalog.Normalize(targetLang);
        if (tl == LanguageCatalog.Auto)
        {
            tl = "zh-CN";
        }

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
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get, endpoint.BuildUrl(sl, tl, trimmed));
                request.Headers.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
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
                    lastError = new InvalidOperationException("免费翻译服务返回了无法解析的响应，请重试。");
                    continue;
                }

                var elapsedMs = (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var result = new TranslationResponse(
                    new TranslationResult(
                        translated,
                        Transcription: string.Empty,
                        Explanation: string.Empty,
                        ProtectedTerms: [],
                        Warnings: [],
                        Phonetic: phonetic),
                    new ProviderDiagnostics(
                        RequestId,
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

        var builder = new StringBuilder();
        var phonetic = string.Empty;
        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.ValueKind != JsonValueKind.Array || sentence.GetArrayLength() == 0)
            {
                continue;
            }
            if (sentence[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(sentence[0].GetString());
            }
            // Index 3 carries the romanization of the *source* text.
            if (sentence.GetArrayLength() > 3 && sentence[3].ValueKind == JsonValueKind.String)
            {
                phonetic = sentence[3].GetString() ?? string.Empty;
            }
        }
        return (builder.ToString(), phonetic);
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

        var builder = new StringBuilder();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Array &&
                element.GetArrayLength() > 0 &&
                element[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(element[0].GetString());
            }
        }
        return (builder.ToString(), string.Empty);
    }

    private static void AddToCache(string key, TranslationResponse value)
    {
        if (Cache.Count >= CacheCapacity)
        {
            Cache.Clear();
        }
        Cache[key] = value;
    }

    // ================= Health probe =================

    public readonly record struct FreeEngineHealth(bool Ok, int LatencyMs, string? Error);

    private static readonly TimeSpan HealthTtl = TimeSpan.FromMinutes(10);
    private static long _healthCompletedTicks;
    private static Task<FreeEngineHealth>? _healthProbe;
    private static FreeEngineHealth _lastHealth;
    private static int _probeSequence;

    /// <summary>
    /// Whether the free engine currently reaches a working endpoint. Cached
    /// for HealthTtl; pass force=true to re-check immediately (footer click).
    /// </summary>
    public static Task<FreeEngineHealth> GetHealthAsync(bool force = false)
    {
        if (!force && DateTime.UtcNow.Ticks - Interlocked.Read(ref _healthCompletedTicks) < HealthTtl.Ticks)
        {
            return Task.FromResult(_lastHealth);
        }
        // Single in-flight probe; a forced click supersedes it.
        var probe = Interlocked.Exchange(ref _healthProbe, null);
        if (probe is not null && !force)
        {
            return probe;
        }
        probe = ProbeCoreAsync();
        _healthProbe = probe;
        return probe;
    }

    private static async Task<FreeEngineHealth> ProbeCoreAsync()
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            // A unique text every time so the probe never answers from the
            // translation cache — it must hit the real endpoint.
            var text = $"ping {Interlocked.Increment(ref _probeSequence)}";
            await TranslateAsync(text, "auto", "zh-CN", CancellationToken.None);
            _lastHealth = new FreeEngineHealth(
                true, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception exception)
        {
            _lastHealth = new FreeEngineHealth(false, 0, exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _healthCompletedTicks, DateTime.UtcNow.Ticks);
            _healthProbe = null;
        }
        return _lastHealth;
    }

    private static InvalidOperationException RateLimitedError(bool inCooldown) => new(
        inCooldown
            ? "免费翻译接口刚刚被限流（HTTP 429），一分钟内暂不自动重试；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。"
            : "免费翻译接口被限流（HTTP 429，本机 IP 已被暂时限制）；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。");
}
