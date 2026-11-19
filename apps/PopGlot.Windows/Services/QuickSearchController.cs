using System.Text;

namespace PopGlot.Windows.Services;

internal readonly record struct LiveTranslateGuardResult(bool Allowed, string? Hint);

internal static class QuickSearchLivePolicy
{
    public const int MinAutoQuotaRemaining = 30;
    public const string LowQuotaHint = "剩余自动配额不足，按 Enter 手动翻译";
    public const string MyMemoryTooLongHint = "内容较长，按 Enter 使用分段翻译";
    public const string YoudaoTooLongHint = "有道词典适合单词和短语，长句可按 Enter 或切换 Bing";

    public static bool ContainsCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch is (>= '\u4E00' and <= '\u9FFF')
                or (>= '\u3400' and <= '\u4DBF')
                or (>= '\uF900' and <= '\uFAFF')
                or (>= '\u3040' and <= '\u30FF')
                or (>= '\uAC00' and <= '\uD7AF'))
            {
                return true;
            }
        }
        return false;
    }

    public static bool MeetsMinimumLength(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        return trimmed.Length >= 2 || ContainsCjk(trimmed);
    }

    public static LiveTranslateGuardResult EvaluateFreeRouteGuards(
        string trimmed,
        FreeEngineProvider provider,
        int quotaRemaining,
        string sourceLang = LanguageCatalog.Auto,
        string targetLang = "zh-CN")
    {
        if (quotaRemaining < MinAutoQuotaRemaining)
        {
            return new LiveTranslateGuardResult(false, LowQuotaHint);
        }

        var effective = provider == FreeEngineProvider.Auto
            ? FreeTranslateService.SelectAutoProvider(trimmed, sourceLang, targetLang)
            : provider;

        if (effective == FreeEngineProvider.MyMemory &&
            Encoding.UTF8.GetByteCount(trimmed) > FreeTranslateService.MyMemoryMaxQueryBytes)
        {
            return new LiveTranslateGuardResult(false, MyMemoryTooLongHint);
        }

        if (effective == FreeEngineProvider.Youdao &&
            FreeTranslateService.IsYoudaoTooLongOrMultiSentence(trimmed))
        {
            return new LiveTranslateGuardResult(false, YoudaoTooLongHint);
        }

        return new LiveTranslateGuardResult(true, null);
    }
}

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
    public string? ErrorMessage { get; private set; }
    public string StatusText { get; private set; } = "输入后按 Enter 翻译";

    public bool IsResultVisible { get; private set; }
    public bool IsStreamLayerVisible { get; private set; }
    public bool IsRichBoxVisible { get; private set; }
    public bool IsStreamIndicatorVisible { get; private set; }
    public bool IsIncompleteBadgeVisible { get; private set; }
    public string IncompleteBadgeText { get; private set; } = "未完成";
    public bool IsPartialWarningTone { get; private set; } = true;
    public bool IsProgressVisible { get; private set; }
    public bool IsDimmingPreviousResult { get; private set; }
    public string? RouteLatencyText { get; private set; }
    public string? RouteStagesTooltip { get; private set; }

    public bool CanCopy { get; private set; }
    public bool CanSpeak { get; private set; }
    public bool CanStar { get; private set; }

    /// <summary>
    /// One-shot footer notice (e.g. the hotkey pre-read timeout landing
    /// notice). State-driven: <see cref="PostPendingNotice"/> arms it, every
    /// sync consults it, and ANY real state transition below clears it — so
    /// the next query/status update replaces it naturally and it can never
    /// permanently occupy the footer.
    /// </summary>
    public string? PendingNotice { get; private set; }
    public bool IsPendingNoticeError { get; private set; }

    /// <summary>Arms the one-shot footer notice (null clears it).</summary>
    public void PostPendingNotice(string? notice, bool error = false)
    {
        PendingNotice = notice;
        IsPendingNoticeError = notice is not null && error;
    }

    // 页脚右侧的动态快捷键提示：随阶段刷新，只承诺当前真正可用的操作。
    public string HintsText { get; private set; } = IdleHintsText;

    private const string IdleHintsText = "Enter 翻译 · Shift+Enter 换行 · Esc 关闭";
    private const string StreamingHintsText = "Esc 取消生成";
    // T9.23：朗读键可录制，提示从 InterfaceKeys.SpeakHotkey 拼装；
    // Ctrl+Shift+C / Ctrl+S 是固定键，保留字面量。
    private static string CompletedHintsText =>
        $"Ctrl+Shift+C 复制 · {(InterfaceKeys.SpeakHotkey.IsValid ? InterfaceKeys.SpeakHotkey.DisplayName : "Ctrl+P")} 朗读 · Ctrl+S 收藏 · Esc 关闭";
    private const string PartialHintsText = "Ctrl+Shift+C 复制 · Enter 重试 · Esc 关闭";
    private const string DismissHintsText = "Enter 重试 · Esc 关闭";

    private void RefreshHints()
    {
        HintsText = Stage switch
        {
            QuickSearchUiStage.Idle => IdleHintsText,
            QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing => StreamingHintsText,
            QuickSearchUiStage.Completed => CompletedHintsText,
            _ => CanCopy ? PartialHintsText : DismissHintsText,
        };
    }

    public bool AcceptUpdate(long epoch, string queryText)
    {
        if (IsClosed) return false;
        // Epoch fencing alone cannot reject callbacks already queued by the
        // same request. A terminal result is immutable until the next search.
        if (Stage is not (QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing)) return false;
        if (epoch != CurrentEpoch) return false;
        if (!string.Equals(CurrentQuery.Trim(), queryText.Trim(), StringComparison.Ordinal)) return false;
        return true;
    }

    public void StartNewSearch(string query) => StartNewSearch(query, preservePreviousResultForLive: false);

    public void StartNewSearch(string query, bool preservePreviousResultForLive)
    {
        var hasPreviousResult = preservePreviousResultForLive &&
            IsResultVisible &&
            Stage is QuickSearchUiStage.Completed or QuickSearchUiStage.Partial &&
            (!string.IsNullOrWhiteSpace(FinalRenderedText) || !string.IsNullOrWhiteSpace(AccumulatedText));

        CurrentEpoch++;
        CurrentQuery = query.Trim();
        Stage = QuickSearchUiStage.Streaming;
        PendingNotice = null;
        IsProgressVisible = true;
        IsIncompleteBadgeVisible = false;
        IncompleteBadgeText = "未完成";
        IsPartialWarningTone = true;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
        RouteLatencyText = null;
        RouteStagesTooltip = null;

        if (hasPreviousResult)
        {
            IsDimmingPreviousResult = true;
            IsResultVisible = true;
            IsStreamIndicatorVisible = false;
            StatusText = "正在实时翻译…";
        }
        else
        {
            IsDimmingPreviousResult = false;
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
            Phonetic = null;
            Explanation = null;
            ErrorMessage = null;
            IsResultVisible = true;
            IsStreamLayerVisible = true;
            IsRichBoxVisible = false;
            IsStreamIndicatorVisible = true;
            StatusText = "正在生成…";
        }

        RefreshHints();
    }

    public bool OnStreamUpdate(TranslationStreamUpdate update, string currentInputQuery)
    {
        if (!AcceptUpdate(update.Epoch, currentInputQuery))
        {
            return false;
        }

        // A live stream update is a real state update: any pending notice
        // yields to the progress copy.
        PendingNotice = null;
        Stage = QuickSearchUiStage.Streaming;
        IsProgressVisible = true;
        if (IsDimmingPreviousResult)
        {
            IsDimmingPreviousResult = false;
            FinalRenderedText = string.Empty;
            Phonetic = null;
            Explanation = null;
            ErrorMessage = null;
        }

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
            RefreshHints();
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
        // 0.1.6 文案减法：流式状态只说"正在生成…"，不向用户暴露 TTFT 这类
        // 内部测量（update.Ttft 仍是数据字段，仅不再进入显示文案）。
        StatusText = "正在生成…";
        RefreshHints();
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
            PendingNotice = null;
            Stage = QuickSearchUiStage.Finalizing;
            IsStreamIndicatorVisible = false;
            StatusText = "正在整理结果…";
            RefreshHints();
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

        var wasDimming = IsDimmingPreviousResult;
        IsDimmingPreviousResult = false;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        // A terminal state always rewrites the status copy: any pending
        // notice is superseded here.
        PendingNotice = null;

        if (session.Stage == TranslationSessionStage.Completed)
        {
            Stage = QuickSearchUiStage.Completed;
            FinalRenderedText = session.TranslatedText;
            AccumulatedText = session.TranslatedText;
            Phonetic = session.Phonetic;
            Explanation = string.Join("\n", new[] { session.Explanation }
                .Concat(session.Warnings).Where(note => !string.IsNullOrWhiteSpace(note)).Distinct());
            ErrorMessage = null;
            IsResultVisible = true;
            IsStreamLayerVisible = false;
            IsRichBoxVisible = true;
            IsIncompleteBadgeVisible = false;
            CanCopy = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanSpeak = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanStar = true;
            // 0.1.27 文案减法：引擎名不再进状态行（产品即 PopGlot/OMP，
            // 徽标已指明引擎），只保留面向用户的秒级用时。
            StatusText = TranslationElapsedText.ForTiming(session.Timing);
            RouteLatencyText = TranslationElapsedText.FormatRouteLatency(session.PipelineLabel, null, session.Timing);
            RouteStagesTooltip = TranslationElapsedText.DescribeStagesTooltip(session.Timing);
            RefreshHints();
            return true;
        }

        if (wasDimming)
        {
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
            Phonetic = null;
            Explanation = null;
        }
        RouteLatencyText = null;
        RouteStagesTooltip = null;

        if (session.Stage == TranslationSessionStage.Partial)
        {
            Stage = QuickSearchUiStage.Partial;
            FinalRenderedText = string.Empty;
            if (!string.IsNullOrEmpty(session.TranslatedText))
            {
                AccumulatedText = session.TranslatedText;
            }
            Phonetic = session.Phonetic;
            var partialError = session.Error is not null ? ComposeErrorText(session.Error)
                : session.Warnings.Count > 0 ? string.Join("\n", session.Warnings) : null;
            var partialExplanation = string.Join("\n", new[] { session.Explanation, partialError }
                .Concat(session.Warnings).Where(note => !string.IsNullOrWhiteSpace(note)).Distinct());
            // Keep the reason and retry guidance in the existing explanation card.
            var partialHasText = !string.IsNullOrWhiteSpace(AccumulatedText);
            var partialPresentation = TranslationStatusText.Resolve(
                session.Stage, partialHasText, partialError, session.Warnings);
            ApplyIncompleteCopy(partialPresentation);
            ErrorMessage = partialError;
            Explanation = AppendGuidance(partialExplanation, partialPresentation.RetryGuidance);
            IsResultVisible = partialHasText;
            IsStreamLayerVisible = partialHasText;
            IsRichBoxVisible = false;
            IsIncompleteBadgeVisible = partialHasText;
            // G06/N04: partial text remains manually copyable only.
            CanCopy = partialHasText;
            CanSpeak = false;
            CanStar = false;
            StatusText = partialPresentation.StatusText;
            RefreshHints();
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
        // WIN-01: Show result area so user sees error card & settings repair path
        IsResultVisible = true;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        // 与面板 gate 一致：failed+partial 只允许手动复制。
        CanCopy = hasPartial;
        CanSpeak = false;
        CanStar = false;
        var err = session.Error is not null ? ComposeErrorText(session.Error) : "翻译失败";
        var presentation = TranslationStatusText.Resolve(session.Stage, hasPartial, err, session.Warnings);
        ApplyIncompleteCopy(presentation);
        ErrorMessage = err;
        Explanation = AppendGuidance(string.Join("\n", new[] { session.Explanation }
            .Concat(session.Warnings).Where(note => !string.IsNullOrWhiteSpace(note))), presentation.RetryGuidance);
        StatusText = hasPartial ? presentation.StatusText : err;
        RefreshHints();
        return true;
    }

    public bool OnCachedSessionCompleted(TranslationSession session, long epoch, string currentInputQuery)
    {
        if (!OnSessionCompleted(session, epoch, currentInputQuery)) return false;
        StatusText = TranslationRoundTripCache.ReusedStatus;
        RouteLatencyText = string.IsNullOrWhiteSpace(session.PipelineLabel)
            ? TranslationRoundTripCache.ReusedFact
            : $"{session.PipelineLabel.Trim()} · {TranslationRoundTripCache.ReusedFact}";
        RouteStagesTooltip = null;
        return true;
    }

    /// <summary>
    /// One error-composition source for the Partial and Failed branches: the
    /// message plus a NON-EMPTY actionable suggestion, deduplicated when the
    /// classifier already embedded the suggestion in the message — the same
    /// failure must never read differently (or doubled) across the two stages.
    /// </summary>
    private static string ComposeErrorText(TranslationError error)
    {
        var message = error.Message.Trim();
        var suggestion = error.ActionableSuggestion?.Trim();
        if (string.IsNullOrEmpty(suggestion))
        {
            return message;
        }
        if (message.Contains(suggestion, StringComparison.Ordinal))
        {
            return message;
        }
        return string.IsNullOrEmpty(message) ? suggestion : $"{message} {suggestion}";
    }
    private void ApplyIncompleteCopy(TranslationStatusCopy copy)
    {
        IncompleteBadgeText = copy.BadgeText;
        IsPartialWarningTone = copy.IsWarningTone;
    }

    private static string AppendGuidance(string text, string? guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance) || text.Contains(guidance, StringComparison.Ordinal)) return text;
        return string.IsNullOrWhiteSpace(text) ? guidance : $"{text}\n{guidance}";
    }

    public bool OnCancelled(long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        if (IsDimmingPreviousResult)
        {
            IsDimmingPreviousResult = false;
            AccumulatedText = string.Empty;
        }
        RouteLatencyText = null;
        RouteStagesTooltip = null;
        PendingNotice = null;
        Stage = QuickSearchUiStage.Cancelled;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        FinalRenderedText = string.Empty;
        ErrorMessage = null;
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        ApplyIncompleteCopy(TranslationStatusText.Resolve(
            TranslationSessionStage.Cancelled, hasPartial, null, warnings: null));
        IsResultVisible = hasPartial;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        // 取消+partial：手动复制之外的动作全部禁止（与面板 gate 一致）。
        CanCopy = hasPartial;
        CanSpeak = false;
        CanStar = false;
        StatusText = hasPartial ? TranslationStatusText.Resolve(
            TranslationSessionStage.Cancelled, hasPartial, null, warnings: null).StatusText : "翻译请求已取消";
        RefreshHints();
        return true;
    }

    public bool OnException(Exception ex, long epoch, string currentInputQuery)
    {
        if (!AcceptUpdate(epoch, currentInputQuery))
        {
            return false;
        }

        if (IsDimmingPreviousResult)
        {
            IsDimmingPreviousResult = false;
            AccumulatedText = string.Empty;
        }
        RouteLatencyText = null;
        RouteStagesTooltip = null;
        PendingNotice = null;
        Stage = QuickSearchUiStage.Failed;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        FinalRenderedText = string.Empty;
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        // WIN-01: Show result card on exception so user sees error & settings button
        IsResultVisible = true;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        // 异常+partial：同样只允许手动复制。
        CanCopy = hasPartial;
        CanSpeak = false;
        CanStar = false;
        ErrorMessage = $"翻译失败: {ex.Message}";
        var presentation = TranslationStatusText.Resolve(
            TranslationSessionStage.Failed, hasPartial, ErrorMessage, warnings: null);
        ApplyIncompleteCopy(presentation);
        Explanation = AppendGuidance(ErrorMessage, presentation.RetryGuidance);
        StatusText = hasPartial ? presentation.StatusText : ErrorMessage;
        RefreshHints();
        return true;
    }

    public void OnQueryTextChanged(string newQuery)
    {
        var trimmed = newQuery.Trim();
        if (trimmed == CurrentQuery)
        {
            return;
        }

        // Epoch 自增即把在途请求的一切后续回调（流式、阶段、终态） fenced
        // 掉：旧的会话不再能写进新查询的界面，不留 Streaming 孤儿。
        // The pending notice (if any) is superseded by every branch below.
        CurrentEpoch++;
        CurrentQuery = trimmed;
        PendingNotice = null;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        if (IsDimmingPreviousResult)
        {
            IsDimmingPreviousResult = false;
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
        }
        RouteLatencyText = null;
        RouteStagesTooltip = null;

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
            ErrorMessage = null;
            IsIncompleteBadgeVisible = false;
            CanCopy = false;
            CanSpeak = false;
            CanStar = false;
            StatusText = "输入后按 Enter 翻译";
        }
        else if (Stage is QuickSearchUiStage.Streaming or QuickSearchUiStage.Finalizing &&
                 !string.IsNullOrEmpty(AccumulatedText))
        {
            // 流式中改查询：旧内容不是无标记孤儿，而是明示转成
            // Cancelled+partial——文本保留、徽标可见、只可手动复制，
            // 状态行告诉用户按 Enter 重新翻译。
            ApplyIncompleteCopy(TranslationStatusText.Resolve(
                TranslationSessionStage.Cancelled, hasPartial: true, errorMessage: null, warnings: null));
            Stage = QuickSearchUiStage.Cancelled;
            ErrorMessage = null;
            IsResultVisible = true;
            IsStreamLayerVisible = true;
            IsRichBoxVisible = false;
            IsIncompleteBadgeVisible = true;
            CanCopy = true;
            CanSpeak = false;
            CanStar = false;
            StatusText = "旧查询已取消，按 Enter 重新翻译";
        }
        else
        {
            // 尚无真实内容（准备期或空流）或已有终态：直接回到待翻译，
            // 不留下任何流式残迹。
            Stage = QuickSearchUiStage.Idle;
            IsResultVisible = false;
            IsStreamLayerVisible = false;
            IsRichBoxVisible = false;
            AccumulatedText = string.Empty;
            FinalRenderedText = string.Empty;
            Phonetic = null;
            Explanation = null;
            ErrorMessage = null;
            IsIncompleteBadgeVisible = false;
            CanCopy = false;
            CanSpeak = false;
            CanStar = false;
            StatusText = "按 Enter 立即翻译 · Shift+Enter 换行";
        }
        RefreshHints();
    }

    public void OnClose()
    {
        IsClosed = true;
        CurrentEpoch++;
        Stage = QuickSearchUiStage.Idle;
        AccumulatedText = string.Empty;
        FinalRenderedText = string.Empty;
        ErrorMessage = null;
        PendingNotice = null;
        IsResultVisible = false;
        IsStreamLayerVisible = false;
        IsRichBoxVisible = false;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        IsIncompleteBadgeVisible = false;
        IsDimmingPreviousResult = false;
        RouteLatencyText = null;
        RouteStagesTooltip = null;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
        RefreshHints();
    }
}
