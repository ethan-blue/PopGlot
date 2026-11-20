namespace PopGlot.Windows.Services;

/// <summary>Protocol-only JSON examples. No shared/public credential is bundled.</summary>
internal static class CustomTranslationApiPresets
{
    // Omitting source_lang is the documented auto-detection mode; the UI names it explicitly.
    internal static CustomTranslationApiOptions DeepLApi() => new(
        Name: "DeepL API（自动检测原文）",
        Endpoint: "https://api-free.deepl.com/v2/translate",
        RequestTemplate: "{\"text\":[\"{text}\"],\"target_lang\":\"{targetUpper}\"}",
        ResponsePointer: "/translations/0/text",
        ApiKeyHeader: "Authorization",
        ApiKeyPrefix: "DeepL-Auth-Key ");

    internal static CustomTranslationApiOptions Caiyun() => new(
        Name: "彩云小译 API",
        Endpoint: "https://api.interpreter.caiyunai.com/v1/translator",
        RequestTemplate: "{\"source\":[\"{text}\"],\"trans_type\":\"{sourceBase}2{targetBase}\",\"request_id\":\"popglot\",\"detect\":true}",
        ResponsePointer: "/target/0",
        ApiKeyHeader: "x-authorization",
        ApiKeyPrefix: "token ");
}
