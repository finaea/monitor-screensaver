using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MonitorDim.Core;

public enum AwakeReason
{
    /// <summary>Idle timer has expired and nothing is holding the display; we blank.</summary>
    None,
    UserInput,
    ForegroundChange,
    DisplayRequest,
    Fullscreen,
    Resumed,
    Paused,
}

public sealed record EngineStatus(
    bool Blanked,
    TimeSpan Idle,
    TimeSpan UntilBlank,
    AwakeReason Reason,
    ExecutionState Exec,
    bool Paused,
    bool FullscreenActive);

/// <summary>
/// Reimplements the Windows display-idle decision so it can be applied to a subset of
/// monitors instead of all of them.
///
/// Windows blanks when BOTH hold:
///   Category 1 — the idle timer expired. Per the SetThreadExecutionState docs the system
///                "automatically detects activities such as local keyboard or mouse input,
///                server activity, and changing window focus".
///   Category 2 — no process holds a DISPLAY power request. ES_DISPLAY_REQUIRED
///                "forces the display to be on by resetting the display idle timer".
///
/// Because a held request continuously *resets* the timer, we model it the same way:
/// while it is held we keep pushing the activity baseline forward, so releasing it starts
/// a fresh full timeout rather than blanking instantly.
/// </summary>
public sealed class BlankingEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;

    private IntPtr _winEventHook = IntPtr.Zero;
    private Native.WinEventProc? _winEventProc;   // must outlive the hook

    private ulong _foregroundTick;
    private ulong _displayRequestTick;
    private ulong _fullscreenTick;
    private ulong _resumeTick;

    /// <summary>Input tick at the moment "Blank now" was pressed; cleared by real input.</summary>
    private ulong? _manualBlankAtInput;

    private bool _paused;
    private bool _disposed;

    public BlankingEngine(AppSettings settings)
    {
        _settings = settings;

        var now = Native.GetTickCount64();
        _foregroundTick = _displayRequestTick = _fullscreenTick = _resumeTick = now;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(settings.PollIntervalMs),
        };
        _timer.Tick += (_, _) => Tick();

        HookForeground();
    }

    public EngineStatus Status { get; private set; } =
        new(false, TimeSpan.Zero, TimeSpan.Zero, AwakeReason.UserInput, default, false, false);

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised when the blank/unblank decision flips.</summary>
    public event Action<bool>? BlankStateChanged;

    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!value) NoteActivity();
            Tick();
        }
    }

    public void Start() => _timer.Start();

    public void ApplyPollInterval() => _timer.Interval = TimeSpan.FromMilliseconds(_settings.PollIntervalMs);

    /// <summary>Treat "right now" as activity — used on resume, unlock and display changes.</summary>
    public void NoteActivity()
    {
        var now = Native.GetTickCount64();
        _foregroundTick = _displayRequestTick = _fullscreenTick = _resumeTick = now;
        _manualBlankAtInput = null;
    }

    /// <summary>
    /// Blank immediately and stay blanked until real keyboard/mouse input arrives.
    /// Deliberately overrides a held display request — it is an explicit user command,
    /// not a policy decision.
    /// </summary>
    public void BlankNow()
    {
        _manualBlankAtInput = LastInputTick();
        Tick();
    }

    // ------------------------------------------------------------------ category 1

    /// <summary>
    /// GetLastInputInfo returns a 32-bit tick that wraps every ~49.7 days. Subtracting in
    /// unsigned 32-bit space handles the wrap, then we rebase onto the 64-bit clock.
    /// </summary>
    private static ulong LastInputTick()
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

    private void HookForeground()
    {
        // GetLastInputInfo does not report focus changes, but Windows counts them.
        // Native invokes this, so nothing may escape it.
        _winEventProc = (_, _, _, _, _, _, _) =>
            CrashLog.GuardCallback("EVENT_SYSTEM_FOREGROUND", () => _foregroundTick = Native.GetTickCount64());

        _winEventHook = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
    }

    // ------------------------------------------------------------------ extras

    private static bool IsFullscreenActive()
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

    // ------------------------------------------------------------------ the decision

    private void Tick()
    {
        if (_disposed) return;

        var now = Native.GetTickCount64();
        var exec = ExecutionState.Read();
        var fullscreen = _settings.NeverBlankDuringFullscreen && IsFullscreenActive();

        var inputTick = LastInputTick();
        var baseline = inputTick;
        var reason = AwakeReason.UserInput;

        void Consider(ulong tick, AwakeReason candidate)
        {
            if (tick <= baseline) return;
            baseline = tick;
            reason = candidate;
        }

        if (_settings.TrackForegroundChanges)
            Consider(_foregroundTick, AwakeReason.ForegroundChange);

        if (_settings.HonourDisplayRequests && exec.DisplayRequired)
            _displayRequestTick = now;
        Consider(_displayRequestTick, AwakeReason.DisplayRequest);

        if (fullscreen)
            _fullscreenTick = now;
        Consider(_fullscreenTick, AwakeReason.Fullscreen);

        Consider(_resumeTick, AwakeReason.Resumed);

        var idle = TimeSpan.FromMilliseconds(now >= baseline ? now - baseline : 0);
        var timeout = TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds);

        // A manual "blank now" holds until the user actually touches something.
        var forced = _manualBlankAtInput is { } mark && inputTick <= mark;
        if (!forced) _manualBlankAtInput = null;

        var blanked = !_paused && (forced || idle >= timeout);

        if (_paused) reason = AwakeReason.Paused;
        else if (blanked) reason = AwakeReason.None;

        var untilBlank = blanked ? TimeSpan.Zero
            : timeout > idle ? timeout - idle : TimeSpan.Zero;

        var wasBlanked = Status.Blanked;
        Status = new EngineStatus(blanked, idle, untilBlank, reason, exec, _paused, fullscreen);

        StatusChanged?.Invoke(Status);
        if (blanked != wasBlanked) BlankStateChanged?.Invoke(blanked);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();

        if (_winEventHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        _winEventProc = null;
    }
}
