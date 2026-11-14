using PopGlot.Windows.Services;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void TranslationStatusCopyClassification()
    {
        var cases = new[]
        {
            ("模型输出达到长度上限，译文可能被截断。", TranslationStatusCategory.OutputTruncated,
                "输出截断", "输出截断 · 内容不完整", "输出达到长度上限，内容不完整"),
            ("SSE 流在协议完成事件前正常结束；译文可能不完整。", TranslationStatusCategory.ConnectionClosed,
                "连接早闭", "连接早闭 · 内容可能不完整", "服务提前结束响应，内容可能不完整"),
            ("HTTP 500 server error", TranslationStatusCategory.Failed,
                "生成失败", "生成失败 · 内容不完整", "生成失败，内容不完整"),
            ("", TranslationStatusCategory.Failed,
                "生成失败", "生成失败 · 内容不完整", "生成失败，内容不完整"),
        };

        foreach (var item in cases)
        {
            var partial = TranslationStatusText.Resolve(
                TranslationSessionStage.Failed, hasPartial: true, item.Item1, warnings: null);
            Equal(item.Item2, partial.Category, $"partial category for '{item.Item1}'");
            Equal(item.Item3, partial.BadgeText, $"partial badge for '{item.Item1}'");
            Equal(item.Item4, partial.StatusText, $"partial status for '{item.Item1}'");
            Equal(item.Item5, partial.BannerText, $"partial banner for '{item.Item1}'");

            var empty = TranslationStatusText.Resolve(
                TranslationSessionStage.Failed, hasPartial: false, item.Item1, warnings: null);
            Equal(TranslationStatusCategory.Failed, empty.Category, $"empty category for '{item.Item1}'");
            Equal("未完成", empty.BadgeText, $"empty badge for '{item.Item1}'");
            Equal("翻译失败", empty.StatusText, $"empty status for '{item.Item1}'");
            Equal<string?>(null, empty.BannerText, $"empty banner for '{item.Item1}'");
        }

        var warningOnlyTruncation = TranslationStatusText.Resolve(
            TranslationSessionStage.Partial, hasPartial: true, errorMessage: null,
            warnings: ["模型输出达到长度上限，译文可能被截断。"]);
        Equal(TranslationStatusCategory.OutputTruncated, warningOnlyTruncation.Category,
            "a partial's warning-only reason must classify as output truncation");
        Equal("输出截断 · 内容不完整", warningOnlyTruncation.StatusText);

        var cancelledPartial = TranslationStatusText.Resolve(
            TranslationSessionStage.Cancelled, hasPartial: true, "HTTP 500", warnings: null);
        Equal(TranslationStatusCategory.Cancelled, cancelledPartial.Category);
        Equal("已取消", cancelledPartial.BadgeText);
        Equal("翻译已取消 · 内容不完整", cancelledPartial.StatusText);
        Equal("已取消，内容不完整", cancelledPartial.BannerText);

        var cancelledEmpty = TranslationStatusText.Resolve(
            TranslationSessionStage.Cancelled, hasPartial: false, null, warnings: null);
        Equal("已取消", cancelledEmpty.BadgeText);
        Equal("翻译已取消", cancelledEmpty.StatusText);
        Equal<string?>(null, cancelledEmpty.BannerText);

        var partialStage = TranslationStatusText.Resolve(
            TranslationSessionStage.Partial, hasPartial: true, "响应可能不完整", warnings: null);
        Equal(TranslationStatusCategory.Failed, partialStage.Category);
        Equal("生成失败", partialStage.BadgeText);
    }
}
