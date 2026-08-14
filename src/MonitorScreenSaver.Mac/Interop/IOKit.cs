using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

internal static class IOKit
{
    internal const string Lib = "/System/Library/Frameworks/IOKit.framework/IOKit";

    // ---------------------------------------------------------------- power assertions
    //
    // Assertion type strings from IOPMLib.h (verified against the macOS 26 SDK):
    //   kIOPMAssertionTypePreventUserIdleDisplaySleep = "PreventUserIdleDisplaySleep"
    //   kIOPMAssertionTypeNoDisplaySleep (legacy)     = "NoDisplaySleepAssertion"
    //   kIOPMAssertionTypePreventUserIdleSystemSleep  = "PreventUserIdleSystemSleep"
    //   kIOPMAssertionTypePreventSystemSleep          = "PreventSystemSleep"
    // Dictionary keys: AssertType / AssertName / AssertLevel / Details.

    internal const string AssertPreventUserIdleDisplaySleep = "PreventUserIdleDisplaySleep";
    internal const string AssertNoDisplaySleep = "NoDisplaySleepAssertion";
    internal const string AssertPreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";
    internal const string AssertPreventSystemSleep = "PreventSystemSleep";
    internal const string AssertUserIsActive = "UserIsActive";

    internal const string KeyAssertType = "AssertType";
    internal const string KeyAssertName = "AssertName";
    internal const string KeyAssertLevel = "AssertLevel";
    internal const string KeyAssertDetails = "Details";

    /// <summary>Aggregate levels per assertion type. Caller must CFRelease the dict.</summary>
    [DllImport(Lib)]
    internal static extern int IOPMCopyAssertionsStatus(out IntPtr assertionsStatus);

    /// <summary>pid → CFArray of assertion dicts. No elevation needed (since 10.7). Caller must CFRelease.</summary>
    [DllImport(Lib)]
    internal static extern int IOPMCopyAssertionsByProcess(out IntPtr assertionsByPid);

    // ---------------------------------------------------------------- sleep / wake

    // IOMessage.h: iokit_common_msg(x) = 0xE0000000 | x  (verified against the SDK)
    internal const uint kIOMessageCanSystemSleep = 0xE0000000 | 0x270;
    internal const uint kIOMessageSystemWillSleep = 0xE0000000 | 0x280;
    internal const uint kIOMessageSystemWillNotSleep = 0xE0000000 | 0x290;
    internal const uint kIOMessageSystemHasPoweredOn = 0xE0000000 | 0x300;

    // Callback: void (*)(void* refcon, io_service_t service, uint32_t messageType, void* messageArgument)
    [DllImport(Lib)]
    internal static extern uint IORegisterForSystemPower(
        IntPtr refcon, out IntPtr notifyPort, IntPtr callback, out uint notifier);

    [DllImport(Lib)]
    internal static extern IntPtr IONotificationPortGetRunLoopSource(IntPtr notifyPort);

    /// <summary>Must be called for CanSystemSleep/WillSleep or the OS stalls the sleep for ~30 s.</summary>
    [DllImport(Lib)]
    internal static extern int IOAllowPowerChange(uint rootPort, IntPtr notificationID);

    // ---------------------------------------------------------------- process names

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int proc_name(int pid, byte[] buffer, uint bufferSize);

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int sysctl(int[] name, uint nameLen, byte[] oldp, ref nuint oldLen, IntPtr newp, nuint newLen);

    // sysctl(CTL_KERN, KERN_PROC, KERN_PROC_PID, pid) → struct kinfo_proc.
    // Layout probed on the macOS 26 SDK (fixed 64-bit ABI): size 648, p_comm at 243,
    // MAXCOMLEN 16. Unlike proc_name, this works for other users' processes — the
    // same way ps shows every process name without privileges.
    private const int KinfoProcSize = 648;
    private const int PCommOffset = 243;
    private const int MaxComLen = 16;

    internal static string ProcessName(int pid)
    {
        try
        {
            // Full name (up to 2*MAXCOMLEN) — works for the caller's own processes.
            var buffer = new byte[256];
            var len = proc_name(pid, buffer, (uint)buffer.Length);
            if (len > 0) return System.Text.Encoding.UTF8.GetString(buffer, 0, len);
        }
        catch
        {
            // fall through
        }

        try
        {
            // proc_name is denied for other users' processes (powerd, WindowServer…);
            // kinfo_proc.p_comm is not, at the cost of 16-char truncation.
            var kinfo = new byte[KinfoProcSize];
            var size = (nuint)kinfo.Length;
            int[] mib = [1 /* CTL_KERN */, 14 /* KERN_PROC */, 1 /* KERN_PROC_PID */, pid];

            if (sysctl(mib, (uint)mib.Length, kinfo, ref size, IntPtr.Zero, 0) == 0 && size > 0)
            {
                var end = Array.IndexOf(kinfo, (byte)0, PCommOffset, MaxComLen + 1);
                var nameLen = (end < 0 ? PCommOffset + MaxComLen : end) - PCommOffset;
                if (nameLen > 0) return System.Text.Encoding.UTF8.GetString(kinfo, PCommOffset, nameLen);
            }
        }
        catch
        {
            // fall through
        }

        return $"pid {pid}";
    }
}
