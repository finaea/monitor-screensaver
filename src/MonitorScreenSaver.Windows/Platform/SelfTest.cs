using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace MonitorScreenSaver.Core;

/// <summary>
/// Headless diagnostic. Exercises every detection path the engine depends on and writes
/// a report. Run with:  MonitorScreenSaver.exe --selftest [path]
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Out = new();
    private static int _failures;

    public static int Run(string? outputPath)
    {
        Line($"MonitorScreenSaver self-test  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"OS {Environment.OSVersion.Version}  |  process {(Environment.Is64BitProcess ? "x64" : "x86")}  |  elevated={PowerRequestList.IsElevated}");
        Line(new string('=', 78));

        TextRendering();
        TrayIconResource();
        Displays();
        OverlayPlacement();
        SystemEventPlumbing();
        Category2Live();
        Category1();
        FullscreenProbe();
        AudioProbe();
        ParserCheck();
        LiveRequesterQuery();
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
                // Line, not `text +=`: text is a local snapshot that nothing reads again,
                // so appending to it discarded the message entirely.
                Line($"(could not write report: {ex.Message})");
            }
        }

        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- sections

    /// <summary>
    /// Forces a real WPF text layout pass and resolves the theme resources.
    ///
    /// Added after InvariantGlobalization=true shipped: it broke every window containing
    /// text (MS.Internal.FontCache.MajorLanguages threw in its type initializer on first
    /// measure) while leaving the empty overlay windows working, so nothing else here
    /// noticed. Any regression in fonts, globalization or Theme.xaml trips this.
    /// </summary>
    private static void TextRendering()
    {
        Section("WPF text rendering and theme resources");

        try
        {
            var app = System.Windows.Application.Current;
            Check(app is not null, "Application.Current available");

            foreach (var key in new[] { "Bg", "Surface", "Text", "TextMuted", "TextFaint", "Accent", "Warn", "Ok" })
            {
                var found = app?.TryFindResource(key) is System.Windows.Media.Brush;
                Check(found, $"brush resource '{key}' resolves");
            }

            foreach (var key in new[] { "Card", "Chip", "H1", "H2", "Body", "Muted", "Mono", "ToggleSwitch", "PrimaryButton", "GhostButton", "FieldBox" })
                Check(app?.TryFindResource(key) is System.Windows.Style, $"style resource '{key}' resolves");

            // The actual failure path is TextBlock.MeasureOverride -> TextFormatter ->
            // Typeface.CheckFastPathNominalGlyphs -> ComputeTypographyAvailabilities ->
            // MajorLanguages. Which fonts reach it depends on their typography tables, so
            // exercise every family the UI actually uses, in both formatting modes.
            (string Font, string Sample)[] cases =
            [
                ("Segoe UI Variable Text, Segoe UI", "MonitorScreenSaver 0123 idle 5m 0s"),
                ("Cascadia Mono, Consolas", @"\\.\DISPLAY1  5120 x 1440"),
                ("Segoe MDL2 Assets", "\uE921\uE8BB"),
                ("Segoe UI", "Console lock display off timeout"),
            ];

            foreach (var (font, sample) in cases)
            {
                foreach (var mode in new[] { TextFormattingMode.Display, TextFormattingMode.Ideal })
                {
                    var tb = new System.Windows.Controls.TextBlock
                    {
                        Text = sample,
                        FontFamily = new System.Windows.Media.FontFamily(font),
                        FontSize = 13,
                    };

                    TextOptions.SetTextFormattingMode(tb, mode);

                    tb.Measure(new System.Windows.Size(1000, 1000));
                    tb.Arrange(new System.Windows.Rect(tb.DesiredSize));

                    Check(tb.DesiredSize.Width > 0 && tb.DesiredSize.Height > 0,
                        $"'{font}' [{mode}] measured to {tb.DesiredSize.Width:F1} x {tb.DesiredSize.Height:F1} DIP");
                }
            }

            // And through the theme styles, so Theme.xaml itself is exercised.
            foreach (var key in new[] { "H1", "Body", "Mono" })
            {
                var styled = new System.Windows.Controls.TextBlock { Text = "styled sample 123" };
                if (app?.TryFindResource(key) is System.Windows.Style st) styled.Style = st;
                styled.Measure(new System.Windows.Size(1000, 1000));

                Check(styled.DesiredSize.Width > 0, $"style '{key}' measured to {styled.DesiredSize.Width:F1} DIP");
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");

            var inner = ex.InnerException;
            while (inner is not null)
            {
                Line($"        inner: {inner.GetType().Name}: {inner.Message}");
                inner = inner.InnerException;
            }
        }
    }

    /// <summary>
    /// pack:// URIs plus single-file publishing is a classic silent-failure combo, and the
    /// tray code falls back to a generic icon rather than crashing — so assert it here.
    /// Also guards against the PNG-compressed .ico trap: GDI+ cannot decode those, which
    /// would leave the tray showing nothing.
    /// </summary>
    private static void TrayIconResource()
    {
        Section("Tray icon resource");

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MonitorScreenSaver.ico", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            Check(info?.Stream is not null, "icon found at pack://application:,,,/Assets/MonitorScreenSaver.ico");

            if (info?.Stream is null) return;

            using var stream = info.Stream;

            // WPF path (settings window + window icon)
            stream.Position = 0;
            var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

            Check(decoder.Frames.Count > 0, $"WPF decoded {decoder.Frames.Count} frame(s): " +
                string.Join(", ", decoder.Frames.Select(f => $"{f.PixelWidth}px")));

            Check(decoder.Frames.Any(f => f.PixelWidth is 16), "has a 16px frame for the tray");

            // GDI+ path (NotifyIcon) — this is the one that breaks on PNG-compressed .ico
            stream.Position = 0;
            using var small = new System.Drawing.Icon(stream, new System.Drawing.Size(16, 16));
            using var bmp = small.ToBitmap();
            Check(bmp.Width == 16 && bmp.Height == 16, $"GDI+ rendered the 16px frame ({bmp.Width}×{bmp.Height})");

            var opaque = 0;
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).A > 8) opaque++;

            Check(opaque > 40, $"icon has visible content ({opaque}/{bmp.Width * bmp.Height} opaque pixels)");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
    }

    private static void Displays()
    {
        Section("Display enumeration");

        try
        {
            var displays = DisplayEnumerator.Enumerate();
            Check(displays.Count > 0, $"found {displays.Count} display(s)");

            foreach (var d in displays)
            {
                Line($"    * {d.FriendlyName}");
                Line($"        gdi      {d.DeviceName}{(d.IsPrimary ? "   [PRIMARY]" : "")}");
                Line($"        geometry {d.Geometry}");
                Line($"        stableId {d.StableId}");
            }

            var ids = displays.Select(d => d.StableId).ToList();
            Check(ids.Distinct().Count() == ids.Count, "stable ids are unique (settings keys will not collide)");
            Check(ids.All(id => !string.IsNullOrWhiteSpace(id)), "every display has a non-blank stable id");
            Check(displays.Count(d => d.IsPrimary) == 1, $"exactly one display is primary ({displays.Count(d => d.IsPrimary)})");
            Check(displays.All(d => d.Bounds.Width > 0 && d.Bounds.Height > 0), "every display has non-empty bounds");
        }
        catch (Exception ex)
        {
            Fail($"enumeration threw: {ex}");
        }
    }

    /// <summary>
    /// The riskiest part of the app: an overlay must land exactly on its monitor in
    /// physical pixels, including negative coordinates and portrait panels, regardless
    /// of the primary monitor's DPI scale.
    /// </summary>
    private static void OverlayPlacement()
    {
        Section("Overlay placement (physical pixels)");

        try
        {
            foreach (var d in DisplayEnumerator.Enumerate())
            {
                OverlayWindow? win = null;

                try
                {
                    win = new OverlayWindow(d, new MonitorConfig { Mode = OverlayMode.TrueBlack });
                    win.ShowOverlay();

                    var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    Check(hwnd != IntPtr.Zero, $"{d.FriendlyName}: window handle created");

                    if (!Native.GetWindowRect(hwnd, out var actual))
                    {
                        Fail($"{d.FriendlyName}: GetWindowRect failed");
                        continue;
                    }

                    var want = d.Bounds;
                    var exact = actual.Left == want.Left && actual.Top == want.Top &&
                                actual.Right == want.Right && actual.Bottom == want.Bottom;

                    Check(exact,
                        $"{d.FriendlyName}: placed at ({actual.Left},{actual.Top})-({actual.Right},{actual.Bottom}), " +
                        $"expected ({want.Left},{want.Top})-({want.Right},{want.Bottom})");

                    var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
                    Check((ex & Native.WS_EX_NOACTIVATE) != 0, $"{d.FriendlyName}: WS_EX_NOACTIVATE set (will not steal focus)");
                    Check((ex & Native.WS_EX_TOOLWINDOW) != 0, $"{d.FriendlyName}: WS_EX_TOOLWINDOW set (kept out of Alt+Tab)");
                    // Reported, not asserted. An overlay is supposed to stay out of the
                    // foreground, and the two flags above are the whole of what the app can do
                    // about it — but the system will still hand the foreground to a
                    // non-activating topmost window when the previous holder has just been
                    // destroyed, which is what this sequence does between displays. Measured
                    // here: it happens on some runs and not others, so asserting on it makes
                    // this suite flake rather than telling anyone anything.
                    //
                    // It is reported because it is the precondition for the one real hazard —
                    // a foreground overlay receives the auto-repeat WM_KEYDOWN stream of a
                    // held blank-now shortcut, and waking on that would cancel the blank it
                    // just asked for. OverlayWindow ignores repeats for exactly this reason.
                    //
                    // WPF's IsActive is not the measure: an overlay created straight after
                    // another was destroyed reports IsActive=true and IsKeyboardFocusWithin=true
                    // while the window manager has the foreground somewhere else entirely.
                    // That is in-process bookkeeping and it delivers nothing, since no
                    // WM_KEYDOWN reaches a process that is not in front.
                    if (Native.GetForegroundWindow() == hwnd)
                        Line($"    NOTE: {d.FriendlyName}: the system gave this overlay the foreground " +
                             "(expected on this create/destroy sequence; repeats are ignored so a held shortcut still holds)");
                    // Opaque overlays must stay opaque (cheap, hardware rendered), and must
                    // refuse an in-place switch to dim rather than silently no-op.
                    Check(!win.IsTranslucent, $"{d.FriendlyName}: true black uses an opaque window");
                    Check(!win.TryApply(new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 70 }),
                        $"{d.FriendlyName}: switching to dim correctly demands a rebuild");
                    Check(win.TryApply(new MonitorConfig { Mode = OverlayMode.TrueBlack }),
                        $"{d.FriendlyName}: staying at true black is applied in place");
                }
                finally
                {
                    try { win?.HideOverlay(); win?.Close(); } catch { /* teardown */ }
                }
            }

            DimOverlay();
            VideoOverlay();
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex}");
        }
    }

    /// <summary>Dim mode takes a different rendering path, so verify it lands too.</summary>
    private static void DimOverlay()
    {
        var target = DisplayEnumerator.Enumerate().FirstOrDefault();
        if (target is null) return;

        OverlayWindow? win = null;

        try
        {
            win = new OverlayWindow(target, new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 70 });
            win.ShowOverlay();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;

            Check(win.IsTranslucent, $"{target.FriendlyName}: dim overlay reports translucent");

            var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Check((ex & Native.WS_EX_LAYERED) != 0,
                $"{target.FriendlyName}: dim overlay is a layered window (WPF applied it)");
            Check((ex & Native.WS_EX_NOACTIVATE) != 0,
                $"{target.FriendlyName}: dim overlay still does not steal focus");

            if (Native.GetWindowRect(hwnd, out var r))
            {
                var w = target.Bounds;
                Check(r.Left == w.Left && r.Top == w.Top && r.Right == w.Right && r.Bottom == w.Bottom,
                    $"{target.FriendlyName}: dim overlay placed at ({r.Left},{r.Top})-({r.Right},{r.Bottom})");
            }

            Check(win.TryApply(new MonitorConfig { Mode = OverlayMode.Dim, DimPercent = 47 }),
                $"{target.FriendlyName}: dim level changes in place");
            Check(!win.TryApply(new MonitorConfig { Mode = OverlayMode.TrueBlack }),
                $"{target.FriendlyName}: switching to true black correctly demands a rebuild");
        }
        finally
        {
            try { win?.HideOverlay(); win?.Close(); } catch { /* teardown */ }
        }
    }

    /// <summary>
    /// Video mode without a usable file must degrade to an opaque black window — on
    /// OLED that is the correct fallback — and config changes must demand rebuilds
    /// exactly where a fresh MediaElement is needed.
    /// </summary>
    private static void VideoOverlay()
    {
        var target = DisplayEnumerator.Enumerate().FirstOrDefault();
        if (target is null) return;

        var missing = Path.Combine(Path.GetTempPath(), "monitorscreensaver-selftest-missing.mp4");
        var cfg = new MonitorConfig { Mode = OverlayMode.Video, VideoPath = missing, VideoStretch = VideoStretch.Fit };

        OverlayWindow? win = null;

        try
        {
            win = new OverlayWindow(target, cfg);
            win.ShowOverlay();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            Check(hwnd != IntPtr.Zero, $"{target.FriendlyName}: video overlay window created");
            Check(!win.IsTranslucent, $"{target.FriendlyName}: video overlay is an opaque window");
            Check(win.IsVideo, $"{target.FriendlyName}: video overlay reports video mode");
            Check(!win.VideoPlaying, $"{target.FriendlyName}: missing file degrades to black (no media element)");

            if (Native.GetWindowRect(hwnd, out var r))
            {
                var w = target.Bounds;
                Check(r.Left == w.Left && r.Top == w.Top && r.Right == w.Right && r.Bottom == w.Bottom,
                    $"{target.FriendlyName}: video overlay placed at ({r.Left},{r.Top})-({r.Right},{r.Bottom})");
            }

            Check(win.TryApply(cfg with { VideoStretch = VideoStretch.Fill }),
                $"{target.FriendlyName}: stretch mode changes in place");
            Check(!win.TryApply(cfg with { VideoPath = missing + ".other.mp4" }),
                $"{target.FriendlyName}: a different video file correctly demands a rebuild");
            Check(!win.TryApply(new MonitorConfig { Mode = OverlayMode.TrueBlack }),
                $"{target.FriendlyName}: switching to true black correctly demands a rebuild");
        }
        finally
        {
            try { win?.HideOverlay(); win?.Close(); } catch { /* teardown */ }
        }
    }

    private static void SystemEventPlumbing()
    {
        Section("System event plumbing");

        SystemEventSink? sink = null;

        try
        {
            sink = new SystemEventSink();
            Check(true, "hidden top-level window created and power/session notifications registered");
            Line("    (WM_POWERBROADCAST needs a real top-level HWND — message-only windows do not receive it)");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
        finally
        {
            try { sink?.Dispose(); } catch { /* teardown */ }
        }
    }

    private static void Category2Live()
    {
        Section("Category 2 — display power requests (SystemExecutionState)");

        var before = WindowsExecutionSource.Read();
        Line($"    baseline raw=0x{before.Raw:X8}  display={before.DisplayRequired}  system={before.SystemRequired}");

        if (before.DisplayRequired)
        {
            Line("    NOTE: something is already holding a display request, so the clear-side");
            Line("          assertions are skipped for this run.");
        }

        // Legacy path: SetThreadExecutionState.
        try
        {
            const uint ES_CONTINUOUS = 0x80000000;

            SetThreadExecutionState(ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED);
            var (set, setMs) = WaitForDisplayFlag(true);
            Check(set, $"SetThreadExecutionState(ES_DISPLAY_REQUIRED) observable after {setMs} ms");

            SetThreadExecutionState(ES_CONTINUOUS);
            var (cleared, clearMs) = WaitForDisplayFlag(false);
            Check(cleared || before.DisplayRequired, $"clearing it observable after {clearMs} ms");
        }
        catch (Exception ex)
        {
            Fail($"legacy path threw: {ex.Message}");
        }

        // Modern path: PowerSetRequest — this is what Parsec/OBS-class apps use.
        try
        {
            var ctx = new Native.REASON_CONTEXT
            {
                Version = 0,
                Flags = Native.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "MonitorScreenSaver self-test",
            };

            var handle = Native.PowerCreateRequest(ref ctx);
            Check(handle != IntPtr.Zero && handle != new IntPtr(-1), "PowerCreateRequest succeeded");

            Native.PowerSetRequest(handle, Native.PowerRequestDisplayRequired);
            var (set, setMs) = WaitForDisplayFlag(true);
            Check(set, $"PowerSetRequest(DisplayRequired) observable after {setMs} ms");

            Native.PowerClearRequest(handle, Native.PowerRequestDisplayRequired);
            var (cleared, clearMs) = WaitForDisplayFlag(false);
            Check(cleared || before.DisplayRequired, $"PowerClearRequest observable after {clearMs} ms");

            Native.CloseHandle(handle);
        }
        catch (Exception ex)
        {
            Fail($"modern path threw: {ex.Message}");
        }

        BlacklistDecision();
    }

    /// <summary>
    /// The other half of Category 2: whether a held display request is *ignored* because
    /// every holder is blacklisted (AppSettings.BlacklistCovers, shared Core logic, consumed
    /// at BlankingEngine.Tick).
    ///
    /// Deliberately synthetic rather than live. Attribution needs elevation on Windows, so a
    /// live-holder version of this check would silently not run for most people — which is
    /// precisely when a blacklist regression would ship. The mac twin can be live because
    /// IOKit attribution always works there (MacSelfTest.Category2Live).
    ///
    /// The rule under test is all-or-nothing: one unlisted holder is enough to keep honouring
    /// the request, because the aggregate flag cannot be attributed any finer than "somebody".
    /// </summary>
    private static void BlacklistDecision()
    {
        try
        {
            PowerSnapshot Snapshot(params string[] display) => new(true, null,
                [.. display.Select(n => new PowerRequester(RequesterKind.Process, $@"\Device\HarddiskVolume3\{n}", null, "DISPLAY"))]);

            var one = Snapshot("parsecd.exe");
            var two = Snapshot("parsecd.exe", "obs64.exe");

            AppSettings Listing(params string[] names) => new() { BlacklistedRequesters = [.. names] };

            Check(Listing("parsecd.exe").BlacklistCovers(one),
                "blacklisting every current holder makes the engine ignore the aggregate flag");
            Check(!Listing("parsecd.exe").BlacklistCovers(two),
                "one unlisted holder is enough to keep honouring the request");
            Check(Listing("parsecd.exe", "obs64.exe").BlacklistCovers(two),
                "blacklisting both holders covers both");
            Check(Listing("PARSECD.EXE").BlacklistCovers(one),
                "blacklist matching ignores case");
            Check(!new AppSettings().BlacklistCovers(one),
                "an empty blacklist never covers a live holder");
            Check(!Listing("parsecd.exe").BlacklistCovers(Snapshot()),
                "no holders at all is not 'covered' (nothing to ignore)");

            // Without attribution rights the snapshot carries no names, and guessing would
            // blank through a request somebody is legitimately holding.
            Check(!Listing("parsecd.exe").BlacklistCovers(new PowerSnapshot(false, "Requires administrator rights.", [])),
                "an unavailable snapshot disables the blacklist rather than guessing");
        }
        catch (Exception ex)
        {
            Fail($"blacklist decision threw: {ex.Message}");
        }
    }

    /// <summary>
    /// SystemExecutionState does not update synchronously with the set/clear call — a
    /// same-instant read can still see the old value. The engine polls, so this is
    /// harmless in production, but the test has to poll too or it flakes.
    /// </summary>
    private static (bool Ok, int ElapsedMs) WaitForDisplayFlag(bool expected, int timeoutMs = 1500)
    {
        const int step = 25;

        for (var waited = 0; ; waited += step)
        {
            if (WindowsExecutionSource.Read().DisplayRequired == expected) return (true, waited);
            if (waited >= timeoutMs) return (false, waited);
            Thread.Sleep(step);
        }
    }

    private static void Category1()
    {
        Section("Category 1 — input idle");

        try
        {
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
            var ok = Native.GetLastInputInfo(ref lii);
            Check(ok, "GetLastInputInfo succeeded");

            unchecked
            {
                var idleMs = (uint)Environment.TickCount - lii.dwTime;
                Line($"    idle for {idleMs / 1000.0:F1}s  (tick={lii.dwTime}, now={(uint)Environment.TickCount})");
                Check(idleMs < 7L * 24 * 60 * 60 * 1000, "idle value is sane (wrap-around arithmetic correct)");
            }

            Check(Native.GetTickCount64() > 0, "GetTickCount64 responds");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
    }

    private static void FullscreenProbe()
    {
        Section("Fullscreen guard");

        try
        {
            var hr = Native.SHQueryUserNotificationState(out var state);
            Check(hr == 0, $"SHQueryUserNotificationState -> {state}");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
    }

    /// <summary>
    /// The audio-activity option reads WASAPI endpoint peak meters. Exercise the whole
    /// interop path: enumerate active render endpoints and take a live peak reading.
    /// Zero endpoints is legal (headless box); an exception is the failure.
    /// </summary>
    private static void AudioProbe()
    {
        Section("Audio activity probe (WASAPI endpoint meters)");

        try
        {
            var (endpoints, peak) = AudioActivity.Probe();
            Check(endpoints >= 0, $"enumerated {endpoints} active render endpoint(s)");
            Check(peak is >= 0f and <= 1f, $"live peak reading {peak:F4} is in range 0..1");
            Line($"    IsPlaying() -> {AudioActivity.IsPlaying()}");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ParserCheck()
    {
        Section("powercfg /requests parser");

        const string sample = """
            DISPLAY:
            [PROCESS] \Device\HarddiskVolume4\Program Files\Parsec\parsecd.exe
            Streaming session active

            [DRIVER] Realtek High Definition Audio
            An audio stream is currently in use.

            SYSTEM:
            [PROCESS] \Device\HarddiskVolume7\Steam\steam.exe

            AWAYMODE:
            None.

            EXECUTION:
            None.

            PERFBOOST:
            None.

            ACTIVELOCKSCREEN:
            None.
            """;

        try
        {
            var parsed = PowerRequestList.Parse(sample);

            Check(parsed.Count == 3, $"parsed {parsed.Count} entries (expected 3)");

            var display = parsed.Where(p => p.RequestType == "DISPLAY").ToList();
            Check(display.Count == 2, $"{display.Count} DISPLAY entries (expected 2)");

            var parsec = display.FirstOrDefault(p => p.ShortName == "parsecd.exe");
            Check(parsec is not null, "parsecd.exe extracted from the \\Device\\HarddiskVolume path");
            Check(parsec?.Kind == RequesterKind.Process, "classified as PROCESS");
            Check(parsec?.Reason == "Streaming session active", "reason line attached to the right caller");

            var audio = display.FirstOrDefault(p => p.Kind == RequesterKind.Driver);
            Check(audio?.Caller == "Realtek High Definition Audio", "driver caller parsed");

            var system = parsed.FirstOrDefault(p => p.RequestType == "SYSTEM");
            Check(system?.ShortName == "steam.exe", "SYSTEM section kept separate from DISPLAY");
            Check(system?.Reason is null, "caller with no reason line does not swallow the next section");

            Check(PowerRequestList.Parse("").Count == 0, "empty input yields no entries");
            Check(PowerRequestList.Parse("DISPLAY:\nNone.\n").Count == 0, "'None.' yields no entries");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex}");
        }
    }

    private static void LiveRequesterQuery()
    {
        Section("Live requester query");

        try
        {
            var snap = PowerRequestList.QueryAsync().GetAwaiter().GetResult();

            if (!snap.Available)
            {
                Line($"    unavailable: {snap.Unavailable}");
                Line(PowerRequestList.IsElevated
                    ? "    NOTE: elevated but still unavailable — unexpected."
                    : "    expected when not elevated; blanking is unaffected.");
                Check(!PowerRequestList.IsElevated, "unavailability is explained by lack of elevation");
            }
            else
            {
                Check(true, $"powercfg returned {snap.Requesters.Count} request(s)");
                foreach (var r in snap.Requesters)
                    Line($"    [{r.RequestType}] {r.ShortName} ({r.Kind}) {r.Reason}");
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
    }

    /// <summary>
    /// The blank-now shortcut, end to end against the real OS.
    ///
    /// The point of interest here is the mirror image of the mac head's problem. macOS returns
    /// noErr when another process already holds a combination, so MacHotkey needs four
    /// pre-flight layers and cannot trust its own registration. Windows documents the
    /// opposite — RegisterHotKey "typically … fails if the keystrokes specified for the hot
    /// key have already been registered for another hot key" — so the clash is provable, and
    /// it is proved here rather than taken on trust: a second registration of a combination
    /// this process already holds must come back refused.
    /// </summary>
    private static void HotkeyCheck()
    {
        Section("Blank now shortcut (global hot key)");

        WindowsHotkey? first = null;
        WindowsHotkey? second = null;

        try
        {
            var configured = AppSettings.Load().BlankNowHotkey;
            Line($"    configured: {configured ?? "(none)"}");

            Check(string.IsNullOrWhiteSpace(configured) || HotkeySpec.TryParse(configured, out _),
                "the stored shortcut parses (or is deliberately empty)");

            first = new WindowsHotkey(() => { });

            // Shape rules, from Core — the half that is identical on both heads.
            void Refuses(string text, string why)
            {
                Check(HotkeySpec.TryParse(text, out var spec) && spec is not null && first!.Blocker(spec) is not null,
                    $"refuses {text} ({why})");
            }

            Refuses("Ctrl+B", "one modifier is app-accelerator territory");
            Refuses("Shift+Cmd+B", "Command and Shift alone are what app menus are built from");
            Refuses("Ctrl+Alt+Shift+F12", "F12 belongs to the debugger at all times");
            Refuses("Ctrl+Alt+Cmd+B", "Windows reserves the Windows key for the shell");
            Refuses("Ctrl+Shift+T", "reopening a closed tab, in every browser");

            Check(HotkeySpec.TryParse("Ctrl+Alt+Shift+B", out var ok) && ok is not null && first.Blocker(ok) is null,
                "accepts the default Ctrl+Alt+Shift+B");

            // Key name to virtual-key code. Contiguous ASCII ranges, so the ends are enough.
            Check(WindowsKeyCodes.Code("A") == 0x41 && WindowsKeyCodes.Code("Z") == 0x5A, "letters map to VK 0x41-0x5A");
            Check(WindowsKeyCodes.Code("0") == 0x30 && WindowsKeyCodes.Code("9") == 0x39, "digits map to VK 0x30-0x39");
            Check(WindowsKeyCodes.Code("F1") == 0x70 && WindowsKeyCodes.Code("F20") == 0x83, "F-keys map to VK 0x70-0x83");
            Check(WindowsKeyCodes.Code("Space") == 0x20, "Space maps to VK_SPACE");
            Check(WindowsKeyCodes.Code("Tab") is null, "a key outside the allowed set has no code");

            // Live registration. F19 is deliberately obscure: it exists as a virtual key on
            // every Windows install but sits off the end of a normal keyboard, so nothing
            // ships a shortcut on it and this cannot fight the user's real bindings.
            HotkeySpec.TryParse("Ctrl+Alt+Shift+F19", out var spare);
            var status = first.Apply(spare);
            Check(status.State == HotkeyState.Active, $"registered {spare}: {status.State} — {status.Detail}");

            if (status.State == HotkeyState.Active)
            {
                // The asymmetry with macOS, demonstrated. A second holder of the same
                // combination is refused, and it is refused with the error that lets the
                // settings window say so instead of guessing.
                second = new WindowsHotkey(() => { });
                var clash = second.Apply(spare);

                Check(clash.State == HotkeyState.Failed, $"a second registration of {spare} is refused ({clash.State})");
                Check(clash.Detail.Contains("already registered", StringComparison.OrdinalIgnoreCase),
                    $"the refusal is attributed to the clash: {clash.Detail}");

                // Releasing it must actually release it, or a settings change would burn the
                // combination for the rest of the session.
                first.Apply(null);
                Check(first.Status.State == HotkeyState.Off, "clearing the shortcut unregisters it");

                var retaken = second.Apply(spare);
                Check(retaken.State == HotkeyState.Active, "the combination is free again once released");
            }
            else
            {
                Line("    NOTE: could not take the probe combination, so the clash-detection");
                Line("          assertions are skipped for this run.");
            }
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { first?.Dispose(); } catch { /* teardown */ }
            try { second?.Dispose(); } catch { /* teardown */ }
        }
    }

    /// <summary>
    /// The manual-blank hold, driven by a fake clock so the sequence is exact and no display is
    /// ever covered. This is the shipped bug it exists for: the shortcut blanked the screens and
    /// they came straight back when the user lifted their finger, because the key *release* is
    /// itself input and the hold was watching for input from the moment of the press.
    ///
    /// Portable logic (BlankingEngine.ManualBlankSettleMs), and the twin of the mac head's
    /// ManualBlankHold — it is checked on both heads because it is shared code that both ship.
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
            Check(AutoStart.IsEnabled == AutoStart.IsEnabled, $"autostart query works (enabled={AutoStart.IsEnabled}, elevatedTask={AutoStart.IsElevatedTask})");

            // Per-display config resolution, entirely in memory.
            var probe = new AppSettings { Mode = OverlayMode.Dim, DimPercent = 40 };
            Check(probe.ConfigFor("X").Mode == OverlayMode.Dim, "shared config applies when per-display is off");

            probe.PerMonitorConfig = true;
            probe.PerMonitor["X"] = new MonitorConfig { Mode = OverlayMode.Video, VideoPath = @"C:\x.mp4" };
            Check(probe.ConfigFor("X").Mode == OverlayMode.Video, "per-display override wins when per-display is on");
            Check(probe.ConfigFor("Y").Mode == OverlayMode.Dim, "display without an override falls back to the shared config");
            Check(probe.OverrideFor("Y").DimPercent == 40, "first-touch override is seeded from the shared config");
        }
        catch (Exception ex)
        {
            Fail($"threw: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private static void Section(string name)
    {
        // ASCII only: this report gets read in cmd.exe and PowerShell 5.1, both of
        // which mangle UTF-8 box-drawing characters.
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
