using Microsoft.Win32;

namespace PopGlot.Windows;

/// <summary>
/// C07: the honest startup picture — what the user asked for, what Windows
/// actually has, and where the two disagree. The settings page renders this
/// object instead of guessing from a single boolean.
/// </summary>
/// <param name="DesiredEnabled">The user's preference (ShellSettings.StartWithWindows).</param>
/// <param name="RunEntryPresent">HKCU Run carries a PopGlot value.</param>
/// <param name="PathMatches">That value points at the current executable.</param>
/// <param name="OsDisabled">Explorer's StartupApproved marks the entry disabled (Task Manager); null = unreadable/unknown.</param>
/// <param name="EffectiveEnabled">Windows will actually launch PopGlot at sign-in.</param>
/// <param name="LastError">Registry failure reason, or null.</param>
internal sealed record StartupState(
    bool DesiredEnabled,
    bool RunEntryPresent,
    bool PathMatches,
    bool? OsDisabled,
    bool EffectiveEnabled,
    string? LastError)
{
    /// <summary>One agreed wording for every settings surface that shows state.</summary>
    public string DescribeZh() => LastError is not null
        ? "开机启动状态暂时无法确认"
        : (DesiredEnabled, RunEntryPresent, PathMatches, OsDisabled) switch
        {
            (_, _, _, null) =>
                "开机启动状态暂时无法确认",
            (_, false, _, _) when DesiredEnabled =>
                "开机启动未成功",
            (_, _, false, false) when DesiredEnabled =>
                "开机启动未成功",
            (_, _, _, true) when DesiredEnabled =>
                "开机启动已被 Windows 关闭",
            (_, true, true, false) when DesiredEnabled =>
                "已开启",
            (_, _, _, true) =>
                "已关闭",
            (_, false, _, _) =>
                "已关闭",
            _ =>
                "已关闭",
        };
}

/// <summary>A01: what a settings save may (or must not) do to the OS startup state.</summary>
internal enum StartupSaveAction
{
    /// <summary>Desire and reality already agree; no registry write.</summary>
    None,
    /// <summary>Desire on, entry missing, not OS-disabled: recreate it.</summary>
    Create,
    /// <summary>Desire off, entry present: remove it.</summary>
    Remove,
    /// <summary>Desire on, entry present but stale: rewrite the path only.</summary>
    RepairPath,
}

