using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
            // Multi-service: the key may live on any profile's own credential
            // target, never assume the legacy default slot.
            var hasKey = CredentialStore.HasApiKey(ProfileManager.ResolveActiveCredentialTarget());
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
        catch (InvalidOperationException)
        {
            EngineSummary.Text = "配置不可用";
        }
    }

    private static bool UsesFreeEngine(ProviderSettings settings, bool hasKey, FreeEngineConsent consent) =>
        !settings.SafeDevMode &&
        settings.NetworkEnabled &&
        !hasKey &&
        !settings.TargetsLocalRuntime &&
        consent != FreeEngineConsent.Denied;

    /// <summary>Probes the free engine and paints the result into the footer.</summary>
    private async Task UpdateFreeEngineHealthAsync(bool force)
    {
        try
        {
            if (force)
            {
                EngineSummary.Text = "免费引擎 · 检测中…";
            }
            var health = await FreeTranslateService.GetHealthAsync(force);
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                return; // window may be gone; RefreshEngineStatus will repaint next time
            }
            if (health.Ok)
            {
                EngineSummary.Text = $"免费引擎可用 · {health.LatencyMs} ms";
                EngineDot.Background = (Brush)FindResource("SuccessBrush");
                EngineHealthButton.ToolTip = "免费翻译引擎连通正常 · 点击重新检测";
            }
            else
            {
                EngineSummary.Text = "免费引擎不可用";
                EngineDot.Background = (Brush)FindResource("WarningBrush");
                EngineHealthButton.ToolTip = $"免费引擎检测失败：{health.Error} · 点击重新检测";
            }
        }
        catch (Exception)
        {
            // Probe failures already land in health.Error; never crash the footer.
        }
    }

    private void EngineHealthButton_Click(object sender, RoutedEventArgs e) =>
        _ = UpdateFreeEngineHealthAsync(force: true);

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

            return route.ScreenshotPipeline == ScreenshotPipeline.VisionDirect

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
        var compact = e.NewSize.Width < 900;
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
            // The "still running" balloon shows at most once per install:
            // in-memory for this run, persisted so later runs never nag.
            if (!_closeHintShown && !ShellSettingsStore.Load().CloseHintShown)
            {
                _closeHintShown = true;
                NotifyTray?.Invoke(
                    "PopGlot 还在运行",
                    "窗口已最小化到托盘；划词、截图翻译与快捷键仍然可用。托盘图标右键可真正退出。");
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
