using System.Runtime.InteropServices;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The macOS twin of the Windows SystemEventSink, built entirely on C callbacks (no
/// Objective-C observer classes):
///
///   sleep / wake       — IORegisterForSystemPower (IOKit)
///   display topology   — CGDisplayRegisterReconfigurationCallback
///   lock / unlock      — distributed notifications com.apple.screenIsLocked /
///                        com.apple.screenIsUnlocked. Undocumented but stable for a
///                        decade-plus; the mac self-test should assert they still fire.
///
/// Requires a pumping main CFRunLoop (the harness runs CFRunLoopRun; a real app's UI
/// loop pumps it anyway).
/// </summary>
public sealed unsafe class MacSystemEvents : ISystemEvents
{
    private GCHandle _self;
    private uint _rootPort;
    private uint _powerNotifier;
    private IntPtr _lockedName;
    private IntPtr _unlockedName;
    private bool _disposed;

    public event Action<SystemEventKind>? Event;

    public MacSystemEvents()
    {
        _self = GCHandle.Alloc(this);
        var refcon = GCHandle.ToIntPtr(_self);

        // Sleep / wake. WillSleep must be acknowledged or the OS stalls for ~30 s.
        _rootPort = IOKit.IORegisterForSystemPower(
            refcon, out var notifyPort,
            (IntPtr)(delegate* unmanaged<IntPtr, uint, uint, IntPtr, void>)&OnPowerMessage,
            out _powerNotifier);

        if (notifyPort != IntPtr.Zero)
            CF.CFRunLoopAddSource(
                CF.CFRunLoopGetMain(), IOKit.IONotificationPortGetRunLoopSource(notifyPort), CF.RunLoopCommonModes);

        // Display topology.
        CG.CGDisplayRegisterReconfigurationCallback(
            (IntPtr)(delegate* unmanaged<uint, uint, IntPtr, void>)&OnDisplayReconfigured, refcon);

        // Session lock / unlock.
        var center = CF.CFNotificationCenterGetDistributedCenter();
        _lockedName = CF.CreateString("com.apple.screenIsLocked");
        _unlockedName = CF.CreateString("com.apple.screenIsUnlocked");

        var callback = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnDistributedNotification;
        CF.CFNotificationCenterAddObserver(center, refcon, callback, _lockedName, IntPtr.Zero, CF.SuspensionBehaviorDeliverImmediately);
        CF.CFNotificationCenterAddObserver(center, refcon, callback, _unlockedName, IntPtr.Zero, CF.SuspensionBehaviorDeliverImmediately);
    }

    private void Raise(SystemEventKind kind) => Event?.Invoke(kind);

    private static MacSystemEvents? FromRefcon(IntPtr refcon) =>
        GCHandle.FromIntPtr(refcon).Target as MacSystemEvents;

    [UnmanagedCallersOnly]
    private static void OnPowerMessage(IntPtr refcon, uint service, uint messageType, IntPtr argument)
    {
        CrashLog.GuardCallback("IORegisterForSystemPower", () =>
        {
            var self = FromRefcon(refcon);
            if (self is null) return;

            switch (messageType)
            {
                case IOKit.kIOMessageCanSystemSleep:
                    IOKit.IOAllowPowerChange(self._rootPort, argument);
                    break;

                case IOKit.kIOMessageSystemWillSleep:
                    self.Raise(SystemEventKind.SuspendingToSleep);
                    IOKit.IOAllowPowerChange(self._rootPort, argument);
                    break;

                case IOKit.kIOMessageSystemHasPoweredOn:
                    self.Raise(SystemEventKind.ResumedFromSleep);
                    break;
            }
        });
    }

    [UnmanagedCallersOnly]
    private static void OnDisplayReconfigured(uint display, uint flags, IntPtr refcon)
    {
        CrashLog.GuardCallback("CGDisplayReconfiguration", () =>
        {
            // The callback fires once with BeginConfiguration and again with the result;
            // only the completed change is worth reacting to.
            if ((flags & CG.kCGDisplayBeginConfigurationFlag) != 0) return;

            FromRefcon(refcon)?.Raise(SystemEventKind.DisplayTopologyChanged);
        });
    }

    [UnmanagedCallersOnly]
    private static void OnDistributedNotification(IntPtr center, IntPtr observer, IntPtr name, IntPtr obj, IntPtr userInfo)
    {
        CrashLog.GuardCallback("DistributedNotification", () =>
        {
            var self = FromRefcon(observer);
            if (self is null) return;

            var notification = CF.FromString(name);

            if (notification == "com.apple.screenIsLocked") self.Raise(SystemEventKind.SessionLocked);
            else if (notification == "com.apple.screenIsUnlocked") self.Raise(SystemEventKind.SessionUnlocked);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // The GCHandle is about to be freed, so every native registration that
            // carries it as refcon must go first.
            CG.CGDisplayRemoveReconfigurationCallback(
                (IntPtr)(delegate* unmanaged<uint, uint, IntPtr, void>)&OnDisplayReconfigured,
                GCHandle.ToIntPtr(_self));

            CF.CFNotificationCenterRemoveEveryObserver(
                CF.CFNotificationCenterGetDistributedCenter(), GCHandle.ToIntPtr(_self));

            if (_lockedName != IntPtr.Zero) CF.CFRelease(_lockedName);
            if (_unlockedName != IntPtr.Zero) CF.CFRelease(_unlockedName);
        }
        catch
        {
            // shutting down anyway
        }

        if (_self.IsAllocated) _self.Free();
    }
}
