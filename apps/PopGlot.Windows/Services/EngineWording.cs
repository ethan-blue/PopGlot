namespace PopGlot.Windows.Services;

/// <summary>Short route names, kept distinct from the public-translation category.</summary>
internal static class EngineWording
{
    public const string FreeEngineName = "Google";
    public const string AlternateFreeEngineName = "MyMemory";
    public const string YoudaoEngineName = "有道词典";
    public const string YoudaoDisplayName = "有道词典（词句最佳）";
    public const string BingEngineName = "Bing";
    public const string AutoEngineName = "自动优选";
    public const string FreePublicTranslationName = "公共翻译";

    /// <summary>离线总开关的统一名称。</summary>
    public const string SafeOfflineModeName = "安全离线模式";

    /// <summary>本机文字识别步骤名称（只描述识别，不描述翻译）。</summary>
    public const string LocalOcrStepName = "本地 OCR";

    /// <summary>本地运行时（Ollama / LM Studio）线路名称。</summary>
    public const string LocalModelName = "本地模型";

    /// <summary>隐私语境中对免费引擎的诚实定性：它是联网公共服务。</summary>
    public const string FreeEngineOnlineServiceNote =
        "联网公共翻译，每次只把待翻译文本发给你选中的那一条（选择「自动优选」时也仅发给当次优选的单条线路）：Google（translate.google.com / translate.googleapis.com / clients5.google.com）、Bing（cn.bing.com）、有道词典（dict.youdao.com）、MyMemory（api.mymemory.translated.net）或手动选择的 Yandex（translate.yandex.net，未加入自动优选）。不发送截图或历史；选择自定义翻译 API 时仅发给你配置的地址，可能携带你为该地址配置的密钥。";

    public static string NameFor(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.Yandex => "Yandex",
        FreeEngineProvider.Custom => "自定义翻译 API",
        FreeEngineProvider.MyMemory => AlternateFreeEngineName,
        FreeEngineProvider.Youdao => YoudaoEngineName,
        FreeEngineProvider.Bing => BingEngineName,
        FreeEngineProvider.Auto => AutoEngineName,
        _ => FreeEngineName,
    };

    public static string DisplayNameFor(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.Yandex => "Yandex",
        FreeEngineProvider.Custom => "自定义翻译 API",
        FreeEngineProvider.MyMemory => AlternateFreeEngineName,
        FreeEngineProvider.Youdao => YoudaoDisplayName,
        FreeEngineProvider.Bing => "Bing（实验性）",
        FreeEngineProvider.Auto => AutoEngineName,
        _ => FreeEngineName,
    };

    public static string HostFor(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.Yandex => "translate.yandex.net",
        FreeEngineProvider.Custom => "用户配置的翻译 API 地址",
        FreeEngineProvider.MyMemory => "api.mymemory.translated.net",
        FreeEngineProvider.Youdao => "dict.youdao.com",
        FreeEngineProvider.Bing => "cn.bing.com",
        FreeEngineProvider.Auto => "Google / Bing / 有道 / MyMemory（单次择一）",
        _ => "translate.google.com / translate.googleapis.com / clients5.google.com",
    };

    /// <summary>The public engine the user last chose. Falls back to Google.</summary>
    public static string ActiveFreeEngineName()
    {
        try
        {
            return NameFor(ShellSettingsStore.Load().FreeEngineProvider);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return FreeEngineName;
        }
    }

    /// <summary>主窗口关闭按钮：设置选择关闭驻留托盘时的名称。</summary>
    public const string CloseToTrayAction = "关闭到托盘";

    /// <summary>主窗口关闭按钮：设置选择直接退出时的名称。</summary>
    public const string ExitAppAction = "退出 PopGlot";
}
