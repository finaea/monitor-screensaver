using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Core;

/// <summary>
/// "Is anything audible right now?" via WASAPI endpoint peak meters, the same signal
/// oled_aegis keys its media detection on. Enumerate every ACTIVE render endpoint (not
/// just the default — audio may be routed to speakers while headphones are default)
/// and take the max instantaneous peak.
///
/// A peak meter reflects the post-mix, post-mute signal, so a muted stream reads zero.
/// That is what makes this safe to combine with the Video overlay: the screensaver's
/// own MediaElement is always muted and therefore never counts as audio activity.
///
/// COM notes: the interfaces are declared vtable-truncated — only slots up to the last
/// method actually called, which for every interface here is the first one or two.
/// All calls happen on the engine's dispatcher thread (STA), which WASAPI accepts.
/// </summary>
public static class AudioActivity
{
    /// <summary>Instantaneous peak below this is treated as silence. Meters idle at exactly 0.</summary>
    private const float Threshold = 0.001f;

    /// <summary>Re-enumerate endpoints this often; devices appear/vanish rarely.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private static readonly List<IAudioMeterInformation> Meters = [];
    private static DateTime _lastEnumerated = DateTime.MinValue;

    public static bool IsPlaying()
    {
        try
        {
            return Peak() > Threshold;
        }
        catch
        {
            // Any COM hiccup (device invalidated mid-call, audio service restart):
            // drop the cache and report silence; next tick re-enumerates.
            Invalidate();
            return false;
        }
    }

    /// <summary>Max instantaneous peak across all active render endpoints (0..1).</summary>
    public static float Peak()
    {
        EnsureMeters();

        var max = 0f;

        foreach (var meter in Meters)
        {
            meter.GetPeakValue(out var peak);
            if (peak > max) max = peak;
        }

        return max;
    }

    /// <summary>For the self-test: endpoint count and current peak, exceptions surfaced.</summary>
    public static (int Endpoints, float Peak) Probe()
    {
        Invalidate();
        EnsureMeters();

        var max = 0f;
        foreach (var meter in Meters)
        {
            meter.GetPeakValue(out var peak);
            if (peak > max) max = peak;
        }

        return (Meters.Count, max);
    }

    private static void EnsureMeters()
    {
        if (Meters.Count > 0 && DateTime.UtcNow - _lastEnumerated < RefreshEvery) return;

        Invalidate();
        _lastEnumerated = DateTime.UtcNow;

        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

        try
        {
            const int eRender = 0;
            const int DEVICE_STATE_ACTIVE = 0x1;

            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0)
                return;

            try
            {
                collection.GetCount(out var count);

                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;

                    try
                    {
                        var iid = IID_IAudioMeterInformation;
                        const uint CLSCTX_ALL = 23;

                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var meterObj) == 0
                            && meterObj is IAudioMeterInformation meter)
                        {
                            Meters.Add(meter);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void Invalidate()
    {
        foreach (var meter in Meters)
        {
            try { Marshal.ReleaseComObject(meter); } catch { /* already gone */ }
        }

        Meters.Clear();
        _lastEnumerated = DateTime.MinValue;
    }

    // ---------------------------------------------------------------- interop

    private static Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        // later vtable slots unused
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
        // later vtable slots unused
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
        // later vtable slots unused
    }
}
