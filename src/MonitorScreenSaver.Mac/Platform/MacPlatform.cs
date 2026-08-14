using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>macOS implementations of the Core platform seams, plus the bundle factory.</summary>
public static class MacPlatform
{
    public static EnginePlatform CreateEnginePlatform() => new(
        new MacActivityClock(),
        new MacExecutionSource(),
        new MacFullscreenDetector(),
        new MacAudioSource(),
        onChange => new MacForegroundWatch(onChange),
        (interval, tick) => new MacRunLoopTimer(interval, tick));
}

/// <summary>
/// Environment.TickCount64 as the monotonic clock; last input from
/// CGEventSourceSecondsSinceLastEventType (HID system state, any input event).
/// Reading idle seconds needs no TCC permission — only event taps do.
/// </summary>
public sealed class MacActivityClock : IActivityClock
{
    /// <summary>
    /// Two reads with no input in between can differ by ±1-2 ms (measured): the value
    /// is now − idle, subtracting across two clocks that round independently. The
    /// engine's manual "Blank now" compares consecutive reads for "did input arrive
    /// since?", so that wobble must be absorbed — only a jump past this threshold
    /// counts as real input. Real input resets idle to ~0, jumping the value forward
    /// by whole seconds, so wake latency is unaffected.
    /// </summary>
    private const ulong JitterMs = 100;

    /// <summary>A backward jump too big to be jitter — a clock discontinuity; resync.</summary>
    private const ulong ResyncMs = 10_000;

    private ulong _lastInput;

    public ulong NowMs => (ulong)Environment.TickCount64;

    public ulong LastInputMs
    {
        get
        {
            var now = NowMs;
            var idleSeconds = CG.CGEventSourceSecondsSinceLastEventType(
                CG.kCGEventSourceStateHIDSystemState, CG.kCGAnyInputEventType);

            if (double.IsNaN(idleSeconds) || idleSeconds < 0) return now;

            var idleMs = (ulong)(idleSeconds * 1000.0);
            var candidate = idleMs > now ? 0 : now - idleMs;

            var last = _lastInput;
            if (candidate > last + JitterMs || last > candidate + ResyncMs)
                _lastInput = last = candidate;

            return last;
        }
    }
}

/// <summary>
/// Aggregate display/system-sleep hold state from IOPMCopyAssertionsStatus. The Raw
/// bits are synthesised to match the Windows ES_* layout so diagnostics read the same:
/// system=0x1, display=0x2, present=0x4.
/// </summary>
public sealed class MacExecutionSource : IExecutionStateSource
{
    ExecutionState IExecutionStateSource.Read() => Read();

    public static ExecutionState Read()
    {
        try
        {
            if (IOKit.IOPMCopyAssertionsStatus(out var dict) != 0 || dict == IntPtr.Zero)
                return default;

            try
            {
                var display = CF.DictGetLong(dict, IOKit.AssertPreventUserIdleDisplaySleep) > 0
                           || CF.DictGetLong(dict, IOKit.AssertNoDisplaySleep) > 0;
                var system = CF.DictGetLong(dict, IOKit.AssertPreventUserIdleSystemSleep) > 0
                          || CF.DictGetLong(dict, IOKit.AssertPreventSystemSleep) > 0;
                var present = CF.DictGetLong(dict, IOKit.AssertUserIsActive) > 0;

                var raw = (system ? 0x1u : 0) | (display ? 0x2u : 0) | (present ? 0x4u : 0);
                return new ExecutionState(display, system, present, raw);
            }
            finally
            {
                CF.CFRelease(dict);
            }
        }
        catch
        {
            return default;
        }
    }
}

