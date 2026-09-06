using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// Zero-configuration fallback so the app translates something useful before
/// the user has any API key.
/// </summary>
internal static class FreeTranslateService
{
    /// <summary>Request id stamped on results from this engine.</summary>
    public const string RequestId = "free-web";

    /// <summary>
    /// Test seam: the last send boundary. When installed, every outbound
    /// request of this engine goes through it instead of the shared
    /// <see cref="HttpClient"/>, so the isolated test host can count sends and
    /// refuse non-loopback destinations. Production leaves it null.
    /// </summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HttpSenderOverride { get; set; }

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
        // A redirect would re-send the URL query (which carries the source
        // text) to whichever host the 302 names. Not following redirects is
        // the honest boundary; a 3xx surfaces as an explicit failure.
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    // These undocumented endpoints rate-limit per IP; after a 429 we back off
    // instead of hammering them on every retry.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(1);
    private const int CacheCapacity = 256;
    private const long CacheMaxBytes = 16 * 1024 * 1024;
    private const int MaxSourceCharacters = 5_000;
    /// <summary>Cumulative response-body cap, enforced with or without Content-Length.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, TranslationResponse> Cache = new();
    private static readonly LinkedList<string> CacheOrder = new();
    private static long _cacheBytes;
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
        FreeEngineAuthorization? authorization = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        // The last send boundary: without an authorization issued by
        // OutboundPolicy — including for health probes — nothing leaves the
        // machine, no matter which entry point got here.
        EnsureOutboundAuthorized(authorization);
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
        TranslationResponse? cached;
        lock (CacheGate)
        {
            Cache.TryGetValue(cacheKey, out cached);
        }
        if (cached is not null)
        {
            // A hit must never masquerade as a fresh measurement: the original
            // request timing is replaced with an explicit zero.
            return cached with { Diagnostics = cached.Diagnostics with { ElapsedMs = 0 } };
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
                using var response = HttpSenderOverride is { } sender
                    ? await sender(request, cancellationToken)
                    : await HttpClient.SendAsync(request, cancellationToken);

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

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = new InvalidOperationException(
                        "免费翻译服务返回了网页而不是翻译结果（可能是访问提示页）；请稍后重试，或在设置中配置自己的模型服务。");
                    continue;
                }

                // Cumulative cap enforced while streaming the body: a missing
                // or lying Content-Length cannot grow the buffer unbounded.
                if (response.Content.Headers.ContentLength is { } declaredLength &&
                    declaredLength > MaxResponseBytes)
                {
                    lastError = new InvalidOperationException(
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                    continue;
                }
                var json = await ReadCappedAsync(response.Content, cancellationToken);
                if (json is null)
                {
                    lastError = new InvalidOperationException(
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                    continue;
                }
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

    private static void EnsureOutboundAuthorized(FreeEngineAuthorization? authorization)
    {
        if (authorization is null)
        {
            throw new InvalidOperationException(
                "内置免费引擎未获出网授权；未发送任何请求。可在「设置 → 隐私与数据」中允许，或配置自己的模型服务。");
        }
        var settings = authorization.Settings;
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            throw new InvalidOperationException(
                "已开启安全离线模式或网络翻译已关闭；未发送任何请求。");
        }
    }

    /// <summary>
    /// Reads the response body with a hard cumulative cap that holds even when
    /// Content-Length is absent. Returns null when the cap is exceeded.
    /// </summary>
    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var payload = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                return null;
            }
            payload.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(payload.ToArray());
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
        // Strict bound under one lock: at most CacheCapacity entries and at
        // most CacheMaxBytes of payload, evicting least-recently-used first.
        // The key (source text + languages) counts toward the budget too.
        static long EstimateBytes(string entryKey, TranslationResponse response) =>
            Encoding.UTF8.GetByteCount(entryKey) +
            Encoding.UTF8.GetByteCount(response.Result.TranslatedText) + 256;

        lock (CacheGate)
        {
            if (Cache.ContainsKey(key))
            {
                var existingNode = CacheOrder.Find(key);
                if (existingNode is not null)
                {
                    CacheOrder.Remove(existingNode);
                    CacheOrder.AddFirst(existingNode);
                }
                return;
            }

            var bytes = EstimateBytes(key, value);
            while (Cache.Count >= CacheCapacity ||
                   (_cacheBytes + bytes > CacheMaxBytes && CacheOrder.Count > 0))
            {
                var oldest = CacheOrder.Last!.Value;
                CacheOrder.RemoveLast();
                _cacheBytes -= EstimateBytes(oldest, Cache[oldest]);
                Cache.Remove(oldest);
            }
            if (Cache.Count >= CacheCapacity)
            {
                return;
            }
            Cache[key] = value;
            CacheOrder.AddFirst(key);
            _cacheBytes += bytes;
        }
    }

    // ================= Health probe =================

    public readonly record struct FreeEngineHealth(bool Ok, int LatencyMs, string? Error);

    private static readonly TimeSpan HealthTtl = TimeSpan.FromMinutes(10);
    private static readonly object HealthGate = new();
    private static long _healthCompletedTicks;
    private static Task<FreeEngineHealth>? _healthProbe;
    private static FreeEngineHealth _lastHealth;
    private static bool _hasHealthResult;
    private static int _probeSequence;

    /// <summary>Most recent probe outcome; never triggers a network call.</summary>
    public static FreeEngineHealth LastHealth => _lastHealth;

    /// <summary>False until the first probe ever completed in this process.</summary>
    public static bool HasHealthResult => _hasHealthResult;

    /// <summary>
    /// Whether the free engine currently reaches a working endpoint. Cached
    /// for HealthTtl; pass force=true to re-check immediately (footer click).
    /// A probe transmits only with an authorization issued by OutboundPolicy;
    /// force relaxes the cache, never the authorization.
    /// </summary>
    public static Task<FreeEngineHealth> GetHealthAsync(
        bool force = false,
        FreeEngineAuthorization? authorization = null)
    {
        lock (HealthGate)
        {
            if (!force && DateTime.UtcNow.Ticks - Interlocked.Read(ref _healthCompletedTicks) < HealthTtl.Ticks)
            {
                return Task.FromResult(_lastHealth);
            }
            if (_healthProbe is not null && !force)
            {
                return _healthProbe;
            }
            var probe = ProbeCoreAsync(authorization);
            _healthProbe = probe;
            return probe;
        }
    }

    private static async Task<FreeEngineHealth> ProbeCoreAsync(FreeEngineAuthorization? authorization)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            // A unique text every time so the probe never answers from the
            // translation cache — it must hit the real endpoint.
            var text = $"ping {Interlocked.Increment(ref _probeSequence)}";
            await TranslateAsync(text, "auto", "zh-CN", authorization, CancellationToken.None);
            _lastHealth = new FreeEngineHealth(
                true, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception exception)
        {
            _lastHealth = new FreeEngineHealth(false, 0, exception.Message);
        }
        finally
        {
            _hasHealthResult = true;
            Interlocked.Exchange(ref _healthCompletedTicks, DateTime.UtcNow.Ticks);
            lock (HealthGate)
            {
                _healthProbe = null;
            }
        }
        return _lastHealth;
    }

    private static InvalidOperationException RateLimitedError(bool inCooldown) => new(
        inCooldown
            ? "免费翻译接口刚刚被限流（HTTP 429），一分钟内暂不自动重试；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。"
            : "免费翻译接口被限流（HTTP 429，本机 IP 已被暂时限制）；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。");
}
