using System.IO;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void TranslationRoundTripCacheMatchesAndInvalidates()
    {
        var cache = new TranslationRoundTripCache();
        True(!cache.TryGet("hello", "en", "zh-CN", 1, "faithful:1", out _),
            "an empty cache must miss");

        cache.Set("hello", "en", "zh-CN", "你好", "说明", 1, "faithful:1", "模型A", "准确");
        True(cache.TryGet("hello", "en", "zh-CN", 1, "faithful:1", out var forward),
            "the exact completed request must hit");
        Equal("你好", forward.Text);
        Equal("说明", forward.Explanation);
        True(!forward.IsReverse, "a forward hit must be identified as forward");
        True(!cache.TryGet("other", "en", "zh-CN", 1, "faithful:1", out _),
            "a different source must miss");
        True(!cache.TryGet("hello", "fr", "zh-CN", 1, "faithful:1", out _),
            "a different direction must miss");

        True(cache.TryGet("你好", "zh-CN", "en", 1, "faithful:1", out var reverse),
            "the exact reversed pair must reuse the previous source");
        Equal("hello", reverse.Text);
        Equal(string.Empty, reverse.Explanation,
            "an explanation for the opposite direction must not be presented as a new explanation");
        True(reverse.IsReverse, "a reverse hit must be identified as reverse");

        True(!cache.TryGet("hello", "en", "zh-CN", 2, "faithful:1", out _),
            "a configuration revision change must invalidate the entry");
        cache.Set("hello", "en", "zh-CN", "你好", "说明", 1, "faithful:1");
        True(!cache.TryGet("hello", "en", "zh-CN", 1, "formal:2", out _),
            "a style identity change must invalidate the entry");

        cache.Set("first", "en", "zh-CN", "第一", "", 3, "faithful:1");
        cache.Set("second", "en", "zh-CN", "第二", "", 3, "faithful:1");
        True(!cache.TryGet("first", "en", "zh-CN", 3, "faithful:1", out _),
            "a newer completed translation must replace the previous entry");
        True(cache.TryGet("second", "en", "zh-CN", 3, "faithful:1", out var replacement),
            "the newest entry must remain available");
        Equal("第二", replacement.Text);
    }

    private static void HistoryDedupIncludesSourceLanguage(string root)
    {
        var path = Path.Combine(root, $"history-direction-{Guid.NewGuid():N}.json");
        var history = new HistoryStore(path);
        TranslationHistoryEntry Entry(string sourceLanguage, string translation) => new(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "输入", "same source", translation, "", [],
            sourceLanguage, "zh-CN");
        try
        {
            history.TryAdd(Entry("en", "英文来源译文"), enabled: true);
            history.TryAdd(Entry("auto", "自动来源译文"), enabled: true);
            history.TryAdd(Entry("en", "更新的英文来源译文"), enabled: true);
            var entries = history.Load();
            Equal(2, entries.Count, "en→zh and auto→zh entries must coexist");
            Equal("更新的英文来源译文", entries.Single(entry => entry.SourceLanguage == "en").Translation);
            Equal("自动来源译文", entries.Single(entry => entry.SourceLanguage == "auto").Translation);
            history.Flush();
        }
        finally
        {
            history.Flush();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
