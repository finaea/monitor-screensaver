namespace MonitorScreenSaver.Core;

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
