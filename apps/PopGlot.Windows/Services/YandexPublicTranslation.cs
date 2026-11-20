using System.Net.Http;
using System.Text.Json;

namespace PopGlot.Windows.Services;

/// <summary>
/// Independent HTTP protocol adapter for Yandex's public translation endpoint.
/// The protocol's availability was cross-checked against Pot; no Pot code is embedded.
/// </summary>
internal static class YandexPublicTranslation
{
    internal static string LanguageCode(string language, bool source)
    {
        var normalized = LanguageCatalog.Normalize(language);
        if (normalized == LanguageCatalog.Auto)
            return source ? string.Empty : "zh";
        if (normalized == "zh-TW" && !source)
            throw new FreeTranslateException(FreeTranslateFailureKind.Unavailable,
                "Yandex 当前公共接口不区分中文简繁；需要繁体中文时请选择 Google 或其他支持繁体的线路。");
        return normalized switch
        {
            "zh-CN" or "zh-TW" => "zh",
            "nb-NO" or "nn-NO" => "no",
            _ => normalized.Split('-')[0],
        };
    }

    internal static HttpRequestMessage BuildRequest(string source, string target, string text)
    {
        var from = LanguageCode(source, source: true);
        var to = LanguageCode(target, source: false);
        var requestId = Guid.NewGuid().ToString("N") + "-0-0";
        return new HttpRequestMessage(HttpMethod.Post,
            FreeEndpointCatalog.YandexTranslateUrl + "?srv=android&id=" + requestId)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["source_lang"] = from,
                ["target_lang"] = to,
                ["text"] = text,
            }),
        };
    }

    internal static (string Translated, string Phonetic) ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var status) || status != 200 ||
            !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.Array)
            return (string.Empty, string.Empty);
        var parts = new List<string>();
        foreach (var part in text.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.String) return (string.Empty, string.Empty);
            parts.Add(part.GetString() ?? string.Empty);
        }
        return (string.Join("\n", parts), string.Empty);
    }
}
