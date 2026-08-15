using System.Runtime.InteropServices;
using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The macOS <see cref="IGlobalHotkey"/>: a Carbon hot key, plus as much conflict detection
/// as this platform actually allows.
///
/// The uncomfortable fact this class is built around, measured on macOS 26.6 rather than
/// assumed: <c>RegisterEventHotKey</c> reports almost nothing. It returns noErr for ⌘Space
/// (Spotlight), ⌘Tab, ⌘Q and ⌘⇧4, and it returns noErr when a *different process* already
/// holds the same combination. The only clash it reports is a duplicate inside this same
/// process (eventHotKeyExistsErr, -9878). So a successful registration is not evidence that
/// the shortcut is free, and the checks that matter all have to happen before it:
///
///   1. <see cref="HotkeySpec.Weakness"/> — portable, keeps us out of app-shortcut space.
///   2. <see cref="Reserved"/> — combinations macOS or near-every app owns, by hand.
///   3. <see cref="MacSystemHotkeys"/> — every shortcut macOS itself holds, read live.
///   4. Registration, which catches the one case the OS will admit to.
///
/// There is a fifth check that costs nothing and is the strongest of the lot: the recorder in
/// the settings window never sees a keystroke the system has already claimed, because the
/// system consumes it first. Press ⌘Space there and Spotlight opens instead of the field
/// filling in.
/// </summary>
internal sealed unsafe class MacHotkey : IGlobalHotkey
{
    /// <summary>'MSSk' — this app's hot key signature, and the id of its only hot key.</summary>
    private static readonly Carbon.EventHotKeyID HotKeyId = new() { Signature = 0x4D53536B, Id = 1 };

    private static MacHotkey? _instance;

    private readonly Action _onPressed;
    private IntPtr _hotKey;
    private IntPtr _handler;

    public HotkeyStatus Status { get; private set; } = new(HotkeyState.Off, "no shortcut set");

    internal MacHotkey(Action onPressed)
    {
        _onPressed = onPressed;
        _instance = this;
    }

    // ------------------------------------------------------------------ conflicts

    /// <summary>
    /// Combinations that survive the portable shape rules but are still taken. macOS-owned
    /// ones are absolute; the app-convention ones (Web Inspector, Hide Others) are near
    /// universal, and being wrong about one of those costs a user nothing but picking
    /// another combination.
    ///
    /// Best-effort by nature: there is no API that enumerates this, which is the whole
    /// reason the list is hand-written.
    /// </summary>
    private static readonly (string Spec, string What)[] Reserved =
    [
        ("Ctrl+Cmd+Q",        "Lock Screen"),
        ("Ctrl+Cmd+F",        "Enter/Exit Full Screen"),
        ("Ctrl+Cmd+Space",    "Emoji & Symbols"),
        ("Ctrl+Cmd+D",        "Look Up"),
        ("Ctrl+Cmd+Shift+3",  "screenshot to clipboard"),
        ("Ctrl+Cmd+Shift+4",  "screenshot selection to clipboard"),
        ("Alt+Cmd+Space",     "Finder search window"),
        ("Alt+Cmd+D",         "Show/Hide the Dock"),
        ("Alt+Cmd+H",         "Hide Others"),
        ("Alt+Cmd+M",         "Minimise All"),
        ("Alt+Cmd+W",         "Close All Windows"),
        ("Alt+Cmd+T",         "Show/Hide Toolbar"),
        ("Alt+Cmd+I",         "Web Inspector, in every browser and Electron app"),
        ("Alt+Cmd+J",         "the JavaScript console, in every browser and Electron app"),
        ("Alt+Cmd+C",         "Copy Style / Inspect Element"),
        ("Alt+Cmd+Shift+V",   "Paste and Match Style"),
        ("Alt+Cmd+Shift+I",   "Web Inspector"),
    ];

    public string? Blocker(HotkeySpec spec)
    {
        if (spec.Weakness() is { } weak) return weak;

        foreach (var (text, what) in Reserved)
        {
            if (HotkeySpec.TryParse(text, out var reserved) && reserved == spec)
                return $"macOS uses {spec.Display()} for {what}";
        }

        if (MacKeyCodes.Code(spec.Key) is null)
            return $"{spec.Key} has no macOS key code — letters, digits and F1-F20 only";

        if (MacSystemHotkeys.Find(spec) is { } system)
            return $"{spec.Display()} is taken by {system}";

        return null;
    }

    // ------------------------------------------------------------------ registration

