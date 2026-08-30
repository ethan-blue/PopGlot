namespace PopGlot.Windows.Services;

internal enum QuickSearchUiStage
{
    Idle,
    Streaming,
    Finalizing,
    Completed,
    Partial,
    Failed,
    Cancelled,
}

internal sealed class QuickSearchState
{
    public const double ResultAreaMinHeight = 68.0;
    public const double ResultStreamMinHeight = 24.0;

    public long CurrentEpoch { get; private set; }
    public string CurrentQuery { get; private set; } = string.Empty;
    public bool IsClosed { get; private set; }
    public QuickSearchUiStage Stage { get; private set; } = QuickSearchUiStage.Idle;

    public string AccumulatedText { get; private set; } = string.Empty;
    public string FinalRenderedText { get; private set; } = string.Empty;
    public string? Phonetic { get; private set; }
    public string? Explanation { get; private set; }
    public string StatusText { get; private set; } = "输入文字后按 Enter 翻译";

    public bool IsResultVisible { get; private set; }
    public bool IsStreamLayerVisible { get; private set; }
    public bool IsRichBoxVisible { get; private set; }
    public bool IsStreamIndicatorVisible { get; private set; }
    public bool IsIncompleteBadgeVisible { get; private set; }
    public bool IsProgressVisible { get; private set; }

    public bool CanCopy { get; private set; }
    public bool CanSpeak { get; private set; }
    public bool CanStar { get; private set; }

    public bool AcceptUpdate(long epoch, string queryText)
    {
        if (IsClosed) return false;
        if (epoch != CurrentEpoch) return false;
        if (!string.Equals(CurrentQuery.Trim(), queryText.Trim(), StringComparison.Ordinal)) return false;
        return true;
    }

    public void StartNewSearch(string query)
    {
        CurrentEpoch++;
        CurrentQuery = query.Trim();
        Stage = QuickSearchUiStage.Streaming;
        AccumulatedText = string.Empty;
        FinalRenderedText = string.Empty;
        Phonetic = null;
        Explanation = null;
        IsResultVisible = true;
        IsStreamLayerVisible = true;
        IsRichBoxVisible = false;
        IsStreamIndicatorVisible = true;
        IsIncompleteBadgeVisible = false;
        IsProgressVisible = true;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
        StatusText = "正在生成…";
    }

    public bool OnStreamUpdate(TranslationStreamUpdate update, string currentInputQuery)
    {
        if (!AcceptUpdate(update.Epoch, currentInputQuery))
        {
            return false;
        }

        Stage = QuickSearchUiStage.Streaming;
        IsProgressVisible = true;

        if (update.Kind == TranslationStreamUpdateKind.Reset)
        {
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
            IsResultVisible = true;
            IsStreamLayerVisible = true;
            IsRichBoxVisible = false;
            IsStreamIndicatorVisible = true;
            IsIncompleteBadgeVisible = false;
            CanCopy = false;
            CanSpeak = false;
            CanStar = false;
            StatusText = "正在生成…";
            return true;
        }

        AccumulatedText = update.AccumulatedText;
        IsResultVisible = true;
        IsStreamLayerVisible = true;
        IsRichBoxVisible = false;
        IsStreamIndicatorVisible = true;
        IsIncompleteBadgeVisible = false;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
        StatusText = update.Ttft.HasValue
            ? $"正在生成… · TTFT {update.Ttft.Value.TotalMilliseconds:F0} ms"
            : "正在生成…";
        return true;
    }

    public bool OnStageChanged(TranslationSessionStage stage, long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        if (stage == TranslationSessionStage.Finalizing)
        {
            Stage = QuickSearchUiStage.Finalizing;
            IsStreamIndicatorVisible = false;
            StatusText = "正在整理结果…";
            return true;
        }

        return false;
    }

