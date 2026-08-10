using System.Windows;
using System.Windows.Threading;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.UI;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MonitorScreenSaver;

public partial class App : System.Windows.Application
{
    internal const string RelaunchFlag = "--relaunch";
    private const string SingleInstanceName = @"Local\MonitorScreenSaver.SingleInstance";

    private static Mutex? _singleInstance;

    /// <summary>
    /// Claims the single-instance mutex, optionally waiting for a predecessor to let go.
    /// Returns null if another instance is genuinely running.
    /// </summary>
    private static Mutex? AcquireSingleInstance(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;

        while (true)
        {
            var mutex = new Mutex(true, SingleInstanceName, out var isNew);
            if (isNew) return mutex;

            mutex.Dispose();

            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(250);
        }
    }

    /// <summary>Hand the mutex over so a replacement process can claim it.</summary>
    private static void ReleaseSingleInstance()
    {
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not the owner; nothing to release
        }
        finally
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }
    }

    private AppSettings _settings = null!;
    private BlankingEngine _engine = null!;
    private OverlayManager _overlays = null!;
    private SystemEventSink _events = null!;

    private Forms.NotifyIcon _tray = null!;
    private Forms.ContextMenuStrip _menu = null!;
    private Forms.ToolStripMenuItem _headerItem = null!;
    private Forms.ToolStripMenuItem _pauseItem = null!;
    private Forms.ToolStripMenuItem _requestersItem = null!;
    private Forms.ToolStripMenuItem _startupItem = null!;

    private DispatcherTimer _watchdog = null!;
    private ConfigWindow? _config;

    private bool _sessionLocked;
    private PowerRequestList.Snapshot _requesters = new(false, null, []);
    private DateTime _lastRequesterQuery = DateTime.MinValue;

    internal AppSettings Settings => _settings;
    internal BlankingEngine Engine => _engine;
    internal OverlayManager Overlays => _overlays;
    internal SystemEventSink Events => _events;
    internal PowerRequestList.Snapshot Requesters => _requesters;

    internal event Action? RequestersUpdated;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless diagnostic paths: no tray, no windows, no engine.
        if (e.Args.Length > 0 && e.Args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            var path = e.Args.Length > 1 ? e.Args[1] : null;
            Shutdown(SelfTest.Run(path));
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--watch", StringComparison.OrdinalIgnoreCase))
        {
            // Stays alive on the message loop, logging display-power transitions.
            WatchMode.Start(e.Args.Length > 1 ? e.Args[1] : null);
            return;
        }

        // A relaunch (e.g. "restart elevated") races its own predecessor: the new process
        // starts while the old one still owns the mutex, so without a grace period it
        // would see "already running", exit, and leave nothing behind at all.
        var relaunching = e.Args.Any(a => a.Equals(RelaunchFlag, StringComparison.OrdinalIgnoreCase));

        _singleInstance = AcquireSingleInstance(relaunching ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);

        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        CrashLog.Install();

        DispatcherUnhandledException += (_, args) =>
        {
            // A background hiccup must never take the tray app down — but it must be
            // recorded, or a failure to build a window looks like "nothing happened".
            CrashLog.Write("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        _settings = AppSettings.Load();

        _overlays = new OverlayManager(_settings);
        _overlays.WakeRequested += OnWakeRequested;
        _overlays.Refresh();

        _engine = new BlankingEngine(_settings);
        _engine.BlankStateChanged += OnBlankStateChanged;
        _engine.VideoOverlayVisible = () => _overlays.AnyVideoVisible;
        _engine.Start();

        // Best effort: carry the Run-key / scheduled-task entry over from the app's
        // pre-rename identity so autostart survives the upgrade.
        if (_settings.StartWithWindows)
            AutoStart.MigrateLegacy(checkTask: _settings.StartElevated);

        _events = new SystemEventSink();
        _events.Event += OnSystemEvent;

        BuildTray();

        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _watchdog.Tick += (_, _) => WatchdogTick();
        _watchdog.Start();

        _ = RefreshRequestersAsync();

        // Show the window on first run (nothing selected yet) and after a relaunch —
        // otherwise "Restart elevated" silently replaces the process and looks like
        // nothing happened at all.
        if (_settings.ManagedDisplayIds.Count == 0 || relaunching)
            ShowConfig();
    }

    // ------------------------------------------------------------------ tray

    private void BuildTray()
    {
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkColorTable.Surface,
            ForeColor = DarkColorTable.TextCol,
            ShowImageMargin = false,
        };

        _headerItem = new Forms.ToolStripMenuItem("MonitorScreenSaver") { Enabled = false };

        _requestersItem = new Forms.ToolStripMenuItem("Holding display awake");

        _pauseItem = new Forms.ToolStripMenuItem("Pause blanking", null, (_, _) => TogglePause());

        var blankNow = new Forms.ToolStripMenuItem("Blank now", null, (_, _) => _engine.BlankNow());

        _startupItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
        };

        var settings = new Forms.ToolStripMenuItem("Settings…", null, (_, _) => ShowConfig());
        var exit = new Forms.ToolStripMenuItem("Exit", null, (_, _) => Shutdown());

        _menu.Items.AddRange(
        [
            _headerItem,
            new Forms.ToolStripSeparator(),
            _requestersItem,
            new Forms.ToolStripSeparator(),
            blankNow,
            _pauseItem,
            new Forms.ToolStripSeparator(),
            settings,
            _startupItem,
            new Forms.ToolStripSeparator(),
            exit,
        ]);

        _menu.Opening += (_, _) => UpdateMenu();

        _tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "MonitorScreenSaver",
            ContextMenuStrip = _menu,
        };

        _tray.DoubleClick += (_, _) => ShowConfig();
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MonitorScreenSaver.ico", UriKind.Absolute);
            var stream = GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                var size = Forms.SystemInformation.SmallIconSize;
                return new Drawing.Icon(stream, size);
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch
        {
            // fall through
        }

        return Drawing.SystemIcons.Application;
    }

    private void UpdateMenu()
    {
        var s = _engine.Status;

        _headerItem.Text = s.Paused
            ? "MonitorScreenSaver — paused"
            : s.Blanked
                ? "MonitorScreenSaver — blanked"
                : $"MonitorScreenSaver — {Describe(s.Reason)}, blanks in {Format(s.UntilBlank)}";

        _pauseItem.Text = s.Paused ? "Resume blanking" : "Pause blanking";

        _startupItem.Checked = AutoStart.IsEnabled;

        RebuildRequesterMenu();

        _ = RefreshRequestersAsync();
    }

    private void RebuildRequesterMenu()
    {
        _requestersItem.DropDownItems.Clear();

        var exec = _engine.Status.Exec;

        if (!_requesters.Available)
        {
            var reason = _requesters.Unavailable ?? "Unavailable.";

            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem(
                exec.DisplayRequired
                    ? "Something IS holding the display awake"
                    : "Nothing is holding the display awake")
            { Enabled = false });

            _requestersItem.DropDownItems.Add(new Forms.ToolStripSeparator());
            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem($"Names need admin — {reason}") { Enabled = false });
            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem("Restart elevated to see names", null,
                (_, _) => RelaunchElevated()));

            _requestersItem.Text = exec.DisplayRequired
                ? "Holding display awake  ●"
                : "Holding display awake";
            return;
        }

        var display = _requesters.Display.ToList();

        if (display.Count == 0)
        {
            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem("None") { Enabled = false });
            _requestersItem.Text = "Holding display awake";
            return;
        }

        foreach (var r in display)
        {
            var label = r.Reason is null
                ? $"{r.ShortName}   [{r.Kind}]"
                : $"{r.ShortName}   [{r.Kind}] — {r.Reason}";

            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem(label) { Enabled = false });
        }

        var others = _requesters.Requesters
            .Where(r => !r.RequestType.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (others.Count > 0)
        {
            _requestersItem.DropDownItems.Add(new Forms.ToolStripSeparator());
            _requestersItem.DropDownItems.Add(new Forms.ToolStripMenuItem(
                $"({others.Count} other non-display request(s))") { Enabled = false });
        }

        _requestersItem.Text = $"Holding display awake  ({display.Count})";
    }

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

    // ------------------------------------------------------------------ actions

    private void TogglePause() => _engine.Paused = !_engine.Paused;

    private void ToggleStartup()
    {
        var enable = !AutoStart.IsEnabled;
        var error = AutoStart.Apply(enable, elevated: enable && _settings.StartElevated && PowerRequestList.IsElevated);

        if (error is not null)
        {
            Forms.MessageBox.Show(error, "MonitorScreenSaver", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return;
        }

        _settings.StartWithWindows = enable;
        _settings.Save();
    }

    internal void RelaunchElevated()
    {
        if (PowerRequestList.IsElevated)
        {
            System.Windows.MessageBox.Show("MonitorScreenSaver is already running elevated.",
                "MonitorScreenSaver", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Drop the tray icon first so the user never sees two, and release the mutex so
        // the elevated process can claim it as soon as UAC is accepted.
        if (_tray is not null) _tray.Visible = false;
        ReleaseSingleInstance();

        if (PowerRequestList.TryRelaunchElevated(out var error))
        {
            Shutdown();
            return;
        }

        // Cancelled or failed — put ourselves back the way we were.
        _singleInstance = AcquireSingleInstance(TimeSpan.FromSeconds(2));
        if (_tray is not null) _tray.Visible = true;

        if (error is not null)
        {
            CrashLog.Write("RelaunchElevated", error);

            System.Windows.MessageBox.Show(
                $"Could not restart elevated.\n\n{error.Message}",
                "MonitorScreenSaver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void ShowConfig()
    {
        try
        {
            ShowConfigCore();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ShowConfig", ex);
            _config = null;

            System.Windows.MessageBox.Show(
                $"The settings window failed to open.\n\n{ex.GetType().Name}: {ex.Message}\n\nDetails: {CrashLog.FilePath}",
                "MonitorScreenSaver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowConfigCore()
    {
        if (_config is null || !_config.IsLoaded)
        {
            _config = new ConfigWindow(this);
            _config.Closed += (_, _) => _config = null;
        }

        _config.Show();
        if (_config.WindowState == WindowState.Minimized) _config.WindowState = WindowState.Normal;
        _config.Activate();
        _config.Topmost = true;
        _config.Topmost = false;
    }

    /// <summary>Re-reads displays and rebuilds overlays. Safe to call often.</summary>
    internal void RefreshDisplays()
    {
        _overlays.Refresh();
        if (_engine.Status.Blanked && !_sessionLocked) _overlays.ShowAll();
    }

    internal async Task RefreshRequestersAsync(bool force = false)
    {
        // Keep the tray submenu near-fresh without spawning powercfg constantly.
        // When not elevated the query short-circuits and costs nothing.
        var minInterval = _config is not null || _menu.Visible
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(5);

        if (!force && DateTime.UtcNow - _lastRequesterQuery < minInterval) return;
        _lastRequesterQuery = DateTime.UtcNow;

        try
        {
            _requesters = await PowerRequestList.QueryAsync().ConfigureAwait(true);
            RequestersUpdated?.Invoke();
        }
        catch
        {
            // transient; next tick retries
        }
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

    private void OnSystemEvent(SystemEventKind kind)
    {
        switch (kind)
        {
            case SystemEventKind.DisplayTopologyChanged:
                _engine.NoteActivity();
                RefreshDisplays();
                break;

            case SystemEventKind.ResumedFromSleep:
                // Fresh baseline, then rebuild: adapters often renumber across suspend.
                _engine.NoteActivity();
                _overlays.HideAll();
                RefreshDisplays();
                break;

            case SystemEventKind.SuspendingToSleep:
                _overlays.HideAll();
                break;

            case SystemEventKind.SessionLocked:
                // The lock screen lives on a different desktop; our overlay cannot cover it.
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
        _ = RefreshRequestersAsync();
    }

    // ------------------------------------------------------------------ teardown

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _watchdog?.Stop();
            _engine?.Dispose();
            _overlays?.Dispose();
            _events?.Dispose();

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            _settings?.Save();
        }
        catch
        {
            // best effort
        }

        base.OnExit(e);
    }
}
