using System.Security.Cryptography;
using System.Text;

namespace PopGlot.Windows.Services;

internal enum VerificationApplyResult
{
    Applied,
    Missing,
    Stale,
    Busy,
    Cancelled,
}

/// <summary>
/// Identity of one connection test. The credential slot is a revision counter,
/// never the key and never a hash of the key.
/// </summary>
internal readonly record struct VerificationLease(
    string ProfileId,
    string ConnectionVersion,
    int CredentialRevision,
    string EvidenceFingerprint);

/// <summary>
/// What a later save may keep after an editor test. Null fields mean the old
/// healthy mark must not be copied onto the new draft.
/// </summary>
internal readonly record struct EditorEvidenceDecision(
    string? Outcome,
    DateTime? TestedAtUtc,
    string? Fingerprint);

internal static class ProfileVerification
{
    public static string ConnectionVersion(ProviderProfile profile)
    {
        var material = string.Join('\n',
            profile.ProviderType,
            NormalizeBase(profile.ApiBaseUrl),
            (profile.TextEndpoint ?? string.Empty).Trim(),
            (profile.TextModel ?? string.Empty).Trim(),
            HeaderText(profile.ExtraHeaders),
            (profile.AnthropicVersion ?? string.Empty).Trim(),
            profile.AllowInsecureTls);
        return Hash(material);
    }

    /// <summary>
    /// Evidence binding for the connection and credential revision that were
    /// actually tested. Changing the model, address, or credential revision
    /// produces a different value. The key itself is not an input.
    /// </summary>
    public static string EvidenceFingerprint(ProviderProfile profile, bool credentialAvailable) =>
        Hash(string.Join('\n',
            ConnectionVersion(profile),
            profile.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            credentialAvailable));

    public static VerificationLease Capture(ProviderProfile profile, bool credentialAvailable) =>
        new(
            profile.Id,
            ConnectionVersion(profile),
            profile.CredentialRevision,
            EvidenceFingerprint(profile, credentialAvailable));

    /// <summary>
    /// Lease for a key that is typed but not saved yet. The saved profile stays
    /// at its current revision, so applying this lease cannot stamp the old row.
    /// </summary>
    public static VerificationLease CaptureForProspectiveCredential(ProviderProfile profile)
    {
        var copy = profile.Clone();
        copy.CredentialRevision = profile.CredentialRevision + 1;
        return Capture(copy, credentialAvailable: true);
    }

    public static EditorEvidenceDecision Decide(
        VerificationLease? pending,
        string? pendingOutcome,
        DateTime? pendingTestedAtUtc,
        ProviderProfile? existing,
        ProviderProfile draft,
        bool keyWritten,
        bool credentialAvailable)
    {
        var fingerprint = EvidenceFingerprint(draft, credentialAvailable);
        if (pending is { } lease &&
            pendingTestedAtUtc is not null &&
            !string.IsNullOrWhiteSpace(pendingOutcome) &&
            string.Equals(lease.EvidenceFingerprint, fingerprint, StringComparison.Ordinal) &&
            lease.CredentialRevision == draft.CredentialRevision &&
            string.Equals(lease.ConnectionVersion, ConnectionVersion(draft), StringComparison.Ordinal))
        {
            return new EditorEvidenceDecision(pendingOutcome, pendingTestedAtUtc, lease.EvidenceFingerprint);
        }

        if (!keyWritten &&
            existing is not null &&
            string.Equals(existing.LastTestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new EditorEvidenceDecision(
                existing.LastTestOutcome,
                existing.LastTestedAtUtc,
                existing.LastTestFingerprint);
        }

        return new EditorEvidenceDecision(null, null, null);
    }

    /// <summary>The saved-key mask is a placeholder, never a credential.</summary>
    public static bool IsMaskNotAKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var ch in key.Trim())
        {
            if (ch is not ('•' or '*' or '·' or '●'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool CredentialAvailable(ProviderProfile profile)
    {
        if (ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl))
        {
            return true;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(profile.CredentialTarget) &&
                CredentialStore.HasApiKey(profile.CredentialTarget))
            {
                return true;
            }

            var activeId = ProfileManager.Load().ActiveProfileId;
            return profile.Id == activeId &&
                CredentialStore.HasApiKey(CredentialStore.DefaultTargetName);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string NormalizeBase(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : trimmed;
    }

    private static string HeaderText(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return string.Empty;
        }

        return string.Join('\n', headers
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key.Trim().ToLowerInvariant()}:{pair.Value.Trim()}"));
    }

    private static string Hash(string material) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
}

/// <summary>Short, stable labels for a connection-test failure. Detail stays separate.</summary>
internal static class ConnectionTestDiagnostics
{
    public static string Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return "cancel";
        }

        var message = exception.Message ?? string.Empty;
        if (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("403", StringComparison.Ordinal) ||
            message.Contains("鉴权失败", StringComparison.Ordinal) ||
            message.Contains("密钥无效", StringComparison.Ordinal))
        {
            return "auth";
        }

        if (message.Contains("429", StringComparison.Ordinal) ||
            message.Contains("额度", StringComparison.Ordinal) ||
            message.Contains("限流", StringComparison.Ordinal))
        {
            return "rate";
        }

        if (message.Contains("404", StringComparison.Ordinal) &&
            message.Contains("模型", StringComparison.Ordinal))
        {
            return "model";
        }

        if (message.Contains("404", StringComparison.Ordinal))
        {
            return "endpoint";
        }

        if (message.Contains("超时", StringComparison.Ordinal) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "timeout";
        }

        if (message.Contains("未知的主机", StringComparison.Ordinal) ||
            message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase))
        {
            return "endpoint";
        }

        if (message.Contains("SSE", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("协议", StringComparison.Ordinal) ||
            (message.Contains("JSON", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("流", StringComparison.Ordinal)))
        {
            return "protocol";
        }

        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("网络访问未启用", StringComparison.Ordinal))
        {
            return "unreachable";
        }

        return "fail";
    }

    public static string ShortLabel(string? code) => code switch
    {
        "ok" => "上次成功",
        "auth" => "密钥无效",
        "model" => "模型不可用",
        "endpoint" => "地址错误",
        "rate" => "无额度/限流",
        "timeout" => "超时",
        "protocol" => "协议响应异常",
        "unreachable" => "地址错误",
        "cancel" => "已取消",
        _ => "测试失败",
    };
}

internal static class ModelCatalogMessages
{
    public static string DescribeLoad(int count) =>
        count <= 0
            ? "未找到模型，可手动输入"
            : $"模型目录已更新 · {count} 个";
}
