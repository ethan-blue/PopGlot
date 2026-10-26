using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopGlot.Windows.Services.AiConnections;
using PopGlot.Windows.Sections;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PopGlot.Windows.Services;

internal static class TextRouteMenu
{
    // Presence-only sentinel: ResolveTextRouteCapability null-checks this
    // value, so a non-null marker proves a key exists without materializing
    // the secret on UI-thread repaint paths.
    internal const string KeyPresent = "present";

    private static readonly object CacheGate = new();
    private static int _cacheRevision = -1;
    private static ShellSettings? _cacheShell;
    private static (string Label, string Detail, ProviderProfile? Profile, bool IsFree, StatusTone Tone) _cache;

    /// <summary>
    /// Repaint-hot route snapshot. Keyed on the monotonic profile revision
    /// plus the cached shell-settings instance (its identity only changes
    /// when the file content changes), so alt-tab popups and menu builds do
    /// zero CredReads and zero extra disk reads in the steady state.
    /// </summary>
    internal static (string Label, string Detail, ProviderProfile? Profile, bool IsFree, StatusTone Tone) ReadCurrent()
    {
        var shell = ShellSettingsStore.Load();
        lock (CacheGate)
        {
            if (_cacheRevision == ProfileManager.Revision && ReferenceEquals(shell, _cacheShell))
            {
                return _cache;
            }
        }
        var snapshot = ReadCurrentUncached(shell);
        lock (CacheGate)
        {
            _cacheRevision = ProfileManager.Revision;
            _cacheShell = shell;
            _cache = snapshot;
        }
        return snapshot;
    }

    private static (string Label, string Detail, ProviderProfile? Profile, bool IsFree, StatusTone Tone) ReadCurrentUncached(ShellSettings shell)
    {
        var config = ProfileManager.Load();
        var settings = CoreBridge.GetSettings();
        var profile = config.TryGetActiveProfile();
        if (profile is not null)
        {
            var keyPresent = profile.ExecutionFacts.IsManagedExecution || CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(profile));
            var key = profile.ExecutionFacts.IsManagedExecution ? null : (keyPresent ? KeyPresent : null);
            var capability = TranslationCoordinator.ResolveTextRouteCapability(
                profile.ToProviderSettings(settings), key, settings, profile);
            if (capability.HasConfiguredProvider)
            {
                var permitted = capability.IsLocal || settings.NetworkEnabled && !settings.SafeDevMode;
                var ready = permitted && ProfileManager.IsTextEngineExecutable(profile);
                if (!ready)
                {
                    return (ShortName(profile), Detail(profile, ModelName(profile)), profile, false, StatusTone.Warning);
                }

                // Health/evidence honesty: only a matching test outcome produces green (Success)
                // or red (Error). Untested or configuration-changed endpoints stay neutral (Info).
                var currentFingerprint = ProfileVerification.EvidenceFingerprint(
                    profile, ProfileVerification.CredentialAvailable(profile));
                var validEvidence = !string.IsNullOrWhiteSpace(profile.LastTestFingerprint) &&
                    string.Equals(profile.LastTestFingerprint, currentFingerprint, StringComparison.Ordinal);

                StatusTone tone;
                if (validEvidence && profile.LastTestOutcome == "ok")
                {
                    tone = StatusTone.Success;
                }
                else if (validEvidence && !string.IsNullOrWhiteSpace(profile.LastTestOutcome))
                {
                    tone = StatusTone.Error;
                }
                else
                {
                    tone = StatusTone.Info;
                }

                return (ShortName(profile), Detail(profile, ModelName(profile)), profile, false, tone);
            }
        }
        else if (!config.PreferFreeEngine && settings.TargetsLocalRuntime)
            return ("本机模型", settings.TextModel, null, false,
                settings.TextIsConfigured ? StatusTone.Info : StatusTone.Warning);
        var allowed = shell.FreeEngineConsent == FreeEngineConsent.Allowed && settings.NetworkEnabled && !settings.SafeDevMode;
        var selected = config.PreferFreeEngine || allowed;
        if (!selected || !allowed)
        {
            return ("选择模型", "选择翻译引擎；已有文本时会重新翻译。", null, false, StatusTone.Warning);
        }

