namespace MonitorScreenSaver.Core;

/// <summary>
/// A rectangle in the platform's window-placement units: physical pixels on Windows,
/// global desktop points on macOS. Consistent between the display enumerator and the
/// overlay windows, which is all that matters.
/// </summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>One physical display.</summary>
public sealed record DisplayTarget
{
    /// <summary>OS device name, e.g. <c>\\.\DISPLAY1</c> or <c>CGDisplay 5</c>. Not stable across replug.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Human name, e.g. "Predator X49V". Falls back to a generic string.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Stable hardware id (EDID path on Windows, display UUID on macOS). Used as the settings key; survives replug.</summary>
    public required string StableId { get; init; }

    public required PixelRect Bounds { get; init; }
    public required bool IsPrimary { get; init; }

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public string Geometry => $"{Width} × {Height}  at  ({Bounds.Left}, {Bounds.Top})";
}
