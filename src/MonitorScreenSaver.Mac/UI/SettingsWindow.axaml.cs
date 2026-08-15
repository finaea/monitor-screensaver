using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MonitorScreenSaver.Core;

namespace MonitorScreenSaver.Mac.UI;

/// <summary>One row of the display list. The mac twin of the Windows head's DisplayRow.</summary>
public sealed class DisplayRow : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _isManaged;

    public DisplayRow(DisplayTarget target, AppSettings settings, Action onChanged)
    {
        Target = target;
        _settings = settings;
        _onChanged = onChanged;
        _isManaged = settings.ManagedDisplayIds.Contains(target.StableId);
    }

    public DisplayTarget Target { get; }

    public string FriendlyName => Target.FriendlyName;

    public string Detail => $"{Target.DeviceName}    {Target.Geometry}";

    public bool IsPrimary => Target.IsPrimary;

    private string _coverState = string.Empty;
    public string CoverState
    {
        get => _coverState;
        set { if (_coverState == value) return; _coverState = value; Raise(); }
    }

    /// <summary>This display's effective overlay, shown only when per-display config is on.</summary>
    private string _configSummary = string.Empty;
    public string ConfigSummary
    {
        get => _configSummary;
        set
        {
            if (_configSummary == value) return;
            _configSummary = value;
            Raise();
            Raise(nameof(HasConfigSummary));
        }
    }

    /// <summary>
    /// Avalonia has no "collapse when the text is empty" trigger like the WPF template
    /// used, so the row exposes the emptiness as a bindable flag instead.
    /// </summary>
    public bool HasConfigSummary => _configSummary.Length > 0;

    public bool IsManaged
    {
        get => _isManaged;
        set
        {
            if (_isManaged == value) return;
            _isManaged = value;

            if (value)
            {
                if (!_settings.ManagedDisplayIds.Contains(Target.StableId))
                    _settings.ManagedDisplayIds.Add(Target.StableId);
            }
            else
            {
                _settings.ManagedDisplayIds.Remove(Target.StableId);
            }

            _settings.Save();
            Raise();
            _onChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One row of the "holding the display awake" list, with its blacklist state.</summary>
public sealed class RequesterRow
{
    public required PowerRequester Requester { get; init; }
    public required bool IsBlacklisted { get; init; }

    public string ShortName => Requester.ShortName;
    public RequesterKind Kind => Requester.Kind;
    public string? Reason => Requester.Reason;

    public string ToggleLabel => IsBlacklisted ? "Unblacklist" : "Blacklist";
    public double RowOpacity => IsBlacklisted ? 0.45 : 1.0;
}

public partial class SettingsWindow : Window
{
    private readonly MacApp _app;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refresh;
    private readonly ObservableCollection<DisplayRow> _rows = [];
    private readonly ObservableCollection<RequesterRow> _requesters = [];
    private readonly ObservableCollection<string> _blacklist = [];

    private static readonly string Version =
        $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";

    private bool _loading = true;

    /// <summary>Display whose config the overlay card edits; null = the shared config.</summary>
    private string? _editTargetId;

    /// <summary>
    /// The holder list is polled rather than pushed (there is no RequestersUpdated event
    /// on the mac shell — MacApp refreshes the snapshot on its watchdog tick). Rebuilding
    /// the items every tick would shift the Blacklist buttons under the cursor, so the
    /// rebuild only happens when this signature of the visible content changes.
    /// </summary>
    private string _requesterSignature = "";

    public SettingsWindow(MacApp app)
    {
        _app = app;
        _settings = app.Settings;

        InitializeComponent();

        DisplayList.ItemsSource = _rows;
        RequesterList.ItemsSource = _requesters;
        BlacklistList.ItemsSource = _blacklist;

        TitleBar.PointerPressed += OnTitleBarPressed;

        // Tunnelling, not bubbling: while recording a shortcut we need first refusal on the
        // keystroke. The Set-a-shortcut button has focus at that moment, and a bubbling
        // handler would never see Space or Enter — the button would have activated itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        LoadSettingsIntoUi();
        RebuildDisplayRows();
        RebuildEditTargets();
        LoadOverlayControls();
        RenderRequesters();

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _refresh.Tick += (_, _) =>
        {
            RenderStatus();
            RenderRequestersIfChanged();
        };

        Opened += (_, _) => { _refresh.Start(); RenderStatus(); };
        Closed += (_, _) => _refresh.Stop();

        _loading = false;
    }

    // ------------------------------------------------------------------ chrome

    /// <summary>
    /// The window is custom-chromed (no traffic lights), so the title bar has to move the
    /// window itself — the same job WindowChrome.CaptionHeight does on Windows.
    /// </summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ------------------------------------------------------------------ load

    private void LoadSettingsIntoUi()
    {
        _loading = true;

        TimeoutBox.Text = _settings.IdleTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        PerDisplayToggle.IsChecked = _settings.PerMonitorConfig;
        ForegroundToggle.IsChecked = _settings.TrackForegroundChanges;
        RequestToggle.IsChecked = _settings.HonourDisplayRequests;
        FullscreenToggle.IsChecked = _settings.NeverBlankDuringFullscreen;
        AudioToggle.IsChecked = _settings.NeverBlankDuringAudio;

        StartupToggle.IsChecked = MacAutoStart.IsEnabled;

        UpdateTimeoutEcho();
        RenderHotkey();
        _loading = false;
    }

    private void RebuildDisplayRows()
    {
        _rows.Clear();

        foreach (var target in _app.Overlays.Displays)
            _rows.Add(new DisplayRow(target, _settings, OnManagedChanged));

        NoDisplayHint.IsVisible = !_rows.Any(r => r.IsManaged);
    }

    private void OnManagedChanged()
    {
        _app.RefreshDisplays();
        NoDisplayHint.IsVisible = !_rows.Any(r => r.IsManaged);
        RebuildEditTargets();
        LoadOverlayControls();
    }

    // ------------------------------------------------------- overlay config target

    /// <summary>The config the overlay card is currently showing. Read-only resolution.</summary>
    private MonitorConfig ReadTarget() =>
        _settings.PerMonitorConfig && _editTargetId is not null
            ? _settings.ConfigFor(_editTargetId)
            : _settings.GlobalConfig();

    /// <summary>Mutates the edited config (override or shared), saves, and pushes to overlays.</summary>
    private void MutateTarget(Action<MonitorConfig> mutate)
    {
        if (_settings.PerMonitorConfig && _editTargetId is not null)
        {
            mutate(_settings.OverrideFor(_editTargetId));
        }
        else
        {
            var g = _settings.GlobalConfig();
            mutate(g);
            _settings.ApplyGlobal(g);
        }

        _settings.Save();
        _app.Overlays.ApplyAppearance();
        UpdateOverlayModeUi();
    }

    /// <summary>One segment button per managed display, shown when per-display config is on.</summary>
    private void RebuildEditTargets()
    {
        EditTargetPanel.Children.Clear();

        var per = _settings.PerMonitorConfig;
        var managed = _rows.Where(r => r.IsManaged).Select(r => r.Target).ToList();

        EditTargetHost.IsVisible = per && managed.Count > 0;
        EditTargetHint.IsVisible = per && managed.Count == 0;

        if (!per || managed.Count == 0)
        {
            _editTargetId = null;
            return;
        }

        if (_editTargetId is null || managed.All(t => t.StableId != _editTargetId))
            _editTargetId = managed[0].StableId;

        // Two identical monitors share a friendly name; disambiguate with the device name.
        var dupes = managed.GroupBy(t => t.FriendlyName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var t in managed)
        {
            var label = dupes.Contains(t.FriendlyName)
                ? $"{t.FriendlyName} · {t.DeviceName}"
                : t.FriendlyName;

            var rb = new RadioButton
            {
                Classes = { "segment" },
                GroupName = "EditTarget",
                Content = label,
                Tag = t.StableId,
                IsChecked = string.Equals(t.StableId, _editTargetId, StringComparison.OrdinalIgnoreCase),
            };

            rb.Click += OnEditTargetClicked;
            EditTargetPanel.Children.Add(rb);
        }
    }

    private void OnEditTargetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string id) return;

        _editTargetId = id;
        LoadOverlayControls();
    }

    /// <summary>Pushes the edited config into the overlay card's controls.</summary>
    private void LoadOverlayControls()
    {
        var wasLoading = _loading;
        _loading = true;

        var cfg = ReadTarget();

        ModeBlackRadio.IsChecked = cfg.Mode == OverlayMode.TrueBlack;
        ModeDimRadio.IsChecked = cfg.Mode == OverlayMode.Dim;
        ModeVideoRadio.IsChecked = cfg.Mode == OverlayMode.Video;

        DimSlider.Value = cfg.DimPercent;

        VideoPathBox.Text = cfg.VideoPath ?? "";
        StretchFitRadio.IsChecked = cfg.VideoStretch == VideoStretch.Fit;
        StretchFillRadio.IsChecked = cfg.VideoStretch == VideoStretch.Fill;
        StretchStretchRadio.IsChecked = cfg.VideoStretch == VideoStretch.Stretch;

        UpdateOverlayModeUi();

        _loading = wasLoading;
    }

    // ------------------------------------------------------------------ render

    private void RenderStatus()
    {
        var s = _app.Engine.Status;

        if (s.Paused)
        {
            StateText.Text = "Paused";
            StateDot.Fill = Brush("TextMuted");
            StateDetail.Text = "Blanking is suspended. Nothing will be covered.";
        }
        else if (s.Blanked)
        {
            StateText.Text = "Blanked";
            StateDot.Fill = Brush("Accent");
            var n = _app.Overlays.CoveredDisplayIds.Count;
            StateDetail.Text = $"Covering {n} display{(n == 1 ? "" : "s")} · idle {MacApp.Format(s.Idle)}";
        }
        else
        {
            StateText.Text = "Awake";
            StateDot.Fill = Brush(s.Reason is AwakeReason.DisplayRequest or AwakeReason.Audio ? "Warn" : "Ok");
            StateDetail.Text =
                $"Held awake by {MacApp.Describe(s.Reason)} · idle {MacApp.Format(s.Idle)} · blanks in {MacApp.Format(s.UntilBlank)}";
        }

        PauseButton.Content = s.Paused ? "Resume" : "Pause";

        RenderChips(s);
        RenderCoverStates();

        FooterText.Text = $"{Version} · engine tick {_settings.PollIntervalMs} ms · " +
                          $"{_rows.Count(r => r.IsManaged)} of {_rows.Count} displays managed";
    }

    /// <summary>
    /// The Windows head shows the ES_* flags plus the two Windows-only signals
    /// (display state, user presence). Here the flags are the IOKit assertion families
    /// the mac execution source actually reads, so the chips name those instead.
    /// </summary>
    private void RenderChips(EngineStatus s)
    {
        ChipHost.Children.Clear();

        AddChip($"assertions raw 0x{s.Exec.Raw:X2}", "TextMuted");

        var displayIgnored = s.Exec.DisplayRequired && _settings.BlacklistCovers(_app.Requesters);
        AddChip(displayIgnored ? "PreventUserIdleDisplaySleep (blacklisted)" : "PreventUserIdleDisplaySleep",
            s.Exec.DisplayRequired && !displayIgnored ? "Warn" : "TextFaint");

        AddChip("PreventUserIdleSystemSleep", s.Exec.SystemRequired ? "Warn" : "TextFaint");
        AddChip("UserIsActive", s.Exec.UserPresent ? "Warn" : "TextFaint");

        if (s.FullscreenActive) AddChip("fullscreen", "Warn");
        if (s.AudioActive) AddChip("audio", "Warn");
    }

    private void AddChip(string text, string brushKey)
    {
        var border = new Border
        {
            Classes = { "chip" },
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                Classes = { "mono" },
                Foreground = Brush(brushKey),
            },
        };

        ChipHost.Children.Add(border);
    }

    private void RenderCoverStates()
    {
        var covered = _app.Overlays.CoveredDisplayIds;
        var per = _settings.PerMonitorConfig;

        foreach (var row in _rows)
        {
            row.CoverState = !row.IsManaged
                ? "not managed"
                : covered.Contains(row.Target.StableId)
                    ? "covered"
                    : "visible";

            row.ConfigSummary = per && row.IsManaged
                ? _settings.ConfigFor(row.Target.StableId).Summary
                : "";
        }
    }

    private void RenderRequestersIfChanged()
    {
        var snapshot = _app.Requesters;

        var signature = string.Join('|', snapshot.Display.Select(r => $"{r.ShortName}~{r.Reason}")) +
                        "#" + string.Join('|', _settings.BlacklistedRequesters) +
                        "#" + snapshot.Available;

        if (signature == _requesterSignature) return;

        _requesterSignature = signature;
        RenderRequesters();
    }

    private void RenderRequesters()
    {
        var snapshot = _app.Requesters;

        _requesters.Clear();
        RenderBlacklist();

        if (!snapshot.Available)
        {
            // Attribution is never unavailable on macOS (no elevation involved), so this
            // only fires if the IOKit query itself failed.
            RequesterEmpty.Text = $"Holder list unavailable — {snapshot.Unavailable}";
            RequesterEmpty.IsVisible = true;
            return;
        }

        foreach (var r in snapshot.Display)
            _requesters.Add(new RequesterRow { Requester = r, IsBlacklisted = _settings.IsBlacklisted(r.ShortName) });

        RequesterEmpty.Text = "Nothing is holding the display awake.";
        RequesterEmpty.IsVisible = _requesters.Count == 0;
    }

    private void RenderBlacklist()
    {
        _blacklist.Clear();
        foreach (var name in _settings.BlacklistedRequesters)
            _blacklist.Add(name);

        BlacklistPanel.IsVisible = _blacklist.Count > 0;
    }

    private void OnToggleBlacklist(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not RequesterRow row) return;

        if (row.IsBlacklisted) _app.Unblacklist(row.ShortName);
        else _app.Blacklist(row.ShortName);

        RenderRequesters();
    }

    private void OnUnblacklist(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not string name) return;

        _app.Unblacklist(name);
        RenderRequesters();
    }

    // ------------------------------------------------------------------ handlers

    private void OnMinimise(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnRescan(object? sender, RoutedEventArgs e)
    {
        _app.RefreshDisplays();
        RebuildDisplayRows();
        RebuildEditTargets();
        LoadOverlayControls();
    }

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (!int.TryParse(tag, out var seconds)) return;

        TimeoutBox.Text = seconds.ToString(CultureInfo.InvariantCulture);
    }

    private void OnTimeoutChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(TimeoutBox.Text, out var seconds)) return;
        if (seconds < 10 || seconds > 24 * 60 * 60) return;

        _settings.IdleTimeoutSeconds = seconds;
        _settings.Save();
        UpdateTimeoutEcho();
    }

    private void UpdateTimeoutEcho() =>
        TimeoutEcho.Text = "= " + MacApp.Format(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds));

    // ------------------------------------------------------------------ shortcut

    /// <summary>True while the next keystroke is being read as the new shortcut.</summary>
    private bool _recording;

    /// <summary>
    /// Shows the configured shortcut and what actually happened to it — registered, refused,
    /// or not set. The state line matters more here than in most settings: macOS cannot tell
    /// us whether another *app* holds the same combination, so this line is the only place a
    /// user finds out that the shortcut they chose was blocked for a reason we could see.
    /// </summary>
    private void RenderHotkey()
    {
        if (_recording)
        {
            HotkeyButton.Content = "Press a combination…";
            HotkeyStateText.Text = "Two or more modifiers, including Control or Option. Esc to cancel.";
            HotkeyStateText.Foreground = Brush("TextMuted");
            return;
        }

        var spec = _settings.BlankNowHotkeySpec;
        HotkeyButton.Content = spec is null ? "Set a shortcut…" : spec.Display();

        var status = _app.Hotkey.Status;
        HotkeyStateText.Text = status.State switch
        {
            HotkeyState.Active => "Listening system-wide — works while another app is in front.",
            HotkeyState.Blocked => $"Not taken — {status.Detail}",
            HotkeyState.Failed => $"Could not register — {status.Detail}",
            _ => "No shortcut — blanking is a menu bar click away regardless.",
        };

        HotkeyStateText.Foreground = status.State switch
        {
            HotkeyState.Active => Brush("Ok"),
            HotkeyState.Blocked or HotkeyState.Failed => Brush("Warn"),
            _ => Brush("TextMuted"),
        };
    }

    private void OnRecordHotkey(object? sender, RoutedEventArgs e)
    {
        _recording = true;
        RenderHotkey();
    }

    private void OnClearHotkey(object? sender, RoutedEventArgs e)
    {
        _recording = false;
        _settings.BlankNowHotkey = null;
        _settings.Save();
        _app.ApplyHotkey();
        RenderHotkey();
    }

    /// <summary>
    /// Reads the recorded combination. Anything macOS has already claimed never arrives here
    /// — the system consumes the keystroke first — which makes this the one conflict check
    /// that needs no table: press ⌘Space and Spotlight opens instead of this field filling in.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_recording) return;

        // Modifiers alone are not a shortcut; wait for the key they modify.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _recording = false;
            RenderHotkey();
            return;
        }

        if (KeyName(e.Key) is not { } key)
        {
            _recording = false;
            RenderHotkey();
            HotkeyStateText.Text = $"{e.Key} cannot be used — letters, digits and F1-F20 only.";
            HotkeyStateText.Foreground = Brush("Warn");
            return;
        }

        var spec = new HotkeySpec(Modifiers(e.KeyModifiers), key);
        _recording = false;

        // Refuse rather than save: a shortcut we already know is taken would either not fire
        // or would steal a keystroke the user needs elsewhere.
        if (_app.Hotkey.Blocker(spec) is { } blocker)
        {
            RenderHotkey();
            HotkeyStateText.Text = $"{spec.Display()} not used — {blocker}";
            HotkeyStateText.Foreground = Brush("Warn");
            return;
        }

        _settings.BlankNowHotkey = spec.ToString();
        _settings.Save();
        _app.ApplyHotkey();
        RenderHotkey();
    }

    private static HotkeyModifiers Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? HotkeyModifiers.Control : 0) |
        (modifiers.HasFlag(KeyModifiers.Alt) ? HotkeyModifiers.Alt : 0) |
        (modifiers.HasFlag(KeyModifiers.Shift) ? HotkeyModifiers.Shift : 0) |
        (modifiers.HasFlag(KeyModifiers.Meta) ? HotkeyModifiers.Command : 0);

    /// <summary>Avalonia's key to the canonical name stored in settings.</summary>
    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
        >= Key.F1 and <= Key.F20 => key.ToString(),
        Key.Space => "Space",
        _ => null,
    };

    private void OnPerDisplayChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.PerMonitorConfig = PerDisplayToggle.IsChecked == true;
        _settings.Save();

        RebuildEditTargets();
        LoadOverlayControls();
        _app.Overlays.ApplyAppearance();
    }

    private void OnOverlayModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var mode = ModeVideoRadio.IsChecked == true ? OverlayMode.Video
            : ModeDimRadio.IsChecked == true ? OverlayMode.Dim
            : OverlayMode.TrueBlack;

        MutateTarget(c => c.Mode = mode);
    }

    private void OnDimChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        MutateTarget(c => c.DimPercent = (int)Math.Round(e.NewValue));
    }

    private void OnStretchChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var stretch = StretchFillRadio.IsChecked == true ? VideoStretch.Fill
            : StretchStretchRadio.IsChecked == true ? VideoStretch.Stretch
            : VideoStretch.Fit;

        MutateTarget(c => c.VideoStretch = stretch);
    }

    private async void OnBrowseVideo(object? sender, RoutedEventArgs e)
    {
        try
        {
            var start = ReadTarget().VideoPath;
            IStorageFolder? folder = null;

            if (!string.IsNullOrWhiteSpace(start))
            {
                try
                {
                    var dir = Path.GetDirectoryName(start);
                    if (dir is not null) folder = await StorageProvider.TryGetFolderFromPathAsync(dir);
                }
                catch
                {
                    // odd path; fall back to the system default location
                }
            }

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a screensaver video",
                AllowMultiple = false,
                SuggestedStartLocation = folder,
                FileTypeFilter =
                [
                    new FilePickerFileType("Video files")
                    {
                        // AVFoundation containers; the Windows head also lists wmv/avi/mkv/webm,
                        // which AVFoundation cannot decode (documented gap in the mac README).
                        Patterns = ["*.mp4", "*.m4v", "*.mov", "*.ts", "*.m2ts"],
                        AppleUniformTypeIdentifiers = ["public.movie"],
                    },
                    new FilePickerFileType("All files") { Patterns = ["*"] },
                ],
            });

            var picked = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(picked)) return;

            VideoPathBox.Text = picked;
            MutateTarget(c => c.VideoPath = picked);
        }
        catch (Exception ex)
        {
            CrashLog.Write("SettingsWindow.OnBrowseVideo", ex);
        }
    }

    private void UpdateOverlayModeUi()
    {
        var cfg = ReadTarget();

        DimRow.IsVisible = cfg.Mode == OverlayMode.Dim;
        VideoPanel.IsVisible = cfg.Mode == OverlayMode.Video;

        ModeHint.Text = cfg.Mode switch
        {
            OverlayMode.Dim => "The screen stays readable underneath, at reduced brightness.",
            OverlayMode.Video => "A muted looping video instead of black — still fights burn-in, just less than true black.",
            _ => "Pixels emit nothing. Burn-in accrual stops completely.",
        };

        DimEcho.Text = $"{cfg.DimPercent}%  ·  α {cfg.Alpha}";

        switch (cfg.Mode)
        {
            case OverlayMode.Dim when cfg.DimPercent < 100:
                DimWarning.Text = "Dim only slows burn-in. Anything below 100% still emits light.";
                WarnBox.IsVisible = true;
                break;

            case OverlayMode.Video:
                DimWarning.Text = cfg.VideoPath is null
                    ? "No video chosen yet — this display will fall back to true black."
                    : "A playing video keeps pixels lit. More motion (colour change) or a darker video means less burn-in.";
                WarnBox.IsVisible = true;
                break;

            default:
                WarnBox.IsVisible = false;
                break;
        }
    }

    private void OnPolicyChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.TrackForegroundChanges = ForegroundToggle.IsChecked == true;
        _settings.HonourDisplayRequests = RequestToggle.IsChecked == true;
        _settings.NeverBlankDuringFullscreen = FullscreenToggle.IsChecked == true;
        _settings.NeverBlankDuringAudio = AudioToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnStartupChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = StartupToggle.IsChecked == true;
        var error = MacAutoStart.Apply(enable);

        if (error is not null)
        {
            StartupNote.Text = error;
            StartupNote.IsVisible = true;

            _loading = true;
            StartupToggle.IsChecked = MacAutoStart.IsEnabled;
            _loading = false;
            return;
        }

        StartupNote.IsVisible = false;

        // Same JSON key as the Windows head ("start with the OS"); only the label differs.
        _settings.StartWithWindows = enable;
        _settings.Save();
    }

    private void OnRefreshRequesters(object? sender, RoutedEventArgs e)
    {
        _app.RefreshRequesters(force: true);
        RenderRequesters();
    }

    private void OnPause(object? sender, RoutedEventArgs e) => _app.Engine.Paused = !_app.Engine.Paused;

    // Instant, with no beat to let go of the mouse first: the engine's manual-blank hold now
    // ignores input until it has settled (BlankingEngine.ManualBlankSettleMs), so the click
    // being released no longer cancels the blank it just asked for.
    private void OnBlankNow(object? sender, RoutedEventArgs e) => _app.Engine.BlankNow();

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, out var value) ? value as IBrush : null;
}
