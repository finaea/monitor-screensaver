using System.Runtime.InteropServices;

namespace MonitorScreenSaver.Mac.Interop;

/// <summary>
/// The Carbon Event Manager's hot key API — the only way to hold a system-wide shortcut on
/// macOS without asking for Accessibility permission. Deprecated for two decades and still
/// what every menu bar app uses, because the alternatives are worse: an
/// NSEvent global monitor cannot consume the keystroke, and a CGEventTap needs the user to
/// grant Accessibility in System Settings.
///
/// Verified against the macOS 26 SDK headers (Events.h, CarbonEvents.h) and the exported
/// symbols in HIToolbox.tbd; the constants below were printed by a C program compiled
/// against those headers rather than copied from memory.
/// </summary>
internal static class Carbon
{
    private const string Lib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Events.h modifier masks. Note these are the *Carbon* masks, unrelated to the
    // NSEvent flags the same information is expressed in elsewhere (see MacSystemHotkeys).
    internal const uint CmdKey = 0x0100;
    internal const uint ShiftKey = 0x0200;
    internal const uint OptionKey = 0x0800;
    internal const uint ControlKey = 0x1000;

    /// <summary>kEventKeyModifierFnMask — the Fn key. Appears in the system hot key table.</summary>
    internal const uint FnMask = 0x20000;

    internal const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    internal const uint EventHotKeyPressed = 5;

    /// <summary>This process already registered that combination. The only clash macOS reports.</summary>
    internal const int EventHotKeyExistsErr = -9878;

    internal const int NoErr = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [DllImport(Lib)]
    internal static extern IntPtr GetApplicationEventTarget();

    [DllImport(Lib)]
    internal static extern int RegisterEventHotKey(
        uint inHotKeyCode, uint inHotKeyModifiers, EventHotKeyID inHotKeyID,
        IntPtr inTarget, uint inOptions, out IntPtr outRef);

    [DllImport(Lib)]
    internal static extern int UnregisterEventHotKey(IntPtr inHotKey);

    /// <summary>
    /// EventHandlerUPP is a plain function pointer on macOS (NewEventHandlerUPP has been a
    /// no-op since Carbon went 64-bit), so an [UnmanagedCallersOnly] method is passed straight in.
    /// </summary>
    [DllImport(Lib)]
    internal static extern int InstallEventHandler(
        IntPtr inTarget, IntPtr inHandler, int inNumTypes, ref EventTypeSpec inList,
        IntPtr inUserData, out IntPtr outRef);

    [DllImport(Lib)]
    internal static extern int RemoveEventHandler(IntPtr inHandlerRef);

    // ---------------------------------------------------------------- system hot keys

    /// <summary>
    /// Every system-wide symbolic hot key — Spotlight, Mission Control, screen capture,
    /// keyboard navigation, spaces — whether or not the user has ever changed it. Declared in
    /// CarbonEvents.h: "Returns an array of CFDictionaryRefs containing information about the
    /// system-wide symbolic hotkeys that are defined in the Keyboard preferences pane."
    ///
    /// This is what both reference implementations use (MASShortcut's MASShortcutValidator and
    /// sindresorhus/KeyboardShortcuts' HotKeyCenter.systemShortcuts), and it is the only way to
    /// see the shortcuts a user has *not* customised: the com.apple.symbolichotkeys preference
    /// domain stores overrides only. Measured on macOS 26.6: 230 entries, 170 of them enabled,
    /// against 6 enabled and 2 readable in that domain.
    ///
    /// The caller releases the array (the dictionaries inside it are autoreleased with it). Per
    /// the header it is O(number of hot keys) and not thread safe, so it is called on the main
    /// thread when a shortcut is being validated, never on the engine path.
    /// </summary>
    [DllImport(Lib)]
    internal static extern int CopySymbolicHotKeys(out IntPtr outHotKeyArray);

    internal const string SymbolicHotKeyCode = "kHISymbolicHotKeyCode";
    internal const string SymbolicHotKeyModifiers = "kHISymbolicHotKeyModifiers";
    internal const string SymbolicHotKeyEnabled = "kHISymbolicHotKeyEnabled";

    // ---------------------------------------------------------------- key labels

    // Turning a key code back into the character it types, so the shortcut is shown the way the
    // user's own keyboard layout produces it. Key codes are positional: 0x0B is "the key where
    // B is on an ANSI keyboard", which is not B on Dvorak.

    internal const ushort KeyActionDisplay = 3;               // kUCKeyActionDisplay
    internal const uint TranslateNoDeadKeys = 0x1;            // kUCKeyTranslateNoDeadKeysMask

    [DllImport(Lib)]
    internal static extern IntPtr TISCopyCurrentKeyboardInputSource();

    /// <summary>
    /// The fallback for input sources with no layout data of their own — every IME (Pinyin,
    /// Kotoeri). Without it, key labels silently disappear for those users.
    /// </summary>
    [DllImport(Lib)]
    internal static extern IntPtr TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

    [DllImport(Lib)]
    internal static extern IntPtr TISGetInputSourceProperty(IntPtr inputSource, IntPtr propertyKey);

    internal static readonly IntPtr PropertyUnicodeKeyLayoutData =
        CF.GetConstant(Lib, "kTISPropertyUnicodeKeyLayoutData");

    [DllImport(Lib)]
    internal static extern uint LMGetKbdType();

    [DllImport(Lib)]
    internal static extern int UCKeyTranslate(
        IntPtr keyLayoutPtr, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState,
        uint keyboardType, uint keyTranslateOptions, ref uint deadKeyState,
        nint maxStringLength, out nint actualStringLength, [Out] char[] unicodeString);
}
