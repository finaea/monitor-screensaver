using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Core;

/// <summary>A rectangle in physical pixels. Public mirror of the internal RECT.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    internal static PixelRect From(Native.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

/// <summary>One physical display, in physical pixels.</summary>
public sealed record DisplayTarget
{
    /// <summary>GDI name, e.g. <c>\\.\DISPLAY1</c>. Not stable across replug.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Human name from DisplayConfig, e.g. "Predator X49V". Falls back to the generic PnP string.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Hardware id (e.g. <c>MONITOR\ACR0123\...</c>). Used as the settings key; survives replug.</summary>
    public required string StableId { get; init; }

    public required PixelRect Bounds { get; init; }
    public required bool IsPrimary { get; init; }

    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public string Geometry => $"{Width} × {Height}  at  ({Bounds.Left}, {Bounds.Top})";
}

public static class DisplayEnumerator
{
    public static IReadOnlyList<DisplayTarget> Enumerate()
    {
        var friendly = TryGetFriendlyNames();
        var results = new List<DisplayTarget>();

        // Invoked by user32; an exception escaping here kills the process.
        Native.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            try
            {
                Collect(hMonitor);
            }
            catch (Exception ex)
            {
                CrashLog.Write("callback: EnumDisplayMonitors", ex);
            }

            return true;
        };

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return results
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Bounds.Left)
            .ToList();

        void Collect(IntPtr hMonitor)
        {
            var info = new Native.MONITORINFOEXW
            {
                cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEXW>(),
                szDevice = string.Empty,
            };

            if (!Native.GetMonitorInfoW(hMonitor, ref info))
                return;

            var device = info.szDevice ?? string.Empty;
            var (pnpName, hardwareId) = TryGetMonitorChild(device);

            var name = friendly.TryGetValue(device, out var f) && !string.IsNullOrWhiteSpace(f)
                ? f
                : (!string.IsNullOrWhiteSpace(pnpName) ? pnpName : device);

            results.Add(new DisplayTarget
            {
                DeviceName = device,
                FriendlyName = name,
                StableId = !string.IsNullOrWhiteSpace(hardwareId) ? hardwareId : device,
                Bounds = PixelRect.From(info.rcMonitor),
                IsPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
            });
        }
    }

    /// <summary>The monitor child of an adapter carries the PnP string and hardware id.</summary>
    private static (string Name, string HardwareId) TryGetMonitorChild(string adapterDeviceName)
    {
        try
        {
            var dd = new Native.DISPLAY_DEVICEW
            {
                cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICEW>(),
                DeviceName = string.Empty,
                DeviceString = string.Empty,
                DeviceID = string.Empty,
                DeviceKey = string.Empty,
            };

            if (Native.EnumDisplayDevicesW(adapterDeviceName, 0, ref dd, 0))
                return (dd.DeviceString ?? string.Empty, dd.DeviceID ?? string.Empty);
        }
        catch
        {
            // best effort only
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Maps <c>\\.\DISPLAYn</c> to the EDID monitor name via DisplayConfig. This is the only
    /// API that yields the marketing name rather than "Generic PnP Monitor".
    /// </summary>
    private static Dictionary<string, string> TryGetFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return map;

            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];

            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            for (var i = 0; i < pathCount; i++)
            {
                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                    viewGdiDeviceName = string.Empty,
                };

                var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                    monitorFriendlyDeviceName = string.Empty,
                    monitorDevicePath = string.Empty,
                };

                if (Native.DisplayConfigGetDeviceInfo(ref source) != 0) continue;
                if (Native.DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                var gdi = source.viewGdiDeviceName;
                var name = target.monitorFriendlyDeviceName;

                if (!string.IsNullOrWhiteSpace(gdi) && !string.IsNullOrWhiteSpace(name))
                    map[gdi] = name;
            }
        }
        catch
        {
            // DisplayConfig is best effort; callers fall back to the PnP string.
        }

        return map;
    }
}
