using System.Diagnostics;

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
            return new ListTestCompletion(
                VerificationApplyResult.Busy, "busy", null, 0,
                FormatCompletionStatus(VerificationApplyResult.Busy, "busy", 0, string.Empty));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = ProfileManager.Load();
            var profile = config.Profiles.FirstOrDefault(item => item.Id == profileId);
            if (profile is null)
            {
                return new ListTestCompletion(
                    VerificationApplyResult.Missing, "missing", null, 0,
                    FormatCompletionStatus(VerificationApplyResult.Missing, "missing", 0, string.Empty));
            }

            var lease = ProfileVerification.Capture(profile, ProfileVerification.CredentialAvailable(profile));
            string outcome;
            Exception? error = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                outcome = await execute(profile.Clone()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new ListTestCompletion(
                    VerificationApplyResult.Cancelled, "cancel", null, stopwatch.ElapsedMilliseconds,
                    FormatCompletionStatus(VerificationApplyResult.Cancelled, "cancel", stopwatch.ElapsedMilliseconds, profile.Name));
            }
            catch (Exception exception)
            {
                outcome = ConnectionTestDiagnostics.Classify(exception);
                if (outcome == "cancel")
                {
                    stopwatch.Stop();
                    return new ListTestCompletion(
                        VerificationApplyResult.Cancelled, "cancel", null, stopwatch.ElapsedMilliseconds,
                        FormatCompletionStatus(VerificationApplyResult.Cancelled, "cancel", stopwatch.ElapsedMilliseconds, profile.Name));
                }

                error = exception;
            }
            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;

            // execute may not support cancellation itself. A cancellation that
            // arrives while it is running must still prevent stale evidence.
            if (cancellationToken.IsCancellationRequested)
            {
                return new ListTestCompletion(
                    VerificationApplyResult.Cancelled, "cancel", null, elapsedMs,
                    FormatCompletionStatus(VerificationApplyResult.Cancelled, "cancel", elapsedMs, profile.Name));
            }

            if (string.IsNullOrWhiteSpace(outcome))
            {
                outcome = "fail";
            }

            var applied = ProfileManager.TryPatchVerification(lease, outcome, DateTime.UtcNow);
            var statusText = FormatCompletionStatus(applied, outcome, elapsedMs, profile.Name);
            return new ListTestCompletion(applied, outcome, error, elapsedMs, statusText);
        }
        finally
        {
            Exit(profileId);
        }
    }

    internal static string FormatCompletionStatus(
        VerificationApplyResult apply,
        string outcome,
        long elapsedMs,
        string profileName)
    {
        var prefix = string.IsNullOrWhiteSpace(profileName) ? string.Empty : $"{profileName} · ";
        var latency = TranslationElapsedText.ForMilliseconds(elapsedMs);
        return apply switch
        {
            VerificationApplyResult.Busy => "正在测试连接，请稍候",
            VerificationApplyResult.Missing => "未找到该引擎配置",
            VerificationApplyResult.Cancelled => "测试已取消",
            VerificationApplyResult.Applied when outcome == "ok" => $"{prefix}连接正常（{latency}）",
            VerificationApplyResult.Applied => $"{prefix}{DescribeFailureOutcome(outcome)}（{latency}）",
            _ => $"{prefix}配置已变更，结果未保存",
        };
    }

    internal static string DescribeFailureOutcome(string outcome) => outcome switch
    {
        "auth" => "密钥无效或已过期",
        "rate" => "请求过于频繁，请稍后再试",
        "model" => "模型名称不可用",
        "endpoint" => "接口地址无法访问",
        "timeout" => "连接超时，请检查网络或代理",
        "protocol" => "返回格式不兼容",
        "unreachable" => "无法连接到服务器，请检查网络或代理",
        _ => "连接测试未通过",
    };
}

internal readonly record struct ListTestCompletion(
    VerificationApplyResult Apply,
    string Outcome,
    Exception? Error,
    long ElapsedMs = 0,
    string? StatusText = null);
