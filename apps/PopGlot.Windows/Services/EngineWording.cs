namespace PopGlot.Windows.Services;

/// <summary>
/// 用户可见引擎术语的唯一出处（Wave C 术语统一）。所有生产代码与可编辑
/// XAML 引用这里的常量，禁止再散落字面量造成字符串漂移：
/// - 免费兜底线路在一切表面统一叫「内置免费引擎」（含空态展示名），隐私
///   语境必须说明它是<b>联网</b>的公共服务；
/// - 离线总开关统一叫「安全离线模式」（不再出现「纯离线模式」）；
/// - 「本地 OCR」只描述本机识别这一步，绝不暗示翻译也在本地完成。
/// </summary>
internal static class EngineWording
{
    /// <summary>免费兜底引擎的用户可见名称（设置、页脚、管线徽章一致）。</summary>
    public const string FreeEngineName = "内置免费引擎";

    /// <summary>
    /// 空态/向导中对同一条免费线路的展示名。0.1.6 起与
    /// <see cref="FreeEngineName"/> 统一，不再使用「内置公共翻译」别名。
    /// </summary>
    public const string FreePublicTranslationName = FreeEngineName;

    /// <summary>离线总开关的统一名称。</summary>
    public const string SafeOfflineModeName = "安全离线模式";

    /// <summary>本机文字识别步骤名称（只描述识别，不描述翻译）。</summary>
    public const string LocalOcrStepName = "本地 OCR";

    /// <summary>本地运行时（Ollama / LM Studio）线路名称。</summary>
    public const string LocalModelName = "本地模型";

    /// <summary>隐私语境中对免费引擎的诚实定性：它是联网公共服务。</summary>
    public const string FreeEngineOnlineServiceNote =
        "联网公共翻译服务，仅发送待翻译文本；不发送截图、历史记录或密钥。";

    /// <summary>主窗口关闭按钮：设置选择关闭驻留托盘时的名称。</summary>
    public const string CloseToTrayAction = "关闭到托盘";

    /// <summary>主窗口关闭按钮：设置选择直接退出时的名称。</summary>
    public const string ExitAppAction = "退出 PopGlot";
}
