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
            var key = profile.ExecutionFacts.IsManagedExecution ? null :
                CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(profile)) ? KeyPresent : null;
            var capability = TranslationCoordinator.ResolveTextRouteCapability(
                profile.ToProviderSettings(settings), key, settings, profile);
            if (capability.HasConfiguredProvider)
            {
                var permitted = capability.IsLocal || settings.NetworkEnabled && !settings.SafeDevMode;
                var ready = permitted && ProfileManager.IsTextEngineExecutable(profile);
                return (ShortName(profile), Detail(profile, ModelName(profile)), profile, false,
                    ready ? StatusTone.Info : StatusTone.Warning);
            }
        }
        else if (!config.PreferFreeEngine && settings.TargetsLocalRuntime)
            return ("本机模型", settings.TextModel, null, false,
                settings.TextIsConfigured ? StatusTone.Info : StatusTone.Warning);
        var allowed = shell.FreeEngineConsent == FreeEngineConsent.Allowed && settings.NetworkEnabled && !settings.SafeDevMode;
        var selected = config.PreferFreeEngine || allowed;
        return (selected ? FreeName(shell.FreeEngineProvider) : "选择模型",
            selected ? FreeDetail(shell.FreeEngineProvider) : "选择文字模型；不会自动提交请求。",
            null, selected, allowed ? StatusTone.Info : StatusTone.Warning);
    }
    internal static string ShortName(ProviderProfile profile) => profile.ResolvedExecutionKind switch
    {
        EngineExecutionKind.CodexAppServer => "Codex",
        EngineExecutionKind.ChatGptDirect => "ChatGPT",
        EngineExecutionKind.OmpRpc => profile.CliConnection?.UpstreamProviderId switch
        {
            "google-antigravity" => "Antigravity",
            "openai-codex" => "Codex",
            _ => "OMP",
        },
        _ => string.IsNullOrWhiteSpace(profile.Name) ? "模型" : profile.Name,
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
        menu.Items.Add(Header("模型 · 风格与要点"));
        foreach (var profile in config.Profiles.Where(profile => profile.SupportsText))
        {
            var id = profile.Id;
            var item = ProfileItem(profile, ModelName(profile), id == activeId);
            item.Tag = id;
            item.Click += (_, _) => selectProfile(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(Header("公共翻译"));
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
            item.Click += async (_, _) => await selectFree(selected);
            menu.Items.Add(item);
        }
    }

    internal static MenuItem ProfileItem(ProviderProfile profile, string model, bool active)
    {
        // Keep the model once; connection and profile details belong in the tooltip.
        var item = new MenuItem
        {
            Header = CheckRow(string.IsNullOrWhiteSpace(model) ? ShortName(profile) : model, null, active),
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
            EngineExecutionKind.OmpRpc => $"OMP 当前登录 · {profile.CliConnection?.UpstreamProviderId}",
            EngineExecutionKind.ChatGptDirect => "ChatGPT 账号授权 · 独立会话",
            EngineExecutionKind.Unknown => "不支持的连接类型",
            _ => ProviderSettings.ClassifyEndpoint(profile.ApiBaseUrl) == EndpointClass.Loopback ? "本机服务" : "API 服务",
        };
        return $"{profile.Name}\n{model}\n{source}";
    }

    private static string FreeDetail(FreeEngineProvider provider) => provider switch
    {
        FreeEngineProvider.MyMemory => "公共翻译 · 仅文字\n发送至 api.mymemory.translated.net；需先授权。",
        _ => "公共翻译 · 仅文字\n发送至 translate.googleapis.com / clients5.google.com；需先授权。",
    };
}
