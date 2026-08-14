using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

/// <summary>AppKit constants (verified against the macOS 26 SDK's NSWindow.h/NSGraphics.h).</summary>
internal static class AppKit
{
    // NSWindowStyleMask
    internal const nuint StyleMaskBorderless = 0;
    internal const nuint StyleMaskNonactivatingPanel = 1 << 7;   // NSPanel only

    // NSBackingStoreType
    internal const nuint BackingStoreBuffered = 2;

    // NSWindowLevel — NSScreenSaverWindowLevel = kCGScreenSaverWindowLevel = 1000
    internal const nint ScreenSaverWindowLevel = 1000;

    // NSWindowCollectionBehavior
    internal const nuint BehaviorCanJoinAllSpaces = 1 << 0;
    internal const nuint BehaviorStationary = 1 << 4;
    internal const nuint BehaviorFullScreenAuxiliary = 1 << 8;

    // NSApplicationActivationPolicy
    internal const nint ActivationPolicyAccessory = 1;

    private static bool _appInitialised;

    /// <summary>
    /// AppKit windows need NSApplication initialised first. Accessory policy = no Dock
    /// icon, no menu bar takeover — the tray-app posture. Idempotent.
    /// </summary>
    internal static void EnsureApplication()
    {
        if (_appInitialised) return;
        _appInitialised = true;

        var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
        ObjC.SendVoid(app, ObjC.Sel("setActivationPolicy:"), ActivationPolicyAccessory);
    }
}

/// <summary>AVFoundation class/constant access for the video overlay.</summary>
internal static class AVF
{
    private const string Lib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    static AVF()
    {
        NativeLibrary.Load(Lib);
    }

    // AVLayerVideoGravity exported CFString constants (AVAnimation.h, macos 10.7+).
    internal static readonly IntPtr GravityResizeAspect = CF.GetConstant(Lib, "AVLayerVideoGravityResizeAspect");
    internal static readonly IntPtr GravityResizeAspectFill = CF.GetConstant(Lib, "AVLayerVideoGravityResizeAspectFill");
    internal static readonly IntPtr GravityResize = CF.GetConstant(Lib, "AVLayerVideoGravityResize");
}
