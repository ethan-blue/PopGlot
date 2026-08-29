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
/// The single authority on whether the built-in free web engine may send text.
/// Windows and services must never re-derive this from booleans themselves.
/// </summary>
internal static class OutboundPolicy
{
    public const string FreeEngineDestination = "Google 公共翻译服务（translate.googleapis.com）";

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
    /// Decides whether a no-config text translation may leave the machine.
    /// SafeDevMode and disabled network deny unconditionally; otherwise the
    /// persisted consent decides, and the very first use asks.
    /// </summary>
    public static bool AllowsFreeEngine(
        ProviderSettings settings,
        out TranslationError? denial)
    {
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
                "可在「翻译服务」设置中重新允许，或配置自己的模型服务。");
            return false;
        }

        if (consent == FreeEngineConsent.Allowed)
        {
            denial = null;
            return true;
        }

        // First use: ask. No prompt installed (tests, headless) fails closed.
        var decision = ConsentPrompt?.Invoke(FreeEngineDestination) ?? FreeEngineDecision.Deny;
        if (decision == FreeEngineDecision.AlwaysAllow)
        {
            PersistConsent(FreeEngineConsent.Allowed);
            denial = null;
            return true;
        }
        if (decision == FreeEngineDecision.AllowOnce)
        {
            denial = null;
            return true;
        }

        PersistConsent(FreeEngineConsent.Denied);
        denial = new TranslationError(
            TranslationErrorKind.Configuration,
            "你选择了不使用内置免费引擎；没有发送任何请求。",
            "可在「翻译服务」设置中配置模型服务，或重新允许免费引擎。");
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
