using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PopGlot.Windows.Sections;

/// <summary>Shared helpers used by multiple sections.</summary>
internal static class Helpers
{
    internal static string SelectedLanguage(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as LanguageOption)?.Tag ?? fallback;

    internal static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem candidate &&
                string.Equals(candidate.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = candidate;
                return;
            }
        }
        comboBox.SelectedIndex = 0;
    }

    internal static T SelectedEnum<T>(ComboBox comboBox, T fallback) where T : struct, Enum =>
        comboBox.SelectedItem is ComboBoxItem selected &&
        Enum.TryParse<T>(selected.Tag?.ToString(), out var value)
            ? value
            : fallback;

    /// <summary>
    /// Test seam: replaces the system clipboard writer so UI copy actions can
    /// be verified without touching the real clipboard. Production leaves it
    /// null and every copy goes through the hardened STA worker.
    /// </summary>
    internal static Func<string?, Task<bool>>? ClipboardWriterOverride { get; set; }

    internal static Task<bool> CopyToClipboardAsync(string? text) =>
        ClipboardWriterOverride is { } writer
            ? writer(text)
            : WindowsSelectionClipboardAdapter.TryWriteTextAsync(text);
}

/// <summary>Status tone for footer messages, shared across sections.</summary>
internal enum StatusTone
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Two-step destructive confirmation that stays inside the window: the first
/// click arms the button with a danger caption, the second click within a few
/// seconds fires. Replaces system MessageBox confirms for delete/wipe flows.
/// </summary>
internal sealed class ConfirmButton
{
    private static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(3);
    private static ConfirmButton? _currentlyArmed;

    private readonly Button _button;
    private readonly Func<string> _armedContentProvider;
    private readonly Action _fire;
    private readonly DispatcherTimer _timer;
    private bool _armed;

    private PropertySnapshot? _widthSnapshot;
    private PropertySnapshot? _minWidthSnapshot;
    private PropertySnapshot? _paddingSnapshot;
    private PropertySnapshot? _contentSnapshot;
    private PropertySnapshot? _automationNameSnapshot;
    private PropertySnapshot? _backgroundSnapshot;
    private PropertySnapshot? _foregroundSnapshot;

    internal bool IsArmed => _armed;

    private ConfirmButton(Button button, Func<string> armedContentProvider, Action fire)
    {
        _button = button;
        _armedContentProvider = armedContentProvider;
        _fire = fire;
        _timer = new DispatcherTimer { Interval = ArmTimeout };
        _timer.Tick += (_, _) => Disarm();
        button.Click += OnClick;
        button.Unloaded += (_, _) => Disarm();
        button.PreviewKeyDown += OnPreviewKeyDown;
    }

    internal static ConfirmButton Attach(Button button, string armedContent, Action fire) =>
        new(button, () => armedContent, fire);

    internal static ConfirmButton Attach(Button button, Func<string> armedContentProvider, Action fire) =>
        new(button, armedContentProvider, fire);

    internal static void DisarmCurrent()
    {
        _currentlyArmed?.Disarm();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_armed && e.Key == Key.Escape)
        {
            Disarm();
            e.Handled = true;
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (!_armed)
        {
            if (_currentlyArmed != null && _currentlyArmed != this)
            {
                _currentlyArmed.Disarm();
            }
            _currentlyArmed = this;
            _armed = true;

            var armedText = _armedContentProvider();

            // Snapshot and adjust geometry only as needed.
            // Icon buttons carry a fixed style width (32/36 DIP): an armed caption
            // such as "确认删除？" does not fit and clips. Let the button grow to
            // the caption for the armed window, then restore the original metrics.
            _widthSnapshot = PropertySnapshot.Capture(_button, FrameworkElement.WidthProperty);
            if (!double.IsNaN(_button.ActualWidth) && _button.ActualWidth > 0)
            {
                _minWidthSnapshot = PropertySnapshot.Capture(_button, FrameworkElement.MinWidthProperty);
                _button.MinWidth = _button.ActualWidth;
            }
            else
            {
                _minWidthSnapshot = null;
            }
            _button.Width = double.NaN;

            if (_button.Padding.Left < 8 && _button.Padding.Right < 8)
            {
                _paddingSnapshot = PropertySnapshot.Capture(_button, Control.PaddingProperty);
                _button.Padding = new Thickness(10, 0, 10, 0);
            }
            else
            {
                _paddingSnapshot = null;
            }

            _contentSnapshot = PropertySnapshot.Capture(_button, ContentControl.ContentProperty);
            _button.Content = armedText;

            _automationNameSnapshot = PropertySnapshot.Capture(_button, System.Windows.Automation.AutomationProperties.NameProperty);
            System.Windows.Automation.AutomationProperties.SetName(_button, armedText);

            _backgroundSnapshot = PropertySnapshot.Capture(_button, Control.BackgroundProperty);
            _foregroundSnapshot = PropertySnapshot.Capture(_button, Control.ForegroundProperty);
            _button.SetResourceReference(Control.BackgroundProperty, "DangerSoftBrush");
            _button.SetResourceReference(Control.ForegroundProperty, "DangerBrush");

            _timer.Start();
            return;
        }
        Disarm();
        _fire();
    }

