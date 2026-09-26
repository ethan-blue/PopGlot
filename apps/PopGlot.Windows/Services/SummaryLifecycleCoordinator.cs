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
    SummaryRequestIdentity Identity,
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

    /// <summary>
    /// Cancels any active in-flight summary request and advances the generation counter.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
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
        Action<SummaryErrorResult> onError)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(readingState);
        ArgumentNullException.ThrowIfNull(sourceText);

        var gen = Interlocked.Increment(ref _generation);
        var snapshot = coordinator.CreateSummarySnapshot(
            sourceText, sourceLanguage, targetLanguage, TextTaskKind.Summarize, gen);

        // 1. Check cache first
        if (readingState.TryGetSummary(snapshot.Identity, out var cached))
        {
            readingState.ShowSummary(snapshot.Identity);
            onSuccess(new SummaryExecutionResult(
                Identity: snapshot.Identity,
                SummaryText: cached.Text,
                Notes: cached.Note,
                ElapsedMs: 0UL,
                EngineLabel: string.Empty,
                IsCurrent: true));
            return;
        }

        // 2. Cancel prior operation
        CancellationTokenSource operation;
        lock (_gate)
        {
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
            }
            operation = new CancellationTokenSource();
            _activeCts = operation;
        }

        onStarting();

        try
        {
            var response = await coordinator.RunSummaryTaskAsync(snapshot, operation.Token);

            lock (_gate)
            {
                if (operation.IsCancellationRequested || !ReferenceEquals(_activeCts, operation))
                {
                    return;
                }
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
                            string.Equals(currentSource, snapshot.Identity.Source, StringComparison.Ordinal) &&
                            string.Equals(currentTarget, snapshot.Identity.TargetLanguage, StringComparison.OrdinalIgnoreCase);

            var show = isHoldingSummary() && isCurrent;
            readingState.RememberSummary(snapshot.Identity, response.Result.TranslatedText, note, show);

            onSuccess(new SummaryExecutionResult(
                Identity: snapshot.Identity,
                SummaryText: response.Result.TranslatedText,
                Notes: note,
                ElapsedMs: response.Diagnostics.ElapsedMs,
                EngineLabel: response.EngineLabel,
                IsCurrent: show));
        }
        catch (OperationCanceledException)
        {
            var isCurrent = isHoldingSummary() && snapshot.Generation == Volatile.Read(ref _generation);
            onError(new SummaryErrorResult(
                Identity: snapshot.Identity,
                Message: ReadingRequestCopy.SummaryCancelled,
                WasCancelled: true,
                IsCurrent: isCurrent));
        }
        catch (Exception ex)
        {
            var isCurrent = isHoldingSummary() && snapshot.Generation == Volatile.Read(ref _generation);
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
                }
            }
        }
    }
}
