using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// The user picks one (or opts into <see cref="Auto"/>, which picks ONE single
/// route per request). A failure never silently sends the same text to another.
/// </summary>
internal enum FreeEngineProvider
{
    Google,
    MyMemory,
    Youdao,
    Bing,
    Auto,
    Custom,
    Yandex,
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
    internal static HttpClient? HttpClientOverrideForTest { get; set; }

    /// <summary>免费端点 TCP/TLS 连接超时：2.5 秒内快速失败，避免死端点拖垮响应。</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>免费端点整请求超时：长文本需要比 12 秒更宽的预算。</summary>
    internal static readonly TimeSpan WholeRequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(2.5);

    internal static HttpClient GetActiveHttpClient() =>
        HttpClientOverrideForTest ??
        ProxyHttpClientFactory.GetClient(ProxyConfig.FromCurrentSettings(), ConnectTimeout, WholeRequestTimeout);

    // These undocumented endpoints rate-limit per IP; after a 429 we back off
    // instead of hammering them on every retry. The cooldown is PER HOST: a
    // rate-limited endpoint must not also bench the healthy alternative, so
    // the next request skips only the cooled-down host.
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TransportCooldown = TimeSpan.FromSeconds(90);
    private const int CacheCapacity = 256;
    private const long CacheMaxBytes = 16 * 1024 * 1024;
    private const int MaxSourceCharacters = 5_000;
    private const int YoudaoMaxSourceCharacters = 120;
    // MyMemory's documented `q` contract is byte based rather than character
    // based. Reject locally before authorization is consumed or a body is sent.
    internal const int MyMemoryMaxQueryBytes = 500;
    /// <summary>Cumulative response-body cap, enforced with or without Content-Length.</summary>
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 保守的编码后请求 URL 长度预算。协议本身对请求行长度没有统一上限，
    /// 但实际部署中的服务端、代理与中间盒各有自己的上限（常见量级在几 KB）；
    /// 2000 字符覆盖其中最保守的组合。这只是本机发出的本地预算，
    /// 不是任何服务商的官方阈值。超预算的 endpoint 在授权发放前被跳过。
    /// </summary>
    internal const int MaxRequestUrlLength = 2_000;

    // 本机配额：公共端点按 IP 限流，一个失控的客户端会把这台机器的 IP 打
    // 到被封。每小时上限保护的是用户自己的可用性（也是对被借用端点的自我
    // 节制），超限后本小时内不再自动重试。
    internal const int HourlySendBudget = 400;
    private const long QuotaWindowMs = 60L * 60 * 1000;
    private static readonly object QuotaGate = new();
    private static int _quotaUsed;
    private static long _quotaWindowStartMs;

    /// <summary>Test seam: clears the local hourly budget.</summary>
    internal static void ResetQuotaForTest()
    {
        lock (QuotaGate)
        {
            _quotaUsed = 0;
            _quotaWindowStartMs = 0;
        }
    }

    /// <summary>Remaining sends in the current hour (test/invariant seam).</summary>
    internal static int QuotaRemaining
    {
        get
        {
            var now = MonotonicMilliseconds;
            lock (QuotaGate)
            {
                if (_quotaWindowStartMs != 0 && now - _quotaWindowStartMs >= QuotaWindowMs)
                {
                    return HourlySendBudget;
                }
                return Math.Max(0, HourlySendBudget - _quotaUsed);
            }
        }
    }

    private static bool TryConsumeQuota()
    {
        var now = MonotonicMilliseconds;
        lock (QuotaGate)
        {
            // 有符号差：跨 TickCount64 回绕仍然成立。
            if (_quotaWindowStartMs == 0 || now - _quotaWindowStartMs >= QuotaWindowMs)
            {
                _quotaWindowStartMs = now;
                _quotaUsed = 0;
            }
            if (_quotaUsed >= HourlySendBudget)
            {
                return false;
            }
            _quotaUsed++;
            return true;
        }
    }

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, TranslationResponse> Cache = new();
    private static readonly LinkedList<string> CacheOrder = new();
    private static readonly ConcurrentDictionary<string, long> RateLimitedUntilMsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> TransportCooldownUntilMsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> LastSuccessAtMsByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> LastSuccessOrderByHost =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _successOrderSeq;
    private static long _cacheBytes;

    // Bing IG / IID / AbusePreventionHelper token cache.
    private static readonly SemaphoreSlim BingTokenGate = new(1, 1);
    private static readonly TimeSpan BingTokenTtl = TimeSpan.FromMinutes(10);
    private static BingTokenState? _bingTokenCache;
    private static int _bingSequence;

    internal sealed record BingTokenState(
        string Ig,
        string Iid,
        string Key,
        string Token,
        long ExpiresAtMs);

