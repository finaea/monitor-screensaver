using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

/// <summary>
/// Minimal objc_msgSend surface for the AppKit/AVFoundation calls the platform layer
/// makes (NSWorkspace, NSScreen, NSPanel overlays, AVPlayer). CFString and NSString
/// are toll-free bridged, so CF.CreateString doubles as an NSString factory.
///
/// All variants bind the same objc_msgSend export with different managed signatures.
/// On arm64 this is always correct (one calling convention); on x64 it holds for
/// every signature used here (pointer/integer/small-struct args, pointer/BOOL/void
/// returns) — only large struct *returns* would need objc_msgSend_stret, which this
/// file deliberately avoids.
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.dylib";

    static ObjC()
    {
        // NSWorkspace/NSScreen/NSPanel live in AppKit; load it once so objc_getClass
        // finds them. AVFoundation is loaded lazily by AVF (Interop/AppKit.cs).
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    [DllImport(Lib)]
    internal static extern IntPtr objc_getClass(string name);

    [DllImport(Lib)]
    internal static extern IntPtr sel_registerName(string name);

    // ---------------------------------------------------------------- class creation
    //
    // Menu items need a target object whose action selector dispatches back into C#.
    // These three calls mint a tiny NSObject subclass at runtime whose methods are
    // UnmanagedCallersOnly function pointers.

    [DllImport(Lib)]
    internal static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr imp, string typeEncoding);

    [DllImport(Lib)]
    internal static extern void objc_registerClassPair(IntPtr cls);

    // ---------------------------------------------------------------- autorelease pools

    [DllImport(Lib)]
    internal static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Lib)]
    internal static extern void objc_autoreleasePoolPop(IntPtr pool);

    // ---------------------------------------------------------------- msgSend variants

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    /// <summary>initWithTitle:action:keyEquivalent:</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    /// <summary>statusItemWithLength: (CGFloat argument, object return).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr SendForDouble(IntPtr receiver, IntPtr selector, double arg);

    /// <summary>registerAndReturnError:-style calls taking an NSError**.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SendBoolRef(IntPtr receiver, IntPtr selector, ref IntPtr error);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern int SendInt(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern uint SendUInt(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern nint SendNInt(IntPtr receiver, IntPtr selector);

    /// <summary>indexOfItem:-style calls (object argument, NSInteger return).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern nint SendNInt(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SendBool(IntPtr receiver, IntPtr selector);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector);

    /// <summary>Also covers nint args (setLevel:, setCollectionBehavior:) — nint is IntPtr.</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>insertItem:atIndex:-style calls (object + NSInteger, void return).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, CG.CGRect rect);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern void SendVoid(IntPtr receiver, IntPtr selector, CG.CGRect rect, [MarshalAs(UnmanagedType.I1)] bool display);

    /// <summary>initWithContentRect:styleMask:backing:defer:</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr SendInitWindow(IntPtr receiver, IntPtr selector,
        CG.CGRect contentRect, nuint styleMask, nuint backing, [MarshalAs(UnmanagedType.I1)] bool defer);

    /// <summary>initWithSize: (NSSize argument, object return).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, CG.CGSize size);

    /// <summary>initWithImage:hotSpot: (object + NSPoint, object return).</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg, CG.CGPoint point);

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
