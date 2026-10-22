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

    internal static (string Label, string Detail, ProviderProfile? Profile, bool IsFree, StatusTone Tone) ReadCurrent()
    {
        var config = ProfileManager.Load();
        var settings = CoreBridge.GetSettings();
        var shell = ShellSettingsStore.Load();
        var profile = config.TryGetActiveProfile();
        if (profile is not null)
        {
            var key = profile.ExecutionFacts.IsManagedExecution ? null :
                CredentialStore.LoadApiKey(ProfileManager.ResolveCredentialTargetFor(profile));
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

    internal static ContextMenu Create(Action<string> selectProfile, Func<FreeEngineProvider, Task> selectFree, Action manage)
    {
        var menu = new ContextMenu { Width = 264, MaxWidth = 264, MaxHeight = 420 };
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
                Header = Label(FreeName(provider)),
                ToolTip = FreeDetail(provider),
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = active ? ActiveCheck() : null,
                Tag = provider,
            };
            System.Windows.Automation.AutomationProperties.SetName(item, FreeName(provider));
            item.Click += async (_, _) => await selectFree(selected);
            menu.Items.Add(item);
        }
    }

    internal static MenuItem ProfileItem(ProviderProfile profile, string model, bool active)
    {
        var content = new StackPanel();
        content.Children.Add(Label(ShortName(profile)));
        if (!string.IsNullOrWhiteSpace(model))
        {
            var detail = Label(model);
            detail.FontSize = 11;
            detail.Margin = new Thickness(0, 3, 0, 0);
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            content.Children.Add(detail);
        }
        var description = Detail(profile, model);
        var item = new MenuItem
        {
            Header = content,
            ToolTip = description,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = active ? ActiveCheck() : null,
        };
        System.Windows.Automation.AutomationProperties.SetName(item, description);
        return item;
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
        Text = text, MaxWidth = 204, TextTrimming = TextTrimming.CharacterEllipsis,
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
