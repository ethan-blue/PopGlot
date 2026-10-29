using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace PopGlot.Windows.Services;

/// <summary>
/// The shared global 文字翻译风格 (TEXT-translation style) selector (prompt
/// templates) behind the main workbench, the floating panel and quick search.
/// Lists the built-in 准确/自然/正式 styles plus the custom list straight from
/// the core's prompt store, switches the active template via CoreBridge, and
/// offers the shared 管理提示词 entry (routed through each surface's existing
/// open-settings callback). The helper persists the choice; each surface
/// then retranslates its current text. Each request snapshots the template.
/// Source text is never spliced with style instructions anywhere in this shell.
/// Scope honesty: the template applies to TEXT requests only. 0.1.6 has no
/// vision-prompt support, so a vision-direct screenshot translation never
/// receives it — those sessions say so before and after the run instead of
/// silently claiming support. Screenshot pipelines that OCR first and
/// translate through the text provider honour the template as usual.
/// </summary>
internal static class TranslationStyleMenu
{
    internal const string FaithfulTemplateId = "faithful";
    internal const string NaturalTemplateId = "natural";
    internal const string FormalTemplateId = "formal";

    /// <summary>
    /// Selector wording while the NEXT text translation is proven to honour
    /// the active style: names the feature 文字翻译风格 and states the one
    /// exception (vision-direct screenshots) so it never over-claims. Single
    /// source: <see cref="ApplyTo"/> always uses this constant, surfaces no
    /// longer pass their own copy.
    /// </summary>
    internal const string SupportedToolTip = "风格（点击切换）";

    internal const string UnknownToolTip = "无法确认风格支持，请检查当前模型";

    /// <summary>
    /// 0.1.6 文案减法：免费引擎的事前/事后提示统一为这一行诚实短状态，
    /// 三个表面（工作台/浮窗/极速查词）共用，不再各写一句长解释。
    /// </summary>
    internal const string FreeEngineStyleNotAppliedStatus = "公共翻译 · 未应用风格";

    /// <summary>
    /// 0.1.6 文案减法：视觉直译的事前/事后提示统一为这一行诚实短状态，
    /// 三个表面共用；只陈述事实，不附带长解释。
    /// </summary>
    internal const string VisionDirectStyleNotAppliedStatus = "图片直译 · 未应用文字风格";

    /// <summary>
    /// Compact label for the selector and menu items: short names for the
    /// three built-ins, the template's own name for the custom list.
    /// </summary>
    internal static string ShortLabel(PromptTemplateDto template) => template.Id switch
    {
        FaithfulTemplateId => "准确",
        NaturalTemplateId => "自然",
        FormalTemplateId => "正式",
        _ => string.IsNullOrWhiteSpace(template.Name) ? template.Id : template.Name,
    };

    /// <summary>The active template as the core resolves it (falls back to faithful); null when unreadable.</summary>
    internal static PromptTemplateDto? TryGetActiveTemplate()
    {
        try
        {
            return CoreBridge.GetActivePromptTemplate();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One-click cycle target: the enabled templates in store order, the
    /// active one advancing by one (wrapping). A one-template store returns
    /// that template. Null when the store is unreadable or empty.
    /// </summary>
    internal static PromptTemplateDto? NextTemplate()
    {
        try
        {
            var templates = CoreBridge.ListPromptTemplates().Where(t => t.Enabled).ToList();
            if (templates.Count == 0)
            {
                return null;
            }
            var activeId = CoreBridge.GetActivePromptTemplate().Id;
            var index = templates.FindIndex(t => string.Equals(t.Id, activeId, StringComparison.Ordinal));
            return templates[(index + 1) % templates.Count];
        }
        catch (Exception)
        {
            return null;
        }
    }


    /// <summary>
    /// Read-only local probe of the route the next text translation takes,
    /// sharing the coordinator's own provider decision (including the legacy
    /// global TargetsLocalRuntime fallback) via
    /// <see cref="TranslationCoordinator.ResolveTextProviderCapability"/> so
    /// the two can never drift: a legacy local runtime must keep the selector
    /// enabled instead of being mistaken for the free engine. Zero network;
    /// Unknown means local state could not be read this instant.
    /// </summary>
    private static int _styleProbeRevision = -1;
    private static TranslationStyleSupport _styleProbeCache;

    internal static TranslationStyleSupport ProbeNextTextRoute()
    {
        // Same revision-keyed caching as the footer route snapshot: this
        // probe runs on every popup/activation repaint and must not hit the
        // credential vault in the steady state.
        var revision = ProfileManager.Revision;
        if (revision == _styleProbeRevision)
        {
            return _styleProbeCache;
        }
        var support = ProbeNextTextRouteUncached();
        _styleProbeRevision = revision;
        _styleProbeCache = support;
        return support;
    }

    private static TranslationStyleSupport ProbeNextTextRouteUncached()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            var (textRoute, _) = ProfileManager.ResolveRoutes();
            var textApiKey = textRoute is null || textRoute.Profile.ExecutionFacts.IsManagedExecution
                ? null
                : CredentialStore.HasApiKey(textRoute.CredentialTarget) ? TextRouteMenu.KeyPresent : null;
            var textRuntimeSettings = textRoute?.Profile.ToProviderSettings(settings);
            return TranslationCoordinator
                .ResolveTextRouteCapability(textRuntimeSettings, textApiKey, settings, textRoute?.Profile)
                .HasConfiguredProvider
                ? TranslationStyleSupport.Supported
                : TranslationStyleSupport.FreeEngine;
        }
        catch (Exception)
        {
            return TranslationStyleSupport.Unknown;
        }
    }

