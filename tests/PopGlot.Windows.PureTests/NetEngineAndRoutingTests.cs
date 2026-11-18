using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using PopGlot.Windows;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static async Task<TException> CaptureAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException ex)
        {
            return ex;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static async Task RunNetEngineAndRoutingTestsAsync(string root)
    {
        ShellSettingsBackwardsCompatibleDefaultsAndNormalization(root);
        await FreeEngineTransportCooldownSkipsDeadHostAndFailsFastAsync();
        await FreeEngineQuotaSkipsCoolingAndOverBudgetAndResetsAfterOneHourAsync();
        await YoudaoParsingValidationAndDegradationAsync();
        await BingTokenScrapeCacheAndRefreshOnAuthFailureAsync();
        await AutoProviderSelectionStickinessCooldownAndPrivacyAsync();
        await WarmupFreeRoutesRespectsPrivacyGatesAndSendsZeroPayloadAsync();
        ProxyHttpClientFactoryAndDomesticBypassRules();
        TranslationElapsedTextAndConnectionTestRunnerFormatting();
        await ModelCatalogDiscoveryDetectsMockLocalRuntimesAndEnvWithoutLeakingSecretsAsync();
    }

    private static void ShellSettingsBackwardsCompatibleDefaultsAndNormalization(string root)
    {
        var path = Path.Combine(root, "legacy-shell-settings.json");
        File.WriteAllText(path, """
            {
              "SchemaVersion": 3,
              "SelectionHotkey": "Ctrl+Alt+W",
              "ScreenshotHotkey": "Ctrl+Alt+Space",
              "CloseHotkey": "Ctrl+Alt+X",
              "HistoryEnabled": true,
              "Theme": "Dark"
            }
            """, Encoding.UTF8);

        var loaded = ShellSettingsStore.Load(path);
        Equal(true, loaded.LiveTranslateEnabled, "live translate defaults to enabled");
        Equal(500, loaded.LiveTranslateDelayMs, "live translate delay defaults to 500ms");
        Equal(LiveTranslateScope.FreeEngineOnly, loaded.LiveTranslateScope, "live translate scope defaults to FreeEngineOnly");
        Equal(QuickSearchWindowMode.Compact, loaded.QuickSearchWindowMode, "quicksearch window mode defaults to Compact");
        Equal(660.0, loaded.QuickSearchExpandedWidth, "expanded width defaults to 660");
        Equal(520.0, loaded.QuickSearchExpandedHeight, "expanded height defaults to 520");
        Equal(NetworkProxyMode.System, loaded.NetworkProxyMode, "proxy mode defaults to System");
        True(loaded.CustomProxyUrl is null, "custom proxy url defaults to null");
        Equal(true, loaded.ProxyBypassLocal, "bypass local defaults to true");
        Equal(true, loaded.ProxyBypassDomesticRoutes, "bypass domestic defaults to true");
        Equal(FreeEngineProvider.Google, loaded.FreeEngineProvider, "legacy missing provider defaults to Google");

        // Out-of-range values normalize cleanly on save & reload
        var dirty = loaded with
        {
            LiveTranslateDelayMs = 420,
            LiveTranslateScope = LiveTranslateScope.AllEngines,
            QuickSearchWindowMode = QuickSearchWindowMode.Expanded,
            QuickSearchExpandedWidth = 200,
            QuickSearchExpandedHeight = 9999,
            NetworkProxyMode = NetworkProxyMode.Custom,
            CustomProxyUrl = "  http://127.0.0.1:7897  ",
            ProxyBypassLocal = true,
            ProxyBypassDomesticRoutes = false,
            FreeEngineProvider = FreeEngineProvider.Bing,
        };
        ShellSettingsStore.Save(dirty, path);
        ShellSettingsStore.InvalidateCache();
        var reloaded = ShellSettingsStore.Load(path);
        Equal(500, reloaded.LiveTranslateDelayMs, "420 snaps to 500ms");
        Equal(LiveTranslateScope.AllEngines, reloaded.LiveTranslateScope, "LiveTranslateScope persists");
        Equal(QuickSearchWindowMode.Expanded, reloaded.QuickSearchWindowMode, "QuickSearchWindowMode persists");
        Equal(480.0, reloaded.QuickSearchExpandedWidth, "expanded width clamps to min 480");
        Equal(1000.0, reloaded.QuickSearchExpandedHeight, "expanded height clamps to max 1000");
        Equal(NetworkProxyMode.Custom, reloaded.NetworkProxyMode, "NetworkProxyMode persists");
        Equal("http://127.0.0.1:7897", reloaded.CustomProxyUrl, "CustomProxyUrl trims whitespace");
        Equal(false, reloaded.ProxyBypassDomesticRoutes, "ProxyBypassDomesticRoutes persists");
        Equal(FreeEngineProvider.Bing, reloaded.FreeEngineProvider, "new FreeEngineProvider values persist");
    }

    private static async Task FreeEngineTransportCooldownSkipsDeadHostAndFailsFastAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalEndpoints = FreeTranslateService.EndpointsOverride;
        var originalClock = FreeTranslateService.MonotonicClockOverrideForTest;
        var originalLoader = OutboundPolicy.SettingsLoader;
        FreeTranslateService.ResetRateLimitStateForTest();
        FreeTranslateService.ResetTransportStateForTest();
        FreeTranslateService.ResetQuotaForTest();

        long fakeNow = 10_000;
        FreeTranslateService.MonotonicClockOverrideForTest = () => Interlocked.Read(ref fakeNow);
        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };

        var hostASends = 0;
        var hostBSends = 0;
        var failB = false;

        FreeTranslateService.EndpointsOverride =
        [
            new FreeTranslateService.FreeEndpoint(
                "dead-a.test",
                (_, _, q) => $"https://dead-a.test/t?q={Uri.EscapeDataString(q)}",
                _ => ("A-译文", "")),
            new FreeTranslateService.FreeEndpoint(
                "live-b.test",
                (_, _, q) => $"https://live-b.test/t?q={Uri.EscapeDataString(q)}",
                _ => ("B-译文", "")),
        ];

        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            if (host == "dead-a.test")
            {
                Interlocked.Increment(ref hostASends);
                throw new HttpRequestException("simulated connect refused");
            }
            Interlocked.Increment(ref hostBSends);
            if (failB)
            {
                throw new TaskCanceledException("simulated timeout");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        };

        var auth = new FreeEngineAuthorization(DemoSettings());
        try
        {
            // 1. A fails with HttpRequestException -> A enters 90s transport cooldown, B succeeds.
            var first = await FreeTranslateService.TranslateAsync($"tc-1-{Guid.NewGuid():N}", "en", "zh-CN", auth);
            Equal("B-译文", first.Result.TranslatedText, "B serves after A transport failure");
            Equal(1, Volatile.Read(ref hostASends), "A tried once");
            Equal(1, Volatile.Read(ref hostBSends), "B tried once");

            // 2. Second request skips dead A immediately and sticks to healthy B.
            Interlocked.Add(ref fakeNow, 5_000);
            var second = await FreeTranslateService.TranslateAsync($"tc-2-{Guid.NewGuid():N}", "en", "zh-CN", auth);
            Equal("B-译文", second.Result.TranslatedText, "B still serves");
            Equal(1, Volatile.Read(ref hostASends), "dead A must be skipped during 90s cooldown");
            Equal(2, Volatile.Read(ref hostBSends), "healthy B handles second request");

            // 3. B also fails with TaskCanceledException -> both hosts now cooling down.
            failB = true;
            var secondFail = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync($"tc-3-{Guid.NewGuid():N}", "en", "zh-CN", auth));
            Equal(FreeTranslateFailureKind.NetworkOrTimeout, secondFail.Kind, "transport failure kind preserved");

            // 4. Subsequent request within 90s fails fast (<= 50ms, zero sends) with helpful guidance.
            var sw = Stopwatch.StartNew();
            var fastFail = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync($"tc-4-{Guid.NewGuid():N}", "en", "zh-CN", auth));
            sw.Stop();
            Equal(FreeTranslateFailureKind.NetworkOrTimeout, fastFail.Kind, "all-cooling reports NetworkOrTimeout");
            True(fastFail.Message.Contains("Google", StringComparison.Ordinal) &&
                 fastFail.Message.Contains("Bing", StringComparison.Ordinal),
                $"fast fail must include actionable provider switch hint, got: {fastFail.Message}");
            True(sw.ElapsedMilliseconds < 200, $"all-cooling fail-fast must not wait on network, took {sw.ElapsedMilliseconds}ms");
            Equal(1, Volatile.Read(ref hostASends), "zero sends to A while cooling");
            Equal(3, Volatile.Read(ref hostBSends), "zero sends to B while cooling");

            // 5. After 90s cooldown expires, B is retried again (and B goes first if stickiness or A/B recover).
            failB = false;
            Interlocked.Add(ref fakeNow, 91_000);
            var recovered = await FreeTranslateService.TranslateAsync($"tc-5-{Guid.NewGuid():N}", "en", "zh-CN", auth);
            Equal("B-译文", recovered.Result.TranslatedText, "recovered host serves after 90s cooldown");
            Equal(4, Volatile.Read(ref hostBSends), "B was attempted after cooldown expiry");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            FreeTranslateService.EndpointsOverride = originalEndpoints;
            FreeTranslateService.MonotonicClockOverrideForTest = originalClock;
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.ResetTransportStateForTest();
            FreeTranslateService.ResetQuotaForTest();
        }
    }

    private static async Task FreeEngineQuotaSkipsCoolingAndOverBudgetAndResetsAfterOneHourAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalEndpoints = FreeTranslateService.EndpointsOverride;
        var originalClock = FreeTranslateService.MonotonicClockOverrideForTest;
        var originalLoader = OutboundPolicy.SettingsLoader;
        FreeTranslateService.ResetRateLimitStateForTest();
        FreeTranslateService.ResetTransportStateForTest();
        FreeTranslateService.ResetQuotaForTest();

        long fakeNow = 1_000_000;
        FreeTranslateService.MonotonicClockOverrideForTest = () => Interlocked.Read(ref fakeNow);
        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };

        FreeTranslateService.EndpointsOverride =
        [
            new FreeTranslateService.FreeEndpoint(
                "cool-a.test",
                (_, _, q) => $"https://cool-a.test/t?q={Uri.EscapeDataString(q)}",
                _ => ("A", "")),
            new FreeTranslateService.FreeEndpoint(
                "ok-b.test",
                (_, _, q) => $"https://ok-b.test/t?q={Uri.EscapeDataString(q)}",
                _ => ("B", "")),
        ];

        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            if (request.RequestUri?.Host == "cool-a.test")
            {
                throw new HttpRequestException("down");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        };

        var auth = new FreeEngineAuthorization(DemoSettings());
        try
        {
            var initial = FreeTranslateService.QuotaRemaining;
            Equal(FreeTranslateService.HourlySendBudget, initial, "fresh quota starts at full budget");

            // Request 1: A fails (1 quota), B succeeds (1 quota) => 2 consumed.
            await FreeTranslateService.TranslateAsync($"q-1-{Guid.NewGuid():N}", "en", "zh-CN", auth);
            Equal(initial - 2, FreeTranslateService.QuotaRemaining, "attempted endpoints consume quota");

            // Request 2: A is cooling down (skipped -> 0 quota!), B succeeds (1 quota) => only 1 consumed!
            await FreeTranslateService.TranslateAsync($"q-2-{Guid.NewGuid():N}", "en", "zh-CN", auth);
            Equal(initial - 3, FreeTranslateService.QuotaRemaining, "cooling host must NOT consume quota");

            // Request 3: Over-budget URL is skipped locally -> 0 quota consumed!
            var longText = new string('x', 2200);
            await ThrowsAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync(longText, "en", "zh-CN", auth));
            Equal(initial - 3, FreeTranslateService.QuotaRemaining, "over-budget URL must NOT consume quota");

            // Advance monotonic clock >= 1 hour -> QuotaRemaining immediately reflects full budget BEFORE any new send!
            Interlocked.Add(ref fakeNow, 60L * 60 * 1000 + 1);
            Equal(FreeTranslateService.HourlySendBudget, FreeTranslateService.QuotaRemaining,
                "QuotaRemaining must reset to full HourlySendBudget after 1h window expires");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            FreeTranslateService.EndpointsOverride = originalEndpoints;
            FreeTranslateService.MonotonicClockOverrideForTest = originalClock;
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.ResetTransportStateForTest();
            FreeTranslateService.ResetQuotaForTest();
        }
    }

    private static async Task YoudaoParsingValidationAndDegradationAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalLoader = OutboundPolicy.SettingsLoader;
        FreeTranslateService.ResetRateLimitStateForTest();
        FreeTranslateService.ResetTransportStateForTest();
        FreeTranslateService.ResetQuotaForTest();

        long sends = 0;
        string? lastUrl = null;
        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            Interlocked.Increment(ref sends);
            lastUrl = request.RequestUri?.ToString();
            const string youdaoJson = """
                {
                  "ec": {
                    "word": [
                      {
                        "usphone": "həˈloʊ",
                        "trs": [
                          { "tr": [ { "l": { "i": ["int. 你好；喂"] } } ] },
                          { "tr": [ { "l": { "i": ["n. 招呼，问候"] } } ] }
                        ]
                      }
                    ]
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(youdaoJson, Encoding.UTF8, "application/json"),
            });
        };
        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with
        {
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Youdao,
        };
        var auth = new FreeEngineAuthorization(DemoSettings());

        try
        {
            var dictResult = await FreeTranslateService.TranslateAsync(
                $"hello-{Guid.NewGuid():N}"[..5], "en", "zh-CN", auth, provider: FreeEngineProvider.Youdao);
            True(dictResult.Result.TranslatedText.Contains("你好", StringComparison.Ordinal), "ec.word.trs parsed");
            Equal("/həˈloʊ/", dictResult.Result.Phonetic, "usphone parsed");
            Equal("有道词典（词句最佳）", dictResult.EngineLabel, "Youdao label clearly identifies dictionary focus");
            True(lastUrl is not null && lastUrl.Contains("dict.youdao.com/jsonapi", StringComparison.Ordinal),
                "Youdao endpoint hit");
            Equal(1L, Interlocked.Read(ref sends), "one request sent");

            // Direct parser tests for fanyi.tran and web_trans fallback
            var fanyiParsed = FreeTranslateService.ParseYoudao("""{"fanyi":{"input":"good morning","tran":"早上好"}}""");
            Equal("早上好", fanyiParsed.Translated, "fanyi.tran fallback parsed");

            var webParsed = FreeTranslateService.ParseYoudao("""
                {"web_trans":{"web-translation":[{"trans":[{"value":"热更新"},{"value":"热重载"}]}]}}
                """);
            Equal("热更新；热重载", webParsed.Translated, "web_trans fallback parsed");

            // Validation checks: non-EN/ZH target, >120 chars, multi-sentence -> 0 sends!
            var nonEnZh = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync("hello", "en", "ja", auth, provider: FreeEngineProvider.Youdao));
            True(nonEnZh.Message.Contains("有道词典仅支持中英单词与短语", StringComparison.Ordinal),
                "non-EN/ZH target rejected clearly");

            var over120 = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync(new string('a', 125), "en", "zh-CN", auth, provider: FreeEngineProvider.Youdao));
            Equal(FreeTranslateFailureKind.LongContent, over120.Kind, ">120 chars rejected as LongContent");

            var multiSentence = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync("First sentence. Second sentence.", "en", "zh-CN", auth, provider: FreeEngineProvider.Youdao));
            Equal(FreeTranslateFailureKind.LongContent, multiSentence.Kind, "multi-sentence rejected locally");

            Equal(1L, Interlocked.Read(ref sends), "invalid Youdao inputs must send nothing");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.ResetTransportStateForTest();
        }
    }

    private static async Task BingTokenScrapeCacheAndRefreshOnAuthFailureAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalLoader = OutboundPolicy.SettingsLoader;
        FreeTranslateService.ResetRateLimitStateForTest();
        FreeTranslateService.ResetTransportStateForTest();
        FreeTranslateService.ResetQuotaForTest();

        var pageGets = 0;
        var apiPosts = 0;
        var return401OnNextPost = false;
        var currentToken = "tok-v1";

        FreeTranslateService.HttpSenderOverride = async (request, _) =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("cn.bing.com/translator", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref pageGets);
                currentToken = $"tok-v{n}";
                var html = $"""
                    <html><script>
                    var IG:"IG-KEY-{n}",IID:"translator.5024";
                    var params_AbusePreventionHelper = [1710000000{n},"{currentToken}",3600000];
                    </script></html>
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                };
            }

            Interlocked.Increment(ref apiPosts);
            var body = request.Content is not null ? await request.Content.ReadAsStringAsync() : string.Empty;
            if (return401OnNextPost)
            {
                return401OnNextPost = false;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            True(body.Contains($"token={currentToken}", StringComparison.Ordinal),
                $"Bing POST must send latest scraped token {currentToken}, got: {body}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"translations":[{"text":"必应译文","to":"zh-Hans"}]}]""",
                    Encoding.UTF8, "application/json"),
            };
        };

        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with
        {
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Bing,
        };
        var auth = new FreeEngineAuthorization(DemoSettings());

        try
        {
            // 1. First translation scrapes page + posts
            var r1 = await FreeTranslateService.TranslateAsync($"bing-1-{Guid.NewGuid():N}", "en", "zh-CN", auth, provider: FreeEngineProvider.Bing);
            Equal("必应译文", r1.Result.TranslatedText, "Bing translation parsed");
            Equal("Bing", r1.EngineLabel, "Bing engine label");
            Equal(1, Volatile.Read(ref pageGets), "first call scrapes token page once");
            Equal(1, Volatile.Read(ref apiPosts), "first call posts once");

            // 2. Second translation reuses cached token (0 new GETs!)
            var r2 = await FreeTranslateService.TranslateAsync($"bing-2-{Guid.NewGuid():N}", "en", "zh-CN", auth, provider: FreeEngineProvider.Bing);
            Equal("必应译文", r2.Result.TranslatedText, "second Bing call succeeds");
            Equal(1, Volatile.Read(ref pageGets), "cached IG/IID/token reused without re-scraping");
            Equal(2, Volatile.Read(ref apiPosts), "second POST executed");

            // 3. 401 invalidates cache, refreshes token ONCE, and retries POST -> succeeds!
            return401OnNextPost = true;
            var r3 = await FreeTranslateService.TranslateAsync($"bing-3-{Guid.NewGuid():N}", "en", "zh-CN", auth, provider: FreeEngineProvider.Bing);
            Equal("必应译文", r3.Result.TranslatedText, "401 triggers single refresh and succeeds");
            Equal(2, Volatile.Read(ref pageGets), "token refreshed once on 401");
            Equal(4, Volatile.Read(ref apiPosts), "POST retried once with fresh token");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.ResetTransportStateForTest();
        }
    }

    private static async Task AutoProviderSelectionStickinessCooldownAndPrivacyAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalLoader = OutboundPolicy.SettingsLoader;
        FreeTranslateService.ResetRateLimitStateForTest();
        FreeTranslateService.ResetTransportStateForTest();
        FreeTranslateService.ResetQuotaForTest();

        // Privacy disclosure check: all 4 providers explicitly disclosed
        foreach (var host in new[] { "translate.googleapis.com", "cn.bing.com", "dict.youdao.com", "api.mymemory.translated.net" })
        {
            True(OutboundPolicy.FreeEngineDestination.Contains(host, StringComparison.Ordinal),
                $"OutboundPolicy.FreeEngineDestination must disclose {host}");
            True(EngineWording.FreeEngineOnlineServiceNote.Contains(host, StringComparison.Ordinal),
                $"EngineWording.FreeEngineOnlineServiceNote must disclose {host}");
        }

        var contactedHosts = new List<string>();
        var failBing = false;

        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            contactedHosts.Add(host);
            if (host == "cn.bing.com")
            {
                if (failBing)
                {
                    throw new HttpRequestException("bing blocked");
                }
                if (request.RequestUri!.AbsolutePath.Contains("translator", StringComparison.Ordinal) &&
                    !request.RequestUri.AbsolutePath.Contains("ttranslatev3", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "var IG:\"AUTOIG\",IID:\"translator.5024\";var params_AbusePreventionHelper=[12345,\"autotok\",3600];",
                            Encoding.UTF8, "text/html"),
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"translations":[{"text":"自动-Bing"}]}]""", Encoding.UTF8, "application/json"),
                });
            }
            if (host == "dict.youdao.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"fanyi":{"tran":"自动-有道"}}""", Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[[["自动-Google","src","en",""]]]""", Encoding.UTF8, "application/json"),
            });
        };

        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with
        {
            FreeEngineConsent = FreeEngineConsent.Allowed,
            FreeEngineProvider = FreeEngineProvider.Auto,
        };
        var auth = new FreeEngineAuthorization(DemoSettings());

        try
        {
            // 1. Short word selects Youdao
            Equal(FreeEngineProvider.Youdao, FreeTranslateService.SelectAutoProvider("apple", "en", "zh-CN"),
                "short EN->ZH word selects Youdao");
            // 2. Long sentence selects Bing
            Equal(FreeEngineProvider.Bing,
                FreeTranslateService.SelectAutoProvider("This is a complete sentence that needs full translation.", "en", "zh-CN"),
                "sentence selects Bing when untried");

            // 3. When Bing fails on transport, Auto sends ONLY to Bing (never double-sends to Google!)
            //    and cools Bing down so the NEXT request picks Google!
            failBing = true;
            contactedHosts.Clear();
            var failed = await CaptureAsync<FreeTranslateException>(() =>
                FreeTranslateService.TranslateAsync(
                    $"This is a complete sentence {Guid.NewGuid():N}.", "en", "zh-CN", auth, provider: FreeEngineProvider.Auto));
            Equal(FreeTranslateFailureKind.NetworkOrTimeout, failed.Kind);
            True(contactedHosts.All(h => h == "cn.bing.com"),
                "Auto must NEVER silently re-send the same failed text to another provider");

            // Next sentence request skips cooled-down Bing and picks Google!
            contactedHosts.Clear();
            var next = await FreeTranslateService.TranslateAsync(
                $"Another complete sentence {Guid.NewGuid():N}.", "en", "zh-CN", auth, provider: FreeEngineProvider.Auto);
            Equal("自动-Google", next.Result.TranslatedText, "next request switches to healthy Google");
            True(next.EngineLabel.Contains("Google", StringComparison.Ordinal), $"label shows chosen route: {next.EngineLabel}");
            True(contactedHosts.All(h => h == "translate.googleapis.com"),
                "next request contacts only the chosen healthy provider");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.ResetRateLimitStateForTest();
            FreeTranslateService.ResetTransportStateForTest();
        }
    }

    private static async Task WarmupFreeRoutesRespectsPrivacyGatesAndSendsZeroPayloadAsync()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var requests = new List<HttpRequestMessage>();
        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        };

        try
        {
            var offline = DemoSettings() with { SafeDevMode = true, NetworkEnabled = true };
            var netOff = DemoSettings() with { SafeDevMode = false, NetworkEnabled = false };
            var online = DemoSettings() with { SafeDevMode = false, NetworkEnabled = true };

            await FreeTranslateService.WarmupFreeRoutesAsync(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied, FreeEngineProvider = FreeEngineProvider.Bing },
                providerSettingsOverride: online);
            await FreeTranslateService.WarmupFreeRoutesAsync(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Unset, FreeEngineProvider = FreeEngineProvider.Bing },
                providerSettingsOverride: online);
            await FreeTranslateService.WarmupFreeRoutesAsync(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed, FreeEngineProvider = FreeEngineProvider.Bing },
                providerSettingsOverride: offline);
            await FreeTranslateService.WarmupFreeRoutesAsync(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed, FreeEngineProvider = FreeEngineProvider.Bing },
                providerSettingsOverride: netOff);

            Equal(0, requests.Count, "warmup must not send anything when gated by consent/offline/network");

            await FreeTranslateService.WarmupFreeRoutesAsync(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed, FreeEngineProvider = FreeEngineProvider.Bing },
                providerSettingsOverride: online);
            Equal(1, requests.Count, "allowed warmup sends one zero-payload preconnect");
            Equal(HttpMethod.Head, requests[0].Method, "warmup uses HEAD");
            Equal("/", requests[0].RequestUri!.AbsolutePath, "warmup hits root path");
            Equal(string.Empty, requests[0].RequestUri!.Query, "warmup carries zero query payload");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
        }
    }

    private static void ProxyHttpClientFactoryAndDomesticBypassRules()
    {
        var direct = new ProxyConfig(NetworkProxyMode.Direct, "http://127.0.0.1:7897", BypassLocal: true, BypassDomesticRoutes: true);
        using var directHandler = ProxyHttpClientFactory.CreateHandler(direct, TimeSpan.FromSeconds(2.5));
        Equal(false, directHandler.UseProxy, "Direct mode disables proxy");

        var custom = new ProxyConfig(NetworkProxyMode.Custom, "socks5://127.0.0.1:7897", BypassLocal: true, BypassDomesticRoutes: true);
        using var customHandler = ProxyHttpClientFactory.CreateHandler(custom, TimeSpan.FromSeconds(2.5));
        Equal(true, customHandler.UseProxy, "Custom mode enables proxy");
        True(customHandler.Proxy is not null, "Custom mode installs proxy");
        True(customHandler.Proxy!.IsBypassed(new Uri("http://localhost:11434/v1/models")), "localhost bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("http://192.168.1.50:8080/v1")), "LAN IP bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("https://dict.youdao.com/jsonapi?q=hi")), "Youdao bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("https://cn.bing.com/ttranslatev3")), "Bing CN bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("https://api.deepseek.com/v1/chat/completions")), "DeepSeek bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("https://open.bigmodel.cn/api/paas/v4/chat/completions")), "Zhipu bypassed");
        True(customHandler.Proxy.IsBypassed(new Uri("https://api.siliconflow.cn/v1/chat/completions")), "SiliconFlow bypassed");
        Equal(false, customHandler.Proxy.IsBypassed(new Uri("https://translate.googleapis.com/translate_a/single")),
            "Google uses custom proxy");
        Equal(false, customHandler.Proxy.IsBypassed(new Uri("https://api.openai.com/v1/chat/completions")),
            "OpenAI uses custom proxy");

        var noDomesticBypass = custom with { BypassDomesticRoutes = false };
        using var noBypassHandler = ProxyHttpClientFactory.CreateHandler(noDomesticBypass, TimeSpan.FromSeconds(2.5));
        Equal(false, noBypassHandler.Proxy!.IsBypassed(new Uri("https://dict.youdao.com/jsonapi?q=hi")),
            "disabling domestic bypass routes Youdao through custom proxy");
    }

    private static void TranslationElapsedTextAndConnectionTestRunnerFormatting()
    {
        Equal("180 ms", TranslationElapsedText.ForTiming(new TranslationSessionTiming(TotalElapsedMs: 180)));
        Equal("1.2s", TranslationElapsedText.ForTiming(new TranslationSessionTiming(TotalElapsedMs: 1200)));
        Equal("1.4s · 首字 0.5s", TranslationElapsedText.ForTiming(new TranslationSessionTiming(TotalElapsedMs: 1400, FirstDeltaMs: 500)));
        Equal("有道词典 · 180 ms", TranslationElapsedText.FormatRouteLatency("有道词典", 180, new TranslationSessionTiming(TotalElapsedMs: 180)));
        Equal("Google · 1.2s", TranslationElapsedText.FormatRouteLatency("Google", 1200, null));

        var okStatus = ConnectionTestRunner.FormatCompletionStatus(VerificationApplyResult.Applied, "ok", 240, "DeepSeek");
        Equal("DeepSeek · 连接正常（240 ms）", okStatus);
    }

    private static async Task ModelCatalogDiscoveryDetectsMockLocalRuntimesAndEnvWithoutLeakingSecretsAsync()
    {
        const string baitSecret = "sk-bait-env-secret-998877";
        using var handler = new MockLocalDiscoveryHandler();
        var discovery = await ModelCatalogService.DiscoverLocalAndFreeSourcesAsync(
            testHandler: handler,
            envReader: name => name == "SILICONFLOW_API_KEY" ? baitSecret : null,
            ompDbPath: Path.Combine(Path.GetTempPath(), $"missing-omp-{Guid.NewGuid():N}.db"));

        True(discovery.CuratedPresets.Count >= 6, "curated presets cover SiliconFlow/Groq/OpenRouter/Zhipu/Ollama/LM Studio");
        var ollama = discovery.LocalRuntimes.First(r => r.Id == "ollama");
        Equal(true, ollama.IsReachable, "mock Ollama detected");
        Equal(2, ollama.DiscoveredModels.Count, "Ollama models parsed");
        var lmStudio = discovery.LocalRuntimes.First(r => r.Id == "lm-studio");
        Equal(false, lmStudio.IsReachable, "refused LM Studio marked unreachable");

        var sfEnv = discovery.EnvConfiguredSources.FirstOrDefault(s => s.Id == "siliconflow");
        True(sfEnv is not null && sfEnv.HasEnvKey, "env var presence detected");
        True(!sfEnv!.StatusSummary.Contains(baitSecret, StringComparison.Ordinal),
            "discovery metadata must NEVER leak raw secret value");
        Equal(baitSecret, ModelCatalogService.TryReadEnvApiKeyForImport("siliconflow",
            name => name == "SILICONFLOW_API_KEY" ? baitSecret : null),
            "explicit import helper reads key on demand");
    }

    private sealed class MockLocalDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Port == 11434)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"qwen2.5:7b"},{"id":"llama3.2:3b"}]}""",
                        Encoding.UTF8, "application/json"),
                });
            }
            throw new HttpRequestException("Connection refused");
        }
    }
}
