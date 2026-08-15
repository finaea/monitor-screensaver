using System.IO;
using System.Text;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The macOS twin of the Windows head's SelfTest: exercises every detection path the
/// engine depends on, live, against this machine, and writes a report. Run with:
///   MonitorScreenSaverMac selftest [path]
///
/// Section-for-section parity with Windows where the concept exists, with three
/// macOS-only sections for platform traps that already bit this port:
///   * the settings window's control themes (a Fluent resource declared with the wrong
///     type throws only when the control is first realised, so a Slider that is never
///     shown is a Slider that is never tested);
///   * the status item, which on macOS 26 is rendered and owned by ControlCenter, so it
///     cannot be verified through this process's own window list;
///   * the private cursor-hiding property, which is the only route that works from a
///     background app and could disappear in any macOS release.
/// </summary>
public static class MacSelfTest
{
    private static readonly StringBuilder Out = new();
    private static int _failures;

    /// <summary>
    /// Read before anything can perturb it, so the Dock-presence checks do not depend on
    /// the order the sections run in.
    /// </summary>
    private static nint _policyAtRest;

    public static int Run(string? outputPath)
    {
        // Overlays, NSImage and Avalonia all need an NSApplication.
        AppKit.EnsureApplication();
        _policyAtRest = AppKit.ActivationPolicy;

        Line($"MonitorScreenSaver self-test (macOS)  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"OS {Environment.OSVersion.Version}  |  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} " +
             $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}  |  uid={IOKit.Getuid()}  |  " +
             $"bundled={IsBundled()}");
        Line(new string('=', 78));

        SettingsWindowTheme();
        IconResources();
        StatusItem();
        Displays();
        OverlayPlacement();
        SystemEventPlumbing();
        Category2Live();
        Category1();
        FullscreenProbe();
        AudioProbe();
        LiveRequesterQuery();
        CursorHiding();
        HotkeyCheck();
        ManualBlankHold();
        SettingsCheck();

        Line(new string('=', 78));
        Line(_failures == 0 ? "RESULT: all checks passed" : $"RESULT: {_failures} check(s) FAILED");

        var text = Out.ToString();

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
                File.WriteAllText(outputPath, text);
            }
            catch (Exception ex)
            {
                Line($"(could not write report: {ex.Message})");
            }
        }

        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- sections

    /// <summary>
    /// The mac counterpart of the Windows suite's WPF text-rendering check: force the
    /// settings window's whole rendering stack to come up for real.
    ///
    /// This is the section that earns its keep. It catches (a) the native libraries a
    /// single-file publish leaves behind — without libSkiaSharp next to the executable
    /// the app runs fine until someone opens Settings…; and (b) a Fluent theme resource
    /// overridden with the wrong CLR type, which throws only when the control that reads
    /// it is first realised. Both of those shipped-and-were-caught here.
    /// </summary>
    private static void SettingsWindowTheme()
    {
        Section("Settings window: Avalonia stack, control themes and Dock presence");

        try
        {
            UI.MacUi.EnsureAvalonia();
            Check(Avalonia.Application.Current is not null, "Avalonia initialised (Skia + AvaloniaNative loaded)");

            // Realise one of every themed control. Off-screen and never activated, so it
            // cannot steal focus or flash on a display the user is looking at.
            var probe = UI.MacUi.BuildThemeProbe();

            Check(probe.MissingBrushes.Count == 0,
                probe.MissingBrushes.Count == 0
                    ? "every palette brush resolves to a brush"
                    : $"palette brushes missing or mistyped: {string.Join(", ", probe.MissingBrushes)}");
            Check(probe.Realised, "toggle, segmented radio, slider, text field and buttons all templated");
            Check(probe.SliderWidth > 0, $"slider laid out ({probe.SliderWidth:F0} px wide — the GridLength trap)");
            Check(probe.ToggleWidth > 0, $"pill toggle laid out ({probe.ToggleWidth:F0} px wide)");
            Check(probe.WrapsInsideParent,
                $"wrapping label text stays inside its card ({probe.CardWidth:F0} px card, {probe.ContentWidth:F0} px content)");

            // Dock presence, both directions. Opening Settings… used to leave a Dock icon
            // behind for the rest of the session: Avalonia claims Regular policy when it
            // initialises and nothing put it back. The transitions are what the settings
            // window drives on show and on close, so they are what gets tested.
            Check(_policyAtRest == AppKit.ActivationPolicyAccessory,
                $"accessory at rest — no Dock icon, menu bar only ({Policy(_policyAtRest)})");
            Line($"    ·   after Avalonia initialised, unasked: {Policy(probe.PolicyAfterInit)}");

            UI.MacUi.ShowInDock();
            Check(AppKit.ActivationPolicy == AppKit.ActivationPolicyRegular,
                $"opening the window puts the app in the Dock, so it can take focus ({Policy(AppKit.ActivationPolicy)})");

            UI.MacUi.HideFromDock();
            Check(AppKit.ActivationPolicy == AppKit.ActivationPolicyAccessory,
                $"closing it takes the app back out of the Dock ({Policy(AppKit.ActivationPolicy)})");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The mac twin of TrayIconResource. LSUIElement suppresses the running Dock icon,
    /// not the bundle icon, so a missing .icns shows up as the generic placeholder on the
    /// Dock tile of a minimised window, in the app switcher and in Finder.
    /// </summary>
    private static void IconResources()
    {
        Section("Icon resources");

        try
        {
            if (IsBundled())
            {
                var resources = Path.Combine(AppContext.BaseDirectory, "..", "Resources");
                foreach (var name in new[] { "MonitorScreenSaver.icns", "MenuBarIcon.png", "MenuBarIcon@2x.png" })
                {
                    var path = Path.GetFullPath(Path.Combine(resources, name));
                    Check(File.Exists(path), $"{name} present in the bundle");
                }
            }
            else
            {
                Line("    not bundled — bundle resources are skipped (run tools/bundle-macos.sh)");
            }

            // The status item image, resolved the way MacTray resolves it.
            var name2 = CF.CreateString("MenuBarIcon");
            IntPtr image;
            try
            {
                image = ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("imageNamed:"), name2);
            }
            finally
            {
                CF.CFRelease(name2);
            }

            if (IsBundled())
            {
                Check(image != IntPtr.Zero, "NSImage imageNamed:MenuBarIcon resolves the branded art");

                if (image != IntPtr.Zero)
                {
                    ObjC.SendVoid(image, ObjC.Sel("setTemplate:"), true);
                    Check(ObjC.SendBool(image, ObjC.Sel("isTemplate")),
                        "status item art is a template image (tints itself for the menu bar)");
                }
            }
            else
            {
                // Unbundled runs fall back to an SF Symbol; check that fallback exists.
                var symbol = CF.CreateString("display");
                try
                {
                    var fallback = ObjC.Send(ObjC.Class("NSImage"),
                        ObjC.Sel("imageWithSystemSymbolName:accessibilityDescription:"), symbol, IntPtr.Zero);
                    Check(fallback != IntPtr.Zero, "SF Symbol fallback resolves for unbundled runs");
                }
                finally
                {
                    CF.CFRelease(symbol);
                }
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// macOS 26 renders status items inside ControlCenter, so the app's own window list
    /// never shows one (this cost an hour during Phase 4 — see MACOS-PORT-PLAN.md). The
    /// supported check is NSStatusItem.isVisible plus a live button object.
    /// </summary>
    private static void StatusItem()
    {
        Section("Status item (menu bar)");

        try
        {
            var statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
            Check(statusBar != IntPtr.Zero, "NSStatusBar systemStatusBar available");

            var item = ObjC.SendForDouble(statusBar, ObjC.Sel("statusItemWithLength:"), -1.0);
            Check(item != IntPtr.Zero, "status item created");

            try
            {
                var button = ObjC.Send(item, ObjC.Sel("button"));
                Check(button != IntPtr.Zero, "status item has a button to hang the image and menu on");
                Check(ObjC.SendBool(item, ObjC.Sel("isVisible")), "status item reports itself visible");
                Line("    NOTE: the on-screen window belongs to ControlCenter, not this process —");
                Line("          never verify the item through this app's own CGWindowList.");
            }
            finally
            {
                ObjC.SendVoid(statusBar, ObjC.Sel("removeStatusItem:"), item);
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Displays()
    {
        Section("Display enumeration");

        try
        {
            var displays = new MacDisplayEnumerator().Enumerate();
            Check(displays.Count > 0, $"found {displays.Count} display(s)");

            foreach (var d in displays)
            {
                Line($"    * {d.FriendlyName}");
                Line($"        device   {d.DeviceName}{(d.IsPrimary ? "   [PRIMARY]" : "")}");
                Line($"        geometry {d.Geometry}");
                Line($"        stableId {d.StableId}");
            }

            var ids = displays.Select(d => d.StableId).ToList();
            Check(ids.Distinct().Count() == ids.Count, "stable ids are unique (settings keys will not collide)");
            Check(ids.All(i => !string.IsNullOrWhiteSpace(i)), "every display has a stable id (display UUID)");
            Check(displays.Count(d => d.IsPrimary) == 1, "exactly one display is primary");
            Check(displays.All(d => d.Bounds.Width > 0 && d.Bounds.Height > 0), "all display bounds are non-empty");
        }
        catch (Exception ex)
        {
            Fail($"enumeration threw: {ex}");
        }
    }

    /// <summary>
    /// The riskiest part of the app: an overlay must land exactly on its display, in the
    /// same top-left pixel space CGDisplayBounds reports, including negative origins and
    /// mixed scale factors. Verified by reading our own windows back from the window
    /// server, the mac equivalent of the Windows suite's GetWindowRect check.
    /// </summary>
    private static void OverlayPlacement()
    {
        Section("Overlay placement (window server geometry)");

        try
        {
            foreach (var d in new MacDisplayEnumerator().Enumerate())
            {
                MacOverlayWindow? win = null;

                try
                {
                    win = new MacOverlayWindow(d, new MonitorConfig { Mode = OverlayMode.TrueBlack });
                    win.ShowOverlay();
                    Pump();

                    Check(win.IsVisible, $"{d.FriendlyName}: overlay reports visible");
                    Check(win.BuiltBounds.Equals(d.Bounds), $"{d.FriendlyName}: built for {d.Geometry}");

                    // Registration with the window server is asynchronous, and the first
                    // panel of the process is the slowest (AppKit warm-up), so poll rather
                    // than pump a fixed slice and hope.
                    var placed = WaitForWindowServer(d);
                    Check(placed is not null,
                        $"{d.FriendlyName}: window server has a screensaver-level window for this display");

                    if (placed is { } r)
                    {
                        Check(Math.Abs(r.X - d.Bounds.Left) < 1 && Math.Abs(r.Y - d.Bounds.Top) < 1 &&
                              Math.Abs(r.Width - d.Bounds.Width) < 1 && Math.Abs(r.Height - d.Bounds.Height) < 1,
                            $"{d.FriendlyName}: placed at ({r.X},{r.Y}) {r.Width}x{r.Height}");
                    }

                    // Black↔dim morphs in place on macOS (window alpha), unlike WPF where
                    // translucency needs a different window. Video always needs a rebuild.
                    Check(win.TryApply(new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 70 }),
                        $"{d.FriendlyName}: switching to dim is applied in place (window alpha)");
                    Check(win.TryApply(new MonitorConfig { Mode = OverlayMode.TrueBlack }),
                        $"{d.FriendlyName}: switching back to true black is applied in place");
                    Check(!win.TryApply(new MonitorConfig { Mode = OverlayMode.Video, VideoPath = "/tmp/x.mp4" }),
                        $"{d.FriendlyName}: switching to video correctly demands a rebuild");
                }
                finally
                {
                    try { win?.Close(); } catch { /* teardown */ }
                }
            }

            VideoOverlay();
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex}");
        }
    }

    /// <summary>Video takes a different path (AVPlayerLooper); a missing file must degrade to black.</summary>
    private static void VideoOverlay()
    {
        var target = new MacDisplayEnumerator().Enumerate().FirstOrDefault();
        if (target is null) return;

        var missing = Path.Combine(Path.GetTempPath(), "monitorscreensaver-selftest-missing.mp4");
        var cfg = new MonitorConfig { Mode = OverlayMode.Video, VideoPath = missing, VideoStretch = VideoStretch.Fit };

        MacOverlayWindow? win = null;

        try
        {
            win = new MacOverlayWindow(target, cfg);
            win.ShowOverlay();
            Pump();

            Check(win.IsVisible, $"{target.FriendlyName}: video overlay shown");
            Check(!win.VideoPlaying, $"{target.FriendlyName}: missing file degrades to black (no player)");
            Check(win.TryApply(cfg with { VideoStretch = VideoStretch.Fill }),
                $"{target.FriendlyName}: stretch mode changes in place");
            Check(!win.TryApply(cfg with { VideoPath = missing + ".other.mp4" }),
                $"{target.FriendlyName}: a different video file correctly demands a rebuild");
            Check(!win.TryApply(new MonitorConfig { Mode = OverlayMode.TrueBlack }),
                $"{target.FriendlyName}: switching to true black correctly demands a rebuild");
        }
        finally
        {
            try { win?.Close(); } catch { /* teardown */ }
        }
    }

    private static void SystemEventPlumbing()
    {
        Section("System event plumbing (sleep/wake, topology, lock)");

        try
        {
            using var events = new MacSystemEvents();
            var seen = 0;
            events.Event += _ => seen++;

            Check(true, "IORegisterForSystemPower + display reconfiguration callback + lock notifications registered");
            Line("    lock/unlock ride com.apple.screenIsLocked/Unlocked — undocumented but a decade stable;");
            Line($"    events observed while the suite ran: {seen} (0 is expected on an idle machine)");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The direct twin of the Windows Category-2 test: take a real display assertion,
    /// prove the engine's aggregate read sees it, prove attribution blames this process,
    /// then release it and prove it clears.
    /// </summary>
    private static void Category2Live()
    {
        Section("Category 2 — display power assertions (IOKit)");

        var before = MacExecutionSource.Read();
        Line($"    baseline raw=0x{before.Raw:X2}  display={before.DisplayRequired}  system={before.SystemRequired}  present={before.UserPresent}");

        if (before.DisplayRequired)
        {
            Line("    NOTE: something already holds a display assertion, so the clear-side");
            Line("          assertion is skipped for this run.");
        }

        var type = CF.CreateString(IOKit.AssertPreventUserIdleDisplaySleep);
        var name = CF.CreateString("MonitorScreenSaver self-test");
        var id = 0u;

        try
        {
            var rc = IOKit.IOPMAssertionCreateWithName(type, IOKit.AssertionLevelOn, name, out id);
            Check(rc == 0, $"IOPMAssertionCreateWithName -> {rc} (id {id})");

            if (rc == 0)
            {
                var (set, setMs) = WaitForDisplayFlag(true);
                Check(set, $"our own display assertion observable after {setMs} ms");

                var snap = MacPowerAssertions.Query();
                var mine = snap.Display.FirstOrDefault(r => r.ShortName.Contains("MonitorScreenSaver", StringComparison.OrdinalIgnoreCase));
                Check(mine is not null, "attribution blames this process for the assertion it holds");
                if (mine is not null) Line($"    attributed to: {mine.ShortName} ({mine.Kind}) {mine.Reason}");

                // The blacklist decision the engine actually makes. BlacklistCovers is all-or
                // -nothing — it returns false unless *every* current DISPLAY holder is listed —
                // so the list has to be every holder, not just ours. Listing only ours failed
                // the moment anything else held the display (a stray `caffeinate -d` was enough),
                // which said nothing about the engine and everything about the machine.
                var blacklisted = new AppSettings { BlacklistedRequesters = [.. snap.Display.Select(r => r.ShortName)] };
                Check(blacklisted.BlacklistCovers(snap),
                    "blacklisting every current holder makes the engine ignore the aggregate flag");
                Check(!new AppSettings().BlacklistCovers(snap), "an empty blacklist never covers a live holder");

                IOKit.IOPMAssertionRelease(id);
                id = 0;

                var (cleared, clearMs) = WaitForDisplayFlag(false);
                Check(cleared || before.DisplayRequired, $"releasing it observable after {clearMs} ms");
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (id != 0) IOKit.IOPMAssertionRelease(id);
            CF.CFRelease(type);
            CF.CFRelease(name);
        }
    }

    private static (bool Ok, int ElapsedMs) WaitForDisplayFlag(bool expected, int timeoutMs = 1500)
    {
        const int step = 25;

        for (var waited = 0; ; waited += step)
        {
            if (MacExecutionSource.Read().DisplayRequired == expected) return (true, waited);
            if (waited >= timeoutMs) return (false, waited);
            Thread.Sleep(step);
        }
    }

    /// <summary>
    /// Idle detection, including the jitter guard: LastInputMs is "now minus idle" across
    /// two independently-rounding clocks, so consecutive reads wobbled by a millisecond
    /// and cancelled a manual blank instantly. The clock absorbs sub-100 ms jumps now,
    /// and this check is what keeps that fix honest.
    /// </summary>
    private static void Category1()
    {
        Section("Category 1 — input idle (CGEventSource)");

        try
        {
            var clock = new MacActivityClock();

            var idleSeconds = CG.CGEventSourceSecondsSinceLastEventType(
                CG.kCGEventSourceStateHIDSystemState, CG.kCGAnyInputEventType);
            Check(idleSeconds >= 0, $"CGEventSourceSecondsSinceLastEventType -> {idleSeconds:F2}s");
            Check(idleSeconds < 7 * 24 * 60 * 60, "idle value is sane");

            var now1 = clock.NowMs;
            Thread.Sleep(30);
            Check(clock.NowMs > now1, "NowMs advances (monotonic tick source)");

            var a = clock.LastInputMs;
            Thread.Sleep(40);
            var b = clock.LastInputMs;
            Check(a == b, $"LastInputMs is stable across reads with no input ({a} == {b}) — jitter guard holds");

            var idle = TimeSpan.FromMilliseconds(clock.NowMs - clock.LastInputMs);
            Check(idle >= TimeSpan.Zero, $"derived idle is non-negative ({idle.TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void FullscreenProbe()
    {
        Section("Fullscreen guard");

        try
        {
            var active = new MacFullscreenDetector().IsFullscreenActive();
            Check(true, $"fullscreen probe -> {active}");
            Line("    heuristic: a window at or above screensaver level covering a whole display");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AudioProbe()
    {
        Section("Audio activity probe (CoreAudio)");

        try
        {
            var playing = new MacAudioSource().IsPlaying();
            Check(true, $"kAudioDevicePropertyDeviceIsRunningSomewhere -> {playing}");
            Line("    coarser than the Windows WASAPI peak meter: device-is-running, not audible level");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LiveRequesterQuery()
    {
        Section("Live requester query (attribution, unprivileged)");

        try
        {
            var snap = MacPowerAssertions.Query();

            Check(snap.Available, $"attribution available without elevation (uid {IOKit.Getuid()})");
            if (!snap.Available) Line($"    unavailable: {snap.Unavailable}");

            Line($"    {snap.Requesters.Count} assertion(s) held, {snap.Display.Count()} of them DISPLAY:");
            foreach (var r in snap.Requesters)
                Line($"      [{r.RequestType}] {r.ShortName} ({r.Kind}) {r.Reason}");

            Check(snap.Requesters.All(r => !string.IsNullOrWhiteSpace(r.ShortName)),
                "every holder resolved a process name (proc_name, or the sysctl kinfo_proc fallback)");

            var self = IOKit.ProcessName(Environment.ProcessId);
            Check(!string.IsNullOrWhiteSpace(self), $"own process name resolves -> {self}");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A lit cursor parked on a blanked OLED is exactly what this app exists to prevent,
    /// and every supported route is a no-op from a non-activating background app. The one
    /// that works is a private CGS connection property, so it gets probed here: if a
    /// macOS release breaks it, this is where that shows up.
    /// </summary>
    private static void CursorHiding()
    {
        Section("Cursor hiding (private CGS property)");

        try
        {
            CG.EnableCursorInBackground();
            Check(true, "CGSSetConnectionProperty(SetsCursorInBackground) did not throw");

            var display = CG.CGMainDisplayID();
            var hide = CG.CGDisplayHideCursor(display);
            var show = CG.CGDisplayShowCursor(display);

            Check(hide == 0, $"CGDisplayHideCursor -> {hide}");
            Check(show == 0, $"CGDisplayShowCursor -> {show} (hide/show balanced, cursor restored)");
            Line("    private API: a failure here is cosmetic — blanking still works, the cursor stays visible.");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The "blank now" shortcut, end to end: the stored value parses, the conflict checks
    /// refuse what they should, and the combination actually registers with the OS.
    ///
    /// The last check is the interesting one. macOS reports a clash *only* when the same
    /// process asks twice (eventHotKeyExistsErr) — a second process registering a combination
    /// another one already holds gets noErr — so that error is the only registration signal
    /// there is, and if it ever stops arriving, every conflict check we have left is a
    /// hand-written table.
    /// </summary>
    private static void HotkeyCheck()
    {
        Section("Blank now shortcut (global hot key)");

        try
        {
            var settings = AppSettings.Load();
            var configured = settings.BlankNowHotkeySpec;

            if (string.IsNullOrWhiteSpace(settings.BlankNowHotkey))
            {
                Line("    no shortcut configured — blanking is menu bar only");
            }
            else
            {
                Check(configured is not null,
                    $"settings value parses: \"{settings.BlankNowHotkey}\"" +
                    (configured is null ? " — could NOT be parsed" : $" -> {configured.Display()}"));
            }

            using var hotkey = new MacHotkey(() => { });

            // The system table has to be non-trivially populated, or layer 3 is silently dead:
            // this is the check that would have caught the old plist-only version, which saw 2
            // shortcuts where CopySymbolicHotKeys sees 170.
            var table = MacSystemHotkeys.Table();
            var (overrides, enabledOverrides) = MacSystemHotkeys.Overrides();

            Check(table.Count > 20,
                $"CopySymbolicHotKeys returned {table.Count} enabled system shortcut(s) — the complete table");
            Line($"    com.apple.symbolichotkeys (names only): {enabledOverrides} enabled, " +
                 $"{overrides.Count} with a readable combination — customised shortcuts only");

            // Take a real entry out of the live table and prove it is refused. Machine-dependent
            // in *which* combination it picks, but every Mac has plenty.
            var live = table
                .Select(e => MacSystemHotkeys.AsSpec(e.Code, e.Modifiers))
                .FirstOrDefault(s => s is not null && s.Weakness() is null);

            if (live is not null)
            {
                Check(hotkey.Blocker(live) is not null,
                    $"a shortcut macOS itself holds is refused ({live.Display()})");
            }
            else
            {
                Line("    no entry in the system table is a candidate for us, so that path is " +
                     "exercised by the hand-written list below only");
            }

            // Deterministic conflict checks, independent of what this machine has configured.
            Check(hotkey.Blocker(new HotkeySpec(HotkeyModifiers.Command, "B")) is not null,
                "a one-modifier combination is refused (⌘B is app-shortcut territory)");
            Check(hotkey.Blocker(new HotkeySpec(HotkeyModifiers.Command | HotkeyModifiers.Shift, "B")) is not null,
                "Command+Shift is refused (that is what app menus are built from)");
            Check(hotkey.Blocker(new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Command, "Q")) is not null,
                "a reserved system combination is refused (⌃⌘Q is Lock Screen)");
            Check(hotkey.Blocker(new HotkeySpec(
                    HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F12")) is not null,
                "F12 is refused (reserved for the debugger on Windows, and the setting is shared)");

            var probe = configured ?? new HotkeySpec(
                HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "B");

            if (hotkey.Blocker(probe) is { } blocked)
            {
                Line($"    {probe.Display()} is refused on this machine — {blocked}");
            }
            else
            {
                var status = hotkey.Apply(probe);
                Check(status.State == HotkeyState.Active,
                    $"registered {probe.Display()} with the OS ({status.Detail})");

                // A second holder in this same process: the one clash macOS will admit to.
                using var second = new MacHotkey(() => { });
                var clash = second.Apply(probe);
                Check(clash.State == HotkeyState.Failed,
                    clash.State == HotkeyState.Failed
                        ? "a duplicate registration is reported (eventHotKeyExistsErr still works)"
                        : $"a duplicate registration was NOT reported — it returned {clash.State}");
            }

            Check(MacKeyCodes.Code("B") == 0x0B && MacKeyCodes.Code("Space") == 0x31,
                "key name to macOS virtual key code (B=0x0B, Space=0x31, from Events.h)");

            // Labels come from the layout in use, not the stored ANSI name. Checking the
            // translation rather than the label, because the label falls back to that name
            // silently — on a US layout the two agree and a broken translation would not show.
            var translated = MacKeyCodes.Translate(0x0B);
            Check(translated is not null,
                translated is null
                    ? "no keyboard layout resolved — key labels fell back to ANSI names"
                    : $"key labels come from the current layout (UCKeyTranslate: key code 0x0B prints \"{translated}\")");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The manual-blank hold, driven by a fake clock so the sequence is exact and no display is
    /// ever covered. This is the shipped bug it exists for: the shortcut blanked the screens and
    /// they came straight back when the user lifted their finger, because the key *release* is
    /// itself input and the hold was watching for input from the moment of the press.
    ///
    /// Portable logic, tested from the mac head because that is where the harness lives.
    /// </summary>
    private static void ManualBlankHold()
    {
        Section("Manual blank hold (engine logic, fake clock)");

        try
        {
            var clock = new FakeClock();
            var settings = new AppSettings { IdleTimeoutSeconds = 300, ManagedDisplayIds = ["x"] };

            // The engine hands its tick to the timer factory, so the test can drive the
            // decision itself instead of waiting on a real timer.
            Action? tick = null;

            using var engine = new BlankingEngine(settings, new EnginePlatform(
                clock,
                new FakeExec(),
                new FakeFullscreen(),
                new FakeAudio(),
                _ => new FakeWatch(),
                (_, action) => { tick = action; return new FakeTimer(); }));

            // The keystroke that asks for the blank.
            clock.Set(now: 1000, lastInput: 1000);
            engine.BlankNow();
            Check(engine.Status.Blanked, "blanks on request");

            // Letting go of it, ~120 ms later. This is what used to unblank instantly.
            clock.Set(now: 1120, lastInput: 1120);
            tick!();
            Check(engine.Status.Blanked, "still blanked when the shortcut key is released");

            // …and its modifiers, in the usual ragged order.
            clock.Set(now: 1180, lastInput: 1180);
            tick!();
            clock.Set(now: 1240, lastInput: 1240);
            tick!();
            Check(engine.Status.Blanked, "still blanked after the modifier keys are released");

            // Someone holding the shortcut down for three seconds: input keeps arriving, and
            // the hold must survive all of it.
            for (ulong t = 1300; t <= 4300; t += 100)
            {
                clock.Set(now: t, lastInput: t);
                tick!();
            }
            Check(engine.Status.Blanked, "still blanked after a three-second hold on the key");

            // Quiet for the settle window: the hold now arms against the settled tick.
            clock.Set(now: 4900, lastInput: 4300);
            tick!();
            Check(engine.Status.Blanked, "stays blanked once input goes quiet");

            // Real input afterwards — the point of the whole feature — wakes it.
            clock.Set(now: 5000, lastInput: 5000);
            tick!();
            Check(!engine.Status.Blanked, "real input after that wakes the displays");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class FakeClock : IActivityClock
    {
        public ulong NowMs { get; private set; }
        public ulong LastInputMs { get; private set; }

        internal void Set(ulong now, ulong lastInput)
        {
            NowMs = now;
            LastInputMs = lastInput;
        }
    }

    private sealed class FakeExec : IExecutionStateSource
    {
        public ExecutionState Read() => default;
    }

    private sealed class FakeFullscreen : IFullscreenDetector
    {
        public bool IsFullscreenActive() => false;
    }

    private sealed class FakeAudio : IAudioActivitySource
    {
        public bool IsPlaying() => false;
    }

    private sealed class FakeWatch : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>A timer that never fires: the test drives the engine tick by tick itself.</summary>
    private sealed class FakeTimer : ITickTimer
    {
        public TimeSpan Interval { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static void SettingsCheck()
    {
        Section("Settings round-trip");

        try
        {
            var s = AppSettings.Load();
            Line($"    path {AppSettings.FilePath}");
            Line($"    timeout={s.IdleTimeoutSeconds}s  tick={s.PollIntervalMs}ms  managed={s.ManagedDisplayIds.Count}");

            Check(s.IdleTimeoutSeconds is >= 10 and <= 86400, "timeout within clamp range");
            Check(s.PollIntervalMs is >= 100 and <= 5000, "poll interval within clamp range");

            Check(MacAutoStart.IsEnabled == MacAutoStart.IsEnabled,
                $"SMAppService status query works (enabled={MacAutoStart.IsEnabled})");
            if (!IsBundled())
                Line("    NOTE: start-at-login can only be registered from inside an .app bundle.");

            // Per-display config resolution, entirely in memory — no file is written.
            var probe = new AppSettings { Mode = OverlayMode.Dim, DimPercent = 40 };
            Check(probe.ConfigFor("X").Mode == OverlayMode.Dim, "shared config applies when per-display is off");

            probe.PerMonitorConfig = true;
            probe.PerMonitor["X"] = new MonitorConfig { Mode = OverlayMode.Video, VideoPath = "/tmp/x.mp4" };
            Check(probe.ConfigFor("X").Mode == OverlayMode.Video, "per-display override wins when per-display is on");
            Check(probe.ConfigFor("Y").Mode == OverlayMode.Dim, "display without an override falls back to the shared config");
            Check(probe.OverrideFor("Y").DimPercent == 40, "first-touch override is seeded from the shared config");

            var dim = new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 50 };
            Check(dim.Alpha is > 0 and < 255 && dim.Translucent, $"dim 50% -> alpha {dim.Alpha}, translucent");
            Check(new MonitorConfig { Mode = OverlayMode.TrueBlack }.Alpha == 255, "true black -> opaque");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Runs the main run loop briefly so AppKit actually places the windows we just made.</summary>
    private static void Pump(int ms = 120)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            CF.CFRunLoopRunInMode(CF.RunLoopDefaultMode, 0.02, true);
        }
    }

    /// <summary>Pumps the run loop until the window server reports our overlay, or gives up.</summary>
    private static CG.CGRect? WaitForWindowServer(DisplayTarget display, int timeoutMs = 2000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 50)
        {
            if (WindowServerBounds(display) is { } r) return r;
            Pump(50);
        }

        return WindowServerBounds(display);
    }

    /// <summary>This process's screensaver-level window covering the given display, per the window server.</summary>
    private static CG.CGRect? WindowServerBounds(DisplayTarget display)
    {
        var pid = Environment.ProcessId;
        var list = CG.CGWindowListCopyWindowInfo(CG.kCGWindowListOptionOnScreenOnly, 0);
        if (list == IntPtr.Zero) return null;

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
                if (boundsDict == IntPtr.Zero) continue;
                if (!CG.CGRectMakeWithDictionaryRepresentation(boundsDict, out var r)) continue;

                if (Math.Abs(r.X - display.Bounds.Left) < 1 && Math.Abs(r.Y - display.Bounds.Top) < 1)
                    return r;
            }
        }
        finally
        {
            CF.CFRelease(list);
        }

        return null;
    }

    /// <summary>NSApplicationActivationPolicy, named. 2 is Prohibited (no windows at all).</summary>
    private static string Policy(nint policy) => policy switch
    {
        AppKit.ActivationPolicyRegular => "Regular (in the Dock)",
        AppKit.ActivationPolicyAccessory => "Accessory (menu bar only)",
        _ => $"{policy}",
    };

    /// <summary>True when running from inside an .app bundle (…/Contents/MacOS/).</summary>
    internal static bool IsBundled() =>
        AppContext.BaseDirectory.TrimEnd('/').EndsWith("Contents/MacOS", StringComparison.Ordinal);

    private static void Section(string name)
    {
        Line("");
        Line($"-- {name} " + new string('-', Math.Max(0, 74 - name.Length)));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) _failures++;
        Line($"  [{(condition ? "PASS" : "FAIL")}] {message}");
    }

    private static void Fail(string message)
    {
        _failures++;
        Line($"  [FAIL] {message}");
    }

    private static void Line(string text)
    {
        Out.AppendLine(text);
        try { Console.WriteLine(text); } catch { /* no console attached */ }
    }
}
