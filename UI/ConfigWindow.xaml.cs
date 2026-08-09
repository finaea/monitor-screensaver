using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MonitorDim.Core;

namespace MonitorDim.UI;

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

public partial class ConfigWindow : Window
{
    private readonly App _app;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refresh;
    private readonly ObservableCollection<DisplayRow> _rows = [];
    private readonly ObservableCollection<PowerRequester> _requesters = [];

    private bool _loading = true;

    public ConfigWindow(App app)
    {
        _app = app;
        _settings = app.Settings;

        InitializeComponent();

        DisplayList.ItemsSource = _rows;
        RequesterList.ItemsSource = _requesters;

        LoadIcon();
        LoadSettingsIntoUi();
        RebuildDisplayRows();
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
            var uri = new Uri("pack://application:,,,/Assets/MonitorDim.ico", UriKind.Absolute);
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

        ModeBlackRadio.IsChecked = _settings.Mode == OverlayMode.TrueBlack;
        ModeDimRadio.IsChecked = _settings.Mode == OverlayMode.Dim;
        DimSlider.Value = _settings.DimPercent;
        UpdateOverlayModeUi();
        ForegroundToggle.IsChecked = _settings.TrackForegroundChanges;
        RequestToggle.IsChecked = _settings.HonourDisplayRequests;
        FullscreenToggle.IsChecked = _settings.NeverBlankDuringFullscreen;

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
            StateDot.Fill = (Brush)FindResource(s.Reason == AwakeReason.DisplayRequest ? "Warn" : "Ok");
            StateDetail.Text =
                $"Held awake by {App.Describe(s.Reason)} · idle {App.Format(s.Idle)} · blanks in {App.Format(s.UntilBlank)}";
        }

        PauseButton.Content = s.Paused ? "Resume" : "Pause";

        RenderChips(s);
        RenderCoverStates();

        FooterText.Text = $"v1.0.0 · engine tick {_settings.PollIntervalMs} ms · {_rows.Count(r => r.IsManaged)} of {_rows.Count} displays managed";
    }

    private void RenderChips(EngineStatus s)
    {
        ChipHost.Children.Clear();

        AddChip($"ES raw 0x{s.Exec.Raw:X2}", "TextMuted");
        AddChip("ES_DISPLAY_REQUIRED", s.Exec.DisplayRequired ? "Warn" : "TextFaint");
        AddChip("ES_SYSTEM_REQUIRED", s.Exec.SystemRequired ? "Warn" : "TextFaint");

        if (s.FullscreenActive) AddChip("fullscreen", "Warn");

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

        foreach (var row in _rows)
        {
            row.CoverState = !row.IsManaged
                ? "not managed"
                : covered.Contains(row.Target.StableId)
                    ? "covered"
                    : "visible";
        }
    }

    private void OnRequestersUpdated() => Dispatcher.Invoke(RenderRequesters);

    private void RenderRequesters()
    {
        var snapshot = _app.Requesters;

        ElevateBanner.Visibility = snapshot.Available ? Visibility.Collapsed : Visibility.Visible;

        _requesters.Clear();

        if (!snapshot.Available)
        {
            RequesterEmpty.Text = _app.Engine.Status.Exec.DisplayRequired
                ? "Something is holding the display awake right now — elevate to see which app."
                : "Nothing is holding the display awake right now.";
            RequesterEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var r in snapshot.Display)
            _requesters.Add(r);

        RequesterEmpty.Text = "Nothing is holding the display awake.";
        RequesterEmpty.Visibility = _requesters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ handlers

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnRescan(object sender, RoutedEventArgs e)
    {
        _app.RefreshDisplays();
        RebuildDisplayRows();
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

    private void OnOverlayModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.Mode = ModeDimRadio.IsChecked == true ? OverlayMode.Dim : OverlayMode.TrueBlack;
        _settings.Save();

        UpdateOverlayModeUi();
        _app.Overlays.ApplyAppearance();
    }

    private void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _settings.DimPercent = (int)Math.Round(e.NewValue);
        _settings.Save();

        UpdateOverlayModeUi();
        _app.Overlays.ApplyAppearance();
    }

    private void UpdateOverlayModeUi()
    {
        var dim = _settings.Mode == OverlayMode.Dim;

        DimRow.Visibility = dim ? Visibility.Visible : Visibility.Collapsed;
        DimWarning.Visibility = dim && _settings.DimPercent < 100 ? Visibility.Visible : Visibility.Collapsed;

        ModeHint.Text = dim
            ? "The screen stays readable underneath, at reduced brightness."
            : "Pixels emit nothing. Burn-in accrual stops completely.";

        DimEcho.Text = $"{_settings.DimPercent}%  ·  α {_settings.OverlayAlpha}";
    }

    private void OnPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.TrackForegroundChanges = ForegroundToggle.IsChecked == true;
        _settings.HonourDisplayRequests = RequestToggle.IsChecked == true;
        _settings.NeverBlankDuringFullscreen = FullscreenToggle.IsChecked == true;
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
