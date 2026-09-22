using System.Windows;

namespace PopGlot.Windows.Services;

internal enum FreeEngineConsent
{
    Unset = 0,
    Allowed = 1,
    Denied = 2,
}

internal enum FreeEngineDecision
{
    AlwaysAllow,
    AllowOnce,
    Deny,
}

/// <summary>
/// Proof that <see cref="OutboundPolicy"/> authorized free-engine traffic for
/// one request scope, carrying the exact network settings the decision was
/// made against. Issued only on the Allowed/AllowOnce paths; the last send
/// layer (<see cref="FreeTranslateService"/>) refuses to transmit without one,
/// so a UI or health-probe caller can never bypass the policy.
/// </summary>
/// <remarks>
/// C01: the authorization is a per-request token, never stored in a UI or
/// singleton field. <see cref="TryClaimSend"/> is the atomic send boundary —
/// an AllowOnce permission dies with its first real send, and every send
/// re-reads the live policy, so a revocation between the decision and a
/// later endpoint fallback stops the remaining sends.
/// </remarks>
internal sealed class FreeEngineAuthorization
{
    private int _consumed;

    public ProviderSettings Settings { get; }

    public bool IsOnceOnly { get; }

    public FreeEngineAuthorization(ProviderSettings settings, bool IsOnceOnly)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.IsOnceOnly = IsOnceOnly;
    }

    /// <summary>True once this authorization has backed a real send (AllowOnce only).</summary>
    public bool IsConsumed => Volatile.Read(ref _consumed) == 1;

    /// <summary>
    /// Atomically claims ONE real HTTP send at the transport boundary and
    /// re-reads the live policy. The live check runs BEFORE the consume, so
    /// a refused request never burns the permit; the consume is the last
    /// step before the transport call, so a failed send attempt stays
    /// consumed (no refund) but a request that never reaches submission
    /// does not. AllowOnce succeeds exactly once, and dies with an explicit
    /// later denial even though it was originally issued against Unset.
    /// </summary>
    public bool TryClaimSend(out string? refusal)
    {
        refusal = null;
        if (!OutboundPolicy.SendStillAllowed(Settings, IsOnceOnly, out refusal))
        {
            return false;
        }
        if (IsOnceOnly && Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            refusal = "“仅本次允许”已用于上一次发送；没有再次发送任何请求。可在「设置 → 隐私与数据」中选择始终允许。";
            return false;
        }
        return true;
    }
}

/// <summary>
/// The single authority on whether the built-in free web engine may send text.
/// Windows and services must never re-derive this from booleans themselves.
/// </summary>
internal static class OutboundPolicy
{
    public const string FreeEngineDestination =
        "你选中的公共翻译（Google translate.googleapis.com，或备用 MyMemory api.mymemory.translated.net）";

    /// <summary>
    /// Asked once per unset consent, before the first outbound free-engine
    /// request. The host installs a prompt with a window owner; headless
    /// callers leave it null, which fails closed.
    /// </summary>
    public static Func<string, FreeEngineDecision>? ConsentPrompt { get; set; }

    /// <summary>Test seam: where consent is persisted. Production uses the real store.</summary>
    internal static Func<ShellSettings> SettingsLoader { get; set; } = () => ShellSettingsStore.Load();
    internal static Action<ShellSettings> SettingsSaver { get; set; } = settings => ShellSettingsStore.Save(settings);

    /// <summary>
    /// Live view of the core settings for send-boundary re-checks. Production
    /// binds CoreBridge.GetSettings (in-memory cached, refreshed on save) in
    /// App startup; when null the send boundary falls back to the snapshot
    /// the decision was issued against.
    /// </summary>
    internal static Func<ProviderSettings>? LiveSettingsLoader { get; set; }