    internal void Disarm()
    {
        _timer.Stop();
        if (!_armed)
        {
            return;
        }
        _armed = false;
        if (_currentlyArmed == this)
        {
            _currentlyArmed = null;
        }

        _widthSnapshot?.Restore(_button);
        _widthSnapshot = null;
        _minWidthSnapshot?.Restore(_button);
        _minWidthSnapshot = null;
        _paddingSnapshot?.Restore(_button);
        _paddingSnapshot = null;
        _contentSnapshot?.Restore(_button);
        _contentSnapshot = null;
        _automationNameSnapshot?.Restore(_button);
        _automationNameSnapshot = null;
        _backgroundSnapshot?.Restore(_button);
        _backgroundSnapshot = null;
        _foregroundSnapshot?.Restore(_button);
        _foregroundSnapshot = null;
    }

    private readonly struct PropertySnapshot
    {
        private readonly DependencyProperty _dp;
        private readonly System.Windows.Data.BindingBase? _binding;
        private readonly object? _localValue;
        private readonly bool _hadLocalValue;

        private PropertySnapshot(DependencyProperty dp, System.Windows.Data.BindingBase? binding, object? localValue, bool hadLocalValue)
        {
            _dp = dp;
            _binding = binding;
            _localValue = localValue;
            _hadLocalValue = hadLocalValue;
        }

        internal static PropertySnapshot Capture(DependencyObject target, DependencyProperty dp)
        {
            var binding = System.Windows.Data.BindingOperations.GetBindingBase(target, dp);
            if (binding != null)
            {
                return new PropertySnapshot(dp, binding, null, true);
            }
            var local = target.ReadLocalValue(dp);
            if (local == DependencyProperty.UnsetValue)
            {
                return new PropertySnapshot(dp, null, null, false);
            }
            return new PropertySnapshot(dp, null, local, true);
        }

        internal void Restore(DependencyObject target)
        {
            if (_binding != null)
            {
                System.Windows.Data.BindingOperations.SetBinding(target, _dp, _binding);
                return;
            }
            if (!_hadLocalValue)
            {
                target.ClearValue(_dp);
                return;
            }
            try
            {
                target.SetValue(_dp, _localValue);
            }
            catch
            {
                target.ClearValue(_dp);
            }
        }
    }
}

/// <summary>
/// Pure state helper that preserves distinct vision models across toggles of the
/// "use text model for vision" checkbox, allowing check -> uncheck to cleanly restore.
/// </summary>
public sealed class SharedVisionModelTracker
{
    private string? _stashedVisionModel;

    public string? StashedVisionModel => _stashedVisionModel;

    public void OnLoaded(string? textModel, string? visionModel)
    {
        _ = textModel;
        _ = visionModel;
        _stashedVisionModel = null;
    }

    public (string EffectiveVisionModel, bool IsEnabled) OnToggleShared(
        bool shared, string? currentTextModel, string? currentVisionModel)
    {
        var text = (currentTextModel ?? string.Empty).Trim();
        var vision = currentVisionModel ?? string.Empty;

        if (shared)
        {
            _stashedVisionModel ??= vision;
            return (text, false);
        }
        else
        {
            var restored = _stashedVisionModel ?? vision;
            _stashedVisionModel = null;
            return (restored, true);
        }
    }

    public void Reset()
    {
        _stashedVisionModel = null;
    }
}

/// <summary>
/// Normalized snapshot for the service editor form. Comparing this against baseline
/// guarantees zero false drafts from formatting differences, model list refreshes or connection tests.
/// </summary>
public sealed record ServiceEditorSnapshot(
    string Name,
    string ProviderType,
    string BaseUrl,
    string TextEndpoint,
    string VisionEndpoint,
    string TextModel,
    string VisionModel,
    string ExtraHeaders,
    string AnthropicVersion,
    bool SupportsText,
    bool SupportsVision,
    bool UseTextModelForVision,
    bool AllowInsecureTls,
    string ApiKey)
{
    public static ServiceEditorSnapshot CreateNormalized(
        string? name,
        string? providerType,
        string? baseUrl,
        string? textEndpoint,
        string? visionEndpoint,
        string? textModel,
        string? visionModel,
        string? extraHeaders,
        string? anthropicVersion,
        bool supportsText,
        bool supportsVision,
        bool useTextModelForVision,
        bool allowInsecureTls,
        string? apiKey)
    {
        var normTextModel = ServicesSection.NormalizeEditorText(textModel);
        var normVisionModel = useTextModelForVision
            ? normTextModel
            : ServicesSection.NormalizeEditorText(visionModel);

        return new ServiceEditorSnapshot(
            ServicesSection.NormalizeEditorText(name),
            providerType ?? string.Empty,
            ServicesSection.NormalizeEditorText(baseUrl),
            ServicesSection.NormalizeEditorText(textEndpoint),
            ServicesSection.NormalizeEditorText(visionEndpoint),
            normTextModel,
            normVisionModel,
            ServicesSection.NormalizeHeaderValue(extraHeaders),
            ServicesSection.NormalizeEditorText(anthropicVersion),
            supportsText,
            supportsVision,
            useTextModelForVision,
            allowInsecureTls,
            apiKey ?? string.Empty);
    }

    public string Serialize() => string.Join('\u001f',
        Name,
        ProviderType,
        BaseUrl,
        TextEndpoint,
        VisionEndpoint,
        TextModel,
        VisionModel,
        ExtraHeaders,
        AnthropicVersion,
        SupportsText ? "1" : "0",
        SupportsVision ? "1" : "0",
        UseTextModelForVision ? "1" : "0",
        AllowInsecureTls ? "1" : "0",
        ApiKey);
}

