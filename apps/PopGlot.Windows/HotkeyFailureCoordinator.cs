namespace PopGlot.Windows;

/// <summary>
/// Typed failure context of an ApplyShellSettings attempt. Each surface
/// (settings window inline status, tray, workbench footer, balloon) decides
/// how loudly to report from this evidence — never from a global boolean
/// guess about "some" failure.
/// </summary>
internal enum ShellApplyFailureKind
{
    /// <summary>Settings applied and hotkeys registered.</summary>
    None = 0,

    /// <summary>
    /// The candidate set itself is invalid (incomplete or duplicated
    /// combinations) — registration was never attempted.
    /// </summary>
    InvalidHotkeys,

    /// <summary>
    /// Settings-page probe: the candidate lost the registration race, but
    /// the previous set is fully live again. Only the settings window owns
    /// this failure (inline) — no global unavailable state, no balloon and
    /// no workbench transient may be raised for it.
    /// </summary>
    HotkeyConflictRestored,

    /// <summary>
    /// The process is left without its full working hotkey set — a real
    /// global failure every surface must reflect until recovery.
    /// </summary>
    HotkeyUnavailable,
}

/// <summary>Result of applying a shell settings snapshot.</summary>
internal sealed record ShellApplyOutcome(
    bool Applied,
    ShellApplyFailureKind Failure = ShellApplyFailureKind.None,
    string? ConflictDetail = null)
{
    public static ShellApplyOutcome Ok() => new(true);

    public static ShellApplyOutcome Failed(ShellApplyFailureKind failure, string? conflictDetail) =>
        new(false, failure, conflictDetail);
}

/// <summary>How loudly a newly reported hotkey failure should surface.</summary>
internal enum HotkeyFailureDecision
{
    /// <summary>Already surfaced this cycle with the same detail: stay quiet.</summary>
    None = 0,

    /// <summary>First failure of a cycle: the one balloon this cycle gets.</summary>
    Balloon,

    /// <summary>
    /// Same cycle, changed detail: status surfaces may refresh, but another
    /// balloon would only flood the tray — the cycle already announced its
    /// first failure.
    /// </summary>
    UpdateStatusOnly,
}

/// <summary>
/// Pure state machine behind hotkey-failure notification dedup. One failure
/// cycle — from the first failure until recovery — balloons at most once;
/// a changed detail refreshes the status surfaces without re-ballooning;
/// recovery closes the cycle so a future failure can notify again. Kept
/// free of WPF/tray dependencies so the dedup contract is directly
/// testable; <see cref="App"/> owns the single production instance.
/// </summary>
internal sealed class HotkeyFailureCoordinator
{
    /// <summary>Detail of the failure that opened the active cycle, or null.</summary>
    public string? ActiveDetail { get; private set; }

    /// <summary>True between the first failure of a cycle and its recovery.</summary>
    public bool FailureActive => ActiveDetail is not null;

    /// <summary>True once the active cycle has had its single balloon.</summary>
    public bool BalloonShownThisCycle { get; private set; }

    /// <summary>
    /// Records a global failure (the process really lost its hotkeys) and
    /// decides how loudly it must surface.
    /// </summary>
    public HotkeyFailureDecision ReportFailure(string detail)
    {
        if (FailureActive)
        {
            if (string.Equals(ActiveDetail, detail, StringComparison.Ordinal))
            {
                // Same persistent failure reported again (every retry will
                // do so): one balloon per cycle is the whole contract.
                return HotkeyFailureDecision.None;
            }
            // The failure shape changed within one cycle (e.g. a different
            // combination turned out to be the blocker): refresh surfaces,
            // but do not balloon again — that would flood the tray.
            ActiveDetail = detail;
            return HotkeyFailureDecision.UpdateStatusOnly;
        }
        ActiveDetail = detail;
        BalloonShownThisCycle = true;
        return HotkeyFailureDecision.Balloon;
    }

    /// <summary>
    /// Records a recovery. Returns true when an active cycle actually
    /// closed — the caller clears the failure surfaces so a future failure
    /// opens a fresh, re-notifying cycle.
    /// </summary>
    public bool ReportRecovery()
    {
        if (!FailureActive)
        {
            return false;
        }
        ActiveDetail = null;
        BalloonShownThisCycle = false;
        return true;
    }
}

/// <summary>Who owns reporting a failed settings apply, decided by evidence.</summary>
internal enum ShellApplyFailureRoute
{
    /// <summary>
    /// The previous set is fully live again: a probe failure the settings
    /// window reports inline — no global failure exists at all.
    /// </summary>
    ProbeInlineOnly = 0,

    /// <summary>
    /// The service's RegistrationFailed event already reported this attempt
    /// with the honest combined detail; an App-side re-report could only
    /// downgrade that detail on the status surfaces.
    /// </summary>
    ServiceReported = 1,

    /// <summary>
    /// Nobody reported this failure yet (the previous set was empty, so its
    /// restore trivially succeeded — e.g. at startup, or an already fully
    /// dead set): the App must report, whether or not a failure cycle is
    /// already open — dedup is the coordinator's job, not the caller's.
    /// </summary>
    AppMustReport = 2,
}

/// <summary>
/// Pure classifier for routing a failed settings apply, extracted from
/// <see cref="App.TryApplyShellSettings"/> so the routing contract is
/// directly testable. The pre-fix bug suppressed the App-side report
/// whenever a failure cycle was already open, so a SECOND, different
/// conflict inside one open cycle never reached the coordinator and
/// MainWindow/tray kept the stale first detail forever. The honest inputs
/// are only service evidence — cycle state is deliberately NOT one:
/// <see cref="HotkeyFailureCoordinator"/> already collapses repeats.
/// </summary>
internal static class ShellApplyFailureRouting
{
    public static ShellApplyFailureRoute Classify(
        bool previousSetFullyAvailable,
        bool serviceAlreadyReportedThisAttempt) =>
        previousSetFullyAvailable
            ? ShellApplyFailureRoute.ProbeInlineOnly
            : serviceAlreadyReportedThisAttempt
                ? ShellApplyFailureRoute.ServiceReported
                : ShellApplyFailureRoute.AppMustReport;
}