    public HotkeyStatus Apply(HotkeySpec? spec)
    {
        Unregister();

        if (spec is null)
            return Status = new HotkeyStatus(HotkeyState.Off, "no shortcut set");

        if (Blocker(spec) is { } blocker)
            return Status = new HotkeyStatus(HotkeyState.Blocked, blocker);

        try
        {
            var code = MacKeyCodes.Code(spec.Key)!.Value;
            var mods = CarbonModifiers(spec.Modifiers);

            if (!EnsureHandler(out var handlerError))
                return Status = new HotkeyStatus(HotkeyState.Failed, handlerError!);

            var status = Carbon.RegisterEventHotKey(
                code, mods, HotKeyId, Carbon.GetApplicationEventTarget(), 0, out var hotKey);

            if (status != Carbon.NoErr || hotKey == IntPtr.Zero)
            {
                return Status = new HotkeyStatus(HotkeyState.Failed, status == Carbon.EventHotKeyExistsErr
                    ? $"{spec.Display()} is already registered by this app"
                    : $"macOS refused the shortcut (RegisterEventHotKey returned {status})");
            }

            _hotKey = hotKey;
            return Status = new HotkeyStatus(HotkeyState.Active, $"{spec.Display()} is listening");
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacHotkey.Apply", ex);
            return Status = new HotkeyStatus(HotkeyState.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static uint CarbonModifiers(HotkeyModifiers modifiers) =>
        (modifiers.HasFlag(HotkeyModifiers.Control) ? Carbon.ControlKey : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Alt) ? Carbon.OptionKey : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Shift) ? Carbon.ShiftKey : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Command) ? Carbon.CmdKey : 0);

    /// <summary>
    /// One handler for the process's lifetime. Only one hot key is ever registered, so the
    /// handler does not need to read the EventHotKeyID back out of the event.
    /// </summary>
    private bool EnsureHandler(out string? error)
    {
        error = null;
        if (_handler != IntPtr.Zero) return true;

        var type = new Carbon.EventTypeSpec
        {
            EventClass = Carbon.EventClassKeyboard,
            EventKind = Carbon.EventHotKeyPressed,
        };

        var status = Carbon.InstallEventHandler(
            Carbon.GetApplicationEventTarget(),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHotKeyPressed,
            1, ref type, IntPtr.Zero, out var handler);

        if (status != Carbon.NoErr)
        {
            error = $"could not install the key handler (InstallEventHandler returned {status})";
            return false;
        }

        _handler = handler;
        return true;
    }

    [UnmanagedCallersOnly]
    private static int OnHotKeyPressed(IntPtr callRef, IntPtr theEvent, IntPtr userData)
    {
        // Runs on the main run loop, which is also the engine's thread — no marshalling.
        CrashLog.GuardCallback("MacHotkey.pressed", () => _instance?._onPressed());
        return Carbon.NoErr;
    }

    private void Unregister()
    {
        if (_hotKey == IntPtr.Zero) return;
        try { Carbon.UnregisterEventHotKey(_hotKey); } catch { /* going away anyway */ }
        _hotKey = IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();

        if (_handler != IntPtr.Zero)
        {
            try { Carbon.RemoveEventHandler(_handler); } catch { /* going away anyway */ }
            _handler = IntPtr.Zero;
        }

        if (_instance == this) _instance = null;
        Status = new HotkeyStatus(HotkeyState.Off, "no shortcut set");
    }
}

/// <summary>How a shortcut is written on this Mac, with this keyboard layout.</summary>
internal static class HotkeyText
{
    /// <summary>"⌃⌥⇧B" — modifier glyphs plus what the key prints on the layout in use.</summary>
    internal static string Display(this HotkeySpec spec) =>
        spec.ModifierGlyphs + MacKeyCodes.Label(spec.Key);
}

