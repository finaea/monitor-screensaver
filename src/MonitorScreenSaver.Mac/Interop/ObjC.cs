using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

/// <summary>
/// Minimal objc_msgSend surface for the few AppKit reads the platform services need
/// (NSWorkspace frontmost app, NSScreen names). CFString and NSString are toll-free
/// bridged, so CF.CreateString doubles as an NSString factory.
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.dylib";

    static ObjC()
    {
        // NSWorkspace/NSScreen live in AppKit; load it once so objc_getClass finds them.
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    [DllImport(Lib)]
    internal static extern IntPtr objc_getClass(string name);

    [DllImport(Lib)]
    internal static extern IntPtr sel_registerName(string name);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern int SendInt(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern uint SendUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern nint SendNInt(IntPtr receiver, IntPtr selector);

    internal static IntPtr Class(string name) => objc_getClass(name);
    internal static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>Reads an NSString via UTF8String.</summary>
    internal static string? NSStringToManaged(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }
}
