using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.CompilerServices;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.Sections;

/// <summary>One row in the service list, shaped for display.</summary>
internal sealed record ProfilesRow(
    string Id,
    string Name,
    string Summary,
    string StateText,
    Brush StateBrush,
    Brush StateTextBrush,
    System.Windows.Visibility IsDefaultBadge,
    System.Windows.Visibility SetDefaultVisibility,
    IReadOnlyList<string>? CapabilityBadges = null,
    string? LatencyText = null,
    System.Windows.Visibility LatencyVisibility = System.Windows.Visibility.Collapsed);

internal sealed record FreeRouteRow(
    FreeEngineProvider Provider,
    string Name,
    string Description,
    IReadOnlyList<string> Tags,
    string LatencyText,
    Brush StateBrush,
    Brush LatencyBrush,
    System.Windows.Visibility ActiveBadgeVisibility,
    System.Windows.Visibility SelectVisibility,
    bool IsActive,
    string ProbeButtonName,
    string SelectButtonName);

internal sealed record DiscoveredSourceRow(
    string Id,
    string DisplayName,
    string BaseUrl,
    ProviderType ProviderType,
    string TextEndpoint,
    bool IsLocalRuntime,
    bool HasEnvKey,
    string? EnvVarName,
    string KindBadge,
    string DetailText,
    string DefaultModel,
    IReadOnlyList<string> DiscoveredModels,
    string AddButtonName);