/// <summary>
/// Canonical key name to macOS virtual key code. Values are the kVK_ constants from the
/// macOS 26 SDK's HIToolbox Events.h, not remembered — the header was read to build this.
///
/// These codes are positional (kVK_ANSI_B is "the key where B sits on an ANSI keyboard"),
/// so on an exotic layout the label the settings window shows can disagree with the key cap.
/// The recorder captures whatever the user actually pressed, so what they see is what they
/// pressed; only the stored name is ANSI-flavoured.
/// </summary>
internal static class MacKeyCodes
{
    private static readonly Dictionary<string, uint> Codes = new(StringComparer.Ordinal)
    {
        ["A"] = 0x00, ["S"] = 0x01, ["D"] = 0x02, ["F"] = 0x03, ["H"] = 0x04, ["G"] = 0x05,
        ["Z"] = 0x06, ["X"] = 0x07, ["C"] = 0x08, ["V"] = 0x09, ["B"] = 0x0B, ["Q"] = 0x0C,
        ["W"] = 0x0D, ["E"] = 0x0E, ["R"] = 0x0F, ["Y"] = 0x10, ["T"] = 0x11, ["O"] = 0x1F,
        ["U"] = 0x20, ["I"] = 0x22, ["P"] = 0x23, ["L"] = 0x25, ["J"] = 0x26, ["K"] = 0x28,
        ["N"] = 0x2D, ["M"] = 0x2E,

        ["1"] = 0x12, ["2"] = 0x13, ["3"] = 0x14, ["4"] = 0x15, ["5"] = 0x17, ["6"] = 0x16,
        ["7"] = 0x1A, ["8"] = 0x1C, ["9"] = 0x19, ["0"] = 0x1D,

        ["Space"] = 0x31,

        ["F1"] = 0x7A, ["F2"] = 0x78, ["F3"] = 0x63, ["F4"] = 0x76, ["F5"] = 0x60,
        ["F6"] = 0x61, ["F7"] = 0x62, ["F8"] = 0x64, ["F9"] = 0x65, ["F10"] = 0x6D,
        ["F11"] = 0x67, ["F12"] = 0x6F, ["F13"] = 0x69, ["F14"] = 0x6B, ["F15"] = 0x71,
        ["F16"] = 0x6A, ["F17"] = 0x40, ["F18"] = 0x4F, ["F19"] = 0x50, ["F20"] = 0x5A,
    };

    internal static uint? Code(string key) => Codes.TryGetValue(key, out var code) ? code : null;

    /// <summary>
    /// What the key actually prints on the keyboard in use, for display only — ⌃⌥⇧B on ANSI is
    /// ⌃⌥⇧N on Dvorak, because the stored key code is a *position*. Translated through the
    /// current input source's 'uchr' layout data, the same way
    /// sindresorhus/KeyboardShortcuts renders its labels.
    ///
    /// F-keys and Space are never translated: they print nothing, and UCKeyTranslate would
    /// hand back a control character or an empty string. Falls back to the stored name for
    /// anything it cannot resolve, so a label is never blank.
    /// </summary>
    internal static string Label(string key)
    {
        if (key == "Space") return "␣";
        if (key.StartsWith('F') && key.Length > 1) return key;
        if (Code(key) is not { } code) return key;

        return Translate((ushort)code) ?? key;
    }

    /// <summary>
    /// The character a key code prints on the current layout, or null when no layout could be
    /// resolved. Exposed so the selftest can tell a real translation from the silent fallback
    /// to the stored ANSI name.
    /// </summary>
    internal static string? Translate(ushort keyCode)
    {
        var source = IntPtr.Zero;
        var asciiSource = IntPtr.Zero;

        try
        {
            source = Carbon.TISCopyCurrentKeyboardInputSource();
            var layout = source == IntPtr.Zero
                ? IntPtr.Zero
                : Carbon.TISGetInputSourceProperty(source, Carbon.PropertyUnicodeKeyLayoutData);

            // Every IME (Pinyin, Kotoeri) reports no layout data of its own; without this
            // fallback their users would see no key labels at all.
            if (layout == IntPtr.Zero)
            {
                asciiSource = Carbon.TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
                if (asciiSource == IntPtr.Zero) return null;
                layout = Carbon.TISGetInputSourceProperty(asciiSource, Carbon.PropertyUnicodeKeyLayoutData);
                if (layout == IntPtr.Zero) return null;
            }

            var bytes = CF.CFDataGetBytePtr(layout);
            if (bytes == IntPtr.Zero) return null;

            var buffer = new char[4];
            uint deadKeyState = 0;

            var status = Carbon.UCKeyTranslate(
                bytes, keyCode, Carbon.KeyActionDisplay, 0, Carbon.LMGetKbdType(),
                Carbon.TranslateNoDeadKeys, ref deadKeyState, buffer.Length, out var length, buffer);

            if (status != Carbon.NoErr || length <= 0) return null;

            var text = new string(buffer, 0, (int)length).ToUpperInvariant();
            return text.Length == 0 || char.IsControl(text[0]) ? null : text;
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacKeyCodes.Translate", ex);
            return null;
        }
        finally
        {
            if (source != IntPtr.Zero) CF.CFRelease(source);
            if (asciiSource != IntPtr.Zero) CF.CFRelease(asciiSource);
        }
    }

    /// <summary>The reverse lookup, for decoding the system's own shortcut table.</summary>
    internal static string? Name(long code)
    {
        foreach (var (name, value) in Codes)
            if (value == code) return name;
        return null;
    }
}

