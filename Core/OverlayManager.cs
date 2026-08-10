namespace MonitorScreenSaver.Core;

/// <summary>
/// Owns one <see cref="OverlayWindow"/> per managed display and keeps that set in sync
/// with the physical display topology (hotplug, resolution change, resume from sleep)
/// and with each display's effective <see cref="MonitorConfig"/>.
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<DisplayTarget> _displays = [];
    private bool _shown;
    private bool _disposed;

    public OverlayManager(AppSettings settings) => _settings = settings;

    /// <summary>Raised when the user pokes an overlay (mouse/keys landing on a blanked screen).</summary>
    public event Action? WakeRequested;

    public IReadOnlyList<DisplayTarget> Displays => _displays;

    public bool AnyManaged => _displays.Any(d => _settings.ManagedDisplayIds.Contains(d.StableId));

    public IReadOnlyList<string> CoveredDisplayIds =>
        _shown ? _windows.Keys.ToList() : [];

    /// <summary>
    /// True while any visible overlay is playing video. The engine uses this to ignore
    /// display power requests that may have been filed by our own media pipeline.
    /// </summary>
    public bool AnyVideoVisible =>
        _shown && _windows.Values.Any(w => w.VideoPlaying);

    /// <summary>Re-reads the display topology and rebuilds overlay windows to match.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        _displays = DisplayEnumerator.Enumerate();

        var wanted = _displays
            .Where(d => _settings.ManagedDisplayIds.Contains(d.StableId))
            .ToDictionary(d => d.StableId, d => d, StringComparer.OrdinalIgnoreCase);

        // Drop overlays for displays that vanished or were unmanaged.
        foreach (var id in _windows.Keys.ToList())
        {
            if (wanted.ContainsKey(id)) continue;
            Destroy(id);
        }

        // Recreate anything whose geometry moved, and create anything new.
        foreach (var (id, target) in wanted)
        {
            if (_windows.TryGetValue(id, out var existing))
            {
                if (existing.Tag is PixelRect r && r == target.Bounds) continue;
                Destroy(id);
            }

            var win = new OverlayWindow(target, _settings.ConfigFor(id)) { Tag = target.Bounds };
            win.WakeRequested += () => WakeRequested?.Invoke();
            _windows[id] = win;

            if (_shown) win.ShowOverlay();
        }
    }

    public void ShowAll()
    {
        if (_disposed) return;
        _shown = true;

        ApplyAppearance();

        foreach (var win in _windows.Values)
            win.ShowOverlay();
    }

    /// <summary>
    /// Pushes each display's current config to its live overlay. Windows that cannot
    /// morph in place (mode change, opaque/translucent crossing, different video file)
    /// are rebuilt individually — the others are left untouched.
    /// </summary>
    public void ApplyAppearance()
    {
        if (_disposed) return;

        var stale = _windows
            .Where(kv => !kv.Value.TryApply(_settings.ConfigFor(kv.Key)))
            .Select(kv => kv.Key)
            .ToList();

        if (stale.Count == 0) return;

        foreach (var id in stale)
            Destroy(id);

        Refresh();
    }

    public void HideAll()
    {
        _shown = false;

        foreach (var win in _windows.Values)
            win.HideOverlay();
    }

    /// <summary>
    /// Cheap periodic correction. Topology changes sometimes arrive without a
    /// WM_DISPLAYCHANGE (notably after resume), and a topmost window can lose its
    /// z-order to another topmost app.
    /// </summary>
    public void Reassert()
    {
        if (!_shown || _disposed) return;

        foreach (var win in _windows.Values)
        {
            if (!win.IsVisible) win.ShowOverlay();
            else win.ApplyBounds();
        }
    }

    private void Destroy(string id)
    {
        if (!_windows.Remove(id, out var win)) return;

        try
        {
            win.HideOverlay();
            win.Close();
        }
        catch
        {
            // window may already be gone
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _windows.Keys.ToList())
            Destroy(id);
    }
}
