using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PopGlot.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.CliTests;

internal static class ServicesAndProxyUiTests
{
    public static void Run(string root)
    {
        EnsureWpfApplication();
        TestServicesSectionHubAndDiscovery();
        TestGeneralSectionProxyAndLiveTranslate(root);
        TestTranslateSectionFreeProvidersAndDomesticTips();
        TestPrivacyAndFirstRunUiRegressions();
    }

    private static void EnsureWpfApplication()
    {
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var document = System.Xml.Linq.XDocument.Load("apps/PopGlot.Windows/App.xaml");
            var resources = document.Root!.Elements().Single().Elements().Single();
            foreach (var element in resources.Descendants())
            {
                if (element.Attribute("Source") is { } source && source.Value == "Themes/Controls.xaml")
                {
                    source.Value = "/PopGlot;component/Themes/Controls.xaml";
                }
            }
            resources.SetAttributeValue(
                System.Xml.Linq.XNamespace.Xmlns + "x",
                "http://schemas.microsoft.com/winfx/2006/xaml");
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
        }
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
    }

    private static void TestServicesSectionHubAndDiscovery()
    {
        var originalShell = ShellSettingsStore.Load();
        var originalCore = CoreBridge.GetSettings();
        var section = new ServicesSection();
        var window = new Window
        {
            Content = section,
            Width = 860,
            Height = 760,
            Left = -12000,
            Top = -12000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Background = (Brush)Application.Current.Resources["CanvasBrush"],
        };

        try
        {
            window.Show();
            Pump();

            // Public routes are a separate page; select it as a user would.
            ((TabControl)section.FindName("ServicesTabs")).SelectedItem = section.FindName("PublicServicesTab");
            Pump(); window.UpdateLayout();
            // 1. Free routes panel displays all 6 routes with honest tags
            var freeList = (ItemsControl)section.FindName("FreeRoutesList");
            var freeRows = freeList.ItemsSource!.Cast<FreeRouteRow>().ToList();
            Program.Assert(freeRows.Count == 6, $"expected 6 built-in free routes, got {freeRows.Count}");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.Auto && r.Tags.Contains("按文本择优")),
                "Auto free route must be rendered with tag");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.Bing && r.Tags.Contains("实验性")),
                "Bing must disclose its experimental upstream instead of implying verified reachability");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.Youdao && r.Tags.Contains("词典级")),
                "Youdao free route must be rendered with 词典级 badge");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.MyMemory && r.Tags.Contains("500字节限制")),
                "MyMemory free route must disclose 500-byte limit");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.Yandex && r.Tags.Contains("手动选用")),
                "Yandex must be selectable without quietly extending Auto recipients");
            Program.Assert(freeRows.Any(r => r.Provider == FreeEngineProvider.Google && r.Tags.Contains("需代理")),
                "Google free route must be rendered with 需代理 badge");

            // 2. Switching active free route to Youdao updates ShellSettings + CoreProductConfig
            ShellSettingsStore.Update(c => c with { FreeEngineConsent = FreeEngineConsent.Allowed });
            section.RefreshFreeRoutesPanel();
            Pump();
            window.UpdateLayout();
            Pump();
            var youdaoSwitchBtn = FindVisualChildren<Button>(freeList)
                .FirstOrDefault(b => b.Tag is FreeEngineProvider p && p == FreeEngineProvider.Youdao && Equals(b.Content, "设为当前"))
                ?? throw new InvalidOperationException($"Youdao switch button not found; buttons={FindVisualChildren<Button>(freeList).Count()}");
            youdaoSwitchBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Program.Assert(ShellSettingsStore.Load().FreeEngineProvider == FreeEngineProvider.Youdao,
                "clicking 设为当前 on Youdao must persist FreeEngineProvider.Youdao");
            Program.Assert(ProfileManager.Load().PreferFreeEngine,
                "switching to free route must set PreferFreeEngine = true");

            // 3. User-initiated probe displays latency & Google timeout shortcut banner
            section.FreeRouteProbeExecutor = (provider, force, auth) =>
            {
                Program.Assert(force, "manual probe must pass force: true");
                Program.Assert(auth is not null, "probe must carry OutboundPolicy authorization");
                return Task.FromResult(provider switch
                {
                    FreeEngineProvider.Bing => new FreeTranslateService.FreeEngineHealth(true, 145, null),
                    FreeEngineProvider.Youdao => new FreeTranslateService.FreeEngineHealth(true, 92, null),
                    FreeEngineProvider.MyMemory => new FreeTranslateService.FreeEngineHealth(true, 310, null),
                    FreeEngineProvider.Google => new FreeTranslateService.FreeEngineHealth(false, 0, "请求超时"),
                    _ => new FreeTranslateService.FreeEngineHealth(true, 92, null),
                });
            };
            var testAllBtn = (Button)section.FindName("TestAllFreeRoutesButton");
            testAllBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => ((Border)section.FindName("GoogleProxyHintBanner")).Visibility == Visibility.Visible,
                "Google timeout during probe must reveal the proxy/domestic shortcut banner");
            var probedRows = freeList.ItemsSource!.Cast<FreeRouteRow>().ToList();
            Program.Assert(probedRows.Single(r => r.Provider == FreeEngineProvider.Bing).LatencyText == "145 ms",
                "Bing probe must display millisecond latency");
            Program.Assert(probedRows.Single(r => r.Provider == FreeEngineProvider.Google).LatencyText == "超时",
                "Google timeout must display 超时");

            // 4. AI sources have their own page, not a second section below public routes.
            ((TabControl)section.FindName("ServicesTabs")).SelectedItem = section.FindName("AiServicesTab");
            Pump();
            Program.Assert(((Button)section.FindName("AddEngineHeaderButton")).IsVisible,
                "adding AI must be available on the AI page");
            // Grouped presets include SiliconFlow, Groq, OpenRouter, Ollama, LM Studio, Zhipu
            ((Button)section.FindName("AddEngineHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            window.UpdateLayout();
            Pump();
            Program.Assert(section.PresetsPanel.Visibility == Visibility.Visible, "PresetsPanel must open");
            var presetButtons = FindVisualChildren<Button>(section.PresetsPanel)
                .Where(b => b.Tag is string)
                .ToList();
            foreach (var requiredTag in new[] { "ollama", "lm-studio", "siliconflow", "zhipu", "groq", "openrouter", "deepseek", "openai", "gemini", "claude", "litellm", "custom", "codex", "omp" })
            {
                Program.Assert(presetButtons.Any(b => Equals(b.Tag, requiredTag)),
                    $"grouped presets must contain tag '{requiredTag}'");
            }
            var siliconFlowBtn = presetButtons.First(b => Equals(b.Tag, "siliconflow"));
            siliconFlowBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Program.Assert(((TextBox)section.FindName("BaseUrlTextBox")).Text == "https://api.siliconflow.cn/v1",
                "SiliconFlow preset must populate official base URL");
            Program.Assert(((Border)section.FindName("PresetTierHintBanner")).Visibility == Visibility.Visible,
                "curated free-tier preset must display free-tier hint banner");

            // Test connection in editor displays elapsed ms
            ((ComboBox)section.FindName("TextModelCombo")).Text = "Qwen/Qwen2.5-7B-Instruct";
            ((PasswordBox)section.FindName("ApiKeyPasswordBox")).Password = "sk-test-preset-key";
            section.ConnectionTestExecutor = (_, _) => Task.FromResult(
                new TranslationResponse(new TranslationResult("你好", "", "", [], []), null));
            ((Button)section.FindName("TestConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => section.TestSummaryText.Text.StartsWith("测试通过 · ", StringComparison.Ordinal) &&
                        section.TestSummaryText.Text.Contains("ms", StringComparison.Ordinal),
                "connection test summary must display elapsed ms");

            // Discard draft back to overview
            ((Button)section.FindName("BackToListHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            if (((Border)section.FindName("DraftGuardBar")).Visibility == Visibility.Visible)
            {
                ((Button)section.FindName("DraftDiscardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
            }

            // 5. One-click local & env discovery + 1-click add
            section.DiscoverFreeSourcesExecutor = _ => Task.FromResult(new LocalAndFreeDiscoveryResult(
                LocalRuntimes:
                [
                    new DiscoveredModelSource(
                        Id: "ollama",
                        DisplayName: "Ollama（本地）",
                        BaseUrl: "http://localhost:11434/v1",
                        ProviderType: ProviderType.OpenAiCompatible,
                        TextEndpoint: "/chat/completions",
                        IsLocalRuntime: true,
                        IsReachable: true,
                        HasEnvKey: false,
                        EnvVarName: null,
                        DiscoveredModels: ["qwen2.5:7b"],
                        ElapsedMs: 12,
                        StatusSummary: "发现 1 个模型"),
                ],
                EnvConfiguredSources:
                [
                    new DiscoveredModelSource(
                        Id: "siliconflow",
                        DisplayName: "SiliconFlow（硅基流动）",
                        BaseUrl: "https://api.siliconflow.cn/v1",
                        ProviderType: ProviderType.OpenAiCompatible,
                        TextEndpoint: "/chat/completions",
                        IsLocalRuntime: false,
                        IsReachable: true,
                        HasEnvKey: true,
                        EnvVarName: "SILICONFLOW_API_KEY",
                        DiscoveredModels: [],
                        ElapsedMs: 0,
                        StatusSummary: "检测到环境变量 SILICONFLOW_API_KEY"),
                ],
                CuratedPresets: ModelCatalogService.CuratedFreePresets));
            section.EnvKeyReader = sourceId => sourceId == "siliconflow" ? "sk-env-siliconflow" : null;
            section.ModelCatalogExecutor = (_, _, _) => Task.FromResult(new ModelCatalogResult(
                [new ModelDescriptor("Qwen/Qwen2.5-7B-Instruct", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog")],
                new Uri("https://api.siliconflow.cn/v1/models"),
                45,
                "OpenAiCompatible"));

            ((Button)section.FindName("ScanLocalAndEnvButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var discoveredList = (ItemsControl)section.FindName("DiscoveredSourcesList");
            Until(() => discoveredList.Visibility == Visibility.Visible &&
                        FindVisualChildren<Button>(discoveredList).Any(b => b.Tag is DiscoveredSourceRow r && r.Id == "siliconflow"),
                "manual scan must populate DiscoveredSourcesList");

            var beforeCount = ProfileManager.Load().Profiles.Count;
            var quickAddSiliconFlow = FindVisualChildren<Button>(discoveredList)
                .FirstOrDefault(b => b.Tag is DiscoveredSourceRow r && r.Id == "siliconflow")
                ?? throw new InvalidOperationException("Quick-add button for SiliconFlow not found");
            quickAddSiliconFlow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => ProfileManager.Load().Profiles.Count == beforeCount + 1 &&
                        section.ProfilesListBox.Items.Cast<ProfilesRow>().Any(r => r.Name == "SiliconFlow（硅基流动）"),
                "1-click add must create, persist, and render the discovered SiliconFlow profile");

            var addedRow = section.ProfilesListBox.Items.Cast<ProfilesRow>()
                .First(r => r.Name == "SiliconFlow（硅基流动）");
            Program.Assert(addedRow.CapabilityBadges is not null &&
                           addedRow.CapabilityBadges.Contains("文本") &&
                           addedRow.CapabilityBadges.Contains("流式"),
                "ProfilesListBox row must expose capability pills");
            Program.Assert(addedRow.LatencyVisibility == Visibility.Visible &&
                           !string.IsNullOrWhiteSpace(addedRow.LatencyText),
                "verified 1-click profile must show latency pill in ProfilesListBox");

            // 6. P0 #7: When a user engine is active (PreferFreeEngine == false), previously selected
            // free route must NOT show 当前线路 badge and MUST allow clicking 设为当前 to switch back.
            var cfg = ProfileManager.Load();
            cfg.ActiveProfileId = addedRow.Id;
            cfg.PreferFreeEngine = false;
            ProfileManager.Save(cfg);
            section.RefreshProfilesList();
            ((TabControl)section.FindName("ServicesTabs")).SelectedItem = section.FindName("PublicServicesTab");
            Pump(); window.UpdateLayout();
            var inactiveFreeRows = freeList.ItemsSource!.Cast<FreeRouteRow>().ToList();
            Program.Assert(inactiveFreeRows.All(r => r.ActiveBadgeVisibility == Visibility.Collapsed && r.SelectVisibility == Visibility.Visible && !r.IsActive),
                "when a user profile is active, no free route may show 当前线路 or hide 设为当前");
            var switchBackYoudao = FindVisualChildren<Button>(freeList)
                .FirstOrDefault(b => b.Tag is FreeEngineProvider p && p == FreeEngineProvider.Youdao && Equals(b.Content, "设为当前"))
                ?? throw new InvalidOperationException("Youdao switch-back button must be visible while user engine is active");
            switchBackYoudao.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Program.Assert(ProfileManager.Load().PreferFreeEngine && ShellSettingsStore.Load().FreeEngineProvider == FreeEngineProvider.Youdao,
                "clicking 设为当前 on previously selected Youdao must switch back from user profile to Youdao");

            // 7. QuickProxyUrlTextBox Enter submits custom proxy
            ((Button)section.FindName("ProxyStatusButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var quickCustomRadio = (RadioButton)section.FindName("QuickProxyCustomRadio");
            quickCustomRadio.IsChecked = true;
            quickCustomRadio.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            var quickProxyInput = (TextBox)section.FindName("QuickProxyUrlTextBox");
            quickProxyInput.Text = "http://127.0.0.1:7899";
            quickProxyInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(quickProxyInput)!, 0, Key.Enter)
            {
                RoutedEvent = UIElement.KeyDownEvent,
            });
            Pump();
            Program.Assert(ShellSettingsStore.Load().CustomProxyUrl == "http://127.0.0.1:7899",
                "pressing Enter in QuickProxyUrlTextBox must apply custom proxy");
        }
        finally
        {
            window.Close();
            Pump();
            ShellSettingsStore.Save(originalShell);
            CoreBridge.SaveSettings(originalCore);
        }
    }

    private static void TestGeneralSectionProxyAndLiveTranslate(string root)
    {
        var originalShell = ShellSettingsStore.Load();
        var section = new GeneralSection();
        var window = new Window
        {
            Content = section,
            Width = 720,
            Height = 760,
            Left = -12000,
            Top = -12000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Background = (Brush)Application.Current.Resources["CanvasBrush"],
        };

        try
        {
            window.Show();
            Pump();

            // 1. Proxy Direct mode persists & syncs CoreBridge
            section.ProxyDirect.IsChecked = true;
            section.ProxyDirect.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Program.Assert(ShellSettingsStore.Load().NetworkProxyMode == NetworkProxyMode.Direct,
                "ProxyDirect must persist NetworkProxyMode.Direct");
            Program.Assert(CoreBridge.GetNetworkProxy().Mode == "Direct",
                "ProxyDirect must sync CoreBridge.ConfigureNetworkProxy(Direct)");

            // 2. Proxy Custom validation rejects malformed URL without syncing bad URL to CoreBridge
            section.ProxyCustom.IsChecked = true;
            section.ProxyCustom.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            section.CustomProxyUrlInput.Text = "not-a-valid-proxy";
            ((Button)section.FindName("ApplyCustomProxyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Program.Assert(section.ProxyValidationHint.Visibility == Visibility.Visible,
                "malformed custom proxy URL must show inline validation error");
            Program.Assert(CoreBridge.GetNetworkProxy().Mode != "Custom",
                "malformed proxy URL must not configure Rust core in Custom mode");

            // 3. Valid custom proxy URL persists & syncs CoreBridge + domestic bypass
            section.CustomProxyUrlInput.Text = "http://127.0.0.1:7890";
            ((Button)section.FindName("ApplyCustomProxyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var savedCustom = ShellSettingsStore.Load();
            Program.Assert(savedCustom.NetworkProxyMode == NetworkProxyMode.Custom &&
                           savedCustom.CustomProxyUrl == "http://127.0.0.1:7890",
                "valid custom proxy must persist to ShellSettings");
            var coreProxy = CoreBridge.GetNetworkProxy();
            Program.Assert(coreProxy.Mode == "Custom" &&
                           coreProxy.ProxyUrl == "http://127.0.0.1:7890" &&
                           !string.IsNullOrWhiteSpace(coreProxy.NoProxy),
                $"valid custom proxy must sync CoreBridge with domestic noProxy (got mode={coreProxy.Mode}, url={coreProxy.ProxyUrl}, noProxy={coreProxy.NoProxy})");

            // Toggle ProxyBypassDomestic off -> noProxy becomes null/empty
            section.ProxyBypassDomestic.IsChecked = false;
            Pump();
            Program.Assert(!ShellSettingsStore.Load().ProxyBypassDomesticRoutes,
                "ProxyBypassDomestic toggle must persist false");
            Program.Assert(string.IsNullOrEmpty(CoreBridge.GetNetworkProxy().NoProxy),
                "disabling domestic bypass must clear CoreBridge noProxy");

            // 4. Live translate & QuickSearch window mode persistence
            Helpers.SelectComboByTag(section.LiveTranslateDelay, "300");
            Helpers.SelectComboByTag(section.LiveTranslateScope, "AllEngines");
            Helpers.SelectComboByTag(section.QuickSearchMode, "Expanded");
            Pump();
            var savedLive = ShellSettingsStore.Load();
            Program.Assert(savedLive.LiveTranslateDelayMs == 300 &&
                           savedLive.LiveTranslateScope == LiveTranslateScope.AllEngines &&
                           savedLive.QuickSearchWindowMode == QuickSearchWindowMode.Expanded,
                "live-translate delay/scope and quick-search window mode must persist to ShellSettings");

            Helpers.SelectComboByTag(section.LiveTranslateDelay, "800");
            section.LiveTranslate.IsChecked = false;
            Pump();
            savedLive = ShellSettingsStore.Load();
            Program.Assert(!savedLive.LiveTranslateEnabled && savedLive.LiveTranslateDelayMs == 800,
                "disabling LiveTranslate and choosing 800ms delay must persist");

            // 5. Uniform width of stacked QuickSearch ComboBoxes & collapsed empty StartupStateHint
            Program.Assert(
                Math.Abs(section.LiveTranslateDelay.Width - section.LiveTranslateScope.Width) < 0.1 &&
                Math.Abs(section.LiveTranslateScope.Width - section.QuickSearchMode.Width) < 0.1,
                "QuickSearch ComboBoxes under 查词窗口 must share uniform width");
            Program.Assert(section.StartupStateHintText.Visibility == Visibility.Collapsed,
                "StartupStateHint must stay Collapsed when empty");

            // 6. Clearing CustomProxyUrlInput clears persisted CustomProxyUrl
            section.ProxyCustom.IsChecked = true;
            section.ProxyCustom.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            section.CustomProxyUrlInput.Text = string.Empty;
            section.CustomProxyUrlInput.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Program.Assert(string.IsNullOrEmpty(ShellSettingsStore.Load().CustomProxyUrl),
                "clearing CustomProxyUrlInput must clear stored CustomProxyUrl");
        }
        finally
        {
            window.Close();
            Pump();
            ShellSettingsStore.Save(originalShell);
            App.SyncNetworkProxyToCore(originalShell);
        }
    }

    private static void TestTranslateSectionFreeProvidersAndDomesticTips()
    {
        var originalShell = ShellSettingsStore.Load();
        var originalConfig = ProfileManager.Load();
        var section = new TranslateSection();
        var window = new Window
        {
            Content = section,
            Width = 760,
            Height = 640,
            Left = -12000,
            Top = -12000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Background = (Brush)Application.Current.Resources["CanvasBrush"],
        };

        try
        {
            // Ensure no configured user engine so UnconfiguredGuidePanel is rendered
            foreach (var p in originalConfig.Profiles.ToList())
            {
                ProfileManager.TryDeleteProfile(originalConfig, p.Id, out _, out _, out _);
            }
            ProfileManager.Save(originalConfig);

            window.Show();
            Pump();

            foreach (var provider in new[]
                     {
                         FreeEngineProvider.Youdao,
                         FreeEngineProvider.Bing,
                         FreeEngineProvider.Auto,
                         FreeEngineProvider.MyMemory,
                         FreeEngineProvider.Yandex,
                         FreeEngineProvider.Google,
                     })
            {
                ShellSettingsStore.Update(c => c with
                {
                    FreeEngineConsent = FreeEngineConsent.Allowed,
                    FreeEngineProvider = provider,
                });
                section.RefreshAfterSettingsChanged();
                Pump();

                var guideTitle = (TextBlock)section.FindName("GuideTitle");
                Program.Assert(guideTitle.Text.Contains(EngineWording.NameFor(provider), StringComparison.Ordinal),
                    $"TranslateSection GuideTitle must name {provider}, got '{guideTitle.Text}'");

                var statusText = TranslateSection.FormatFreeEngineEnabledStatus(provider);
                Program.Assert(statusText.Contains(EngineWording.HostFor(provider), StringComparison.Ordinal),
                    $"TranslateSection status must include EngineWording.HostFor({provider}), got '{statusText}'");
            }

            var tipsPanel = (Border)section.FindName("DomesticRouteTipsPanel");
            Program.Assert(tipsPanel is not null, "TranslateSection must include DomesticRouteTipsPanel");
            var baiduBtn = (Button)section.FindName("BaiduOpenPlatformLink");
            var youdaoBtn = (Button)section.FindName("YoudaoOpenPlatformLink");
            Program.Assert(Equals(baiduBtn.Tag, "https://fanyi-api.baidu.com") &&
                           Equals(youdaoBtn.Tag, "https://ai.youdao.com"),
                "DomesticRouteTipsPanel must expose Baidu and Youdao official open-platform links");
        }
        finally
        {
            window.Close();
            Pump();
            ShellSettingsStore.Save(originalShell);
            ProfileManager.Save(originalConfig);
        }
    }

    private static void TestPrivacyAndFirstRunUiRegressions()
    {
        var originalShell = ShellSettingsStore.Load();
        try
        {
            var privacy = new PrivacySection();
            var ocrList = (ListBox)privacy.FindName("OcrLanguagesListBox");
            Program.Assert(ocrList.IsHitTestVisible,
                "OcrLanguagesListBox must remain hit-test visible so users with >4 language packs can scroll");

            ShellSettingsStore.Save(originalShell with
            {
                HasCompletedOnboarding = true,
                StartWithWindows = false,
                FreeEngineConsent = FreeEngineConsent.Denied,
            });
            var wizard = new FirstRunWindow
            {
                Left = -12000,
                Top = -12000,
                ShowInTaskbar = false,
            };
            try
            {
                wizard.Show();
                Pump();
                Program.Assert(wizard.FindName("StepScroller") is ScrollViewer,
                    "FirstRunWindow must wrap steps in StepScroller");
                var startupToggle = (ToggleButton)wizard.FindName("StartupToggle");
                Program.Assert(startupToggle.IsChecked == false,
                    "FirstRunWindow StartupToggle must match StartWithWindows=false");
                wizard.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(wizard)!, 0, Key.Escape)
                {
                    RoutedEvent = UIElement.KeyDownEvent,
                });
                Pump();
                var after = ShellSettingsStore.Load();
                Program.Assert(after.FreeEngineConsent == FreeEngineConsent.Denied && !after.StartWithWindows,
                    "pressing Esc on reopened FirstRunWindow must not overwrite existing settings");
            }
            finally
            {
                if (wizard.IsLoaded)
                {
                    wizard.Close();
                    Pump();
                }
            }
        }
        finally
        {
            ShellSettingsStore.Save(originalShell);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Until(Func<bool> test, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!test() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump();
            Thread.Sleep(10);
        }
        if (!test())
        {
            throw new InvalidOperationException(error);
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
