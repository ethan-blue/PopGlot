using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// The main window is a work surface only: the translate workbench and the
/// library. All settings — including the save bar — live in SettingsWindow;
/// the footer is a quiet status line plus the entry to settings.
/// </summary>
public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore? _vocabulary;
    private readonly TranslationCoordinator _coordinator;

    internal MainWindow(ShellSettings shellSettings, HistoryStore history, VocabularyStore? vocabulary = null)
    {
        _history = history;
        _vocabulary = vocabulary;
        _coordinator = new TranslationCoordinator(history, vocabulary);

        InitializeComponent();

        TranslateSection.Initialize(_coordinator, vocabulary);
        LibrarySection.Initialize(history, vocabulary);

        // Wire up cross-section events.
        LibrarySection.LoadToTranslate += OnLoadToTranslate;
        LibrarySection.StatusChanged += SetStatus;

        RefreshEngineStatus();
        RefreshEngineStatusOnActivated();

        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += (_, _) => ThemeService.ApplyWindowChrome(this);
        StateChanged += (_, _) => UpdateMaximizeButtonGlyph();
    }

    /// <summary>Entry point into SettingsWindow, wired by App.</summary>
    internal Action? OpenSettings { get; set; }

    /// <summary>Tray balloon used for the one-time close-to-tray hint.</summary>
    internal Action<string, string>? NotifyTray { get; set; }

    internal bool AllowClose { get; set; }

    private bool _closeHintShown;

    // ================= Window Caption Controls =================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonGlyph();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    // The sidebar entry is the one settings entry of the main window; the
    // old footer duplicate was removed so "打开设置" names exactly one control.
    private void NavSettingsButton_Click(object sender, RoutedEventArgs e) =>
        OpenSettings?.Invoke();

    private void UpdateMaximizeButtonGlyph()
    {
        if (MaximizeBtn is null) return;
        Ui.SetIcon(
            MaximizeBtn,
            (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconCaptionRestore" : "IconCaptionMax"));
        MaximizeBtn.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";
    }

    // ================= Engine status footer =================

    private void RefreshEngineStatusOnActivated() =>
        Activated += (_, _) => RefreshEngineStatus();

    /// <summary>Quiet picture of what actually runs right now, in the footer.</summary>
    internal void RefreshEngineStatus()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            // 以实际生效的引擎配置为准（快速切换器可选「内置免费引擎」，
            // 此时即使凭据库里有残留 key 也不算配置了模型服务）。
            var activeProfile = ProfileManager.Load().TryGetActiveProfile();
            var hasKey = activeProfile is not null &&
                CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(activeProfile));
            var consent = ShellSettingsStore.Load().FreeEngineConsent;
            var (summary, tone) = DescribeEngine(settings, hasKey, consent);
            if (UsesFreeEngine(settings, hasKey, consent))
            {
                // The free engine is the active text route: the probe result
                // replaces the static guess below, so show a probing state.
                EngineSummary.Text = "内置免费引擎 · 检测中…";
            }
            else
            {
                EngineSummary.Text = summary + " · " + DescribeUploadNote(settings, hasKey);
            }
            EngineDot.Background = (Brush)FindResource(tone switch
            {
                StatusTone.Error => "DangerBrush",
                StatusTone.Warning => "WarningBrush",
                StatusTone.Info => "TextSecondaryBrush",
                _ => "SuccessBrush",
            });

            // When the built-in free engine is the active text route, probe it
            // once so the footer shows verified reachability, not a guess.
            if (UsesFreeEngine(settings, hasKey, consent))
            {
                _ = UpdateFreeEngineHealthAsync(force: false);
            }
        }
        catch (Exception)
        {
            // The credential vault (Win32Exception) and profile store can
            // both fail transiently; the footer must degrade quietly on
            // every alt-tab instead of crashing the window.
            EngineSummary.Text = "配置不可用";
        }
    }

    private static bool UsesFreeEngine(ProviderSettings settings, bool hasKey, FreeEngineConsent consent) =>
        !settings.SafeDevMode &&
        settings.NetworkEnabled &&
        !hasKey &&
        !settings.TargetsLocalRuntime &&
        consent != FreeEngineConsent.Denied;

    /// <summary>
    /// Probes the free engine and paints the result into the footer — but only
    /// when the free engine is the active text route. With a configured
    /// provider the footer must keep showing the active model; the probe
    /// result is still cached and surfaces in the switcher menu.
    /// </summary>
    private async Task UpdateFreeEngineHealthAsync(bool force)
    {
        try
        {
            var paintsFooter = IsActiveRouteFreeEngine();
            if (force && paintsFooter)
            {
                EngineSummary.Text = "内置免费引擎 · 检测中…";
            }
            var health = await FreeTranslateService.GetHealthAsync(force);
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                return; // window may be gone; RefreshEngineStatus will repaint next time
            }
            if (!paintsFooter)
            {
                return;
            }
            if (health.Ok)
            {
                EngineSummary.Text = $"免费引擎可用 · {health.LatencyMs} ms";
                EngineDot.Background = (Brush)FindResource("SuccessBrush");
                EngineHealthButton.ToolTip = "免费引擎可用 · 点击重新检测";
            }
            else
            {
                EngineSummary.Text = "免费引擎不可用";
                EngineDot.Background = (Brush)FindResource("WarningBrush");
                EngineHealthButton.ToolTip =
                    $"免费引擎不可用：{health.Error} · 点击重新检测";
            }
        }
        catch (Exception)
        {
            // Probe failures already land in health.Error; never crash the footer.
        }
    }

    private bool IsActiveRouteFreeEngine()
    {
        try
        {
            var settings = CoreBridge.GetSettings();
            var activeProfile = ProfileManager.Load().TryGetActiveProfile();
            var hasKey = activeProfile is not null &&
                CredentialStore.HasApiKey(ProfileManager.ResolveCredentialTargetFor(activeProfile));
            var consent = ShellSettingsStore.Load().FreeEngineConsent;
            return UsesFreeEngine(settings, hasKey, consent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async void EngineHealthButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }
        ShowEngineSwitchMenu(button);
        // 打开菜单的同时后台刷新一次免费引擎探测，让菜单里的状态尽量新鲜。
        await UpdateFreeEngineHealthAsync(force: true);
    }

    /// <summary>
    /// 右下角快速切换器：点击即列出已配置的文字/图片引擎，选中立即生效，
    /// 不再需要绕进设置页。免费引擎是兜底线路，只展示状态不可选。
    /// </summary>
    private void ShowEngineSwitchMenu(Button anchor)
    {
        CoreProductConfig config;
        try
        {
            config = ProfileManager.Load();
        }
        catch (Exception exception)
        {
            SetStatus($"无法加载引擎列表：{exception.Message}", StatusTone.Error);
            return;
        }

        var menu = new ContextMenu();
        var activeId = config.TryGetActiveProfile()?.Id;
        var currentVisionId = config.VisionProfileId;

        menu.Items.Add(MakeMenuHeader("文字引擎"));
        var textProfiles = config.Profiles.Where(p => p.SupportsText).ToList();
        if (textProfiles.Count == 0)
        {
            menu.Items.Add(MakeDisabledItem("尚未配置引擎"));
        }
        foreach (var profile in textProfiles)
        {
            var id = profile.Id;
            var isActive = profile.Id == activeId;
            var item = new MenuItem
            {
                Header = $"{profile.Name} · {profile.TextModel}" + (isActive ? "（当前）" : string.Empty),
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = isActive ? MakeActiveCheck() : null,
            };
            item.Click += (_, _) => SwitchTextEngine(id);
            menu.Items.Add(item);
        }

        // 免费引擎是可显式选择的文字线路（仅文字；截图视觉线路不变），
        // 并附带最近一次探测结果，选中前就知道通不通。
        var freeActive = config.PreferFreeEngine;
        var freeState = FreeTranslateService.LastHealth.Ok
            ? $"可用 · {FreeTranslateService.LastHealth.LatencyMs} ms"
            : "当前不可用";
        var freeItem = new MenuItem
        {
            Header = $"内置免费引擎 · {freeState} · 仅文字" + (freeActive ? "（当前）" : string.Empty),
            FontWeight = freeActive ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = freeActive ? MakeActiveCheck() : null,
        };
        freeItem.Click += async (_, _) => await SwitchToFreeEngineAsync();
        menu.Items.Add(freeItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuHeader("图片引擎"));
        var followActive = string.IsNullOrEmpty(currentVisionId);
        var visionFollow = new MenuItem
        {
            Header = "跟随文字引擎" + (followActive ? "（当前）" : string.Empty),
            FontWeight = followActive ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = followActive ? MakeActiveCheck() : null,
        };
        visionFollow.Click += (_, _) => SwitchVisionEngine(null);
        menu.Items.Add(visionFollow);
        foreach (var profile in config.Profiles.Where(p =>
                     p.SupportsVision && !string.IsNullOrWhiteSpace(p.VisionModel)))
        {
            var id = profile.Id;
            var isActive = profile.Id == currentVisionId;
            var item = new MenuItem
            {
                Header = $"{profile.Name} · {profile.VisionModel}" + (isActive ? "（当前）" : string.Empty),
                FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Icon = isActive ? MakeActiveCheck() : null,
            };
            item.Click += (_, _) => SwitchVisionEngine(id);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "管理引擎…" };
        manage.Click += (_, _) => OpenSettings?.Invoke();
        menu.Items.Add(manage);
        var reprobe = new MenuItem { Header = "重新检测免费引擎" };
        reprobe.Click += async (_, _) => await UpdateFreeEngineHealthAsync(force: true);
        menu.Items.Add(reprobe);

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.PlacementRectangle = new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        menu.IsOpen = true;
    }

    private static TextBlock MakeActiveCheck() => new()
    {
        Text = "✓",
        FontSize = 13,
        FontWeight = FontWeights.Bold,
        Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
    };

    private static System.Windows.Controls.MenuItem MakeMenuHeader(string text) => new()
    {
        Header = text,
        IsEnabled = false,
        FontWeight = FontWeights.SemiBold,
    };

    private static System.Windows.Controls.MenuItem MakeDisabledItem(string text) => new()
    {
        Header = text,
        IsEnabled = false,
    };

    private async void SwitchTextEngine(string profileId)
    {
        try
        {
            SetStatus("正在切换文字引擎…", StatusTone.Info);
            // 持久化走后台：写盘（含杀软扫描）在 UI 线程上会造成窗口卡死。
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchActiveProfile(profileId, out var message);
                return (success, message);
            });
            SetStatus(ok ? "已切换文字引擎，即时生效。" : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            // async void: an escapee here would kill the process via the
            // dispatcher; the switcher must degrade to a status line.
            SetStatus($"切换文字引擎失败：{exception.Message}", StatusTone.Error);
        }
    }

    private async void SwitchVisionEngine(string? profileId)
    {
        try
        {
            SetStatus("正在切换图片引擎…", StatusTone.Info);
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchVisionProfile(profileId, out var message);
                return (success, message);
            });
            SetStatus(
                ok
                    ? (profileId is null ? "图片引擎已改为跟随文字引擎。" : "已切换图片引擎，即时生效。")
                    : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            SetStatus($"切换图片引擎失败：{exception.Message}", StatusTone.Error);
        }
    }

    private async Task SwitchToFreeEngineAsync()
    {
        try
        {
            SetStatus("正在切换到内置免费引擎…", StatusTone.Info);
            var (ok, error) = await Task.Run(() =>
            {
                var success = ProfileManager.TrySwitchToFreeEngine(out var message);
                return (success, message);
            });
            SetStatus(
                ok ? "已切换到内置免费引擎（仅文字翻译）。" : error,
                ok ? StatusTone.Success : StatusTone.Error);
            RefreshEngineStatus();
        }
        catch (Exception exception)
        {
            // Invoked from an async-void menu lambda: must not throw upward.
            SetStatus($"切换到免费引擎失败：{exception.Message}", StatusTone.Error);
        }
    }

    private static (string Summary, StatusTone Tone) DescribeEngine(
        ProviderSettings settings, bool hasKey, FreeEngineConsent consent)
    {
        if (settings.SafeDevMode)
        {
            return ("安全离线模式", StatusTone.Warning);
        }
        if (!settings.NetworkEnabled)
        {
            return ("网络翻译已关闭", StatusTone.Warning);
        }
        if (!hasKey && !settings.TargetsLocalRuntime)
        {
            return consent == FreeEngineConsent.Denied
                ? ("免费引擎已关闭，且未配置模型服务", StatusTone.Warning)
                : ("内置免费引擎", StatusTone.Info);
        }
        return (string.IsNullOrWhiteSpace(settings.TextModel)
            ? "未填写文本模型"
            : settings.TextModel, StatusTone.Success);
    }

    private static string DescribeUploadNote(ProviderSettings settings, bool hasKey)
    {
        try
        {
            // Use the same profile resolver as screenshot execution. The Rust
            // legacy planner only sees mirrored settings and can describe a
            // different vision route than the one the user selected.
            var route = ProfileManager.ResolveRoute(settings, WindowsOcrService.IsSupported);
            return route.ScreenshotPipeline is ScreenshotPipeline.VisionDirect or ScreenshotPipeline.VisionOcr
                ? route.MayUploadImage ? "截图会发送到所选视觉服务" : "本地视觉服务，图片不离开本机"
                : "截图不上传，使用本地 OCR";
        }
        catch (Exception)
        {
            return settings.TargetsLocalRuntime ? "本地模型" : "在线文本服务";
        }
    }


    // ================= Cross-section event handlers =================

    private void OnLoadToTranslate(
        string source, string translation, string? explanation,
        string? sourceLang, string? targetLang, string? badge)
    {
        TranslateSection.InputBox.Text = source;
        TranslateSection.ResultBox.Text = translation;
        TranslateSection.ExplanationText.Text = explanation ?? string.Empty;
        TranslateSection.ExplanationBox.Visibility = string.IsNullOrWhiteSpace(explanation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (sourceLang is not null)
        {
            TranslateSection.SourceLangCombo.SelectedItem = LanguageCatalog.ResolveSource(sourceLang);
        }
        if (targetLang is not null)
        {
            TranslateSection.TargetLangCombo.SelectedItem = LanguageCatalog.ResolveTarget(targetLang);
        }
        TranslateSection.EngineBadge.Text = badge ?? "已载入";
        TranslateSection.StatusBlock.Text = "已载入记录。";
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
    }

    // ================= Navigation =================

    /// <summary>
    /// Compact breakpoint: below 900 DIP of content width the pages drop
    /// secondary affordances instead of squeezing both panes. Never stacks
    /// the dual panes — this is a desktop workbench.
    /// </summary>
    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 880;
        TranslateSection.SetCompact(compact);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        ShowSection((sender as RadioButton)?.Tag as string);
    }

    private void ShowSection(string? tag)
    {
        TranslateSection.Visibility = Visibility.Collapsed;
        LibrarySection.Visibility = Visibility.Collapsed;

        if (tag == "Library")
        {
            LibrarySection.ReloadHistory();
            LibrarySection.ReloadVocabulary();
            LibrarySection.Visibility = Visibility.Visible;
        }
        else
        {
            TranslateSection.Visibility = Visibility.Visible;
        }
    }

    // ================= Public API for App.xaml.cs =================

    /// <summary>Pre-fills the translate page; also receives an expanded panel session.</summary>
    internal void FocusTranslate(
        string? initialText = null,
        string? targetLang = null,
        string? sourceLang = null,
        string? existingTranslation = null)
    {
        NavTranslate.IsChecked = true;
        ShowSection("Translate");
        TranslateSection.FocusTranslate(initialText, targetLang, sourceLang, existingTranslation);
    }

    internal void ReloadHistory() => LibrarySection.ReloadHistory();

    internal void ShowShortcutConflict(string conflict) =>
        SetStatus($"快捷键注册失败 — {conflict}。请换一个组合后重试。", StatusTone.Error);

    private void SetStatus(string message, StatusTone tone)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextSecondaryBrush",
        });
        // Info covers the idle/ready state and neutral confirmations; a gray
        // dot reads as "disabled" — the accent reads as "alive" instead.
        StatusDot.Background = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "AccentBrush",
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            // 主窗口驻留托盘后释放工作集：隐藏状态下不需要保住渲染页，
            // 任务管理器中的内存随之回落，重新显示时自动调回。
            App.TrimWorkingSet();
            // The "still running" balloon shows at most once per install:
            // in-memory for this run, persisted so later runs never nag.
            if (!_closeHintShown && !ShellSettingsStore.Load().CloseHintShown)
            {
                _closeHintShown = true;
                NotifyTray?.Invoke(
                    "PopGlot 还在运行",
                    "窗口已最小化到托盘；划词、截图翻译与快捷键仍然可用。托盘图标右键退出。");
                try
                {
                    var settings = ShellSettingsStore.Load();
                    ShellSettingsStore.Save(settings with { CloseHintShown = true });
                }
                catch (Exception)
                {
                    // Best-effort persistence; the in-memory flag still gates this run.
                }
            }
        }
        base.OnClosing(e);
    }
}
