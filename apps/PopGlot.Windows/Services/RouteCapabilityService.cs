using System;
using System.Collections.Generic;
using System.Linq;

namespace PopGlot.Windows.Services;

/// <summary>
/// R03 统一路线能力状态：可用、需配置、不支持、未知。
/// 供工作台、浮窗与设置页共享，确保能力差异提前可见，避免未配置时遭遇运行时异常。
/// </summary>
internal enum RouteCapabilityState
{
    Available,          // 可用
    NeedsConfiguration, // 需配置
    Unsupported,        // 不支持
    Unknown,            // 未知
}

/// <summary>
/// R03 验收的五种路线类型。
/// </summary>
internal enum RouteKind
{
    FreeEngine,         // 免费线路
    LocalModel,         // 本机模型
    ValidRemote,        // 有效远程路线
    MissingCredential,  // 无凭据
    UnknownModel,       // 未知模型
}

/// <summary>
/// 要点/文本生成任务能力派生结果。
/// </summary>
internal sealed record SummaryCapability(
    RouteCapabilityState State,
    RouteKind RouteKind,
    string Reason,
    string ShortBadge,
    bool CanNavigateToAddEngine);

internal static class RouteCapabilityService
{
    private static readonly HashSet<string> KnownCloudHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.openai.com",
        "api.deepseek.com",
        "generativelanguage.googleapis.com",
        "api.anthropic.com",
        "open.bigmodel.cn",
        "api.siliconflow.cn",
        "api.groq.com",
        "api.mistral.ai",
    };

    internal static bool IsKnownCloudHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        try
        {
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                return KnownCloudHosts.Contains(uri.Host);
            }
            return KnownCloudHosts.Any(h => baseUrl.Contains(h, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 纯函数派生要点能力状态：严格根据传入的配置事实判定，无副作用。
    /// </summary>
    internal static SummaryCapability Evaluate(
        ProviderProfile? activeProfile,
        bool preferFreeEngine,
        FreeEngineConsent freeEngineConsent,
        string? apiKey,
        ProviderSettings settings)
    {
        // 1. 免费线路或未配置档案
        if (activeProfile is null || preferFreeEngine)
        {
            var allowsFree = freeEngineConsent == FreeEngineConsent.Allowed &&
                             !settings.SafeDevMode && settings.NetworkEnabled;
            if (allowsFree)
            {
                return new SummaryCapability(
                    RouteCapabilityState.Unsupported,
                    RouteKind.FreeEngine,
                    "当前公共翻译不支持整理要点，请配置模型引擎。",
                    "不支持要点",
                    CanNavigateToAddEngine: true);
            }

            return new SummaryCapability(
                RouteCapabilityState.NeedsConfiguration,
                RouteKind.FreeEngine,
                "尚未配置模型引擎，请先添加引擎。",
                "未配置引擎",
                CanNavigateToAddEngine: true);
        }

        // 2. 本地运行时（Ollama / LM Studio）
        var isLocal = activeProfile.IsLocal || activeProfile.ToProviderSettings(settings).TargetsLocalRuntime;
        if (isLocal)
        {
            return new SummaryCapability(
                RouteCapabilityState.Available,
                RouteKind.LocalModel,
                "本地模型已就绪。",
                "本地模型",
                CanNavigateToAddEngine: false);
        }

        // 3. 远端服务缺少凭据
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SummaryCapability(
                RouteCapabilityState.NeedsConfiguration,
                RouteKind.MissingCredential,
                "模型引擎未配置密钥，请在设置中补充。",
                "未配置密钥",
                CanNavigateToAddEngine: true);
        }

        // 4. 远端服务缺少模型名
        var effectiveModel = !string.IsNullOrWhiteSpace(activeProfile.TextModel)
            ? activeProfile.TextModel
            : settings.TextModel;
        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return new SummaryCapability(
                RouteCapabilityState.NeedsConfiguration,
                RouteKind.MissingCredential,
                "尚未配置模型名称，请在设置中补充。",
                "未配置模型",
                CanNavigateToAddEngine: true);
        }

        // 5. 未知模型或自建端点
        var baseUrl = activeProfile.ApiBaseUrl ?? string.Empty;
        var isKnown = IsKnownCloudHost(baseUrl);
        if (!isKnown)
        {
            return new SummaryCapability(
                RouteCapabilityState.Unknown,
                RouteKind.UnknownModel,
                "可以尝试整理要点。",
                "未知模型",
                CanNavigateToAddEngine: false);
        }

        // 6. 有效远程路线
        return new SummaryCapability(
            RouteCapabilityState.Available,
            RouteKind.ValidRemote,
            "可以整理要点。",
            "模型引擎",
            CanNavigateToAddEngine: false);
    }

    /// <summary>
    /// 读取当前配置与凭据状态派生能力。
    /// </summary>
    internal static SummaryCapability EvaluateCurrent()
    {
        try
        {
            var config = ProfileManager.Load();
            var active = config.TryGetActiveProfile();
            var shell = ShellSettingsStore.Load();
            var settings = CoreBridge.GetSettings();
            string? apiKey = null;
            if (active is not null)
            {
                var target = ProfileManager.ResolveCredentialTargetFor(active);
                apiKey = CredentialStore.LoadApiKey(target);
            }
            return Evaluate(active, config.PreferFreeEngine, shell.FreeEngineConsent, apiKey, settings);
        }
        catch
        {
            return new SummaryCapability(
                RouteCapabilityState.Unknown,
                RouteKind.UnknownModel,
                "无法读取当前引擎配置，请检查设置。",
                "配置异常",
                CanNavigateToAddEngine: true);
        }
    }
}
