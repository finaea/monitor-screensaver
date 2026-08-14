namespace MonitorScreenSaver.Core;

// The seams between the portable policy engine and the OS. One implementation set
// per platform: src/MonitorScreenSaver.Windows/Platform, src/MonitorScreenSaver.Mac/Platform.

/// <summary>Monotonic millisecond clock plus the last-input timestamp on the same timebase.</summary>
public interface IActivityClock
{
    ulong NowMs { get; }

    /// <summary>
    /// When keyboard/mouse input last arrived, on the same timebase as <see cref="NowMs"/>.
    /// Never greater than <see cref="NowMs"/>.
    /// </summary>
    ulong LastInputMs { get; }
}

/// <summary>Aggregate "is anything holding the display/system awake" state.</summary>
public interface IExecutionStateSource
{
    ExecutionState Read();
}

/// <summary>The "never blank during exclusive fullscreen" probe. Must not throw.</summary>
public interface IFullscreenDetector
{
    bool IsFullscreenActive();
}

/// <summary>The "never blank while audio is playing" probe. Must not throw.</summary>
public interface IAudioActivitySource
{
    bool IsPlaying();
}

/// <summary>A repeating timer delivering ticks on the engine's (UI) thread.</summary>
public interface ITickTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    void Start();
    void Stop();
}

public interface IDisplayEnumerator
{
    IReadOnlyList<DisplayTarget> Enumerate();
}

/// <summary>One overlay window covering one display: black, dim or video.</summary>
public interface IOverlayWindow
{
    /// <summary>Raised when the user pokes the overlay (mouse/keys landing on a blanked screen).</summary>
    event Action? WakeRequested;

    bool IsVisible { get; }

    /// <summary>True when this overlay is visible with a live video pipeline.</summary>
    bool VideoPlaying { get; }

    /// <summary>The display bounds this window was built for; a mismatch means rebuild.</summary>
    PixelRect BuiltBounds { get; }

    /// <summary>
    /// Applies a config change in place when possible. Returns false when the window
    /// must be recreated instead.
    /// </summary>
    bool TryApply(MonitorConfig cfg);

    void ShowOverlay();
    void HideOverlay();

    /// <summary>Re-asserts position and z-order; cheap, called by the watchdog.</summary>
    void ApplyBounds();

    void Close();
}

public interface IOverlayFactory
{
    IOverlayWindow Create(DisplayTarget target, MonitorConfig cfg);
}

/// <summary>Sleep/wake, display topology and session lock notifications.</summary>
public interface ISystemEvents : IDisposable
{
    event Action<SystemEventKind> Event;
}

/// <summary>Everything the blanking engine needs from the OS, bundled for injection.</summary>
/// <param name="ForegroundWatchFactory">
/// Creates a watcher that invokes the callback whenever the foreground window/app
/// changes. The implementation owns callback-safety (nothing may escape into native
/// callers) and thread affinity.
/// </param>
/// <param name="TimerFactory">Creates a stopped <see cref="ITickTimer"/> with the given interval and tick callback.</param>
public sealed record EnginePlatform(
    IActivityClock Clock,
    IExecutionStateSource ExecutionState,
    IFullscreenDetector Fullscreen,
    IAudioActivitySource Audio,
    Func<Action, IDisposable> ForegroundWatchFactory,
    Func<TimeSpan, Action, ITickTimer> TimerFactory);