/// <summary>
/// macOS's own keyboard shortcuts, from two sources that answer different questions.
///
///   * <see cref="Table"/> — <c>CopySymbolicHotKeys</c>, the complete set: every system-wide
///     symbolic hot key, customised or not, 230 entries with 170 enabled on this machine. This
///     is what decides whether a combination is taken. It is what MASShortcut and
///     sindresorhus/KeyboardShortcuts both use, and the header is explicit that there is "no
///     way to determine which hotkey in the Keyboards preference pane corresponds to a
///     specific dictionary" — so it can say *taken*, never *by what*.
///   * <see cref="Overrides"/> — the com.apple.symbolichotkeys preference domain, which holds
///     only shortcuts the user has changed (20 here, 6 enabled, 2 carrying a readable
///     combination) but does key them by an id we can put a name to. Used solely to name a hit
///     the table has already found.
///
/// An earlier version had only the second source, which is why it could see 2 shortcuts where
/// there are 170.
/// </summary>
internal static class MacSystemHotkeys
{
    // NSEvent.ModifierFlags — the spelling symbolichotkeys stores, unrelated to Carbon's.
    private const long NsShift = 1 << 17;
    private const long NsControl = 1 << 18;
    private const long NsOption = 1 << 19;
    private const long NsCommand = 1 << 20;

    /// <summary>
    /// The handful of ids worth naming. Everything else is reported as "a system shortcut
    /// (id N)", which is still enough for someone to go and look.
    /// </summary>
    private static readonly Dictionary<long, string> Known = new()
    {
        [7] = "Move focus to the menu bar",
        [27] = "Move focus to the window drawer",
        [28] = "Save a picture of the screen as a file",
        [29] = "Copy a picture of the screen to the clipboard",
        [30] = "Save a picture of the selected area as a file",
        [31] = "Copy a picture of the selected area to the clipboard",
        [32] = "Mission Control",
        [33] = "Application windows",
        [36] = "Show Desktop",
        [60] = "Select the previous input source",
        [61] = "Select the next input source",
        [64] = "Spotlight search",
        [65] = "Finder search window",
        [79] = "Move left a space",
        [80] = "Move right a space",
        [81] = "Move up a space",
        [82] = "Move down a space",
        [175] = "Show Notification Center",
        [184] = "Turn Do Not Disturb on or off",
    };

    internal record Entry(long Id, long Code, long Flags)
    {
        internal string Describe() => Known.TryGetValue(Id, out var name) ? name : $"a system shortcut (id {Id})";
    }

    /// <summary>Description of the system shortcut using this combination, or null.</summary>
    internal static string? Find(HotkeySpec spec)
    {
        var wantedCode = MacKeyCodes.Code(spec.Key);
        if (wantedCode is null) return null;

        var wantedCarbon = MacHotkey.CarbonModifiers(spec.Modifiers);

        foreach (var (code, modifiers) in Table())
        {
            if (code != wantedCode) continue;

            // Compare the four real modifiers and ignore Fn. Entries like ⌃fn+F5 (keyboard
            // navigation) then also block plain ⌃F5, which is the safe direction to be wrong
            // in: over-blocking costs someone one alternative combination, under-blocking
            // costs them a shortcut that silently never fires.
            var mask = Carbon.CmdKey | Carbon.ShiftKey | Carbon.OptionKey | Carbon.ControlKey;
            if ((modifiers & mask) != wantedCarbon) continue;

            // The complete table has no names, so borrow one from the overrides domain when
            // the user happens to have customised this very shortcut.
            var name = NameFrom(spec) ?? "a macOS system shortcut";
            return $"{name} (System Settings → Keyboard → Keyboard Shortcuts)";
        }

        return null;
    }

    /// <summary>
    /// Every enabled system hot key as (Carbon key code, Carbon modifier mask). Complete —
    /// including the shortcuts nobody has ever touched, which the preferences domain omits.
    /// </summary>
    internal static List<(long Code, uint Modifiers)> Table()
    {
        var found = new List<(long, uint)>();
        var array = IntPtr.Zero;

        try
        {
            if (Carbon.CopySymbolicHotKeys(out array) != Carbon.NoErr || array == IntPtr.Zero)
                return found;

            var count = CF.CFArrayGetCount(array);

            for (nint i = 0; i < count; i++)
            {
                var entry = CF.CFArrayGetValueAtIndex(array, i);
                if (entry == IntPtr.Zero) continue;
                if (!CF.DictGetBool(entry, Carbon.SymbolicHotKeyEnabled)) continue;

                found.Add((
                    CF.DictGetLong(entry, Carbon.SymbolicHotKeyCode),
                    (uint)CF.DictGetLong(entry, Carbon.SymbolicHotKeyModifiers)));
            }
        }
        catch (Exception ex)
        {
            // A shortcut check is not worth taking the app down for.
            CrashLog.Write("MacSystemHotkeys.Table", ex);
        }
        finally
        {
            if (array != IntPtr.Zero) CF.CFRelease(array);
        }

        return found;
    }

