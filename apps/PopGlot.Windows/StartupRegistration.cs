using Microsoft.Win32;

namespace PopGlot.Windows;

/// <summary>
/// Registers PopGlot in the per-user "run at sign-in" list.
/// </summary>
/// <remarks>
/// Writes only to HKCU so no elevation is ever required, and quotes the path so
/// a program directory containing spaces still launches.
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PopGlot";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Applies the preference; returns false when the registry refused.</summary>
    public static bool TrySet(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    return false;
                }
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
