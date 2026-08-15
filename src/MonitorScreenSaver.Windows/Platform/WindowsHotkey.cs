using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MonitorScreenSaver.Core;

/// <summary>
/// The Windows <see cref="IGlobalHotkey"/>: <c>RegisterHotKey</c> against a hidden window,
/// with the conflict checking split the opposite way round from macOS.
///
/// On macOS almost nothing can be learned before registering, so the mac twin
/// (<c>MacHotkey</c>) carries four pre-flight layers and treats a successful registration as
/// meaningless. Windows is the reverse: the documentation is explicit that <c>RegisterHotKey</c>
/// "typically … fails if the keystrokes specified for the hot key have already been
/// registered for another hot key", and <c>GetLastError</c> then reports
/// <see cref="Native.ERROR_HOTKEY_ALREADY_REGISTERED"/> (1409). So the registration *is*
/// the authoritative check here, and <see cref="Blocker"/> only has to cover what
/// registration cannot report:
///
///   1. <see cref="HotkeySpec.Weakness"/> — portable, keeps us out of app-shortcut space.
///   2. The Windows key, which the OS reserves for itself outright.
///   3. <see cref="Reserved"/> — combinations near every application owns, by hand. These
///      register perfectly well; they are refused because a system-wide hot key *wins*,
///      so taking one silently breaks that shortcut everywhere else.
///
/// Anything past that is settled by <see cref="Apply"/>, which reports the clash Windows
/// admits to rather than guessing at it.
/// </summary>
internal sealed class WindowsHotkey : IGlobalHotkey
{
    /// <summary>
    /// Only one hot key is ever registered, so the id is a constant. Applications must stay
    /// in 0x0000-0xBFFF (0xC000+ belongs to shared DLLs, via GlobalAddAtom).
    /// </summary>
    private const int HotKeyId = 0xB1A4;

    private readonly Action _onPressed;
    private readonly HwndSource _source;

    /// <summary>What is registered right now, so a re-check of it is not read as a clash.</summary>
    private HotkeySpec? _registered;

    private bool _disposed;

    public HotkeyStatus Status { get; private set; } = new(HotkeyState.Off, "no shortcut set");