    /// <summary>
    /// A system table entry as a spec, or null when the key is outside our set (arrows, Tab,
    /// Escape and the rest of what macOS uses and we do not offer).
    /// </summary>
    internal static HotkeySpec? AsSpec(long code, uint carbonModifiers)
    {
        if (MacKeyCodes.Name(code) is not { } key) return null;

        var modifiers =
            ((carbonModifiers & Carbon.ControlKey) != 0 ? HotkeyModifiers.Control : 0) |
            ((carbonModifiers & Carbon.OptionKey) != 0 ? HotkeyModifiers.Alt : 0) |
            ((carbonModifiers & Carbon.ShiftKey) != 0 ? HotkeyModifiers.Shift : 0) |
            ((carbonModifiers & Carbon.CmdKey) != 0 ? HotkeyModifiers.Command : 0);

        return new HotkeySpec(modifiers, key);
    }

    /// <summary>The name of a customised system shortcut matching this combination, if any.</summary>
    private static string? NameFrom(HotkeySpec spec)
    {
        var wantedCode = MacKeyCodes.Code(spec.Key);

        var wantedFlags =
            (spec.Modifiers.HasFlag(HotkeyModifiers.Control) ? NsControl : 0) |
            (spec.Modifiers.HasFlag(HotkeyModifiers.Alt) ? NsOption : 0) |
            (spec.Modifiers.HasFlag(HotkeyModifiers.Shift) ? NsShift : 0) |
            (spec.Modifiers.HasFlag(HotkeyModifiers.Command) ? NsCommand : 0);

        foreach (var entry in Overrides().Decoded)
        {
            if (entry.Code != wantedCode) continue;
            if ((entry.Flags & (NsShift | NsControl | NsOption | NsCommand)) != wantedFlags) continue;
            return entry.Describe();
        }

        return null;
    }

    /// <summary>
    /// The user's *customised* shortcuts, from the preferences domain. <c>Enabled</c> counts
    /// every one switched on; <c>Decoded</c> is the subset carrying a readable combination.
    /// Partial by construction — see the class remarks — and used for naming, not for deciding.
    /// </summary>
    internal static (List<Entry> Decoded, int Enabled) Overrides()
    {
        var found = new List<Entry>();
        var enabled = 0;

        var key = CF.CreateString("AppleSymbolicHotKeys");
        var domain = CF.CreateString("com.apple.symbolichotkeys");
        var root = IntPtr.Zero;

        try
        {
            root = CF.CFPreferencesCopyAppValue(key, domain);
            if (root == IntPtr.Zero) return (found, enabled);

            var count = (int)CF.CFDictionaryGetCount(root);
            if (count <= 0) return (found, enabled);

            var ids = new IntPtr[count];
            var entries = new IntPtr[count];
            CF.CFDictionaryGetKeysAndValues(root, ids, entries);

            for (var i = 0; i < count; i++)
            {
                // Keys are strings ("64"), not numbers.
                if (!long.TryParse(CF.FromString(ids[i]), out var id)) continue;
                if (!CF.DictGetBool(entries[i], "enabled")) continue;

                enabled++;

                var value = CF.DictGet(entries[i], "value");
                if (value == IntPtr.Zero) continue;

                // parameters = [character, virtual key code, modifier flags]. The character
                // is 65535 for keys that produce none (the F-keys), which is why the key
                // code is the field to match on.
                var parameters = CF.DictGet(value, "parameters");
                if (parameters == IntPtr.Zero || CF.CFArrayGetCount(parameters) < 3) continue;

                found.Add(new Entry(
                    id,
                    CF.NumberToLong(CF.CFArrayGetValueAtIndex(parameters, 1)),
                    CF.NumberToLong(CF.CFArrayGetValueAtIndex(parameters, 2))));
            }
        }
        catch (Exception ex)
        {
            // A shortcut check is not worth taking the app down for.
            CrashLog.Write("MacSystemHotkeys.Overrides", ex);
        }
        finally
        {
            if (root != IntPtr.Zero) CF.CFRelease(root);
            CF.CFRelease(key);
            CF.CFRelease(domain);
        }

        return (found, enabled);
    }
}
