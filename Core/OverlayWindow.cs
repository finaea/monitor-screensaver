using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MonitorScreenSaver.Core;

/// <summary>
/// A borderless, non-activating, always-on-top window sized to one monitor's physical
/// bounds. Three appearances, resolved from a <see cref="MonitorConfig"/>:
///
///   TrueBlack — opaque black. On OLED a full-black frame drives the pixels to zero
///               emission, which is the point: no DPMS, no DisplayPort link drop, no
///               window rearrangement on wake.
///   Dim       — translucent black (AllowsTransparency, software rendered).
///   Video     — an opaque window playing a muted, looping MediaElement. Weaker burn-in
///               protection than black: motion spreads the wear, but lit pixels still age.
///
/// Mode changes always recreate the window: Dim needs AllowsTransparency (create-time
/// only), and tearing down a MediaElement with the window is both simpler and leak-proof
/// compared to morphing in place.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly PixelRect _bounds;
    private MonitorConfig _cfg;
    private MediaElement? _media;

    private Point? _firstCursor;
    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>Ignore stray mouse traffic caused by the window appearing under the cursor.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);
    private const double MoveThresholdPx = 4.0;

    public event Action? WakeRequested;

    /// <summary>
    /// True black and video use an ordinary opaque window: hardware rendered, no
    /// compositing cost. Dim needs to show the desktop through, which in WPF means
    /// AllowsTransparency — a create-time property, and one that switches the window to
    /// a software-rendered per-pixel-alpha surface (~29 MB on a 5120x1440 panel). So we
    /// only pay for it when dim is actually selected, and the window has to be recreated
    /// to cross the boundary.
    /// </summary>
    public bool IsTranslucent { get; }

    public bool IsVideo => _cfg.Mode == OverlayMode.Video;

    /// <summary>True when this overlay is visible with a live video element.</summary>
    public bool VideoPlaying => IsVideo && _media is not null && IsVisible;

    public OverlayWindow(DisplayTarget target, MonitorConfig cfg)
    {
        _bounds = target.Bounds;
        // Snapshot: the caller hands us the live settings object, and TryApply diffs
        // against what this window was actually built with.
        _cfg = cfg with { };
        IsTranslucent = cfg.Translucent;

        Title = "MonitorScreenSaver Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = IsTranslucent;
        Background = IsTranslucent ? MakeBrush(cfg.Alpha) : Brushes.Black;
        Foreground = Brushes.Black;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Cursor = Cursors.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Provisional placement; the authoritative one happens in physical pixels below.
        Left = _bounds.Left;
        Top = _bounds.Top;
        Width = Math.Max(_bounds.Width, 1);
        Height = Math.Max(_bounds.Height, 1);

        if (IsVideo) BuildMedia();

        SourceInitialized += OnSourceInitialized;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseDown += (_, _) => Wake();
        PreviewMouseWheel += (_, _) => Wake();
        PreviewKeyDown += (_, _) => Wake();
    }

    // ------------------------------------------------------------------ video

    private void BuildMedia()
    {
        var path = _cfg.VideoPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            // Degrade to true black rather than failing: on OLED that is the best
            // possible fallback anyway. Recorded so it doesn't look like "video ignored".
            CrashLog.Write("OverlayWindow.Video",
                new FileNotFoundException("Screensaver video not found; showing black.", path));
            return;
        }

        _media = new MediaElement
        {
            Source = new Uri(path, UriKind.Absolute),
            // Manual: we own the clock — play on show, stop on hide, rewind on end.
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            // Muted always. A screensaver must not make noise, and the audio-activity
            // option reads post-mute peaks, so a muted stream can never hold the
            // screens awake by itself.
            IsMuted = true,
            Volume = 0,
            Stretch = MapStretch(_cfg.VideoStretch),
            StretchDirection = StretchDirection.Both,
            IsHitTestVisible = false,
            Focusable = false,
        };

        _media.MediaEnded += (_, _) => CrashLog.GuardCallback("MediaElement.Loop", () =>
        {
            if (_media is null) return;
            _media.Position = TimeSpan.Zero;
            _media.Play();
        });

        _media.MediaFailed += (_, e) =>
        {
            // Bad codec or truncated file: log, drop the element, stay black.
            CrashLog.Write($"MediaElement.MediaFailed ({path})", e.ErrorException);
            ClearMedia();
        };

        Content = _media;
    }

    private void ClearMedia()
    {
        if (_media is null) return;

        try { _media.Stop(); } catch { /* already dead */ }
        try { _media.Source = null; } catch { /* already dead */ }

        Content = null;
        _media = null;
    }

    private static Stretch MapStretch(VideoStretch s) => s switch
    {
        VideoStretch.Fill => Stretch.UniformToFill,
        VideoStretch.Stretch => Stretch.Fill,
        _ => Stretch.Uniform,
    };

    // ------------------------------------------------------------------ window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // NOACTIVATE so blanking never steals focus from whatever you left open.
        // TOOLWINDOW keeps it out of Alt+Tab.
        ApplyExStyles();
        ApplyBounds();
    }

    private static SolidColorBrush MakeBrush(byte alpha)
    {
        // Qualified: DarkMenu.cs needs System.Drawing.Color, so Color is not globally aliased.
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// WPF's HwndTarget rewrites the extended style while the window is being realised,
    /// so this has to be (re)applied after Show() as well as at SourceInitialized.
    /// </summary>
    private void ApplyExStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        var wanted = ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;

        if (wanted != ex)
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(wanted));
    }

    /// <summary>
    /// Applies a config change in place when possible. Returns false when the window
    /// must be recreated: any mode change, a dim change crossing the opaque/translucent
    /// boundary, or a different video file (a fresh MediaElement beats reusing one).
    /// </summary>
    public bool TryApply(MonitorConfig cfg)
    {
        if (cfg.Mode != _cfg.Mode) return false;
        if (cfg.Translucent != IsTranslucent) return false;

        switch (cfg.Mode)
        {
            case OverlayMode.Dim when cfg.Alpha != _cfg.Alpha:
                Background = MakeBrush(cfg.Alpha);
                break;

            case OverlayMode.Video:
                if (!string.Equals(cfg.VideoPath, _cfg.VideoPath, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (cfg.VideoStretch != _cfg.VideoStretch && _media is not null)
                    _media.Stretch = MapStretch(cfg.VideoStretch);
                break;
        }

        _cfg = cfg with { };
        return true;
    }

    /// <summary>
    /// Positions in physical pixels. WPF's Left/Top/Width/Height are DIPs relative to the
    /// primary monitor's scale, which is wrong the moment two monitors have different DPI.
    /// </summary>
    public void ApplyBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
            _bounds.Left, _bounds.Top,
            Math.Max(_bounds.Width, 1), Math.Max(_bounds.Height, 1),
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }

    public void ShowOverlay()
    {
        _firstCursor = null;
        _shownAt = DateTime.UtcNow;

        if (!IsVisible) Show();

        ApplyExStyles();
        ApplyBounds();

        if (_media is not null)
        {
            try
            {
                _media.Position = TimeSpan.Zero;
                _media.Play();
            }
            catch (Exception ex)
            {
                CrashLog.Write("OverlayWindow.Play", ex);
            }
        }
    }

    public void HideOverlay()
    {
        // Stop, not pause: a hidden window keeps its media clock running otherwise,
        // burning decode cycles while nothing is visible.
        if (_media is not null)
        {
            try { _media.Stop(); } catch { /* teardown race */ }
        }

        if (IsVisible) Hide();
        _firstCursor = null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (DateTime.UtcNow - _shownAt < SettleTime) return;

        var p = e.GetPosition(this);

        if (_firstCursor is null)
        {
            _firstCursor = p;
            return;
        }

        var dx = Math.Abs(p.X - _firstCursor.Value.X);
        var dy = Math.Abs(p.Y - _firstCursor.Value.Y);

        if (dx >= MoveThresholdPx || dy >= MoveThresholdPx) Wake();
    }

    private void Wake() => WakeRequested?.Invoke();
}
