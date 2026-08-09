using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MonitorDim.Core;

/// <summary>
/// A borderless, non-activating, always-on-top pure-black window sized to one monitor's
/// physical bounds. On OLED a full-black frame drives the pixels to zero emission, which
/// is the point: no DPMS, no DisplayPort link drop, no window rearrangement on wake.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly PixelRect _bounds;
    private Point? _firstCursor;
    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>Ignore stray mouse traffic caused by the window appearing under the cursor.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);
    private const double MoveThresholdPx = 4.0;

    public event Action? WakeRequested;

    private byte _alpha;

    /// <summary>
    /// True black uses an ordinary opaque window: hardware rendered, no compositing cost.
    /// Dim needs to show the desktop through, which in WPF means AllowsTransparency — a
    /// create-time property, and one that switches the window to a software-rendered
    /// per-pixel-alpha surface (~29 MB on a 5120x1440 panel). So we only pay for it when
    /// dim is actually selected, and the window has to be recreated to cross the boundary.
    /// </summary>
    public bool IsTranslucent { get; }

    public OverlayWindow(DisplayTarget target, byte alpha)
    {
        _bounds = target.Bounds;
        _alpha = alpha;
        IsTranslucent = alpha < 255;

        Title = "MonitorDim Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = IsTranslucent;
        Background = MakeBrush(alpha);
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

        SourceInitialized += OnSourceInitialized;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseDown += (_, _) => Wake();
        PreviewMouseWheel += (_, _) => Wake();
        PreviewKeyDown += (_, _) => Wake();
    }

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
    /// 255 = true black; anything less lets the desktop show through. Returns false when
    /// the change crosses the opaque/translucent boundary and the caller must recreate.
    /// </summary>
    public bool SetAlpha(byte alpha)
    {
        if (alpha < 255 != IsTranslucent) return false;

        if (_alpha != alpha)
        {
            _alpha = alpha;
            Background = MakeBrush(alpha);
        }

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
    }

    public void HideOverlay()
    {
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
