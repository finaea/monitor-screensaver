using System.IO;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The macOS application shell — the twin of the Windows App.xaml.cs: loads settings,
/// wires the engine to the overlays and system events, puts the tray in the menu bar,
/// and pumps the AppKit event loop. LSUIElement in the bundle's Info.plist keeps it
/// out of the Dock, matching the tray-only posture on Windows.
/// </summary>
public sealed class MacApp
{
    private AppSettings _settings = null!;
    private BlankingEngine _engine = null!;
    private OverlayManager _overlays = null!;
    private MacSystemEvents _events = null!;
    private MacTray _tray = null!;
    private MacRunLoopTimer _watchdog = null!;
    private FileStream? _instanceLock;

    private bool _sessionLocked;
    private PowerSnapshot _requesters = new(false, null, []);
    private DateTime _lastRequesterQuery = DateTime.MinValue;

    internal AppSettings Settings => _settings;
    internal BlankingEngine Engine => _engine;
    internal OverlayManager Overlays => _overlays;
    internal PowerSnapshot Requesters => _requesters;

    /// <param name="openSettings">
    /// Opens the settings window as soon as the app has launched. The tray menu is owned
    /// and rendered by ControlCenter on macOS 26, so it cannot be driven from a script —
    /// this is how the window gets exercised (and screenshotted) in the real app shell.
    /// </param>
    public void Run(bool openSettings = false)
    {
        AppKit.EnsureApplication();

        if (!AcquireSingleInstance())
        {
            Console.WriteLine("MonitorScreenSaver is already running.");
            return;
        }

        CrashLog.Install();

        _settings = AppSettings.Load();

        // Until the settings window lands (Phase 5) there is no UI to pick displays,
        // so a fresh install manages everything. Persisted, so unticking later in the
        // settings window sticks.
        if (_settings.ManagedDisplayIds.Count == 0)
        {
            _settings.ManagedDisplayIds = new MacDisplayEnumerator().Enumerate().Select(d => d.StableId).ToList();
            _settings.Save();
        }

        _overlays = new OverlayManager(_settings, new MacDisplayEnumerator(), new MacOverlayFactory());
        _overlays.WakeRequested += OnWakeRequested;
        _overlays.Refresh();

        _engine = new BlankingEngine(_settings, MacPlatform.CreateEnginePlatform());
        _engine.BlankStateChanged += OnBlankStateChanged;
        _engine.VideoOverlayVisible = () => _overlays.AnyVideoVisible;
        _engine.RequesterSnapshot = () => _requesters;
        // Keep the countdown in the open menu live. Text only — rebuilding the inline
        // requester items every tick would shift them under the cursor.
        _engine.StatusChanged += s => _tray?.RenderStatus(s);
        _engine.Start();

        _events = new MacSystemEvents();
        _events.Event += OnSystemEvent;

        // The status item must be created after the app has finished launching (its
        // window-server registration happens inside [NSApp run]); a one-shot timer on
        // the running loop is the simplest way to get there without an app delegate.
        MacRunLoopTimer? trayBootstrap = null;
        trayBootstrap = new MacRunLoopTimer(TimeSpan.FromMilliseconds(50), () =>
        {
            trayBootstrap!.Stop();
            if (_tray is null) _tray = new MacTray(this);
            if (openSettings) OpenSettings();
        });
        trayBootstrap.Start();

        _watchdog = new MacRunLoopTimer(TimeSpan.FromSeconds(3), WatchdogTick);
        _watchdog.Start();

        RefreshRequesters(force: true);

        // The AppKit event loop — pumps the main CFRunLoop (engine timer, IOKit and
        // CG callbacks) and dispatches status-item/menu events. Runs until Quit.
        ObjC.SendVoid(ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication")), ObjC.Sel("run"));
    }

    // ------------------------------------------------------------------ actions

    internal void Blacklist(string shortName)
    {
        if (!_settings.IsBlacklisted(shortName))
        {
            _settings.BlacklistedRequesters.Add(shortName);
            _settings.Save();
        }
    }

    internal void Unblacklist(string shortName)
    {
        _settings.BlacklistedRequesters.RemoveAll(b =>
            string.Equals(b, shortName, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
    }

    internal void OpenSettings() => UI.MacUi.ShowSettings(this);

    internal void Quit()
    {
        try
        {
            _watchdog?.Dispose();
            _engine?.Dispose();
            _overlays?.Dispose();
            _events?.Dispose();
            _tray?.Dispose();
            _settings?.Save();
            _instanceLock?.Dispose();
        }
        catch
        {
            // best effort
        }

        ObjC.SendVoid(ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication")),
            ObjC.Sel("terminate:"), IntPtr.Zero);
    }

    internal void RefreshRequesters(bool force = false)
    {
        // The IOKit query is an in-process call (no child process like powercfg), but
        // there is still no point hammering it every engine tick.
        if (!force && DateTime.UtcNow - _lastRequesterQuery < TimeSpan.FromSeconds(2)) return;
        _lastRequesterQuery = DateTime.UtcNow;

        _requesters = MacPowerAssertions.Query();
    }

    // ------------------------------------------------------------------ events

    private void OnBlankStateChanged(bool blanked)
    {
        if (blanked && !_sessionLocked) _overlays.ShowAll();
        else _overlays.HideAll();
    }

    private void OnWakeRequested()
    {
        _engine.NoteActivity();
        _overlays.HideAll();
    }

    internal void RefreshDisplays()
    {
        _overlays.Refresh();
        if (_engine.Status.Blanked && !_sessionLocked) _overlays.ShowAll();
    }

    private void OnSystemEvent(SystemEventKind kind)
    {
        switch (kind)
        {
            case SystemEventKind.DisplayTopologyChanged:
                _engine.NoteActivity();
                RefreshDisplays();
                break;

            case SystemEventKind.ResumedFromSleep:
                // Fresh baseline, then rebuild: display ids can renumber across sleep.
                _engine.NoteActivity();
                _overlays.HideAll();
                RefreshDisplays();
                break;

            case SystemEventKind.SuspendingToSleep:
                _overlays.HideAll();
                break;

            case SystemEventKind.SessionLocked:
                // The lock screen is loginwindow's; our overlays cannot cover it.
                _sessionLocked = true;
                _overlays.HideAll();
                break;

            case SystemEventKind.SessionUnlocked:
                _sessionLocked = false;
                _engine.NoteActivity();
                RefreshDisplays();
                break;
        }
    }

    private void WatchdogTick()
    {
        if (_engine.Status.Blanked && !_sessionLocked) _overlays.Reassert();
        RefreshRequesters();
    }

    // ------------------------------------------------------------------ single instance

    /// <summary>
    /// An exclusively-opened lock file in Application Support. Named mutexes on macOS
    /// are unverified territory; an O_EXCL-style file lock is boring and works.
    /// </summary>
    private bool AcquireSingleInstance()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            _instanceLock = new FileStream(
                Path.Combine(AppSettings.Directory, ".instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ labels

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

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{t.Seconds}s";
}
