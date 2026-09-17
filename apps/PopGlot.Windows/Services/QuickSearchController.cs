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
    public string? ErrorMessage { get; private set; }
    public string StatusText { get; private set; } = "输入后按 Enter 翻译";

    public bool IsResultVisible { get; private set; }
    public bool IsStreamLayerVisible { get; private set; }
    public bool IsRichBoxVisible { get; private set; }
    public bool IsStreamIndicatorVisible { get; private set; }
    public bool IsIncompleteBadgeVisible { get; private set; }
    public bool IsProgressVisible { get; private set; }

    public bool CanCopy { get; private set; }
    public bool CanSpeak { get; private set; }
    public bool CanStar { get; private set; }

    // 页脚右侧的动态快捷键提示：随阶段刷新，只承诺当前真正可用的操作。
    public string HintsText { get; private set; } = IdleHintsText;

    private const string IdleHintsText = "Enter 翻译 · Shift+Enter 换行 · Esc 关闭";
    private const string StreamingHintsText = "Esc 取消生成";
    private const string CompletedHintsText = "Ctrl+Shift+C 复制 · Ctrl+P 朗读 · Ctrl+S 收藏 · Esc 关闭";
    private const string PartialHintsText = "Ctrl+Shift+C 复制 · Esc 关闭";
    private const string DismissHintsText = "Esc 关闭";

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
        ErrorMessage = null;
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
        RefreshHints();
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
        StatusText = update.Ttft.HasValue
            ? $"正在生成… · TTFT {update.Ttft.Value.TotalMilliseconds:F0} ms"
            : "正在生成…";
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

        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;

        if (session.Stage == TranslationSessionStage.Completed)
        {
            Stage = QuickSearchUiStage.Completed;
            FinalRenderedText = session.TranslatedText;
            AccumulatedText = session.TranslatedText;
            Phonetic = session.Phonetic;
            Explanation = session.Explanation;
            ErrorMessage = null;
            IsResultVisible = true;
            IsStreamLayerVisible = false;
            IsRichBoxVisible = true;
            IsIncompleteBadgeVisible = false;
            CanCopy = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanSpeak = !string.IsNullOrWhiteSpace(session.TranslatedText);
            CanStar = true;
            var engine = session.PipelineLabel ?? "大模型";
            StatusText = $"{engine} · {session.Timing.TotalElapsedMs} ms";
            RefreshHints();
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
            ErrorMessage = session.Error != null
                ? $"{session.Error.Message} {session.Error.ActionableSuggestion}".Trim()
                : null;
            IsResultVisible = !string.IsNullOrEmpty(AccumulatedText);
            IsStreamLayerVisible = !string.IsNullOrEmpty(AccumulatedText);
            IsRichBoxVisible = false;
            IsIncompleteBadgeVisible = !string.IsNullOrEmpty(AccumulatedText);
            // G06/N04 对齐面板 gate：partial 只保留手动复制；朗读、收藏
            // 与一切自动副作用一律禁止——不完整的内容不可被当作可靠结果。
            CanCopy = !string.IsNullOrWhiteSpace(AccumulatedText);
            CanSpeak = false;
            CanStar = false;
            StatusText = session.Error != null
                ? $"{session.Error.Message}（部分内容已保留）"
                : $"部分完成 · {session.Timing.TotalElapsedMs} ms · 译文不完整";
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
        var err = session.Error != null
            ? $"{session.Error.Message} {session.Error.ActionableSuggestion}".Trim()
            : "翻译失败";
        ErrorMessage = err;
        StatusText = hasPartial ? $"{err}（已保留部分内容）" : err;
        RefreshHints();
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
        ErrorMessage = null;
        var hasPartial = !string.IsNullOrEmpty(AccumulatedText);
        IsResultVisible = hasPartial;
        IsStreamLayerVisible = hasPartial;
        IsRichBoxVisible = false;
        IsIncompleteBadgeVisible = hasPartial;
        // 取消+partial：手动复制之外的动作全部禁止（与面板 gate 一致）。
        CanCopy = hasPartial;
        CanSpeak = false;
        CanStar = false;
        StatusText = hasPartial ? "翻译已取消 · 译文不完整" : "翻译请求已取消";
        RefreshHints();
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
        StatusText = hasPartial ? $"翻译失败: {ex.Message}（已保留部分内容）" : $"翻译失败: {ex.Message}";
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
        IsResultVisible = false;
        IsStreamLayerVisible = false;
        IsRichBoxVisible = false;
        IsProgressVisible = false;
        IsStreamIndicatorVisible = false;
        IsIncompleteBadgeVisible = false;
        CanCopy = false;
        CanSpeak = false;
        CanStar = false;
        RefreshHints();
    }
}
