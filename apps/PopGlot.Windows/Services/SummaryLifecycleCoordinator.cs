using System.Threading;
using System.Threading.Tasks;

namespace PopGlot.Windows.Services;

/// <summary>
/// Result of an asynchronously completed summary request.
/// </summary>
internal sealed record SummaryExecutionResult(
    SummaryRequestIdentity Identity,
    string SummaryText,
    string Notes,
    ulong ElapsedMs,
    string EngineLabel,
    bool IsCurrent);

/// <summary>
/// Result when summary fails or is cancelled.
/// </summary>
internal sealed record SummaryErrorResult(
    SummaryRequestIdentity? Identity,
    string Message,
    bool WasCancelled,
    bool IsCurrent);

/// <summary>
/// Coordinates the asynchronous lifecycle of "extract summary / key points" requests (R09 / R01).
/// Enforces:
/// 1. Lifecycle isolation: canceling prior requests when inputs or languages change;
/// 2. Generation recency: discarding stale arrivals from UI display while safely retaining them in cache;
/// 3. Failure isolation: failures or cancellations never clobber or alter existing translations;
/// 4. Decoupling view code from async state machine management.
/// </summary>
internal sealed class SummaryLifecycleCoordinator
{
    private long _generation;
    private CancellationTokenSource? _activeCts;
    private SummaryRequestIdentity? _activeIdentity;
    private string _activeText = string.Empty;
    private readonly Lock _gate = new();

