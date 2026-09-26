using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

    // Task Scheduler COM constants (taskschd.h).
    private const int TaskCreateOrUpdate = 6;
    private const int TaskActionExec = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const string NoTimeLimit = "PT0S";

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

    /// <summary>
    /// Rewrites a logon task left by an older build, which registered it with schtasks
    /// defaults and so had it killed 72 hours after logon (see <see cref="CreateTask"/>).
    /// Needs elevation, which the task itself provides at logon. Only touches a task that
    /// already launches this exe, so a dev build run elevated never takes over the
    /// installed copy's autostart.
    /// </summary>
    public static void RepairTask()
    {
        if (!PowerRequestList.IsElevated) return;

        object? service = null;
        object? folder = null;
        object? task = null;
        object? definition = null;
        object? settings = null;
        object? actions = null;
        object? action = null;

        try
        {
            service = CreateScheduleService();
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");

            try { task = ((dynamic)folder).GetTask(TaskName); }
            catch { return; }   // not registered, nothing to repair

            definition = ((dynamic)task).Definition;
            settings = ((dynamic)definition).Settings;
            actions = ((dynamic)definition).Actions;
            if (((dynamic)actions).Count < 1) return;
            action = ((dynamic)actions).Item(1);

            if ((string?)((dynamic)settings).ExecutionTimeLimit == NoTimeLimit) return;
            if (!SamePath((string?)((dynamic)action).Path ?? string.Empty, ExePath)) return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("AutoStart.RepairTask", Unwrap(ex));
            return;
        }
        finally
        {
            ReleaseCom(action);
            ReleaseCom(actions);
            ReleaseCom(settings);
            ReleaseCom(definition);
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
        }

        var error = CreateTask();
        if (error is not null)
            CrashLog.Write("AutoStart.RepairTask", new InvalidOperationException(error));
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

    /// <summary>
    /// Registers the logon task through the Task Scheduler COM API rather than
    /// "schtasks /create", which leaves every setting at Task Scheduler's defaults. Two of
    /// those are wrong for a tray app that runs all day: a 72-hour ExecutionTimeLimit, so
    /// the app was killed three days after logon, and stopping when the machine goes on
    /// battery. Same settings as HALO's AutostartManager.
    /// </summary>
    private static string? CreateTask()
    {
        object? service = null;
        object? folder = null;
        object? definition = null;
        object? trigger = null;
        object? action = null;

        try
        {
            var user = WindowsIdentity.GetCurrent().Name;

            service = CreateScheduleService();
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");

            definition = ((dynamic)service).NewTask(0);
            dynamic d = definition;
            d.RegistrationInfo.Description = "Starts MonitorScreenSaver at logon.";
            d.Principal.UserId = user;
            d.Principal.LogonType = TaskLogonInteractiveToken;
            d.Principal.RunLevel = TaskRunLevelHighest;   // elevated at logon without a UAC prompt
            d.Settings.DisallowStartIfOnBatteries = false;
            d.Settings.StopIfGoingOnBatteries = false;
            d.Settings.ExecutionTimeLimit = NoTimeLimit;
            d.Settings.StartWhenAvailable = true;
            d.Settings.RestartCount = 3;
            d.Settings.RestartInterval = "PT1M";
            d.Settings.Priority = 5;                      // schtasks default is 7, below normal

            // Scoped to this user; schtasks' onlogon fired on every account's logon.
            trigger = d.Triggers.Create(TaskTriggerLogon);
            ((dynamic)trigger).UserId = user;
            ((dynamic)trigger).Enabled = true;

            action = d.Actions.Create(TaskActionExec);
            ((dynamic)action).Path = ExePath;
            ((dynamic)action).WorkingDirectory = Path.GetDirectoryName(ExePath) ?? string.Empty;

            ((dynamic)folder).RegisterTaskDefinition(
                TaskName, definition, TaskCreateOrUpdate, user, null, TaskLogonInteractiveToken, null);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not register the scheduled task: {Unwrap(ex).Message}";
        }
        finally
        {
            ReleaseCom(action);
            ReleaseCom(trigger);
            ReleaseCom(definition);
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    private static object CreateScheduleService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler COM API is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not connect to Windows Task Scheduler.");
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            static string Normalise(string p) =>
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')));

            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                && Normalise(a).Equals(Normalise(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is System.Reflection.TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            try { Marshal.FinalReleaseComObject(value); } catch { }
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