    /// <summary>
    /// The per-send re-check behind <see cref="FreeEngineAuthorization.TryClaimSend"/>.
    /// Consent is always read live — including for once-only tokens, which
    /// were issued against Unset but must still die on an explicit later
    /// denial. Safe-dev-mode/network fall back to the decision snapshot when
    /// no live loader is installed (pure/test hosts only; production binds
    /// the loader in App startup). Fails closed.
    /// </summary>
    internal static bool SendStillAllowed(ProviderSettings snapshot, bool isOnceOnly, out string? refusal)
    {
        refusal = null;
        var current = LiveSettingsLoader?.Invoke() ?? snapshot;
        if (current.SafeDevMode || !current.NetworkEnabled)
        {
            refusal = "已开启安全离线模式或网络翻译已关闭；未发送任何请求。";
            return false;
        }
        var consent = SettingsLoader().FreeEngineConsent;
        if (isOnceOnly)
        {
            if (consent == FreeEngineConsent.Denied)
            {
                refusal = "内置免费引擎的授权已被撤销；未发送任何请求。";
                return false;
            }
        }
        else if (consent != FreeEngineConsent.Allowed)
        {
            refusal = "内置免费引擎的授权已被撤销或尚未允许；未发送任何请求。可在「设置 → 隐私与数据」中重新允许。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Decides whether a no-config text translation may leave the machine.
    /// SafeDevMode and disabled network deny unconditionally; otherwise the
    /// persisted consent decides, and the very first use asks.
    /// </summary>
    public static bool AllowsFreeEngine(ProviderSettings settings, out TranslationError? denial) =>
        AllowsFreeEngine(settings, out denial, out _);

    public static bool AllowsFreeEngine(
        ProviderSettings settings,
        out TranslationError? denial,
        out FreeEngineAuthorization? authorization)
    {
        authorization = null;
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            denial = new TranslationError(
                TranslationErrorKind.NetworkDisabled,
                "已开启安全离线模式或网络翻译已关闭；未发送任何请求。",
                "可在设置中配置本地模型（如 Ollama / LM Studio），或重新开启网络。");
            return false;
        }

        var consent = SettingsLoader().FreeEngineConsent;
        if (consent == FreeEngineConsent.Denied)
        {
            denial = new TranslationError(
                TranslationErrorKind.Configuration,
                "未允许使用内置免费引擎；没有发送任何请求。",
                "可在「设置 → 隐私与数据」中重新允许，或配置自己的模型服务。");
            return false;
        }

        if (consent == FreeEngineConsent.Allowed)
        {
            denial = null;
            authorization = new FreeEngineAuthorization(settings, IsOnceOnly: false);
            return true;
        }

        // First use: fail closed WITHOUT recording a denial — the user has
        // not answered anything, and authorization lives in
        // 「设置 → 隐私与数据」, not in a popup that interrupts translation.
        // A host may still install ConsentPrompt (future in-window prompts);
        // headless callers leave it null.
        var decision = ConsentPrompt?.Invoke(FreeEngineDestination) ?? FreeEngineDecision.Deny;
        if (decision == FreeEngineDecision.AlwaysAllow)
        {
            PersistConsent(FreeEngineConsent.Allowed);
            denial = null;
            authorization = new FreeEngineAuthorization(settings, IsOnceOnly: false);
            return true;
        }
        if (decision == FreeEngineDecision.AllowOnce)
        {
            denial = null;
            // AllowOnce covers exactly the send it was asked for — never a
            // later automatic health probe.
            authorization = new FreeEngineAuthorization(settings, IsOnceOnly: true);
            return true;
        }
        if (ConsentPrompt is null)
        {
            denial = new TranslationError(
                TranslationErrorKind.Configuration,
                "首次使用内置免费引擎需要先授权；没有发送任何请求。",
                "打开「设置 → 隐私与数据」，在内置免费引擎一行选择「允许」；或在服务页配置自己的模型服务。");
            return false;
        }

        PersistConsent(FreeEngineConsent.Denied);
        denial = new TranslationError(
            TranslationErrorKind.Configuration,
            "你选择了不使用内置免费引擎；没有发送任何请求。",
            "可在「设置 → 隐私与数据」中重新允许，或配置自己的模型服务。");
        return false;
    }

    /// <summary>Persists the consent choice, never throwing into the caller.</summary>
    public static void PersistConsent(FreeEngineConsent consent)
    {
        try
        {
            var settings = SettingsLoader();
            if (settings.FreeEngineConsent == consent)
            {
                return;
            }
            SettingsSaver(settings with { FreeEngineConsent = consent });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Persisting the choice must not mask the translation result; the
            // next run simply asks again.
        }
    }
}