/// <summary>
/// Registers PopGlot in the per-user "run at sign-in" list.
/// </summary>
/// <remarks>
/// Writes only to HKCU so no elevation is ever required, quotes the path so
/// a program directory containing spaces still launches.
///
/// C07: automatic self-heal only repairs a stale Run path or recreates a
/// missing entry. A Task-Manager disable (StartupApproved) is the user's
/// explicit choice — it is surfaced in the settings page with a「重新启用」
/// action and is never silently overwritten at startup. Clearing that
/// disable happens only inside <see cref="TrySet"/>, i.e. from an explicit
/// user action.
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PopGlot";

    /// <summary>Test seam: replaces the registry write so tests never touch HKCU.</summary>
    internal static Func<bool, bool>? TrySetOverride { get; set; }

    /// <summary>Test seam: replaces the registry check so tests never touch HKCU.</summary>
    internal static Func<bool>? IsEnabledOverride { get; set; }

    /// <summary>Test seam: replaces the whole state read so tests never touch HKCU.</summary>
    internal static Func<bool, StartupState>? ReadStateOverride { get; set; }

    /// <summary>Test seam: replaces the Run-path repair so tests never touch HKCU.</summary>
    internal static Func<bool>? RepairRunPathOverride { get; set; }

    /// <summary>Test seam: replaces the create-entry write so tests never touch HKCU.</summary>
    internal static Func<bool>? CreateRunEntryOverride { get; set; }

    /// <summary>Test seam: replaces the remove-entry write so tests never touch HKCU.</summary>
    internal static Func<bool>? RemoveRunEntryOverride { get; set; }

    /// <summary>
    /// C07: reads the real registry picture. Never throws — failures land in
    /// <see cref="StartupState.LastError"/> so the UI can show them.
    /// </summary>
    public static StartupState ReadState(bool desiredEnabled)
    {
        if (ReadStateOverride is not null)
        {
            return ReadStateOverride(desiredEnabled);
        }
        try
        {
            string? runValue = null;
            var readFailed = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                runValue = key?.GetValue(ValueName) as string;
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or System.Security.SecurityException)
            {
                readFailed = true;
            }
            if (readFailed)
            {
                return new StartupState(
                    desiredEnabled, false, false, false, false,
                    "无法读取启动项（权限不足）");
            }

            var runEntryPresent = !string.IsNullOrWhiteSpace(runValue);
            var pathMatches = false;
            if (runEntryPresent)
            {
                pathMatches = RunCommandMatchesCurrent(runValue!);
            }

            var (osDisabled, osReadError) = ReadOsDisabled();
            if (osReadError is not null)
            {
                // A03: an unreadable OS state is UNKNOWN — it must never be
                // reported as "not disabled" (that would fake effectiveness).
                return new StartupState(
                    desiredEnabled, runEntryPresent, pathMatches, null, false, osReadError);
            }
            return new StartupState(
                desiredEnabled,
                runEntryPresent,
                pathMatches,
                osDisabled,
                runEntryPresent && pathMatches && osDisabled == false,
                null);
        }
        catch (Exception exception)
        {
            return new StartupState(
                desiredEnabled, false, false, null, false, exception.GetType().Name);
        }
    }

    /// <summary>
    /// A02: extracts the executable path from a Run command — a quoted path
    /// (with or without trailing arguments) or an unquoted bare path.
    /// </summary>
    internal static string? ExtractExecutablePath(string runValue)
    {
        var value = runValue.Trim();
        if (value.Length == 0)
        {
            return null;
        }
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }

    /// <summary>A02: the single launch-command contract for the Run entry.</summary>
    internal static string BuildRunCommand(string executable) =>
        $"\"{executable}\" --background";

    internal static string? BuildCurrentRunCommand()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return null;

        // `dotnet run` / framework-dependent launches report dotnet.exe as the
        // process path. Registering only dotnet.exe silently opens nothing at
        // sign-in, so include the entry DLL in that case. Installed apphost
        // builds continue to use the ordinary executable command.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryDll) &&
                string.Equals(Path.GetExtension(entryDll), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                return $"\"{executable}\" \"{entryDll}\" --background";
            }
        }
        return BuildRunCommand(executable);
    }

    internal static bool RunCommandMatchesCurrent(string runValue)
    {
        var expected = BuildCurrentRunCommand();
        return !string.IsNullOrWhiteSpace(expected) &&
            string.Equals(runValue.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A01: the save-time decision, pure and testable. A plain save NEVER
    /// clears an OS disable — only Create/Remove/RepairPath happen here.
    /// </summary>
    internal static StartupSaveAction PlanSaveAction(StartupState state)
    {
        if (!state.DesiredEnabled)
        {
            return state.RunEntryPresent ? StartupSaveAction.Remove : StartupSaveAction.None;
        }
        if (state.OsDisabled == true)
        {
            // The user's Task-Manager choice wins until they click re-enable.
            return StartupSaveAction.None;
        }
        if (!state.RunEntryPresent)
        {
            return state.OsDisabled == false ? StartupSaveAction.Create : StartupSaveAction.None;
        }
        if (!state.PathMatches)
        {
            return StartupSaveAction.RepairPath;
        }
        return StartupSaveAction.None;
    }

    /// <summary>Explorer/Task-Manager disable marker; null when unreadable.</summary>
    private static (bool? Disabled, string? Error) ReadOsDisabled()
    {
        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: false);
            if (approvedKey?.GetValue(ValueName) is byte[] bytes && bytes.Length > 0)
            {
                // Byte 0 == 0x02 is enabled. 0x03 or other values indicate a
                // user/admin disable in Task Manager.
                return (bytes[0] != 0x02, null);
            }
            return (false, null);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A03: an unreadable state is unknown, never "enabled".
            return (null, "无法读取 Windows 启用状态（权限不足）");
        }
    }

    public static bool IsEnabled()
    {
        if (IsEnabledOverride is not null)
        {
            return IsEnabledOverride();
        }
        return ReadState(desiredEnabled: false).EffectiveEnabled;
    }

    public static Task<bool> IsEnabledAsync() =>
        Task.Run(IsEnabled);

    public static Task<StartupState> ReadStateAsync(bool desiredEnabled) =>
        Task.Run(() => ReadState(desiredEnabled));

    /// <summary>
    /// Startup self-heal, called when the preference says ON. Repairs a stale
    /// Run path or recreates a missing entry, but NEVER clears an OS-level
    /// disable — that state belongs to the user and is surfaced in settings
    /// (C07/F11).
    /// </summary>
    public static bool EnsureRegistered()
    {
        var state = ReadState(desiredEnabled: true);
        if (!state.DesiredEnabled)
        {
            return true;
        }
        if (state.RunEntryPresent)
        {
            return state.PathMatches || RepairRunPath();
        }
        if (state.OsDisabled == true)
        {
            // A StartupApproved disable without a Run value: recreating and
            // un-disabling in one move would silently undo the user's
            // Task-Manager choice. Leave it to the settings page.
            return false;
        }
        return TrySet(true);
    }

    /// <summary>
    /// Rewrites ONLY the Run value to the current executable. Never touches
    /// StartupApproved, so it is safe even when the entry is OS-disabled.
    /// </summary>
    public static bool RepairRunPath()
    {
        if (RepairRunPathOverride is not null)
        {
            return RepairRunPathOverride();
        }
        try
        {
            var launchCommand = BuildCurrentRunCommand();
            if (string.IsNullOrWhiteSpace(launchCommand))
            {
                return false;
            }
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null || key.GetValue(ValueName) is null)
            {
                return false;
            }
            key.SetValue(ValueName, launchCommand, RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// V02: plain-save create — writes ONLY this app's Run value. Unlike
    /// <see cref="TrySet"/>, it never touches StartupApproved; it is only
    /// reachable when the entry is not OS-disabled (PlanSaveAction).
    /// </summary>
    public static bool CreateRunEntry()
    {
        if (CreateRunEntryOverride is not null)
        {
            return CreateRunEntryOverride();
        }
        try
        {
            var launchCommand = BuildCurrentRunCommand();
            if (string.IsNullOrWhiteSpace(launchCommand))
            {
                return false;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }
            key.SetValue(ValueName, launchCommand, RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// V02: plain-save remove — deletes ONLY this app's Run value. The
    /// StartupApproved record (including a Task-Manager disable) is left
    /// exactly as Windows wrote it; disabling auto-start never edits system
    /// state beyond our own entry.
    /// </summary>
    public static bool RemoveRunEntry()
    {
        if (RemoveRunEntryOverride is not null)
        {
            return RemoveRunEntryOverride();
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is not null && key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// V02: the save handler's single execution step from decision to
    /// registry adapter. Plain saves never reach <see cref="TrySet"/> — only
    /// the explicit re-enable button does.
    /// </summary>
    public static bool ExecuteSaveAction(StartupSaveAction action) => action switch
    {
        StartupSaveAction.Create => CreateRunEntry(),
        StartupSaveAction.Remove => RemoveRunEntry(),
        StartupSaveAction.RepairPath => RepairRunPath(),
        _ => true,
    };

    public static Task<bool> ExecuteSaveActionAsync(StartupSaveAction action) =>
        Task.Run(() => ExecuteSaveAction(action));

    /// <summary>
    /// V03: the honest save-failure resolution. The baseline the form must
    /// adopt is whatever the DISK actually holds — never a display value
    /// pretending a rollback landed. Registry failure + successful rollback
    /// keeps the previous preference (retryable); a failed rollback means
    /// the new preference is what is on disk, stated as such, with the
    /// mismatch to the OS startup entry surfaced by the state row.
    /// </summary>
    public static (ShellSettings Baseline, string StatusZh, bool IsError) ResolveStartupSaveFailure(
        ShellSettings savedNew, ShellSettings previous, bool rollbackWritten)
    {
        if (rollbackWritten)
        {
            // V03: ONLY the startup preference rolls back — every other
            // field keeps its new value, matching what the disk actually
            // holds (the rollback wrote savedNew with the old startup flag).
            return (savedNew with { StartWithWindows = previous.StartWithWindows },
                "开机启动没有成功，其他设置已保存。可以点击“修复”重试。",
                true);
        }
        return (savedNew,
            "开机启动没有成功，当前状态可能不同步。其他设置已保存，请点击“修复”重试。",
            true);
    }

    /// <summary>
    /// Applies the preference from an explicit user action; returns false
    /// when the registry refused. Enabling here also clears an existing
    /// Task-Manager disable — the click IS the user's re-enable (C07).
    /// </summary>
    public static bool TrySet(bool enabled) => TrySetOverride?.Invoke(enabled) ?? TrySetCore(enabled);

    public static Task<bool> TrySetAsync(bool enabled) => Task.Run(() => TrySet(enabled));

    private static bool TrySetCore(bool enabled)
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
                var launchCommand = BuildCurrentRunCommand();
                if (string.IsNullOrWhiteSpace(launchCommand))
                {
                    return false;
                }
                key.SetValue(ValueName, launchCommand, RegistryValueKind.String);

                // Explicit re-enable: clear a Task-Manager disable so the
                // user's click actually takes effect.
                try
                {
                    using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: true);
                    if (approvedKey?.GetValue(ValueName) is byte[] bytes && bytes.Length > 0 && bytes[0] != 0x02)
                    {
                        bytes[0] = 0x02;
                        approvedKey.SetValue(ValueName, bytes, RegistryValueKind.Binary);
                    }
                }
                catch
                {
                    // Non-fatal: StartupApproved key might not exist or might be restricted
                }
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                try
                {
                    using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: true);
                    if (approvedKey?.GetValue(ValueName) is not null)
                    {
                        approvedKey.DeleteValue(ValueName, throwOnMissingValue: false);
                    }
                }
                catch
                {
                    // Non-fatal
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
