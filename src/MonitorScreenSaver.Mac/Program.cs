using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac;
using MonitorScreenSaver.Mac.Interop;

// Phase 2 harness: exercises every macOS platform service against the live machine,
// the same role the Windows head's --selftest/--watch play. The tray app (NSStatusItem),
// overlays and settings window arrive in later phases and will replace this Main.
//
//   status [n]         poll idle/exec/holders/audio/fullscreen every second, n times (default 5)
//   displays           enumerate displays with stable ids
//   assertions         dump the holder list (compare with: pmset -g assertions)
//   watch              log sleep/wake, topology and lock/unlock events until killed
//   engine [sec]       run the real Core BlankingEngine + overlays: an actual working
//                      screensaver loop with a [sec] idle timeout (default 15)
//   overlay <mode> [sec] [videoPath]
//                      show a black|dim|video overlay on every display for [sec]
//                      seconds (default 3), then tear down — the Phase 3 smoke test

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

switch (command)
{
    case "status":
        Status(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 5);
        break;

    case "displays":
        Displays();
        break;

    case "assertions":
        Assertions();
        break;

    case "watch":
        Watch();
        break;

    case "engine":
        Engine(args.Length > 1 && int.TryParse(args[1], out var t) ? t : 15);
        break;

    case "procname":
        Console.WriteLine(IOKit.ProcessName(int.Parse(args[1])));
        break;

    case "overlay":
        Overlay(
            args.Length > 1 ? args[1].ToLowerInvariant() : "black",
            args.Length > 2 && int.TryParse(args[2], out var secs) ? secs : 3,
            args.Length > 3 ? args[3] : null);
        break;

    default:
        Console.WriteLine("usage: MonitorScreenSaverMac [status [n] | displays | assertions | watch | engine [timeoutSeconds] | overlay <black|dim|video> [seconds] [videoPath]]");
        return 1;
}

return 0;

static void Status(int iterations)
{
    var clock = new MacActivityClock();
    var audio = new MacAudioSource();
    var fullscreen = new MacFullscreenDetector();

    for (var i = 0; i < iterations; i++)
    {
        var exec = MacExecutionSource.Read();
        var idle = TimeSpan.FromMilliseconds(clock.NowMs - clock.LastInputMs);

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] idle {idle.TotalSeconds,6:F2}s | " +
            $"exec raw=0x{exec.Raw:X2} display={exec.DisplayRequired} system={exec.SystemRequired} present={exec.UserPresent} | " +
            $"audio={audio.IsPlaying()} fullscreen={fullscreen.IsFullscreenActive()} | " +
            $"frontmost={MacForegroundWatch.FrontmostName()}");

        if (i < iterations - 1) Thread.Sleep(1000);
    }
}

static void Displays()
{
    foreach (var d in new MacDisplayEnumerator().Enumerate())
    {
        Console.WriteLine($"* {d.FriendlyName}{(d.IsPrimary ? "   [PRIMARY]" : "")}");
        Console.WriteLine($"    device   {d.DeviceName}");
        Console.WriteLine($"    geometry {d.Geometry}");
        Console.WriteLine($"    stableId {d.StableId}");
    }
}

static void Assertions()
{
    var snap = MacPowerAssertions.Query();

    if (!snap.Available)
    {
        Console.WriteLine($"unavailable: {snap.Unavailable}");
        return;
    }

    Console.WriteLine($"{snap.Requesters.Count} assertion(s) held:");
    foreach (var r in snap.Requesters)
        Console.WriteLine($"  [{r.RequestType}] {r.ShortName} ({r.Kind}) {r.Reason}");

    Console.WriteLine();
    Console.WriteLine($"DISPLAY holders: {snap.Display.Count()}   (compare with: pmset -g assertions)");
}

static void Watch()
{
    Console.WriteLine("Watching sleep/wake, display topology and lock/unlock. Ctrl+C to stop.");
    Console.WriteLine("Try: lock the screen (Ctrl+Cmd+Q), unlock, change a display setting.");

    using var events = new MacSystemEvents();
    events.Event += kind => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] EVENT  {kind}");

    CF.CFRunLoopRun();
}

static void Engine(int timeoutSeconds)
{
    AppKit.EnsureApplication();

    var enumerator = new MacDisplayEnumerator();
    var settings = new AppSettings
    {
        IdleTimeoutSeconds = timeoutSeconds,
        NeverBlankDuringFullscreen = false,   // a fullscreen terminal would block the demo
        // Manage every display for the demo — the real app reads the settings file.
        ManagedDisplayIds = enumerator.Enumerate().Select(d => d.StableId).ToList(),
    };

    Console.WriteLine($"Running the Core BlankingEngine + overlays with a {timeoutSeconds}s timeout. Ctrl+C to stop.");
    Console.WriteLine($"Managing {settings.ManagedDisplayIds.Count} display(s). Leave the machine untouched to watch");
    Console.WriteLine("them blank to true black; move the mouse to wake them.");

    using var overlays = new OverlayManager(settings, enumerator, new MacOverlayFactory());
    overlays.Refresh();

    using var engine = new BlankingEngine(settings, MacPlatform.CreateEnginePlatform());
    engine.VideoOverlayVisible = () => overlays.AnyVideoVisible;
    engine.RequesterSnapshot = MacPowerAssertions.Query;

    engine.BlankStateChanged += blanked =>
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] >>> blank state: {(blanked ? "ON — covering displays" : "OFF — overlays hidden")}");
        if (blanked) overlays.ShowAll();
        else overlays.HideAll();
    };

    var lastLine = "";
    engine.StatusChanged += s =>
    {
        var line = s.Blanked
            ? "BLANKED"
            : $"awake ({App.Describe(s.Reason)}) — idle {s.Idle.TotalSeconds:F0}s, blanks in {s.UntilBlank.TotalSeconds:F0}s" +
              (s.Exec.DisplayRequired ? "  [display held]" : "");

        if (line == lastLine) return;
        lastLine = line;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    };

    engine.Start();
    CF.CFRunLoopRun();
}

