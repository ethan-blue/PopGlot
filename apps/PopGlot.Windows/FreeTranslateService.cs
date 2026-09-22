using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// 轻量的免费引擎失败类别：让 coordinator 按类型归类错误，不再从中文
/// 消息文本里做脆弱的字符串猜测。
/// </summary>
internal enum FreeTranslateFailureKind
{
    /// <summary>免费端点不可用（非 429 的 HTTP 错误、返回网页等）。</summary>
    Unavailable,

    /// <summary>HTTP 429 限流。</summary>
    RateLimited,

    /// <summary>免费端点返回 401/403：与用户的 API Key 无关（免费引擎没有 Key）。</summary>
    Unauthorized,

    /// <summary>连接失败、超时、DNS 等传输层失败——不是“网络翻译已关闭”。</summary>
    NetworkOrTimeout,

    /// <summary>本地长度预算拒绝：内容过长，未发送任何请求。</summary>
    LongContent,

    /// <summary>免费服务返回了无法解析的响应。</summary>
    Unparsable,
}

/// <summary>
/// The free engine's typed failure. Carries a machine-readable
/// <see cref="FreeTranslateFailureKind"/> so error classification never has to
/// guess from message text (a free-endpoint 401 must not read as “check your
/// API key”; a timeout must not read as “network translation disabled”).
/// </summary>
internal sealed class FreeTranslateException(FreeTranslateFailureKind kind, string message)
    : InvalidOperationException(message)
{
    public FreeTranslateFailureKind Kind { get; } = kind;
}