        // For free engine, tone reflects probed reachability evidence.
        var freeTone = StatusTone.Info;
        if (FreeTranslateService.HasHealthResultFor(shell.FreeEngineProvider))
        {
            var health = FreeTranslateService.LastHealthFor(shell.FreeEngineProvider);
            freeTone = health.Ok ? StatusTone.Success : StatusTone.Warning;
        }

        return (FreeName(shell.FreeEngineProvider), FreeDetail(shell.FreeEngineProvider), null, true, freeTone);
    }
    internal static string ShortName(ProviderProfile profile) => profile.ResolvedExecutionKind switch
    {
        EngineExecutionKind.CodexAppServer => "Codex",
        EngineExecutionKind.ChatGptDirect => "ChatGPT",
        EngineExecutionKind.OmpRpc => OmpProviderShortName(profile.CliConnection?.UpstreamProviderId),
        _ => string.IsNullOrWhiteSpace(profile.Name) ? "模型" : profile.Name,
    };

    /// <summary>
    /// OMP 连接的统一命名：以「提供方」为显示主体（配置维度是 提供方+模型，
    /// 一个档案即一个提供方下的一个模型），不再把所有提供方笼统显示为 OMP。
    /// 已知 id 给出稳定短名；未知 id 原样显示（仍可区分是哪个提供方）；
    /// 缺失时才回退到 OMP。
    /// </summary>
    internal static string OmpProviderShortName(string? providerId) => providerId switch
    {
        null or "" => "OMP",
        "google-antigravity" => "Antigravity",
        "openai-codex" => "Codex",
        "github-copilot" => "Copilot",
        "anthropic" or "claude-code" => "Claude",
        "openai" => "OpenAI",
        "google" => "Gemini",
        _ => providerId,
    };

    internal static string FreeName(FreeEngineProvider provider) => EngineWording.NameFor(provider);

    internal static string ModelName(ProviderProfile profile) => profile.ResolvedExecutionKind switch
    {
        EngineExecutionKind.CodexAppServer or EngineExecutionKind.OmpRpc => profile.CliConnection?.ModelId ?? string.Empty,
        EngineExecutionKind.ChatGptDirect => profile.DirectConnection?.ModelId ?? string.Empty,
        _ => profile.TextModel,
    };


    internal static ContextMenu Create(Action<string> selectProfile, Func<FreeEngineProvider, Task> selectFree, Action manage)
    {
        var menu = new ContextMenu { MinWidth = 190, MaxWidth = 264, MaxHeight = 420 };
        AddTextChoices(menu, selectProfile, selectFree);
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "管理引擎" };
        settings.Click += (_, _) => manage();
        menu.Items.Add(settings);
        return menu;
    }

    internal static void AddTextChoices(ContextMenu menu, Action<string> selectProfile, Func<FreeEngineProvider, Task> selectFree)
    {
        var config = ProfileManager.Load();
        var current = ReadCurrent();
        var activeId = current.Profile?.Id;
        var shell = ShellSettingsStore.Load();
        menu.Items.Add(Header("模型"));
        foreach (var profile in config.Profiles.Where(profile => profile.SupportsText))
        {
            var id = profile.Id;
            var item = ProfileItem(profile, ModelName(profile), id == activeId);
            item.Tag = id;
            item.Click += (_, _) =>
            {
                if (!IsCurrentChoice(id)) selectProfile(id);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(Header("内置"));
        var freeSelected = current.IsFree;
        foreach (var provider in new[] { FreeEngineProvider.Google, FreeEngineProvider.MyMemory })
        {
            var selected = provider;
            var active = freeSelected && shell.FreeEngineProvider == provider;
            var item = new MenuItem
            {
                Header = CheckRow(FreeName(provider), null, active),
                ToolTip = FreeDetail(provider),
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Tag = provider,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, FreeName(provider));
            item.Click += async (_, _) =>
            {
                if (!IsCurrentChoice(null, selected))
                    await selectFree(selected);
            };
            menu.Items.Add(item);
        }
    }

    private static bool IsCurrentChoice(string? profileId, FreeEngineProvider? provider = null)
    {
        try
        {
            var current = ReadCurrent();
            return provider is { } free
                ? current.IsFree && ShellSettingsStore.Load().FreeEngineProvider == free
                : current.Profile?.Id == profileId;
        }
        catch (Exception)
        {
            // A config read failure belongs to the surface's guarded switch
            // operation, not an unhandled exception in the menu click.
            return false;
        }
    }

    internal static MenuItem ProfileItem(ProviderProfile profile, string model, bool active)
    {
        // Keep the model once; the OMP provider disambiguates rows that share
        // a model id across providers; connection and profile details belong
        // in the tooltip.
        var secondary = profile.ResolvedExecutionKind == EngineExecutionKind.OmpRpc
            ? OmpProviderShortName(profile.CliConnection?.UpstreamProviderId)
            : null;
        var item = new MenuItem
        {
            Header = CheckRow(string.IsNullOrWhiteSpace(model) ? ShortName(profile) : model, secondary, active),
            ToolTip = Detail(profile, model),
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
        };
        System.Windows.Automation.AutomationProperties.SetName(item, Detail(profile, model));
        return item;
    }

    /// <summary>
    /// Shared single-line menu row: fixed 16px leading check slot (no wide
    /// MenuItem icon column), primary label, optional inline secondary label.
    /// </summary>
    internal static StackPanel CheckRow(string primary, string? secondary, bool active)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        var slot = new Border { Width = 16, VerticalAlignment = VerticalAlignment.Center };
        if (active)
        {
            slot.Child = ActiveCheck();
        }
        row.Children.Add(slot);
        var name = Label(primary);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        row.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(secondary))
        {
            var detail = Label(secondary);
            detail.FontSize = 11;
            detail.Margin = new Thickness(8, 0, 0, 0);
            detail.MaxWidth = 120;
            detail.VerticalAlignment = VerticalAlignment.Center;
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            row.Children.Add(detail);
        }
        return row;
    }


    internal static MenuItem Header(string text)
    {
        var item = new MenuItem { Header = text, Focusable = false, IsHitTestVisible = false, FontSize = 11 };
        item.SetResourceReference(Control.ForegroundProperty, "TextTertiaryBrush");
        return item;
    }

    internal static FrameworkElement? ActiveCheck()
    {
        if (Application.Current?.TryFindResource("IconCheck") is not Geometry geometry) return null;
        var check = new System.Windows.Shapes.Path
        {
            Data = geometry, Width = 12, Height = 12, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        check.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        return check;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Detail(ProviderProfile profile, string model)
    {
        var source = profile.ResolvedExecutionKind switch
        {
            EngineExecutionKind.CodexAppServer => "Codex 当前登录 · 独立会话",
            EngineExecutionKind.OmpRpc => $"{OmpProviderShortName(profile.CliConnection?.UpstreamProviderId)} 经 OMP 登录 · 提供方 {profile.CliConnection?.UpstreamProviderId}",
            EngineExecutionKind.ChatGptDirect => "ChatGPT 账号授权 · 独立会话",
            EngineExecutionKind.Unknown => "不支持的连接类型",
            _ => ProviderSettings.ClassifyEndpoint(profile.ApiBaseUrl) == EndpointClass.Loopback ? "本机服务" : "API 服务",
        };
        return $"{profile.Name}\n{model}\n{source}";
    }

    private static string FreeDetail(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.MyMemory => "免配置 · 仅文字\n发送至 api.mymemory.translated.net；需先授权。",
        _ => "免配置 · 仅文字\n发送至 translate.googleapis.com / clients5.google.com；需先授权。",
    };
}
