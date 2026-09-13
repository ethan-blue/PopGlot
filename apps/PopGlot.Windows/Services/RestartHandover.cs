namespace PopGlot.Windows.Services;

internal sealed record RestartHandoverResult(
    bool Launched,
    int AttemptsUsed,
    string? LastErrorZh);

/// <summary>
/// V05: the observable result of ONE launch attempt. Process.Start
/// returning is not readiness — <see cref="Ready"/> is true only after the
/// attempt's OWN unique readiness event was signalled by the new instance
/// at the agreed boundary (hotkeys registered + tray + listener live).
/// </summary>
/// <param name="Ready">The new instance confirmed it is up.</param>
/// <param name="ErrorZh">Why the attempt failed; null when ready.</param>
/// <param name="LimboUnconfirmed">
/// True when the attempt ended with a child whose termination could NOT be
/// confirmed — the handover must then refuse any further launch.
/// </param>
internal sealed record LaunchOutcome(bool Ready, string? ErrorZh, bool LimboUnconfirmed = false);

/// <summary>
/// V05: the restart handshake as an observable, bounded, adapter-driven
/// procedure. Reordering cleanup calls is not a handshake: the old process
/// must SEE whether a new process actually started, report failures, give
/// the user a retry decision, cap the attempts, and refuse a duplicate
/// handover while one is already in flight.
/// </summary>
internal static class RestartHandover
{
    private static int _running;

    /// <summary>
    /// V05: a readiness event name UNIQUE to one attempt. A fixed shared
    /// name (like the show-window signal) is not attempt-bound: stale events
    /// from an older attempt, or an event created by another instance, would
    /// falsely confirm readiness — and it fires long before the hotkeys are
    /// registered. The new instance creates and sets THIS name at the agreed
    /// production-ready boundary.
    /// </summary>
    public static string NewReadyEventName() =>
        @"Local\PopGlot.RestartReady." + Guid.NewGuid().ToString("N");

    /// <summary>V05 duplicate-request guard: only one handover at a time.</summary>
    public static bool TryBegin() => Interlocked.Exchange(ref _running, 1) == 0;

    public static void End() => Volatile.Write(ref _running, 0);

    /// <summary>Visible to tests so a stuck caller can be detected.</summary>
    public static bool InFlight => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// V05: the termination result of a LATE spawn task is observed and
    /// saved here — hanging a callback is not the same as confirming exit,
    /// and a failed cleanup must stay visible in the handover state.
    /// </summary>
    public static string? LastLimboCleanupError;

    private static void SaveLimboCleanupError(string? error)
    {
        if (error is not null)
        {
            LastLimboCleanupError = error;
        }
    }

