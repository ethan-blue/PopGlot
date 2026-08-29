using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// The dedicated settings window: general, services, shortcuts, and privacy
/// live here — never inside the main window. It is the only surface with the
/// save bar, and the save/revert buttons appear only while a draft exists.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly HistoryStore _history;
    private readonly VocabularyStore? _vocabulary;
    private ShellSettings _shellSettings;
    private bool _loading = true;
    private bool _isDirty;
    private string _settingsBaseline = string.Empty;

    internal SettingsWindow(ShellSettings shellSettings, HistoryStore history, VocabularyStore? vocabulary = null)
    {
        _shellSettings = shellSettings;
        _history = history;
        _vocabulary = vocabulary;

        InitializeComponent();

        DataSection.Initialize(history, vocabulary);
        CaptureSection.SetShellSettings(shellSettings);

        ProviderSection.StatusChanged += SetStatus;
        ProviderSection.ProfileChanged += () =>
        {
            CaptureSection.RefreshRoutePreview();
            SetStatus("模型服务已更新，立即生效。", StatusTone.Info);
        };
        CaptureSection.StatusChanged += SetStatus;
        CaptureSection.ProviderDirty += () =>
        {
            MarkDirty();
            CaptureSection.SetRouteDraftPending(true);
        };
        CaptureSection.SidebarChanged += () => _shellSettings = CaptureSection.CurrentShellSettings;
        DataSection.StatusChanged += SetStatus;
        DataSection.DataCleared += () => LocalDataCleared?.Invoke();

        HookDirtyTracking();
        LoadAll();
        _loading = false;

        ThemeService.ApplyWindowChrome(this);
        ThemeService.ThemeChanged += (_, _) => ThemeService.ApplyWindowChrome(this);
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

    private void MarkDirtyHandler(object sender, RoutedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_loading)
        {
            return;
        }
        _isDirty = !string.Equals(CaptureSettingsDraft(), _settingsBaseline, StringComparison.Ordinal);
        UpdateSaveBar();
    }

    private IEnumerable<HotkeyRecorder> ShortcutRecorders()
    {
        yield return ShortcutsSection.SelectionHotkey;
        yield return ShortcutsSection.ScreenshotHotkey;
        yield return ShortcutsSection.CloseHotkey;
        yield return ShortcutsSection.ShowWindowHotkey;
    }

    private string CaptureSettingsDraft() => string.Join('\u001f',
        ShortcutsSection.SelectionHotkey.BindingValue?.Serialize() ?? string.Empty,
        ShortcutsSection.ScreenshotHotkey.BindingValue?.Serialize() ?? string.Empty,
        ShortcutsSection.CloseHotkey.BindingValue?.Serialize() ?? string.Empty,
        ShortcutsSection.ShowWindowHotkey.BindingValue?.Serialize() ?? string.Empty,
        DataSection.HistoryEnabled.IsChecked == true ? "1" : "0",
        GeneralSection.CloseOnFocusLoss.IsChecked == true ? "1" : "0",
        GeneralSection.AutoCopy.IsChecked == true ? "1" : "0",
        GeneralSection.StartWithWindows.IsChecked == true ? "1" : "0",
        GeneralSection.IncludeExplanation.IsChecked == true ? "1" : "0",
        GeneralSection.ProtectTokens.IsChecked == true ? "1" : "0",
        Helpers.SelectedEnum(GeneralSection.ThemeCombo, ThemePreference.System).ToString(),
        CaptureSection.NetworkEnabled.IsChecked == true ? "1" : "0",
        CaptureSection.SafeMode.IsChecked == true ? "1" : "0",
        CaptureSection.AllowImageUpload.IsChecked == true ? "1" : "0",
        Helpers.SelectedEnum(CaptureSection.ModeCombo, TranslationMode.Auto).ToString());

    private void UpdateSaveBar() =>
        SaveActionsPanel.Visibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;

    // ================= Loading =================

    private void LoadAll()
    {
        _loading = true;
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
        _isDirty = false;
        _settingsBaseline = CaptureSettingsDraft();
        UpdateSaveBar();
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

    private void ShowPage(string? tag)
    {
        // Leaving the services page with an unsaved editor draft is resolved
        // inline: snap back to the services entry and show the draft guard
        // bar; the requested page opens once the draft is settled. No
        // system dialog.
        if (tag != "Provider" &&
            ProviderSection.Visibility == Visibility.Visible &&
            ProviderSection.IsEditorDirty)
        {
            NavProvider.IsChecked = true;
            ProviderSection.BeginDraftGuard(
                "切换设置页前请先处理服务草稿。",
                () => Dispatcher.BeginInvoke(() => ShowPage(tag)));
            return;
        }

        var page = tag switch
        {
            "Provider" => "Provider",
            "Shortcuts" => "Shortcuts",
            "Privacy" => "Privacy",
            _ => "General",
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
                SetStatus("设置已保存。", StatusTone.Success);
            }

            _shellSettings = shellSettings;
            CaptureSection.SetShellSettings(shellSettings);
            CaptureSection.RefreshRoutePreview();
            CaptureSection.SetRouteDraftPending(false);
            _isDirty = false;
            _settingsBaseline = CaptureSettingsDraft();
            UpdateSaveBar();

            if (CaptureSection.SafeMode.IsChecked == true || CaptureSection.NetworkEnabled.IsChecked != true)
            {
                WarnAboutGates();
            }
        }
        catch (Exception exception)
        {
            SetStatus($"保存失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        ProviderSection.ReloadEditorFromSaved();
        LoadAll();
        CaptureSection.SetRouteDraftPending(false);
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
            _ => "TextSecondaryBrush",
        });
        StatusDot.Background = (Brush)FindResource(tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextTertiaryBrush",
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

    protected override void OnClosing(CancelEventArgs e)
    {
        // Resolve drafts inside the window instead of a system dialog: keep
        // the window open, land on the relevant surface, and say what to do.
        if (ProviderSection.IsEditorDirty)
        {
            e.Cancel = true;
            ShowPage("Provider");
            ProviderSection.BeginDraftGuard(
                "关闭设置前请先处理服务草稿。",
                () => Dispatcher.BeginInvoke(Close));
            return;
        }
        if (_isDirty)
        {
            e.Cancel = true;
            UpdateSaveBar();
            SetStatus("有未保存的修改：点「保存设置」提交，或「放弃修改」后再关闭。", StatusTone.Warning);
            return;
        }
        SetHotkeysSuspended?.Invoke(false);
        base.OnClosing(e);
    }
}