    public bool OnSessionCompleted(TranslationSession session, long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;

        if (session.Stage == TranslationSessionStage.Completed)
        {
            Stage = QuickSearchUiStage.Completed;
            FinalRenderedText = session.TranslatedText;
            AccumulatedText = session.TranslatedText;
            Phonetic = session.Phonetic;
            Explanation = session.Explanation;
            IsResultVisible = true;
            IsStreamLayerVisible = false;
            IsRichBoxVisible = true;
            IsIncompleteBadgeVisible = false;
            CanCopy = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanSpeak = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanStar = true;
            var engine = session.PipelineLabel ?? "大模型";
            StatusText = $"{engine} · {session.Timing.TotalElapsedMs} ms";
            return true;
        }

        if (session.Stage == TranslationSessionStage.Partial)
        {
            Stage = QuickSearchUiStage.Partial;
            FinalRenderedText = string.Empty;
            if (!string.IsNullOrEmpty(session.TranslatedText))
            {
                AccumulatedText = session.TranslatedText;
            }
            Phonetic = session.Phonetic;
            Explanation = session.Explanation;
            IsResultVisible = !string.IsNullOrEmpty(AccumulatedText);
            IsStreamLayerVisible = !string.IsNullOrEmpty(AccumulatedText);
            IsRichBoxVisible = false;
            IsIncompleteBadgeVisible = !string.IsNullOrEmpty(AccumulatedText);
            CanCopy = !string.IsNullOrWhiteSpace(AccumulatedText);
            CanSpeak = !string.IsNullOrWhiteSpace(AccumulatedText);
            CanStar = false;
            StatusText = session.Error != null
                ? $"{session.Error.Message}（部分内容已保留）"
                : $"部分完成 · {session.Timing.TotalElapsedMs} ms · 译文不完整";
            return true;
        }

        if (session.Stage == TranslationSessionStage.Cancelled)
        {
            return OnCancelled(epoch, currentInputQuery);
        }

        // Failed stage or other
        Stage = QuickSearchUiStage.Failed;
        FinalRenderedText = string.Empty;
        if (!string.IsNullOrEmpty(session.TranslatedText))
        {
            AccumulatedText = session.TranslatedText;
        }
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        IsResultVisible = hasPartial;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        CanCopy = hasPartial;
        CanSpeak = hasPartial;
        CanStar = false;
        var err = session.Error != null
            ? $"{session.Error.Message} {session.Error.ActionableSuggestion}".Trim()
            : "翻译失败";
        StatusText = hasPartial ? $"{err}（已保留部分内容）" : err;
        return true;
    }

    public bool OnCancelled(long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        Stage = QuickSearchUiStage.Cancelled;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        FinalRenderedText = string.Empty;
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        IsResultVisible = hasPartial;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        CanCopy = hasPartial;
        CanSpeak = hasPartial;
        CanStar = false;
        StatusText = hasPartial ? "翻译已取消 · 译文不完整" : "翻译请求已取消";
        return true;
    }

    public bool OnException(Exception ex, long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        Stage = QuickSearchUiStage.Failed;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        FinalRenderedText = string.Empty;
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        IsResultVisible = hasPartial;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        CanCopy = hasPartial;
        CanSpeak = hasPartial;
        CanStar = false;
        StatusText = hasPartial ? $"翻译失败: {ex.Message}（已保留部分内容）" : $"翻译失败: {ex.Message}";
        return true;
    }

    public void OnQueryTextChanged(string newQuery)
    {
        var trimmed = newQuery.Trim();
        if (trimmed == CurrentQuery)
        {
            return;
        }

        CurrentEpoch++;
        CurrentQuery = trimmed;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;

        if (string.IsNullOrEmpty(trimmed))
        {
            Stage = QuickSearchUiStage.Idle;
            IsResultVisible = false;
            IsStreamLayerVisible = false;
            IsRichBoxVisible = false;
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
            Phonetic = null;
            Explanation = null;
            IsIncompleteBadgeVisible = false;
            CanCopy = false;
            CanSpeak = false;
            CanStar = false;
            StatusText = "输入文字后按 Enter 翻译";
        }
        else
        {
            StatusText = "按 Enter 立即翻译 · Shift+Enter 换行";
        }
    }

    public void OnClose()
    {
        IsClosed = true;
        CurrentEpoch++;
        Stage = QuickSearchUiStage.Idle;
        AccumulatedText = string.Empty;
        FinalRenderedText = string.Empty;
        IsResultVisible = false;
        IsStreamLayerVisible = false;
        IsRichBoxVisible = false;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        IsIncompleteBadgeVisible = false;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
    }
}
