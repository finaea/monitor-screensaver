using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MonitorDim.Core;

public enum SystemEventKind
{
    DisplayTopologyChanged,
    ResumedFromSleep,
    SuspendingToSleep,
    SessionLocked,
    SessionUnlocked,
    WindowsDisplayOff,
    WindowsDisplayOn,
    WindowsDisplayDim,
    ConsoleDisplayOff,
    ConsoleDisplayOn,
    ConsoleDisplayDim,
    UserPresent,
    UserInactive,
}

/// <summary>
/// A hidden message-only-ish window that subscribes to everything needed to keep the
/// engine correct across sleep, restart, lock and monitor hotplug.
///
/// Also surfaces Windows' own verdicts for diagnostics:
///   GUID_SESSION_DISPLAY_STATUS — when Windows actually powered the display off/on.
///   GUID_SESSION_USER_PRESENCE  — PowerUserInactive means "the user activity timeout
///                                 has elapsed with no interaction from the user".
/// </summary>
public sealed class SystemEventSink : IDisposable
{
    private readonly HwndSource _source;
    private readonly List<IntPtr> _powerRegistrations = [];
    private bool _disposed;

    public event Action<SystemEventKind>? Event;

    /// <summary>Last value reported by Windows for its own display power state.</summary>
    public int? WindowsDisplayState { get; private set; }

    /// <summary>Last value reported by Windows for session user presence.</summary>
    public int? WindowsUserPresence { get; private set; }

    private readonly bool _includeConsoleDisplayState;

    /// <summary>Last value reported for the console (kernel-level) display state.</summary>
    public int? ConsoleDisplayState { get; private set; }

    /// <param name="includeConsoleDisplayState">
    /// Also subscribe to GUID_CONSOLE_DISPLAY_STATE. The main app does not need it —
    /// GUID_SESSION_DISPLAY_STATUS is the one documented for interactive apps — but the
    /// --watch diagnostic registers both so the two can be cross-checked.
    /// </param>
    public SystemEventSink(bool includeConsoleDisplayState = false)
    {
        _includeConsoleDisplayState = includeConsoleDisplayState;
        var parameters = new HwndSourceParameters("MonitorDim.SystemEvents")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        var hwnd = _source.Handle;

        RegisterPower(hwnd, Native.GUID_SESSION_DISPLAY_STATUS);
        RegisterPower(hwnd, Native.GUID_SESSION_USER_PRESENCE);

        if (_includeConsoleDisplayState)
            RegisterPower(hwnd, Native.GUID_CONSOLE_DISPLAY_STATE);

        Native.WTSRegisterSessionNotification(hwnd, Native.NOTIFY_FOR_THIS_SESSION);
    }

    private void RegisterPower(IntPtr hwnd, Guid guid)
    {
        var g = guid;
        var handle = Native.RegisterPowerSettingNotification(hwnd, ref g, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (handle != IntPtr.Zero) _powerRegistrations.Add(handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // This is a window procedure: an escaping exception is a hard process kill.
        var w = wParam;
        var l = lParam;
        CrashLog.GuardCallback($"WndProc msg=0x{msg:X4}", () => Dispatch(msg, w, l));
        return IntPtr.Zero;
    }

    private void Dispatch(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_DISPLAYCHANGE:
                Event?.Invoke(SystemEventKind.DisplayTopologyChanged);
                break;

            case Native.WM_POWERBROADCAST:
                HandlePowerBroadcast(wParam.ToInt32(), lParam);
                break;

            case Native.WM_WTSSESSION_CHANGE:
                switch (wParam.ToInt32())
                {
                    case Native.WTS_SESSION_LOCK:
                        Event?.Invoke(SystemEventKind.SessionLocked);
                        break;
                    case Native.WTS_SESSION_UNLOCK:
                        Event?.Invoke(SystemEventKind.SessionUnlocked);
                        break;
                }
                break;
        }
    }

    private void HandlePowerBroadcast(int eventCode, IntPtr lParam)
    {
        switch (eventCode)
        {
            case Native.PBT_APMSUSPEND:
                Event?.Invoke(SystemEventKind.SuspendingToSleep);
                return;

            case Native.PBT_APMRESUMESUSPEND:
            case Native.PBT_APMRESUMEAUTOMATIC:
                Event?.Invoke(SystemEventKind.ResumedFromSleep);
                return;

            case Native.PBT_POWERSETTINGCHANGE:
                if (lParam == IntPtr.Zero) return;

                var setting = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(lParam);

                if (setting.PowerSetting == Native.GUID_SESSION_DISPLAY_STATUS)
                {
                    WindowsDisplayState = setting.Data;
                    // 0 = PowerMonitorOff, 1 = PowerMonitorOn, 2 = PowerMonitorDim
                    Event?.Invoke(setting.Data switch
                    {
                        0 => SystemEventKind.WindowsDisplayOff,
                        2 => SystemEventKind.WindowsDisplayDim,
                        _ => SystemEventKind.WindowsDisplayOn,
                    });
                }
                else if (setting.PowerSetting == Native.GUID_CONSOLE_DISPLAY_STATE)
                {
                    ConsoleDisplayState = setting.Data;
                    Event?.Invoke(setting.Data switch
                    {
                        0 => SystemEventKind.ConsoleDisplayOff,
                        2 => SystemEventKind.ConsoleDisplayDim,
                        _ => SystemEventKind.ConsoleDisplayOn,
                    });
                }
                else if (setting.PowerSetting == Native.GUID_SESSION_USER_PRESENCE)
                {
                    WindowsUserPresence = setting.Data;
                    // 0 = PowerUserPresent, 2 = PowerUserInactive
                    Event?.Invoke(setting.Data == 0 ? SystemEventKind.UserPresent : SystemEventKind.UserInactive);
                }
                return;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            foreach (var handle in _powerRegistrations)
                Native.UnregisterPowerSettingNotification(handle);

            Native.WTSUnRegisterSessionNotification(_source.Handle);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // shutting down anyway
        }
    }
}
