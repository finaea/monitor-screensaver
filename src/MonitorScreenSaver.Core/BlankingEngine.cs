namespace MonitorScreenSaver.Core;

public enum AwakeReason
{
    /// <summary>Idle timer has expired and nothing is holding the display; we blank.</summary>
    None,
    UserInput,
    ForegroundChange,
    DisplayRequest,
    Fullscreen,
    Audio,
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
    bool FullscreenActive,
    bool AudioActive);

/// <summary>
/// Reimplements the OS display-idle decision so it can be applied to a subset of
/// monitors instead of all of them. The policy is Windows' (documented under
/// SetThreadExecutionState); macOS' power-assertion model maps onto the same shape.
///
/// The OS blanks when BOTH hold:
///   Category 1 — the idle timer expired. Per the SetThreadExecutionState docs the system
///                "automatically detects activities such as local keyboard or mouse input,
///                server activity, and changing window focus".
///   Category 2 — no process holds a DISPLAY power request. ES_DISPLAY_REQUIRED
///                "forces the display to be on by resetting the display idle timer".
///
/// Because a held request continuously *resets* the timer, we model it the same way:
/// while it is held we keep pushing the activity baseline forward, so releasing it starts
/// a fresh full timeout rather than blanking instantly.
///
/// All OS access goes through <see cref="EnginePlatform"/>; this class is portable.
/// </summary>
public sealed class BlankingEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly EnginePlatform _os;
    private readonly ITickTimer _timer;
    private readonly IDisposable _foregroundWatch;

    private ulong _foregroundTick;
    private ulong _displayRequestTick;
    private ulong _fullscreenTick;
    private ulong _audioTick;
    private ulong _resumeTick;

    /// <summary>A manual "blank now" is in force; cleared by real input, once armed.</summary>
    private bool _manualBlank;

    /// <summary>
    /// The input tick the manual blank watches, set once input has settled. Null while the
    /// gesture that asked for the blank is still finishing — see <see cref="ManualBlankSettleMs"/>.
    /// </summary>
    private ulong? _manualBlankMark;

    /// <summary>
    /// How long input has to be quiet before a manual blank starts watching for input.
    ///
    /// The gesture that asks for the blank is itself input, and so is the end of it: releasing
    /// a shortcut key, releasing its modifiers, letting go of the mouse button. Watching from
    /// the moment of the request meant the release cancelled the blank a few milliseconds
    /// later — the screens went black and came straight back when the user lifted their
    /// finger. Long enough to cover a key release and its modifiers, short enough that a real
    /// intent to wake still gets through immediately: any gap of this length arms the hold,
    /// and the next keystroke or mouse movement after that wakes as usual.
    ///
    /// The same shape as the Windows overlay's own SettleTime (400 ms, OverlayWindow.cs), which
    /// exists for the neighbouring problem: ignoring the mouse traffic caused by a window
    /// appearing under the cursor.
    /// </summary>
    private const ulong ManualBlankSettleMs = 500;

    private bool _paused;
    private bool _disposed;

    public BlankingEngine(AppSettings settings, EnginePlatform os)
    {
        _settings = settings;
        _os = os;

        var now = os.Clock.NowMs;
        _foregroundTick = _displayRequestTick = _fullscreenTick = _audioTick = _resumeTick = now;

        _timer = os.TimerFactory(TimeSpan.FromMilliseconds(settings.PollIntervalMs), Tick);

        // GetLastInputInfo-style input tracking does not report focus changes, but the
        // OS counts them as activity, so they are watched separately.
        _foregroundWatch = os.ForegroundWatchFactory(() => _foregroundTick = _os.Clock.NowMs);
    }

    public EngineStatus Status { get; private set; } =
        new(false, TimeSpan.Zero, TimeSpan.Zero, AwakeReason.UserInput, default, false, false, false);

    /// <summary>
    /// Set by the app to report whether any visible overlay is playing video. Media
    /// playback can file its own DISPLAY power request, and honouring our own request
    /// would unblank the screens we just covered — so while this returns true, display
    /// requests are not treated as fresh activity.
    /// </summary>
    public Func<bool>? VideoOverlayVisible { get; set; }

    /// <summary>
    /// Set by the app to expose the latest per-caller attribution snapshot. Used to
    /// ignore a held display request when every current DISPLAY holder is blacklisted.
    /// The snapshot degrades to unavailable without attribution rights, which disables
    /// the blacklist rather than guessing.
    /// </summary>
    public Func<PowerSnapshot>? RequesterSnapshot { get; set; }

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
        var now = _os.Clock.NowMs;
        _foregroundTick = _displayRequestTick = _fullscreenTick = _audioTick = _resumeTick = now;
        ClearManualBlank();
    }

    /// <summary>
    /// Blank immediately and stay blanked until real keyboard/mouse input arrives — where
    /// "real" excludes the tail of the gesture that asked for it (see
    /// <see cref="ManualBlankSettleMs"/>). Deliberately overrides a held display request: it is
    /// an explicit user command, not a policy decision.
    /// </summary>
    public void BlankNow()
    {
        _manualBlank = true;
        _manualBlankMark = null;
        Tick();
    }

    private void ClearManualBlank()
    {
        _manualBlank = false;
        _manualBlankMark = null;
    }

    // ------------------------------------------------------------------ the decision

    private void Tick()
    {
        if (_disposed) return;

        var now = _os.Clock.NowMs;
        var exec = _os.ExecutionState.Read();
        var fullscreen = _settings.NeverBlankDuringFullscreen && _os.Fullscreen.IsFullscreenActive();

        var inputTick = _os.Clock.LastInputMs;
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

        // While our own video overlays are on screen, a DISPLAY request may be ours
        // (media pipelines file "Playing video" requests). Honouring it would unblank
        // the screens we just covered, so requests are ignored until real input or
        // focus wakes us. Without attribution rights requests cannot be blamed on
        // anyone, so this deliberately also ignores foreign requests that start
        // mid-blank — those almost always come with input (Parsec connect, call
        // starting) anyway.
        var suppressRequests = Status.Blanked && VideoOverlayVisible?.Invoke() == true;

        // A request whose only holders are blacklisted does not count. The snapshot
        // refreshes every few seconds, so a holder change can be misjudged for at most
        // that long before the next refresh corrects it.
        var blacklisted = exec.DisplayRequired
            && RequesterSnapshot?.Invoke() is { } snap
            && _settings.BlacklistCovers(snap);

        if (_settings.HonourDisplayRequests && exec.DisplayRequired && !blacklisted && !suppressRequests)
            _displayRequestTick = now;
        Consider(_displayRequestTick, AwakeReason.DisplayRequest);

        if (fullscreen)
            _fullscreenTick = now;
        Consider(_fullscreenTick, AwakeReason.Fullscreen);

        // Audio only *prevents* blanking; it never unblanks. Someone starting music
        // after the screens went dark wants them to stay dark, so the tick is not
        // pushed while blanked — the frozen tick sits in the past and never wins.
        var audio = _settings.NeverBlankDuringAudio && !Status.Blanked && _os.Audio.IsPlaying();
        if (audio)
            _audioTick = now;
        Consider(_audioTick, AwakeReason.Audio);

        Consider(_resumeTick, AwakeReason.Resumed);

        var idle = TimeSpan.FromMilliseconds(now >= baseline ? now - baseline : 0);
        var timeout = TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds);

        // A manual "blank now" holds until the user actually touches something — but not until
        // the gesture that asked for it has finished. Until input has been quiet for
        // ManualBlankSettleMs, every event still belongs to that gesture (the shortcut's key
        // release, its modifiers, the mouse button coming up), so the hold ignores input
        // entirely; after that it watches the settled tick and the next real event cancels it.
        var forced = false;

        if (_manualBlank)
        {
            if (_manualBlankMark is { } mark)
            {
                forced = inputTick <= mark;
                if (!forced) ClearManualBlank();
            }
            else
            {
                forced = true;
                if (now >= inputTick && now - inputTick >= ManualBlankSettleMs)
                    _manualBlankMark = inputTick;
            }
        }

        var blanked = !_paused && (forced || idle >= timeout);

        if (_paused) reason = AwakeReason.Paused;
        else if (blanked) reason = AwakeReason.None;

        var untilBlank = blanked ? TimeSpan.Zero
            : timeout > idle ? timeout - idle : TimeSpan.Zero;

        var wasBlanked = Status.Blanked;
        Status = new EngineStatus(blanked, idle, untilBlank, reason, exec, _paused, fullscreen, audio);

        StatusChanged?.Invoke(Status);
        if (blanked != wasBlanked) BlankStateChanged?.Invoke(blanked);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Dispose();
        _foregroundWatch.Dispose();
    }
}
