namespace MonitorScreenSaver.Core;

/// <summary>
/// Modifiers, named platform-neutrally so one settings value works on both heads.
/// <see cref="Alt"/> is Option on macOS; <see cref="Command"/> has no Windows counterpart
/// (the Windows key is reserved by the OS there, so a spec asking for it cannot be
/// registered on Windows and the Windows head will report it as such).
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Command = 8,
}

/// <summary>What happened to the shortcut the settings asked for.</summary>
public enum HotkeyState
{
    /// <summary>No shortcut configured.</summary>
    Off,

    /// <summary>Registered with the OS and listening.</summary>
    Active,

    /// <summary>Refused before registration: something we can see already uses it.</summary>
    Blocked,

    /// <summary>The OS refused the registration, or the platform call failed.</summary>
    Failed,
}

/// <param name="Detail">One line, written for the settings window.</param>
public sealed record HotkeyStatus(HotkeyState State, string Detail);

/// <summary>
/// A global shortcut, stored as text ("Ctrl+Alt+Shift+B") so settings.json stays readable
/// and portable. The key is a canonical *name*, not a platform key code — each head maps
/// the name to its own numbering (macOS virtual key codes, Windows virtual-key codes),
/// which is the only way one stored value can mean the same keystroke on both.
/// </summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>
    /// Keys a shortcut may use. Letters, digits and F-keys only: everything else is either
    /// layout-dependent (punctuation moves between keyboards) or already spoken for
    /// (Tab, Escape, arrows, Delete).
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedKeys =
    [
        .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
        .. Enumerable.Range(0, 10).Select(d => d.ToString()),
        .. Enumerable.Range(1, 20).Select(f => $"F{f}"),
        "Space",
    ];

    /// <summary>"Ctrl+Alt+Shift+B" — what gets written to settings.json.</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Command)) parts.Add("Cmd");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>
    /// "⌃⌥⇧" — the modifier part in macOS order. The key is appended separately because only
    /// the platform can say what a key code prints on the keyboard actually in use.
    /// </summary>
    public string ModifierGlyphs =>
        (Modifiers.HasFlag(HotkeyModifiers.Control) ? "⌃" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Alt) ? "⌥" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Shift) ? "⇧" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Command) ? "⌘" : "");

    /// <summary>
    /// "⌃⌥⇧B" using the stored (ANSI) key name. The mac head has a layout-aware version that
    /// shows what the key really prints; this is the fallback and what non-mac callers use.
    /// </summary>
    public string MacGlyphs =>
        (Modifiers.HasFlag(HotkeyModifiers.Control) ? "⌃" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Alt) ? "⌥" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Shift) ? "⇧" : "") +
        (Modifiers.HasFlag(HotkeyModifiers.Command) ? "⌘" : "") +
        (Key == "Space" ? "␣" : Key);

    public static bool TryParse(string? text, out HotkeySpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = HotkeyModifiers.None;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Control; break;
                case "alt" or "option" or "opt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "cmd" or "command" or "win" or "meta": mods |= HotkeyModifiers.Command; break;
                default:
                    if (key is not null) return false;   // two non-modifier tokens
                    key = Normalise(raw);
                    if (key is null) return false;
                    break;
            }
        }

        if (key is null) return false;
        spec = new HotkeySpec(mods, key);
        return true;
    }

    private static string? Normalise(string token)
    {
        var upper = token.ToUpperInvariant();
        if (upper == "SPACE") upper = "Space";
        return AllowedKeys.Contains(upper, StringComparer.Ordinal) ? upper : null;
    }

    /// <summary>
    /// The portable half of conflict checking: shapes that are a bad idea on any platform,
    /// independent of what is installed. Returns null when the shape is acceptable.
    ///
    /// Neither OS can enumerate what shortcuts other apps use internally — a global hotkey
    /// simply outranks the focused app and swallows the keystroke — so the only defence
    /// against that class of clash is to stay out of the space applications live in:
    ///
    ///   * one modifier is app-accelerator territory on both platforms (Ctrl+S, ⌘S);
    ///   * Command/Shift-only combos are what macOS menus are built from (⌘⇧A, ⌘⇧3), and
    ///     Ctrl/Shift-only is the same story on Windows (Ctrl+Shift+B builds in VS Code),
    ///     so a shortcut needs Control or Alt/Option to be out of that space.
    ///
    /// The platform check (<see cref="IGlobalHotkey.Blocker"/>) then handles what *is*
    /// enumerable: OS-owned shortcuts and other registered global hotkeys.
    /// </summary>
    public string? Weakness()
    {
        if (!AllowedKeys.Contains(Key, StringComparer.Ordinal))
            return $"{Key} is not a key a shortcut can use here — letters, digits and F1-F20 only";

        // F12 is reserved for the debugger on Windows "at all times", even when nothing is
        // being debugged, and this setting is shared between both heads.
        if (Key == "F12")
            return "F12 is reserved for the debugger on Windows, and this shortcut is shared with the Windows build";

        var count = System.Numerics.BitOperations.PopCount((uint)Modifiers);
        if (count < 2)
            return "needs at least two modifiers — one-modifier combinations are what apps use for their own shortcuts";

        if (!Modifiers.HasFlag(HotkeyModifiers.Control) && !Modifiers.HasFlag(HotkeyModifiers.Alt))
            return "needs Control or Option — Command and Shift alone are what app menus are built from";

        return null;
    }
}

/// <summary>
/// The seam for a system-wide shortcut. macOS: Carbon RegisterEventHotKey. Windows (not
/// implemented yet): RegisterHotKey, whose failure with ERROR_HOTKEY_ALREADY_REGISTERED
/// (1409) is the signal that another process owns the combination.
///
/// The asymmetry to keep in mind when implementing the second head: Windows reports that
/// clash at registration, macOS does not report it at all (measured — a second process
/// registering a combination another process already holds gets noErr), so
/// <see cref="Blocker"/> exists to do whatever pre-flight checking the platform allows
/// before a registration that will not fail even when it should.
/// </summary>
public interface IGlobalHotkey : IDisposable
{
    /// <summary>The result of the last <see cref="Apply"/>.</summary>
    HotkeyStatus Status { get; }

    /// <summary>
    /// Everything the platform can tell us *without* registering: OS-owned shortcuts,
    /// user-assigned system shortcuts, known application conventions. Null when nothing
    /// visible objects — which is not the same as "free".
    /// </summary>
    string? Blocker(HotkeySpec spec);

    /// <summary>
    /// Registers <paramref name="spec"/>, replacing whatever was registered before. Null
    /// unregisters. Never throws: a shortcut is a convenience, and the tray menu is always
    /// there.
    /// </summary>
    HotkeyStatus Apply(HotkeySpec? spec);
}