/// <summary>
/// Normalized snapshot for the four route-determining fields.
/// </summary>
public sealed record RouteDraftSnapshot(
    bool NetworkEnabled,
    bool SafeMode,
    bool AllowImageUpload,
    string Mode)
{
    public static RouteDraftSnapshot Create(
        bool networkEnabled,
        bool safeMode,
        bool allowImageUpload,
        string? mode)
    {
        return new RouteDraftSnapshot(
            networkEnabled,
            safeMode,
            allowImageUpload,
            mode ?? "Auto");
    }

    public string Serialize() => string.Join('\u001f',
        NetworkEnabled ? "1" : "0",
        SafeMode ? "1" : "0",
        AllowImageUpload ? "1" : "0",
        Mode);
}

/// <summary>
/// Normalized snapshot for the entire settings window form.
/// </summary>
public sealed record SettingsFormSnapshot(
    string SelectionHotkey,
    string ScreenshotHotkey,
    string CloseHotkey,
    string ShowWindowHotkey,
    bool HistoryEnabled,
    bool CloseOnFocusLoss,
    bool AutoCopy,
    bool StartWithWindows,
    bool IncludeExplanation,
    bool ProtectTokens,
    string Theme,
    RouteDraftSnapshot Route,
    bool CloseToTray = true,
    string QuickSearchHotkey = "",
    string InterfaceModelHotkey = "",
    string InterfaceStyleHotkey = "",
    string InterfaceSwapHotkey = "",
    string InterfaceWorkbenchHotkey = "",
    string InterfaceLibraryHotkey = "",
    string InterfaceSettingsHotkey = "",
    string InterfaceTranslateHotkey = "",
    string InterfaceSpeakHotkey = "")
{
    public static SettingsFormSnapshot Create(
        string? selectionHotkey,
        string? screenshotHotkey,
        string? closeHotkey,
        string? showWindowHotkey,
        bool historyEnabled,
        bool closeOnFocusLoss,
        bool autoCopy,
        bool startWithWindows,
        bool includeExplanation,
        bool protectTokens,
        string? theme,
        RouteDraftSnapshot route,
        bool closeToTray = true,
        string? quickSearchHotkey = null,
        string? interfaceModelHotkey = null,
        string? interfaceStyleHotkey = null,
        string? interfaceSwapHotkey = null,
        string? interfaceWorkbenchHotkey = null,
        string? interfaceLibraryHotkey = null,
        string? interfaceSettingsHotkey = null,
        string? interfaceTranslateHotkey = null,
        string? interfaceSpeakHotkey = null)
    {
        return new SettingsFormSnapshot(
            selectionHotkey ?? string.Empty,
            screenshotHotkey ?? string.Empty,
            closeHotkey ?? string.Empty,
            showWindowHotkey ?? string.Empty,
            historyEnabled,
            closeOnFocusLoss,
            autoCopy,
            startWithWindows,
            includeExplanation,
            protectTokens,
            theme ?? "System",
            route,
            closeToTray,
            quickSearchHotkey ?? string.Empty,
            interfaceModelHotkey ?? string.Empty,
            interfaceStyleHotkey ?? string.Empty,
            interfaceSwapHotkey ?? string.Empty,
            interfaceWorkbenchHotkey ?? string.Empty,
            interfaceLibraryHotkey ?? string.Empty,
            interfaceSettingsHotkey ?? string.Empty,
            interfaceTranslateHotkey ?? string.Empty,
            interfaceSpeakHotkey ?? string.Empty);
    }

    public string Serialize() => string.Join('\u001f',
        SelectionHotkey,
        ScreenshotHotkey,
        CloseHotkey,
        ShowWindowHotkey,
        HistoryEnabled ? "1" : "0",
        CloseOnFocusLoss ? "1" : "0",
        AutoCopy ? "1" : "0",
        StartWithWindows ? "1" : "0",
        IncludeExplanation ? "1" : "0",
        ProtectTokens ? "1" : "0",
        Theme,
        Route.Serialize(),
        CloseToTray ? "1" : "0",
        QuickSearchHotkey,
        InterfaceModelHotkey,
        InterfaceStyleHotkey,
        InterfaceSwapHotkey,
        InterfaceWorkbenchHotkey,
        InterfaceLibraryHotkey,
        InterfaceSettingsHotkey,
        InterfaceTranslateHotkey,
        InterfaceSpeakHotkey);
}
