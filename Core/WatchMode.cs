using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MonitorDim.Core;

/// <summary>
/// Timestamped log of every display-power and session transition Windows reports.
///
/// Exists to settle empirically what the docs and tutorials disagree on: whether
/// "Console lock display off timeout" still powers the display off when
/// "Turn off display after" is set to Never.
///
/// Run with:  MonitorDim.exe --watch [path]
/// then lock the machine, wait, and unlock. Kill it when done.
/// </summary>
public static class WatchMode
{
    private const string SchemeRoot = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string VideoIdle = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
    private const string ConsoleLock = "8EC4B3A5-6868-48c2-BE75-4F3044BE88A7";

    private static StreamWriter _log = null!;
    private static SystemEventSink _sink = null!;
    private static DispatcherTimer _heartbeat = null!;
    private static DateTime _start;

    public static void Start(string? path)
    {
        path ??= Path.Combine(AppSettings.Directory, "watch.log");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        _start = DateTime.Now;

        Write($"MonitorDim --watch  |  started {_start:yyyy-MM-dd HH:mm:ss}");
        Write($"log: {Path.GetFullPath(path)}");
        Write("");
        WritePowerConfig();
        Write("");
        Write("Now: lock the machine (Win+L), wait ~2-3 minutes, then unlock.");
        Write("Watching for display-power transitions. Ctrl+C or kill the process to stop.");
        Write(new string('=', 78));

        _sink = new SystemEventSink(includeConsoleDisplayState: true);
        _sink.Event += OnEvent;

        _heartbeat = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _heartbeat.Tick += (_, _) => Heartbeat();
        _heartbeat.Start();

        Heartbeat();
    }

    private static void WritePowerConfig()
    {
        var active = ActiveSchemeGuid();
        Write($"active power scheme: {active ?? "(unknown)"}");

        Write($"  Turn off display after      {ReadSetting(active, VideoIdle)}");
        Write($"  Console lock display off    {ReadSetting(active, ConsoleLock)}");
        Write("  (0 = Never; console lock falls back to its shipped default when unset)");
    }

    private static string? ActiveSchemeGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemeRoot);
            return key?.GetValue("ActivePowerScheme") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadSetting(string? scheme, string settingGuid)
    {
        if (scheme is null) return "(unknown)";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SchemeRoot}\{scheme}\{SubVideo}\{settingGuid}");

            if (key is null) return "no per-scheme override (using default)";

            var ac = key.GetValue("ACSettingIndex");
            var dc = key.GetValue("DCSettingIndex");

            return $"AC={Fmt(ac)}  DC={Fmt(dc)}";
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }

        static string Fmt(object? v) => v is int i ? (i == 0 ? "Never" : $"{i}s") : "unset";
    }

    private static void OnEvent(SystemEventKind kind)
    {
        var marker = kind switch
        {
            SystemEventKind.WindowsDisplayOff => "  <<<< DISPLAY OFF",
            SystemEventKind.WindowsDisplayOn => "  >>>> DISPLAY ON",
            SystemEventKind.SessionLocked => "  ---- LOCKED",
            SystemEventKind.SessionUnlocked => "  ---- UNLOCKED",
            _ => string.Empty,
        };

        Write($"EVENT  {kind}{marker}");
    }

    private static void Heartbeat()
    {
        var exec = ExecutionState.Read();

        var display = _sink.WindowsDisplayState switch
        {
            0 => "off", 1 => "on", 2 => "dim", _ => "?",
        };

        var console = _sink.ConsoleDisplayState switch
        {
            0 => "off", 1 => "on", 2 => "dim", _ => "?",
        };

        var presence = _sink.WindowsUserPresence switch
        {
            0 => "present", 2 => "inactive", _ => "?",
        };

        Write($"       session-display={display}  console-display={console}  presence={presence}  exec=0x{exec.Raw:X2}");
    }

    private static void Write(string text)
    {
        var elapsed = DateTime.Now - _start;
        var stamp = _start == default ? "" : $"[{DateTime.Now:HH:mm:ss}  T+{elapsed.TotalSeconds,6:F0}s]  ";
        _log.WriteLine(stamp + text);
    }
}
