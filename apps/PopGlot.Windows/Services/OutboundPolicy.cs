using System.Windows;

namespace PopGlot.Windows.Services;

/// <summary>
/// 内置免费引擎的联网意愿。
///
/// 0.1.35 起「默认允许」：<see cref="Unset"/>（从未表态，含所有旧配置文件）
/// 与 <see cref="Allowed"/> 行为完全一致——用户不再需要钻进设置页点“允许”
/// 才能翻译。隐私选择收敛到两处：总开关「允许连接云端翻译服务」，以及首次
/// 配置向导里的隐私一页。
/// </summary>
internal enum FreeEngineConsent
{
    /// <summary>从未表态。自 0.1.35 起等同允许，仅为兼容旧配置而保留。</summary>
    Unset = 0,

    /// <summary>明确允许。</summary>
    Allowed = 1,

    /// <summary>
    /// 明确拒绝（在首次配置向导里关掉）。这是用户亲手做出的选择，任何升级
    /// 都不得静默翻回允许。
    /// </summary>
    Denied = 2,
}

/// <summary>
/// Proof that <see cref="OutboundPolicy"/> authorized free-engine traffic for
/// one request scope, carrying the exact network settings the decision was
/// made against. The last send layer (<see cref="FreeTranslateService"/>)
/// refuses to transmit without one, so a UI or health-probe caller can never
/// bypass the policy.
/// </summary>
/// <remarks>
/// C01: the authorization is a per-request token, never stored in a UI or
/// singleton field. <see cref="TryClaimSend"/> is the live send boundary —
/// every send re-reads the current policy, so a revocation between the
/// decision and a later endpoint fallback stops the remaining sends.
/// </remarks>
internal sealed class FreeEngineAuthorization
{
    public ProviderSettings Settings { get; }

    public FreeEngineAuthorization(ProviderSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Re-reads the live policy immediately before the transport call. Returns
    /// false (with a user-readable refusal) when the free engine was revoked,
    /// or when safe-mode/offline was switched on since the decision.
    /// </summary>
    public bool TryClaimSend(out string? refusal) =>
        OutboundPolicy.SendStillAllowed(Settings, out refusal);
}

/// <summary>
/// The single authority on whether the built-in free web engine may send text.
/// Windows and services must never re-derive this from booleans themselves.
/// </summary>
internal static class OutboundPolicy
{
    public const string FreeEngineDestination =
        "你选中的公共翻译（Google translate.googleapis.com / clients5.google.com，或备用 MyMemory api.mymemory.translated.net）";

    /// <summary>Test seam: where consent is persisted. Production uses the real store.</summary>
    internal static Func<ShellSettings> SettingsLoader { get; set; } = () => ShellSettingsStore.Load();
    internal static Action<ShellSettings> SettingsSaver { get; set; } = settings =>
        ShellSettingsStore.Update(current => current with { FreeEngineConsent = settings.FreeEngineConsent });

    /// <summary>
    /// Live view of the core settings for send-boundary re-checks. Production
    /// binds CoreBridge.GetSettings (in-memory cached, refreshed on save) in
    /// App startup; when null the send boundary falls back to the snapshot
    /// the decision was issued against.
    /// </summary>
    internal static Func<ProviderSettings>? LiveSettingsLoader { get; set; }

    /// <summary>
    /// The per-send re-check behind <see cref="FreeEngineAuthorization.TryClaimSend"/>.
    /// Consent is always read live from the store, so flipping the switch (or
    /// going offline) stops every endpoint that has not been sent yet.
    /// Safe-dev-mode/network fall back to the decision snapshot when no live
    /// loader is installed (pure/test hosts only). Fails closed.
    /// </summary>
    internal static bool SendStillAllowed(ProviderSettings snapshot, out string? refusal)
    {
        refusal = null;
        var current = LiveSettingsLoader?.Invoke() ?? snapshot;
        if (current.SafeDevMode || !current.NetworkEnabled)
        {
            refusal = "已开启安全离线模式或网络翻译已关闭；未发送任何请求。";
            return false;
        }
        // Unset is allowed by default; only an explicit Denied stops the send.
        if (SettingsLoader().FreeEngineConsent == FreeEngineConsent.Denied)
        {
            refusal = "内置免费引擎的授权已被撤销；未发送任何请求。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Decides whether a no-config text translation may leave the machine.
    /// SafeDevMode and disabled network deny unconditionally; an explicit
    /// Denied consent denies; everything else (including a fresh install that
    /// never answered) is allowed.
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

        if (SettingsLoader().FreeEngineConsent == FreeEngineConsent.Denied)
        {
            denial = new TranslationError(
                TranslationErrorKind.Configuration,
                "未允许使用内置免费引擎；没有发送任何请求。",
                "可在「设置 → 通用 → 重新显示引导」中重新选择，或配置自己的模型服务。");
            return false;
        }

        denial = null;
        authorization = new FreeEngineAuthorization(settings);
        return true;
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
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Persisting the choice must not mask the translation result; the
            // next run simply falls back to the default.
        }
    }
}
