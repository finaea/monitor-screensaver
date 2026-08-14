using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MonitorScreenSaver.Core;

/// <summary>Windows implementations of the Core platform seams, plus the bundle factory.</summary>
public static class WindowsPlatform
{
    public static EnginePlatform CreateEnginePlatform() => new(
        new WindowsActivityClock(),
        new WindowsExecutionSource(),
        new WindowsFullscreenDetector(),
        new WindowsAudioSource(),
        onChange => new WindowsForegroundWatch(onChange),
        (interval, tick) => new DispatcherTickTimer(interval, tick));
}

/// <summary>GetTickCount64 + GetLastInputInfo, with the 32-bit tick wrap handled.</summary>
public sealed class WindowsActivityClock : IActivityClock
{
    public ulong NowMs => Native.GetTickCount64();

    /// <summary>
    /// GetLastInputInfo returns a 32-bit tick that wraps every ~49.7 days. Subtracting in
    /// unsigned 32-bit space handles the wrap, then we rebase onto the 64-bit clock.
    /// </summary>
    public ulong LastInputMs
    {
        get
        {
            var lii = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
            var now = Native.GetTickCount64();

            if (!Native.GetLastInputInfo(ref lii)) return now;

            unchecked
            {
                var idleMs = (uint)Environment.TickCount - lii.dwTime;
                return idleMs > now ? 0 : now - idleMs;
            }
        }
    }
}

/// <summary>
/// POWER_INFORMATION_LEVEL.SystemExecutionState via CallNtPowerInformation. Readable
/// from a non-elevated process, and it reflects requests made by *other* processes via
/// both SetThreadExecutionState and PowerSetRequest.
/// </summary>
public sealed class WindowsExecutionSource : IExecutionStateSource
{
    ExecutionState IExecutionStateSource.Read() => Read();

    public static ExecutionState Read()
    {
        try
        {
            var status = Native.CallNtPowerInformation(Native.SystemExecutionState, IntPtr.Zero, 0, out var value, sizeof(uint));
            if (status != 0) return new ExecutionState(false, false, false, 0);

            return new ExecutionState(
                (value & Native.ES_DISPLAY_REQUIRED) != 0,
                (value & Native.ES_SYSTEM_REQUIRED) != 0,
                (value & Native.ES_USER_PRESENT) != 0,
                value);
        }
        catch
        {
            return new ExecutionState(false, false, false, 0);
        }
    }
}

public sealed class WindowsFullscreenDetector : IFullscreenDetector
{
    public bool IsFullscreenActive()
    {
        try
        {
            if (Native.SHQueryUserNotificationState(out var state) != 0) return false;
            return state is Native.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                          or Native.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class WindowsAudioSource : IAudioActivitySource
{
    public bool IsPlaying() => AudioActivity.IsPlaying();
}

/// <summary>
/// SetWinEventHook(EVENT_SYSTEM_FOREGROUND). GetLastInputInfo does not report focus
/// changes, but Windows counts them as activity.
/// </summary>
public sealed class WindowsForegroundWatch : IDisposable
{
    private IntPtr _hook;
    private Native.WinEventProc? _proc;   // must outlive the hook

    public WindowsForegroundWatch(Action onChange)
    {
        // Native invokes this, so nothing may escape it.
        _proc = (_, _, _, _, _, _, _) =>
            CrashLog.GuardCallback("EVENT_SYSTEM_FOREGROUND", onChange);

        _hook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _proc = null;
    }
}

/// <summary>WPF DispatcherTimer at Background priority, on the UI thread.</summary>
public sealed class DispatcherTickTimer : ITickTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherTickTimer(TimeSpan interval, Action tick)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += (_, _) => tick();
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Stop();
}

public sealed class WindowsDisplays : IDisplayEnumerator
{
    public IReadOnlyList<DisplayTarget> Enumerate() => DisplayEnumerator.Enumerate();
}

public sealed class WindowsOverlayFactory : IOverlayFactory
{
    public IOverlayWindow Create(DisplayTarget target, MonitorConfig cfg) => new OverlayWindow(target, cfg);
}
