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
    }
}
