using System.Text.RegularExpressions;

namespace PopGlot.Windows.Services;

/// <summary>
/// 统一的密钥脱敏器（0.1.35）。
///
/// 你填进来的 API Key 只应出现在两个地方：Windows 凭据管理器，以及发给模型
/// 服务的那个请求头。它决不能出现在日志、诊断导出、备份包、GitHub Issue
/// 正文或界面报错里。这里提供两道防线：
/// <list type="number">
///   <item><b>已知值</b>：凭据读写的那一刻把真实密钥登记进来，之后任何文本
///   里出现该字符串一律替换成掩码——不依赖它长得像不像密钥。</item>
///   <item><b>形状匹配</b>：sk-/ghp_/github_pat_/Bearer/… 之类常见形态，以及
///   <c>api_key=</c>、<c>token:</c>、URL 里的 <c>user:pass@</c> 与签名查询
///   参数，覆盖"从别处复制进来"的密钥。</item>
/// </list>
/// 白名单式的结构化日志（DiagnosticsLog）仍然优先：能不写自由文本就不写。
/// </summary>
internal static partial class SecretRedactor
{
    public const string Mask = "[redacted-secret]";

    /// <summary>短于此长度的登记值不参与匹配，避免把 "1234" 这类公共子串抹掉。</summary>
    private const int MinimumSecretLength = 6;

    private const int MaxTrackedSecrets = 64;

    private static readonly object Gate = new();
    private static readonly List<string> KnownSecrets = new();

    /// <summary>
    /// 登记一个真实密钥。重复登记是幂等的；超出上限时丢掉最旧的一个，
    /// 绝不因为登记失败而让主流程出错。
    /// </summary>
    public static void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumSecretLength)
        {
            return;
        }
        lock (Gate)
        {
            if (KnownSecrets.Contains(secret, StringComparer.Ordinal))
            {
                return;
            }
            if (KnownSecrets.Count >= MaxTrackedSecrets)
            {
                KnownSecrets.RemoveAt(0);
            }
            KnownSecrets.Add(secret);
        }
    }

    /// <summary>移除登记（删除密钥、切换账号时用）。</summary>
    public static void Forget(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }
        lock (Gate)
        {
            KnownSecrets.RemoveAll(value => string.Equals(value, secret, StringComparison.Ordinal));
        }
    }

    /// <summary>Test seam: clears every registered value.</summary>
    internal static void ResetForTest()
    {
        lock (Gate)
        {
            KnownSecrets.Clear();
        }
    }

    /// <summary>True when the text carries a registered secret or a secret shape.</summary>
    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && Redact(text) != text;

    /// <summary>
    /// 返回脱敏后的文本：已知值 → 掩码；密钥形态 → 掩码。任何异常都不能
    /// 打断调用方（日志与导出路径宁可少抹一处，也不能崩）。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var working = text;
        try
        {
            string[] snapshot;
            lock (Gate)
            {
                snapshot = KnownSecrets.ToArray();
            }
            foreach (var secret in snapshot)
            {
                if (working.Contains(secret, StringComparison.Ordinal))
                {
                    working = working.Replace(secret, Mask, StringComparison.Ordinal);
                }
            }

            working = BearerRegex().Replace(working, "Bearer " + Mask);
            working = ProviderTokenRegex().Replace(working, Mask);
            working = KeyValueRegex().Replace(working, "$1" + Mask);
            working = UrlCredentialRegex().Replace(working, "$1" + Mask + "@$2");
            working = QuerySecretRegex().Replace(working, "$1=" + Mask);
        }
        catch (Exception)
        {
            // 脱敏失败时返回已处理到的中间结果，绝不抛出。
        }
        return working;
    }

    // sk- / sk-ant- / sk-proj- / ghp_ / gho_ / github_pat_ / xoxb- / AIza
    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_\-]{8,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[A-Za-z0-9_\-]{20,})",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProviderTokenRegex();

    [GeneratedRegex(@"\b(Bearer)\s+[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    // api_key: "..." / "token": "..." / password=... / Authorization: ...
    [GeneratedRegex(@"([\""']?(?:api[-_]?key|apikey|access[-_]?token|refresh[-_]?token|token|secret|password|authorization)[\""']?\s*[:=]\s*)([\""']?[^\s,;}\]]{6,}[\""']?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    // https://user:password@host/
    [GeneratedRegex(@"(https?://[^\s:/@]+):[^\s/@]+@([^\s/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialRegex();

    // ?key=... &token=... &sig=...
    [GeneratedRegex(@"([?&](?:key|api_key|apikey|token|access_token|sig|signature|secret)=)[^&\s""]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuerySecretRegex();
}
