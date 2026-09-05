using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

/// <summary>One row in the service list, shaped for display.</summary>
internal sealed record ProfilesRow(
    string Id,
    string Name,
    string Summary,
    string StateText,
    Brush StateBrush,
    Brush StateTextBrush,
    System.Windows.Visibility IsDefaultBadge);

/// <summary>An entry of the default text/vision service pickers.</summary>
internal sealed record ProviderComboOption(string Id, string Label, bool IsCompatible = true)
{
    public override string ToString() => Label;
}

/// <summary>
/// Service settings as a master–detail surface: profile list on the left,
/// editor on the right. Key, connection test, and models are on the first
/// screen; protocol and base URL appear only for custom or local services.
/// </summary>
public partial class ServicesSection : System.Windows.Controls.UserControl
{
    private static readonly string[] PresetCloudHosts =
    {
        "api.openai.com",
        "api.deepseek.com",
        "generativelanguage.googleapis.com",
        "api.anthropic.com",
        "open.bigmodel.cn",
    };

    private bool _loading;
    private bool _isAdding;
    private bool _suppressListEvents;
    private bool _suppressComboEvents;
    private bool _editorDirty;
    private string _editorBaseline = string.Empty;
    private string? _editingProfileId;
    private ModelPreference _currentPreference = ModelPreference.Balanced;
    private IReadOnlyList<ModelDescriptor>? _cachedCatalogDescriptors;

    /// <summary>Session-scoped connection-test outcomes by profile id ("ok"/"auth"/…).</summary>
    private readonly Dictionary<string, string> _testOutcomes = new();

    /// <summary>
    /// Tracks vision model state across toggles of the shared-model checkbox.
    /// </summary>
    private readonly SharedVisionModelTracker _visionTracker = new();

    /// <summary>Action queued behind an unresolved editor draft.</summary>
    private Action? _pendingAfterDraft;
    private ConfirmButton? _deleteConfirm;
    private ConfirmButton? _clearKeyConfirm;
    private bool? _compactEditor;
    private readonly System.Windows.Threading.DispatcherTimer _recommendationDebounce;

    /// <summary>Raised when the section needs to show a status message.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised after a profile is saved, activated, or deleted.</summary>
    internal event Action? ProfileChanged;

    /// <summary>Raised when the editor draft becomes dirty or clean.</summary>
    internal event Action? EditorDirtyChanged;

    /// <summary>True while the editor holds unsaved changes.</summary>
    internal bool IsEditorDirty => _editorDirty;

    public ServicesSection()
    {
        InitializeComponent();
        HookEditorDirtyTracking();
        // 模型输入每次按键都重建推荐 chips 会造成持续 GC 压力；合并为
        // 停顿 250ms 后的一次刷新，预设/协议切换仍走立即刷新。
        _recommendationDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _recommendationDebounce.Tick += (_, _) =>
        {
            _recommendationDebounce.Stop();
            RefreshRecommendations();
        };
        _deleteConfirm = ConfirmButton.Attach(DeleteServiceButton, "确认删除？", DeleteSelectedProfile);
        _clearKeyConfirm = ConfirmButton.Attach(ClearKeyButton, "确认清除？", ClearKeyForCurrentProfile);
    }

    internal bool IsLoading { get => _loading; set => _loading = value; }

    // ================= Adaptive layout =================