    /// <summary>
    /// V05: one attempt-bound launch with readiness confirmation, fully
    /// adapter-driven so timing scenarios are reproducible without a real
    /// process. The spawn call runs with a bounded wait; a LATE spawn is not
    /// left unmanaged — a continuation terminates and confirms whatever it
    /// eventually produces. Readiness is polled ONLY on this attempt's own
    /// unique event name. A kill that cannot be confirmed is reported
    /// honestly (<see cref="LaunchOutcome.LimboUnconfirmed"/>).
    /// </summary>
    public static LaunchOutcome LaunchAndWaitReady(
        string readyEventName,
        int spawnTimeoutMs,
        int readyTimeoutMs,
        Func<object?> spawn,
        Func<object, bool> hasExited,
        Func<object, int> exitCodeOf,
        Func<object, string?> terminateAndConfirm,
        Func<int, bool> waitReadySignal)
    {
        var spawnTask = Task.Run(spawn);
        if (!spawnTask.Wait(spawnTimeoutMs))
        {
            // V05 round 2: the spawn task is still running — a child may
            // appear at any moment, so the handover is UNCONFIRMED and the
            // driver must not launch again. The late spawn is not left
            // unmanaged: a continuation terminates and CONFIRMS whatever it
            // eventually produces, and a failed cleanup is saved. The OLD
            // process owns this continuation; if it dies first, the late
            // child falls back to the single-instance guard of the new
            // instance (it bounces and exits on its own).
            spawnTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result is not null)
                {
                    SaveLimboCleanupError(terminateAndConfirm(t.Result));
                }
            });
            return new LaunchOutcome(false, "启动调用超时；迟到的启动任务已纳入受管清理", true);
        }
        if (spawnTask.Result is null)
        {
            return new LaunchOutcome(false, "新进程未能启动", false);
        }
        var process = spawnTask.Result;
        var deadline = Environment.TickCount64 + readyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (hasExited(process))
            {
                return new LaunchOutcome(false,
                    $"新进程在就绪确认前退出（退出码 {exitCodeOf(process)}）", false);
            }
            // V05 round 2: readiness is the SIGNAL STATE of this attempt's
            // own event — not the mere existence of a named object.
            var slice = Math.Min(200, deadline - Environment.TickCount64);
            if (slice > 0 && waitReadySignal((int)slice))
            {
                return new LaunchOutcome(true, null, false);
            }
        }
        var terminationError = terminateAndConfirm(process);
        if (terminationError is not null)
        {
            return new LaunchOutcome(false,
                $"就绪确认超时，且终止未就绪进程失败：{terminationError}", true);
        }
        return new LaunchOutcome(false, "就绪确认超时；未就绪的新进程已确认终止", false);
    }

    /// <summary>
    /// One bounded handover. Every step goes through an adapter so failure,
    /// timeout and cleanup faults are observable results, not swallowed
    /// exceptions. Releasing the mutex FAILING is an attempt failure — it is
    /// never interpreted as "another instance took over". A launch attempt
    /// succeeds only when its OWN readiness confirmation passes.
    /// </summary>
    /// <param name="maxAttempts">Attempt budget; the handover never exceeds it.</param>
    /// <param name="cleanupOldInstance">Teardown of hotkeys/windows/TTS/signals.</param>
    /// <param name="releaseMutex">Single-instance handover; false = the release itself failed.</param>
    /// <param name="launchNew">Attempt-bound readiness confirmation.</param>
    /// <param name="askRetryAfterFailure">User decision after a failed attempt.</param>
    public static RestartHandoverResult Run(
        int maxAttempts,
        Func<bool> cleanupOldInstance,
        Func<bool> releaseMutex,
        Func<LaunchOutcome> launchNew,
        Func<int, bool> askRetryAfterFailure)
    {
        var attempts = 0;
        string? lastError = null;
        var cleanupFaulted = false;
        while (attempts < maxAttempts)
        {
            attempts++;
            // Cleanup faults are recorded but do not stop the handover: a
            // wedged window must not block an ordered shutdown forever.
            if (!cleanupOldInstance())
            {
                cleanupFaulted = true;
            }
            if (!releaseMutex())
            {
                // V05: a release exception/failure is a FAILURE of this
                // attempt — the old instance still owns the single-instance
                // right, so restarting blindly is forbidden.
                return new RestartHandoverResult(false, attempts,
                    "单实例互斥体释放失败，无法安全交接" + (cleanupFaulted ? "（旧实例资源清理也未完全成功）" : ""));
            }
            LaunchOutcome outcome;
            try
            {
                outcome = launchNew();
            }
            catch (Exception exception)
            {
                outcome = new LaunchOutcome(false, $"启动确认异常：{exception.GetType().Name}");
            }
            if (outcome.Ready)
            {
                return new RestartHandoverResult(true, attempts, cleanupFaulted ? "旧实例资源清理未完全成功" : null);
            }
            lastError = outcome.ErrorZh ?? "新进程未能确认就绪";
            if (outcome.LimboUnconfirmed)
            {
                // V05: a child whose termination could not be confirmed may
                // still come up — launching the next attempt is FORBIDDEN
                // until its exit is confirmed, so the handover stops here.
                return new RestartHandoverResult(false, attempts,
                    lastError + "；未确认退出前禁止再次启动");
            }
            if (!askRetryAfterFailure(attempts))
            {
                return new RestartHandoverResult(false, attempts, lastError);
            }
        }
        return new RestartHandoverResult(false, attempts, lastError ?? "重启尝试预算已用尽");
    }
}
