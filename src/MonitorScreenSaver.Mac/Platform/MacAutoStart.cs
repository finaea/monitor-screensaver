using System.Runtime.InteropServices;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// Start-at-login via SMAppService (ServiceManagement, macOS 13+) — the macOS twin of
/// the Windows Run key, visible to the user under System Settings → Login Items.
/// Only works from inside a proper .app bundle (tools/bundle-macos.sh); from a bare
/// binary registration fails with an error we surface instead of hiding.
///
/// No elevated variant exists because nothing on macOS needs elevation (holder names
/// are readable by everyone), so the Windows scheduled-task path has no counterpart.
/// </summary>
public static class MacAutoStart
{
    // SMAppServiceStatus
    private const nint StatusEnabled = 1;

    static MacAutoStart()
    {
        NativeLibrary.Load("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement");
    }

    private static IntPtr Service() =>
        ObjC.Send(ObjC.Class("SMAppService"), ObjC.Sel("mainAppService"));

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return ObjC.SendNInt(Service(), ObjC.Sel("status")) == StatusEnabled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Applies the requested state. Returns null on success, or a message to show the user.</summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            var error = IntPtr.Zero;
            var ok = ObjC.SendBoolRef(Service(),
                ObjC.Sel(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), ref error);

            if (ok) return null;

            var description = error != IntPtr.Zero
                ? ObjC.NSStringToManaged(ObjC.Send(error, ObjC.Sel("localizedDescription")))
                : null;

            return description ?? "SMAppService failed without an error description (running outside an .app bundle?).";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
