using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// Displays via CGGetActiveDisplayList. The stable id is the display UUID from
/// CGDisplayCreateUUIDFromDisplayID — the macOS analog of the Windows EDID hardware
/// path: it survives replug and renumbering. Bounds are global desktop points, the
/// same units NSWindow placement uses.
/// </summary>
public sealed class MacDisplayEnumerator : IDisplayEnumerator
{
    public IReadOnlyList<DisplayTarget> Enumerate()
    {
        var results = new List<DisplayTarget>();

        var ids = new uint[16];
        if (CG.CGGetActiveDisplayList(16, ids, out var count) != 0) return results;

        var main = CG.CGMainDisplayID();
        var names = TryGetScreenNames();

        for (uint i = 0; i < count; i++)
        {
            var id = ids[i];
            var bounds = CG.CGDisplayBounds(id);

            var rect = new PixelRect(
                (int)Math.Round(bounds.X),
                (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.X + bounds.Width),
                (int)Math.Round(bounds.Y + bounds.Height));

            results.Add(new DisplayTarget
            {
                DeviceName = $"CGDisplay {id}",
                FriendlyName = names.TryGetValue(id, out var name) ? name : $"Display {i + 1}",
                StableId = StableIdFor(id),
                Bounds = rect,
                IsPrimary = id == main,
            });
        }

        return results
            .OrderByDescending(d => d.IsPrimary)
            .ThenBy(d => d.Bounds.Left)
            .ToList();
    }

    private static string StableIdFor(uint displayId)
    {
        try
        {
            var uuid = CG.CGDisplayCreateUUIDFromDisplayID(displayId);
            if (uuid == IntPtr.Zero) return $"CGDisplay {displayId}";

            try
            {
                var str = CF.CFUUIDCreateString(IntPtr.Zero, uuid);
                try
                {
                    return CF.FromString(str) ?? $"CGDisplay {displayId}";
                }
                finally
                {
                    if (str != IntPtr.Zero) CF.CFRelease(str);
                }
            }
            finally
            {
                CF.CFRelease(uuid);
            }
        }
        catch
        {
            // Fallback keys by the transient id — still functional, just not replug-stable.
            return $"CGDisplay {displayId}";
        }
    }

    /// <summary>
    /// Marketing names via NSScreen.localizedName (10.15+), matched to CGDisplayIDs
    /// through the NSScreenNumber device-description key. Best effort — a headless
    /// process without AppKit access just gets "Display N".
    /// </summary>
    private static Dictionary<uint, string> TryGetScreenNames()
    {
        var map = new Dictionary<uint, string>();

        try
        {
            var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("screens"));
            if (screens == IntPtr.Zero) return map;

            var count = ObjC.SendNInt(screens, ObjC.Sel("count"));

            for (nint i = 0; i < count; i++)
            {
                var screen = ObjC.Send(screens, ObjC.Sel("objectAtIndex:"), i);
                if (screen == IntPtr.Zero) continue;

                var description = ObjC.Send(screen, ObjC.Sel("deviceDescription"));

                // CFString and NSString are toll-free bridged, so a CFString works as the key.
                var key = CF.CreateString("NSScreenNumber");
                try
                {
                    var number = ObjC.Send(description, ObjC.Sel("objectForKey:"), key);
                    if (number == IntPtr.Zero) continue;

                    var displayId = ObjC.SendUInt(number, ObjC.Sel("unsignedIntValue"));
                    var name = ObjC.NSStringToManaged(ObjC.Send(screen, ObjC.Sel("localizedName")));

                    if (!string.IsNullOrWhiteSpace(name)) map[displayId] = name;
                }
                finally
                {
                    CF.CFRelease(key);
                }
            }
        }
        catch
        {
            // cosmetic only
        }

        return map;
    }
}
