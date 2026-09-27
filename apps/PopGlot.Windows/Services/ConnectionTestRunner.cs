namespace PopGlot.Windows.Services;

/// <summary>
/// Runs one list connection test and writes verification fields only when the
/// engine id, connection version, and credential revision still match.
/// The same engine cannot enter twice; different engines can.
/// </summary>
internal static class ConnectionTestRunner
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> InFlight = new(StringComparer.Ordinal);

    public static void ResetForTests()
    {
        lock (Gate)
        {
            InFlight.Clear();
        }
    }

    public static bool IsInFlight(string profileId)
    {
        lock (Gate)
        {
            return InFlight.Contains(profileId);
        }
    }

    public static bool TryEnter(string profileId)
    {
        lock (Gate)
        {
            return InFlight.Add(profileId);
        }
    }

    public static void Exit(string profileId)
    {
        lock (Gate)
        {
            InFlight.Remove(profileId);
        }
    }

    public static async Task<ListTestCompletion> RunListTestAsync(
        string profileId,
        Func<ProviderProfile, Task<string>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (!TryEnter(profileId))
        {
            return new ListTestCompletion(VerificationApplyResult.Busy, "busy", null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = ProfileManager.Load();
            var profile = config.Profiles.FirstOrDefault(item => item.Id == profileId);
            if (profile is null)
            {
                return new ListTestCompletion(VerificationApplyResult.Missing, "missing", null);
            }

            var lease = ProfileVerification.Capture(profile, ProfileVerification.CredentialAvailable(profile));
            string outcome;
            Exception? error = null;
            try
            {
                outcome = await execute(profile.Clone()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new ListTestCompletion(VerificationApplyResult.Cancelled, "cancel", null);
            }
            catch (Exception exception)
            {
                outcome = ConnectionTestDiagnostics.Classify(exception);
                if (outcome == "cancel")
                {
                    return new ListTestCompletion(VerificationApplyResult.Cancelled, "cancel", null);
                }

                error = exception;
            }

            // execute may not support cancellation itself. A cancellation that
            // arrives while it is running must still prevent stale evidence.
            if (cancellationToken.IsCancellationRequested)
            {
                return new ListTestCompletion(VerificationApplyResult.Cancelled, "cancel", null);
            }

            if (string.IsNullOrWhiteSpace(outcome))
            {
                outcome = "fail";
            }

            var applied = ProfileManager.TryPatchVerification(lease, outcome, DateTime.UtcNow);
            return new ListTestCompletion(applied, outcome, error);
        }
        finally
        {
            Exit(profileId);
        }
    }
}

internal readonly record struct ListTestCompletion(
    VerificationApplyResult Apply,
    string Outcome,
    Exception? Error);