    private static readonly Regex BingIgRegex = new(
        "IG:\"([^\"]+)\"",
        RegexOptions.Compiled);
    private static readonly Regex BingDataIidRegex = new(
        "data-iid=\"([^\"]+)\"",
        RegexOptions.Compiled);
    private static readonly Regex BingIidFallbackRegex = new(
        "IID\\s*[:=]\\s*\"([^\"]+)\"",
        RegexOptions.Compiled);
    private static readonly Regex BingAbuseParamsRegex = new(
        "params_AbusePreventionHelper\\s*=\\s*\\[\\s*(\\d+)\\s*,\\s*\"([^\"]+)\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Test seam: overrides the monotonic millisecond source the cooldowns and
    /// hourly budget are measured against (Environment.TickCount64 in production).
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

    internal static FreeEngineProvider CurrentProvider() => ReadSelectedProvider();

    /// <summary>
    /// Returns true when <paramref name="text"/> is short enough for the active
    /// engine's per-request limit (5,000 chars total) yet exceeds a single
    /// GET URL / query byte budget, so callers can send it in ordered batches.
    /// </summary>
    internal static bool NeedsBatching(string text, out int recommendedChunkChars) =>
        NeedsBatching(text, ReadSelectedProvider(), out recommendedChunkChars);

    internal static bool NeedsBatching(string text, FreeEngineProvider provider, out int recommendedChunkChars)
    {
        if (provider == FreeEngineProvider.Auto)
            provider = SelectAutoProvider(text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSourceCharacters)
        {
            recommendedChunkChars = 0;
            return false;
        }
        if (provider is FreeEngineProvider.Google or FreeEngineProvider.Youdao or FreeEngineProvider.Custom or FreeEngineProvider.Yandex)
        {
            recommendedChunkChars = 0;
            return false;
        }
        if (provider == FreeEngineProvider.Bing)
        {
            recommendedChunkChars = 1000;
            return text.Length > 1000;
        }
        if (provider == FreeEngineProvider.MyMemory)
        {
            recommendedChunkChars = 140;
            return Encoding.UTF8.GetByteCount(text) > MyMemoryMaxQueryBytes - 40;
        }
        recommendedChunkChars = 160;
        // Estimate URL-encoded payload against MaxRequestUrlLength (leaving ~220 chars for host/query params).
        return Uri.EscapeDataString(text).Length > MaxRequestUrlLength - 220;
    }

    private static FreeEngineProvider ReadSelectedProvider()
    {
        try
        {
            return OutboundPolicy.SettingsLoader().FreeEngineProvider;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                return ShellSettingsStore.Load().FreeEngineProvider;
            }
            catch (Exception)
            {
                return FreeEngineProvider.Google;
            }
        }
    }

