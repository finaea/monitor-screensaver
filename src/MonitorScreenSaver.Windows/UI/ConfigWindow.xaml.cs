using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MonitorScreenSaver.Core;

namespace MonitorScreenSaver.UI;

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

    public Visibility PrimaryVisibility => Target.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

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
        set { if (_configSummary == value) return; _configSummary = value; Raise(); }
    }

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
    public Visibility IgnoredVisibility => IsBlacklisted ? Visibility.Visible : Visibility.Collapsed;
}

public partial class ConfigWindow : Window
{
    private readonly App _app;
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

    public ConfigWindow(App app)
    {
        _app = app;
        _settings = app.Settings;

        InitializeComponent();

        DisplayList.ItemsSource = _rows;
        RequesterList.ItemsSource = _requesters;
        BlacklistList.ItemsSource = _blacklist;

        LoadIcon();
        LoadSettingsIntoUi();
        RebuildDisplayRows();
        RebuildEditTargets();
        LoadOverlayControls();
        RenderRequesters();

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _refresh.Tick += (_, _) => RenderStatus();

        Loaded += (_, _) => { _refresh.Start(); RenderStatus(); };
        Closed += (_, _) => { _refresh.Stop(); _app.RequestersUpdated -= OnRequestersUpdated; };
        SourceInitialized += OnSourceInitialised;

        _app.RequestersUpdated += OnRequestersUpdated;
        _loading = false;
    }

    // ------------------------------------------------------------------ chrome

    private void OnSourceInitialised(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var dark = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            var round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch
        {
            // pre-Win11 or unsupported; cosmetic only
        }
    }

    private void LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/MonitorScreenSaver.ico", UriKind.Absolute);
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            TitleIcon.Source = decoder.Frames.OrderBy(f => f.PixelWidth).FirstOrDefault(f => f.PixelWidth >= 16)
                               ?? decoder.Frames[0];
            Icon = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch
        {
            // cosmetic only
        }
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

        StartupToggle.IsChecked = AutoStart.IsEnabled;
        StartElevatedToggle.IsChecked = AutoStart.IsElevatedTask || _settings.StartElevated;

        ElevationChip.Text = PowerRequestList.IsElevated ? "elevated" : "standard";

        if (!PowerRequestList.IsElevated)
        {
            StartElevatedToggle.IsEnabled = false;
            StartupNote.Text = "Elevated autostart can only be configured while running as administrator.";
            StartupNote.Visibility = Visibility.Visible;
        }

