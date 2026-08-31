using System.Windows;
using System.Windows.Controls;
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

    internal static async Task<bool> CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                await Task.Delay(15 * (attempt + 1));
            }
        }
        return false;
    }
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
    private static readonly TimeSpan ArmTimeout = TimeSpan.FromSeconds(5);

    private readonly Button _button;
    private readonly string _normalContent;
    private readonly string _armedContent;
    private readonly Action _fire;
    private readonly DispatcherTimer _timer;
    private bool _armed;

    private ConfirmButton(Button button, string armedContent, Action fire)
    {
        _button = button;
        _normalContent = (string)button.Content;
        _armedContent = armedContent;
        _fire = fire;
        _timer = new DispatcherTimer { Interval = ArmTimeout };
        _timer.Tick += (_, _) => Disarm();
        button.Click += OnClick;
    }

    public static ConfirmButton Attach(Button button, string armedContent, Action fire) =>
        new(button, armedContent, fire);

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (!_armed)
        {
            _armed = true;
            _button.Content = _armedContent;
            _button.Background = (Brush)_button.FindResource("DangerSoftBrush");
            _button.Foreground = (Brush)_button.FindResource("DangerBrush");
            _timer.Start();
            return;
        }
        Disarm();
        _fire();
    }

    private void Disarm()
    {
        _timer.Stop();
        if (!_armed)
        {
            return;
        }
        _armed = false;
        _button.Content = _normalContent;
        _button.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        _button.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
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
    RouteDraftSnapshot Route)
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
        RouteDraftSnapshot route)
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
            route);
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
        Route.Serialize());
}
