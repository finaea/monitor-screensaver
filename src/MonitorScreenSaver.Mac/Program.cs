using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac;
using MonitorScreenSaver.Mac.Interop;

// Command line for the macOS head. "tray" is the real app; the rest are the diagnostics
// that play the same role as the Windows head's --selftest/--watch, plus the per-service
// harnesses the port was built against (they stay: each one isolates a single platform
// service, which is how every macOS surprise in MACOS-PORT-PLAN.md was pinned down).
//
//   tray               run the real app: menu bar item + engine + overlays (default)
//   settings           the real app, with the settings window opened at launch
//   selftest [path]    run every detection path against this machine and report
//   status [n]         poll idle/exec/holders/audio/fullscreen every second, n times (default 5)
//   displays           enumerate displays with stable ids
//   assertions         dump the holder list (compare with: pmset -g assertions)
//   watch [path]       timestamped log of power/topology/session events + engine-input
//                      heartbeat, to a file (default: <settings dir>/watch.log)
//   hotkey [combo]     register the "blank now" shortcut (default: the configured one) and
//                      log every press, without blanking anything — the only way to test
//                      delivery, since a successful registration proves nothing on macOS
//   engine [sec]       run the real Core BlankingEngine + overlays: an actual working
//                      screensaver loop with a [sec] idle timeout (default 15)
//   overlay <mode> [sec] [videoPath]
//                      show a black|dim|video overlay on every display for [sec]
//                      seconds (default 3), then tear down — the Phase 3 smoke test

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "tray";

switch (command)
{
    case "tray":
        new MacApp().Run();
        break;

    case "settings":
        new MacApp().Run(openSettings: true);
        break;

    case "selftest":
        return MacSelfTest.Run(args.Length > 1 ? args[1] : null);

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
        MacWatchMode.Start(args.Length > 1 ? args[1] : null);
        break;

    case "hotkey":
        Hotkey(args.Length > 1 ? args[1] : null);
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
        Console.WriteLine("usage: MonitorScreenSaverMac [tray | settings | selftest [path] | status [n] | displays |");
        Console.WriteLine("                             assertions | watch [path] | hotkey [combo] |");
        Console.WriteLine("                             engine [timeoutSeconds] |");
        Console.WriteLine("                             overlay <black|dim|video> [seconds] [videoPath]]");
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

/// <summary>
/// Holds the shortcut and reports every press. Separate from the real app on purpose: the
/// action here is a printed line rather than blanking the screens, which makes the delivery
/// path testable at any time — including with a synthetic key event — without covering
/// someone's displays to find out.
/// </summary>
static void Hotkey(string? combo)
{
    AppKit.EnsureApplication();

    var text = combo ?? AppSettings.Load().BlankNowHotkey;

    if (!HotkeySpec.TryParse(text, out var spec) || spec is null)
    {
        Console.WriteLine(string.IsNullOrWhiteSpace(text)
            ? "No shortcut configured. Pass one, e.g.: hotkey Ctrl+Alt+Shift+B"
            : $"Could not parse \"{text}\". Try something like Ctrl+Alt+Shift+B.");
        return;
    }

    var presses = 0;
    using var hotkey = new MacHotkey(() =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] fired — press #{++presses} " +
                          "(the real app blanks here)"));

    if (hotkey.Blocker(spec) is { } blocker)
        Console.WriteLine($"pre-flight: {spec.Display()} would be refused — {blocker}");

    var status = hotkey.Apply(spec);
    Console.WriteLine($"{spec} ({spec.Display()}): {status.State} — {status.Detail}");

    if (status.State != HotkeyState.Active) return;

    Console.WriteLine("Press it. Ctrl+C to stop.");

    // [NSApp run], not CFRunLoopRun: hot key presses arrive as Carbon events on the
    // application event target, and it is NSApplication's loop that drains that queue.
    // Measured — with a bare CFRunLoopRun the registration succeeds and nothing is ever
    // delivered, which is exactly the failure this command exists to catch.
    ObjC.SendVoid(ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication")), ObjC.Sel("run"));
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
            : $"awake ({MacApp.Describe(s.Reason)}) — idle {s.Idle.TotalSeconds:F0}s, blanks in {s.UntilBlank.TotalSeconds:F0}s" +
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

