namespace MonitorDim.Core;

/// <summary>
/// Owns one <see cref="OverlayWindow"/> per managed display and keeps that set in sync
/// with the physical display topology (hotplug, resolution change, resume from sleep).
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

            var win = new OverlayWindow(target, _settings.OverlayAlpha) { Tag = target.Bounds };
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
    /// Pushes the current true-black / dim setting to live overlays. Crossing between
    /// opaque and translucent needs a fresh window, since AllowsTransparency can only be
    /// set before the handle exists.
    /// </summary>
    public void ApplyAppearance()
    {
        if (_disposed) return;

        var alpha = _settings.OverlayAlpha;
        var needsRebuild = _windows.Values.Any(w => !w.SetAlpha(alpha));

        if (!needsRebuild) return;

        foreach (var id in _windows.Keys.ToList())
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