    /// <summary>
    /// Field pairs use the same 14 DIP gutter on wide layouts and stack at the
    /// settings window's narrowest supported width. No field is ever restored
    /// into the gutter column.
    /// </summary>
    private void DetailGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }
        var compact = e.NewSize.Width < 680;
        if (_compactEditor.HasValue && compact == _compactEditor.Value)
        {
            return;
        }
        _compactEditor = compact;
        ApplyEditorLayout();
    }

    private void ApplyEditorLayout()
    {
        var compact = _compactEditor == true;
        var hasProtocol = CustomProtocolGroup.Visibility == Visibility.Visible;
        PlacePair(IdentityFieldsGrid, ServiceNamePanel, CustomProtocolGroup, compact && hasProtocol);
        Grid.SetColumnSpan(ServiceNamePanel, hasProtocol && !compact ? 1 : 3);

        var hasVisionModel = VisionModelPanel.Visibility == Visibility.Visible;
        PlacePair(ModelFieldsGrid, TextModelPanel, VisionModelPanel, compact && hasVisionModel);
        Grid.SetColumnSpan(TextModelPanel, hasVisionModel && !compact ? 1 : 3);

        PlacePair(EndpointFieldsGrid, TextEndpointPanel, VisionEndpointPanel, compact);
        PlacePair(AdvancedDetailsGrid, HeadersPanel, CapabilitiesPanel, compact);

        Grid.SetColumnSpan(ApiKeyPasswordBox, compact ? 3 : 1);
        if (compact)
        {
            Grid.SetColumn(KeyActionsPanel, 0);
            Grid.SetColumnSpan(KeyActionsPanel, 3);
            Grid.SetRow(KeyActionsPanel, 1);
            KeyActionsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            KeyActionsPanel.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetColumn(KeyActionsPanel, 2);
            Grid.SetColumnSpan(KeyActionsPanel, 1);
            Grid.SetRow(KeyActionsPanel, 0);
            KeyActionsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            KeyActionsPanel.Margin = new Thickness(0);
        }
    }

    /// <summary>Places a field pair without changing the declared gutter columns.</summary>
    private static void PlacePair(Grid grid, FrameworkElement first, FrameworkElement second, bool stacked)
    {
        Grid.SetColumn(first, 0);
        Grid.SetRow(first, 0);
        Grid.SetColumnSpan(first, stacked ? 3 : 1);
        first.Margin = new Thickness(0);
        first.HorizontalAlignment = HorizontalAlignment.Stretch;

        if (stacked)
        {
            Grid.SetColumn(second, 0);
            Grid.SetColumnSpan(second, 3);
            Grid.SetRow(second, 1);
            second.Margin = new Thickness(0, 14, 0, 0);
        }
        else
        {
            Grid.SetColumn(second, 2);
            Grid.SetColumnSpan(second, 1);
            Grid.SetRow(second, 0);
            second.Margin = new Thickness(0);
        }
        second.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    // ================= Editor dirty state =================

    /// <summary>
    /// Every editor field marks the draft dirty, and the badge plus the
    /// navigation guards make sure a draft can never be silently dropped.
    /// </summary>
    private void HookEditorDirtyTracking()
    {
        void WatchText(TextBox box) => box.TextChanged += (_, _) => MarkEditorDirty();
        // Editable ComboBoxes surface their inner TextBox via this routed event.
        void WatchCombo(ComboBox combo) => combo.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                MarkEditorDirty();
                if (!_loading)
                {
                    // 按键级重建推荐 chips 的开销不小，走防抖合并。
                    _recommendationDebounce.Stop();
                    _recommendationDebounce.Start();
                }
            }));
        void WatchToggle(System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            toggle.Checked += (_, _) => MarkEditorDirty();
            toggle.Unchecked += (_, _) => MarkEditorDirty();
        }

        WatchText(ServiceNameTextBox);
        WatchText(BaseUrlTextBox);
        WatchText(TextEndpointTextBox);
        WatchText(VisionEndpointTextBox);
        WatchText(ExtraHeadersTextBox);
        WatchText(AnthropicVersionTextBox);
        WatchCombo(TextModelCombo);
        WatchCombo(VisionModelCombo);
        WatchToggle(SupportsTextCheckBox);
        WatchToggle(SupportsVisionCheckBox);
        WatchToggle(UseTextModelForVisionCheckBox);
        WatchToggle(AllowInsecureTlsCheckBox);
        ApiKeyPasswordBox.PasswordChanged += (_, _) => MarkEditorDirty();
        ProviderTypeComboBox.SelectionChanged += (_, _) => MarkEditorDirty();
    }

    private void MarkEditorDirty()
    {
        if (_loading || EditorForm.Visibility != Visibility.Visible)
        {
            return;
        }
        var dirty = HasEditorChanges(CaptureEditorState(), _editorBaseline);
        if (_editorDirty == dirty)
        {
            return;
        }
        _editorDirty = dirty;
        UpdateEditorDirtyBadge();
        EditorDirtyChanged?.Invoke();
    }

    internal void ClearEditorDirty()
    {
        _editorBaseline = CaptureEditorState();
        _editorDirty = false;
        UpdateEditorDirtyBadge();
        EditorDirtyChanged?.Invoke();
    }

    /// <summary>
    /// Event-only dirty tracking is unreliable for editable ComboBoxes: WPF
    /// may deliver their inner TextBox change after a programmatic profile
    /// load. Compare the actual form to a saved baseline instead, so merely
    /// opening or switching an existing service never creates a fake draft,
    /// and refreshes that do not change values (model list fetch, ItemsSource
    /// rebuilds, connection tests, read-only status text) never dirty either.
    /// The snapshot is NORMALIZED: line endings, outer whitespace and blank
    /// header lines are canonicalized, so re-typing the same value with a
    /// trailing space or CRLF stays clean.
    /// </summary>
    private string CaptureEditorState()
    {
        var providerTag = (ProviderTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        return ServiceEditorSnapshot.CreateNormalized(
            ServiceNameTextBox.Text,
            providerTag,
            BaseUrlTextBox.Text,
            TextEndpointTextBox.Text,
            VisionEndpointTextBox.Text,
            TextModelCombo.Text,
            VisionModelCombo.Text,
            ExtraHeadersTextBox.Text,
            AnthropicVersionTextBox.Text,
            SupportsTextCheckBox.IsChecked == true,
            SupportsVisionCheckBox.IsChecked == true,
            UseTextModelForVisionCheckBox.IsChecked == true,
            AllowInsecureTlsCheckBox.IsChecked == true,
            ApiKeyPasswordBox.Password).Serialize();
    }

    /// <summary>Canonical single-line value: CRLF→LF, trimmed, no outer noise.</summary>
    internal static string NormalizeEditorText(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Trim();
    }

    /// <summary>
    /// Canonical multi-line header block: LF line endings, trimmed lines,
    /// blank lines dropped — equal to what ParseExtraHeaders would persist.
    /// </summary>
    internal static string NormalizeHeaderValue(string? text) => string.Join('\n',
        NormalizeEditorText(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

    internal static bool HasEditorChanges(string current, string baseline) =>
        !string.Equals(current, baseline, StringComparison.Ordinal);

    /// <summary>
    /// Toggling the shared-model checkbox must be reversible: the distinct
    /// vision model is stashed when sharing is enabled and restored when it
    /// is disabled, so check → uncheck lands back on the saved state and the
    /// draft badge disappears on its own.
    /// </summary>
    private void UseTextModelForVision_Changed(object sender, RoutedEventArgs e)
    {
        var shared = UseTextModelForVisionCheckBox.IsChecked == true;
        var (effectiveVision, enabled) = _visionTracker.OnToggleShared(
            shared,
            TextModelCombo.Text,
            VisionModelCombo.Text);
        VisionModelCombo.IsEnabled = enabled;
        VisionModelCombo.Text = effectiveVision;
        MarkEditorDirty();
        RefreshRecommendations();
    }

    private void UpdateEditorDirtyBadge() =>
        EditorDirtyBadge.Visibility = _editorDirty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Resolves an unsaved editor draft INLINE (bar above the action bar)
    /// before a pending action proceeds — switching service, page, adding, or
    /// closing. No system dialog: the bar offers save/discard/cancel and the
    /// pending action runs only after a save or discard.
    /// </summary>
    internal void BeginDraftGuard(string message, Action? proceed = null)
    {
        if (!_editorDirty)
        {
            proceed?.Invoke();
            return;
        }
        _pendingAfterDraft = proceed;
        DraftGuardText.Text = message;
        DraftGuardBar.Visibility = Visibility.Visible;
    }

    private void HideDraftGuard()
    {
        DraftGuardBar.Visibility = Visibility.Collapsed;
        _pendingAfterDraft = null;
    }

    private void DraftSave_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveService())
        {
            var proceed = _pendingAfterDraft;
            HideDraftGuard();
            proceed?.Invoke();
        }
        // Save failed: keep the bar so the user can retry or discard.
    }

    private void DraftDiscard_Click(object sender, RoutedEventArgs e)
    {
        ReloadEditorFromSaved();
        var proceed = _pendingAfterDraft;
        HideDraftGuard();
        proceed?.Invoke();
    }

    private void DraftCancel_Click(object sender, RoutedEventArgs e) => HideDraftGuard();

    // ================= Profile loading =================

    internal void LoadActiveProfileIntoForm()
    {
        var config = ProfileManager.Load();
        if (config.Profiles.Count > 0 && config.TryGetActiveProfile() is { } profile)
        {
            _editingProfileId = profile.Id;
            LoadProfileIntoForm(profile);
            RefreshApiKeyState();
        }
        ShowOverview();
    }

    /// <summary>Discards editor drafts by reloading the selected profile.</summary>
    internal void ReloadEditorFromSaved()
    {
        var config = ProfileManager.Load();
        var profile = SelectedProfile() ?? config.TryGetActiveProfile();
        if (profile is null)
        {
            ShowOverview();
            return;
        }
        _editingProfileId = profile.Id;
        _isAdding = false;
        LoadProfileIntoForm(profile);
        ShowEditorForm(addMode: false);
        RefreshApiKeyState();
        ClearEditorDirty();
        _suppressListEvents = true;
        try
        {
            SelectProfileInList(profile.Id);
        }
        finally
        {
            _suppressListEvents = false;
        }
    }

    internal void LoadProfileIntoForm(ProviderProfile profile)
    {
        var wasLoading = _loading;
        _loading = true;
        _visionTracker.Reset();
        _cachedCatalogDescriptors = null;
        try
        {
            ServiceNameTextBox.Text = profile.Name;
            Helpers.SelectComboByTag(ProviderTypeComboBox, profile.ProviderType.ToString());
            BaseUrlTextBox.Text = profile.ApiBaseUrl;
            TextEndpointTextBox.Text = profile.TextEndpoint;
            VisionEndpointTextBox.Text = profile.VisionEndpoint;
            TextModelCombo.Text = profile.TextModel;
            VisionModelCombo.Text = profile.VisionModel;
            var sharedModel = !string.IsNullOrWhiteSpace(profile.TextModel) &&
                string.Equals(profile.TextModel, profile.VisionModel, StringComparison.Ordinal);
            UseTextModelForVisionCheckBox.IsChecked = sharedModel;
            VisionModelCombo.IsEnabled = !sharedModel;
            _visionTracker.OnLoaded(profile.TextModel, profile.VisionModel);
            UpdateModelSuggestions(profile.ProviderType);
            ExtraHeadersTextBox.Text = string.Join(
                Environment.NewLine,
                profile.ExtraHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
            AnthropicVersionTextBox.Text = profile.AnthropicVersion;
            SupportsTextCheckBox.IsChecked = !string.IsNullOrWhiteSpace(profile.TextModel);
            SupportsVisionCheckBox.IsChecked = !string.IsNullOrWhiteSpace(profile.VisionModel);
            AllowInsecureTlsCheckBox.IsChecked = profile.AllowInsecureTls;
            var presetHost = IsPresetCloudHost(profile.ApiBaseUrl);
            UpdateEditorIdentity(profile.Name, profile.ProviderType, profile.ApiBaseUrl, profile.IsLocal);
            CustomProtocolGroup.Visibility = presetHost
                ? Visibility.Collapsed
                : Visibility.Visible;
            BaseUrlPanel.Visibility = presetHost
                ? Visibility.Collapsed
                : Visibility.Visible;
            // Built-in cloud providers expose key, models and the test only;
            // endpoints, headers and TLS belong to custom or local services.
            AdvancedGroup.Visibility = presetHost
                ? Visibility.Collapsed
                : Visibility.Visible;
            ApplyEditorLayout();
            ApiKeyPasswordBox.Clear();
            SetTestResult(StatusTone.Info, string.Empty, null);
            ResetModelCatalogStatus();
        }
        finally
        {
            _loading = wasLoading;
        }
        RefreshRecommendations();
        ClearEditorDirty();
    }

    internal string CurrentCredentialTarget()
    {
        if (_editingProfileId is not null)
        {
            var profile = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.CredentialTarget))
            {
                if (CredentialStore.HasApiKey(profile.CredentialTarget))
                {
                    return profile.CredentialTarget;
                }
                if (CredentialStore.HasApiKey(CredentialStore.DefaultTargetName))
                {
                    return CredentialStore.DefaultTargetName;
                }
                return profile.CredentialTarget;
            }
        }
        return CredentialStore.DefaultTargetName;
    }

    internal void RefreshApiKeyState()
    {
        try
        {
            var target = CurrentCredentialTarget();
            ApiKeyStateText.Text = CredentialStore.HasApiKey(target)
                ? "密钥已保存在 Windows 凭据管理器。输入框留空即保持不变。"
                : "尚未配置密钥。本地模型（Ollama 等）无需密钥；未配置且未允许免费引擎时不会出网。";
        }
        catch (Exception exception)
        {
            ApiKeyStateText.Text = $"无法读取密钥状态：{exception.Message}";
        }
    }

    // ================= List & default routes =================

    internal void RefreshProfilesList()
    {
        // One config read for the whole refresh: rows, brushes and the route
        // pickers all draw from this instance.
        var config = ProfileManager.Load();
        ProfilesListBox.ItemsSource = config.Profiles.Select(profile =>
        {
            var (stateText, stateTone) = DescribeProfileState(
                ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl),
                HasStoredKey(profile),
                _testOutcomes.TryGetValue(profile.Id, out var outcome) ? outcome : null);
            return new ProfilesRow(
                profile.Id,
                profile.Name,
                string.IsNullOrWhiteSpace(profile.TextModel) ? "未填模型" : profile.TextModel,
                stateText,
                ToneBrush(stateTone),
                ToneTextBrush(stateTone),
                profile.Id == config.ActiveProfileId
                    ? Visibility.Visible
                    : Visibility.Collapsed);
        }).ToList();
        ProfilesEmptyText.Visibility = config.Profiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshDefaultCombos(config);

        if (config.Profiles.Count == 0)
        {
            _editingProfileId = null;
            _isAdding = false;
            EditorForm.Visibility = Visibility.Collapsed;
            EditorEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            if (EditorForm.Visibility != Visibility.Visible)
            {
                EditorEmpty.Visibility = Visibility.Visible;
            }
            // Keep the editor's service selected when the list rebuilds, so a
            // refresh never orphans the visible draft.
            if (EditorForm.Visibility == Visibility.Visible && !_isAdding && _editingProfileId is not null)
            {
                _suppressListEvents = true;
                try
                {
                    SelectProfileInList(_editingProfileId);
                }
                finally
                {
                    _suppressListEvents = false;
                }
            }
        }
    }

    private static bool HasStoredKey(ProviderProfile profile)
    {
        try
        {
            if (CredentialStore.HasApiKey(profile.CredentialTarget))
            {
                return true;
            }
            var activeId = ProfileManager.Load().ActiveProfileId;
            // A key saved before profiles existed stays at the legacy target
            // until edited; it still counts for the active profile.
            return profile.Id == activeId &&
                CredentialStore.HasApiKey(CredentialStore.DefaultTargetName);
        }
        catch (Exception)
        {
            // The credential vault may be unavailable; report no key honestly.
            return false;
        }
    }

    /// <summary>
    /// Health state for one service. Brand colour never appears here — states
    /// are success / warning / danger / neutral only. Session test outcomes
    /// are labelled as such and never presented as permanent health.
    /// outcome: null = not tested this session; else a ClassifyTestFailure code.
    /// </summary>
    internal static (string Text, StatusTone Tone) DescribeProfileState(
        bool isLocal, bool hasKey, string? outcome)
    {
        if (isLocal && outcome is null or "ok")
        {
            return ("本地服务", StatusTone.Info);
        }
        if (!isLocal && !hasKey)
        {
            return ("缺少 Key", StatusTone.Warning);
        }
        return outcome switch
        {
            "ok" => ("文字连接已验证", StatusTone.Success),
            "auth" => ("鉴权失败", StatusTone.Error),
            "rate" => ("限流", StatusTone.Warning),
            "endpoint" => ("接口不存在", StatusTone.Error),
            "unreachable" => isLocal ? ("本地不可达", StatusTone.Error) : ("服务不可达", StatusTone.Error),
            null => ("已配置 · 尚未验证", StatusTone.Info),
            _ => ("测试失败", StatusTone.Error),
        };
    }

    /// <summary>Maps a raw test error to a session outcome code.</summary>
    internal static string ClassifyTestFailure(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("鉴权失败", StringComparison.Ordinal))
        {
            return "auth";
        }
        if (message.Contains("429", StringComparison.Ordinal))
        {
            return "rate";
        }
        if (message.Contains("404", StringComparison.Ordinal))
        {
            return "endpoint";
        }
        if (message.Contains("超时", StringComparison.Ordinal) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("未知的主机", StringComparison.Ordinal) ||
            message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("网络访问未启用", StringComparison.Ordinal))
        {
            return "unreachable";
        }
        return "fail";
    }

    /// <summary>
    /// Readiness gate for becoming the default text service. Returns null
    /// when ready, otherwise the reason the service is not ready.
    /// </summary>
    internal static string? CheckReadiness(bool isLocal, bool hasKey, string textModel, string baseUrl)
    {
        if (!isLocal && !hasKey)
        {
            return "缺少 API Key";
        }
        if (string.IsNullOrWhiteSpace(textModel))
        {
            return "缺少文字模型";
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return "缺少 Base URL";
        }
        return null;
    }

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_isAdding || _editingProfileId is null)
        {
            StatusChanged?.Invoke("请先保存服务，再设为默认。", StatusTone.Info);
            return;
        }
        try
        {
            var config = ProfileManager.Load();
            var profile = config.Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
            if (profile is null)
            {
                return;
            }
            var notReady = CheckReadiness(
                ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl),
                HasStoredKey(profile),
                profile.TextModel,
                profile.ApiBaseUrl);
            if (notReady is not null)
            {
                StatusChanged?.Invoke($"无法设为默认：{notReady}。请补全后保存。", StatusTone.Warning);
                return;
            }
            config.ActiveProfileId = profile.Id;
            config.PreferFreeEngine = false;
            ProfileManager.Save(config);
            ApplyToCore(config);
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke($"已将「{profile.Name}」设为默认文字服务，即时生效。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"设置默认服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private Brush ToneBrush(StatusTone tone) => (Brush)FindResource(tone switch
    {
        StatusTone.Success => "SuccessBrush",
        StatusTone.Warning => "WarningBrush",
        StatusTone.Error => "DangerBrush",
        _ => "TextTertiaryBrush",
    });

    private Brush ToneTextBrush(StatusTone tone) => (Brush)FindResource(tone switch
    {
        StatusTone.Success => "SuccessBrush",
        StatusTone.Warning => "WarningBrush",
        StatusTone.Error => "DangerBrush",
        _ => "TextTertiaryBrush",
    });

    private void RefreshDefaultCombos(CoreProductConfig config)
    {
        _suppressComboEvents = true;
        try
        {
            DefaultTextCombo.ItemsSource = config.Profiles
                .Where(profile => profile.SupportsText)
                .Select(profile =>
                {
                    // Unready profiles stay visible but disabled, with the
                    // blocking reason inline.
                    var notReady = CheckReadiness(
                        ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl),
                        HasStoredKey(profile),
                        profile.TextModel,
                        profile.ApiBaseUrl);
                    return new ProviderComboOption(
                        profile.Id,
                        notReady is null ? profile.Name : $"{profile.Name}（{notReady}）",
                        notReady is null);
                })
                .ToList();
            DefaultTextCombo.SelectedItem = (DefaultTextCombo.ItemsSource as List<ProviderComboOption>)?
                .FirstOrDefault(option => option.Id == config.ActiveProfileId);

            // Vision options check FULL readiness (model + credential). Text
            // and vision are independent complete routes, so protocols may
            // differ without sharing endpoints, headers or credentials.
            var textProfile = config.TryGetActiveProfile();
            var visionOptions = new List<ProviderComboOption> { new("", "跟随默认文字服务") };
            visionOptions.AddRange(config.Profiles
                .Where(profile => profile.SupportsVision)
                .Select(profile =>
                {
                    var local = ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl);
                    var missingModel = string.IsNullOrWhiteSpace(profile.VisionModel);
                    var missingKey = !local && !HasStoredKey(profile);
                    var reason = missingModel
                        ? "缺少视觉模型"
                        : missingKey
                            ? "缺少 API Key"
                            : null;
                    return new ProviderComboOption(
                        profile.Id,
                        reason is null ? profile.Name : $"{profile.Name}（{reason}）",
                        reason is null);
                }));
            DefaultVisionCombo.ItemsSource = visionOptions;
            var visionId = config.VisionProfileId ?? "";
            DefaultVisionCombo.SelectedItem = visionOptions.FirstOrDefault(option => option.Id == visionId);

            var incompatibleCount = visionOptions.Count(option => option.Id != "" && !option.IsCompatible);
            var hasIncompatible = incompatibleCount > 0;
            VisionIncompatHint.Text = hasIncompatible && textProfile is not null
                ? $"{incompatibleCount} 个图片服务尚未配置完整模型或凭据。"
                : string.Empty;
            VisionIncompatHint.Visibility = hasIncompatible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _suppressComboEvents = false;
        }
    }

    private void DefaultTextCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || _loading ||
            DefaultTextCombo.SelectedItem is not ProviderComboOption option ||
            string.IsNullOrEmpty(option.Id))
        {
            return;
        }
        try
        {
            var config = ProfileManager.Load();
            config.ActiveProfileId = option.Id;
            config.PreferFreeEngine = false;
            ProfileManager.Save(config);
            ApplyToCore(config);
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke($"默认文字服务已切换为「{option.Label}」，即时生效。", StatusTone.Success);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"切换默认服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void DefaultVisionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || _loading ||
            DefaultVisionCombo.SelectedItem is not ProviderComboOption option)
        {
            return;
        }
        try
        {
            var config = ProfileManager.Load();
            config.VisionProfileId = string.IsNullOrEmpty(option.Id) ? null : option.Id;
            ProfileManager.Save(config);
            ApplyToCore(config);
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke(string.IsNullOrEmpty(option.Id)
                ? "默认视觉服务将跟随默认文字服务。"
                : $"默认视觉服务已切换为「{option.Label}」，即时生效。", StatusTone.Info);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"切换默认视觉服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    /// <summary>
    /// Mirrors the active profile into the core via the shared implementation
    /// (also used by the footer quick switcher) so both paths stay identical.
    /// </summary>
    private static void ApplyToCore(CoreProductConfig config) =>
        ProfileManager.ApplyActiveToCore(config);

    internal void SelectProfileInList(string profileId)
    {
        var items = ProfilesListBox.ItemsSource as IEnumerable<ProfilesRow> ?? [];
        var index = items.ToList().FindIndex(row => row.Id == profileId);
        ProfilesListBox.SelectedIndex = index >= 0 ? index : -1;
    }

    // ================= Editor state =================

    private void ShowEditorForm(bool addMode)
    {
        EditorEmpty.Visibility = Visibility.Collapsed;
        EditorForm.Visibility = Visibility.Visible;
        RoutingPanel.Visibility = Visibility.Collapsed;
        PresetsPanel.Visibility = addMode ? Visibility.Visible : Visibility.Collapsed;
        ConfigFormPanel.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        EditorActionBar.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        ChooseAnotherProviderButton.Visibility = Visibility.Collapsed;
        SetDefaultButton.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        DeleteServiceButton.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        _isAdding = addMode;
        if (!_compactEditor.HasValue && DetailGrid.ActualWidth > 0)
        {
            _compactEditor = DetailGrid.ActualWidth < 680;
        }
        ApplyEditorLayout();
        UpdateSaveButtonLabel();
        UpdateDeleteTooltip();
    }

    private void ShowOverview()
    {
        HideDraftGuard();
        EditorForm.Visibility = Visibility.Collapsed;
        EditorEmpty.Visibility = Visibility.Visible;
        RoutingPanel.Visibility = Visibility.Visible;
        _isAdding = false;
        ClearEditorDirty();
        RefreshProfilesList();
    }

    /// <summary>
    /// Save never silently changes the live route: the first configured
    /// service "保存并使用", later ones just "保存服务", edits say "保存修改".
    /// </summary>
    private void UpdateSaveButtonLabel()
    {
        if (_isAdding)
        {
            SaveServiceButton.Content = ProfileManager.Load().Profiles.Count == 0
                ? "保存并使用"
                : "保存服务";
        }
        else
        {
            SaveServiceButton.Content = "保存修改";
        }
    }

    private void UpdateDeleteTooltip()
    {
        if (_isAdding || _editingProfileId is null)
        {
            DeleteServiceButton.ToolTip = null;
            return;
        }
        var config = ProfileManager.Load();
        var profile = config.Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
        if (profile is null)
        {
            DeleteServiceButton.ToolTip = null;
            return;
        }
        var isTextDefault = profile.Id == config.ActiveProfileId;
        var isVisionDefault = profile.Id == config.VisionProfileId;
        var nextDefault = config.Profiles.FirstOrDefault(p => p.Id != profile.Id);
        DeleteServiceButton.ToolTip = isTextDefault
            ? (nextDefault is null
                ? "它是当前默认文字服务；删除后没有其他可用服务，翻译将回退到已授权的内置免费引擎。"
                : $"它是当前默认文字服务；删除后默认路由将切换为「{nextDefault.Name}」。")
            : isVisionDefault
                ? "它是当前默认视觉服务；删除后视觉翻译将跟随默认文字服务。"
                : "默认路由不受影响。该服务保存的 API Key 也会一并删除。";
    }

    private ProviderProfile? SelectedProfile()
    {
        if (ProfilesListBox.SelectedItem is not ProfilesRow row)
        {
            return null;
        }
        return ProfileManager.Load().Profiles.FirstOrDefault(profile => profile.Id == row.Id);
    }

    private static ProviderProfile NewProfileDraft() => new()
    {
        Name = string.Empty,
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "https://api.openai.com/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = string.Empty,
        VisionModel = string.Empty,
        SupportsText = true,
        SupportsVision = false,
    };

    // ================= Presets =================

    private void UpdateEditorIdentity(
        string? name, ProviderType providerType, string? baseUrl, bool isLocal)
    {
        var title = string.IsNullOrWhiteSpace(name) ? "新服务" : name.Trim();
        EditorProviderTitle.Text = title;
        EditorProviderBadge.Text = title[..1].ToUpperInvariant();
        var protocol = providerType switch
        {
            ProviderType.AnthropicMessages => "Anthropic Messages",
            ProviderType.GeminiGenerateContent => "Gemini generateContent",
            ProviderType.OpenAiResponses => "OpenAI Responses",
            _ => "OpenAI 兼容",
        };
        var host = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : (string.IsNullOrWhiteSpace(baseUrl) ? "等待填写地址" : baseUrl.Trim());
        EditorProviderMeta.Text = isLocal
            ? $"本地服务 · {host}"
            : $"{protocol} · {host}";
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string preset)
        {
            return;
        }

        var isCustom = preset == "custom";
        // Presets provide protocol and official endpoints only. Models must
        // be fetched from the provider's catalog or typed by the user — a
        // preset never claims a model exists or that it supports images.
        var (type, baseUrl, endpoint, note) = preset switch
        {
            "openai" => (ProviderType.OpenAiCompatible, "https://api.openai.com/v1",
                "/chat/completions", "已填入 OpenAI 推荐配置，填写 API Key 后即可验证。"),
            "deepseek" => (ProviderType.OpenAiCompatible, "https://api.deepseek.com/v1",
                "/chat/completions", "已填入 DeepSeek 推荐配置，填写 API Key 后即可验证。"),
            "gemini" => (ProviderType.GeminiGenerateContent, "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent", "已填入 Gemini 推荐配置，填写 API Key 后即可验证。"),
            "claude" => (ProviderType.AnthropicMessages, "https://api.anthropic.com",
                "/v1/messages", "已填入 Claude 推荐配置，填写 API Key 后即可验证。"),
            "zhipu" => (ProviderType.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4",
                "/chat/completions", "已填入 GLM 推荐配置，填写 API Key 后即可验证。"),
            "ollama" => (ProviderType.OpenAiCompatible, "http://localhost:11434/v1",
                "/chat/completions", "已应用本地 Ollama 预设，无需 API Key：可直接输入或拉取本地模型。"),
            _ => (ProviderType.OpenAiCompatible, string.Empty,
                "/chat/completions", "自定义服务：请填写协议、Base URL，然后获取或输入模型。"),
        };

        _loading = true;
        _cachedCatalogDescriptors = null;
        try
        {
            if (string.IsNullOrWhiteSpace(ServiceNameTextBox.Text) ||
                ServiceNameTextBox.Text.StartsWith("新服务") ||
                ServiceNameTextBox.Text is "OpenAI" or "DeepSeek" or "Google Gemini" or "Anthropic Claude" or "智谱 GLM" or "Ollama（本地）" or "自定义服务")
            {
                ServiceNameTextBox.Text = preset switch
                {
                    "openai" => "OpenAI",
                    "deepseek" => "DeepSeek",
                    "gemini" => "Google Gemini",
                    "claude" => "Anthropic Claude",
                    "zhipu" => "智谱 GLM",
                    "ollama" => "Ollama（本地）",
                    _ => "自定义服务",
                };
            }

            Helpers.SelectComboByTag(ProviderTypeComboBox, type.ToString());
            BaseUrlTextBox.Text = baseUrl;
            TextEndpointTextBox.Text = endpoint;
            VisionEndpointTextBox.Text = endpoint;
            UpdateModelSuggestions(type);
            var (defaultTextModel, defaultVisionModel) = preset switch
            {
                "openai" => ("gpt-4o-mini", "gpt-4o-mini"),
                "deepseek" => ("deepseek-chat", string.Empty),
                "gemini" => ("gemini-3.6-flash", "gemini-3.6-flash"),
                "claude" => ("claude-3-5-sonnet-latest", "claude-3-5-sonnet-latest"),
                "zhipu" => ("glm-4-flash", "glm-4v-flash"),
                "ollama" => ("qwen2.5:7b", string.Empty),
                _ => (string.Empty, string.Empty),
            };
            TextModelCombo.Text = defaultTextModel;
            VisionModelCombo.Text = defaultVisionModel;
            AnthropicVersionTextBox.Text = "2023-06-01";
            SupportsTextCheckBox.IsChecked = !string.IsNullOrWhiteSpace(defaultTextModel);
            SupportsVisionCheckBox.IsChecked = !string.IsNullOrWhiteSpace(defaultVisionModel);
            _visionTracker.Reset();
            UseTextModelForVisionCheckBox.IsChecked = !string.IsNullOrWhiteSpace(defaultTextModel) &&
                string.Equals(defaultTextModel, defaultVisionModel, StringComparison.Ordinal);
            VisionModelCombo.IsEnabled = true;
            CustomProtocolGroup.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            BaseUrlPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            // Preset cloud hosts hide the protocol surface entirely; custom and
            // local services keep the advanced fields available.
            AdvancedGroup.Visibility = isCustom || !IsPresetCloudHost(baseUrl)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ApplyEditorLayout();
            ResetModelCatalogStatus();
        }
        finally
        {
            _loading = false;
        }

        // Provider selection is its own step. Once chosen, remove the
        // catalogue from view so credentials and models get the whole panel.
        PresetsPanel.Visibility = Visibility.Collapsed;
        ConfigFormPanel.Visibility = Visibility.Visible;
        EditorActionBar.Visibility = Visibility.Visible;
        ChooseAnotherProviderButton.Visibility = Visibility.Visible;
        SetDefaultButton.Visibility = Visibility.Collapsed;
        DeleteServiceButton.Visibility = Visibility.Collapsed;
        UpdateEditorIdentity(ServiceNameTextBox.Text, type, baseUrl, preset == "ollama");
        RefreshRecommendations();
        if (_isAdding)
        {
            // 新建流程里应用预设只是进入第二步，用户还没有真正配置任何
            // 内容：此时把预设状态当作新的干净基线，返回/关闭不再弹出
            // 「未保存修改」守卫。编辑既有服务时保持原判脏逻辑。
            ClearEditorDirty();
        }
        else
        {
            MarkEditorDirty();
        }

        StatusChanged?.Invoke(note, StatusTone.Info);
        if (isCustom)
        {
            BaseUrlTextBox.Focus();
        }
        else
        {
            ApiKeyPasswordBox.Focus();
        }
    }

    private void ChooseAnotherProvider_Click(object sender, RoutedEventArgs e)
    {
        // This is still the same unsaved add draft, so returning to the
        // catalogue is reversible and does not need a confirmation surface.
        PresetsPanel.Visibility = Visibility.Visible;
        ConfigFormPanel.Visibility = Visibility.Collapsed;
        EditorActionBar.Visibility = Visibility.Collapsed;
        ChooseAnotherProviderButton.Visibility = Visibility.Collapsed;
        ClearEditorDirty();
        StatusChanged?.Invoke("选择一个服务商继续。", StatusTone.Info);
    }

    private void BackToServices_Click(object sender, RoutedEventArgs e) =>
        BeginDraftGuard("返回服务列表前请先处理当前修改。", ShowOverview);

    private void ProviderTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderTypeComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }
        var providerType = Enum.Parse<ProviderType>(selected.Tag.ToString()!);
        var (baseUrl, endpoint) = ProviderDefaults(providerType);

        if (IsKnownDefaultUrl(BaseUrlTextBox.Text))
        {
            BaseUrlTextBox.Text = baseUrl;
        }
        if (IsKnownDefaultEndpoint(TextEndpointTextBox.Text))
        {
            TextEndpointTextBox.Text = endpoint;
        }
        if (IsKnownDefaultEndpoint(VisionEndpointTextBox.Text))
        {
            VisionEndpointTextBox.Text = endpoint;
        }
        _cachedCatalogDescriptors = null;
        UpdateModelSuggestions(providerType);
        RefreshRecommendations();
        ResetModelCatalogStatus();
    }

    private void UpdateModelSuggestions(ProviderType providerType)
    {
        // No invented model names: the picker stays empty until the user
        // fetches the provider's real catalog or types a model themselves.
        _ = providerType;
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        FetchModelsButton.IsEnabled = false;
        FetchModelsButton.Content = "获取中…";
        SetModelCatalogStatus("正在读取服务提供的模型列表…", StatusTone.Info);
        try
        {
            var draft = BuildDraftSettings();
            var typedKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? CredentialStore.LoadApiKey(CurrentCredentialTarget())
                : ApiKeyPasswordBox.Password.Trim();
            var result = await ModelCatalogService.FetchAsync(draft, typedKey ?? string.Empty);

            SetModelCatalogStatus(
                $"已从 {result.Endpoint.Host} 获取 {result.Models.Count} 个模型（{result.ProviderKind}）· " +
                $"图片输入 {DescribeCapabilityCounts(result.Models)} · {result.ElapsedMs} ms",
                StatusTone.Success);
            var wasLoading = _loading;
            _loading = true;
            try
            {
                ApplyModelSuggestions(result);
            }
            finally
            {
                _loading = wasLoading;
            }
        }
        catch (Exception exception)
        {
            SetModelCatalogStatus(DescribeModelCatalogFailure(exception), StatusTone.Error);
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
            FetchModelsButton.Content = "获取模型";
        }
    }

    private void ApplyModelSuggestions(ModelCatalogResult result)
    {
        _cachedCatalogDescriptors = result.Models;
        var ids = result.Models.Select(model => model.Id).ToList();
        // Refreshing keeps the current selection; a pick that no longer
        // exists in the catalog is flagged, never silently replaced.
        var currentText = TextModelCombo.Text;
        var currentVision = VisionModelCombo.Text;
        TextModelCombo.ItemsSource = ids;
        VisionModelCombo.ItemsSource = ids;
        TextModelCombo.Text = currentText;
        VisionModelCombo.Text = currentVision;

        if (!string.IsNullOrWhiteSpace(currentVision) &&
            !currentVision.StartsWith('{') &&
            result.Models.All(model => model.Id != currentVision))
        {
            SetModelCatalogStatus(
                $"警告：当前视觉模型「{currentVision}」不在最新列表中，可能已下线；保存前请确认。",
                StatusTone.Warning);
        }
        else if (!string.IsNullOrWhiteSpace(currentVision) &&
            result.Models.FirstOrDefault(model => model.Id == currentVision) is { VisionInput: CapabilityState.Unknown })
        {
            SetModelCatalogStatus(
                $"视觉模型「{currentVision}」存在，但目录未声明图片输入能力；请以供应商文档或连接测试确认，系统不会根据名称猜测。",
                StatusTone.Warning);
        }

        RefreshRecommendations();
    }

    private void PreferenceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || PreferenceSpeedRadio is null || PreferenceBalancedRadio is null || PreferenceQualityRadio is null)
        {
            return;
        }

        var pref = PreferenceSpeedRadio.IsChecked == true
            ? ModelPreference.Speed
            : PreferenceQualityRadio.IsChecked == true
                ? ModelPreference.Quality
                : ModelPreference.Balanced;

        _currentPreference = pref;
        RefreshRecommendations();
    }

    internal void RefreshRecommendations()
    {
        if (_loading || TextRecommendationChipsPanel is null || VisionRecommendationChipsPanel is null)
        {
            return;
        }

        var providerType = ProviderType.OpenAiCompatible;
        if (ProviderTypeComboBox.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ProviderType>(item.Tag?.ToString(), out var parsedType))
        {
            providerType = parsedType;
        }

        var isLocal = ProviderSettings.IsLocalBaseUrl(BaseUrlTextBox.Text);
        var descriptors = GetCandidateDescriptors();

        // 1. Text model recommendation
        var currentText = TextModelCombo.Text?.Trim();
        var textRequest = new ModelRecommendationRequest(
            ProviderType: providerType,
            IsLocal: isLocal,
            Models: descriptors,
            TargetUsage: ModelTargetUsage.Text,
            Preference: _currentPreference,
            CurrentModelId: currentText);

        var textResult = ModelRecommendationService.Recommend(textRequest);
        PopulateRecommendationChips(
            TextRecommendationChipsPanel,
            textResult.Candidates.Where(c => c.IsEligible).Take(3),
            isVision: false);

        var selectedTextEval = textResult.AllEvaluations.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(currentText) &&
            string.Equals(e.Model.Id?.Trim(), currentText, StringComparison.OrdinalIgnoreCase))
            ?? textResult.RecommendedModel;

        UpdateRecommendationReason(
            TextRecommendationReasonRow,
            TextRecommendationReasonText,
            TextEvidenceBadge,
            TextEvidenceBadgeText,
            selectedTextEval);

        // 2. Vision model recommendation
        var sharedVision = UseTextModelForVisionCheckBox.IsChecked == true;
        if (sharedVision)
        {
            VisionRecommendationChipsPanel.Children.Clear();
            if (VisionRecommendationReasonRow is not null)
            {
                VisionRecommendationReasonRow.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            var currentVision = VisionModelCombo.Text?.Trim();
            var visionRequest = new ModelRecommendationRequest(
                ProviderType: providerType,
                IsLocal: isLocal,
                Models: descriptors,
                TargetUsage: ModelTargetUsage.Vision,
                Preference: _currentPreference,
                CurrentModelId: currentVision);

            var visionResult = ModelRecommendationService.Recommend(visionRequest);
            PopulateRecommendationChips(
                VisionRecommendationChipsPanel,
                visionResult.Candidates.Where(c => c.IsEligible).Take(3),
                isVision: true);

            var selectedVisionEval = visionResult.AllEvaluations.FirstOrDefault(e =>
                !string.IsNullOrWhiteSpace(currentVision) &&
                string.Equals(e.Model.Id?.Trim(), currentVision, StringComparison.OrdinalIgnoreCase))
                ?? visionResult.RecommendedModel;

            UpdateRecommendationReason(
                VisionRecommendationReasonRow,
                VisionRecommendationReasonText,
                VisionEvidenceBadge,
                VisionEvidenceBadgeText,
                selectedVisionEval);
        }
    }

    private void PopulateRecommendationChips(
        WrapPanel panel,
        IEnumerable<ModelCandidateEvaluation> candidates,
        bool isVision)
    {
        panel.Children.Clear();
        var style = (Style?)(TryFindResource("ModelRecommendationChipStyle") ?? TryFindResource("ModelChipButton"));

        foreach (var candidate in candidates)
        {
            var modelId = candidate.Model.Id;
            var button = new Button
            {
                Content = modelId,
                Tag = modelId,
                Style = style,
                ToolTip = candidate.PrimaryReason,
            };

            var label = isVision ? $"推荐图片模型 {modelId}" : $"推荐文字模型 {modelId}";
            System.Windows.Automation.AutomationProperties.SetName(button, label);

            if (isVision)
            {
                button.Click += (_, _) =>
                {
                    if (UseTextModelForVisionCheckBox.IsChecked == true)
                    {
                        return;
                    }
                    VisionModelCombo.Text = modelId;
                };
            }
            else
            {
                button.Click += (_, _) =>
                {
                    TextModelCombo.Text = modelId;
                };
            }

            panel.Children.Add(button);
        }
    }

    private void UpdateRecommendationReason(
        Grid? reasonRow,
        TextBlock? reasonText,
        Border? evidenceBadge,
        TextBlock? evidenceBadgeText,
        ModelCandidateEvaluation? evaluation)
    {
        if (reasonRow is null || reasonText is null || evidenceBadge is null || evidenceBadgeText is null)
        {
            return;
        }

        if (evaluation is null || string.IsNullOrWhiteSpace(evaluation.PrimaryReason))
        {
            reasonRow.Visibility = Visibility.Collapsed;
            return;
        }

        reasonRow.Visibility = Visibility.Visible;
        reasonText.Text = evaluation.PrimaryReason;
        UpdateEvidenceBadge(evidenceBadge, evidenceBadgeText, evaluation.EvidenceSources);
    }

    private void UpdateEvidenceBadge(Border badgeBorder, TextBlock badgeText, RecommendationEvidenceSource sources)
    {
        var (text, bgKey, fgKey, borderKey) = ResolveEvidenceBadgeVisualKeys(sources, hasBenchmarkMetric: false);
        badgeText.Text = text;
        if (TryFindResource(fgKey) is Brush fg) badgeText.Foreground = fg;
        if (TryFindResource(bgKey) is Brush bg) badgeBorder.Background = bg;
        if (TryFindResource(borderKey) is Brush border) badgeBorder.BorderBrush = border;
    }

    internal IReadOnlyList<ModelDescriptor> GetCandidateDescriptors()
    {
        if (_cachedCatalogDescriptors is { Count: > 0 })
        {
            return _cachedCatalogDescriptors;
        }

        var list = new List<ModelDescriptor>();
        void AddIfNew(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var trimmed = id.Trim();
            if (list.Any(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new ModelDescriptor(trimmed, CapabilityState.Unknown, CapabilityState.Unknown, "Fallback"));
        }

        AddIfNew(TextModelCombo?.Text);
        AddIfNew(VisionModelCombo?.Text);

        if (!string.IsNullOrWhiteSpace(_editingProfileId))
        {
            var template = ProviderCatalog.Find(_editingProfileId);
            if (template is not null)
            {
                AddIfNew(template.TextModel);
                AddIfNew(template.VisionModel);
            }
        }

        return list;
    }

    internal enum EvidenceBadgeTier
    {
        LocalBenchmark,
        CatalogExplicit,
        FamilyHeuristics,
        Unknown,
    }

    internal static EvidenceBadgeTier ResolveEvidenceTier(RecommendationEvidenceSource sources, bool hasBenchmarkMetric = false)
    {
        if (hasBenchmarkMetric && sources.HasFlag(RecommendationEvidenceSource.LocalBenchmark))
        {
            return EvidenceBadgeTier.LocalBenchmark;
        }
        if (sources.HasFlag(RecommendationEvidenceSource.CatalogExplicit))
        {
            return EvidenceBadgeTier.CatalogExplicit;
        }
        if (sources.HasFlag(RecommendationEvidenceSource.FamilyHeuristics))
        {
            return EvidenceBadgeTier.FamilyHeuristics;
        }
        return EvidenceBadgeTier.Unknown;
    }

    internal static string GetEvidenceBadgeText(EvidenceBadgeTier tier) => tier switch
    {
        EvidenceBadgeTier.LocalBenchmark => "本机实测",
        EvidenceBadgeTier.CatalogExplicit => "官方声明",
        EvidenceBadgeTier.FamilyHeuristics => "系列推断",
        _ => "未声明",
    };

    internal static (string Text, string BackgroundKey, string ForegroundKey, string BorderBrushKey) ResolveEvidenceBadgeVisualKeys(
        RecommendationEvidenceSource sources,
        bool hasBenchmarkMetric = false)
    {
        var tier = ResolveEvidenceTier(sources, hasBenchmarkMetric);
        return tier switch
        {
            EvidenceBadgeTier.LocalBenchmark => ("本机实测", "SuccessSoftBrush", "SuccessBrush", "SuccessBrush"),
            EvidenceBadgeTier.CatalogExplicit => ("官方声明", "AccentSoftBrush", "AccentBrush", "AccentBorderBrush"),
            EvidenceBadgeTier.FamilyHeuristics => ("系列推断", "SurfaceRaisedBrush", "TextSecondaryBrush", "BorderSubtleBrush"),
            _ => ("未声明", "SurfaceMutedBrush", "TextTertiaryBrush", "BorderSubtleBrush"),
        };
    }

    internal static string DescribeCapabilityCounts(IReadOnlyList<ModelDescriptor> models)
    {
        var supported = models.Count(model => model.VisionInput == CapabilityState.Supported);
        var unsupported = models.Count(model => model.VisionInput == CapabilityState.Unsupported);
        var unknown = models.Count - supported - unsupported;
        return $"支持 {supported} / 不支持 {unsupported} / 未知 {unknown}";
    }

    private void ResetModelCatalogStatus()
    {
        ModelCatalogStatusText.Text = string.Empty;
        ModelCatalogStatusText.Visibility = Visibility.Collapsed;
    }

    private void SetModelCatalogStatus(string message, StatusTone tone)
    {
        ModelCatalogStatusText.Text = message;
        ModelCatalogStatusText.Foreground = tone switch
        {
            StatusTone.Success => (Brush)FindResource("SuccessBrush"),
            StatusTone.Error => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("TextTertiaryBrush"),
        };
        ModelCatalogStatusText.Visibility = Visibility.Visible;
    }

    internal static string DescribeModelCatalogFailure(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (exception is TaskCanceledException || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "获取模型超时，请检查 Base URL 和网络连接。";
        }
        if (exception is OperationCanceledException)
        {
            return "已取消获取模型。";
        }
        if (exception is HttpRequestException)
        {
            return $"无法连接模型列表接口：{message}";
        }
        return message;
    }

    private static bool IsKnownDefaultUrl(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Enum.GetValues<ProviderType>().Any(type =>
            string.Equals(ProviderDefaults(type).BaseUrl, value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownDefaultEndpoint(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        Enum.GetValues<ProviderType>().Any(type =>
            string.Equals(ProviderDefaults(type).Endpoint, value.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static (string BaseUrl, string Endpoint) ProviderDefaults(ProviderType providerType) =>
        providerType switch
        {
            ProviderType.OpenAiCompatible => ("https://api.openai.com/v1", "/chat/completions"),
            ProviderType.OpenAiResponses => ("https://api.openai.com/v1", "/responses"),
            ProviderType.AnthropicMessages => ("https://api.anthropic.com", "/v1/messages"),
            ProviderType.GeminiGenerateContent => (
                "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent"),
            _ => ("https://api.openai.com/v1", "/chat/completions"),
        };

    /// <summary>Known cloud preset hosts keep protocol/base URL out of sight.</summary>
    internal static bool IsPresetCloudHost(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }
        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) &&
            PresetCloudHosts.Any(host =>
                string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyDictionary<string, string> ParseExtraHeaders(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"自定义请求头格式无效：{line}（应为 Header: Value）");
            }
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return headers;
    }

    // ================= Draft connection test =================

    /// <summary>Builds the core settings a draft test would use; nothing is saved.</summary>
    private ProviderSettings BuildDraftSettings()
    {
        var current = CoreBridge.GetSettings();
        var textModel = TextModelCombo.Text.Trim();
        var visionModel = UseTextModelForVisionCheckBox.IsChecked == true
            ? textModel
            : VisionModelCombo.Text.Trim();
        return current with
        {
            SchemaVersion = current.SchemaVersion,
            ProviderType = Helpers.SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible),
            ApiBaseUrl = BaseUrlTextBox.Text.Trim(),
            TextEndpoint = TextEndpointTextBox.Text.Trim(),
            VisionEndpoint = VisionEndpointTextBox.Text.Trim(),
            TextModel = textModel,
            VisionModel = visionModel,
            ExtraHeaders = ParseExtraHeaders(ExtraHeadersTextBox.Text),
            AnthropicVersion = AnthropicVersionTextBox.Text.Trim(),
            SupportsText = !string.IsNullOrWhiteSpace(textModel),
            SupportsVision = !string.IsNullOrWhiteSpace(visionModel),
            AllowInsecureTls = AllowInsecureTlsCheckBox.IsChecked == true,
            ApiKeyConfigured = CredentialStore.HasApiKey(CurrentCredentialTarget()),
        };
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        SetTestResult(StatusTone.Info, "正在测试连接…", "仅发送一小段文本，不含截图。");
        try
        {
            var draft = BuildDraftSettings();
            var typedKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? CredentialStore.LoadApiKey(CurrentCredentialTarget())
                : ApiKeyPasswordBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(typedKey) && !draft.TargetsLocalRuntime)
            {
                throw new InvalidOperationException("请先填写 API Key（不会被保存），或改用本地模型地址。");
            }
            var response = await CoreBridge.TestConnectionDraftAsync(
                draft, string.IsNullOrWhiteSpace(typedKey) ? "local" : typedKey);
            var host = Uri.TryCreate(draft.ApiBaseUrl, UriKind.Absolute, out var endpointUri)
                ? endpointUri.Host
                : draft.ApiBaseUrl;
            SetTestResult(StatusTone.Success,
                $"连接成功 · {host} · HTTP {response.Diagnostics.StatusCode} · {response.Diagnostics.ElapsedMs} ms" +
                (string.IsNullOrWhiteSpace(draft.TextModel) ? "" : $" · {draft.TextModel}"),
                "草稿未保存；保存并设为默认后才用于后续翻译。连接测试只报告健康状态，不是使用前提。");
            if (_editingProfileId is not null)
            {
                _testOutcomes[_editingProfileId] = "ok";
                RefreshProfilesList();
            }
        }
        catch (Exception exception)
        {
            SetTestResult(StatusTone.Error, "连接失败", DescribeTestFailure(exception) + "（设置未被修改）");
            if (_editingProfileId is not null)
            {
                _testOutcomes[_editingProfileId] = ClassifyTestFailure(exception);
                RefreshProfilesList();
            }
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    /// <summary>Structured two-line test result: state dot + summary, bounded detail.</summary>
    private void SetTestResult(StatusTone tone, string summary, string? detail)
    {
        TestStatusPanel.Visibility = string.IsNullOrWhiteSpace(summary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TestStateDot.Fill = ToneBrush(tone);
        TestSummaryText.Text = summary;
        TestSummaryText.Foreground = tone switch
        {
            StatusTone.Success => (Brush)FindResource("SuccessBrush"),
            StatusTone.Error => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("TextSecondaryBrush"),
        };
        TestDetailText.Text = detail ?? string.Empty;
        TestDetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Turns a raw connection-test error into the next action the user can
    /// actually take; unmatched errors keep their original message.
    /// </summary>
    internal static string DescribeTestFailure(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("网络访问未启用", StringComparison.Ordinal) ||
            message.Contains("安全离线模式", StringComparison.Ordinal))
        {
            return "当前不允许出网。请在「设置 → 隐私与数据」中开启网络翻译，或配置本地模型。";
        }
        if (message.Contains("401", StringComparison.Ordinal) ||
            message.Contains("鉴权失败", StringComparison.Ordinal))
        {
            return "密钥无效或没有权限。请确认 API Key 正确且属于该服务商，然后重新粘贴。";
        }
        if (message.Contains("403", StringComparison.Ordinal))
        {
            return "服务拒绝了请求。请确认账号状态、密钥权限或所在地区是否可用。";
        }
        if (message.Contains("404", StringComparison.Ordinal))
        {
            return "接口路径不存在。请检查 Endpoint 与 Base URL 是否匹配该服务商的文档。";
        }
        if (message.Contains("429", StringComparison.Ordinal))
        {
            return "请求被限流。请稍后重试，或检查该账号的用量配额。";
        }
        if (message.Contains("500", StringComparison.Ordinal) ||
            message.Contains("502", StringComparison.Ordinal) ||
            message.Contains("503", StringComparison.Ordinal))
        {
            return "服务商暂时不可用。请稍后重试；若持续失败，检查服务商状态页。";
        }
        if (message.Contains("超时", StringComparison.Ordinal) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "连接超时。请检查网络、代理或防火墙设置。";
        }
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
        {
            return "TLS 握手失败。自签名证书需在「高级设置」中允许；否则请检查网络环境。";
        }
        if (message.Contains("未知的主机", StringComparison.Ordinal) ||
            message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase))
        {
            return "无法解析服务域名。请检查 Base URL 拼写与网络连接。";
        }
        return $"{message}。请检查网络连接与服务地址。";
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        // In add mode nothing has been persisted yet; clearing must only
        // reset the input, never the vault (the legacy default target may
        // still hold the active profile's key).
        if (_isAdding || _editingProfileId is null)
        {
            ApiKeyPasswordBox.Clear();
            SetTestResult(StatusTone.Info, string.Empty, null);
            StatusChanged?.Invoke("已清空输入框。新增服务保存后密钥才会写入本机凭据管理器。", StatusTone.Info);
        }
        // Edit mode: the ConfirmButton wrapper asks the second click inline;
        // running ClearKeyForCurrentProfile here too would wipe on the first.
    }

    private void ClearKeyForCurrentProfile()
    {
        try
        {
            var config = ProfileManager.Load();
            var profile = config.Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
            var target = profile is not null && !string.IsNullOrWhiteSpace(profile.CredentialTarget)
                ? profile.CredentialTarget
                : CredentialStore.DefaultTargetName;
            CredentialStore.SaveApiKey(string.Empty, target);
            ApiKeyPasswordBox.Clear();
            RefreshApiKeyState();
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke("该服务的 API Key 已清除；未配置密钥且未允许免费引擎时不会出网。", StatusTone.Info);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"清除 API Key 失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ================= Profile CRUD =================

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        // Starting a new service must not wipe an unsaved draft; the inline
        // guard bar resolves it first and then re-runs this action.
        BeginDraftGuard("新增服务前请先处理当前草稿。", StartAddMode);
    }

    private void StartAddMode()
    {
        _editingProfileId = null;
        _testOutcomes.Clear();
        _suppressListEvents = true;
        try
        {
            ProfilesListBox.SelectedIndex = -1;
        }
        finally
        {
            _suppressListEvents = false;
        }
        LoadProfileIntoForm(NewProfileDraft());
        CustomProtocolGroup.Visibility = Visibility.Collapsed;
        AdvancedGroup.Visibility = Visibility.Visible;
        ApplyEditorLayout();
        SetTestResult(StatusTone.Info, string.Empty, null);
        ShowEditorForm(addMode: true);
        RefreshApiKeyState();
        ClearEditorDirty();
        StatusChanged?.Invoke("先选择服务商；下一步只填写连接所需内容。", StatusTone.Info);
    }

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents)
        {
            return;
        }
        if (ProfilesListBox.SelectedItem is not ProfilesRow row)
        {
            if (ProfileManager.Load().Profiles.Count == 0)
            {
                EditorForm.Visibility = Visibility.Collapsed;
                EditorEmpty.Visibility = Visibility.Visible;
            }
            return;
        }
        // An unsaved draft must be handled before another service is opened;
        // the inline guard bar resolves it, and cancelling snaps the
        // selection back to what was being edited.
        if (_editorDirty && EditorForm.Visibility == Visibility.Visible)
        {
            var previousId = _editingProfileId;
            _suppressListEvents = true;
            try
            {
                SelectProfileInList(previousId ?? string.Empty);
            }
            finally
            {
                _suppressListEvents = false;
            }
            BeginDraftGuard(
                "切换服务前请先处理当前草稿。",
                () => OpenProfileInEditor(row.Id));
            return;
        }
        OpenProfileInEditor(row.Id);
    }

    private void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string profileId)
        {
            return;
        }
        _suppressListEvents = true;
        try
        {
            SelectProfileInList(profileId);
        }
        finally
        {
            _suppressListEvents = false;
        }
        OpenProfileInEditor(profileId);
    }

    private void OpenProfileInEditor(string profileId)
    {
        var profile = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return;
        }
        _editingProfileId = profile.Id;
        _isAdding = false;
        LoadProfileIntoForm(profile);
        ShowEditorForm(addMode: false);
        RefreshApiKeyState();
        ClearEditorDirty();
    }

    private void ProfilesListBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Single click already opens the editor in the master–detail layout.
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            StatusChanged?.Invoke("请先在列表中选择要删除的服务。", StatusTone.Info);
        }
        // A selected profile is deleted only through ConfirmButton's two-step
        // click; deleting here too would wipe on the first click.
    }

    private void DeleteSelectedProfile()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            return;
        }
        try
        {
            var config = ProfileManager.Load();
            if (!ProfileManager.TryDeleteProfile(config, profile.Id, out _, out var isTextDefault, out _))
            {
                return;
            }

            // 1. Persist config to disk first
            ProfileManager.Save(config);

            // 2. Delete credential after config is persisted
            try
            {
                CredentialStore.DeleteApiKey(profile.CredentialTarget);
            }
            catch
            {
                // Vault delete failure shouldn't crash or corrupt profile state
            }

            // 3. Clear or update running core configuration when the default service changed or emptied
            if (isTextDefault)
            {
                ApplyToCore(config);
            }

            _editingProfileId = null;
            _isAdding = false;
            _testOutcomes.Remove(profile.Id);
            _editorDirty = false;
            UpdateEditorDirtyBadge();
            EditorForm.Visibility = Visibility.Collapsed;
            EditorEmpty.Visibility = Visibility.Visible;
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            if (config.Profiles.Count == 0)
            {
                StatusChanged?.Invoke("服务已删除。未配置模型服务时，翻译将使用已授权的内置免费引擎。", StatusTone.Info);
            }
            else
            {
                StatusChanged?.Invoke(
                    $"服务已删除，默认服务切换为「{config.TryGetActiveProfile()?.Name ?? "（无）"}」。", StatusTone.Info);
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"删除服务失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void SaveService_Click(object sender, RoutedEventArgs e) => TrySaveService();

    /// <summary>
    /// Validates and persists the editor draft. Returns false when nothing
    /// was saved (validation failed or the credential write failed), leaving
    /// the draft and the saved state untouched.
    /// </summary>
    internal bool TrySaveService()
    {
        try
        {
            var name = ServiceNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("请先填写服务名称。");
            }
            var draft = BuildProfileFromForm(name);
            var config = ProfileManager.Load();

            // The final profile id and credential target are decided FIRST.
            // Only then is the key written — to this profile's own target, so
            // a DeepSeek/Gemini/Claude key can never end up in the OpenAI
            // default slot.
            var (profileId, credentialTarget) = ProfileManager.ResolveSaveTarget(config, _editingProfileId);

            // Capture the previous key so a failed profile write can restore
            // the credential vault exactly as it was.
            string? previousKey = null;
            var hadPreviousKey = false;
            try
            {
                previousKey = CredentialStore.LoadApiKey(credentialTarget);
                hadPreviousKey = !string.IsNullOrEmpty(previousKey);
            }
            catch (Exception)
            {
                // Vault read failed; a rollback attempt would fail the same way.
            }

            var typedKey = ApiKeyPasswordBox.Password?.Trim();
            var keyWritten = false;
            if (!string.IsNullOrEmpty(typedKey))
            {
                CredentialStore.SaveApiKey(typedKey, credentialTarget);
                keyWritten = true;
                ApiKeyPasswordBox.Clear();
            }

            draft.Id = profileId;
            draft.CredentialTarget = credentialTarget;
            var isFirstService = config.Profiles.Count == 0;
            var existingIndex = config.Profiles.FindIndex(p => p.Id == profileId);
            var wasActive = existingIndex >= 0 && config.Profiles[existingIndex].Id == config.ActiveProfileId;
            if (existingIndex >= 0)
            {
                config.Profiles[existingIndex] = draft;
            }
            else
            {
                config.Profiles.Add(draft);
            }
            _editingProfileId = profileId;

            // Saving never silently reroutes the app: only the very first
            // configured service activates, and editing the currently active
            // service keeps it active. Everything else needs an explicit
            // "设为文字默认".
            if (isFirstService || wasActive)
            {
                config.ActiveProfileId = draft.Id;
                config.PreferFreeEngine = false;
            }

            try
            {
                ProfileManager.Save(config);
            }
            catch
            {
                // Roll back the credential so vault and config never disagree.
                if (keyWritten)
                {
                    TryRestoreCredential(credentialTarget, previousKey, hadPreviousKey);
                }
                _editingProfileId = null;
                throw;
            }

            try
            {
                ApplyToCore(config);
            }
            catch (Exception applyException)
            {
                // Files and vault are consistent; only the running engine is
                // stale. Say so instead of pretending the save failed.
                SetTestResult(StatusTone.Info, string.Empty, null);
                RefreshProfilesList();
                _suppressListEvents = true;
                try
                {
                    SelectProfileInList(draft.Id);
                }
                finally
                {
                    _suppressListEvents = false;
                }
                ShowEditorForm(addMode: false);
                RefreshApiKeyState();
                ClearEditorDirty();
                ProfileChanged?.Invoke();
                StatusChanged?.Invoke(
                    $"服务「{draft.Name}」已保存到本机配置，但应用到运行中的引擎失败（{applyException.Message}）。重启 PopGlot 后生效。",
                    StatusTone.Warning);
                return true;
            }

            RefreshProfilesList();
            _suppressListEvents = true;
            try
            {
                SelectProfileInList(draft.Id);
            }
            finally
            {
                _suppressListEvents = false;
            }
            ShowEditorForm(addMode: false);
            RefreshApiKeyState();
            ClearEditorDirty();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke(config.ActiveProfileId == draft.Id
                ? $"服务「{draft.Name}」已保存并作为默认文字服务生效。"
                : $"服务「{draft.Name}」已保存。用「设为文字默认」启用它。",
                StatusTone.Success);
            return true;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"保存服务失败：{exception.Message}", StatusTone.Error);
            return false;
        }
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        ReloadEditorFromSaved();
        ShowOverview();
        StatusChanged?.Invoke("已放弃修改。", StatusTone.Info);
    }

    /// <summary>
    /// Best-effort credential rollback after a failed profile write: restore
    /// the previous key, or delete the one just written if there was none.
    /// </summary>
    private static void TryRestoreCredential(string credentialTarget, string? previousKey, bool hadPreviousKey)
    {
        try
        {
            if (hadPreviousKey && !string.IsNullOrEmpty(previousKey))
            {
                CredentialStore.SaveApiKey(previousKey, credentialTarget);
            }
            else
            {
                CredentialStore.DeleteApiKey(credentialTarget);
            }
        }
        catch (Exception)
        {
            // The vault is failing; nothing further can be done here. The
            // save error the user sees already reports the write failure.
        }
    }

    internal ProviderProfile BuildProfileFromForm(string name)
    {
        var baseUrl = BaseUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("API Base URL 不能为空。");
        }
        var isLocal = ProviderSettings.IsLocalBaseUrl(baseUrl);
        var textModel = TextModelCombo.Text.Trim();
        var visionModel = UseTextModelForVisionCheckBox.IsChecked == true
            ? textModel
            : VisionModelCombo.Text.Trim();
        if (!baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !isLocal)
        {
            throw new InvalidOperationException("API Base URL 必须使用 HTTPS；仅本机或局域网服务允许 HTTP。");
        }
        return new ProviderProfile
        {
            Name = name,
            ProviderType = Helpers.SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible),
            ApiBaseUrl = baseUrl,
            TextEndpoint = string.IsNullOrWhiteSpace(TextEndpointTextBox.Text)
                ? "/chat/completions" : TextEndpointTextBox.Text.Trim(),
            VisionEndpoint = string.IsNullOrWhiteSpace(VisionEndpointTextBox.Text)
                ? "/chat/completions" : VisionEndpointTextBox.Text.Trim(),
            TextModel = textModel,
            VisionModel = visionModel,
            ExtraHeaders = new Dictionary<string, string>(
                ParseExtraHeaders(ExtraHeadersTextBox.Text),
                StringComparer.OrdinalIgnoreCase),
            AnthropicVersion = string.IsNullOrWhiteSpace(AnthropicVersionTextBox.Text)
                ? "2023-06-01" : AnthropicVersionTextBox.Text.Trim(),
            SupportsText = !string.IsNullOrWhiteSpace(textModel),
            SupportsVision = !string.IsNullOrWhiteSpace(visionModel),
            AllowInsecureTls = AllowInsecureTlsCheckBox.IsChecked == true,
            IsLocal = isLocal,
        };
    }
}