    public long CurrentGeneration => Volatile.Read(ref _generation);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _activeCts is { IsCancellationRequested: false };
            }
        }
    }

    public bool TryGetRunningSummary(string source, string sourceLanguage, string targetLanguage, out string text)
    {
        SummaryRequestIdentity? identity;
        lock (_gate)
        {
            identity = _activeCts is { IsCancellationRequested: false } ? _activeIdentity : null;
            text = _activeText;
        }
        if (identity is null || identity.Source != source.Trim() ||
            identity.SourceLanguage != sourceLanguage || identity.TargetLanguage != targetLanguage ||
            identity.ConfigVersion != ProfileManager.Revision) return false;
        try
        {
            var template = CoreBridge.GetActivePromptTemplate();
            return identity.PromptTemplateId == template.Id && identity.PromptTemplateRevision == template.Revision;
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Cancels any active in-flight summary request and advances the generation counter.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _activeIdentity = null;
            _activeText = string.Empty;
            if (_activeCts is not null)
            {
                try
                {
                    _activeCts.Cancel();
                    _activeCts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                _activeCts = null;
            }
        }
    }

    internal void AdoptOperation(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            _activeCts = cts;
        }
    }

    /// <summary>
    /// Executes the summary request with full lifecycle, recency and caching management.
    /// onDelta receives only the accumulated model body while this request's
    /// generation, source, target and route remain current. It is transient:
    /// only a complete terminal response enters the successful summary cache.
    /// </summary>
    public async Task ExecuteSummaryAsync(
        TranslationCoordinator coordinator,
        ReadingModeState readingState,
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        Func<bool> isHoldingSummary,
        Func<string> getCurrentSource,
        Func<string> getCurrentTargetLanguage,
        Action onStarting,
        Action<SummaryExecutionResult> onSuccess,
        Action<SummaryErrorResult> onError,
        Action<string>? onDelta = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readingState);
        ArgumentNullException.ThrowIfNull(sourceText);

        // A cache hit is still a new request: it must stop the previous
        // network operation instead of merely hiding its eventual result.
        Cancel();
        var gen = CurrentGeneration;
        SummaryRequestSnapshot snapshot;
        try
        {
            snapshot = coordinator.CreateSummarySnapshot(
                sourceText, sourceLanguage, targetLanguage, TextTaskKind.Summarize, gen);
        }
        catch (Exception exception)
        {
            // Route changes during local snapshot preparation are expected.
            // No transport exists yet, but the owning view still needs a terminal.
            onError(new SummaryErrorResult(
                Identity: null,
                Message: exception is OperationCanceledException ? ReadingRequestCopy.SummaryCancelled : exception.Message,
                WasCancelled: exception is OperationCanceledException,
                IsCurrent: gen == CurrentGeneration && isHoldingSummary() &&
                    string.Equals(getCurrentSource().Trim(), sourceText.Trim(), StringComparison.Ordinal) &&
                    string.Equals(getCurrentTargetLanguage(), targetLanguage, StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // 1. Check cache first
        if (readingState.TryGetSummary(snapshot.Identity, out var cached))
        {
            readingState.ShowSummary(snapshot.Identity);
            onSuccess(new SummaryExecutionResult(
                Identity: snapshot.Identity,
                SummaryText: cached.Text,
                Notes: cached.Note,
                ElapsedMs: cached.ElapsedMs,
                EngineLabel: cached.EngineLabel,
                IsCurrent: true));
            return;
        }

        // 2. Own the cancellation source through every exit, including a
        // failing onStarting callback and a normally completed request.
        CancellationTokenSource operation;
        CancellationToken operationToken;
        lock (_gate)
        {
            operation = new CancellationTokenSource();
            _activeCts = operation;
            operationToken = operation.Token;
            _activeIdentity = snapshot.Identity;
            _activeText = string.Empty;
        }

        try
        {
            onStarting();
            var response = await coordinator.RunSummaryTaskAsync(
                snapshot,
                operationToken,
                onDelta: accumulated =>
                {
                    // Only this operation may publish text captured for its
                    // frozen inputs and route, including after cancellation.
                    if (snapshot.Generation != Volatile.Read(ref _generation) ||
                        snapshot.Identity.ConfigVersion != ProfileManager.Revision ||
                        !string.Equals(getCurrentSource().Trim(), snapshot.Identity.Source, StringComparison.Ordinal) ||
                        !string.Equals(getCurrentTargetLanguage(), snapshot.Identity.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    lock (_gate)
                    {
                        if (!ReferenceEquals(_activeCts, operation) || operation.IsCancellationRequested)
                        {
                            return;
                        }
                        _activeText = accumulated;
                    }
                    if (isHoldingSummary()) onDelta?.Invoke(accumulated);
                });

            lock (_gate)
            {
                if (operation.IsCancellationRequested || !ReferenceEquals(_activeCts, operation))
                {
                    return;
                }
            }
            if (response.Result.IsPartial || string.IsNullOrWhiteSpace(response.Result.TranslatedText))
            {
                throw new InvalidOperationException("要点未完整返回；已生成的片段不会保存为成功结果，请重试。");
            }

            var note = string.Join(
                "\n",
                new[] { response.Result.Explanation }
                    .Concat(response.Result.Warnings)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Trim()));

            var currentSource = getCurrentSource().Trim();
            var currentTarget = getCurrentTargetLanguage();
            var isCurrent = snapshot.Generation == Volatile.Read(ref _generation) &&
                            snapshot.Identity.ConfigVersion == ProfileManager.Revision &&
                            string.Equals(currentSource, snapshot.Identity.Source, StringComparison.Ordinal) &&
                            string.Equals(currentTarget, snapshot.Identity.TargetLanguage, StringComparison.OrdinalIgnoreCase);

            var show = isHoldingSummary() && isCurrent;
            readingState.RememberSummary(
                snapshot.Identity,
                response.Result.TranslatedText,
                note,
                show,
                response.ElapsedMs,
                response.EngineLabel);

            onSuccess(new SummaryExecutionResult(
                Identity: snapshot.Identity,
                SummaryText: response.Result.TranslatedText,
                Notes: note,
                ElapsedMs: response.ElapsedMs,
                EngineLabel: response.EngineLabel,
                IsCurrent: show));
        }
        catch (OperationCanceledException)
        {
            var isCurrent = isHoldingSummary() &&
                            snapshot.Generation == Volatile.Read(ref _generation) &&
                            snapshot.Identity.ConfigVersion == ProfileManager.Revision &&
                            string.Equals(getCurrentSource().Trim(), snapshot.Identity.Source, StringComparison.Ordinal) &&
                            string.Equals(getCurrentTargetLanguage(), snapshot.Identity.TargetLanguage, StringComparison.OrdinalIgnoreCase);
            onError(new SummaryErrorResult(
                Identity: snapshot.Identity,
                Message: ReadingRequestCopy.SummaryCancelled,
                WasCancelled: true,
                IsCurrent: isCurrent));
        }
        catch (Exception ex)
        {
            var isCurrent = isHoldingSummary() &&
                            snapshot.Generation == Volatile.Read(ref _generation) &&
                            snapshot.Identity.ConfigVersion == ProfileManager.Revision &&
                            string.Equals(getCurrentSource().Trim(), snapshot.Identity.Source, StringComparison.Ordinal) &&
                            string.Equals(getCurrentTargetLanguage(), snapshot.Identity.TargetLanguage, StringComparison.OrdinalIgnoreCase);
            onError(new SummaryErrorResult(
                Identity: snapshot.Identity,
                Message: ex.Message,
                WasCancelled: false,
                IsCurrent: isCurrent));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCts, operation))
                {
                    _activeCts = null;
                    _activeIdentity = null;
                    _activeText = string.Empty;
                }
            }
            operation.Dispose();
        }
    }
}
