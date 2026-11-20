using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static class PotSourceAdapterTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public static async Task YandexProtocolPreservesTextAndRejectsNonTranslations()
    {
        const string source = "hello + & / \"quote\"\n中文";
        using var request = YandexPublicTranslation.BuildRequest("auto", "zh-CN", source);
        Check(request.Method == HttpMethod.Post && request.RequestUri!.Host == "translate.yandex.net", "Yandex uses form POST to the declared receiver");
        Check(!request.RequestUri!.Query.Contains("hello"), "source must not enter the URL");
        var body = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync());
        Check(body["source_lang"] == "" && body["target_lang"] == "zh", "auto/Chinese language codes must match the wire protocol");
        Check(body["text"] == source, "form encoding must preserve punctuation, newline and Unicode");
        Check(YandexPublicTranslation.ParseResponse("{\"code\":200,\"text\":[\"你好\",\"世界\"]}").Translated == "你好\n世界", "all result parts must be preserved");
        foreach (var invalid in new[] { "{}", "[]", "{\"code\":\"200\",\"text\":[\"不应展示\"]}", "{\"code\":403,\"text\":[\"不应展示\"]}", "{\"code\":200,\"text\":[42]}" })
            Check(YandexPublicTranslation.ParseResponse(invalid).Translated == "", "HTTP success or text-like error is not a successful translation");
        var rejected = false;
        try { using var unsupported = YandexPublicTranslation.BuildRequest("en", "zh-TW", "hello"); }
        catch (FreeTranslateException) { rejected = true; }
        Check(rejected, "a generic zh endpoint must not pretend to satisfy a traditional-Chinese target");
        Check(!FreeTranslateService.NeedsBatching(new string('a', 2000), FreeEngineProvider.Yandex, out _), "Yandex's POST body must not inherit Google's old GET split budget");
        Check((int)FreeEngineProvider.Custom == 5, "adding a route must preserve existing serialized enum values");
        foreach (var host in new[] { "translate.yandex.net", "translate.google.com" })
            Check(OutboundPolicy.FreeEngineDestination.Contains(host) && EngineWording.FreeEngineOnlineServiceNote.Contains(host), "every added host must be disclosed before sending");
    }

    public static async Task YandexPipelineHonorsConsentCacheAndAutoBoundary()
    {
        var oldSender = FreeTranslateService.HttpSenderOverride;
        var oldEndpoints = FreeTranslateService.EndpointsOverride;
        var oldLoader = OutboundPolicy.SettingsLoader;
        var oldLive = OutboundPolicy.LiveSettingsLoader;
        try
        {
            var settings = CoreBridge.GetSettings() with { NetworkEnabled = true, SafeDevMode = false };
            var shell = ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };
            OutboundPolicy.SettingsLoader = () => shell;
            OutboundPolicy.LiveSettingsLoader = () => settings;
            FreeTranslateService.EndpointsOverride = null;
            FreeTranslateService.ResetRateLimitStateForTest(); FreeTranslateService.ResetTransportStateForTest();
            FreeTranslateService.ResetHealthCacheForTest(); FreeTranslateService.ResetQuotaForTest();
            var sends = 0;
            var valid = true;
            FreeTranslateService.HttpSenderOverride = (request, _) =>
            {
                Check(request.RequestUri!.Host == "translate.yandex.net", "a selected Yandex request must not contact another provider");
                sends++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(valid ? "{\"code\":200,\"text\":[\"这是测试译文\"]}" : "{\"code\":403,\"text\":[\"错误内容\"]}") });
            };
            var text = "Yandex fixture " + Guid.NewGuid().ToString("N");
            var auth = new FreeEngineAuthorization(settings);
            var result = await FreeTranslateService.TranslateAsync(text, "en", "zh-CN", auth, provider: FreeEngineProvider.Yandex);
            Check(result.Result.TranslatedText == "这是测试译文" && result.EngineLabel == "Yandex" && sends == 1, "full translation pipeline must report the actual provider and valid text");
            var cached = await FreeTranslateService.TranslateAsync(text, "en", "zh-CN", auth, provider: FreeEngineProvider.Yandex);
            Check(sends == 1 && cached.ElapsedMs == 0, "an identical query must reuse cache without claiming fresh latency");
            Check(FreeTranslateService.SelectAutoProvider("A complete new sentence with several words.", "en", "zh-CN") != FreeEngineProvider.Yandex,
                "a manual success must not silently introduce a new recipient into existing Auto consent");
            valid = false;
            var rejected = false;
            try { await FreeTranslateService.TranslateAsync(text + " failure", "en", "zh-CN", auth, provider: FreeEngineProvider.Yandex); }
            catch (FreeTranslateException e) { rejected = e.Kind == FreeTranslateFailureKind.Unparsable; }
            Check(rejected && sends == 2, "a body-level failure must not be cached/displayed as a successful translation");
            shell = shell with { FreeEngineConsent = FreeEngineConsent.Denied };
            try { await FreeTranslateService.TranslateAsync(text + " denied", "en", "zh-CN", auth, provider: FreeEngineProvider.Yandex); }
            catch (InvalidOperationException) { }
            Check(sends == 2, "revocation must block a new sender submission");
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = oldSender; FreeTranslateService.EndpointsOverride = oldEndpoints;
            OutboundPolicy.SettingsLoader = oldLoader; OutboundPolicy.LiveSettingsLoader = oldLive;
            FreeTranslateService.ResetRateLimitStateForTest(); FreeTranslateService.ResetTransportStateForTest(); FreeTranslateService.ResetQuotaForTest();
        }
    }

    public static void OfficialJsonTemplatesAreExplicitKeyOnlyAndDoNotSendOnSelection()
    {
        Program.EnsureApplication();
        var deepL = CustomTranslationApiPresets.DeepLApi();
        var caiyun = CustomTranslationApiPresets.Caiyun();
        CustomTranslationApi.Validate(deepL); CustomTranslationApi.Validate(caiyun);
        using var dlBody = JsonDocument.Parse(CustomTranslationApi.BuildBody(deepL, "hello", "auto", "zh-CN", "test-fixture"));
        Check(!dlBody.RootElement.TryGetProperty("source_lang", out _) && dlBody.RootElement.GetProperty("target_lang").GetString() == "ZH", "DeepL preset must explicitly use auto-detection, not an invalid AUTO source field");
        Check(deepL.ApiKeyPrefix == "DeepL-Auth-Key " && deepL.ResponsePointer == "/translations/0/text", "DeepL official authentication and response path must be correct");
        using var cyBody = JsonDocument.Parse(CustomTranslationApi.BuildBody(caiyun, "hello", "en", "zh-CN", "test-fixture"));
        Check(cyBody.RootElement.GetProperty("trans_type").GetString() == "en2zh" && cyBody.RootElement.GetProperty("source")[0].GetString() == "hello", "Caiyun template must retain language direction and source array");
        Check(CustomTranslationApi.ParseResponse("{\"target\":[\"你好\"]}", caiyun.ResponsePointer) == "你好", "Caiyun array response must be extractable");
        var oldSender = FreeTranslateService.HttpSenderOverride;
        var sends = 0;
        try
        {
            FreeTranslateService.HttpSenderOverride = (_, _) => { sends++; throw new InvalidOperationException("selection must not send"); };
            var original = CustomTranslationApi.Load();
            var editor = new CustomTranslationApiEditor();
            ((PasswordBox)editor.FindName("KeyInput")).Password = "test-fixture";
            ((Button)editor.FindName("DeepLApiTemplateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((PasswordBox)editor.FindName("KeyInput")).Password == "", "a different endpoint must not inherit an unsaved credential");
            Check(((TextBox)editor.FindName("EndpointInput")).Text == deepL.Endpoint, "DeepL button must populate an editable endpoint");
            ((Button)editor.FindName("CaiyunTemplateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBox)editor.FindName("HeaderInput")).Text == "x-authorization", "Caiyun template must populate its actual auth header");
            Check(sends == 0 && CustomTranslationApi.Load() == original, "picking a template is neither a request nor a save");
        }
        finally { FreeTranslateService.HttpSenderOverride = oldSender; }
    }
}