/// <summary>
/// Which public text service a free-engine request may contact.
/// The user picks one. A failure never silently sends the same text to the other.
/// </summary>
internal enum FreeEngineProvider
{
    Google,
    MyMemory,
}

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
    // instead of hammering them on every retry. The cooldown is PER HOST: a
    // rate-limited endpoint must not also bench the healthy alternative, so
    // the next request skips only the cooled-down host.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(1);
    private const int CacheCapacity = 256;
    private const long CacheMaxBytes = 16 * 1024 * 1024;
    private const int MaxSourceCharacters = 5_000;
    /// <summary>Cumulative response-body cap, enforced with or without Content-Length.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 保守的编码后请求 URL 长度预算。协议本身对请求行长度没有统一上限，
    /// 但实际部署中的服务端、代理与中间盒各有自己的上限（常见量级在几 KB）；
    /// 2000 字符覆盖其中最保守的组合。这只是本机发出的本地预算，
    /// 不是任何服务商的官方阈值。超预算的 endpoint 在授权发放前被跳过。
    /// </summary>
    internal const int MaxRequestUrlLength = 2_000;

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, TranslationResponse> Cache = new();
    private static readonly LinkedList<string> CacheOrder = new();
    private static readonly ConcurrentDictionary<string, long> RateLimitedUntilMsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _cacheBytes;

    /// <summary>
    /// Test seam: overrides the monotonic millisecond source the 429 cooldown
    /// is measured against (Environment.TickCount64 in production). Lets tests
    /// prove the cooldown judgement follows a monotonic clock — including
    /// across the TickCount64 wrap boundary — instead of the wall clock.
    /// </summary>
    internal static Func<long>? MonotonicClockOverrideForTest { get; set; }

    private static long MonotonicMilliseconds =>
        MonotonicClockOverrideForTest is { } clock ? clock() : Environment.TickCount64;

    internal sealed record FreeEndpoint(
        string Host,
        Func<string, string, string, string> BuildUrl,
        Func<string, (string Translated, string Phonetic)> Parse,
        FreeEngineProvider Provider = FreeEngineProvider.Google,
        Func<string, string, string, HttpRequestMessage>? BuildRequest = null);

    /// <summary>A04 test seam: replaces the endpoint table so construction
    /// failures (throwing BuildUrl) are injectable.</summary>
    internal static FreeEndpoint[]? EndpointsOverride { get; set; }

    private static FreeEndpoint[] EndpointsFor(FreeEngineProvider provider) =>
        DefaultEndpoints.Where(endpoint => endpoint.Provider == provider).ToArray();

    private static FreeEngineProvider ReadSelectedProvider()
    {
        try
        {
            return ShellSettingsStore.Load().FreeEngineProvider;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return FreeEngineProvider.Google;
        }
    }

    private static readonly FreeEndpoint[] DefaultEndpoints =
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
        new FreeEndpoint(
            "api.mymemory.translated.net",
            static (_, _, _) => "https://api.mymemory.translated.net/get",
            ParseMyMemory,
            FreeEngineProvider.MyMemory,
            static (sl, tl, q) => BuildMyMemoryRequest(sl, tl, q)),
    ];

    public static async Task<TranslationResponse> TranslateAsync(
        string text,
        string sourceLang = "auto",
        string targetLang = "zh-CN",
        FreeEngineAuthorization? authorization = null,
        CancellationToken cancellationToken = default,
        FreeEngineProvider? provider = null)
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
            throw new FreeTranslateException(
                FreeTranslateFailureKind.LongContent,
                $"{EngineWording.FreeEngineName}单次最多翻译 {MaxSourceCharacters} 个字符。请缩短选区，或在设置中配置自己的模型服务。");
        }

        var sl = LanguageCatalog.Normalize(sourceLang);
        var tl = LanguageCatalog.Normalize(targetLang);
        if (tl == LanguageCatalog.Auto)
        {
            tl = "zh-CN";
        }

        var selected = provider ?? ReadSelectedProvider();
        var cacheKey = $"{selected}|{sl}|{tl}|{trimmed}";
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

        var started = Stopwatch.GetTimestamp();
        Exception? lastError = null;
        var endpoints = EndpointsOverride ?? EndpointsFor(selected);
        var sendable = 0;     // endpoints attempted (past cooldown and URL budget)
        var coolingDown = 0;  // endpoints skipped: host still in 429 cooldown
        var overBudget = 0;   // endpoints skipped: final URI beyond the local length budget

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A rate-limited host only benches ITSELF; a healthy alternative
            // endpoint is still tried on this very request.
            if (IsHostCoolingDown(endpoint.Host))
            {
                coolingDown++;
                continue;
            }

            // Built and measured BEFORE the send claim: a local length-budget
            // rejection must never consume an authorization permit, and a
            // GET URL must not silently balloon with long CJK input.
            // A throwing builder is a construction failure: it propagates
            // without being recorded as a transport error.
            HttpRequestMessage request;
            if (endpoint.BuildRequest is { } buildRequest)
            {
                request = buildRequest(sl, tl, trimmed);
            }
            else
            {
                var url = endpoint.BuildUrl(sl, tl, trimmed);
                if (url.Length > MaxRequestUrlLength)
                {
                    overBudget++;
                    continue;
                }

                request = new HttpRequestMessage(HttpMethod.Get, url);
            }

            sendable++;
            try
            {
                using (request)
                {
                request.Headers.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                // A04 send boundary: the claim is the SUBMISSION step — after
                // URL/header construction, immediately before the transport
                // call. A construction failure never burns an AllowOnce
                // permit; a submitted attempt that later fails stays
                // consumed; each fallback endpoint must re-claim, so a
                // revocation between endpoints stops the remaining sends.
                if (!authorization.TryClaimSend(out var sendRefusal))
                {
                    throw new InvalidOperationException(sendRefusal);
                }
                using var response = HttpSenderOverride is { } sender
                    ? await sender(request, cancellationToken)
                    : await HttpClient.SendAsync(request, cancellationToken);

                if ((int)response.StatusCode == 429)
                {
                    MarkHostRateLimited(endpoint.Host);
                    lastError = RateLimitedError(inCooldown: false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    // 401/403 from the free endpoint is its own fact: the
                    // free engine has no API key, so it must never be
                    // reported as a key problem.
                    lastError = new FreeTranslateException(
                        (int)response.StatusCode is 401 or 403
                            ? FreeTranslateFailureKind.Unauthorized
                            : FreeTranslateFailureKind.Unavailable,
                        $"免费翻译服务不可用（HTTP {(int)response.StatusCode}）；可在设置中配置模型服务以获得稳定翻译。");
                    continue;
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        "免费翻译服务返回了网页而不是翻译结果（可能是访问提示页）；请稍后重试，或在设置中配置自己的模型服务。");
                    continue;
                }

                // Cumulative cap enforced while streaming the body: a missing
                // or lying Content-Length cannot grow the buffer unbounded.
                if (response.Content.Headers.ContentLength is { } declaredLength &&
                    declaredLength > MaxResponseBytes)
                {
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                    continue;
                }
                var json = await ReadCappedAsync(response.Content, cancellationToken);
                if (json is null)
                {
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                    continue;
                }

                // A malformed body is a fallback reason, never a crash: the
                // parse failure is recorded as one user-readable message and
                // the next endpoint still gets its attempt. The raw serializer
                // exception never reaches the user.
                string translated;
                string phonetic;
                try
                {
                    (translated, phonetic) = endpoint.Parse(json);
                }
                catch (JsonException)
                {
                    lastError = UnparsableResponseError();
                    continue;
                }
                if (string.IsNullOrWhiteSpace(translated))
                {
                    lastError = UnparsableResponseError();
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Timeout / DNS / connection refused: a transport failure of
                // the free engine itself — NOT the "网络翻译已关闭" setting.
                lastError = new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    $"免费翻译服务暂时无法访问（{exception.Message}）；请检查本机网络后重试。");
            }
        }

        if (sendable == 0)
        {
            if (overBudget > 0)
            {
                // Every candidate was rejected locally before any send: fail
                // fast with zero network traffic and an actionable reason.
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.LongContent,
                    "内容较长，内置免费引擎单次无法处理，请缩短内容或使用已配置的翻译引擎。");
            }
            if (coolingDown > 0)
            {
                throw RateLimitedError(inCooldown: true);
            }
        }

        throw lastError ??
            new FreeTranslateException(FreeTranslateFailureKind.Unavailable, "免费翻译服务不可用。");
    }

    // Signed difference instead of a direct comparison: subtraction over the
    // 2's-complement circle stays correct across the single TickCount64 wrap,
    // so a host marked just before the wrap still benches for exactly the
    // cooldown, never shorter and never forever.
    private static bool IsHostCoolingDown(string host) =>
        RateLimitedUntilMsByHost.TryGetValue(host, out var untilMs) &&
        untilMs - MonotonicMilliseconds > 0;

    private static void MarkHostRateLimited(string host) =>
        RateLimitedUntilMsByHost[host] = MonotonicMilliseconds + (long)RateLimitCooldown.TotalMilliseconds;

    /// <summary>Test seam: clears every per-host 429 cooldown bucket.</summary>
    internal static void ResetRateLimitStateForTest() => RateLimitedUntilMsByHost.Clear();

    private static FreeTranslateException UnparsableResponseError() => new(
        FreeTranslateFailureKind.Unparsable,
        "免费翻译服务返回了无法解析的响应，请重试。");

    /// <summary>
    /// Entry gate: nothing leaves the machine without an authorization issued
    /// by OutboundPolicy — including for health probes. The live per-send
    /// re-check lives in <see cref="Services.FreeEngineAuthorization.TryClaimSend"/>,
    /// which every endpoint send must pass.
    /// </summary>
    private static void EnsureOutboundAuthorized([NotNull] FreeEngineAuthorization? authorization)
    {
        if (authorization is null)
        {
            throw new InvalidOperationException(
                $"{EngineWording.FreeEngineName}未获出网授权；未发送任何请求。可在「设置 → 隐私与数据」中允许，或配置自己的模型服务。");
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

    /// <summary>
    /// MyMemory has no auto-detect. When the user left the source on auto,
    /// pick a side from the characters actually present instead of guessing
    /// a language over the network.
    /// </summary>
    internal static string ResolveMyMemorySource(string sourceLang, string text)
    {
        var normalized = LanguageCatalog.Normalize(sourceLang);
        if (!string.Equals(normalized, LanguageCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var cjk = 0;
        var latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')
            {
                cjk++;
            }
            else if (char.IsAsciiLetter(ch))
            {
                latin++;
            }
        }

        return cjk > latin ? "zh-CN" : "en";
    }

    private static HttpRequestMessage BuildMyMemoryRequest(string sourceLang, string targetLang, string text)
    {
        var target = LanguageCatalog.Normalize(targetLang);
        if (string.Equals(target, LanguageCatalog.Auto, StringComparison.OrdinalIgnoreCase))
        {
            target = "zh-CN";
        }

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["langpair"] = $"{ResolveMyMemorySource(sourceLang, text)}|{target}",
        });
        return new HttpRequestMessage(HttpMethod.Post, "https://api.mymemory.translated.net/get")
        {
            Content = body,
        };
    }

    internal static (string Translated, string Phonetic) ParseMyMemory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("responseStatus", out var status) &&
            status.ValueKind == JsonValueKind.Number &&
            status.GetInt32() != 200)
        {
            return (string.Empty, string.Empty);
        }

        if (!root.TryGetProperty("responseData", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("translatedText", out var translated) ||
            translated.ValueKind != JsonValueKind.String)
        {
            return (string.Empty, string.Empty);
        }

        var text = translated.GetString() ?? string.Empty;
        // Quota and rejection come back as HTTP 200 with this sentence in the text.
        if (text.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, string.Empty);
        }

        return (text, string.Empty);
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
    private static readonly ConcurrentDictionary<FreeEngineProvider, FreeEngineHealth> HealthByProvider = new();
    private static readonly ConcurrentDictionary<FreeEngineProvider, long> HealthCompletedTicks = new();
    private static readonly ConcurrentDictionary<FreeEngineProvider, Task<FreeEngineHealth>> HealthProbes = new();
    private static int _probeSequence;

    /// <summary>Most recent probe of the provider the user currently has selected.</summary>
    public static FreeEngineHealth LastHealth => LastHealthFor(ReadSelectedProvider());

    /// <summary>False until that selected provider has been probed in this process.</summary>
    public static bool HasHealthResult => HasHealthResultFor(ReadSelectedProvider());

    public static bool HasHealthResultFor(FreeEngineProvider provider) =>
        HealthByProvider.ContainsKey(provider);

    public static FreeEngineHealth LastHealthFor(FreeEngineProvider provider) =>
        HealthByProvider.TryGetValue(provider, out var health) ? health : default;

    /// <summary>
    /// Whether the selected free engine currently reaches a working endpoint.
    /// Cached for HealthTtl; pass force=true to re-check immediately (footer click).
    /// A probe transmits only with an authorization issued by OutboundPolicy;
    /// force relaxes the cache, never the authorization. One provider's result
    /// never stands in for the other.
    /// </summary>
    public static Task<FreeEngineHealth> GetHealthAsync(
        bool force = false,
        FreeEngineAuthorization? authorization = null) =>
        GetHealthAsync(ReadSelectedProvider(), force, authorization);

    public static Task<FreeEngineHealth> GetHealthAsync(
        FreeEngineProvider provider,
        bool force,
        FreeEngineAuthorization? authorization)
    {
        lock (HealthGate)
        {
            if (!force &&
                HealthByProvider.TryGetValue(provider, out var cached) &&
                HealthCompletedTicks.TryGetValue(provider, out var completed) &&
                DateTime.UtcNow.Ticks - completed < HealthTtl.Ticks)
            {
                return Task.FromResult(cached);
            }
            if (!force && HealthProbes.TryGetValue(provider, out var inflight))
            {
                return inflight;
            }
            var probe = ProbeCoreAsync(provider, authorization);
            HealthProbes[provider] = probe;
            return probe;
        }
    }

    private static async Task<FreeEngineHealth> ProbeCoreAsync(
        FreeEngineProvider provider,
        FreeEngineAuthorization? authorization)
    {
        var started = Stopwatch.GetTimestamp();
        FreeEngineHealth health;
        try
        {
            // A unique text every time so the probe never answers from the
            // translation cache — it must hit the real endpoint.
            var text = $"ping {Interlocked.Increment(ref _probeSequence)}";
            await TranslateAsync(text, "auto", "zh-CN", authorization, CancellationToken.None, provider);
            health = new FreeEngineHealth(
                true, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
        catch (Exception exception)
        {
            health = new FreeEngineHealth(false, 0, exception.Message);
        }
        finally
        {
            lock (HealthGate)
            {
                HealthProbes.TryRemove(provider, out _);
            }
        }

        HealthByProvider[provider] = health;
        HealthCompletedTicks[provider] = DateTime.UtcNow.Ticks;
        return health;
    }

    private static FreeTranslateException RateLimitedError(bool inCooldown) => new(
        FreeTranslateFailureKind.RateLimited,
        inCooldown
            ? "免费翻译接口刚刚被限流（HTTP 429），一分钟内暂不自动重试；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。"
            : "免费翻译接口被限流（HTTP 429，本机 IP 已被暂时限制）；通常几分钟内自动恢复，也可在设置中配置自己的模型服务。");
}
