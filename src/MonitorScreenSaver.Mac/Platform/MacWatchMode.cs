using System.Diagnostics;
using System.IO;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The macOS twin of the Windows head's WatchMode: a timestamped log of every display,
/// power and session transition the OS reports, plus a heartbeat of everything the engine
/// keys off, so a blanking decision can be reconstructed after the fact.
///
/// Run with:  MonitorScreenSaverMac watch [path]
/// then lock the machine, let it idle, plug a display in, and unlock. Kill it when done.
///
/// The Windows version exists to settle whether "Console lock display off timeout" still
/// powers the panel off when "Turn off display after" is Never. The macOS question this
/// answers is the one that decides whether our overlays are even reachable: what the
/// system's own displaysleep timer does relative to ours, and whether lock/unlock notices
/// still arrive (they ride an undocumented notification pair — see MacSystemEvents).
/// </summary>
public static class MacWatchMode
{
    private static StreamWriter _log = null!;
    private static MacSystemEvents _events = null!;
    private static MacRunLoopTimer _heartbeat = null!;
    private static MacActivityClock _clock = null!;
    private static MacAudioSource _audio = null!;
    private static MacFullscreenDetector _fullscreen = null!;
    private static DateTime _start;

    public static void Start(string? path)
    {
        AppKit.EnsureApplication();

        path ??= Path.Combine(AppSettings.Directory, "watch.log");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _log = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        _start = DateTime.Now;
        _clock = new MacActivityClock();
        _audio = new MacAudioSource();
        _fullscreen = new MacFullscreenDetector();

        Write($"MonitorScreenSaver watch  |  started {_start:yyyy-MM-dd HH:mm:ss}");
        Write($"log: {Path.GetFullPath(path)}");
        Write("");
        WritePowerConfig();
        Write("");
        WriteDisplays();
        Write("");
        Write("Now: lock the machine (Ctrl+Cmd+Q), let it idle, unlock, change a display setting.");
        Write("Watching power, topology and session transitions. Ctrl+C or kill the process to stop.");
        Write(new string('=', 78));

        _events = new MacSystemEvents();
        _events.Event += OnEvent;

        _heartbeat = new MacRunLoopTimer(TimeSpan.FromSeconds(10), Heartbeat);
        _heartbeat.Start();

        Heartbeat();

        CF.CFRunLoopRun();
    }

    /// <summary>
    /// The mac counterpart of the Windows power-scheme dump. pmset is the documented
    /// interface to these values and is read-only here; parsing its output beats
    /// reverse-engineering the preferences plist for a diagnostic.
    /// </summary>
    private static void WritePowerConfig()
    {
        Write("system power settings (pmset -g custom):");

        var text = RunTool("/usr/bin/pmset", "-g custom");

        if (text is null)
        {
            Write("  (pmset unavailable)");
            return;
        }

        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;

            // Section headers ("AC Power:", "Battery Power:") plus the timers that decide
            // whether the OS blanks before we do.
            if (t.EndsWith(':') ||
                t.StartsWith("displaysleep", StringComparison.Ordinal) ||
                t.StartsWith("sleep", StringComparison.Ordinal) ||
                t.StartsWith("lidwake", StringComparison.Ordinal) ||
                t.StartsWith("powernap", StringComparison.Ordinal))
            {
                Write($"  {t}");
            }
        }

        Write("  (0 = Never. A displaysleep shorter than our idle timeout means the OS wins the race.)");
    }

    private static void WriteDisplays()
    {
        Write("displays:");

        foreach (var d in new MacDisplayEnumerator().Enumerate())
            Write($"  {d.FriendlyName,-28} {d.Geometry}{(d.IsPrimary ? "   [PRIMARY]" : "")}  {d.StableId}");
    }

    private static void OnEvent(SystemEventKind kind)
    {
        var marker = kind switch
        {
            SystemEventKind.SuspendingToSleep => "  <<<< SLEEPING",
            SystemEventKind.ResumedFromSleep => "  >>>> WOKE",
            SystemEventKind.SessionLocked => "  ---- LOCKED",
            SystemEventKind.SessionUnlocked => "  ---- UNLOCKED",
            SystemEventKind.DisplayTopologyChanged => "  ~~~~ DISPLAYS CHANGED",
            _ => string.Empty,
        };

        Write($"EVENT  {kind}{marker}");
    }

    private static void Heartbeat()
    {
        var exec = MacExecutionSource.Read();
        var idle = TimeSpan.FromMilliseconds(_clock.NowMs - _clock.LastInputMs);
        var snapshot = MacPowerAssertions.Query();

        var holders = snapshot.Display.Select(r => r.ShortName).Distinct().ToList();
        var holderText = holders.Count == 0 ? "none" : string.Join(", ", holders);

        Write($"       idle={idle.TotalSeconds,6:F1}s  exec=0x{exec.Raw:X2} " +
              $"(display={exec.DisplayRequired} system={exec.SystemRequired} present={exec.UserPresent})  " +
              $"audio={_audio.IsPlaying()}  fullscreen={_fullscreen.IsFullscreenActive()}  " +
              $"frontmost={MacForegroundWatch.FrontmostName()}  display-holders={holderText}");
    }

    private static string? RunTool(string path, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string text)
    {
        var stamp = _start == default ? "" : $"[{DateTime.Now:HH:mm:ss}  T+{(DateTime.Now - _start).TotalSeconds,6:F0}s]  ";
        _log.WriteLine(stamp + text);
        try { Console.WriteLine(stamp + text); } catch { /* no console attached */ }
    }
}
