using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac;
using MonitorScreenSaver.Mac.Interop;

// Phase 2 harness: exercises every macOS platform service against the live machine,
// the same role the Windows head's --selftest/--watch play. The tray app (NSStatusItem),
// overlays and settings window arrive in later phases and will replace this Main.
//
//   status [n]     poll idle/exec/holders/audio/fullscreen every second, n times (default 5)
//   displays       enumerate displays with stable ids
//   assertions     dump the holder list (compare with: pmset -g assertions)
//   watch          log sleep/wake, topology and lock/unlock events until killed
//   engine [sec]   run the real Core BlankingEngine with a [sec] idle timeout (default 15)

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

    default:
        Console.WriteLine("usage: MonitorScreenSaverMac [status [n] | displays | assertions | watch | engine [timeoutSeconds]]");
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
    var settings = new AppSettings
    {
        IdleTimeoutSeconds = timeoutSeconds,
        NeverBlankDuringFullscreen = false,   // a fullscreen terminal would block the demo
    };

    Console.WriteLine($"Running the Core BlankingEngine with a {timeoutSeconds}s timeout. Ctrl+C to stop.");
    Console.WriteLine("Leave the machine untouched to watch it blank; move the mouse to wake it.");

    using var engine = new BlankingEngine(settings, MacPlatform.CreateEnginePlatform());

    var lastLine = "";
    engine.StatusChanged += s =>
    {
        var line = s.Blanked
            ? "BLANKED — overlays would be covering the managed displays now"
            : $"awake ({App.Describe(s.Reason)}) — idle {s.Idle.TotalSeconds:F0}s, blanks in {s.UntilBlank.TotalSeconds:F0}s" +
              (s.Exec.DisplayRequired ? "  [display held]" : "");

        if (line == lastLine) return;
        lastLine = line;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    };

    engine.BlankStateChanged += blanked =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] >>> blank state: {(blanked ? "ON" : "OFF")}");

    engine.Start();
    CF.CFRunLoopRun();
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