/// <summary>
/// Service settings switch between the saved-engine list and one editor.
/// Connection and model fields stay editable; optional network details are collapsed.
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
    private bool _editorDirty;
    private string _editorBaseline = string.Empty;
    private string? _editingProfileId;
    private ModelPreference _currentPreference = ModelPreference.Balanced;
    private IReadOnlyList<ModelDescriptor>? _cachedCatalogDescriptors;

    // C21 证据溯源：目录是何时、从哪个主机采集的。空值表示没有目录事实，
    // 此时证据 tooltip 只声明等级，绝不编造来源或时间。
    private string? _catalogSourceHost;
    private DateTime? _catalogFetchedAtUtc;

    /// <summary>Session-scoped connection-test outcomes by profile id ("ok"/"auth"/…).</summary>
    private readonly Dictionary<string, string> _testOutcomes = new();
    private readonly Dictionary<string, long> _testLatencies = new(StringComparer.Ordinal);
    private readonly Dictionary<FreeEngineProvider, FreeTranslateService.FreeEngineHealth> _freeRouteProbes = new();
    private bool _freeProbeInProgress;
    private bool _discoveryInProgress;
    private bool _loadingQuickProxy;
    private string? _pendingTestOutcome;
    private DateTime? _pendingTestedAtUtc;
    private string? _pendingTestFingerprint;
    private long? _pendingTestElapsedMs;
    private long _editorRevision;
    private bool _testInProgress;
    private bool _saveInProgress;
    private CancellationTokenSource? _catalogOperation;
    internal Func<ProviderSettings, string, Task<TranslationResponse>> ConnectionTestExecutor { get; set; } =
        (draft, key) => CoreBridge.TestConnectionDraftAsync(draft, key);
    internal Func<ProviderSettings, string, CancellationToken, Task<ModelCatalogResult>> ModelCatalogExecutor { get; set; } =
        (draft, key, ct) => ModelCatalogService.FetchAsync(draft, key, ct);
    internal Func<CancellationToken, Task<LocalAndFreeDiscoveryResult>> DiscoverFreeSourcesExecutor { get; set; } =
        ct => ModelCatalogService.DiscoverLocalAndFreeSourcesAsync(ct);
    internal Func<FreeEngineProvider, bool, FreeEngineAuthorization?, Task<FreeTranslateService.FreeEngineHealth>> FreeRouteProbeExecutor { get; set; } =
        (provider, force, auth) => FreeTranslateService.GetHealthAsync(provider, force, auth);
    internal Func<string, string?> EnvKeyReader { get; set; } =
        sourceId => ModelCatalogService.TryReadEnvApiKeyForImport(sourceId);

    internal Func<ProviderProfile, CancellationToken, Task<TranslationResponse>> SavedConnectionTestExecutor { get; set; } =
        (profile, ct) => CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, ct).Completion;
    private readonly Dictionary<string, CancellationTokenSource> _listTests = new(StringComparer.Ordinal);
    private readonly ComboBox[] _editorCombos;
    /// <summary>
    /// Tracks vision model state across toggles of the shared-model checkbox.
    /// </summary>
    private readonly SharedVisionModelTracker _visionTracker = new();

    /// <summary>Action queued behind an unresolved editor draft.</summary>
    private Action? _pendingAfterDraft;
    private ConfirmButton? _deleteConfirm;
    private readonly ConditionalWeakTable<Button, ConfirmButton> _rowDeleteConfirms = new();
    private ConfirmButton? _forgetAccountConfirm;
    private ConfirmButton? _clearKeyConfirm;

    private void DisarmAllConfirms()
    {
        _deleteConfirm?.Disarm();
        _clearKeyConfirm?.Disarm();
        _forgetAccountConfirm?.Disarm();
        ConfirmButton.DisarmCurrent();
    }
    private bool? _compactEditor;
    private bool _detailWidthReported;
    private readonly System.Windows.Threading.DispatcherTimer _recommendationDebounce;

    /// <summary>Raised when the section needs to show a status message.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised after a profile is saved, activated, or deleted.</summary>
    internal event Action? ProfileChanged;

    /// <summary>Raised when the editor draft becomes dirty or clean.</summary>
    internal event Action? EditorDirtyChanged;

    /// <summary>Raised when the editor opens or closes.</summary>
    internal event Action? EditorOpenStateChanged;

    /// <summary>True while the editor holds unsaved changes.</summary>
    internal bool IsEditorDirty => _editorDirty;

    /// <summary>True while the service editor form is visible.</summary>
    internal bool IsEditorOpen => EditorForm?.Visibility == Visibility.Visible;

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
        _forgetAccountConfirm = ConfirmButton.Attach(ChatGptForgetAccountButton, "确认删除？",
            () => ChatGptForgetAccount_Click(ChatGptForgetAccountButton, new RoutedEventArgs()));
        _clearKeyConfirm = ConfirmButton.Attach(ClearKeyButton, "确认清除？", ClearKeyForCurrentProfile);
        EditorScroll.ScrollChanged += EditorScroll_ScrollChanged;
        _editorCombos = [ProviderTypeComboBox, TextModelCombo, VisionModelCombo, ReasoningLevelCombo, CliModelCombo,
            OmpLoginProviderCombo, ChatGptAccountCombo, ChatGptModelCombo];
        foreach (var combo in _editorCombos)
        {
            combo.DropDownOpened += EditorCombo_DropDownOpened;
            combo.DropDownClosed += EditorCombo_DropDownClosed;
            HookComboWheel(combo);
        }
        Unloaded += (_, _) => CancelListTests();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CancelListTests();
        };
        TextModelCombo.SelectionChanged += (_, _) =>
        {
            if (UseTextModelForVisionCheckBox.IsChecked == true)
            {
                VisionModelCombo.Text = TextModelCombo.Text;
            }
            MarkEditorDirty();
            if (!_loading)
            {
                _recommendationDebounce.Stop();
                _recommendationDebounce.Start();
            }
        };
        VisionModelPanel.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (UseTextModelForVisionCheckBox.IsChecked == true)
            {
                UseTextModelForVisionCheckBox.IsChecked = false;
                VisionModelCombo.IsEnabled = true;
                VisionModelCombo.Focus();
            }
        };
        RefreshFreeRoutesPanel();
        RefreshProxyStatusPill();
    }

    private void CancelListTests()
    {
        foreach (var operation in _listTests.Values.ToArray()) operation.Cancel();
    }

    internal bool IsLoading { get => _loading; set => _loading = value; }

    // ================= Adaptive layout =================

    /// <summary>
    /// Field pairs use the same 14 DIP gutter on wide layouts and stack at the
    /// settings window's narrowest supported width. No field is ever restored
    /// into the gutter column.
    /// </summary>
    internal void SetCompact(bool compact)
    {
        if (_compactEditor.HasValue && compact == _compactEditor.Value)
        {
            return;
        }
        _compactEditor = compact;
        ApplyEditorLayout();
    }

    /// <summary>
    /// DetailGrid 的内容宽度是 Provider 编辑器紧凑布局的唯一判据。窗口层只在
    /// DetailGrid 尚未报告过任何真实尺寸（窗口从未显示、无头环境）时收到一次
    /// 回退提示；一旦 DetailGrid.SizeChanged 触发，本提示永久失效，窗口宽度
    /// 不再参与判定，两套判据互相覆盖的问题就此消除。
    /// </summary>
    internal void SetWindowWidthHint(double windowWidth)
    {
        if (_detailWidthReported)
        {
            return;
        }
        SetCompact(windowWidth < 700);
    }

    private void DetailGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
        {
            return;
        }
        _detailWidthReported = true;
        SetCompact(e.NewSize.Width < 680);
        PlacePresetColumns(e.NewSize.Width < 440);
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

        EditorHeaderGrid.Margin = new Thickness(2, 0, 2, 8);
        ConnectionCard.Margin = new Thickness(0, 0, 0, 10);
        ModelCard.Margin = new Thickness(0, 0, 0, 10);
        PreferenceRowHost.Margin = new Thickness(0, compact ? 8 : 12, 0, 0);
        IdentityFieldsGrid.Margin = new Thickness(0, 0, 0, 10);
        BaseUrlPanel.Margin = new Thickness(0, 0, 0, compact ? 10 : 14);
        ApiKeyStateText.Margin = new Thickness(2, compact ? 4 : 7, 0, 0);

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

    /// <summary>
    /// The catalogue stays two-column while each source has room; below
    /// 440 DIP the second column follows the first in a separate row.
    /// </summary>
    private void PlacePresetColumns(bool compact)
    {
        if (compact)
        {
            PresetLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            PresetGutter.Width = new GridLength(0);
            PresetRightColumn.Width = new GridLength(0);
            Grid.SetColumn(PresetColumnRight, 0);
            Grid.SetRow(PresetColumnRight, 1);
            PresetColumnRight.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            PresetLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            PresetGutter.Width = new GridLength(12);
            PresetRightColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(PresetColumnRight, 2);
            Grid.SetRow(PresetColumnRight, 0);
            PresetColumnRight.Margin = new Thickness(0);
        }
    }

    private void EditorScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged is routed. A model popup owns its own ScrollViewer;
        // its event can bubble through the ComboBox's logical placement
        // target. Closing on that event made the fetched model list disappear
        // on the first wheel tick. Only movement of the editor page itself
        // should dismiss transient dropdowns.
        if (!ReferenceEquals(e.OriginalSource, EditorScroll) ||
            Math.Abs(e.VerticalChange) < 0.5)
        {
            return;
        }

        foreach (var combo in _editorCombos)
        {
            if (combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = false;
            }
        }
    }

    private void HookComboWheel(ComboBox combo)
    {
        combo.PreviewMouseWheel += (s, e) =>
        {
            if (combo.IsDropDownOpen)
            {
                if (combo.Template?.FindName("DropDownScrollViewer", combo) is ScrollViewer sv)
                {
                    int lines = Math.Max(1, Math.Abs(e.Delta) / 40);
                    for (int i = 0; i < lines; i++)
                    {
                        if (e.Delta < 0)
                        {
                            sv.LineDown();
                        }
                        else
                        {
                            sv.LineUp();
                        }
                    }
                    e.Handled = true;
                }
            }
            else
            {
                ForwardEditorWheel(s, e);
            }
        };
    }

    private void ForwardEditorWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        EditorScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = EditorScroll,
        });
    }

    private void EditorCombo_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }
        if (combo.IsEditable && combo.Items.Count == 0)
        {
            combo.IsDropDownOpen = false;
            if (combo == TextModelCombo || combo == VisionModelCombo)
            {
                ModelCatalogStatusText.Text = "可直接输入模型 ID，或刷新列表。";
                ModelCatalogStatusText.Visibility = Visibility.Visible;
            }
            return;
        }
        if (combo.Template?.FindName("PART_Popup", combo) is not Popup popup)
        {
            return;
        }

        popup.PreviewMouseWheel -= EditorPopup_PreviewMouseWheel;
        popup.PreviewMouseWheel += EditorPopup_PreviewMouseWheel;

        if (EditorScroll.ViewportHeight <= 1)
        {
            popup.Placement = PlacementMode.Bottom;
            return;
        }

        Point below;
        Point above;
        try
        {
            below = combo.TranslatePoint(new Point(0, combo.ActualHeight), EditorScroll);
            above = combo.TranslatePoint(new Point(0, 0), EditorScroll);
        }
        catch (InvalidOperationException)
        {
            popup.Placement = PlacementMode.Bottom;
            return;
        }

        var spaceBelow = EditorScroll.ViewportHeight - below.Y;
        var spaceAbove = above.Y;
        var itemCount = combo.Items.Count;
        var estimateNeeded = itemCount > 0 ? (itemCount * 36.0 + 12.0) : 160.0;
        var want = Math.Min(320.0, Math.Max(120.0, estimateNeeded));
        var placement = ResolveEditorPopupPlacement(spaceBelow, spaceAbove, want);
        var openAbove = placement == PlacementMode.Top;
        popup.Placement = placement;
        popup.VerticalOffset = openAbove ? -4 : 4;

        var available = openAbove ? (spaceAbove - 8) : (spaceBelow - 8);
        var maxAvailable = Math.Max(60.0, available);
        combo.MaxDropDownHeight = Math.Min(want, maxAvailable);
    }

    private void EditorCombo_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo)
        {
            combo.MaxDropDownHeight = 320.0;
            if (combo.Template?.FindName("PART_Popup", combo) is Popup popup)
            {
                popup.PreviewMouseWheel -= EditorPopup_PreviewMouseWheel;
            }
        }
    }

    private void EditorPopup_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is Popup popup &&
            popup.TemplatedParent is ComboBox combo &&
            combo.Template?.FindName("DropDownScrollViewer", combo) is ScrollViewer sv)
        {
            int lines = Math.Max(1, Math.Abs(e.Delta) / 40);
            for (int i = 0; i < lines; i++)
            {
                if (e.Delta < 0)
                {
                    sv.LineDown();
                }
                else
                {
                    sv.LineUp();
                }
            }
            e.Handled = true;
        }
    }

    internal static PlacementMode ResolveEditorPopupPlacement(
        double spaceBelow, double spaceAbove, double desiredHeight) =>
        spaceBelow < desiredHeight && spaceAbove > spaceBelow
            ? PlacementMode.Top
            : PlacementMode.Bottom;

    private void AdvancedExpander_Expanded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TextEndpointPanel.BringIntoView();
        }, DispatcherPriority.Loaded);
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
            second.Margin = new Thickness(0, 16, 0, 0);
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
        void WatchText(TextBox box) => box.TextChanged += (_, _) =>
        {
            MarkEditorDirty();
            if (!_loading && box != ServiceNameTextBox) InvalidateModelCatalog();
            box.ToolTip = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text;
        };
        // Editable ComboBoxes surface their inner TextBox via this routed event.
        void WatchCombo(ComboBox combo) => combo.AddHandler(
            System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) =>
            {
                MarkEditorDirty();
                var text = combo.Text;
                combo.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
                if (combo == TextModelCombo && UseTextModelForVisionCheckBox.IsChecked == true)
                {
                    VisionModelCombo.Text = text;
                    VisionModelCombo.ToolTip = "已开启「图文共用此模型」，图片输入将使用与文字相同的模型。如需独立指定，请取消勾选「图文共用此模型」开关。";
                }
                if (!_loading)
                {
                    // 按键级重建推荐 chips 的开销不小，走防抖合并。
                    _recommendationDebounce.Stop();
                    _recommendationDebounce.Start();
                }
            }));
        // C12: Use DependencyPropertyDescriptor to capture programmatic .Text assignment
        // and interactive editing, with symmetric RemoveValueChanged on Unloaded to prevent leaks.
        var textDpd = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.TextProperty, typeof(ComboBox));
        void OnTextModelChanged(object? sender, EventArgs e)
        {
            if (UseTextModelForVisionCheckBox.IsChecked == true)
            {
                VisionModelCombo.Text = TextModelCombo.Text;
                VisionModelCombo.ToolTip = "已开启「图文共用此模型」，图片输入将使用与文字相同的模型。如需独立指定，请取消勾选「图文共用此模型」开关。";
            }
        }
        textDpd.AddValueChanged(TextModelCombo, OnTextModelChanged);
        Loaded += (_, _) => ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            _recommendationDebounce.Stop();
            textDpd.RemoveValueChanged(TextModelCombo, OnTextModelChanged);
            CancelCliOperation();
            _catalogOperation?.Cancel();
        };

        void WatchToggle(System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            toggle.Checked += (_, _) => MarkEditorDirty();
            toggle.Unchecked += (_, _) => MarkEditorDirty();
        }

        WatchText(ServiceNameTextBox);
        WatchText(BaseUrlTextBox);
        BaseUrlTextBox.TextChanged += (_, _) => UpdateCredentialGating();
        WatchText(TextEndpointTextBox);
        WatchText(VisionEndpointTextBox);
        WatchText(ExtraHeadersTextBox);
        WatchText(AnthropicVersionTextBox);
        ContextTokensTextBox.TextChanged += (_, _) => MarkEditorDirty();
        OutputTokensTextBox.TextChanged += (_, _) => MarkEditorDirty();
        ReasoningLevelCombo.SelectionChanged += (_, _) => MarkEditorDirty();

        WatchCombo(TextModelCombo);
        WatchCombo(VisionModelCombo);
        WatchToggle(SupportsTextCheckBox);
        WatchToggle(SupportsVisionCheckBox);
        WatchToggle(UseTextModelForVisionCheckBox);
        WatchToggle(AllowInsecureTlsCheckBox);
        ApiKeyPasswordBox.PasswordChanged += (_, _) =>
        {
            MarkEditorDirty();
            if (!_loading) InvalidateModelCatalog();
            UpdateCredentialGating();
        };
        ProviderTypeComboBox.SelectionChanged += (_, _) => MarkEditorDirty();
    }

    private void MarkEditorDirty()
    {
        if (_loading || EditorForm.Visibility != Visibility.Visible)
        {
            return;
        }
        _editorRevision++;
        var dirty = HasEditorChanges(CaptureEditorState(), _editorBaseline);
        var dirtyChanged = _editorDirty != dirty;
        _editorDirty = dirty;
        if (_pendingTestFingerprint is not null)
        {
            var currentFingerprint = TryCurrentDraftFingerprint();
            if (!string.Equals(currentFingerprint, _pendingTestFingerprint, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(ApiKeyPasswordBox.Password))
            {
                _pendingTestOutcome = null;
                _pendingTestedAtUtc = null;
                _pendingTestFingerprint = null;
                _pendingTestElapsedMs = null;
                SetTestResult(StatusTone.Warning, "配置已变化，请重新测试", null);
            }
        }
        if (dirtyChanged) EditorDirtyChanged?.Invoke();
    }

    internal void ClearEditorDirty()
    {
        _editorBaseline = CaptureEditorState();
        _editorDirty = false;
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
        if (CliConnectionsPanel?.Visibility == Visibility.Visible)
            return ChatGptConnectionPanel.Visibility == Visibility.Visible
                ? $"direct|{DirectIdentity()}"
                : $"cli|{(CliToolsListBox.SelectedItem as CliToolRow)?.ToolId}|{(CliToolsListBox.SelectedItem as CliToolRow)?.EntryPath}|{(CliModelCombo.SelectedItem as CliModel)?.Provider}|{(CliModelCombo.SelectedItem as CliModel)?.Id}";
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
            ApiKeyPasswordBox.Password).Serialize()
            + "|generation|" + ServiceDraftCoordinator.NormalizeReasoningLevel(
                (ReasoningLevelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString())
            + "|" + ContextTokensTextBox.Text.Trim()
            + "|" + OutputTokensTextBox.Text.Trim();
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
        VisionModelCombo.ToolTip = shared
            ? "已开启「图文共用此模型」，图片输入将使用与文字相同的模型。如需独立指定，请取消勾选「图文共用此模型」开关。"
            : (string.IsNullOrWhiteSpace(VisionModelCombo.Text) ? "选择或输入图片模型标识" : VisionModelCombo.Text);
        if (SharedModelHintText != null)
        {
            SharedModelHintText.Visibility = shared ? Visibility.Visible : Visibility.Collapsed;
        }
        MarkEditorDirty();
        RefreshRecommendations();
    }

    /// <summary>
    /// Resolves an unsaved draft beside the top return navigation.
    /// The pending action runs only after an explicit save or discard.
    /// </summary>
    internal void BeginDraftGuard(string message, Action? proceed = null)
    {
        if (_cliCommitInProgress || _saveInProgress) return;
        if (!_editorDirty)
        {
            proceed?.Invoke();
            return;
        }
        _pendingAfterDraft = proceed;
        DraftGuardText.Text = message;
        DraftGuardBar.Visibility = Visibility.Visible;
        EditorActionBar.Visibility = Visibility.Collapsed;
    }

    private void HideDraftGuard()
    {
        DraftGuardBar.Visibility = Visibility.Collapsed;
        if (EditorForm.Visibility == Visibility.Visible &&
            (ConfigFormPanel.Visibility == Visibility.Visible || CliConnectionsPanel.Visibility == Visibility.Visible))
        {
            EditorActionBar.Visibility = Visibility.Visible;
        }
        _pendingAfterDraft = null;
    }

    private async void DraftSave_Click(object sender, RoutedEventArgs e)
    {
        if (await TrySaveServiceAsync())
        {
            var proceed = _pendingAfterDraft;
            HideDraftGuard();
            proceed?.Invoke();
        }
        // Save failed: keep the bar so the user can retry or discard.
    }

    private void DraftDiscard_Click(object sender, RoutedEventArgs e)
    {
        // Capture first. Reloading can close the editor (no saved profile
        // to return to) and that hides the guard, which would drop the
        // pending page change.
        var proceed = _pendingAfterDraft;
        ReloadEditorFromSaved();
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
        // A discarded ADD-MODE connection draft (CLI or ChatGPT direct) has
        // no saved profile to reload: falling back to a list/active profile
        // would flash an unrelated engine's form and leave the editor dirty
        // against that baseline. Return to the unified source picker.
        if (CliConnectionsPanel.Visibility == Visibility.Visible && _editingProfileId is null)
        {
            ShowSourcePicker();
            return;
        }
        var config = ProfileManager.Load();
        var profile = config.Profiles.FirstOrDefault(p => p.Id == _editingProfileId)
            ?? SelectedProfile() ?? config.TryGetActiveProfile();
        if (profile is null)
        {
            ShowOverview();
            return;
        }
        if (profile.ResolvedExecutionKind == EngineExecutionKind.ChatGptDirect) { OpenDirectProfile(profile); return; }
        if (profile.ExecutionFacts.IsCliExecution) { OpenCliProfile(profile); return; }
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
        _editorRevision++;
        _catalogOperation?.Cancel();
        TextModelCombo.ItemsSource = null;
        VisionModelCombo.ItemsSource = null;
        var wasLoading = _loading;
        _loading = true;
        _visionTracker.Reset();
        _cachedCatalogDescriptors = null;
        _catalogSourceHost = null;
        _catalogFetchedAtUtc = null;
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
            if (SharedModelHintText != null)
            {
                SharedModelHintText.Visibility = sharedModel ? Visibility.Visible : Visibility.Collapsed;
            }
            _visionTracker.OnLoaded(profile.TextModel, profile.VisionModel);
            UpdateModelSuggestions(profile.ProviderType);
            ExtraHeadersTextBox.Text = string.Join(
                Environment.NewLine,
                profile.ExtraHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
            AnthropicVersionTextBox.Text = profile.AnthropicVersion;
            Helpers.SelectComboByTag(
                ReasoningLevelCombo, ServiceDraftCoordinator.NormalizeReasoningLevel(profile.ReasoningLevel));
            ContextTokensTextBox.Text = FormatTokenSetting(profile.ContextTokens);
            OutputTokensTextBox.Text = FormatTokenSetting(profile.OutputTokens);
            SupportsTextCheckBox.IsChecked = !string.IsNullOrWhiteSpace(profile.TextModel);
            SupportsVisionCheckBox.IsChecked = !string.IsNullOrWhiteSpace(profile.VisionModel);
            AllowInsecureTlsCheckBox.IsChecked = profile.AllowInsecureTls;
            UpdateEditorIdentity(profile.Name);
            CustomProtocolGroup.Visibility = Visibility.Visible;
            BaseUrlPanel.Visibility = Visibility.Visible;
            AdvancedGroup.Visibility = Visibility.Visible;
            ApplyEditorLayout();
            UpdatePresetTierHint(null);
            ApiKeyPasswordBox.Clear();
            LoadStoredTestEvidence(profile);
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
            _ = CredentialStore.HasApiKey(CurrentCredentialTarget());
        }
        catch (Exception)
        {
            ApiKeyStateText.Text = string.Empty;
            ApiKeyStateText.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateCredentialGating();
    }

    private void UpdateCredentialGating()
    {
        if (FetchModelsButton is null || TestConnectionButton is null ||
            ApiKeyPasswordBox is null || ApiKeyStateText is null)
        {
            return;
        }

        var isLocal = ProviderSettings.IsLocalBaseUrl(BaseUrlTextBox?.Text);
        var hasStoredKey = false;
        try
        {
            if (!_isAdding && _editingProfileId is not null)
            {
                var profile = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
                if (profile is not null)
                {
                    hasStoredKey = HasStoredKey(profile);
                }
            }
        }
        catch
        {
        }

        var hasTypedKey = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password);
        var hasKey = hasStoredKey || hasTypedKey;
        var allowed = isLocal || hasKey;

        // The field already communicates all four states. A second line such
        // as "saved key" only adds weight and can be mistaken for help text.
        ApiKeyStateText.Text = string.Empty;
        ApiKeyStateText.Visibility = Visibility.Collapsed;

        Ui.SetIsCredentialMask(ApiKeyPasswordBox, hasStoredKey && !hasTypedKey);
        Ui.SetPlaceholder(ApiKeyPasswordBox,
            hasStoredKey ? "••••••••••••  已保存" :
            isLocal ? "本地引擎可留空" : "请输入 API Key");

        if (hasStoredKey && !hasTypedKey)
        {
            AutomationProperties.SetName(ApiKeyPasswordBox, "API Key (已保存)");
        }
        else if (hasStoredKey && hasTypedKey)
        {
            AutomationProperties.SetName(ApiKeyPasswordBox, "API Key (正在替换已保存密钥)");
        }
        else if (!hasStoredKey && hasTypedKey)
        {
            AutomationProperties.SetName(ApiKeyPasswordBox, "API Key (未保存)");
        }
        else
        {
            AutomationProperties.SetName(ApiKeyPasswordBox, isLocal ? "API Key (本地引擎可留空)" : "API Key (未填写)");
        }

        FetchModelsButton.IsEnabled = allowed && _catalogOperation is null;
        FetchModelsButton.ToolTip = allowed
            ? "刷新模型列表"
            : "请先填写 API Key（本地服务除外）";

        // Keep validation actionable. A disabled button looks broken and
        // cannot explain what is missing; the click handler focuses the exact
        // field and renders an inline reason without sending anything.
        TestConnectionButton.IsEnabled = !_testInProgress;
        TestConnectionButton.ToolTip = allowed
            ? "测试连接"
            : "点击检查配置；尚缺 API Key（本地服务除外）";

        if (ClearKeyButton is not null)
        {
            var canClear = hasStoredKey || hasTypedKey;
            ClearKeyButton.IsEnabled = canClear;
            ClearKeyButton.ToolTip = (hasStoredKey && hasTypedKey)
                ? "清除正在输入的密钥（恢复已保存密钥）"
                : hasStoredKey
                    ? "清除已保存的密钥"
                    : hasTypedKey
                        ? "清除已输入的密钥"
                        : "没有可清除的密钥";
        }
    }

    // ================= List & default routes =================

    private void OnThemeChanged(object? sender, EventArgs _)
    {
        RefreshProfilesList();
        RefreshFreeRoutesPanel();
        RefreshProxyStatusPill();
    }

    internal void RefreshProfilesList()
    {
        RefreshFreeRoutesPanel();
        RefreshProxyStatusPill();
        // One config read for the whole refresh: rows, brushes and the route
        // pickers all draw from this instance.
        var config = ProfileManager.Load();
        ProfilesListBox.ItemsSource = config.Profiles.Select(profile =>
        {
            var hasKey = HasStoredKey(profile, config.ActiveProfileId);
            var (stateText, stateTone) = DescribeProfileState(profile, hasKey,
                _testOutcomes.TryGetValue(profile.Id, out var outcome) ? outcome : null);
            var hasLatency = _testLatencies.TryGetValue(profile.Id, out var elapsedMs);
            var latencyText = hasLatency ? TranslationElapsedText.ForMilliseconds(elapsedMs) : null;
            return new ProfilesRow(
                profile.Id,
                profile.Name,
                ProfileRowSummary(profile),
                stateText,
                ToneBrush(stateTone),
                ToneTextBrush(stateTone),
                !config.PreferFreeEngine && profile.Id == config.ActiveProfileId
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                !config.PreferFreeEngine && profile.Id == config.ActiveProfileId
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                BuildCapabilityBadges(profile),
                latencyText,
                hasLatency ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
        var hasProfiles = config.Profiles.Count > 0;
        ProfilesEmptyText.Visibility = hasProfiles
            ? Visibility.Collapsed
            : Visibility.Visible;
        // The list occupies the same grid cell as the empty-state CTA. An
        // empty but visible ListBox still wins WPF hit testing, making the
        // button look enabled while swallowing every click. Collapse the list
        // whenever the empty state is active; the Z-order in XAML is only a
        // defensive fallback during a layout refresh.
        ProfilesListBox.Visibility = hasProfiles
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!hasProfiles)
        {
            _editingProfileId = null;
            _isAdding = false;
            var wasOpen = EditorForm.Visibility == Visibility.Visible;
            EditorForm.Visibility = Visibility.Collapsed;
            EditorEmpty.Visibility = Visibility.Visible;
            if (wasOpen)
            {
                EditorOpenStateChanged?.Invoke();
            }
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

    /// <summary>托管引擎在列表行的摘要带上账号/提供方归属，与路由菜单口径一致。</summary>
    private static string ProfileRowSummary(ProviderProfile profile)
    {
        var model = string.IsNullOrWhiteSpace(profile.TextModel) ? "未填模型" : profile.TextModel;
        string? owner = profile.ResolvedExecutionKind switch
        {
            EngineExecutionKind.ChatGptDirect when profile.DirectConnection is { AccountId: { Length: > 0 } accountId }
                => TryAccountLabel(accountId),
            EngineExecutionKind.OmpRpc when !string.IsNullOrWhiteSpace(profile.CliConnection?.UpstreamProviderId)
                => profile.CliConnection?.UpstreamProviderId,
            _ => null,
        };
        return owner is null ? model : $"{model} · {owner}";
    }

    private static string? TryAccountLabel(string accountId)
    {
        try { return ChatGptAccountStore.Get(accountId).Label; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? LoadStoredKey(ProviderProfile profile, string? activeProfileId = null)
    {
        if (profile.ExecutionFacts.IsManagedExecution) return null;
        try
        {
            var profileKey = CredentialStore.LoadApiKey(profile.CredentialTarget);
            if (!string.IsNullOrWhiteSpace(profileKey))
            {
                return profileKey;
            }
            var activeId = activeProfileId ?? ProfileManager.Load().ActiveProfileId;
            // A key saved before profiles existed stays at the legacy target
            // until edited; it still counts for the active profile.
            return profile.Id == activeId
                ? CredentialStore.LoadApiKey(CredentialStore.DefaultTargetName)
                : null;
        }
        catch (Exception)
        {
            // The credential vault may be unavailable; report no key honestly.
            return null;
        }
    }

    private static bool HasStoredKey(ProviderProfile profile, string? activeProfileId = null) =>
        !string.IsNullOrWhiteSpace(LoadStoredKey(profile, activeProfileId));

    /// <summary>
    /// Health state for one service. Brand colour never appears here — states
    /// are success / warning / danger / neutral only. Session test outcomes
    /// are labelled as such and never presented as permanent health.
    /// outcome: null = not tested this session; else a ClassifyTestFailure code.
    /// </summary>
    internal static (string Text, StatusTone Tone) DescribeProfileState(
        bool isLocal, bool hasKey, string? outcome)
    {
        if (isLocal && outcome == "ok")
        {
            return ("本地服务 · 可用", StatusTone.Success);
        }
        if (isLocal && outcome is null)
        {
            return ("本地服务", StatusTone.Info);
        }
        if (!isLocal && !hasKey)
        {
            return ("缺少 Key", StatusTone.Warning);
        }
        return outcome switch
        {
            "ok" => ("上次成功", StatusTone.Success),
            "auth" => ("密钥无效", StatusTone.Error),
            "model" => ("模型不可用", StatusTone.Error),
            "endpoint" => ("地址错误", StatusTone.Error),
            "rate" => ("无额度/限流", StatusTone.Warning),
            "timeout" => ("超时", StatusTone.Error),
            "protocol" => ("协议响应异常", StatusTone.Error),
            "unreachable" => isLocal ? ("本地不可达", StatusTone.Error) : ("地址错误", StatusTone.Error),
            null => ("已配置 · 尚未验证", StatusTone.Info),
            _ => ("测试失败", StatusTone.Error),
        };
    }

    private static (string Text, StatusTone Tone) DescribeProfileState(
        ProviderProfile profile, bool hasKey, string? sessionOutcome)
    {
        if (profile.ExecutionFacts.IsManagedExecution)
        {
            if (!ProfileManager.IsTextEngineExecutable(profile)) return ("已断开 · 需重新连接", StatusTone.Warning);
            var valid = ProfileVerification.EvidenceFingerprint(profile, false) == profile.LastTestFingerprint;
            if (valid && profile.LastTestOutcome == "ok")
            {
                var when = profile.LastTestedAtUtc is { } testedAt ? $" · {testedAt.ToLocalTime():MM-dd HH:mm}" : "";
                return ($"上次测试成功{when}", StatusTone.Success);
            }
            return ("已连接 · 请求待验证", StatusTone.Info);
        }
        var currentFingerprint = CreateProfileFingerprint(profile, hasKey);
        var persistedMatches = !string.IsNullOrWhiteSpace(profile.LastTestFingerprint) &&
            string.Equals(profile.LastTestFingerprint, currentFingerprint, StringComparison.Ordinal);
        if (!persistedMatches && profile.LastTestedAtUtc is not null)
        {
            return ("配置已变化 · 待验证", StatusTone.Warning);
        }

        var outcome = sessionOutcome ?? (persistedMatches ? profile.LastTestOutcome : null);
        var (text, tone) = DescribeProfileState(
            ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl), hasKey, outcome);
        if (profile.LastTestedAtUtc is not null && persistedMatches && outcome is not null)
        {
            var when = profile.LastTestedAtUtc.Value.ToLocalTime().ToString("MM-dd HH:mm");
            text = outcome == "ok" ? $"上次成功 · {when}" : $"{text} · {when}";
        }
        return (text, tone);
    }

    internal static string CreateProfileFingerprint(ProviderProfile profile, bool hasKey) =>
        ProfileVerification.EvidenceFingerprint(
            profile,
            hasKey || ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl));

    private string CurrentDraftFingerprint()
    {
        var draft = BuildDraftSettings();
        var typedKey = !string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password);
        ProviderProfile? saved = null;
        if (!string.IsNullOrWhiteSpace(_editingProfileId))
        {
            saved = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _editingProfileId);
        }

        var hasCredential = draft.TargetsLocalRuntime || typedKey || (saved is not null && HasStoredKey(saved));
        var revision = saved?.CredentialRevision ?? 0;
        if (typedKey)
        {
            revision++;
        }

        var probe = new ProviderProfile
        {
            ProviderType = draft.ProviderType,
            ApiBaseUrl = draft.ApiBaseUrl,
            TextEndpoint = draft.TextEndpoint,
            TextModel = draft.TextModel,
            ExtraHeaders = new Dictionary<string, string>(draft.ExtraHeaders, StringComparer.OrdinalIgnoreCase),
            AnthropicVersion = draft.AnthropicVersion,
            AllowInsecureTls = draft.AllowInsecureTls,
            CredentialRevision = revision,
            ReasoningLevel = draft.ReasoningLevel,
            ContextTokens = draft.ContextTokens,
            OutputTokens = draft.OutputTokens,
        };
        return ProfileVerification.EvidenceFingerprint(probe, hasCredential);
    }

    private string? TryCurrentDraftFingerprint()
    {
        try
        {
            return CurrentDraftFingerprint();
        }
        catch (InvalidOperationException)
        {
            // While the user is midway through a custom-header row, the
            // draft is intentionally incomplete. Treat old evidence as stale
            // without turning a text-change notification into an exception.
            return null;
        }
    }

    private void LoadStoredTestEvidence(ProviderProfile profile)
    {
        _pendingTestOutcome = null;
        _pendingTestedAtUtc = null;
        _pendingTestFingerprint = null;
        if (profile.LastTestedAtUtc is null || string.IsNullOrWhiteSpace(profile.LastTestOutcome))
        {
            SetTestResult(StatusTone.Info, string.Empty, null);
            return;
        }
        var current = CreateProfileFingerprint(profile, HasStoredKey(profile));
        if (!string.Equals(current, profile.LastTestFingerprint, StringComparison.Ordinal))
        {
            SetTestResult(StatusTone.Warning, "需要重新测试", "配置已经改变。");
            return;
        }
        _pendingTestOutcome = profile.LastTestOutcome;
        _pendingTestedAtUtc = profile.LastTestedAtUtc;
        _pendingTestFingerprint = profile.LastTestFingerprint;
        var (text, tone) = DescribeProfileState(
            ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl), HasStoredKey(profile), profile.LastTestOutcome);
        var summary = profile.LastTestOutcome == "ok"
            ? "上次成功"
            : "上次测试未通过";
        SetTestResult(tone, summary, text);
    }

    /// <summary>Maps a raw test error to a session outcome code.</summary>
    internal static string ClassifyTestFailure(Exception exception) =>
        ConnectionTestDiagnostics.Classify(exception);

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

    /// <summary>
    /// Mirrors the active profile into the core via the shared implementation
    /// (also used by the footer quick switcher) so both paths stay identical.
    /// </summary>
    private static void ApplyToCore(CoreProductConfig config) =>
        ProfileManager.ApplyActiveToCore(config);

    internal void SelectProfileInList(string profileId)
    {
        DisarmAllConfirms();
        var items = ProfilesListBox.ItemsSource as IEnumerable<ProfilesRow> ?? [];
        var index = items.ToList().FindIndex(row => row.Id == profileId);
        ProfilesListBox.SelectedIndex = index >= 0 ? index : -1;
    }

    // ================= Editor state =================

    private void ShowEditorForm(bool addMode)
    {
        DisarmAllConfirms();
        _catalogOperation?.Cancel();
        CancelCliOperation();
        CliConnectionsPanel.Visibility = Visibility.Collapsed;
        if (AddEngineHeaderButton is not null)
        {
            AddEngineHeaderButton.Visibility = Visibility.Collapsed;
        }
        BackToListHeaderButton.Visibility = Visibility.Visible;
        EditorEmpty.Visibility = Visibility.Collapsed;
        EditorForm.Visibility = Visibility.Visible;
        PresetsPanel.Visibility = addMode ? Visibility.Visible : Visibility.Collapsed;
        ConfigFormPanel.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        EditorActionBar.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        ChooseAnotherProviderButton.Visibility = Visibility.Collapsed;
        DeleteServiceButton.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        if (TestAndSaveButton is not null)
        {
            TestAndSaveButton.Visibility = addMode ? Visibility.Collapsed : Visibility.Visible;
        }
        _isAdding = addMode;
        // compact 判定只来自 DetailGrid 内容宽度（SizeChanged）或未布局时的
        // 一次性窗口宽度提示；这里不再按 ActualWidth 重新推断第二套判据。
        ApplyEditorLayout();
        UpdateSaveButtonLabel();
        UpdateDeleteTooltip();
        EditorOpenStateChanged?.Invoke();
    }

    // internal: SettingsWindow's close guard folds a clean editor back to the
    // overview (SettingsWindow.xaml.cs "ProviderSection.ShowOverview()").
    internal void ShowOverview()
    {
        DisarmAllConfirms();
        if (_cliCommitInProgress || _saveInProgress) return;
        _catalogOperation?.Cancel();
        if (AddEngineHeaderButton is not null)
        {
            AddEngineHeaderButton.Visibility = Visibility.Visible;
        }
        BackToListHeaderButton.Visibility = Visibility.Collapsed;
        ChooseAnotherProviderButton.Visibility = Visibility.Collapsed;
        HideDraftGuard();
        EditorForm.Visibility = Visibility.Collapsed;
        EditorEmpty.Visibility = Visibility.Visible;
        if (CliConnectionsPanel is not null)
        {
            CancelCliOperation();
            CliConnectionsPanel.Visibility = Visibility.Collapsed;
        }
        _isAdding = false;
        ClearEditorDirty();
        RefreshProfilesList();
        EditorOpenStateChanged?.Invoke();
    }

    /// <summary>
    /// Save never silently changes the live route: the first configured
    /// engine "保存并使用", later ones just "保存引擎", edits say "保存修改".
    /// </summary>
    private void UpdateSaveButtonLabel()
    {
        if (_isAdding)
        {
            SaveServiceButton.Content = ProfileManager.Load().Profiles.Count == 0
                ? "保存并使用"
                : "保存引擎";
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
        var accountManaged = profile.ExecutionFacts.IsCliExecution || profile.ResolvedExecutionKind == EngineExecutionKind.ChatGptDirect;
        var retainedAccount = accountManaged ? " 删除引擎不会删除账号登录。" : "";
        DeleteServiceButton.ToolTip = isTextDefault
            ? (nextDefault is null
                ? "它是当前默认文字引擎；删除后没有其他可用引擎，翻译将回退到已授权的内置免费引擎。" + retainedAccount
                : $"它是当前默认文字引擎；删除后默认路由将切换为「{nextDefault.Name}」。" + retainedAccount)
            : isVisionDefault
                ? "它是当前默认视觉引擎；删除后视觉翻译将跟随默认文字引擎。" + retainedAccount
                : accountManaged ? "默认路由不受影响。删除引擎不会删除账号登录。"
                    : "默认路由不受影响。该引擎保存的 API Key 也会一并删除。";
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

    private void UpdateEditorIdentity(string? name)
    {
        EditorProviderTitle.Text = string.IsNullOrWhiteSpace(name) ? "新引擎" : name.Trim();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string preset)
        {
            return;
        }

        CancelCliOperation();
        var isCustom = preset == "custom";
        // Presets provide protocol and official endpoints only. Models must
        // be fetched from the provider's catalog or typed by the user — a
        // preset never claims a model exists or that it supports images.
        var (type, baseUrl, endpoint, note) = preset switch
        {
            "openai" => (ProviderType.OpenAiCompatible, "https://api.openai.com/v1",
                "/chat/completions", "已填入 OpenAI 接入地址。"),
            "deepseek" => (ProviderType.OpenAiCompatible, "https://api.deepseek.com/v1",
                "/chat/completions", "已填入 DeepSeek 接入地址。"),
            "gemini" => (ProviderType.GeminiGenerateContent, "https://generativelanguage.googleapis.com",
                "/v1beta/models/{model}:generateContent", "已填入 Gemini 接入地址。"),
            "claude" => (ProviderType.AnthropicMessages, "https://api.anthropic.com",
                "/v1/messages", "已填入 Claude 接入地址。"),
            "zhipu" => (ProviderType.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4",
                "/chat/completions", "已填入智谱 GLM 接入地址（glm-4-flash 长期免费）。"),
            "siliconflow" => (ProviderType.OpenAiCompatible, "https://api.siliconflow.cn/v1",
                "/chat/completions", "已填入 SiliconFlow（硅基流动）接入地址。"),
            "groq" => (ProviderType.OpenAiCompatible, "https://api.groq.com/openai/v1",
                "/chat/completions", "已填入 Groq 接入地址。"),
            "openrouter" => (ProviderType.OpenAiCompatible, "https://openrouter.ai/api/v1",
                "/chat/completions", "已填入 OpenRouter 接入地址（支持 :free 免费模型）。"),
            "ollama" => (ProviderType.OpenAiCompatible, "http://localhost:11434/v1",
                "/chat/completions", "已应用 Ollama 本地预设。"),
            "lm-studio" => (ProviderType.OpenAiCompatible, "http://localhost:1234/v1",
                "/chat/completions", "已应用 LM Studio 本地预设。"),
            "litellm" => (ProviderType.OpenAiCompatible, "http://localhost:4000",
                "/chat/completions", "已填入 LiteLLM Proxy 预设。"),
            _ => (ProviderType.OpenAiCompatible, string.Empty,
                "/chat/completions", "填写协议和请求地址，再刷新或输入模型。"),
        };

        _loading = true;
        _catalogOperation?.Cancel();
        TextModelCombo.ItemsSource = null;
        VisionModelCombo.ItemsSource = null;
        _cachedCatalogDescriptors = null;
        _catalogSourceHost = null;
        _catalogFetchedAtUtc = null;
        try
        {
            if (string.IsNullOrWhiteSpace(ServiceNameTextBox.Text) ||
                ServiceNameTextBox.Text.StartsWith("新引擎") ||
                ServiceNameTextBox.Text.StartsWith("新服务") ||
                ServiceNameTextBox.Text is "OpenAI" or "DeepSeek" or "Google Gemini" or "Anthropic Claude" or "智谱 GLM" or "SiliconFlow（硅基流动）" or "Groq" or "OpenRouter" or "Ollama（本地）" or "LM Studio（本地）" or "LiteLLM Proxy" or "自定义引擎" or "自定义服务")
            {
                ServiceNameTextBox.Text = preset switch
                {
                    "openai" => "OpenAI",
                    "deepseek" => "DeepSeek",
                    "gemini" => "Google Gemini",
                    "claude" => "Anthropic Claude",
                    "zhipu" => "智谱 GLM",
                    "siliconflow" => "SiliconFlow（硅基流动）",
                    "groq" => "Groq",
                    "openrouter" => "OpenRouter",
                    "ollama" => "Ollama（本地）",
                    "lm-studio" => "LM Studio（本地）",
                    "litellm" => "LiteLLM Proxy",
                    _ => "自定义引擎",
                };
            }

            UpdatePresetTierHint(preset);

            Helpers.SelectComboByTag(ProviderTypeComboBox, type.ToString());
            BaseUrlTextBox.Text = baseUrl;
            TextEndpointTextBox.Text = endpoint;
            VisionEndpointTextBox.Text = endpoint;
            UpdateModelSuggestions(type);
            // 预设只填协议与接入地址；模型名一律不发明——预设写死的模型
            // ID 会随服务商目录过期（接入即 404 的根源），一律改为拉取
            // 真实目录（「获取模型」）或由用户手填。
            TextModelCombo.Text = string.Empty;
            VisionModelCombo.Text = string.Empty;
            AnthropicVersionTextBox.Text = "2023-06-01";
            // 新引擎从保守默认开始：自动思考、自动上下文、自适应输出。
            Helpers.SelectComboByTag(ReasoningLevelCombo, "auto");
            ContextTokensTextBox.Text = string.Empty;
            OutputTokensTextBox.Text = string.Empty;
            SupportsTextCheckBox.IsChecked = true;
            SupportsVisionCheckBox.IsChecked = false;
            _visionTracker.Reset();
            UseTextModelForVisionCheckBox.IsChecked = false;
            VisionModelCombo.IsEnabled = true;
            if (SharedModelHintText != null)
            {
                SharedModelHintText.Visibility = Visibility.Collapsed;
            }
            // Keep the actual address and protocol editable even for presets.
            CustomProtocolGroup.Visibility = Visibility.Visible;
            BaseUrlPanel.Visibility = Visibility.Visible;
            AdvancedGroup.Visibility = Visibility.Visible;
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
        DeleteServiceButton.Visibility = Visibility.Collapsed;
        if (TestAndSaveButton is not null)
        {
            TestAndSaveButton.Visibility = Visibility.Visible;
        }
        // 本地判定与保存后的 profile.IsLocal（IsLocalBaseUrl）同一口径，
        // 避免 Ollama/LiteLLM 这类本机预设在添加预览与重开编辑器之间
        // 「本地服务 / OpenAI 兼容」来回变。
        UpdateEditorIdentity(ServiceNameTextBox.Text);
        UpdateCredentialGating();
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
        if (isCustom || preset == "litellm")
        {
            BaseUrlTextBox.Focus();
        }
        else if (preset is "ollama" or "lm-studio")
        {
            FetchModelsButton.Focus();
        }
        else
        {
            ApiKeyPasswordBox.Focus();
        }
    }

    private void UpdatePresetTierHint(string? presetId)
    {
        if (PresetTierHintBanner is null || PresetTierHintText is null)
        {
            return;
        }
        var curated = !string.IsNullOrWhiteSpace(presetId)
            ? ModelCatalogService.CuratedFreePresets.FirstOrDefault(p =>
                string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
            : null;
        if (curated is null)
        {
            PresetTierHintText.Text = string.Empty;
            PresetTierHintBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var modelHint = string.IsNullOrWhiteSpace(curated.DefaultModel)
            ? string.Empty
            : $" · 推荐免费/入门模型：{curated.DefaultModel}";
        PresetTierHintText.Text = $"{curated.FreeTierHint}{modelHint}";
        PresetTierHintBanner.Visibility = Visibility.Visible;
    }

    private void ChooseAnotherProvider_Click(object sender, RoutedEventArgs e) =>
        BeginDraftGuard("切换来源前请先处理当前修改。", ShowSourcePicker);

    private void ShowSourcePicker()
    {
        CancelCliOperation();
        _catalogOperation?.Cancel();
        ShowEditorForm(addMode: true);
        _editingProfileId = null;
        LoadProfileIntoForm(NewProfileDraft());
        CustomProtocolGroup.Visibility = Visibility.Collapsed;
        UpdatePresetTierHint(null);
        _selectingCliTool = true;
        try { CliToolsListBox.SelectedItem = null; }
        finally { _selectingCliTool = false; }
        ClearEditorDirty();
        EditorScroll.ScrollToTop();
    }

    private void BackToServices_Click(object sender, RoutedEventArgs e) =>
        BeginDraftGuard("返回引擎列表前请先处理当前修改。", ShowOverview);

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
        _catalogSourceHost = null;
        _catalogFetchedAtUtc = null;
        UpdateModelSuggestions(providerType);
        RefreshRecommendations();
        ResetModelCatalogStatus();
    }

    private void UpdateModelSuggestions(ProviderType providerType)
    {
        _ = providerType;
        InvalidateModelCatalog();
    }

    private void InvalidateModelCatalog()
    {
        _catalogOperation?.Cancel();
        _cachedCatalogDescriptors = null; _catalogSourceHost = null; _catalogFetchedAtUtc = null;
        var text = TextModelCombo.Text; var vision = VisionModelCombo.Text;
        var wasLoading = _loading; _loading = true;
        try
        {
            TextModelCombo.ItemsSource = null; VisionModelCombo.ItemsSource = null;
            TextModelCombo.Text = text; VisionModelCombo.Text = vision;
        }
        finally { _loading = wasLoading; }
        ResetModelCatalogStatus();
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_catalogOperation is not null) return;
        var fetchedRevision = _editorRevision;
        var fetchedProfileId = _editingProfileId;
        using var operation = new CancellationTokenSource();
        _catalogOperation = operation;
        bool IsCurrentCatalog() => !operation.IsCancellationRequested && fetchedRevision == _editorRevision && fetchedProfileId == _editingProfileId;
        ProviderSettings draft;
        string? typedKey;
        try
        {
            draft = BuildDraftSettings();
            typedKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? CredentialStore.LoadApiKey(CurrentCredentialTarget())
                : ApiKeyPasswordBox.Password.Trim();
            if (ProfileVerification.IsMaskNotAKey(typedKey)) typedKey = null;
        }
        catch (Exception exception)
        {
            _catalogOperation = null;
            SetModelCatalogStatus(DescribeModelCatalogFailure(exception), StatusTone.Error);
            return;
        }
        if (string.IsNullOrWhiteSpace(typedKey) && !draft.TargetsLocalRuntime)
        {
            ApiKeyPasswordBox.Focus();
            _catalogOperation = null;
            SetModelCatalogStatus("需要 API Key。本地服务可以留空。", StatusTone.Warning);
            UpdateCredentialGating();
            return;
        }

        FetchModelsButton.IsEnabled = false;
        FetchModelsButton.ToolTip = "正在刷新…";
        SetModelCatalogStatus("正在刷新…", StatusTone.Info);
        try
        {
            var result = await ModelCatalogExecutor(draft, typedKey ?? string.Empty, operation.Token);
            if (!IsCurrentCatalog()) return;

            SetModelCatalogStatus(
                ModelCatalogMessages.DescribeLoad(result.Models.Count),
                StatusTone.Info);
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
            if (IsCurrentCatalog()) SetModelCatalogStatus(DescribeModelCatalogFailure(exception), StatusTone.Error);
        }
        finally
        {
            if (ReferenceEquals(_catalogOperation, operation)) _catalogOperation = null;
            UpdateCredentialGating();
        }
    }

    private void ApplyModelSuggestions(ModelCatalogResult result)
    {
        _cachedCatalogDescriptors = result.Models;
        _catalogSourceHost = result.Endpoint.Host;
        _catalogFetchedAtUtc = DateTime.UtcNow;
        var ids = result.Models.Select(model => model.Id).ToList();
        // Refreshing keeps the current selection; a pick that no longer
        // exists in the catalog is flagged, never silently replaced.
        var currentText = TextModelCombo.Text;
        var currentVision = VisionModelCombo.Text;
        if (ids.Count == 0)
        {
            TextModelCombo.ItemsSource = null;
            VisionModelCombo.ItemsSource = null;
            TextModelCombo.Text = currentText;
            VisionModelCombo.Text = currentVision;
            TextModelCombo.IsDropDownOpen = false;
            VisionModelCombo.IsDropDownOpen = false;
            RefreshRecommendations();
            return;
        }

        TextModelCombo.ItemsSource = ids;
        VisionModelCombo.ItemsSource = ids;
        TextModelCombo.Text = currentText;
        VisionModelCombo.Text = currentVision;
        if (UseTextModelForVisionCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(currentVision))
        {
            VisionModelCombo.Text = currentText;
        }

        if (!string.IsNullOrWhiteSpace(currentText) && result.Models.All(model => model.Id != currentText))
        {
            SetModelCatalogStatus("当前文字模型不在列表中；可按服务商文档确认，或重新选择。", StatusTone.Warning);
        }
        else if (!string.IsNullOrWhiteSpace(currentVision) &&
            !currentVision.StartsWith('{') &&
            result.Models.All(model => model.Id != currentVision))
        {
            SetModelCatalogStatus(
                "当前图片模型不在列表中。",
                StatusTone.Warning);
        }
        else if (!string.IsNullOrWhiteSpace(currentVision) &&
            result.Models.FirstOrDefault(model => model.Id == currentVision) is { VisionInput: CapabilityState.Unknown })
        {
            SetModelCatalogStatus(
                "图片能力未标注，请按服务商说明确认。",
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

        // Ranking logic lives in the coordinator; the view only renders.
        var currentText = TextModelCombo.Text?.Trim();
        var sharedVision = UseTextModelForVisionCheckBox.IsChecked == true;
        var (textResult, visionResult) = ServiceDraftCoordinator.ComputeRecommendations(
            providerType,
            isLocal,
            descriptors,
            _currentPreference,
            currentText,
            VisionModelCombo.Text?.Trim(),
            sharedVision);

        PopulateRecommendationChips(
            TextRecommendationChipsPanel,
            textResult.Candidates.Where(c => c.IsEligible).Take(3),
            isVision: false);

        if (visionResult is null)
        {
            VisionRecommendationChipsPanel.Children.Clear();
            return;
        }

        PopulateRecommendationChips(
            VisionRecommendationChipsPanel,
            visionResult.Candidates.Where(c => c.IsEligible).Take(3),
            isVision: true);
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
            var chipWidth = panel.ActualWidth > 24 ? panel.ActualWidth - 8 : 220;
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = modelId,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    MaxWidth = Math.Max(40, chipWidth - 16),
                },
                MaxWidth = chipWidth,
                Tag = modelId,
                Style = style,
                ToolTip = BuildEvidenceTooltip(candidate),
            };

            var label = isVision ? $"推荐图片模型 {modelId}" : $"推荐文字模型 {modelId}";
            System.Windows.Automation.AutomationProperties.SetName(button, label);

            if (isVision)
            {
                button.Click += (_, _) =>
                {
                    if (UseTextModelForVisionCheckBox.IsChecked == true)
                    {
                        UseTextModelForVisionCheckBox.IsChecked = false;
                        VisionModelCombo.IsEnabled = true;
                    }
                    VisionModelCombo.Text = modelId;
                    VisionModelCombo.ToolTip = modelId;
                    MarkEditorDirty();
                    RefreshRecommendations();
                };
            }
            else
            {
                button.Click += (_, _) =>
                {
                    TextModelCombo.Text = modelId;
                    TextModelCombo.ToolTip = modelId;
                    if (UseTextModelForVisionCheckBox.IsChecked == true)
                    {
                        VisionModelCombo.Text = modelId;
                        VisionModelCombo.ToolTip = modelId;
                    }
                    MarkEditorDirty();
                    RefreshRecommendations();
                };
            }

            panel.Children.Add(button);
        }
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

    /// <summary>
    /// C21 证据溯源 tooltip：等级 + 目录来源主机 + 采集时间（本机时区）。
    /// 没有目录事实时只声明等级；证据永不混入猜测，缺失能力按未知处理。
    /// </summary>
    internal string BuildEvidenceTooltip(ModelCandidateEvaluation candidate)
    {
        var tier = ResolveEvidenceTier(candidate.EvidenceSources, candidate.BenchmarkEvidence is not null);
        var lines = new List<string> { candidate.Model.Id ?? string.Empty, $"推荐依据：{GetEvidenceBadgeText(tier)}" };
        if (!string.IsNullOrWhiteSpace(_catalogSourceHost))
        {
            lines.Add($"目录来源：{_catalogSourceHost}");
        }
        if (_catalogFetchedAtUtc is { } fetchedAt)
        {
            lines.Add($"获取时间：{fetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}（本机时间）");
        }
        lines.Add("未提供的能力信息显示为未知。");
        return string.Join("\n", lines);
    }

    internal static string GetEvidenceBadgeText(EvidenceBadgeTier tier) => tier switch
    {
        EvidenceBadgeTier.LocalBenchmark => "本机实测",
        EvidenceBadgeTier.CatalogExplicit => "官方声明",
        EvidenceBadgeTier.FamilyHeuristics => "系列推断",
        _ => "未声明",
    };


    private void ResetModelCatalogStatus()
    {
        ModelCatalogStatusText.Text = string.Empty;
        ModelCatalogStatusText.Visibility = Visibility.Collapsed;
    }

    private void SetModelCatalogStatus(string message, StatusTone tone)
    {
        ModelCatalogStatusText.Text = message;
        var brushKey = tone switch
        {
            StatusTone.Success => "SuccessBrush",
            StatusTone.Warning => "WarningBrush",
            StatusTone.Error => "DangerBrush",
            _ => "TextTertiaryBrush",
        };
        ModelCatalogStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        ModelCatalogStatusText.Visibility = Visibility.Visible;
    }

    internal static string DescribeModelCatalogFailure(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (exception is TaskCanceledException || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "刷新超时，请检查请求地址和网络。";
        }
        if (exception is OperationCanceledException)
        {
            return "已取消刷新。";
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

    internal static IReadOnlyDictionary<string, string> ParseExtraHeaders(string text) =>
        ServiceDraftCoordinator.ParseHeaders(text);

    // ================= Draft connection test =================

    /// <summary>Builds the core settings a draft test would use; nothing is saved.</summary>
    private ProviderSettings BuildDraftSettings()
    {
        ValidateGenerationForm();
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
            // 连接测试用与真实翻译一致的生成参数，测试结果才有代表性。
            ReasoningLevel = ServiceDraftCoordinator.NormalizeReasoningLevel(
                (ReasoningLevelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
            ContextTokens = ParseTokenSetting(ContextTokensTextBox.Text),
            OutputTokens = ParseTokenSetting(OutputTokensTextBox.Text),
        };
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e) =>
        await RunDraftConnectionTestAsync();

    private async void TestAndSave_Click(object sender, RoutedEventArgs e)
    {
        if (await RunDraftConnectionTestAsync())
        {
            await TrySaveServiceAsync();
        }
    }

    private async Task<bool> RunDraftConnectionTestAsync()
    {
        if (_testInProgress)
        {
            return false;
        }

        var gateId = string.IsNullOrWhiteSpace(_editingProfileId) ? "draft-editor" : _editingProfileId;
        if (!ConnectionTestRunner.TryEnter(gateId))
        {
            SetTestResult(StatusTone.Warning, "这个引擎正在测试", "请等这次结束再试。");
            return false;
        }

        _testInProgress = true;
        var testedRevision = _editorRevision;
        var testedProfileId = _editingProfileId;
        string? testedFingerprint = null;
        bool IsCurrentTest() => testedRevision == _editorRevision && testedProfileId == _editingProfileId;
        TestConnectionButton.IsEnabled = false;
        if (TestAndSaveButton is not null)
        {
            TestAndSaveButton.IsEnabled = false;
        }
        SetTestResult(StatusTone.Info, "正在测试…", null);
        try
        {
            var draft = BuildDraftSettings();
            var typedKey = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
                ? CredentialStore.LoadApiKey(CurrentCredentialTarget())
                : ApiKeyPasswordBox.Password.Trim();
            if (ProfileVerification.IsMaskNotAKey(typedKey))
            {
                typedKey = null;
            }

            if (string.IsNullOrWhiteSpace(typedKey) && !draft.TargetsLocalRuntime)
            {
                ApiKeyPasswordBox.Focus();
                SetTestResult(StatusTone.Warning, "需要 API Key", "填写后再试。本地服务可以留空。");
                return false;
            }
            if (string.IsNullOrWhiteSpace(draft.TextModel))
            {
                TextModelCombo.Focus();
                SetTestResult(StatusTone.Warning, "需要文字模型", "刷新列表选择一个，或直接输入模型名。");
                return false;
            }
            testedFingerprint = CurrentDraftFingerprint();
            var watch = Stopwatch.StartNew();
            _ = await ConnectionTestExecutor(
                draft, string.IsNullOrWhiteSpace(typedKey) ? "local" : typedKey);
            watch.Stop();
            if (!IsCurrentTest())
            {
                if (testedProfileId == _editingProfileId)
                    SetTestResult(StatusTone.Warning, "配置已变化，请重新测试", null);
                return false;
            }
            var testedAtUtc = DateTime.UtcNow;
            var fingerprint = testedFingerprint;
            var elapsedMs = watch.ElapsedMilliseconds;
            var latencyText = TranslationElapsedText.ForMilliseconds(elapsedMs);
            SetTestResult(
                StatusTone.Success,
                $"测试通过 · {latencyText}",
                "这次只检查了文字生成，不包括图片和流式输出。");
            _pendingTestOutcome = "ok";
            _pendingTestedAtUtc = testedAtUtc;
            _pendingTestFingerprint = fingerprint;
            _pendingTestElapsedMs = elapsedMs;
            if (testedProfileId is not null)
            {
                _testLatencies[testedProfileId] = elapsedMs;
            }
            if (testedProfileId is not null && string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            {
                await PersistEditorEvidenceAsync(testedProfileId, "ok", testedAtUtc, fingerprint);
                RefreshProfilesList();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentTest())
            {
                SetTestResult(StatusTone.Warning, "已取消", null);
            }
            return false;
        }
        catch (Exception exception)
        {
            if (!IsCurrentTest())
            {
                if (testedProfileId == _editingProfileId)
                    SetTestResult(StatusTone.Warning, "配置已变化，请重新测试", null);
                return false;
            }
            var outcome = ClassifyTestFailure(exception);
            var testedAtUtc = DateTime.UtcNow;
            var fingerprint = testedFingerprint;
            SetTestResult(
                StatusTone.Error,
                ConnectionTestDiagnostics.ShortLabel(outcome),
                DescribeTestFailure(exception));
            _pendingTestOutcome = outcome;
            _pendingTestedAtUtc = testedAtUtc;
            _pendingTestFingerprint = fingerprint;
            _pendingTestElapsedMs = null;
            if (testedProfileId is not null &&
                fingerprint is not null &&
                string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password))
            {
                await PersistEditorEvidenceAsync(testedProfileId, outcome, testedAtUtc, fingerprint);
                RefreshProfilesList();
            }
            return false;
        }
        finally
        {
            _testInProgress = false;
            ConnectionTestRunner.Exit(gateId);
            UpdateCredentialGating();
            if (TestAndSaveButton is not null)
            {
                TestAndSaveButton.IsEnabled = true;
            }
        }
    }

    private async void TestProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string profileId)
        {
            return;
        }
        var button = (Button)sender;
        if (ConnectionTestRunner.IsInFlight(profileId))
        {
            StatusChanged?.Invoke("这个引擎正在测试。请等这次结束再试。", StatusTone.Warning);
            return;
        }

        var config = ProfileManager.Load();
        var profile = config.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return;
        }
        if (profile.ExecutionFacts.IsCliExecution || profile.ResolvedExecutionKind == EngineExecutionKind.ChatGptDirect)
        {
            await RunSavedListTestAsync(profile, button);
            return;
        }
        var key = LoadStoredKey(profile);
        if (ProfileVerification.IsMaskNotAKey(key))
        {
            key = null;
        }
        if (string.IsNullOrWhiteSpace(key) && !ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl))
        {
            _testOutcomes[profile.Id] = "auth";
            StatusChanged?.Invoke($"引擎「{profile.Name}」缺少 API Key，请先编辑补全。", StatusTone.Warning);
            RefreshProfilesList();
            return;
        }
        if (string.IsNullOrWhiteSpace(profile.TextModel))
        {
            StatusChanged?.Invoke($"引擎「{profile.Name}」尚未配置文字模型。", StatusTone.Warning);
            return;
        }

        button.IsEnabled = false;
        var profileName = profile.Name;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _listTests.Add(profileId, operation);
        try
        {
            StatusChanged?.Invoke($"正在测试「{profileName}」的文字生成…", StatusTone.Info);
            var completion = await ConnectionTestRunner.RunListTestAsync(profileId, async captured =>
            {
                var liveKey = LoadStoredKey(captured);
                if (ProfileVerification.IsMaskNotAKey(liveKey))
                {
                    throw new InvalidOperationException("密钥无效");
                }

                var settings = captured.ToProviderSettings(CoreBridge.GetSettings());
                await ConnectionTestExecutor(
                    settings, string.IsNullOrWhiteSpace(liveKey) ? "local" : liveKey);
                return "ok";
            }, operation.Token);

            ReportListTestOutcome(completion, profileId, profileName);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"「{profileName}」{CliFailureText(ex)}", StatusTone.Error);
        }
        finally
        {
            _listTests.Remove(profileId);
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Shared outcome reporting for list-level engine tests: persists the
    /// outcome, repaints the list and reports status.
    /// </summary>
    private void ReportListTestOutcome(ListTestCompletion completion, string profileId, string profileName)
    {
        var label = ConnectionTestDiagnostics.ShortLabel(
            completion.Apply is VerificationApplyResult.Stale or VerificationApplyResult.Missing
                ? "stale"
                : completion.Outcome);
        if (completion.Apply == VerificationApplyResult.Applied)
        {
            _testOutcomes[profileId] = completion.Outcome;
            if (completion.Outcome == "ok" && completion.ElapsedMs >= 0)
            {
                _testLatencies[profileId] = completion.ElapsedMs;
            }
            else
            {
                _testLatencies.Remove(profileId);
            }
        }
        else
        {
            _testOutcomes.Remove(profileId);
            _testLatencies.Remove(profileId);
        }

        RefreshProfilesList();
        ProfileChanged?.Invoke();
        var tone = completion.Apply == VerificationApplyResult.Applied && completion.Outcome == "ok"
            ? StatusTone.Success
            : completion.Apply == VerificationApplyResult.Applied
                ? StatusTone.Error
                : StatusTone.Warning;
        var latencySuffix = completion.ElapsedMs > 0
            ? $"（{TranslationElapsedText.ForMilliseconds(completion.ElapsedMs)}）"
            : string.Empty;
        var message = completion.Apply switch
        {
            VerificationApplyResult.Applied when completion.Outcome == "ok" =>
                $"「{profileName}」测试通过{latencySuffix}。这次只检查了文字生成。",
            VerificationApplyResult.Applied => $"「{profileName}」{label}{latencySuffix}。{(completion.Error is null ? "" : CliFailureText(completion.Error))}",
            VerificationApplyResult.Busy => "这个引擎正在测试。请等这次结束再试。",
            VerificationApplyResult.Cancelled => "测试已取消，没有写入配置。",
            _ => $"「{profileName}」的配置已变化，这次结果没有写入。",
        };
        StatusChanged?.Invoke(message, tone);
    }

    private async Task RunSavedListTestAsync(ProviderProfile profile, Button button)
    {
        button.IsEnabled = false;
        var profileName = profile.Name;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _listTests.Add(profile.Id, operation);
        try
        {
            StatusChanged?.Invoke($"正在通过「{profileName}」发送测试句 Hello world…", StatusTone.Info);
            var completion = await ConnectionTestRunner.RunListTestAsync(profile.Id, async captured =>
            {
                var response = await SavedConnectionTestExecutor(captured, operation.Token);
                if (response.Result.IsPartial || string.IsNullOrWhiteSpace(response.Result.TranslatedText))
                {
                    throw new InvalidOperationException(
                        TranslationStatusText.FailureReason(response.Result.Warnings) ?? "工具返回了不完整的翻译。");
                }
                return "ok";
            }, operation.Token);
            ReportListTestOutcome(completion, profile.Id, profileName);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"「{profileName}」{CliFailureText(ex)}", StatusTone.Error);
            RefreshProfilesList();
        }
        finally
        {
            _listTests.Remove(profile.Id);
            button.IsEnabled = true;
        }
    }

    private static Task PersistEditorEvidenceAsync(
        string profileId, string outcome, DateTime testedAtUtc, string draftFingerprint)
    {
        var config = ProfileManager.Load();
        var profile = config.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            return Task.CompletedTask;
        }

        var lease = ProfileVerification.Capture(profile, ProfileVerification.CredentialAvailable(profile));
        if (!string.Equals(lease.EvidenceFingerprint, draftFingerprint, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => ProfileManager.TryPatchVerification(lease, outcome, testedAtUtc));
    }

    /// <summary>Structured two-line test result: state dot + summary, bounded detail.</summary>
    private void SetTestResult(StatusTone tone, string summary, string? detail)
    {
        TestStatusPanel.Visibility = string.IsNullOrWhiteSpace(summary)
            ? Visibility.Collapsed
            : Visibility.Visible;
        TestSummaryText.Text = summary;
        var (dotBrushKey, textBrushKey, bgBrushKey, borderBrushKey) = tone switch
        {
            StatusTone.Success => ("SuccessBrush", "SuccessBrush", "SuccessSoftBrush", "BorderSubtleBrush"),
            StatusTone.Error => ("DangerBrush", "DangerBrush", "DangerSoftBrush", "DangerBrush"),
            StatusTone.Warning => ("WarningBrush", "WarningBrush", "WarningSoftBrush", "WarningBrush"),
            _ => ("TextTertiaryBrush", "TextSecondaryBrush", "SurfaceMutedBrush", "BorderSubtleBrush"),
        };
        TestStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dotBrushKey);
        TestSummaryText.SetResourceReference(TextBlock.ForegroundProperty, textBrushKey);
        TestStatusPanel.SetResourceReference(Border.BackgroundProperty, bgBrushKey);
        TestStatusPanel.SetResourceReference(Border.BorderBrushProperty, borderBrushKey);
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
        if (message.Contains("模型", StringComparison.Ordinal) ||
            message.Contains("Base URL", StringComparison.Ordinal) ||
            message.Contains("至少需要", StringComparison.Ordinal))
        {
            // 配置类错误（缺模型/地址非法）与网络无关，原样返回，
            // 不再追加“请检查网络连接”的误导性建议。
            return message.TrimEnd('。') + "。";
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
            StatusChanged?.Invoke("输入已清空。", StatusTone.Info);
        }
        // Edit mode keeps the destructive credential deletion behind the
        // existing inline confirmation. "清空全部" in the data surfaces is
        // the immediate action; deleting a saved key remains a separate risk.
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
            string? previousKey = null;
            var hadPreviousKey = false;
            try
            {
                previousKey = CredentialStore.LoadApiKey(target);
                hadPreviousKey = !string.IsNullOrEmpty(previousKey);
            }
            catch (Exception)
            {
                StatusChanged?.Invoke("清除 API Key 失败。原来的密钥还在。", StatusTone.Error);
                return;
            }

            CredentialStore.DeleteApiKey(target);
            try
            {
                if (profile is not null && !ProfileManager.NoteCredentialChanged(profile.Id))
                {
                    TryRestoreCredential(target, previousKey, hadPreviousKey);
                    StatusChanged?.Invoke("清除 API Key 失败。原来的密钥还在。", StatusTone.Error);
                    return;
                }
            }
            catch (Exception)
            {
                TryRestoreCredential(target, previousKey, hadPreviousKey);
                StatusChanged?.Invoke("清除 API Key 失败。原来的密钥还在。", StatusTone.Error);
                return;
            }

            ApiKeyPasswordBox.Clear();
            _pendingTestOutcome = null;
            _pendingTestedAtUtc = null;
            _pendingTestFingerprint = null;
            if (_editingProfileId is not null)
            {
                _testOutcomes.Remove(_editingProfileId);
                _testLatencies.Remove(_editingProfileId);
            }
            SetTestResult(StatusTone.Info, string.Empty, null);
            RefreshApiKeyState();
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke("API Key 已清除。", StatusTone.Info);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"清除 API Key 失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ================= Profile CRUD =================

    /// <summary>
    /// Direct entry from the main-window "添加翻译引擎" call to action:
    /// opens the add-engine flow exactly as the 添加引擎 button does.
    /// </summary>
    internal void BeginAddEngineFlow() => AddProfile_Click(this, new RoutedEventArgs());

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        // Starting a new engine must not wipe an unsaved draft; the inline
        // guard bar resolves it first and then re-runs this action.
        BeginDraftGuard("新增引擎前请先处理当前草稿。", StartAddMode);
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
        CustomProtocolGroup.Visibility = Visibility.Collapsed;
        AdvancedGroup.Visibility = Visibility.Visible;
        ApplyEditorLayout();
        SetTestResult(StatusTone.Info, string.Empty, null);
        ShowSourcePicker();
        RefreshApiKeyState();
        ClearEditorDirty();
        StatusChanged?.Invoke("选择来源，再连接账号和模型。", StatusTone.Info);
    }

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DisarmAllConfirms();
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
                "切换引擎前请先处理当前草稿。",
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

    private void SetDefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string profileId)
        {
            return;
        }

        try
        {
            if (!ProfileManager.TrySwitchActiveProfile(profileId, out var error))
            {
                StatusChanged?.Invoke(error, StatusTone.Warning);
                return;
            }

            var profile = ProfileManager.Load().Profiles.FirstOrDefault(item => item.Id == profileId);
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke(
                profile is null ? "已切换引擎。" : $"已切换到「{profile.Name}」。",
                StatusTone.Success);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"切换失败：{exception.Message}", StatusTone.Error);
        }
    }

    private void OpenProfileInEditor(string profileId)
    {
        DisarmAllConfirms();
        var profile = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return;
        }
        if (_saveInProgress || _cliCommitInProgress) return;
        if (profile.ResolvedExecutionKind == EngineExecutionKind.ChatGptDirect) { OpenDirectProfile(profile); return; }
        if (profile.ExecutionFacts.IsCliExecution) { OpenCliProfile(profile); return; }
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
            StatusChanged?.Invoke("请先在列表中选择要删除的引擎。", StatusTone.Info);
        }
        // A selected profile is deleted only through ConfirmButton's two-step
        // click; deleting here too would wipe on the first click.
    }

    private void DeleteProfileRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && !_rowDeleteConfirms.TryGetValue(button, out _))
            _rowDeleteConfirms.Add(button, ConfirmButton.Attach(button, "确认删除？", DeleteSelectedProfile));
    }

    private void DeleteProfileRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ProfilesRow row }) ProfilesListBox.SelectedItem = row;
    }

    private async void DeleteSelectedProfile()
    {
        DisarmAllConfirms();
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

            // 1. Persist config to disk first asynchronously
            await ProfileManager.SaveAsync(config);

            // 2. Delete credential after config is persisted
            if (!string.IsNullOrWhiteSpace(profile.CredentialTarget))
            {
                try
                {
                    CredentialStore.DeleteApiKey(profile.CredentialTarget);
                }
                catch
                {
                    // Vault delete failure shouldn't crash or corrupt profile state
                }
            }

            // 3. Clear or update running core configuration when the default service changed or emptied
            if (isTextDefault)
            {
                await ProfileManager.ApplyActiveToCoreAsync(config);
            }

            _editingProfileId = null;
            _isAdding = false;
            _testOutcomes.Remove(profile.Id);
            _testLatencies.Remove(profile.Id);
            _editorDirty = false;
            EditorForm.Visibility = Visibility.Collapsed;
            EditorEmpty.Visibility = Visibility.Visible;
            EditorOpenStateChanged?.Invoke();
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            if (config.Profiles.Count == 0)
            {
                StatusChanged?.Invoke("引擎已删除。未配置翻译引擎时，翻译将使用已授权的内置免费引擎。", StatusTone.Info);
            }
            else
            {
                StatusChanged?.Invoke(
                    $"引擎已删除，默认引擎切换为「{config.TryGetActiveProfile()?.Name ?? "（无）"}」。", StatusTone.Info);
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"删除引擎失败：{exception.Message}", StatusTone.Error);
        }
    }

    private async void SaveService_Click(object sender, RoutedEventArgs e) => await TrySaveServiceAsync();

    /// <summary>
    /// Validates and persists the editor draft asynchronously. Returns false when nothing
    /// was saved (validation failed or the credential write failed), leaving
    /// the draft and the saved state untouched.
    /// </summary>
    internal async Task<bool> TrySaveServiceAsync()
    {
        if (CliConnectionsPanel.Visibility == Visibility.Visible)
            return ChatGptConnectionPanel.Visibility == Visibility.Visible
                ? await SaveDirectConnectionAsync(use: false)
                : await SaveCliConnectionAsync(use: false);
        if (_saveInProgress) return false;
        _saveInProgress = true;
        EditorForm.IsEnabled = false;
        BackToListHeaderButton.IsEnabled = ChooseAnotherProviderButton.IsEnabled = false;
        var originalEditingId = _editingProfileId;
        try
        {
            var name = ServiceNameTextBox.Text.Trim();
            // Pure draft validation lives in the coordinator; the save
            // orchestration (credential order, rollback) stays with the view.
            var validationError = ServiceDraftCoordinator.Validate(name, BaseUrlTextBox.Text);
            if (validationError is not null)
            {
                throw new InvalidOperationException(validationError);
            }
            ValidateGenerationForm();
            var draft = BuildProfileFromForm(name);
            var config = ProfileManager.Load();
            var pendingOutcome = _pendingTestOutcome;
            var pendingTestedAtUtc = _pendingTestedAtUtc;
            var pendingFingerprint = _pendingTestFingerprint;

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
                throw new InvalidOperationException("无法读取已保存的密钥。请检查 Windows 凭据库后重试；未替换原密钥。");
            }

            var typedKey = ApiKeyPasswordBox.Password?.Trim();
            if (ProfileVerification.IsMaskNotAKey(typedKey))
                throw new InvalidOperationException("占位圆点不是 API Key。请粘贴真实密钥，或留空保留已有密钥。");
            var keyWritten = false;
            if (!string.IsNullOrEmpty(typedKey))
            {
                CredentialStore.SaveApiKey(typedKey, credentialTarget);
                keyWritten = true;
                // Clear only after the profile commit succeeds; a failed save keeps the draft key.
            }

            draft.Id = profileId;
            draft.CredentialTarget = credentialTarget;
            var isFirstService = config.Profiles.Count == 0;
            var existingIndex = config.Profiles.FindIndex(p => p.Id == profileId);
            var existingProfile = existingIndex >= 0 ? config.Profiles[existingIndex] : null;
            draft.CredentialRevision = (existingProfile?.CredentialRevision ?? 0) + (keyWritten ? 1 : 0);

            var credentialAvailable = ProviderSettings.IsLocalBaseUrl(draft.ApiBaseUrl) || keyWritten || hadPreviousKey;
            VerificationLease? pendingLease = null;
            if (pendingFingerprint is not null && pendingTestedAtUtc is not null)
            {
                pendingLease = new VerificationLease(
                    profileId,
                    ProfileVerification.ConnectionVersion(draft),
                    draft.CredentialRevision,
                    pendingFingerprint);
            }

            var evidence = ProfileVerification.Decide(
                pendingLease,
                pendingOutcome,
                pendingTestedAtUtc,
                existingProfile,
                draft,
                keyWritten,
                credentialAvailable);
            draft.LastTestOutcome = evidence.Outcome;
            draft.LastTestedAtUtc = evidence.TestedAtUtc;
            draft.LastTestFingerprint = evidence.Fingerprint;
            if (evidence.Outcome is null)
            {
                _testOutcomes.Remove(profileId);
                _testLatencies.Remove(profileId);
            }
            else if (evidence.Outcome == "ok" && _pendingTestElapsedMs is { } pendingElapsedMs)
            {
                _testLatencies[profileId] = pendingElapsedMs;
            }

            // The manager merges this draft with the latest routes inside its lock.
            var canActivateForText = draft.SupportsText && !string.IsNullOrWhiteSpace(draft.TextModel);

            try
            {
                config = await Task.Run(() => ProfileManager.SaveApiProfile(draft, originalEditingId));
            }
            catch
            {
                // Roll back the credential so vault and config never disagree.
                if (keyWritten)
                {
                    TryRestoreCredential(credentialTarget, previousKey, hadPreviousKey);
                }
                _pendingTestOutcome = null;
                _pendingTestedAtUtc = null;
                _pendingTestFingerprint = null;
                _testOutcomes.Remove(profileId);
                SetTestResult(StatusTone.Warning, "保存失败", "密钥已恢复，之前的验证标记没有保留。");
                _editingProfileId = originalEditingId;
                throw;
            }

            _editingProfileId = profileId;
            var wasLoading = _loading;
            _loading = true;
            try { ApiKeyPasswordBox.Clear(); }
            finally { _loading = wasLoading; }
            try
            {
                await ProfileManager.ApplyLatestToCoreAsync();
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
                    $"引擎「{draft.Name}」已保存到本机配置，但应用到运行中的引擎失败（{applyException.Message}）。重启 PopGlot 后生效。",
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
            if (!config.PreferFreeEngine && config.ActiveProfileId == draft.Id)
            {
                StatusChanged?.Invoke($"引擎「{draft.Name}」已保存并作为默认文字引擎生效。", StatusTone.Success);
            }
            else if (isFirstService && !canActivateForText)
            {
                StatusChanged?.Invoke("已保存；补全模型后才能使用。", StatusTone.Warning);
            }
            else
            {
                StatusChanged?.Invoke($"引擎「{draft.Name}」已保存。用「设为文字默认」启用它。", StatusTone.Success);
            }
            return true;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"保存引擎失败：{exception.Message}", StatusTone.Error);
            return false;
        }
        finally
        {
            _saveInProgress = false;
            EditorForm.IsEnabled = true;
            BackToListHeaderButton.IsEnabled = ChooseAnotherProviderButton.IsEnabled = true;
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
        // Thin view adapter: read the controls, hand plain values to the
        // coordinator, which owns validation and construction.
        var textModel = TextModelCombo.Text.Trim();
        var inputs = new ServiceDraftInputs(
            Name: name,
            BaseUrl: BaseUrlTextBox.Text.Trim(),
            ProviderType: Helpers.SelectedEnum(ProviderTypeComboBox, ProviderType.OpenAiCompatible),
            TextEndpoint: TextEndpointTextBox.Text.Trim(),
            VisionEndpoint: VisionEndpointTextBox.Text.Trim(),
            TextModel: textModel,
            VisionModel: UseTextModelForVisionCheckBox.IsChecked == true
                ? textModel
                : VisionModelCombo.Text.Trim(),
            ExtraHeaders: ParseExtraHeaders(ExtraHeadersTextBox.Text),
            AnthropicVersion: AnthropicVersionTextBox.Text,
            AllowInsecureTls: AllowInsecureTlsCheckBox.IsChecked == true,
            ReasoningLevel: ServiceDraftCoordinator.NormalizeReasoningLevel(
                (ReasoningLevelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
            ContextTokens: ParseTokenSetting(ContextTokensTextBox.Text),
            OutputTokens: ParseTokenSetting(OutputTokensTextBox.Text));
        return ServiceDraftCoordinator.BuildDraft(inputs);
    }

    private void ValidateGenerationForm()
    {
        var error = ValidateGenerationSettings(ContextTokensTextBox.Text, OutputTokensTextBox.Text);
        if (error is not null) throw new InvalidOperationException(error);
        _ = CoreBridge.PlanSessionBudget(CoreBridge.GetSettings() with
        {
            ContextTokens = ParseTokenSetting(ContextTokensTextBox.Text),
            OutputTokens = ParseTokenSetting(OutputTokensTextBox.Text),
            ReasoningLevel = ServiceDraftCoordinator.NormalizeReasoningLevel(
                (ReasoningLevelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
        });
    }

    internal static string? ValidateGenerationSettings(string? context, string? output)
    {
        static bool Valid(string? text, uint min, uint max) => string.IsNullOrWhiteSpace(text)
            || uint.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                && (value == 0 || value >= min && value <= max);
        if (!Valid(context, 4096, 262144))
            return "上下文窗口请输入 4096–262144 的整数，留空或 0 使用自动预算。";
        if (!Valid(output, 512, 65536))
            return "输出上限请输入 512–65536 的整数，留空或 0 使用自适应预算。";
        var contextValue = ParseTokenSetting(context);
        var outputValue = ParseTokenSetting(output);
        if (outputValue > 0 && outputValue + 2048 >= (contextValue == 0 ? 32768u : contextValue))
            return "输出上限与提示词不能占满上下文窗口，请降低输出上限或增大上下文窗口。";
        return null;
    }

    /// <summary>0 显示为空（自动/自适应）；非法输入按 0 处理，绝不上传脏值。</summary>
    internal static uint ParseTokenSetting(string? text) =>
        uint.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string FormatTokenSetting(uint value) =>
        value == 0 ? string.Empty : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ================= Capability pills, free route hub, quick proxy & auto-discovery =================

    private static IReadOnlyList<string> BuildCapabilityBadges(ProviderProfile profile)
    {
        var badges = new List<string>(4);
        if (profile.SupportsText || !string.IsNullOrWhiteSpace(profile.TextModel))
        {
            badges.Add("文本");
        }
        if (profile.SupportsVision || !string.IsNullOrWhiteSpace(profile.VisionModel))
        {
            badges.Add("视觉");
        }
        if (profile.IsLocal || ProviderSettings.IsLocalBaseUrl(profile.ApiBaseUrl))
        {
            badges.Add("本地");
        }
        if (!profile.ExecutionFacts.IsCliExecution &&
            (profile.SupportsText || !string.IsNullOrWhiteSpace(profile.TextModel)))
        {
            badges.Add("流式");
        }
        return badges;
    }

    internal void RefreshFreeRoutesPanel()
    {
        if (FreeRoutesList is null)
        {
            return;
        }

        var shell = ShellSettings.Default;
        try
        {
            shell = ShellSettingsStore.Load();
        }
        catch (Exception)
        {
        }

        ProviderSettings? core = null;
        try
        {
            core = CoreBridge.GetSettings();
        }
        catch (Exception)
        {
        }

        CoreProductConfig? config = null;
        try
        {
            config = ProfileManager.Load();
        }
        catch (Exception)
        {
        }

        var hasActiveUserEngine = config is not null && !config.PreferFreeEngine && config.TryGetActiveProfile() is not null;
        var consentAllowed = shell.FreeEngineConsent != FreeEngineConsent.Denied;
        var onlineAllowed = core is null || (!core.SafeDevMode && core.NetworkEnabled);

        if (FreeRoutesConsentText is not null && FreeRoutesConsentBadge is not null)
        {
            if (!onlineAllowed)
            {
                FreeRoutesConsentText.Text = core?.SafeDevMode == true ? "安全离线模式" : "联网已关闭";
                FreeRoutesConsentText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                if (EnableFreeRoutesInlineButton is not null)
                {
                    EnableFreeRoutesInlineButton.Visibility = Visibility.Collapsed;
                }
            }
            else if (!consentAllowed)
            {
                FreeRoutesConsentText.Text = "已关闭";
                FreeRoutesConsentText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                if (EnableFreeRoutesInlineButton is not null)
                {
                    EnableFreeRoutesInlineButton.Visibility = Visibility.Visible;
                }
            }
            else
            {
                FreeRoutesConsentText.Text = hasActiveUserEngine ? "备用启用" : "当前启用";
                FreeRoutesConsentText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
                if (EnableFreeRoutesInlineButton is not null)
                {
                    EnableFreeRoutesInlineButton.Visibility = Visibility.Collapsed;
                }
            }
        }

        var specs = new (FreeEngineProvider Provider, string Name, string Desc, string[] Tags)[]
        {
            (FreeEngineProvider.Auto, "自动优选", "短词走有道，长句走 Bing / Google（每次只发给单条线路）", ["按文本择优", "免 Key"]),
            (FreeEngineProvider.Bing, "Bing 翻译", "cn.bing.com · 国内直连，适合长句与段落", ["国内直连", "长句"]),
            (FreeEngineProvider.Youdao, "有道词典", "dict.youdao.com · 单词与短语释义准确", ["国内直连", "词典级"]),
            (FreeEngineProvider.MyMemory, "MyMemory", "api.mymemory.translated.net · 单次不超过 500 字节", ["500字节限制", "直连"]),
            (FreeEngineProvider.Google, "Google 翻译", "translate.googleapis.com · 多语种长句支持好", ["需代理", "长句"]),
        };

        var rows = new List<FreeRouteRow>(specs.Length);
        var googleFailed = false;
        foreach (var spec in specs)
        {
            var isSelected = shell.FreeEngineProvider == spec.Provider;
            var hasHealth = _freeRouteProbes.TryGetValue(spec.Provider, out var health) ||
                            FreeTranslateService.TryGetValidHealth(spec.Provider, out health);

            string latencyText;
            Brush stateBrush;
            Brush latencyBrush;
            if (!hasHealth)
            {
                latencyText = "未测速";
                stateBrush = ToneBrush(isSelected ? StatusTone.Info : StatusTone.Info);
                latencyBrush = ToneTextBrush(StatusTone.Info);
            }
            else if (health.Ok)
            {
                latencyText = TranslationElapsedText.ForMilliseconds(health.LatencyMs);
                stateBrush = ToneBrush(StatusTone.Success);
                latencyBrush = ToneTextBrush(StatusTone.Success);
            }
            else
            {
                latencyText = ClassifyFreeHealthFailure(health.Error);
                stateBrush = ToneBrush(StatusTone.Error);
                latencyBrush = ToneTextBrush(StatusTone.Error);
                if (spec.Provider == FreeEngineProvider.Google)
                {
                    googleFailed = true;
                }
            }

            rows.Add(new FreeRouteRow(
                Provider: spec.Provider,
                Name: spec.Name,
                Description: spec.Desc,
                Tags: spec.Tags,
                LatencyText: latencyText,
                StateBrush: stateBrush,
                LatencyBrush: latencyBrush,
                ActiveBadgeVisibility: isSelected ? Visibility.Visible : Visibility.Collapsed,
                SelectVisibility: isSelected ? Visibility.Collapsed : Visibility.Visible,
                IsActive: isSelected,
                ProbeButtonName: $"测速 {spec.Name}",
                SelectButtonName: $"设为当前 {spec.Name}"));
        }

        FreeRoutesList.ItemsSource = rows;
        if (GoogleProxyHintBanner is not null)
        {
            GoogleProxyHintBanner.Visibility = googleFailed ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string ClassifyFreeHealthFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "不可用";
        }
        if (error.Contains("超时", StringComparison.Ordinal) ||
            error.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "超时";
        }
        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("拦截", StringComparison.Ordinal) ||
            error.Contains("拒绝", StringComparison.Ordinal) ||
            error.Contains("HTML", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("block", StringComparison.OrdinalIgnoreCase))
        {
            return "被阻断";
        }
        return "不可用";
    }

    private void EnableFreeRoutesInline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShellSettingsStore.Update(current => current with { FreeEngineConsent = FreeEngineConsent.Allowed });
            RefreshFreeRoutesPanel();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke("已开启内置免费线路。", StatusTone.Success);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"开启免费线路失败：{ex.Message}", StatusTone.Error);
        }
    }

    private void SelectFreeRoute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is FreeEngineProvider provider)
        {
            SwitchFreeRoute(provider);
        }
    }

    private void QuickSwitchBing_Click(object sender, RoutedEventArgs e) =>
        SwitchFreeRoute(FreeEngineProvider.Bing);

    private void QuickSwitchYoudao_Click(object sender, RoutedEventArgs e) =>
        SwitchFreeRoute(FreeEngineProvider.Youdao);

    private void OpenQuickProxyFromBanner_Click(object sender, RoutedEventArgs e)
    {
        if (QuickProxyPanel is not null)
        {
            QuickProxyPanel.Visibility = Visibility.Visible;
            RefreshProxyStatusPill();
        }
    }

    private void SwitchFreeRoute(FreeEngineProvider provider)
    {
        try
        {
            if (!ProfileManager.TrySwitchToFreeEngine(provider, out var error))
            {
                StatusChanged?.Invoke(error, StatusTone.Warning);
                return;
            }
            RefreshFreeRoutesPanel();
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            StatusChanged?.Invoke($"已切换到免费线路：{EngineWording.DisplayNameFor(provider)}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"切换免费线路失败：{ex.Message}", StatusTone.Error);
        }
    }

    private async void ProbeFreeRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_freeProbeInProgress || (sender as Button)?.Tag is not FreeEngineProvider provider)
        {
            return;
        }
        await RunFreeRoutesProbeAsync([provider]);
    }

    private async void TestAllFreeRoutes_Click(object sender, RoutedEventArgs e)
    {
        if (_freeProbeInProgress)
        {
            return;
        }
        await RunFreeRoutesProbeAsync(
        [
            FreeEngineProvider.Bing,
            FreeEngineProvider.Youdao,
            FreeEngineProvider.MyMemory,
            FreeEngineProvider.Google,
        ]);
    }

    private async Task RunFreeRoutesProbeAsync(FreeEngineProvider[] providers)
    {
        FreeEngineAuthorization? authorization = null;
        try
        {
            var settings = CoreBridge.GetSettings();
            if (!OutboundPolicy.AllowsFreeEngine(settings, out var denial, out authorization))
            {
                StatusChanged?.Invoke(denial?.Message ?? "当前策略不允许测速内置免费线路。", StatusTone.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"无法读取网络策略：{ex.Message}", StatusTone.Error);
            return;
        }

        _freeProbeInProgress = true;
        if (TestAllFreeRoutesButton is not null)
        {
            TestAllFreeRoutesButton.IsEnabled = false;
            TestAllFreeRoutesButton.Content = "测速中…";
        }
        try
        {
            StatusChanged?.Invoke("正在测试内置免费线路延迟…", StatusTone.Info);
            var tasks = providers.Select(async provider =>
            {
                var health = await FreeRouteProbeExecutor(provider, true, authorization);
                return (provider, health);
            });
            var results = await Task.WhenAll(tasks);
            foreach (var (provider, health) in results)
            {
                _freeRouteProbes[provider] = health;
            }

            if (providers.Length > 1)
            {
                var best = results.Where(r => r.health.Ok).OrderBy(r => r.health.LatencyMs).FirstOrDefault();
                if (best.health.Ok)
                {
                    _freeRouteProbes[FreeEngineProvider.Auto] = best.health;
                }
            }

            RefreshFreeRoutesPanel();
            var okCount = results.Count(r => r.health.Ok);
            StatusChanged?.Invoke(
                okCount > 0
                    ? $"免费线路测速完成：{okCount}/{results.Length} 条可用。"
                    : "免费线路测速未通过，可切换国内直连线路或检查代理设置。",
                okCount > 0 ? StatusTone.Success : StatusTone.Warning);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"免费线路测速失败：{ex.Message}", StatusTone.Error);
        }
        finally
        {
            _freeProbeInProgress = false;
            if (TestAllFreeRoutesButton is not null)
            {
                TestAllFreeRoutesButton.IsEnabled = true;
                TestAllFreeRoutesButton.Content = "一键测速";
            }
        }
    }

    // ================= Quick proxy pill & bar =================

    internal void RefreshProxyStatusPill()
    {
        if (ProxyStatusLabel is null)
        {
            return;
        }

        var shell = ShellSettings.Default;
        try
        {
            shell = ShellSettingsStore.Load();
        }
        catch (Exception)
        {
        }

        var (label, tone) = shell.NetworkProxyMode switch
        {
            NetworkProxyMode.Direct => ("代理：直连", StatusTone.Info),
            NetworkProxyMode.Custom when ProxyHttpClientFactory.TryParseProxyUri(shell.CustomProxyUrl, out var uri) && uri is not null =>
                ($"代理：{uri.Authority}", StatusTone.Success),
            NetworkProxyMode.Custom => ("代理：自定义（待填写）", StatusTone.Warning),
            _ => ("代理：跟随系统", StatusTone.Success),
        };
        ProxyStatusLabel.Text = label;
        ProxyStatusDot?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, tone switch
        {
            StatusTone.Warning => "WarningBrush",
            StatusTone.Success => "SuccessBrush",
            _ => "TextSecondaryBrush",
        });

        if (QuickProxySystemRadio is null)
        {
            return;
        }
        _loadingQuickProxy = true;
        try
        {
            QuickProxySystemRadio.IsChecked = shell.NetworkProxyMode == NetworkProxyMode.System;
            QuickProxyDirectRadio.IsChecked = shell.NetworkProxyMode == NetworkProxyMode.Direct;
            QuickProxyCustomRadio.IsChecked = shell.NetworkProxyMode == NetworkProxyMode.Custom;
            QuickProxyUrlTextBox.Text = shell.CustomProxyUrl ?? string.Empty;
            QuickProxyCustomRow.Visibility = shell.NetworkProxyMode == NetworkProxyMode.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _loadingQuickProxy = false;
        }
    }

    private void ProxyStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (QuickProxyPanel is null)
        {
            return;
        }
        QuickProxyPanel.Visibility = QuickProxyPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (QuickProxyPanel.Visibility == Visibility.Visible)
        {
            RefreshProxyStatusPill();
        }
    }

    private void OpenGeneralProxySettings_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is SettingsWindow settingsWindow)
        {
            settingsWindow.ShowPage("General");
        }
        else if (QuickProxyPanel is not null)
        {
            QuickProxyPanel.Visibility = Visibility.Visible;
        }
    }

    private void QuickProxyMode_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingQuickProxy)
        {
            return;
        }

        var mode = QuickProxyDirectRadio.IsChecked == true
            ? NetworkProxyMode.Direct
            : QuickProxyCustomRadio.IsChecked == true
                ? NetworkProxyMode.Custom
                : NetworkProxyMode.System;

        QuickProxyCustomRow.Visibility = mode == NetworkProxyMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (mode == NetworkProxyMode.Custom)
        {
            if (App.TryValidateCustomProxyUrl(QuickProxyUrlTextBox.Text, out var parsed) && parsed is not null)
            {
                ApplyQuickProxy(NetworkProxyMode.Custom, parsed);
            }
            else
            {
                QuickProxyValidationText.Text = "请输入代理地址，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:7891";
                QuickProxyValidationText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                QuickProxyValidationText.Visibility = Visibility.Visible;
                QuickProxyUrlTextBox.Focus();
            }
            return;
        }

        QuickProxyValidationText.Visibility = Visibility.Collapsed;
        ApplyQuickProxy(mode, null);
    }

    private void QuickProxyApply_Click(object sender, RoutedEventArgs e)
    {
        if (!App.TryValidateCustomProxyUrl(QuickProxyUrlTextBox.Text, out var normalized) || normalized is null)
        {
            QuickProxyValidationText.Text = "代理地址格式无效，请使用 http://、https://、socks5:// 或 socks5h:// 并包含端口。";
            QuickProxyValidationText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            QuickProxyValidationText.Visibility = Visibility.Visible;
            return;
        }

        QuickProxyUrlTextBox.Text = normalized;
        QuickProxyValidationText.Visibility = Visibility.Collapsed;
        ApplyQuickProxy(NetworkProxyMode.Custom, normalized);
    }

    private void ApplyQuickProxy(NetworkProxyMode mode, string? customUrl)
    {
        try
        {
            var updated = ShellSettingsStore.Update(current => current with
            {
                NetworkProxyMode = mode,
                CustomProxyUrl = customUrl ?? current.CustomProxyUrl,
            });
            App.SyncNetworkProxyToCore(updated);
            RefreshProxyStatusPill();
            StatusChanged?.Invoke($"代理设置已更新：{ProxyStatusLabel.Text}", StatusTone.Success);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"更新代理设置失败：{ex.Message}", StatusTone.Error);
        }
    }

    // ================= Local & env 1-click discovery =================

    private async void ScanLocalAndEnv_Click(object sender, RoutedEventArgs e)
    {
        if (_discoveryInProgress)
        {
            return;
        }
        _discoveryInProgress = true;
        if (ScanLocalAndEnvButton is not null)
        {
            ScanLocalAndEnvButton.IsEnabled = false;
            ScanLocalAndEnvButton.Content = "扫描中…";
        }
        DiscoveryStatusText.Text = "正在扫描本机模型服务与环境变量密钥…";
        DiscoveryStatusText.Visibility = Visibility.Visible;
        try
        {
            var result = await DiscoverFreeSourcesExecutor(CancellationToken.None);
            var rows = new List<DiscoveredSourceRow>();

            foreach (var local in result.LocalRuntimes.Where(r => r.IsReachable))
            {
                var detail = local.DiscoveredModels.Count > 0
                    ? $"发现 {local.DiscoveredModels.Count} 个模型：{string.Join("、", local.DiscoveredModels.Take(3))}"
                    : local.StatusSummary;
                rows.Add(new DiscoveredSourceRow(
                    Id: local.Id,
                    DisplayName: local.DisplayName,
                    BaseUrl: local.BaseUrl,
                    ProviderType: local.ProviderType,
                    TextEndpoint: local.TextEndpoint,
                    IsLocalRuntime: true,
                    HasEnvKey: false,
                    EnvVarName: null,
                    KindBadge: "本地运行",
                    DetailText: detail,
                    DefaultModel: local.DiscoveredModels.FirstOrDefault() ?? string.Empty,
                    DiscoveredModels: local.DiscoveredModels,
                    AddButtonName: $"一键添加 {local.DisplayName}"));
            }

            foreach (var env in result.EnvConfiguredSources)
            {
                var curatedDefault = ModelCatalogService.CuratedFreePresets
                    .FirstOrDefault(p => string.Equals(p.Id, env.Id, StringComparison.OrdinalIgnoreCase))
                    ?.DefaultModel;
                var fallbackModel = curatedDefault ?? DefaultModelForDiscoveredSource(env.Id);
                rows.Add(new DiscoveredSourceRow(
                    Id: env.Id,
                    DisplayName: env.DisplayName,
                    BaseUrl: env.BaseUrl,
                    ProviderType: env.ProviderType,
                    TextEndpoint: env.TextEndpoint,
                    IsLocalRuntime: false,
                    HasEnvKey: env.HasEnvKey,
                    EnvVarName: env.EnvVarName,
                    KindBadge: env.EnvVarName is not null ? "环境变量" : "本机 CLI",
                    DetailText: env.StatusSummary,
                    DefaultModel: fallbackModel,
                    DiscoveredModels: env.DiscoveredModels,
                    AddButtonName: $"一键添加 {env.DisplayName}"));
            }

            if (rows.Count == 0)
            {
                DiscoveredSourcesList.ItemsSource = null;
                DiscoveredSourcesList.Visibility = Visibility.Collapsed;
                DiscoveryStatusText.Text = "未发现运行中的本地模型（Ollama / LM Studio / LiteLLM）或环境变量密钥；可点击右上角「添加引擎」手动选择预设。";
                StatusChanged?.Invoke("扫描完成，暂未发现可自动添加的本地服务或环境变量密钥。", StatusTone.Info);
            }
            else
            {
                DiscoveredSourcesList.ItemsSource = rows;
                DiscoveredSourcesList.Visibility = Visibility.Visible;
                DiscoveryStatusText.Text = $"发现 {rows.Count} 个可用来源，点击右侧「一键添加」即可配置并测试。";
                StatusChanged?.Invoke($"扫描完成，发现 {rows.Count} 个可添加来源。", StatusTone.Success);
            }
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"扫描未完成：{ex.Message}";
            StatusChanged?.Invoke($"扫描失败：{ex.Message}", StatusTone.Error);
        }
        finally
        {
            _discoveryInProgress = false;
            if (ScanLocalAndEnvButton is not null)
            {
                ScanLocalAndEnvButton.IsEnabled = true;
                ScanLocalAndEnvButton.Content = "扫描本机与环境";
            }
        }
    }

    private async void QuickAddDiscoveredSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DiscoveredSourceRow row)
        {
            return;
        }
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            string? apiKey = null;
            if (!row.IsLocalRuntime)
            {
                if (row.EnvVarName is not null)
                {
                    apiKey = EnvKeyReader(row.Id);
                }
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    if (TryMapOmpProviderForImport(row.Id) is { } ompProvider && !string.IsNullOrWhiteSpace(row.DefaultModel))
                    {
                        var imported = await Task.Run(() => OmpApiKeyImporter.Import(ompProvider, row.DefaultModel, confirmed: true));
                        await ProfileManager.ApplyLatestToCoreAsync();
                        RefreshProfilesList();
                        ProfileChanged?.Invoke();
                        StatusChanged?.Invoke($"已从 OMP 导入并启用「{imported.Name}」。", StatusTone.Success);
                        return;
                    }
                    StatusChanged?.Invoke($"未读取到 {row.DisplayName} 的密钥，请通过「添加引擎」手动填写。", StatusTone.Warning);
                    return;
                }
            }

            var modelId = row.DiscoveredModels.FirstOrDefault() ?? row.DefaultModel;
            var coreSettings = CoreBridge.GetSettings();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                try
                {
                    var probeSettings = coreSettings with
                    {
                        ProviderType = row.ProviderType,
                        ApiBaseUrl = row.BaseUrl,
                        TextEndpoint = row.TextEndpoint,
                        ApiKeyConfigured = !string.IsNullOrWhiteSpace(apiKey),
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var catalog = await ModelCatalogExecutor(probeSettings, apiKey ?? string.Empty, cts.Token);
                    modelId = catalog.Models.FirstOrDefault()?.Id ?? string.Empty;
                }
                catch (Exception)
                {
                    // Fall back to curated/empty model if catalog endpoint does not respond.
                }
            }

            var config = ProfileManager.Load();
            var (profileId, credentialTarget) = ProfileManager.ResolveSaveTarget(config, null);
            var keyWritten = false;
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                CredentialStore.SaveApiKey(apiKey, credentialTarget);
                keyWritten = true;
            }

            var profile = ServiceDraftCoordinator.BuildDraft(new ServiceDraftInputs(
                Name: row.DisplayName,
                BaseUrl: row.BaseUrl,
                ProviderType: row.ProviderType,
                TextEndpoint: row.TextEndpoint,
                VisionEndpoint: row.TextEndpoint,
                TextModel: modelId,
                VisionModel: string.Empty,
                ExtraHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                AnthropicVersion: "2023-06-01",
                AllowInsecureTls: false));
            profile.Id = profileId;
            profile.CredentialTarget = credentialTarget;
            profile.CredentialRevision = keyWritten ? 1 : 0;

            long? verifiedMs = null;
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var testSettings = profile.ToProviderSettings(coreSettings) with
                {
                    ApiKeyConfigured = row.IsLocalRuntime || keyWritten,
                };
                var watch = Stopwatch.StartNew();
                try
                {
                    _ = await ConnectionTestExecutor(testSettings, string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey);
                    watch.Stop();
                    verifiedMs = watch.ElapsedMilliseconds;
                    profile.LastTestOutcome = "ok";
                    profile.LastTestedAtUtc = DateTime.UtcNow;
                    profile.LastTestFingerprint = CreateProfileFingerprint(profile, row.IsLocalRuntime || keyWritten);
                    _testOutcomes[profileId] = "ok";
                    _testLatencies[profileId] = verifiedMs.Value;
                }
                catch (Exception testEx)
                {
                    watch.Stop();
                    var outcome = ClassifyTestFailure(testEx);
                    profile.LastTestOutcome = outcome;
                    profile.LastTestedAtUtc = DateTime.UtcNow;
                    profile.LastTestFingerprint = CreateProfileFingerprint(profile, row.IsLocalRuntime || keyWritten);
                    _testOutcomes[profileId] = outcome;
                }
            }

            await Task.Run(() => ProfileManager.SaveApiProfile(profile, editingId: null));
            await ProfileManager.ApplyLatestToCoreAsync();
            RefreshProfilesList();
            ProfileChanged?.Invoke();

            var latencySuffix = verifiedMs is { } ms ? $" · 验证通过（{TranslationElapsedText.ForMilliseconds(ms)}）" : string.Empty;
            StatusChanged?.Invoke($"已添加「{profile.Name}」{latencySuffix}。", StatusTone.Success);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"一键添加失败：{ex.Message}", StatusTone.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static string DefaultModelForDiscoveredSource(string sourceId) => sourceId switch
    {
        "deepseek" => "deepseek-chat",
        "openai" => "gpt-4o-mini",
        "gemini" => "gemini-2.5-flash",
        "siliconflow" => "Qwen/Qwen2.5-7B-Instruct",
        "groq" => "llama-3.1-8b-instant",
        "openrouter" => "meta-llama/llama-3.2-3b-instruct:free",
        "zhipu" => "glm-4-flash",
        _ => string.Empty,
    };

    private static string? TryMapOmpProviderForImport(string sourceId) => sourceId switch
    {
        "openai" => "openai",
        "deepseek" => "deepseek",
        "zhipu" => "zhipu-coding-plan",
        _ => null,
    };
}
