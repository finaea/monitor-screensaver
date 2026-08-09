using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace MonitorDim.Core;

/// <summary>
/// Headless diagnostic. Exercises every detection path the engine depends on and writes
/// a report. Run with:  MonitorDim.exe --selftest [path]
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Out = new();
    private static int _failures;

    public static int Run(string? outputPath)
    {
        Line($"MonitorDim self-test  |  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
        ParserCheck();
        LiveRequesterQuery();
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
                text += $"\n(could not write report: {ex.Message})";
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
                ("Segoe UI Variable Text, Segoe UI", "MonitorDim 0123 idle 5m 0s"),
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
            var uri = new Uri("pack://application:,,,/Assets/MonitorDim.ico", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            Check(info?.Stream is not null, "icon found at pack://application:,,,/Assets/MonitorDim.ico");

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
                    win = new OverlayWindow(d, alpha: 255);
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
                    // Opaque overlays must stay opaque (cheap, hardware rendered), and must
                    // refuse an in-place switch to dim rather than silently no-op.
                    Check(!win.IsTranslucent, $"{d.FriendlyName}: true black uses an opaque window");
                    Check(!win.SetAlpha(180), $"{d.FriendlyName}: switching to dim correctly demands a rebuild");
                    Check(win.SetAlpha(255), $"{d.FriendlyName}: staying at true black is applied in place");
                }
                finally
                {
                    try { win?.HideOverlay(); win?.Close(); } catch { /* teardown */ }
                }
            }

            DimOverlay();
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
            win = new OverlayWindow(target, alpha: 180);
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

            Check(win.SetAlpha(120), $"{target.FriendlyName}: dim level changes in place");
            Check(!win.SetAlpha(255), $"{target.FriendlyName}: switching to true black correctly demands a rebuild");
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

        var before = ExecutionState.Read();
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
                SimpleReasonString = "MonitorDim self-test",
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
            if (ExecutionState.Read().DisplayRequired == expected) return (true, waited);
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
