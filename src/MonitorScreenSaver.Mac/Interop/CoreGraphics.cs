using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

internal static class CG
{
    internal const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ColorSyncLib = "/System/Library/Frameworks/ColorSync.framework/ColorSync";

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect
    {
        public double X, Y, Width, Height;
    }

    // ---------------------------------------------------------------- input idle

    /// <summary>CGEventSourceStateID — HID system state covers all physical input.</summary>
    internal const int kCGEventSourceStateHIDSystemState = 1;

    /// <summary>CGEventType (~0): any input event. CGEventTypes.h:491.</summary>
    internal const uint kCGAnyInputEventType = 0xFFFFFFFF;

    [DllImport(Lib)]
    internal static extern double CGEventSourceSecondsSinceLastEventType(int stateID, uint eventType);

    // ---------------------------------------------------------------- displays

    [DllImport(Lib)]
    internal static extern int CGGetActiveDisplayList(uint maxDisplays, uint[] activeDisplays, out uint displayCount);

    [DllImport(Lib)]
    internal static extern CGRect CGDisplayBounds(uint display);

    [DllImport(Lib)]
    internal static extern uint CGMainDisplayID();

    /// <summary>ColorSyncDevice.h; follows the Create rule — CFRelease the result.</summary>
    [DllImport(ColorSyncLib)]
    internal static extern IntPtr CGDisplayCreateUUIDFromDisplayID(uint display);

    // Callback: void (*)(CGDirectDisplayID display, CGDisplayChangeSummaryFlags flags, void* userInfo)
    internal const uint kCGDisplayBeginConfigurationFlag = 1;

    [DllImport(Lib)]
    internal static extern int CGDisplayRegisterReconfigurationCallback(IntPtr callback, IntPtr userInfo);

    [DllImport(Lib)]
    internal static extern int CGDisplayRemoveReconfigurationCallback(IntPtr callback, IntPtr userInfo);

    // ---------------------------------------------------------------- window list

    internal const uint kCGWindowListOptionOnScreenOnly = 1 << 0;
    internal const uint kCGWindowListExcludeDesktopElements = 1 << 4;

    /// <summary>Returns a CFArray of CFDictionary; follows the Copy rule — CFRelease it.</summary>
    [DllImport(Lib)]
    internal static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);

    // Exported CFStringRef constants for the window-info dictionary keys.
    internal static readonly IntPtr WindowLayerKey = CF.GetConstant(Lib, "kCGWindowLayer");
    internal static readonly IntPtr WindowBoundsKey = CF.GetConstant(Lib, "kCGWindowBounds");
    internal static readonly IntPtr WindowOwnerPidKey = CF.GetConstant(Lib, "kCGWindowOwnerPID");
}