static void Overlay(string mode, int seconds, string? videoPath)
{
    AppKit.EnsureApplication();

    var cfg = mode switch
    {
        "dim" => new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 50 },
        "video" => new MonitorConfig { Mode = OverlayMode.Video, VideoPath = videoPath, VideoStretch = VideoStretch.Fit },
        _ => new MonitorConfig { Mode = OverlayMode.TrueBlack },
    };

    var enumerator = new MacDisplayEnumerator();
    var displays = enumerator.Enumerate();

    var settings = new AppSettings { ManagedDisplayIds = displays.Select(d => d.StableId).ToList() };
    settings.ApplyGlobal(cfg);

    Console.WriteLine($"Showing {cfg.Summary} overlay on {displays.Count} display(s) for {seconds}s…");

    using var overlays = new OverlayManager(settings, enumerator, new MacOverlayFactory());
    overlays.Refresh();
    overlays.ShowAll();

    // One-shot: verify placement against the window server's records, then stop.
    using var stopTimer = new MacRunLoopTimer(TimeSpan.FromSeconds(seconds), () =>
    {
        Verify.VerifyPlacement(displays);
        CF.CFRunLoopStop(CF.CFRunLoopGetMain());
    });
    stopTimer.Start();
    CF.CFRunLoopRun();

    overlays.HideAll();
    Console.WriteLine($"done — covered {string.Join(", ", displays.Select(d => d.FriendlyName))}, torn down clean");
}

internal static class Verify
{
    /// <summary>
    /// The mac twin of the Windows selftest's GetWindowRect check
    /// (SelfTest.OverlayPlacement): ask the window server for this process's
    /// screensaver-level windows and compare their bounds — reported in the same
    /// top-left CG space as CGDisplayBounds — against each display.
    /// </summary>
    internal static void VerifyPlacement(IReadOnlyList<MonitorScreenSaver.Core.DisplayTarget> displays)
    {
        var pid = Environment.ProcessId;
        var mine = new List<CG.CGRect>();

        var list = CG.CGWindowListCopyWindowInfo(CG.kCGWindowListOptionOnScreenOnly, 0);
        if (list == IntPtr.Zero)
        {
            Console.WriteLine("  [FAIL] CGWindowListCopyWindowInfo returned nothing");
            return;
        }

        try
        {
            var n = CF.CFArrayGetCount(list);

            for (nint i = 0; i < n; i++)
            {
                var win = CF.CFArrayGetValueAtIndex(list, i);
                if (win == IntPtr.Zero) continue;
                if (CF.NumberToLong(CF.CFDictionaryGetValue(win, CG.WindowOwnerPidKey)) != pid) continue;
                if (CF.NumberToLong(CF.CFDictionaryGetValue(win, CG.WindowLayerKey)) != AppKit.ScreenSaverWindowLevel) continue;

                var boundsDict = CF.CFDictionaryGetValue(win, CG.WindowBoundsKey);
                if (boundsDict != IntPtr.Zero && CG.CGRectMakeWithDictionaryRepresentation(boundsDict, out var r))
                    mine.Add(r);
            }
        }
        finally
        {
            CF.CFRelease(list);
        }

        foreach (var d in displays)
        {
            var covered = mine.Any(r =>
                Math.Abs(r.X - d.Bounds.Left) < 1 && Math.Abs(r.Y - d.Bounds.Top) < 1 &&
                Math.Abs(r.Width - d.Bounds.Width) < 1 && Math.Abs(r.Height - d.Bounds.Height) < 1);

            Console.WriteLine($"  [{(covered ? "PASS" : "FAIL")}] {d.FriendlyName}: overlay at screensaver level covers {d.Geometry}");
        }
    }
}

internal static class App
{
    internal static string Describe(AwakeReason reason) => reason switch
    {
        AwakeReason.UserInput => "input",
        AwakeReason.ForegroundChange => "window focus",
        AwakeReason.DisplayRequest => "app request",
        AwakeReason.Fullscreen => "fullscreen",
        AwakeReason.Audio => "audio",
        AwakeReason.Resumed => "resumed",
        AwakeReason.Paused => "paused",
        _ => "idle",
    };
}
