using System.Runtime.InteropServices;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// ITickTimer on the main CFRunLoop — the macOS analog of a WPF DispatcherTimer.
/// Ticks arrive on the thread pumping the main run loop, so the engine stays
/// single-threaded exactly like on Windows.
/// </summary>
public sealed unsafe class MacRunLoopTimer : ITickTimer
{
    private readonly Action _tick;
    private GCHandle _self;
    private IntPtr _timer;
    private TimeSpan _interval;

    public MacRunLoopTimer(TimeSpan interval, Action tick)
    {
        _interval = interval;
        _tick = tick;
        _self = GCHandle.Alloc(this);
    }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (_interval == value) return;
            _interval = value;

            if (_timer == IntPtr.Zero) return;
            Stop();
            Start();
        }
    }

    public void Start()
    {
        if (_timer != IntPtr.Zero || !_self.IsAllocated) return;

        var context = new CF.CFRunLoopTimerContext { Info = GCHandle.ToIntPtr(_self) };
        var seconds = _interval.TotalSeconds;

        _timer = CF.CFRunLoopTimerCreate(
            IntPtr.Zero, CF.CFAbsoluteTimeGetCurrent() + seconds, seconds, 0, 0,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Fire, ref context);

        CF.CFRunLoopAddTimer(CF.CFRunLoopGetMain(), _timer, CF.RunLoopCommonModes);
    }

    public void Stop()
    {
        if (_timer == IntPtr.Zero) return;

        CF.CFRunLoopTimerInvalidate(_timer);
        CF.CFRelease(_timer);
        _timer = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void Fire(IntPtr timer, IntPtr info)
    {
        // Invoked by CFRunLoop: an exception escaping here would tear the process down.
        CrashLog.GuardCallback("CFRunLoopTimer", () =>
        {
            if (GCHandle.FromIntPtr(info).Target is MacRunLoopTimer self)
                self._tick();
        });
    }

    public void Dispose()
    {
        Stop();
        if (_self.IsAllocated) _self.Free();
    }
}
