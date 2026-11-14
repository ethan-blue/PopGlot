namespace PopGlot.Windows.Services;

internal enum TranslationStatusCategory
{
    None,
    Cancelled,
    OutputTruncated,
    ConnectionClosed,
    Failed,
}

internal sealed record TranslationStatusCopy(
    TranslationStatusCategory Category,
    string BadgeText,
    string StatusText,
    string? BannerText,
    string? RetryGuidance,
    bool IsWarningTone);

/// <summary>One deterministic copy source for incomplete translations across all surfaces.</summary>
internal static class TranslationStatusText
{
    public static TranslationStatusCopy Resolve(
        TranslationSessionStage stage,
        bool hasPartial,
        string? errorMessage,
        IEnumerable<string>? warnings)
    {
        if (stage == TranslationSessionStage.Cancelled)
        {
            return hasPartial
                ? new(TranslationStatusCategory.Cancelled, "已取消", "翻译已取消 · 内容不完整",
                    "已取消，内容不完整", null, IsWarningTone: true)
                : new(TranslationStatusCategory.Cancelled, "已取消", "翻译已取消",
                    null, null, IsWarningTone: false);
        }

        if (hasPartial && HasReason(errorMessage, warnings, "长度上限", "截断"))
        {
            return new(TranslationStatusCategory.OutputTruncated, "输出截断", "输出截断 · 内容不完整",
                "输出达到长度上限，内容不完整",
                "可缩短原文后重试，或换用更大输出预算的模型", IsWarningTone: true);
        }

        if (hasPartial && HasReason(errorMessage, warnings, "协议完成事件前"))
        {
            return new(TranslationStatusCategory.ConnectionClosed, "连接早闭", "连接早闭 · 内容可能不完整",
                "服务提前结束响应，内容可能不完整",
                "可重试；若反复出现，请检查服务地址或更换引擎", IsWarningTone: true);
        }

        if (hasPartial)
        {
            return new(TranslationStatusCategory.Failed, "生成失败", "生成失败 · 内容不完整",
                "生成失败，内容不完整", null, IsWarningTone: false);
        }

        if (stage == TranslationSessionStage.Failed || stage == TranslationSessionStage.Partial)
        {
            return new(TranslationStatusCategory.Failed, "未完成", "翻译失败", null, null, IsWarningTone: false);
        }

        return new(TranslationStatusCategory.None, string.Empty, string.Empty, null, null, IsWarningTone: false);
    }

    /// <summary>
    /// T9.35：连接测试失败原因优先取截断/连接早闭类警告（与上方状态分类
    /// 同口径），无则取首条警告。中性提示不再冒充失败原因。
    /// </summary>
    public static string? FailureReason(IEnumerable<string>? warnings)
    {
        if (warnings is null) return null;
        string? first = null;
        string? connectionClosed = null;
        foreach (var warning in warnings)
        {
            first ??= warning;
            if (Contains(warning, "长度上限") || Contains(warning, "截断")) return warning;
            connectionClosed ??= Contains(warning, "协议完成事件前") ? warning : null;
        }
        return connectionClosed ?? first;
    }

    private static bool HasReason(
        string? message,
        IEnumerable<string>? warnings,
        string first,
        string? second = null)
    {
        if (Contains(message, first) || second is not null && Contains(message, second)) return true;
        if (warnings is null) return false;
        foreach (var warning in warnings)
        {
            if (Contains(warning, first) || second is not null && Contains(warning, second)) return true;
        }
        return false;
    }

    private static bool Contains(string? value, string keyword) =>
        value?.Contains(keyword, StringComparison.Ordinal) == true;
}
