namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void DetectScriptClassifiesSupportedScripts()
    {
        Equal("zh-CN", LanguageCatalog.DetectScript("中文中文中文 hello"));
        Equal("zh-CN", LanguageCatalog.DetectScript("中文内容"));
        Equal("en", LanguageCatalog.DetectScript("Hello world!"));
        Equal("ja", LanguageCatalog.DetectScript("漢字とかな"));
        Equal("ko", LanguageCatalog.DetectScript("안녕하세요"));
        Equal("ru", LanguageCatalog.DetectScript("Привет мир"));
        Equal("en", LanguageCatalog.DetectScript("école déjà vu"));
        Equal("zh-CN", LanguageCatalog.DetectScript("```text\nEnglish in code\n```\n中文正文"));
        Equal("en", LanguageCatalog.DetectScript("Hello https://example.com/path?q=world"));
        Equal<string?>(null, LanguageCatalog.DetectScript(string.Empty));
        Equal<string?>(null, LanguageCatalog.DetectScript("12345"));
        Equal<string?>(null, LanguageCatalog.DetectScript("```csharp\nvar value = 42;\n```"));
        Equal<string?>(null, LanguageCatalog.DetectScript("你好 hello"));
    }

    private static void ResolveAutoTargetUsesSourceAndFallback()
    {
        Equal("en", LanguageCatalog.ResolveAutoTarget("你好世界", "zh-CN"));
        Equal("zh-CN", LanguageCatalog.ResolveAutoTarget("Hello world", "en"));
        Equal("zh-CN", LanguageCatalog.ResolveAutoTarget("こんにちは", "fr"));
        Equal("fr", LanguageCatalog.ResolveAutoTarget("12345", "fr"));
    }
}
