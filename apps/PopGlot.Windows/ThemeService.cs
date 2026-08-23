using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PopGlot.Windows;

internal static class ThemeService
{
    public static void Apply(ThemePreference preference)
    {
        var light = preference == ThemePreference.Light ||
            (preference == ThemePreference.System && SystemPrefersLight());
        var colors = light
            ? new Dictionary<string, string>
            {
                ["PageBackground"] = "#F4F6F9",
                ["SidebarBackground"] = "#ECEFF3",
                ["CardBackground"] = "#FFFFFF",
                ["ElevatedBackground"] = "#F7F9FB",
                ["InputBackground"] = "#FFFFFF",
                ["AccentBrush"] = "#087F6B",
                ["AccentTextBrush"] = "#FFFFFF",
                ["AccentMutedBrush"] = "#D9F0EB",
                ["PrimaryText"] = "#17202B",
                ["SecondaryText"] = "#566273",
                ["TertiaryText"] = "#7A8797",
                ["DividerBrush"] = "#D9DEE6",
                ["DangerBrush"] = "#B84A4A",
            }
            : new Dictionary<string, string>
            {
                ["PageBackground"] = "#0E1117",
                ["SidebarBackground"] = "#12161E",
                ["CardBackground"] = "#171C25",
                ["ElevatedBackground"] = "#1D2430",
                ["InputBackground"] = "#11161E",
                ["AccentBrush"] = "#59D3B1",
                ["AccentTextBrush"] = "#07130F",
                ["AccentMutedBrush"] = "#205C50",
                ["PrimaryText"] = "#F4F7FB",
                ["SecondaryText"] = "#A5AFBD",
                ["TertiaryText"] = "#748093",
                ["DividerBrush"] = "#26303D",
                ["DangerBrush"] = "#F08A8A",
            };

        foreach (var (key, value) in colors)
        {
            Application.Current.Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value));
        }
    }

    private static bool SystemPrefersLight()
    {
        try
        {
            return Convert.ToInt32(Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0)) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
