using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

/// <summary>
/// CoreFoundation P/Invoke surface plus small helpers for the CF types the platform
/// services read (dictionaries of numbers/strings/arrays from IOKit and CoreGraphics).
/// </summary>
internal static class CF
{
    internal const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const int kCFNumberSInt64Type = 4;

    // ---------------------------------------------------------------- memory

    [DllImport(Lib)]
    internal static extern void CFRelease(IntPtr cf);

    // ---------------------------------------------------------------- strings

    [DllImport(Lib)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

    [DllImport(Lib)]
    private static extern nint CFStringGetLength(IntPtr str);

    [DllImport(Lib)]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);

    /// <summary>Creates a CFString the caller must CFRelease.</summary>
    internal static IntPtr CreateString(string s) =>
        CFStringCreateWithCString(IntPtr.Zero, s, kCFStringEncodingUTF8);

    internal static string? FromString(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;

        var max = CFStringGetMaximumSizeForEncoding(CFStringGetLength(cfString), kCFStringEncodingUTF8) + 1;
        var buffer = new byte[max];

        if (!CFStringGetCString(cfString, buffer, max, kCFStringEncodingUTF8)) return null;

        var len = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
    }

    // ---------------------------------------------------------------- numbers

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

    internal static long NumberToLong(IntPtr cfNumber) =>
        cfNumber != IntPtr.Zero && CFNumberGetValue(cfNumber, kCFNumberSInt64Type, out var v) ? v : 0;

    // ---------------------------------------------------------------- dictionaries

    [DllImport(Lib)]
    internal static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(Lib)]
    internal static extern nint CFDictionaryGetCount(IntPtr dict);

    [DllImport(Lib)]
    internal static extern void CFDictionaryGetKeysAndValues(IntPtr dict, IntPtr[] keys, IntPtr[] values);

    /// <summary>Dictionary value for a string key; IntPtr.Zero when absent.</summary>
    internal static IntPtr DictGet(IntPtr dict, string key)
    {
        var cfKey = CreateString(key);
        try
        {
            return CFDictionaryGetValue(dict, cfKey);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    internal static long DictGetLong(IntPtr dict, string key) => NumberToLong(DictGet(dict, key));

    internal static string? DictGetString(IntPtr dict, string key) => FromString(DictGet(dict, key));

    // ---------------------------------------------------------------- arrays

    [DllImport(Lib)]
    internal static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(Lib)]
    internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    // ---------------------------------------------------------------- uuids

    [DllImport(Lib)]
    internal static extern IntPtr CFUUIDCreateString(IntPtr allocator, IntPtr uuid);

    // ---------------------------------------------------------------- run loop

    [DllImport(Lib)]
    internal static extern IntPtr CFRunLoopGetMain();

    [DllImport(Lib)]
    internal static extern void CFRunLoopRun();

    [DllImport(Lib)]
    internal static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(Lib)]
    internal static extern void CFRunLoopAddTimer(IntPtr runLoop, IntPtr timer, IntPtr mode);

    [DllImport(Lib)]
    internal static extern IntPtr CFRunLoopTimerCreate(
        IntPtr allocator, double fireDate, double interval, nuint flags, nint order,
        IntPtr callout, ref CFRunLoopTimerContext context);

    [DllImport(Lib)]
    internal static extern void CFRunLoopTimerInvalidate(IntPtr timer);

    [DllImport(Lib)]
    internal static extern double CFAbsoluteTimeGetCurrent();

    [StructLayout(LayoutKind.Sequential)]
    internal struct CFRunLoopTimerContext
    {
        public nint Version;
        public IntPtr Info;
        public IntPtr Retain;
        public IntPtr Release;
        public IntPtr CopyDescription;
    }

    /// <summary>Dereferences an exported CFStringRef constant (e.g. kCFRunLoopCommonModes).</summary>
    internal static IntPtr GetConstant(string libraryPath, string symbol)
    {
        var lib = NativeLibrary.Load(libraryPath);
        var export = NativeLibrary.GetExport(lib, symbol);
        return Marshal.ReadIntPtr(export);
    }

    internal static readonly IntPtr RunLoopCommonModes = GetConstant(Lib, "kCFRunLoopCommonModes");
    internal static readonly IntPtr RunLoopDefaultMode = GetConstant(Lib, "kCFRunLoopDefaultMode");

    // ---------------------------------------------------------------- distributed notifications

    internal const nint SuspensionBehaviorDeliverImmediately = 4;

    [DllImport(Lib)]
    internal static extern IntPtr CFNotificationCenterGetDistributedCenter();

    [DllImport(Lib)]
    internal static extern void CFNotificationCenterAddObserver(
        IntPtr center, IntPtr observer, IntPtr callback, IntPtr name, IntPtr obj, nint suspensionBehavior);

    [DllImport(Lib)]
    internal static extern void CFNotificationCenterRemoveEveryObserver(IntPtr center, IntPtr observer);
}