    internal WindowsHotkey(Action onPressed)
    {
        _onPressed = onPressed;

        // Same shape as SystemEventSink's sink window, and for a related reason: the hot key
        // has to belong to a window this thread created ("This function fails if you try to
        // associate a hot key with a window created by another thread"), and WM_HOTKEY is a
        // posted message, so it needs a window with a queue the WPF dispatcher pumps.
        var parameters = new HwndSourceParameters("MonitorScreenSaver.Hotkey")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    // ------------------------------------------------------------------ conflicts

    /// <summary>
    /// Combinations that pass the portable shape rules and register without complaint, but
    /// are spoken for by convention rather than by the OS. A global hot key takes the
    /// keystroke before the focused application ever sees it, so claiming one of these does
    /// not clash — it silently removes that shortcut from every app on the machine, which is
    /// worse than a clash because nothing reports it.
    ///
    /// Best-effort by nature: no API enumerates what shortcuts applications use internally,
    /// which is the whole reason this is hand-written. Being wrong about an entry costs
    /// someone one alternative combination; the mac head carries the same list for the same
    /// reason (MacHotkey.Reserved).
    /// </summary>
    private static readonly (string Spec, string What)[] Reserved =
    [
        ("Ctrl+Shift+B", "Build, in Visual Studio and VS Code"),
        ("Ctrl+Shift+C", "Inspect Element, in every browser and Electron app"),
        ("Ctrl+Shift+I", "developer tools, in every browser and Electron app"),
        ("Ctrl+Shift+J", "the JavaScript console, in every browser and Electron app"),
        ("Ctrl+Shift+K", "the console or delete-line, depending on the app"),
        ("Ctrl+Shift+N", "New incognito window, and New folder in File Explorer"),
        ("Ctrl+Shift+P", "the command palette, in VS Code and browser dev tools"),
        ("Ctrl+Shift+S", "Save As / Save All, in most editors"),
        ("Ctrl+Shift+T", "Reopen the last closed tab, in every browser"),
        ("Ctrl+Shift+V", "Paste as plain text"),
        ("Ctrl+Shift+W", "Close all tabs / close the window"),
        ("Ctrl+Shift+Z", "Redo"),
    ];

    public string? Blocker(HotkeySpec spec)
    {
        if (spec.Weakness() is { } weak) return weak;

        // MOD_WIN is accepted by RegisterHotKey, but the documentation is unambiguous that
        // "keyboard shortcuts that involve the WINDOWS key are reserved for use by the
        // operating system" — so a combination stored on macOS as ⌘-something is refused
        // here rather than registered into a fight with the shell.
        if (spec.Modifiers.HasFlag(HotkeyModifiers.Command))
            return "Windows reserves the Windows key for the operating system — pick a combination without it";

        if (WindowsKeyCodes.Code(spec.Key) is null)
            return $"{spec.Key} has no virtual-key code — letters, digits and F1-F20 only";

        foreach (var (text, what) in Reserved)
        {
            if (HotkeySpec.TryParse(text, out var reserved) && reserved == spec)
                return $"{spec} is {what}, and a global shortcut would take it from every app";
        }

        return null;
    }

    // ------------------------------------------------------------------ registration

    public HotkeyStatus Apply(HotkeySpec? spec)
    {
        Unregister();

        if (_disposed) return Status = new HotkeyStatus(HotkeyState.Off, "no shortcut set");

        if (spec is null)
            return Status = new HotkeyStatus(HotkeyState.Off, "no shortcut set");

        if (Blocker(spec) is { } blocker)
            return Status = new HotkeyStatus(HotkeyState.Blocked, blocker);

        try
        {
            var hwnd = _source.Handle;
            if (hwnd == IntPtr.Zero)
                return Status = new HotkeyStatus(HotkeyState.Failed, "the hot key window was not created");

            // MOD_NOREPEAT: "blank now" is an edge, not a level. Without it, holding the
            // shortcut posts a WM_HOTKEY every auto-repeat interval for as long as the key
            // is down, and the engine would re-arm its manual blank on each one.
            var modifiers = Native.MOD_NOREPEAT |
                (spec.Modifiers.HasFlag(HotkeyModifiers.Control) ? Native.MOD_CONTROL : 0) |
                (spec.Modifiers.HasFlag(HotkeyModifiers.Alt) ? Native.MOD_ALT : 0) |
                (spec.Modifiers.HasFlag(HotkeyModifiers.Shift) ? Native.MOD_SHIFT : 0);

            if (!Native.RegisterHotKey(hwnd, HotKeyId, modifiers, WindowsKeyCodes.Code(spec.Key)!.Value))
            {
                var error = Marshal.GetLastWin32Error();

                return Status = new HotkeyStatus(HotkeyState.Failed,
                    // 1409 covers any other holder, in this process or another one. In practice
                    // it is always another application, since only one hot key is registered here.
                    error == Native.ERROR_HOTKEY_ALREADY_REGISTERED
                        ? $"{spec} is already registered by something else"
                        : $"Windows refused the shortcut (RegisterHotKey error {error})");
            }

            _registered = spec;
            return Status = new HotkeyStatus(HotkeyState.Active, $"{spec} is listening");
        }
        catch (Exception ex)
        {
            CrashLog.Write("WindowsHotkey.Apply", ex);
            return Status = new HotkeyStatus(HotkeyState.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Unregister()
    {
        if (_registered is null) return;

        try
        {
            Native.UnregisterHotKey(_source.Handle, HotKeyId);
        }
        catch
        {
            // going away anyway
        }

        _registered = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // This is a window procedure: an escaping exception is a hard process kill.
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            CrashLog.GuardCallback("WindowsHotkey.pressed", _onPressed);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unregister();

        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // shutting down anyway
        }

        Status = new HotkeyStatus(HotkeyState.Off, "no shortcut set");
    }
}

/// <summary>
/// Canonical key name to Windows virtual-key code. The ranges are contiguous and documented
/// (Virtual Key Codes): 'A'-'Z' are 0x41-0x5A and '0'-'9' are 0x30-0x39, both matching ASCII,
/// and VK_F1..VK_F24 run from 0x70. Unlike the macOS codes these are not positional — Windows
/// maps them through the active layout — so the stored name and what the key prints agree
/// without a translation step.
/// </summary>
internal static class WindowsKeyCodes
{
    private const uint VkSpace = 0x20;
    private const uint VkF1 = 0x70;

    internal static uint? Code(string key)
    {
        if (key == "Space") return VkSpace;

        if (key.Length == 1)
        {
            var c = key[0];
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            return null;
        }

        if (key.Length > 1 && key[0] == 'F' &&
            int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 20)
            return VkF1 + (uint)(n - 1);

        return null;
    }
}