/// <summary>
/// Per-process assertion attribution via IOPMCopyAssertionsByProcess — the macOS
/// equivalent of powercfg /requests, except it needs no elevation, ever.
/// </summary>
public static class MacPowerAssertions
{
    /// <summary>Snapshot of who is holding what. Always Available on macOS.</summary>
    public static PowerSnapshot Query()
    {
        try
        {
            if (IOKit.IOPMCopyAssertionsByProcess(out var byPid) != 0 || byPid == IntPtr.Zero)
                return new PowerSnapshot(false, "IOPMCopyAssertionsByProcess failed.", []);

            try
            {
                var results = new List<PowerRequester>();
                var count = CF.CFDictionaryGetCount(byPid);
                var keys = new IntPtr[count];
                var values = new IntPtr[count];
                CF.CFDictionaryGetKeysAndValues(byPid, keys, values);

                for (var i = 0; i < count; i++)
                {
                    var pid = (int)CF.NumberToLong(keys[i]);
                    var assertions = values[i];
                    var n = CF.CFArrayGetCount(assertions);

                    for (nint j = 0; j < n; j++)
                    {
                        var a = CF.CFArrayGetValueAtIndex(assertions, j);
                        if (a == IntPtr.Zero) continue;

                        var type = CF.DictGetString(a, IOKit.KeyAssertType) ?? "unknown";
                        var name = CF.DictGetString(a, IOKit.KeyAssertName);
                        var details = CF.DictGetString(a, IOKit.KeyAssertDetails);

                        // AssertLevel 0 means the assertion exists but is switched off.
                        var levelValue = CF.DictGet(a, IOKit.KeyAssertLevel);
                        if (levelValue != IntPtr.Zero && CF.NumberToLong(levelValue) == 0) continue;

                        var requestType = type switch
                        {
                            IOKit.AssertPreventUserIdleDisplaySleep or IOKit.AssertNoDisplaySleep => "DISPLAY",
                            IOKit.AssertPreventUserIdleSystemSleep or IOKit.AssertPreventSystemSleep => "SYSTEM",
                            _ => type.ToUpperInvariant(),
                        };

                        var reason = (name, details) switch
                        {
                            (null, null) => null,
                            (not null, null) => name,
                            (null, not null) => details,
                            _ => $"{name} — {details}",
                        };

                        results.Add(new PowerRequester(
                            RequesterKind.Process, IOKit.ProcessName(pid), reason, requestType));
                    }
                }

                return new PowerSnapshot(true, null, results);
            }
            finally
            {
                CF.CFRelease(byPid);
            }
        }
        catch (Exception ex)
        {
            return new PowerSnapshot(false, ex.Message, []);
        }
    }
}