        UpdateTimeoutEcho();
        _loading = false;
    }

    private void RebuildDisplayRows()
    {
        _rows.Clear();

        foreach (var target in _app.Overlays.Displays)
            _rows.Add(new DisplayRow(target, _settings, OnManagedChanged));

        NoDisplayHint.Visibility = _rows.Any(r => r.IsManaged) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnManagedChanged()
    {
        _app.RefreshDisplays();
        NoDisplayHint.Visibility = _rows.Any(r => r.IsManaged) ? Visibility.Collapsed : Visibility.Visible;
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

        EditTargetHost.Visibility = per && managed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditTargetHint.Visibility = per && managed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!per || managed.Count == 0)
        {
            _editTargetId = null;
            return;
        }

        if (_editTargetId is null || managed.All(t => t.StableId != _editTargetId))
            _editTargetId = managed[0].StableId;

        // Two identical monitors share a friendly name; disambiguate with the GDI name.
        var dupes = managed.GroupBy(t => t.FriendlyName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var t in managed)
        {
            var label = dupes.Contains(t.FriendlyName)
                ? $"{t.FriendlyName} · {t.DeviceName.Replace(@"\\.\", "")}"
                : t.FriendlyName;

            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "EditTarget",
                Content = label,
                Tag = t.StableId,
                IsChecked = string.Equals(t.StableId, _editTargetId, StringComparison.OrdinalIgnoreCase),
            };

            rb.Click += OnEditTargetClicked;
            EditTargetPanel.Children.Add(rb);
        }
    }

    private void OnEditTargetClicked(object sender, RoutedEventArgs e)
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
            StateDot.Fill = (Brush)FindResource("TextMuted");
            StateDetail.Text = "Blanking is suspended. Nothing will be covered.";
        }
        else if (s.Blanked)
        {
            StateText.Text = "Blanked";
            StateDot.Fill = (Brush)FindResource("Accent");
            var n = _app.Overlays.CoveredDisplayIds.Count;
            StateDetail.Text = $"Covering {n} display{(n == 1 ? "" : "s")} · idle {App.Format(s.Idle)}";
        }
        else
        {
            StateText.Text = "Awake";
            StateDot.Fill = (Brush)FindResource(s.Reason is AwakeReason.DisplayRequest or AwakeReason.Audio ? "Warn" : "Ok");
            StateDetail.Text =
                $"Held awake by {App.Describe(s.Reason)} · idle {App.Format(s.Idle)} · blanks in {App.Format(s.UntilBlank)}";
        }

        PauseButton.Content = s.Paused ? "Resume" : "Pause";

        RenderChips(s);
        RenderCoverStates();

        FooterText.Text = $"{Version} · engine tick {_settings.PollIntervalMs} ms · {_rows.Count(r => r.IsManaged)} of {_rows.Count} displays managed";
    }

    private void RenderChips(EngineStatus s)
    {
        ChipHost.Children.Clear();

        AddChip($"ES raw 0x{s.Exec.Raw:X2}", "TextMuted");

        var displayIgnored = s.Exec.DisplayRequired && _settings.BlacklistCovers(_app.Requesters);
        AddChip(displayIgnored ? "ES_DISPLAY_REQUIRED (blacklisted)" : "ES_DISPLAY_REQUIRED",
            s.Exec.DisplayRequired && !displayIgnored ? "Warn" : "TextFaint");

        AddChip("ES_SYSTEM_REQUIRED", s.Exec.SystemRequired ? "Warn" : "TextFaint");

        if (s.FullscreenActive) AddChip("fullscreen", "Warn");
        if (s.AudioActive) AddChip("audio", "Warn");

        var displayState = _app.Events.WindowsDisplayState switch
        {
            0 => "windows display: off",
            1 => "windows display: on",
            2 => "windows display: dim",
            _ => "windows display: —",
        };
        AddChip(displayState, "TextFaint");

        var presence = _app.Events.WindowsUserPresence switch
        {
            0 => "windows presence: present",
            2 => "windows presence: inactive",
            _ => "windows presence: —",
        };
        AddChip(presence, "TextFaint");
    }

    private void AddChip(string text, string brushKey)
    {
        var border = new Border
        {
            Style = (Style)FindResource("Chip"),
            Margin = new Thickness(0, 0, 6, 6),
        };

        border.Child = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("Mono"),
            Foreground = (Brush)FindResource(brushKey),
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

    private void OnRequestersUpdated() => Dispatcher.Invoke(RenderRequesters);

    private void RenderRequesters()
    {
        var snapshot = _app.Requesters;

        ElevateBanner.Visibility = snapshot.Available ? Visibility.Collapsed : Visibility.Visible;

        _requesters.Clear();
        RenderBlacklist();

        if (!snapshot.Available)
        {
            RequesterEmpty.Text = _app.Engine.Status.Exec.DisplayRequired
                ? "Something is holding the display awake right now — elevate to see which app."
                : "Nothing is holding the display awake right now.";
            RequesterEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var r in snapshot.Display)
            _requesters.Add(new RequesterRow { Requester = r, IsBlacklisted = _settings.IsBlacklisted(r.ShortName) });

        RequesterEmpty.Text = "Nothing is holding the display awake.";
        RequesterEmpty.Visibility = _requesters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderBlacklist()
    {
        _blacklist.Clear();
        foreach (var name in _settings.BlacklistedRequesters)
            _blacklist.Add(name);

        BlacklistPanel.Visibility = _blacklist.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleBlacklist(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RequesterRow row) return;

        if (row.IsBlacklisted) _app.Unblacklist(row.ShortName);
        else _app.Blacklist(row.ShortName);
    }

    private void OnUnblacklist(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string name) return;

        _app.Unblacklist(name);
    }

    // ------------------------------------------------------------------ handlers

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        _app.RefreshDisplays();
        RebuildDisplayRows();
        RebuildEditTargets();
        LoadOverlayControls();
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        if (!int.TryParse(tag, out var seconds)) return;

        TimeoutBox.Text = seconds.ToString(CultureInfo.InvariantCulture);
    }

    private void OnTimeoutChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(TimeoutBox.Text, out var seconds)) return;
        if (seconds < 10 || seconds > 24 * 60 * 60) return;

        _settings.IdleTimeoutSeconds = seconds;
        _settings.Save();
        UpdateTimeoutEcho();
    }

    private void UpdateTimeoutEcho() =>
        TimeoutEcho.Text = "= " + App.Format(TimeSpan.FromSeconds(_settings.IdleTimeoutSeconds));

    private void OnPerDisplayChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.PerMonitorConfig = PerDisplayToggle.IsChecked == true;
        _settings.Save();

        RebuildEditTargets();
        LoadOverlayControls();
        _app.Overlays.ApplyAppearance();
    }

    private void OnOverlayModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var mode = ModeVideoRadio.IsChecked == true ? OverlayMode.Video
            : ModeDimRadio.IsChecked == true ? OverlayMode.Dim
            : OverlayMode.TrueBlack;

        MutateTarget(c => c.Mode = mode);
    }

    private void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        MutateTarget(c => c.DimPercent = (int)Math.Round(e.NewValue));
    }

    private void OnStretchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var stretch = StretchFillRadio.IsChecked == true ? VideoStretch.Fill
            : StretchStretchRadio.IsChecked == true ? VideoStretch.Stretch
            : VideoStretch.Fit;

        MutateTarget(c => c.VideoStretch = stretch);
    }

    private void OnBrowseVideo(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a screensaver video",
            Filter = "Video files|*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.mkv;*.webm;*.ts;*.m2ts|All files|*.*",
            CheckFileExists = true,
        };

        var current = ReadTarget().VideoPath;
        if (!string.IsNullOrWhiteSpace(current))
        {
            try { dialog.InitialDirectory = Path.GetDirectoryName(current); } catch { /* odd path */ }
        }

        if (dialog.ShowDialog(this) != true) return;

        VideoPathBox.Text = dialog.FileName;
        MutateTarget(c => c.VideoPath = dialog.FileName);
    }

    private void UpdateOverlayModeUi()
    {
        var cfg = ReadTarget();

        DimRow.Visibility = cfg.Mode == OverlayMode.Dim ? Visibility.Visible : Visibility.Collapsed;
        VideoPanel.Visibility = cfg.Mode == OverlayMode.Video ? Visibility.Visible : Visibility.Collapsed;

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
                WarnBox.Visibility = Visibility.Visible;
                break;

            case OverlayMode.Video:
                DimWarning.Text = cfg.VideoPath is null
                    ? "No video chosen yet — this display will fall back to true black."
                    : "A playing video keeps pixels lit. More motion (colour change) or a darker video means less burn-in.";
                WarnBox.Visibility = Visibility.Visible;
                break;

            default:
                WarnBox.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void OnPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.TrackForegroundChanges = ForegroundToggle.IsChecked == true;
        _settings.HonourDisplayRequests = RequestToggle.IsChecked == true;
        _settings.NeverBlankDuringFullscreen = FullscreenToggle.IsChecked == true;
        _settings.NeverBlankDuringAudio = AudioToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = StartupToggle.IsChecked == true;
        var elevated = StartElevatedToggle.IsChecked == true && PowerRequestList.IsElevated;

        var error = AutoStart.Apply(enable, elevated);

        if (error is not null)
        {
            StartupNote.Text = error;
            StartupNote.Visibility = Visibility.Visible;
            _loading = true;
            StartupToggle.IsChecked = AutoStart.IsEnabled;
            _loading = false;
            return;
        }

        StartupNote.Visibility = PowerRequestList.IsElevated ? Visibility.Collapsed : StartupNote.Visibility;

        _settings.StartWithWindows = enable;
        _settings.StartElevated = elevated;
        _settings.Save();
    }

    private void OnRefreshRequesters(object sender, RoutedEventArgs e) => _ = _app.RefreshRequestersAsync(force: true);

    private void OnElevate(object sender, RoutedEventArgs e) => _app.RelaunchElevated();

    private void OnPause(object sender, RoutedEventArgs e) => _app.Engine.Paused = !_app.Engine.Paused;

    private void OnBlankNow(object sender, RoutedEventArgs e)
    {
        // Give the user a beat to move the mouse off the button before we cover the screen.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            _app.Engine.BlankNow();
        };
        delay.Start();
    }
}
