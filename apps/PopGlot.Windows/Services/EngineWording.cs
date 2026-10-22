namespace PopGlot.Windows.Services;

/// <summary>Short route names, kept distinct from the public-translation category.</summary>
internal static class EngineWording
{
    public const string FreeEngineName = "Google";
    public const string AlternateFreeEngineName = "MyMemory";
    public const string FreePublicTranslationName = "公共翻译";

    /// <summary>离线总开关的统一名称。</summary>
    public const string SafeOfflineModeName = "安全离线模式";

    /// <summary>本机文字识别步骤名称（只描述识别，不描述翻译）。</summary>
    public const string LocalOcrStepName = "本地 OCR";

    /// <summary>本地运行时（Ollama / LM Studio）线路名称。</summary>
    public const string LocalModelName = "本地模型";

    /// <summary>隐私语境中对免费引擎的诚实定性：它是联网公共服务。</summary>
    public const string FreeEngineOnlineServiceNote =
        "联网公共翻译，只把待翻译文本发给你选中的那一条：Google（translate.googleapis.com / clients5.google.com）或 MyMemory（api.mymemory.translated.net）。不发送截图、历史或密钥。";

    public static string NameFor(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.MyMemory => AlternateFreeEngineName,
        _ => FreeEngineName,
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