/// <summary>
/// "Is a fullscreen app up?" macOS has no exclusive fullscreen (the compositor always
/// owns the screen), so the Windows-era hazard is gone; this keeps the option's
/// behaviour by checking whether any normal-layer window exactly covers a display.
/// </summary>
public sealed class MacFullscreenDetector : IFullscreenDetector
{
    public bool IsFullscreenActive()
    {
        try
        {
            var displays = new uint[16];
            if (CG.CGGetActiveDisplayList(16, displays, out var displayCount) != 0) return false;

            var list = CG.CGWindowListCopyWindowInfo(
                CG.kCGWindowListOptionOnScreenOnly | CG.kCGWindowListExcludeDesktopElements, 0);
            if (list == IntPtr.Zero) return false;

            try
            {
                var n = CF.CFArrayGetCount(list);

                for (nint i = 0; i < n; i++)
                {
                    var win = CF.CFArrayGetValueAtIndex(list, i);
                    if (win == IntPtr.Zero) continue;

                    // Layer 0 is the normal app-window level; menus, docks and our
                    // future overlays live on other layers.
                    if (CF.NumberToLong(CF.CFDictionaryGetValue(win, CG.WindowLayerKey)) != 0) continue;

                    var boundsDict = CF.CFDictionaryGetValue(win, CG.WindowBoundsKey);
                    if (boundsDict == IntPtr.Zero) continue;
                    if (!CG.CGRectMakeWithDictionaryRepresentation(boundsDict, out var r)) continue;

                    for (uint d = 0; d < displayCount; d++)
                    {
                        var b = CG.CGDisplayBounds(displays[d]);
                        if (Math.Abs(r.X - b.X) < 1 && Math.Abs(r.Y - b.Y) < 1 &&
                            Math.Abs(r.Width - b.Width) < 1 && Math.Abs(r.Height - b.Height) < 1)
                            return true;
                    }
                }

                return false;
            }
            finally
            {
                CF.CFRelease(list);
            }
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// v1 audio probe: is any output-capable device running an IO cycle
/// (kAudioDevicePropertyDeviceIsRunningSomewhere)? Coarser than the Windows WASAPI
/// peak meters — a paused app that keeps the device open counts as playing. The
/// accurate alternative (Core Audio process taps, macOS 14.2+) costs a TCC
/// audio-capture prompt; revisit if this false-positives in practice.
/// </summary>
public sealed class MacAudioSource : IAudioActivitySource
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private uint[] _outputDevices = [];
    private DateTime _lastEnumerated = DateTime.MinValue;

    public bool IsPlaying()
    {
        try
        {
            EnsureDevices();

            foreach (var device in _outputDevices)
            {
                var addr = new CoreAudio.AudioObjectPropertyAddress(
                    CoreAudio.kAudioDevicePropertyDeviceIsRunningSomewhere,
                    CoreAudio.kAudioObjectPropertyScopeGlobal);

                uint size = sizeof(uint);
                if (CoreAudio.AudioObjectGetPropertyData(device, ref addr, 0, IntPtr.Zero, ref size, out uint running) == 0
                    && running != 0)
                    return true;
            }

            return false;
        }
        catch
        {
            _lastEnumerated = DateTime.MinValue;
            return false;
        }
    }

    private void EnsureDevices()
    {
        if (_outputDevices.Length > 0 && DateTime.UtcNow - _lastEnumerated < RefreshEvery) return;
        _lastEnumerated = DateTime.UtcNow;

        var addr = new CoreAudio.AudioObjectPropertyAddress(
            CoreAudio.kAudioHardwarePropertyDevices, CoreAudio.kAudioObjectPropertyScopeGlobal);

        if (CoreAudio.AudioObjectGetPropertyDataSize(
                CoreAudio.kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, out var size) != 0)
        {
            _outputDevices = [];
            return;
        }

        var devices = new uint[size / sizeof(uint)];
        if (CoreAudio.AudioObjectGetPropertyData(
                CoreAudio.kAudioObjectSystemObject, ref addr, 0, IntPtr.Zero, ref size, devices) != 0)
        {
            _outputDevices = [];
            return;
        }

        _outputDevices = devices.Where(HasOutputStreams).ToArray();
    }

    /// <summary>Input-only devices (microphones) must not count as audio playback.</summary>
    private static bool HasOutputStreams(uint device)
    {
        var addr = new CoreAudio.AudioObjectPropertyAddress(
            CoreAudio.kAudioDevicePropertyStreams, CoreAudio.kAudioObjectPropertyScopeOutput);

        return CoreAudio.AudioObjectGetPropertyDataSize(device, ref addr, 0, IntPtr.Zero, out var size) == 0
               && size > 0;
    }
}

/// <summary>
/// Foreground-change tracking. The engine only needs "the user switched apps counts
/// as activity", so polling the frontmost app once a second is enough — no
/// notification observer classes, no Accessibility permission. (Window-level focus
/// changes inside one app would need AX; app-level matches NSWorkspace's own signal.)
/// </summary>
public sealed class MacForegroundWatch : IDisposable
{
    private readonly MacRunLoopTimer _timer;
    private int _lastPid = -1;

    public MacForegroundWatch(Action onChange)
    {
        _timer = new MacRunLoopTimer(TimeSpan.FromSeconds(1), () =>
        {
            var pid = FrontmostPid();
            if (pid == _lastPid || pid < 0) return;

            var first = _lastPid == -1;
            _lastPid = pid;
            if (!first) onChange();
        });
        _timer.Start();
    }

    public static int FrontmostPid()
    {
        try
        {
            var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
            var app = ObjC.Send(workspace, ObjC.Sel("frontmostApplication"));
            if (app == IntPtr.Zero) return -1;
            return ObjC.SendInt(app, ObjC.Sel("processIdentifier"));
        }
        catch
        {
            return -1;
        }
    }

    public static string FrontmostName()
    {
        try
        {
            var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
            var app = ObjC.Send(workspace, ObjC.Sel("frontmostApplication"));
            if (app == IntPtr.Zero) return "?";
            return ObjC.NSStringToManaged(ObjC.Send(app, ObjC.Sel("localizedName"))) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    public void Dispose() => _timer.Dispose();
}