    // 0.1.35：端点地址来自混淆目录（FreeEndpointCatalog），不再明文硬编码。
    // 详见该文件的诚实边界说明：提高扒取门槛，不等于加密。
    private static readonly FreeEndpoint[] DefaultEndpoints =
    [
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GoogleWebsiteUrl),
            static (sl, tl, q) => FreeEndpointCatalog.GoogleWebsiteUrl
                .Replace("{sl}", sl).Replace("{tl}", tl).Replace("{q}", HttpUtility.UrlEncode(q)),
            ParseGtxSingle),
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YandexTranslateUrl),
            static (_, _, _) => FreeEndpointCatalog.YandexTranslateUrl,
            YandexPublicTranslation.ParseResponse,
            FreeEngineProvider.Yandex,
            YandexPublicTranslation.BuildRequest),
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GtxSingleUrl),
            static (sl, tl, q) => FreeEndpointCatalog.GtxSingleUrl
                .Replace("{sl}", sl)
                .Replace("{tl}", tl)
                .Replace("{q}", HttpUtility.UrlEncode(q)),
            ParseGtxSingle),
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.DictChromeExUrl),
            static (sl, tl, q) => FreeEndpointCatalog.DictChromeExUrl
                .Replace("{sl}", sl)
                .Replace("{tl}", tl)
                .Replace("{q}", HttpUtility.UrlEncode(q)),
            ParseDictChromeEx),
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.MyMemoryUrl),
            static (_, _, _) => FreeEndpointCatalog.MyMemoryUrl,
            ParseMyMemory,
            FreeEngineProvider.MyMemory,
            static (sl, tl, q) => BuildMyMemoryRequest(sl, tl, q)),
        new FreeEndpoint(
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YoudaoDictUrl),
            static (_, _, q) => FreeEndpointCatalog.YoudaoDictUrl
                .Replace("{q}", Uri.EscapeDataString(q)),
            ParseYoudao,
            FreeEngineProvider.Youdao),
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
                $"{EngineWording.FreeEngineName}原文最多 {MaxSourceCharacters} 个字符，实际容量还受当前服务的编码字节预算限制。请缩短选区，或在设置中配置自己的模型服务。");
        }

        var sl = LanguageCatalog.Normalize(sourceLang);
        var tl = LanguageCatalog.Normalize(targetLang);
        if (tl == LanguageCatalog.Auto)
        {
            tl = "zh-CN";
        }

        var selected = provider ?? authorization.ResolvedProvider ?? ReadSelectedProvider();
        var isAuto = selected == FreeEngineProvider.Auto || authorization.WasAutoSelected;
        var effective = selected == FreeEngineProvider.Auto ? SelectAutoProvider(trimmed, sl, tl) : selected;
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestBudget.CancelAfter(WholeRequestTimeout);

        try
        {
            var response = await TranslateWithResolvedProviderAsync(
                effective,
                trimmed,
                sl,
                tl,
                authorization,
                requestBudget.Token);

            if (isAuto)
            {
                return response with
                {
                    OverrideEngineLabel = $"{EngineWording.AutoEngineName} · {EngineWording.NameFor(effective)}",
                };
            }
            if (effective is FreeEngineProvider.Youdao or FreeEngineProvider.Yandex && string.IsNullOrEmpty(response.OverrideEngineLabel))
            {
                return response with
                {
                    OverrideEngineLabel = EngineWording.DisplayNameFor(effective),
                };
            }
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestBudget.IsCancellationRequested)
        {
            foreach (var host in HostsForProvider(effective)) MarkHostTransportCooldown(host);
            throw new FreeTranslateException(FreeTranslateFailureKind.NetworkOrTimeout,
                "公共翻译请求超过 30 秒预算，已取消；请检查网络、代理或切换线路。");
        }
        catch (FreeTranslateException ex) when (isAuto && ex.Kind is FreeTranslateFailureKind.NetworkOrTimeout or FreeTranslateFailureKind.RateLimited)
        {
            throw new FreeTranslateException(
                ex.Kind,
                $"{EngineWording.AutoEngineName}本次选择了 {EngineWording.NameFor(effective)}，但暂时不可用（{ex.Message}）；已暂时冷却该线路，重试将自动切换其他可用线路。");
        }
    }

    private static async Task<TranslationResponse> TranslateWithResolvedProviderAsync(
        FreeEngineProvider selected,
        string trimmed,
        string sl,
        string tl,
        FreeEngineAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (selected == FreeEngineProvider.Custom)
            return await CustomTranslationApi.TranslateAsync(trimmed, sl, tl, authorization, cancellationToken).ConfigureAwait(false);

        if (selected == FreeEngineProvider.MyMemory &&
            Encoding.UTF8.GetByteCount(trimmed) > MyMemoryMaxQueryBytes)
        {
            throw new FreeTranslateException(
                FreeTranslateFailureKind.LongContent,
                $"MyMemory 单次最多处理 {MyMemoryMaxQueryBytes} 字节。请缩短内容或使用已配置的翻译引擎。");
        }
        if (selected == FreeEngineProvider.MyMemory)
        {
            sl = ResolveMyMemorySource(sl, trimmed);
        }
        if (selected == FreeEngineProvider.Youdao)
        {
            ValidateYoudaoRequest(trimmed, sl, tl);
        }

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
            return cached with { Diagnostics = cached.Diagnostics is { } diagnostics ? diagnostics with { ElapsedMs = 0 } : null };
        }

        if (selected == FreeEngineProvider.Bing && EndpointsOverride is null)
        {
            return await TranslateBingAsync(trimmed, sl, tl, cacheKey, authorization, cancellationToken);
        }

        var started = Stopwatch.GetTimestamp();
        Exception? lastError = null;
        var endpoints = EndpointsOverride ?? EndpointsFor(selected);
        if (EndpointsOverride is null && selected == FreeEngineProvider.Google &&
            Uri.EscapeDataString(trimmed).Length > MaxRequestUrlLength - 220)
        {
            // The live GTX service accepts form POST. Keep long text in one request
            // instead of serializing dozens of 160-character GET chunks.
            // GET fallbacks remain bounded and never switch recipient/provider.
            static FreeEndpoint FormEndpoint(string template, Func<string, (string, string)> parse) => new(
                FreeEndpointCatalog.HostOf(template), (_, _, _) => template, parse,
                BuildRequest: (source, target, text) => new HttpRequestMessage(HttpMethod.Post,
                    template.Replace("&q={q}", string.Empty, StringComparison.Ordinal)
                        .Replace("{sl}", source, StringComparison.Ordinal).Replace("{tl}", target, StringComparison.Ordinal))
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = text }) });
            endpoints = [FormEndpoint(FreeEndpointCatalog.GoogleWebsiteUrl, ParseGtxSingle),
                FormEndpoint(FreeEndpointCatalog.GtxSingleUrl, ParseGtxSingle),
                FormEndpoint(FreeEndpointCatalog.DictChromeExUrl, ParseDictChromeEx), .. endpoints];
        }
        endpoints = OrderEndpoints(endpoints, selected);
        var sendable = 0;       // endpoints attempted (past cooldown and URL budget)
        var rateCoolingDown = 0;
        var transportCoolingDown = 0;
        var overBudget = 0;     // endpoints skipped: final URI beyond the local length budget

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A cooled-down host (429 or transport failure) benches ONLY itself;
            // healthy alternative endpoints still serve this request, and skipped
            // endpoints NEVER burn hourly quota.
            if (IsHostCoolingDown(endpoint.Host))
            {
                if (IsHostTransportCoolingDown(endpoint.Host))
                {
                    transportCoolingDown++;
                }
                else
                {
                    rateCoolingDown++;
                }
                continue;
            }

            // Built and measured BEFORE quota & send claim: a local length-budget
            // rejection must never consume quota or an authorization permit, and a
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

            // 本机配额在冷却与本地长度检查之后、正式发送之前消耗：
            // 被跳过或超长预算的端点绝不浪费配额。
            if (!TryConsumeQuota())
            {
                request.Dispose();
                lastError = new FreeTranslateException(
                    FreeTranslateFailureKind.RateLimited,
                    $"本机这一小时内使用内置免费引擎已达 {HourlySendBudget} 次上限，暂停自动重试以保护公共端点；可稍后重试，或在设置中配置自己的模型服务。");
                break;
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
                        : await GetActiveHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

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

                    MarkHostSucceeded(endpoint.Host);
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
                // Timeout / DNS / connection refused: mark 90s per-host transport
                // cooldown so subsequent requests skip the dead host immediately.
                MarkHostTransportCooldown(endpoint.Host);
                lastError = new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    "免费翻译服务暂时无法访问（连接、代理、DNS 或超时）；请检查本机网络后重试。");
            }
        }

        if (sendable == 0 && lastError is null)
        {
            if (overBudget > 0)
            {
                // Every candidate was rejected locally before any send: fail
                // fast with zero network traffic and an actionable reason.
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.LongContent,
                    "内容较长，内置免费引擎单次无法处理，请缩短内容或使用已配置的翻译引擎。");
            }
            if (transportCoolingDown > 0)
            {
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    TransportCooldownMessage(selected));
            }
            if (rateCoolingDown > 0)
            {
                throw RateLimitedError(inCooldown: true);
            }
        }

        throw lastError ??
            new FreeTranslateException(FreeTranslateFailureKind.Unavailable, "免费翻译服务不可用。");
    }

    // ================= Endpoint stickiness & Auto route selection =================

    private static FreeEndpoint[] OrderEndpoints(FreeEndpoint[] endpoints, FreeEngineProvider provider)
    {
        if (endpoints.Length <= 1)
        {
            return endpoints;
        }

        var healthDemoted = TryGetValidHealth(provider, out var cachedHealth) && !cachedHealth.Ok;

        return endpoints
            .Select((endpoint, index) =>
            {
                var coolingDown = IsHostCoolingDown(endpoint.Host);
                var hasSuccess = LastSuccessOrderByHost.TryGetValue(endpoint.Host, out var order);
                var tier = coolingDown
                    ? 3
                    : hasSuccess
                        ? 0
                        : healthDemoted ? 2 : 1;
                return (Endpoint: endpoint, Index: index, Tier: tier, Order: order);
            })
            .OrderBy(item => item.Tier)
            .ThenByDescending(item => item.Order)
            .ThenBy(item => item.Index)
            .Select(item => item.Endpoint)
            .ToArray();
    }

    /// <summary>
    /// Selects ONE public route for <see cref="FreeEngineProvider.Auto"/> using
    /// per-host cooldown, last-success stickiness, cached health probes, and
    /// input shape. Never transmits text itself.
    /// </summary>
    internal static FreeEngineProvider SelectAutoProvider(
        string text,
        string sourceLang = "auto",
        string targetLang = "zh-CN")
    {
        var trimmed = text?.Trim() ?? string.Empty;
        var sl = LanguageCatalog.Normalize(sourceLang);
        var tl = LanguageCatalog.Normalize(targetLang);
        if (tl == LanguageCatalog.Auto)
        {
            tl = "zh-CN";
        }

        var isShortLookup = IsYoudaoShortLookup(trimmed, sl, tl);
        var candidates = new List<FreeEngineProvider>(4);
        if (IsYoudaoLanguagePairSupported(sl, tl) && !IsYoudaoTooLongOrMultiSentence(trimmed))
        {
            candidates.Add(FreeEngineProvider.Youdao);
        }
        // The anonymous Bing web protocol may return 401 even with a fresh token.
        // Do not route an unsuspecting first Auto query there without positive evidence.
        if (ProviderLastSuccessOrder(FreeEngineProvider.Bing) > 0 ||
            (TryGetValidHealth(FreeEngineProvider.Bing, out var bingHealth) && bingHealth.Ok))
            candidates.Add(FreeEngineProvider.Bing);
        candidates.Add(FreeEngineProvider.Google);
        if (Encoding.UTF8.GetByteCount(trimmed) <= MyMemoryMaxQueryBytes &&
            (sl != LanguageCatalog.Auto || LanguageCatalog.DetectScript(trimmed) is not null))
        {
            candidates.Add(FreeEngineProvider.MyMemory);
        }

        var available = candidates.Where(p => !IsProviderCoolingDown(p)).ToList();
        if (available.Count == 0)
        {
            var anyTransportCooldown = candidates.Any(IsProviderTransportCoolingDown);
            if (anyTransportCooldown)
            {
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    TransportCooldownMessage(FreeEngineProvider.Auto));
            }
            throw RateLimitedError(inCooldown: true);
        }

        int ShapeRank(FreeEngineProvider p) => isShortLookup
            ? p switch
            {
                FreeEngineProvider.Youdao => 0,
                FreeEngineProvider.Bing => 1,
                FreeEngineProvider.Google => 2,
                FreeEngineProvider.MyMemory => 3,
                _ => 4,
            }
            : p switch
            {
                FreeEngineProvider.Bing => 0,
                FreeEngineProvider.Google => 1,
                FreeEngineProvider.MyMemory => 2,
                FreeEngineProvider.Youdao => 3,
                _ => 4,
            };

        int HealthTier(FreeEngineProvider p)
        {
            var hasSuccess = ProviderLastSuccessOrder(p) > 0;
            var hasHealth = TryGetValidHealth(p, out var health);
            if (hasSuccess || (hasHealth && health.Ok))
            {
                return 0; // Proven healthy
            }
            if (hasHealth && !health.Ok)
            {
                return 2; // Recently failed health probe
            }
            return 1; // Untried
        }

        return available
            .OrderBy(HealthTier)
            .ThenByDescending(ProviderLastSuccessOrder)
            .ThenBy(ShapeRank)
            .First();
    }

    private static string[] HostsForProvider(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.Google =>
        [
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GoogleWebsiteUrl),
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GtxSingleUrl),
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.DictChromeExUrl),
        ],
        FreeEngineProvider.Yandex => [FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YandexTranslateUrl)],
        FreeEngineProvider.MyMemory => [FreeEndpointCatalog.HostOf(FreeEndpointCatalog.MyMemoryUrl)],
        FreeEngineProvider.Youdao => [FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YoudaoDictUrl)],
        FreeEngineProvider.Bing => [FreeEndpointCatalog.HostOf(FreeEndpointCatalog.BingTranslateV3Url)],
        FreeEngineProvider.Auto =>
        [
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.BingTranslateV3Url),
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YoudaoDictUrl),
            FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GtxSingleUrl),
        ],
        _ => [],
    };

    private static bool IsProviderCoolingDown(FreeEngineProvider provider)
    {
        var hosts = HostsForProvider(provider);
        return hosts.Length > 0 && hosts.All(IsHostCoolingDown);
    }

    private static bool IsProviderTransportCoolingDown(FreeEngineProvider provider)
    {
        var hosts = HostsForProvider(provider);
        return hosts.Length > 0 && hosts.All(IsHostTransportCoolingDown);
    }

    private static long ProviderLastSuccessOrder(FreeEngineProvider provider)
    {
        long latest = 0;
        foreach (var host in HostsForProvider(provider))
        {
            if (LastSuccessOrderByHost.TryGetValue(host, out var order) && order > latest)
            {
                latest = order;
            }
        }
        return latest;
    }

    // ================= Zero-payload DNS / TCP / TLS Prewarm =================

    /// <summary>
    /// Opens a zero-payload HEAD preconnect to warm DNS, TCP, and TLS pools
    /// for the active free provider. Never sends user text, and only runs when
    /// SafeDevMode is off, NetworkEnabled is on, and FreeEngineConsent == Allowed.
    /// </summary>
    public static async Task WarmupFreeRoutesAsync(
        ShellSettings settings,
        CancellationToken cancellationToken = default,
        ProviderSettings? providerSettingsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!RuntimeGate.NewWorkAllowed || settings.FreeEngineConsent != FreeEngineConsent.Allowed)
        {
            return;
        }

        var coreSettings = providerSettingsOverride ?? TryResolveLiveProviderSettings();
        if (coreSettings is null || coreSettings.SafeDevMode || !coreSettings.NetworkEnabled)
        {
            return;
        }

        var hosts = settings.FreeEngineProvider switch
        {
            FreeEngineProvider.Auto => new[]
            {
                FreeEndpointCatalog.HostOf(FreeEndpointCatalog.BingTranslateV3Url),
                FreeEndpointCatalog.HostOf(FreeEndpointCatalog.YoudaoDictUrl),
                FreeEndpointCatalog.HostOf(FreeEndpointCatalog.GoogleWebsiteUrl),
            },
            var specific => HostsForProvider(specific).Take(1).ToArray(),
        };

        var client = HttpClientOverrideForTest ??
            ProxyHttpClientFactory.GetClient(ProxyConfig.FromShellSettings(settings), ConnectTimeout, WholeRequestTimeout);

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHostCoolingDown(host))
            {
                continue;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(WarmupTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
                request.Headers.Add(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                using var response = HttpSenderOverride is { } sender
                    ? await sender(request, cts.Token).ConfigureAwait(false)
                    : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Best-effort connection warmup must never surface errors or block startup.
            }
        }
    }

    private static ProviderSettings? TryResolveLiveProviderSettings()
    {
        if (OutboundPolicy.LiveSettingsLoader is { } loader)
        {
            try
            {
                return loader();
            }
            catch (Exception)
            {
                return null;
            }
        }
        try
        {
            return CoreBridge.GetSettings();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ================= Cooldowns & Test Seams =================

    // Signed difference instead of a direct comparison: subtraction over the
    // 2's-complement circle stays correct across the single TickCount64 wrap,
    // so a host marked just before the wrap still benches for exactly the
    // cooldown, never shorter and never forever.
    private static bool IsHostRateLimited(string host) =>
        RateLimitedUntilMsByHost.TryGetValue(host, out var untilMs) &&
        untilMs - MonotonicMilliseconds > 0;

    private static bool IsHostTransportCoolingDown(string host) =>
        TransportCooldownUntilMsByHost.TryGetValue(host, out var untilMs) &&
        untilMs - MonotonicMilliseconds > 0;

    private static bool IsHostCoolingDown(string host) =>
        IsHostRateLimited(host) || IsHostTransportCoolingDown(host);

    private static void MarkHostRateLimited(string host) =>
        RateLimitedUntilMsByHost[host] = MonotonicMilliseconds + (long)RateLimitCooldown.TotalMilliseconds;

    private static void MarkHostTransportCooldown(string host) =>
        TransportCooldownUntilMsByHost[host] = MonotonicMilliseconds + (long)TransportCooldown.TotalMilliseconds;

    private static void MarkHostSucceeded(string host)
    {
        LastSuccessAtMsByHost[host] = MonotonicMilliseconds;
        LastSuccessOrderByHost[host] = Interlocked.Increment(ref _successOrderSeq);
    }

    /// <summary>Test seam: clears every per-host cooldown and stickiness bucket.</summary>
    internal static void ResetRateLimitStateForTest()
    {
        RateLimitedUntilMsByHost.Clear();
        TransportCooldownUntilMsByHost.Clear();
        LastSuccessAtMsByHost.Clear();
        LastSuccessOrderByHost.Clear();
    }

    internal static void ResetTransportStateForTest()
    {
        TransportCooldownUntilMsByHost.Clear();
        LastSuccessAtMsByHost.Clear();
        LastSuccessOrderByHost.Clear();
        _bingTokenCache = null;
        HealthByProvider.Clear();
        HealthCompletedTicks.Clear();
    }

    internal static void ResetBingTokenCacheForTest() => _bingTokenCache = null;

    internal static void ResetHealthCacheForTest()
    {
        HealthByProvider.Clear();
        HealthCompletedTicks.Clear();
    }

    private static string TransportCooldownMessage(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.Google =>
            "当前网络无法直连 Google 公共线路，可切换 Bing / 有道词典 / MyMemory，或检查代理设置。",
        FreeEngineProvider.Bing =>
            "当前网络无法连接 Bing 翻译，可切换有道词典 / MyMemory，或检查网络与代理设置。",
        FreeEngineProvider.Youdao =>
            "当前网络无法连接有道词典，可切换 Bing / MyMemory，或检查网络设置。",
        FreeEngineProvider.MyMemory =>
            "当前网络无法连接 MyMemory，可切换 Bing / 有道词典，或检查网络与代理设置。",
        _ =>
            "当前所有公共翻译线路均暂时不可达，可检查网络或代理设置，或在设置中配置自己的模型服务。",
    };

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

    // ================= Youdao Dictionary Route =================

    internal static bool IsYoudaoLanguagePairSupported(string sourceLang, string targetLang)
    {
        var sl = LanguageCatalog.Normalize(sourceLang);
        var tl = LanguageCatalog.Normalize(targetLang);
        if (tl == LanguageCatalog.Auto)
        {
            tl = "zh-CN";
        }
        var targetOk = tl is "zh-CN" or "zh-TW" or "en";
        var sourceOk = sl is LanguageCatalog.Auto or "zh-CN" or "zh-TW" or "en";
        return targetOk && sourceOk;
    }

    internal static bool IsYoudaoShortLookup(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }
        if (!IsYoudaoLanguagePairSupported(sourceLang, targetLang) || IsYoudaoTooLongOrMultiSentence(text))
        {
            return false;
        }
        var trimmed = text.Trim();
        if (trimmed[^1] is '.' or '。' or '!' or '！' or '?' or '？')
        {
            return false;
        }
        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 3;
    }

    internal static bool IsYoudaoTooLongOrMultiSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        if (text.Length > YoudaoMaxSourceCharacters)
        {
            return true;
        }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1)
        {
            return true;
        }
        return IsMultiSentence(text);
    }

    private static bool IsMultiSentence(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            var ch = text[i];
            if (ch is '。' or '！' or '？')
            {
                if (HasSubstantiveContentAfter(text, i + 1))
                {
                    return true;
                }
                continue;
            }

            if (ch is '!' or '?' or '.')
            {
                if (!char.IsWhiteSpace(text[i + 1]))
                {
                    continue;
                }
                if (ch == '.' && IsLikelyAbbreviation(text, i))
                {
                    continue;
                }
                if (HasSubstantiveContentAfter(text, i + 1))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsLikelyAbbreviation(string text, int dotIndex)
    {
        var start = dotIndex - 1;
        while (start >= 0 && char.IsLetter(text[start]))
        {
            start--;
        }
        var tokenLength = dotIndex - 1 - start;
        if (tokenLength <= 0 || tokenLength > 3)
        {
            return false;
        }
        var token = text.Substring(start + 1, tokenLength);
        return token.Equals("Mr", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("Mrs", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("Ms", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("Dr", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("St", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("vs", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("etc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSubstantiveContentAfter(string text, int startIndex)
    {
        var letterCount = 0;
        for (var i = startIndex; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                letterCount++;
                if (letterCount >= 2)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static void ValidateYoudaoRequest(string trimmed, string sl, string tl)
    {
        if (!IsYoudaoLanguagePairSupported(sl, tl))
        {
            throw new FreeTranslateException(
                FreeTranslateFailureKind.Unavailable,
                "有道词典仅支持中英单词与短语，长句建议切换 Bing 或自建引擎");
        }
        if (IsYoudaoTooLongOrMultiSentence(trimmed))
        {
            throw new FreeTranslateException(
                FreeTranslateFailureKind.LongContent,
                "有道词典仅支持中英单词与短语，长句建议切换 Bing 或自建引擎");
        }
    }

    internal static (string Translated, string Phonetic) ParseYoudao(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty);
        }

        var phonetic = string.Empty;
        var definitions = new List<string>();

        if (root.TryGetProperty("ec", out var ec) && ec.ValueKind == JsonValueKind.Object &&
            ec.TryGetProperty("word", out var ecWord))
        {
            ExtractYoudaoWordEntries(ecWord, definitions, ref phonetic);
        }

        if (definitions.Count == 0 &&
            root.TryGetProperty("ce", out var ce) && ce.ValueKind == JsonValueKind.Object &&
            ce.TryGetProperty("word", out var ceWord))
        {
            ExtractYoudaoWordEntries(ceWord, definitions, ref phonetic);
        }

        if (definitions.Count > 0)
        {
            return (string.Join("\n", definitions.Take(4)), phonetic);
        }

        if (root.TryGetProperty("fanyi", out var fanyi) && fanyi.ValueKind == JsonValueKind.Object &&
            fanyi.TryGetProperty("tran", out var tran) && tran.ValueKind == JsonValueKind.String)
        {
            var fanyiText = tran.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(fanyiText))
            {
                return (fanyiText, phonetic);
            }
        }

        if (root.TryGetProperty("web_trans", out var webTrans) && webTrans.ValueKind == JsonValueKind.Object &&
            webTrans.TryGetProperty("web-translation", out var webList) && webList.ValueKind == JsonValueKind.Array)
        {
            var webValues = new List<string>();
            foreach (var entry in webList.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("trans", out var transArray) ||
                    transArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var item in transArray.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("value", out var val) &&
                        val.ValueKind == JsonValueKind.String)
                    {
                        var v = val.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(v) && !webValues.Contains(v, StringComparer.Ordinal))
                        {
                            webValues.Add(v);
                        }
                    }
                    if (webValues.Count >= 4)
                    {
                        break;
                    }
                }
                if (webValues.Count >= 4)
                {
                    break;
                }
            }
            if (webValues.Count > 0)
            {
                return (string.Join("；", webValues), phonetic);
            }
        }

        return (string.Empty, phonetic);
    }

    private static void ExtractYoudaoWordEntries(
        JsonElement wordElement,
        List<string> definitions,
        ref string phonetic)
    {
        IEnumerable<JsonElement> entries = wordElement.ValueKind switch
        {
            JsonValueKind.Array => wordElement.EnumerateArray(),
            JsonValueKind.Object => [wordElement],
            _ => [],
        };

        foreach (var word in entries)
        {
            if (word.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (string.IsNullOrEmpty(phonetic))
            {
                phonetic = ReadYoudaoPhonetic(word);
            }
            if (!word.TryGetProperty("trs", out var trs) || trs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var trEntry in trs.EnumerateArray())
            {
                if (trEntry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                // Direct { "pos": "n.", "tran": "..." } shape
                if (trEntry.TryGetProperty("tran", out var directTran) && directTran.ValueKind == JsonValueKind.String)
                {
                    var pos = trEntry.TryGetProperty("pos", out var posProp) && posProp.ValueKind == JsonValueKind.String
                        ? posProp.GetString()?.Trim()
                        : null;
                    var t = directTran.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                    {
                        definitions.Add(string.IsNullOrWhiteSpace(pos) ? t : $"{pos} {t}");
                    }
                    continue;
                }
                // Standard jsonapi { "tr": [ { "l": { "i": [ "..." ] } } ] } shape
                if (!trEntry.TryGetProperty("tr", out var trArray) || trArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var trItem in trArray.EnumerateArray())
                {
                    if (trItem.ValueKind != JsonValueKind.Object ||
                        !trItem.TryGetProperty("l", out var lObj) || lObj.ValueKind != JsonValueKind.Object ||
                        !lObj.TryGetProperty("i", out var iVal))
                    {
                        continue;
                    }
                    if (iVal.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<string>();
                        foreach (var part in iVal.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.String)
                            {
                                var s = part.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            }
                            else if (part.ValueKind == JsonValueKind.Object &&
                                     part.TryGetProperty("#text", out var textProp) &&
                                     textProp.ValueKind == JsonValueKind.String)
                            {
                                var s = textProp.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(s)) parts.Add(s);
                            }
                        }
                        if (parts.Count > 0)
                        {
                            definitions.Add(string.Join(" ", parts));
                        }
                    }
                    else if (iVal.ValueKind == JsonValueKind.String)
                    {
                        var s = iVal.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            definitions.Add(s);
                        }
                    }
                }
            }
        }
    }

    private static string ReadYoudaoPhonetic(JsonElement word)
    {
        foreach (var field in new[] { "usphone", "ukphone", "phone" })
        {
            if (word.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var raw = prop.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    return raw.StartsWith('/') && raw.EndsWith('/') ? raw : $"/{raw}/";
                }
            }
        }
        return string.Empty;
    }

    // ================= Bing IG / IID / ttranslatev3 Route =================

    private static async Task<TranslationResponse> TranslateBingAsync(
        string trimmed,
        string sl,
        string tl,
        string cacheKey,
        FreeEngineAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var bingHost = FreeEndpointCatalog.HostOf(FreeEndpointCatalog.BingTranslateV3Url);
        if (IsHostCoolingDown(bingHost))
        {
            if (IsHostTransportCoolingDown(bingHost))
            {
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    TransportCooldownMessage(FreeEngineProvider.Bing));
            }
            throw RateLimitedError(inCooldown: true);
        }

        if (!TryConsumeQuota())
        {
            throw new FreeTranslateException(
                FreeTranslateFailureKind.RateLimited,
                $"本机这一小时内使用内置免费引擎已达 {HourlySendBudget} 次上限，暂停自动重试以保护公共端点；可稍后重试，或在设置中配置自己的模型服务。");
        }

        var started = Stopwatch.GetTimestamp();
        Exception? lastError = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokenState = await GetOrRefreshBingTokenAsync(
                bingHost, authorization, forceRefresh: attempt > 0, cancellationToken).ConfigureAwait(false);

            var seq = Interlocked.Increment(ref _bingSequence);
            var postUrl = $"{FreeEndpointCatalog.BingTranslateV3Url}?isVertical=1&IG={Uri.EscapeDataString(tokenState.Ig)}&IID={Uri.EscapeDataString(tokenState.Iid)}.{seq}";
            using var request = new HttpRequestMessage(HttpMethod.Post, postUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["fromLang"] = MapBingLanguage(sl, isSource: true),
                    ["text"] = trimmed,
                    ["to"] = MapBingLanguage(tl, isSource: false),
                    ["token"] = tokenState.Token,
                    ["key"] = tokenState.Key,
                }),
            };
            request.Headers.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Origin", "https://cn.bing.com");
            request.Headers.TryAddWithoutValidation("Referer", FreeEndpointCatalog.BingTranslatorPageUrl);

            try
            {
                if (!authorization.TryClaimSend(out var sendRefusal))
                {
                    throw new InvalidOperationException(sendRefusal);
                }
                using var response = HttpSenderOverride is { } sender
                    ? await sender(request, cancellationToken).ConfigureAwait(false)
                    : await GetActiveHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if ((int)response.StatusCode == 429)
                {
                    MarkHostRateLimited(bingHost);
                    throw RateLimitedError(inCooldown: false);
                }

                if ((int)response.StatusCode is 401 or 403)
                {
                    InvalidateBingTokenCache(tokenState);
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unauthorized,
                        $"Bing 翻译令牌已失效（HTTP {(int)response.StatusCode}）；请稍后重试或切换其他线路。");
                    if (attempt == 0)
                    {
                        continue;
                    }
                    MarkHostTransportCooldown(bingHost);
                    throw lastError;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"Bing 翻译服务不可用（HTTP {(int)response.StatusCode}）；请稍后重试或切换其他线路。");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    InvalidateBingTokenCache(tokenState);
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        "Bing 翻译返回了网页而不是翻译结果（可能需要刷新令牌或人机验证）；请稍后重试或切换其他线路。");
                    if (attempt == 0)
                    {
                        continue;
                    }
                    throw lastError;
                }

                if (response.Content.Headers.ContentLength is { } declaredLength &&
                    declaredLength > MaxResponseBytes)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                }

                var json = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"免费翻译响应超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                }

                string translated;
                try
                {
                    (translated, _) = ParseBing(json);
                }
                catch (JsonException)
                {
                    lastError = UnparsableResponseError();
                    if (attempt == 0)
                    {
                        InvalidateBingTokenCache(tokenState);
                        continue;
                    }
                    throw lastError;
                }

                if (string.IsNullOrWhiteSpace(translated))
                {
                    InvalidateBingTokenCache(tokenState);
                    lastError = new FreeTranslateException(
                        FreeTranslateFailureKind.Unparsable,
                        "Bing 翻译未返回有效译文（令牌可能已过期），请重试或切换其他线路。");
                    if (attempt == 0)
                    {
                        continue;
                    }
                    throw lastError;
                }

                MarkHostSucceeded(bingHost);
                var elapsedMs = (ulong)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var result = new TranslationResponse(
                    new TranslationResult(
                        translated,
                        Transcription: string.Empty,
                        Explanation: string.Empty,
                        ProtectedTerms: [],
                        Warnings: [],
                        Phonetic: string.Empty),
                    new ProviderDiagnostics(
                        RequestId,
                        ProviderType.OpenAiCompatible,
                        bingHost,
                        (byte)(attempt + 1),
                        (ushort)response.StatusCode,
                        elapsedMs),
                    OverrideEngineLabel: EngineWording.BingEngineName);
                AddToCache(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                MarkHostTransportCooldown(bingHost);
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    "Bing 翻译暂时无法访问（连接、代理、DNS 或超时）；请检查本机网络后重试。");
            }
        }

        throw lastError ?? new FreeTranslateException(FreeTranslateFailureKind.Unavailable, "Bing 翻译服务不可用。");
    }

    private static async Task<BingTokenState> GetOrRefreshBingTokenAsync(
        string bingHost,
        FreeEngineAuthorization authorization,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh && TryGetValidBingToken(out var cached))
        {
            return cached;
        }

        await BingTokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryGetValidBingToken(out cached))
            {
                return cached;
            }

            if (!authorization.TryClaimSend(out var sendRefusal))
            {
                throw new InvalidOperationException(sendRefusal);
            }

            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, FreeEndpointCatalog.BingTranslatorPageUrl);
            pageRequest.Headers.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

            try
            {
                using var pageResponse = HttpSenderOverride is { } sender
                    ? await sender(pageRequest, cancellationToken).ConfigureAwait(false)
                    : await GetActiveHttpClient().SendAsync(pageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if ((int)pageResponse.StatusCode == 429)
                {
                    MarkHostRateLimited(bingHost);
                    throw RateLimitedError(inCooldown: false);
                }
                if ((int)pageResponse.StatusCode is 401 or 403)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unauthorized,
                        $"Bing 翻译令牌页拒绝访问（HTTP {(int)pageResponse.StatusCode}）。");
                }
                if (!pageResponse.IsSuccessStatusCode)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"无法获取 Bing 翻译令牌（HTTP {(int)pageResponse.StatusCode}）。");
                }

                if (pageResponse.Content.Headers.ContentLength is { } len && len > MaxResponseBytes)
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        $"Bing 翻译页面超过 {MaxResponseBytes / 1024 / 1024} MiB 上限，已中止读取。");
                }

                var html = await ReadCappedAsync(pageResponse.Content, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html) || !TryParseBingTokens(html, out var parsed))
                {
                    throw new FreeTranslateException(
                        FreeTranslateFailureKind.Unavailable,
                        "无法解析 Bing 翻译页面令牌；请稍后重试或切换其他公共线路。");
                }

                var state = parsed with
                {
                    ExpiresAtMs = MonotonicMilliseconds + (long)BingTokenTtl.TotalMilliseconds,
                };
                _bingTokenCache = state;
                return state;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                MarkHostTransportCooldown(bingHost);
                throw new FreeTranslateException(
                    FreeTranslateFailureKind.NetworkOrTimeout,
                    "Bing 翻译暂时无法访问（连接、代理、DNS 或超时）；请检查本机网络后重试。");
            }
        }
        finally
        {
            BingTokenGate.Release();
        }
    }

    private static bool TryGetValidBingToken([NotNullWhen(true)] out BingTokenState? state)
    {
        var current = Volatile.Read(ref _bingTokenCache);
        if (current is not null && current.ExpiresAtMs - MonotonicMilliseconds > 0)
        {
            state = current;
            return true;
        }
        state = null;
        return false;
    }

    private static void InvalidateBingTokenCache(BingTokenState? expected)
    {
        if (expected is null)
        {
            Volatile.Write(ref _bingTokenCache, null);
            return;
        }
        Interlocked.CompareExchange(ref _bingTokenCache, null, expected);
    }

    internal static bool TryParseBingTokens(string html, out BingTokenState state)
    {
        state = null!;
        var igMatch = BingIgRegex.Match(html);
        var abuseMatch = BingAbuseParamsRegex.Match(html);
        if (!igMatch.Success || !abuseMatch.Success)
        {
            return false;
        }

        var iidMatch = BingDataIidRegex.Match(html);
        if (!iidMatch.Success)
        {
            iidMatch = BingIidFallbackRegex.Match(html);
        }

        var ig = igMatch.Groups[1].Value;
        var iid = iidMatch.Success ? iidMatch.Groups[1].Value : "translator.5024";
        var key = abuseMatch.Groups[1].Value;
        var token = abuseMatch.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(ig) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        state = new BingTokenState(ig, iid, key, token, ExpiresAtMs: 0);
        return true;
    }

    internal static string MapBingLanguage(string lang, bool isSource)
    {
        var normalized = LanguageCatalog.Normalize(lang);
        return normalized switch
        {
            LanguageCatalog.Auto => isSource ? "auto-detect" : "zh-Hans",
            "zh-CN" => "zh-Hans",
            "zh-TW" => "zh-Hant",
            _ => normalized,
        };
    }

    internal static (string Translated, string Phonetic) ParseBing(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return (string.Empty, string.Empty);
        }

        var first = root[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !first.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array ||
            translations.GetArrayLength() == 0)
        {
            return (string.Empty, string.Empty);
        }

        var builder = new StringBuilder();
        foreach (var item in translations.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("text", out var textProp) &&
                textProp.ValueKind == JsonValueKind.String)
            {
                builder.Append(textProp.GetString());
                break;
            }
        }
        return (builder.ToString(), string.Empty);
    }

    // ================= Google & MyMemory Parsers =================

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

        return LanguageCatalog.DetectScript(text) ?? throw new FreeTranslateException(
            FreeTranslateFailureKind.Unavailable,
            "MyMemory 无法可靠判断这段混合文本的原文语言，请先选择原文语言后重试。");
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
        return new HttpRequestMessage(HttpMethod.Post, FreeEndpointCatalog.MyMemoryUrl)
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
            // The live endpoint also returns a flat string array, not only pairs.
            if (element.ValueKind == JsonValueKind.String)
            {
                builder.Append(element.GetString());
                continue;
            }
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

    internal static bool TryGetValidHealth(FreeEngineProvider provider, out FreeEngineHealth health)
    {
        if (HealthByProvider.TryGetValue(provider, out health) &&
            HealthCompletedTicks.TryGetValue(provider, out var completed) &&
            DateTime.UtcNow.Ticks - completed < HealthTtl.Ticks)
        {
            return true;
        }
        health = default;
        return false;
    }

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