    /// <summary>
    /// Read-only local probe of the route the NEXT screenshot translation
    /// resolves to, through the very same decision the coordinator executes
    /// (<see cref="ProfileManager.ResolveRoute"/> → Rust SelectRoute): zero
    /// network, no request is started. Unavailable (route unreadable or no
    /// pipeline) means the caller stays silent rather than over-claiming.
    /// </summary>
    internal static ScreenshotPipeline ProbeNextScreenshotPipeline()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            return ProfileManager.ResolveRoute(settings, WindowsOcrService.IsSupported).ScreenshotPipeline;
        }
        catch (Exception)
        {
            return ScreenshotPipeline.Unavailable;
        }
    }

    /// <summary>
    /// Honest pre-flight notice for the vision-direct screenshot route, where
    /// the active TEXT style cannot take part. Null — and therefore no claim
    /// at all — when the resolved pipeline is not vision-direct (OCR-based
    /// pipelines translate through the text provider and honour the style),
    /// or when the faithful default is active so nothing is at stake. The
    /// notice never blocks, reroutes or re-sends anything.
    /// </summary>
    internal static string? PreScreenshotNotice()
    {
        if (ProbeNextScreenshotPipeline() != ScreenshotPipeline.VisionDirect)
        {
            return null;
        }
        if (TryGetActiveTemplate() is not { } template ||
            template.Id == FaithfulTemplateId)
        {
            return null;
        }
        return VisionDirectStyleNotAppliedStatus;
    }

    /// <summary>
    /// True when the session's pipeline label says the vision model itself
    /// produced the translation (vision-direct). The labels must stay in sync
    /// with TranslationCoordinator's screenshot pipeline; every other label
    /// (本地 OCR, 视觉识别 + 文本模型 …) went through the text provider, where
    /// the style applies normally.
    /// </summary>
    internal static bool IsVisionDirectPipeline(string? pipelineLabel) =>
        pipelineLabel is "本地视觉模型" or "视觉模型 · 独立服务";

    /// <summary>
    /// Builds the shared menu purely from local prompt-store state — no
    /// network, no translation request. Built-in styles first, the custom
    /// list after, then the manage entry. Returns null (status already
    /// reported) when the store is unreadable.
    /// </summary>
    internal static ContextMenu? Build(
        Action<PromptTemplateDto> styleChosen,
        Action<string> reportStatus,
        Action managePrompts)
    {
        IReadOnlyList<PromptTemplateDto> templates;
        string activeId;
        try
        {
            templates = CoreBridge.ListPromptTemplates();
            activeId = CoreBridge.GetActivePromptTemplate().Id;
        }
        catch (Exception exception)
        {
            reportStatus($"无法读取翻译风格列表：{exception.Message}");
            return null;
        }

        var menu = new ContextMenu();
        menu.MinWidth = 150;
        menu.MaxWidth = 260;
        if (ProbeNextTextRoute() == TranslationStyleSupport.FreeEngine)
        {
            menu.Items.Add(MakeDisabledItem("当前为公共直译 · 切换专属引擎后生效"));
            menu.Items.Add(new Separator());
        }
        var builtInCount = 0;
        foreach (var template in templates.Where(t => t.Enabled && t.IsBuiltIn))
        {
            menu.Items.Add(MakeStyleItem(template, activeId, styleChosen));
            builtInCount++;
        }

        var custom = templates.Where(t => t.Enabled && !t.IsBuiltIn).ToList();
        if (custom.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeHeader("自定义"));
            foreach (var template in custom)
            {
                menu.Items.Add(MakeStyleItem(template, activeId, styleChosen));
            }
        }

        if (builtInCount == 0 && custom.Count == 0)
        {
            menu.Items.Add(MakeDisabledItem("暂无可用风格"));
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "管理风格" };
        manage.Click += (_, _) => managePrompts();
        menu.Items.Add(manage);
        return menu;
    }

    /// <summary>Anchors the shared menu to a surface's selector button and opens it.</summary>
    internal static void Show(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementRectangle = new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        menu.HorizontalOffset = -10;
        menu.VerticalOffset = -2;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Persists a style switch off the UI thread and reports the outcome.
    /// Returns false on failure so the caller can repaint the authoritative
    /// state. The caller owns any retranslation of the visible source.
    /// </summary>
    internal static async Task<bool> SwitchActiveTemplateAsync(
        string? templateId,
        Action<string> reportStatus)
    {
        try
        {
            await CoreBridge.SetActivePromptTemplateAsync(templateId);
            var isFree = ProbeNextTextRoute() == TranslationStyleSupport.FreeEngine;
            var active = TryGetActiveTemplate();
            var name = active is not null ? ShortLabel(active) : "风格";
            ReportOnUi(reportStatus, isFree ? $"已选用「{name}」（公共翻译不支持风格）" : $"已选用「{name}」");
            return true;
        }
        catch (Exception exception)
        {
            ReportOnUi(reportStatus, $"切换文字翻译风格失败：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// The one shared 选择风格 → 持久化 → 重译 sequence behind the workbench,
    /// the floating panel and quick search: optimistic label first, persist
    /// through <see cref="SwitchActiveTemplateAsync"/>, then retranslate the
    /// surface's own source text when there is one on screen so the new style
    /// applies immediately instead of waiting for the next Enter.
    /// Returns false when the persist failed — each surface then repaints its
    /// selector from the authoritative state; a surface-specific guard (for
    /// example quick search's "closed or hidden" check) stays inside its own
    /// <paramref name="retranslate"/> callback.
    /// </summary>
    internal static async Task<bool> ChooseStyleAsync(
        PromptTemplateDto template,
        TextBlock label,
        Action<string> reportStatus,
        Func<string?> getSourceText,
        Func<Task> retranslate)
    {
        // Optimistic label; the caller repaints it from the authoritative
        // state when the persist fails. Same contract as the model switch and
        // every other surface.
        label.Text = ShortLabel(template);
        if (!await SwitchActiveTemplateAsync(template.Id, reportStatus))
        {
            return false;
        }
        if (!string.IsNullOrWhiteSpace(getSourceText()))
        {
            await retranslate();
        }
        return true;
    }

    /// <summary>
    /// Honest notice for the moment a translation is triggered. Null when the
    /// route is proven to honour the active template, or when the default
    /// faithful style is active so nothing is at stake. FreeEngine states the
    /// shared one-line short status; Unknown says exactly what cannot be
    /// promised. The notice never blocks the translation and never cancels
    /// anything.
    /// </summary>
    internal static string? PreTranslateNotice()
    {
        var probe = ProbeNextTextRoute();
        if (probe == TranslationStyleSupport.Supported)
        {
            return null;
        }
        if (TryGetActiveTemplate() is not { } template ||
            template.Id == FaithfulTemplateId)
        {
            return null;
        }
        return probe == TranslationStyleSupport.FreeEngine
            ? FreeEngineStyleNotAppliedStatus
            : $"{UnknownToolTip}。";
    }

    /// <summary>
    /// Pure typed mapping from a session's <see cref="TranslationPromptSupport"/>
    /// fact to the shared short status line — the display counterpart of the
    /// coordinator's own route marking. Empty string means there is nothing to
    /// claim. Callers must branch on the typed support (executor/pipeline
    /// kind), never on display strings, and use this only to render.
    /// </summary>
    internal static string StyleStatusFor(TranslationPromptSupport support) => support switch
    {
        TranslationPromptSupport.NotSupported => FreeEngineStyleNotAppliedStatus,
        TranslationPromptSupport.NotApplicable => VisionDirectStyleNotAppliedStatus,
        TranslationPromptSupport.Unknown => $"{UnknownToolTip}。",
        // Applied: the style took part — no disclaimer. Pending: no text stage
        // resolved yet — no claim either way.
        _ => string.Empty,
    };

    /// <summary>Shows the style selector only on text routes that can apply it.</summary>
    internal static TranslationStyleSupport ApplyTo(Button selectorButton)
    {
        var probe = ProbeNextTextRoute();
        var active = TryGetActiveTemplate();
        var styleName = active is not null ? ShortLabel(active) : null;
        var styleDesc = active?.Description;

        string message;
        if (probe == TranslationStyleSupport.FreeEngine)
        {
            message = styleName is null
                ? "翻译风格（当前为公共直译；切换专属引擎后生效）"
                : $"当前选定：{styleName}（公共直译下未应用；切换专属引擎后生效）";
        }
        else if (probe == TranslationStyleSupport.Unknown)
        {
            message = UnknownToolTip;
        }
        else
        {
            if (styleName is not null)
            {
                var descPart = string.IsNullOrWhiteSpace(styleDesc) ? string.Empty : $" · {styleDesc}";
                message = $"当前风格：{styleName}{descPart}\n点击选择风格 ({InterfaceKeys.StyleHotkey.DisplayName})";
            }
            else
            {
                message = $"{SupportedToolTip} ({InterfaceKeys.StyleHotkey.DisplayName})";
            }
        }

        selectorButton.IsEnabled = probe != TranslationStyleSupport.FreeEngine;
        selectorButton.Visibility = probe == TranslationStyleSupport.FreeEngine
            ? Visibility.Collapsed : Visibility.Visible;
        selectorButton.ToolTip = message;
        AutomationProperties.SetHelpText(selectorButton, message);
        return probe;
    }

    /// <summary>Active-style short label for a selector, with a neutral fallback.</summary>
    internal static string ActiveLabel()
    {
        var active = TryGetActiveTemplate();
        return active is null ? "风格" : ShortLabel(active);
    }

    private static MenuItem MakeStyleItem(
        PromptTemplateDto template,
        string activeId,
        Action<PromptTemplateDto> styleChosen)
    {
        var id = template.Id;
        var isActive = string.Equals(id, activeId, StringComparison.Ordinal);

        // The shared MenuItem template collapses empty icon and shortcut gutters.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        var slot = new Border { Width = 20, VerticalAlignment = VerticalAlignment.Center };
        if (isActive)
        {
            slot.Child = MakeActiveCheck();
        }
        row.Children.Add(slot);
        var titleText = ShortLabel(template);
        var title = new TextBlock
        {
            Text = titleText,
            FontSize = 12.5,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        row.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            var info = new System.Windows.Shapes.Path
            {
                Data = (Geometry)Application.Current.FindResource("IconInfo"),
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = template.Description,
            };
            info.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "TextTertiaryBrush");
            row.Children.Add(info);
        }

        var item = new MenuItem
        {
            Header = row,
            ToolTip = string.IsNullOrWhiteSpace(template.Description) ? null : template.Description,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
        };
        AutomationProperties.SetName(item, titleText);
        item.Click += (_, _) =>
        {
            if (!string.Equals(TryGetActiveTemplate()?.Id, id, StringComparison.Ordinal))
            {
                styleChosen(template);
            }
        };
        return item;
    }

    private static FrameworkElement MakeActiveCheck()
    {
        var geom = (Geometry)Application.Current.FindResource("IconCheck");
        var path = new System.Windows.Shapes.Path
        {
            Data = geom,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        path.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "AccentBrush");
        return path;
    }

    private static MenuItem MakeHeader(string text)
    {
        var item = new MenuItem
        {
            Header = text,
            IsEnabled = true,
            Focusable = false,
            IsHitTestVisible = false,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Padding = new Thickness(12, 6, 12, 4),
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextTertiaryBrush");
        return item;
    }

    private static MenuItem MakeDisabledItem(string text) => new()
    {
        Header = text,
        IsEnabled = false,
    };

    /// <summary>Status reports land on the caller's UI thread from any context.</summary>
    private static void ReportOnUi(Action<string> reportStatus, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => reportStatus(message));
        }
        else
        {
            reportStatus(message);
        }
    }
}
