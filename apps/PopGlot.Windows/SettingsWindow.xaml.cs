using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// Lifecycle of the settings form. Transitions are driven by comparing the
/// live form against the saved baseline — never by one-way flags — so editing
/// a value back to its saved state returns the window to Clean on its own.
/// </summary>
internal enum SettingsEditState
{
    /// <summary>Programmatic load in progress; change events are ignored.</summary>
    Loading,

    /// <summary>The form matches the persisted baseline exactly.</summary>
    Clean,

    /// <summary>The form differs from the baseline; the save bar is shown.</summary>
    Dirty,

    /// <summary>A save is committing; re-entrant edits and close are refused.</summary>
    Saving,
}

/// <summary>
/// The dedicated settings window: the translation engine, general, shortcuts,
/// and privacy pages live here — never inside the main window. It is the only
/// surface with the save bar, and the save/revert buttons appear only while a
/// draft exists.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore? _vocabulary;
    private ShellSettings _shellSettings;
    private bool _loading = true;
    private SettingsEditState _state = SettingsEditState.Loading;
    private string _settingsBaseline = string.Empty;
    private string _routeBaseline = string.Empty;
    private bool _routePendingShown;
    private readonly EventHandler _themeChangedHandler;

    /// <summary>True while the form differs from the saved baseline.</summary>
    internal bool IsDirty => _state == SettingsEditState.Dirty;

    /// <summary>The lifecycle state of the settings form (Loading/Clean/Dirty/Saving).</summary>
    internal SettingsEditState EditState => _state;

    internal SettingsWindow(ShellSettings shellSettings, HistoryStore history, VocabularyStore? vocabulary = null)
    {
        _shellSettings = shellSettings;
        _history = history;
        _vocabulary = vocabulary;
        _themeChangedHandler = (_, _) => ThemeService.ApplyWindowChrome(this);

        InitializeComponent();

        DataSection.Initialize(history, vocabulary);
        CaptureSection.SetShellSettings(shellSettings);

        ProviderSection.StatusChanged += SetStatus;
        ProviderSection.ProfileChanged += () =>
        {
            CaptureSection.RefreshRoutePreview();
            // Running requests keep the snapshot they started with; only
            // translations started afterwards see the new service.
            SetStatus("模型服务已更新，后续翻译生效。", StatusTone.Info);
        };
        CaptureSection.StatusChanged += SetStatus;
        CaptureSection.ProviderDirty += EvaluateDraftState;
        CaptureSection.SidebarChanged += () => _shellSettings = CaptureSection.CurrentShellSettings;
        DataSection.StatusChanged += SetStatus;
        DataSection.DataCleared += () => LocalDataCleared?.Invoke();

        HookDirtyTracking();
        LoadAll();
        _loading = false;
        ShowPage("Provider");

        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += _themeChangedHandler;
        StateChanged += (_, _) => UpdateMaximizeButtonGlyph();

        foreach (var recorder in ShortcutRecorders())
        {
            recorder.RecordingStateChanged += (_, recording) => SetHotkeysSuspended?.Invoke(recording);
        }
    }

    /// <summary>Registers hotkeys and applies theme; returns false on conflict.</summary>
    internal Func<ShellSettings, bool>? ApplyShellSettings { get; init; }

    /// <summary>Pauses global shortcuts while one shortcut field records.</summary>
    internal Action<bool>? SetHotkeysSuspended { get; init; }

    /// <summary>Raised after history or vocabulary was wiped so open windows can refresh.</summary>
    internal event Action? LocalDataCleared;

    // ================= Dirty tracking =================

    private void HookDirtyTracking()
    {
        void Watch(ToggleButton toggle)
        {
            toggle.Checked += MarkDirtyHandler;
            toggle.Unchecked += MarkDirtyHandler;
        }

        Watch(GeneralSection.CloseOnFocusLoss);
        Watch(GeneralSection.AutoCopy);
        Watch(GeneralSection.StartWithWindows);
        Watch(GeneralSection.IncludeExplanation);
        Watch(GeneralSection.ProtectTokens);
        // Theme applies immediately and is persisted on save; changing it is
        // still a draft until saved so a revert restores the applied theme.
        GeneralSection.ThemeCombo.SelectionChanged += MarkDirtyHandler;

        ShortcutsSection.SelectionHotkey.Recorded += MarkDirtyHandler;
        ShortcutsSection.ScreenshotHotkey.Recorded += MarkDirtyHandler;
        ShortcutsSection.CloseHotkey.Recorded += MarkDirtyHandler;
        ShortcutsSection.ShowWindowHotkey.Recorded += MarkDirtyHandler;

        Watch(DataSection.HistoryEnabled);
    }

    private void MarkDirtyHandler(object sender, RoutedEventArgs e) => EvaluateDraftState();

    /// <summary>
    /// Recomputes the form state from the live draft. Pure comparison against
    /// the saved baseline: a value edited back to its saved form clears the
    /// dirty flag (and the route-draft hint) on its own — no flag is ever
    /// latched on by an event.
    /// </summary>
    private void EvaluateDraftState()
    {
        if (_loading || _state is SettingsEditState.Loading or SettingsEditState.Saving)
        {
            return;
        }
        RecomputeStateFromDraft();
    }

    /// <summary>
    /// Guard-free snapshot recompute: applies whatever the live draft says to
    /// the state machine, the save bar and the route hint. This is the
    /// recovery path for a failed save, where the state may still say
    /// <see cref="SettingsEditState.Saving"/> and the ordinary guards in
    /// <see cref="EvaluateDraftState"/> would refuse to run — leaving the
    /// window stuck. A failed save keeps real edits at Dirty; a form rolled
    /// back onto the baseline returns to Clean.
    /// </summary>
    private void RecomputeStateFromDraft()
    {
        _state = StateFromDraft(CaptureSettingsDraft(), _settingsBaseline);
        UpdateSaveBar();
        UpdateRouteDraftPending();
    }

    /// <summary>
    /// The route preview hint is driven by the same snapshot comparison,
    /// restricted to the four fields the screenshot route actually reads.
    /// Toggling one of them back to its saved value drops the hint; the card
    /// then returns to showing the live route instead of a stale draft.
    /// </summary>
    private void UpdateRouteDraftPending()
    {
        var pending = HasDraftChanges(CaptureRouteDraft(), _routeBaseline);
        CaptureSection.SetRouteDraftPending(pending);
        if (_routePendingShown && !pending)
        {
            // The draft converged back onto the saved route: repaint the card
            // as the actual current route again.
            CaptureSection.RefreshRoutePreview();
        }
        _routePendingShown = pending;
    }

    /// <summary>Ordinal draft comparison; both sides are canonical snapshots.</summary>
    internal static bool HasDraftChanges(string current, string baseline) =>
        !string.Equals(current, baseline, StringComparison.Ordinal);

    /// <summary>
    /// Pure state decision shared by the live form and the logic tests: the
    /// state is whatever the draft says against the baseline — Dirty when
    /// they differ, Clean when they match. No window state is read here.
    /// </summary>
    internal static SettingsEditState StateFromDraft(string current, string baseline) =>
        HasDraftChanges(current, baseline) ? SettingsEditState.Dirty : SettingsEditState.Clean;

    private IEnumerable<HotkeyRecorder> ShortcutRecorders()
    {
        yield return ShortcutsSection.SelectionHotkey;
        yield return ShortcutsSection.ScreenshotHotkey;
        yield return ShortcutsSection.CloseHotkey;
        yield return ShortcutsSection.ShowWindowHotkey;
    }

    private string CaptureSettingsDraft() =>
        SettingsFormSnapshot.Create(
            ShortcutsSection.SelectionHotkey.BindingValue?.Serialize(),
            ShortcutsSection.ScreenshotHotkey.BindingValue?.Serialize(),
            ShortcutsSection.CloseHotkey.BindingValue?.Serialize(),
            ShortcutsSection.ShowWindowHotkey.BindingValue?.Serialize(),
            DataSection.HistoryEnabled.IsChecked == true,
            GeneralSection.CloseOnFocusLoss.IsChecked == true,
            GeneralSection.AutoCopy.IsChecked == true,
            GeneralSection.StartWithWindows.IsChecked == true,
            GeneralSection.IncludeExplanation.IsChecked == true,
            GeneralSection.ProtectTokens.IsChecked == true,
            Helpers.SelectedEnum(GeneralSection.ThemeCombo, ThemePreference.System).ToString(),
            CaptureRouteDraftSnapshot()).Serialize();

    private RouteDraftSnapshot CaptureRouteDraftSnapshot() =>
        RouteDraftSnapshot.Create(
            CaptureSection.NetworkEnabled.IsChecked == true,
            CaptureSection.SafeMode.IsChecked == true,
            CaptureSection.AllowImageUpload.IsChecked == true,
            Helpers.SelectedEnum(CaptureSection.ModeCombo, TranslationMode.Auto).ToString());

    /// <summary>Only the four fields the screenshot route resolves from.</summary>
    private string CaptureRouteDraft() => CaptureRouteDraftSnapshot().Serialize();

    /// <summary>
    /// The save bar mirrors the state machine: hidden while Clean, badge plus
    /// enabled actions while Dirty, locked with feedback while Saving.
    /// </summary>
    private void UpdateSaveBar()
    {
        var showActions = _state is SettingsEditState.Dirty or SettingsEditState.Saving;
        SaveActionsPanel.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        UnsavedBadge.Visibility = _state == SettingsEditState.Dirty
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveButton.IsEnabled = _state == SettingsEditState.Dirty;
        SaveButton.Content = _state == SettingsEditState.Saving ? "正在保存…" : "保存";
    }

    // ================= Loading =================

    private void LoadAll()
    {
        _loading = true;
        _state = SettingsEditState.Loading;
        try
        {
            LoadShellSettings(_shellSettings);
            LoadPolicySettings();
            ProviderSection.LoadActiveProfileIntoForm();
            ProviderSection.RefreshProfilesList();
            CaptureSection.LoadOcrState();
            CaptureSection.RefreshRoutePreview();
            CaptureSection.UpdateSafeModeGating();
        }
        finally
        {
            _loading = false;
        }
        // Rebuild both baselines from the freshly loaded form: everything the
        // loader wrote is by definition the saved state, so programmatic
        // assignment (theme included) can never register as a draft.
        _settingsBaseline = CaptureSettingsDraft();
        _routeBaseline = CaptureRouteDraft();
        _routePendingShown = false;
        _state = SettingsEditState.Clean;
        UpdateSaveBar();
        CaptureSection.SetRouteDraftPending(false);
    }

    private void LoadShellSettings(ShellSettings settings)
    {
        ShortcutsSection.SelectionHotkey.BindingValue = settings.SelectionHotkey;
        ShortcutsSection.ScreenshotHotkey.BindingValue = settings.ScreenshotHotkey;
        ShortcutsSection.CloseHotkey.BindingValue = settings.CloseHotkey;
        ShortcutsSection.ShowWindowHotkey.BindingValue = settings.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault;
        DataSection.HistoryEnabled.IsChecked = settings.HistoryEnabled;
        GeneralSection.CloseOnFocusLoss.IsChecked = settings.ClosePanelOnFocusLoss;
        GeneralSection.AutoCopy.IsChecked = settings.CopyTranslationAutomatically;
        GeneralSection.StartWithWindows.IsChecked = settings.StartWithWindows || StartupRegistration.IsEnabled();
        Helpers.SelectComboByTag(GeneralSection.ThemeCombo, settings.Theme.ToString());
        CaptureSection.SetShellSettings(settings);
        CaptureSection.RefreshFreeEngineState();
    }

    private void LoadPolicySettings()
    {
        GeneralSection.IsLoading = true;
        ProviderSection.IsLoading = true;
        CaptureSection.IsLoading = true;
        try
        {
            var settings = CoreBridge.GetSettings();
            CaptureSection.NetworkEnabled.IsChecked = settings.NetworkEnabled;
            CaptureSection.SafeMode.IsChecked = settings.SafeDevMode;
            CaptureSection.AllowImageUpload.IsChecked = settings.AllowImageUploadInAuto;
            GeneralSection.IncludeExplanation.IsChecked = settings.IncludeExplanation;
            GeneralSection.ProtectTokens.IsChecked = settings.ProtectCodeTokens;
            Helpers.SelectComboByTag(CaptureSection.ModeCombo, settings.Mode.ToString());
        }
        catch (Exception exception)
        {
            SetStatus($"读取设置失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            GeneralSection.IsLoading = false;
            ProviderSection.IsLoading = false;
            CaptureSection.IsLoading = false;
        }
    }

    // ================= Navigation =================

    private void SubNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && _loading)
        {
            return;
        }
        ShowPage((sender as RadioButton)?.Tag as string);
    }

    // Internal for the logic tests: page browsing must never dirty the form.
    internal void ShowPage(string? tag)
    {
        // Leaving the engine page with an unsaved editor draft is resolved
        // inline: snap back to the engine entry and show the draft guard
        // bar; the requested page opens once the draft is settled. No
        // system dialog.
        if (tag != "Provider" &&
            ProviderSection.Visibility == Visibility.Visible &&
            ProviderSection.IsEditorDirty)
        {
            NavProvider.IsChecked = true;
            ProviderSection.BeginDraftGuard(
                "切换设置页前，请先保存或放弃这个翻译引擎的未保存修改。",
                () => Dispatcher.BeginInvoke(() => ShowPage(tag)));
            return;
        }

        var page = tag switch
        {
            "General" => "General",
            "Shortcuts" => "Shortcuts",
            "Privacy" => "Privacy",
            _ => "Provider",
        };

        GeneralSection.Visibility = Visibility.Collapsed;
        ShortcutsSection.Visibility = Visibility.Collapsed;
        ProviderSection.Visibility = Visibility.Collapsed;
        PrivacyPageHost.Visibility = Visibility.Collapsed;

        switch (page)
        {
            case "Provider":
                ProviderSection.RefreshApiKeyState();
                ProviderSection.RefreshProfilesList();
                ProviderSection.Visibility = Visibility.Visible;
                break;
            case "Shortcuts":
                ShortcutsSection.Visibility = Visibility.Visible;
                break;
            case "Privacy":
                CaptureSection.LoadOcrState();
                CaptureSection.RefreshRoutePreview();
                CaptureSection.UpdateSafeModeGating();
                PrivacyPageHost.Visibility = Visibility.Visible;
                break;
            default:
                GeneralSection.Visibility = Visibility.Visible;
                break;
        }

        SettingsScroll?.ScrollToTop();
    }

    // ================= Save / revert =================

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_state != SettingsEditState.Dirty)
        {
            return; // Nothing to commit; also refuses re-entry while Saving.
        }
        _state = SettingsEditState.Saving;
        UpdateSaveBar();
        try
        {
            // ===== Validation phase: nothing is persisted below this point. =====
            var shellSettings = new ShellSettings(
                ShellSettings.CurrentSchemaVersion,
                ShortcutsSection.SelectionHotkey.BindingValue ?? _shellSettings.SelectionHotkey,
                ShortcutsSection.ScreenshotHotkey.BindingValue ?? _shellSettings.ScreenshotHotkey,
                ShortcutsSection.CloseHotkey.BindingValue ?? _shellSettings.CloseHotkey,
                DataSection.HistoryEnabled.IsChecked == true,
                Helpers.SelectedEnum(GeneralSection.ThemeCombo, ThemePreference.System),
                GeneralSection.CloseOnFocusLoss.IsChecked == true,
                GeneralSection.AutoCopy.IsChecked == true,
                GeneralSection.StartWithWindows.IsChecked == true,
                ShortcutsSection.ShowWindowHotkey.BindingValue ?? _shellSettings.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault,
                FreeEngineConsent: _shellSettings.FreeEngineConsent);

            var validationError = shellSettings.ValidateHotkeys();
            if (validationError is not null)
            {
                throw new InvalidOperationException($"{validationError}未保存任何修改。");
            }
            // Registering hotkeys is the last system-level check that can
            // fail; it runs before any write so a conflicting combination
            // cannot leave a half-saved state behind.
            if (ApplyShellSettings is not null && !ApplyShellSettings(shellSettings))
            {
                throw new InvalidOperationException(
                    "快捷键注册失败，请换一个未被占用的组合。未保存任何修改。");
            }

            // ===== Commit phase =====
            var policySettings = BuildPolicySettingsFromForm();
            var previousCoreSettings = CoreBridge.GetSettings();
            try
            {
                CoreBridge.SaveSettings(policySettings);
            }
            catch (Exception commitException)
            {
                _ = ApplyShellSettings?.Invoke(_shellSettings);
                throw new InvalidOperationException(
                    $"策略设置未能写入（{commitException.Message}）。已恢复原快捷键，其他设置未改动。");
            }

            try
            {
                ShellSettingsStore.Save(shellSettings);
            }
            catch (Exception commitException)
            {
                // Roll back everything the commit already touched.
                CoreBridge.SaveSettings(previousCoreSettings);
                _ = ApplyShellSettings?.Invoke(_shellSettings);
                throw new InvalidOperationException(
                    $"设置未能写入磁盘（{commitException.Message}）。已回滚本次全部修改。");
            }

            if (!StartupRegistration.TrySet(shellSettings.StartWithWindows))
            {
                SetStatus("设置已保存，但无法写入开机启动项（可能被安全软件拦截）。", StatusTone.Warning);
            }
            else
            {
                SetStatus("设置已保存，后续翻译生效。", StatusTone.Success);
            }

            _shellSettings = shellSettings;
            CaptureSection.SetShellSettings(shellSettings);
            CaptureSection.RefreshRoutePreview();
            // The commit landed: rebuild the baselines from the saved form so
            // the state machine returns to Clean on evidence, not on trust.
            _settingsBaseline = CaptureSettingsDraft();
            _routeBaseline = CaptureRouteDraft();
            _routePendingShown = false;
            _state = SettingsEditState.Clean;
            UpdateSaveBar();
            CaptureSection.SetRouteDraftPending(false);

            if (CaptureSection.SafeMode.IsChecked == true || CaptureSection.NetworkEnabled.IsChecked != true)
            {
                WarnAboutGates();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            if (_state == SettingsEditState.Saving)
            {
                // Nothing was committed (or the commit threw): recompute the
                // state straight from the live draft. Real edits still in the
                // form return to Dirty — the save bar comes back — and a form
                // rolled back onto the baseline returns to Clean. The draft
                // itself decides; no guard may keep the window stuck.
                RecomputeStateFromDraft();
            }
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        ProviderSection.ReloadEditorFromSaved();
        LoadAll();
        SetStatus("已放弃未保存的修改。", StatusTone.Info);
    }

    private ProviderSettings BuildPolicySettingsFromForm()
    {
        var current = CoreBridge.GetSettings();
        return current with
        {
            NetworkEnabled = CaptureSection.NetworkEnabled.IsChecked == true,
            SafeDevMode = CaptureSection.SafeMode.IsChecked == true,
            AllowImageUploadInAuto = CaptureSection.AllowImageUpload.IsChecked == true,
            Mode = Helpers.SelectedEnum(CaptureSection.ModeCombo, TranslationMode.Auto),
            IncludeExplanation = GeneralSection.IncludeExplanation.IsChecked == true,
            ProtectCodeTokens = GeneralSection.ProtectTokens.IsChecked == true,
        };
    }

    private void WarnAboutGates()
    {
        if (CaptureSection.SafeMode.IsChecked == true)
        {
            SetStatus("安全离线模式已开启：保存后所有模型请求都会被拒绝。", StatusTone.Warning);
        }
        else if (CaptureSection.NetworkEnabled.IsChecked != true)
        {
            SetStatus("「启用大模型网络翻译」已关闭：保存后模型请求会被拒绝。", StatusTone.Warning);
        }
    }

    // ================= Shared helpers =================

    private void SetStatus(string message, StatusTone tone)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "AccentBrush",
        });
        StatusDot.Background = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "AccentBrush",
        });
    }

    // ================= Window caption =================

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonGlyph();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeButtonGlyph()
    {
        if (MaximizeBtn is null) return;
        Ui.SetIcon(
            MaximizeBtn,
            (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconCaptionRestore" : "IconCaptionMax"));
        MaximizeBtn.ToolTip = WindowState == WindowState.Maximized ? "向下还原" : "最大化";
    }

    /// <summary>
    /// App 退出时强制关闭：跳过未保存草稿守卫，否则 OnClosing 取消关闭
    /// 与 Shutdown 相互纠缠会让进程卡死在退出路径上。
    /// </summary>
    internal bool ForceClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Resolve drafts inside the window instead of a system dialog: keep
        // the window open, land on the relevant surface, and say what to do.
        if (ForceClose)
        {
            base.OnClosing(e);
            return;
        }
        if (_state == SettingsEditState.Saving)
        {
            e.Cancel = true;
            return;
        }
        if (ProviderSection.IsEditorDirty)
        {
            e.Cancel = true;
            ShowPage("Provider");
            ProviderSection.BeginDraftGuard(
                "关闭设置前，请先保存或放弃这个翻译引擎的未保存修改。",
                () => Dispatcher.BeginInvoke(Close));
            return;
        }
        if (_state == SettingsEditState.Dirty)
        {
            e.Cancel = true;
            UpdateSaveBar();
            SetStatus("有未保存的修改：点「保存」提交，或「放弃修改」后再关闭。", StatusTone.Warning);
            return;
        }
        SetHotkeysSuspended?.Invoke(false);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.ThemeChanged -= _themeChangedHandler;
        base.OnClosed(e);
    }
}
