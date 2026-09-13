namespace PopGlot.Windows.Services;

internal enum TranslationPanelStage
{
    Idle,
    Preparing,
    Streaming,
    Finalizing,
    Completed,
    CancelledWithPartial,
    CancelledWithoutPartial,
    FailedWithPartial,
    FailedWithoutPartial,
}

/// <summary>
/// Authoritative state gate and lifecycle tracker for TranslationPanelWindow.
/// Enforces monotonic epoch fencing, stream buffer synchronization, and
/// strict action gating (copy, speak, star, auto-copy) across session stages.
/// </summary>
internal sealed class TranslationPanelStreamGate
{
    private long _currentEpoch;
    private int _currentOperationId;

    public long CurrentEpoch => Volatile.Read(ref _currentEpoch);
    public int CurrentOperationId => Volatile.Read(ref _currentOperationId);
    public TranslationPanelStage Stage { get; private set; } = TranslationPanelStage.Idle;
    public string StreamedText { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }

    public (long Epoch, int OperationId) BeginNewOperation()
    {
        var epoch = Interlocked.Increment(ref _currentEpoch);
        var opId = Interlocked.Increment(ref _currentOperationId);
        Stage = TranslationPanelStage.Preparing;
        StreamedText = string.Empty;
        ErrorMessage = null;
        return (epoch, opId);
    }

    public bool ShouldAcceptUpdate(long updateEpoch, bool isClosed, bool isLoadedOrVisible = true)
    {
        if (isClosed) return false;
        if (!isLoadedOrVisible) return false;
        return updateEpoch == Volatile.Read(ref _currentEpoch);
    }

    public bool ApplyUpdate(TranslationStreamUpdate update)
    {
        if (update.Epoch != Volatile.Read(ref _currentEpoch))
        {
            return false;
        }

        if (update.Kind == TranslationStreamUpdateKind.Reset)
        {
            Stage = TranslationPanelStage.Preparing;
            StreamedText = string.Empty;
            return true;
        }

        if (update.Kind == TranslationStreamUpdateKind.Delta)
        {
            Stage = TranslationPanelStage.Streaming;
            StreamedText = update.AccumulatedText ?? string.Empty;
            return true;
        }

        return false;
    }

    public void OnStageChanged(TranslationSessionStage stage)
    {
        if (Stage is TranslationPanelStage.Completed or TranslationPanelStage.CancelledWithPartial
            or TranslationPanelStage.CancelledWithoutPartial or TranslationPanelStage.FailedWithPartial
            or TranslationPanelStage.FailedWithoutPartial)
        {
            return;
        }

        if (stage == TranslationSessionStage.Finalizing)
        {
            Stage = TranslationPanelStage.Finalizing;
        }
    }

    public void OnCompleted(string finalText)
    {
        Stage = TranslationPanelStage.Completed;
        StreamedText = finalText ?? string.Empty;
    }

    public void OnCancelled(string? partialText = null)
    {
        var text = !string.IsNullOrEmpty(partialText) ? partialText : StreamedText;
        StreamedText = text ?? string.Empty;
        Stage = !string.IsNullOrWhiteSpace(StreamedText)
            ? TranslationPanelStage.CancelledWithPartial
            : TranslationPanelStage.CancelledWithoutPartial;
    }

    public void OnFailed(string errorMessage, string? partialText = null)
    {
        ErrorMessage = errorMessage;
        var text = !string.IsNullOrEmpty(partialText) ? partialText : StreamedText;
        StreamedText = text ?? string.Empty;
        Stage = !string.IsNullOrWhiteSpace(StreamedText)
            ? TranslationPanelStage.FailedWithPartial
            : TranslationPanelStage.FailedWithoutPartial;
    }

    public void ResetToIdle()
    {
        Stage = TranslationPanelStage.Idle;
        StreamedText = string.Empty;
        ErrorMessage = null;
    }

    /// <summary>
    /// TTS and Star actions are allowed ONLY when the session reached clean completion
    /// with non-empty translation text. They are strictly prohibited during
    /// Preparing, Streaming, Finalizing, and on Cancelled/Failed (even with partial).
    /// Explicit manual copy is permitted for partial text per G06/N04 via <see cref="CanCopy"/>
    /// and <see cref="CanCopyPartial"/>.
    /// </summary>
    public bool CanPerformResultActions =>
        Stage == TranslationPanelStage.Completed && !string.IsNullOrWhiteSpace(StreamedText);

    /// <summary>
    /// G06 / V2 N04: Explicit manual copy is allowed for clean completions as
    /// well as partial texts on Cancelled or Failed sessions.
    /// Automatic copy, TTS, and starring remain strictly prohibited for partials.
    /// </summary>
    public bool CanCopyPartial => HasPartialText;

    public bool CanCopy => CanPerformResultActions || CanCopyPartial;

    /// <summary>
    /// A05: automatic side effects require the window to be visible. The
    /// panel keeps this in sync with its real visibility; a completion that
    /// lands while hidden never auto-copies, and restoring the window later
    /// does not replay the missed copy.
    /// </summary>
    public bool WindowVisible { get; set; } = true;

    /// <summary>
    /// Automatic clipboard copy is gated on: the user preference, a clean
    /// final success, non-empty text, AND window visibility (A05).
    /// </summary>
    public bool ShouldTriggerAutoCopy(bool autoCopySettingEnabled) =>
        autoCopySettingEnabled && WindowVisible &&
        Stage == TranslationPanelStage.Completed && !string.IsNullOrWhiteSpace(StreamedText);

    public bool HasPartialText =>
        (Stage is TranslationPanelStage.CancelledWithPartial or TranslationPanelStage.FailedWithPartial) &&
        !string.IsNullOrWhiteSpace(StreamedText);

    public string? GetPartialWarningBanner() => Stage switch
    {
        TranslationPanelStage.CancelledWithPartial => "已取消，内容不完整",
        TranslationPanelStage.FailedWithPartial => "生成中断，内容不完整",
        _ => null,
    };
}
