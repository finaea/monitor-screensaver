using System.Diagnostics;
using Microsoft.Win32;

namespace MonitorScreenSaver.Core;

/// <summary>
/// Two start-with-Windows mechanisms:
///  - HKCU\...\Run for the normal case (no admin needed, no UAC prompt).
///  - A logon scheduled task with RunLevel=HIGHEST when the user wants the requester
///    name list available from boot, since that needs elevation and a Run-key entry
///    would trigger a UAC prompt at every logon.
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorScreenSaver";
    private const string TaskName = "MonitorScreenSaver Autostart";

    // Pre-rename identity, migrated away from at startup.
    private const string LegacyValueName = "MonitorDim";
    private const string LegacyTaskName = "MonitorDim Autostart";

    private static string ExePath => Environment.ProcessPath ?? string.Empty;

    public static bool IsEnabled => RunKeyPresent || TaskPresent();

    public static bool IsElevatedTask => TaskPresent();

    private static bool RunKeyPresent
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Replaces the old "MonitorDim" Run-key entry (which points at an exe that no
    /// longer exists) with one under the new name. The old elevated task is deleted
    /// too when we have the rights; failing that it is left behind, harmless — its
    /// target is gone, so it silently does nothing at logon.
    /// </summary>
    public static void MigrateLegacy(bool checkTask)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

                if (!string.IsNullOrEmpty(ExePath) && key.GetValue(ValueName) is null)
                    key.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            }
        }
        catch
        {
            // best effort
        }

        if (!checkTask) return;

        try
        {
            if (RunSchtasks($"/query /tn \"{LegacyTaskName}\"").ExitCode == 0)
                RunSchtasks($"/delete /tn \"{LegacyTaskName}\" /f");
        }
        catch
        {
            // best effort; deleting an elevated task needs elevation
        }
    }

    /// <summary>Applies the requested state. Returns null on success, or a message to show the user.</summary>
    public static string? Apply(bool enabled, bool elevated)
    {
        try
        {
            RemoveRunKey();
            RemoveTask();

            if (!enabled) return null;

            if (string.IsNullOrEmpty(ExePath))
                return "Could not determine the executable path.";

            if (elevated)
            {
                if (!PowerRequestList.IsElevated)
                    return "Elevated autostart must be configured while running as administrator.";

                return CreateTask();
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key?.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // nothing to undo
        }
    }

    private static bool TaskPresent() => RunSchtasks($"/query /tn \"{TaskName}\"").ExitCode == 0;

    private static void RemoveTask()
    {
        if (TaskPresent()) RunSchtasks($"/delete /tn \"{TaskName}\" /f");
    }

    private static string? CreateTask()
    {
        // /rl HIGHEST runs elevated at logon without a UAC prompt.
        var args = $"/create /tn \"{TaskName}\" /tr \"\\\"{ExePath}\\\"\" /sc onlogon /rl HIGHEST /f";
        var result = RunSchtasks(args);

        return result.ExitCode == 0
            ? null
            : $"schtasks failed ({result.ExitCode}): {result.Output}".Trim();
    }

    private static (int ExitCode, string Output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "could not start schtasks.exe");

            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            return (proc.ExitCode, output.Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
