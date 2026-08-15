using MonitorScreenSaver.Core;
using MonitorScreenSaver.Mac.Interop;

namespace MonitorScreenSaver.Mac;

/// <summary>
/// The menu bar presence: an NSStatusItem whose menu mirrors the Windows tray menu
/// item-for-item (App.xaml.cs BuildTray/UpdateMenu/RebuildRequesterMenu):
///
///   MonitorScreenSaver — status header (live countdown while the menu is open)
///   ─────
///   Holding display awake (n)     inline holder list, read-only: blacklisted holders
///   …                             are dimmed, nothing here is actionable
///   ─────
///   Blank now / Pause blanking / Settings… / Start at login / Quit
///
/// The holder list reports; it does not configure. Blacklisting and un-blacklisting both
/// live in the settings window, which has room for a button per row and a list of the
/// blacklist itself (SettingsWindow.axaml "BlacklistPanel"). The menu used to carry both,
/// and the result was the same process appearing twice — once dimmed in the holder list as
/// live status, once bright underneath as a remove button — which read as a duplicate.
///
/// Rendering is native NSMenu — macOS menus cannot be custom-painted, and shouldn't
/// be. The elevation rows from Windows ("names need admin", "Restart elevated") have
/// no macOS counterpart: attribution always works here.
/// </summary>
public sealed unsafe class MacTray : IDisposable
{
    private static IntPtr _targetClass;
    private static readonly Dictionary<IntPtr, Action> Handlers = [];
    private static MacTray? _instance;   // menu delegate dispatch

    private readonly MacApp _app;
    private readonly IntPtr _target;
    private readonly IntPtr _menu;
    private readonly IntPtr _statusItem;

    private readonly IntPtr _header;
    private readonly IntPtr _requestersHeader;
    private readonly IntPtr _requestersEnd;
    private readonly IntPtr _blankItem;
    private readonly IntPtr _pauseItem;
    private readonly IntPtr _startupItem;

    /// <summary>Items of the dynamic requester block, tracked for removal on rebuild.</summary>
    private readonly List<IntPtr> _dynamicItems = [];

    private bool _menuOpen;

    public MacTray(MacApp app)
    {
        _app = app;
        _instance = this;

        EnsureTargetClass();
        _target = ObjC.Send(ObjC.Send(_targetClass, ObjC.Sel("alloc")), ObjC.Sel("init"));

        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            _menu = ObjC.Send(ObjC.Send(ObjC.Class("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            // We manage enabled-state explicitly; autoenable would grey out every
            // item whose target isn't in the responder chain.
            ObjC.SendVoid(_menu, ObjC.Sel("setAutoenablesItems:"), false);
            ObjC.SendVoid(_menu, ObjC.Sel("setDelegate:"), _target);

            _header = AddItem("MonitorScreenSaver", null);
            AddSeparator();

            _requestersHeader = AddItem("Holding display awake", null);
            _requestersEnd = AddSeparator();

            _blankItem = AddItem("Blank now", () => _app.Engine.BlankNow());
            _pauseItem = AddItem("Pause blanking", () => _app.Engine.Paused = !_app.Engine.Paused);
            AddSeparator();

            AddItem("Settings…", _app.OpenSettings);
            _startupItem = AddItem("Start at login", ToggleStartup);
            AddSeparator();

            AddItem("Quit", _app.Quit);

            // The status item itself.
            var statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
            _statusItem = ObjC.SendForDouble(statusBar, ObjC.Sel("statusItemWithLength:"), -1.0 /* NSVariableStatusItemLength */);
            ObjC.SendVoid(_statusItem, ObjC.Sel("retain"));

            // NOTE (macOS 26): the item's on-screen window is rendered and owned by
            // ControlCenter, not this process — the button's own NSWindow never gets a
            // window-server device. Do not "verify" a status item via CGWindowList on
            // the app's pid; count ControlCenter's layer-25 windows instead.
            var button = ObjC.Send(_statusItem, ObjC.Sel("button"));
            ObjC.SendVoid(button, ObjC.Sel("setImage:"), MenuBarImage());

            var tooltip = CF.CreateString("MonitorScreenSaver");
            try
            {
                ObjC.SendVoid(button, ObjC.Sel("setToolTip:"), tooltip);
            }
            finally
            {
                CF.CFRelease(tooltip);
            }

            ObjC.SendVoid(_statusItem, ObjC.Sel("setMenu:"), _menu);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>The cheap text-only part of the menu, safe to rewrite every engine tick.</summary>
    public void RenderStatus(EngineStatus s)
    {
        if (!_menuOpen) return;

        SetTitle(_header, s.Paused
            ? "MonitorScreenSaver — paused"
            : s.Blanked
                ? "MonitorScreenSaver — blanked"
                : $"MonitorScreenSaver — {MacApp.Describe(s.Reason)}, blanks in {MacApp.Format(s.UntilBlank)}");

        SetTitle(_pauseItem, s.Paused ? "Resume blanking" : "Pause blanking");
    }

    private void UpdateMenu()
    {
        RenderStatus(_app.Engine.Status);

        // The shortcut goes in the title rather than as a real keyEquivalent: a key
        // equivalent would only fire while this menu is open, and the global hot key
        // (MacHotkey) already covers the rest of the time. Shown only when it is actually
        // held, so the menu never advertises a shortcut that was refused.
        SetTitle(_blankItem, _app.Hotkey.Status is { State: HotkeyState.Active } && _app.Settings.BlankNowHotkeySpec is { } spec
            ? $"Blank now   {spec.Display()}"
            : "Blank now");

        ObjC.SendVoid(_startupItem, ObjC.Sel("setState:"), (nint)(MacAutoStart.IsEnabled ? 1 : 0));
        RebuildRequesterMenu();
        _app.RefreshRequesters();
    }

    private void RebuildRequesterMenu()
    {
        foreach (var item in _dynamicItems)
            ObjC.SendVoid(_menu, ObjC.Sel("removeItem:"), item);
        _dynamicItems.Clear();

        var insertAt = ObjC.SendNInt(_menu, ObjC.Sel("indexOfItem:"), _requestersHeader) + 1;

        // Every row here is inert. `dimmed` is the only thing that varies, and it cannot be
        // expressed any other way: AppKit greys the title of any disabled menu item, and an
        // attributedTitle carrying an explicit labelColor is greyed identically (measured —
        // enabled/plain, disabled/plain and disabled/attributed render the same three greys).
        // So a row that must stay at full contrast has to remain *enabled*, and the most that
        // can be done is to give it no action, which is why these pass a null handler and then
        // undo the disabling MakeItem applies to handler-less items.
        void Add(string title, bool dimmed)
        {
            var item = MakeItem(title, null, indent: 1);
            if (!dimmed) ObjC.SendVoid(item, ObjC.Sel("setEnabled:"), true);
            ObjC.SendVoid(_menu, ObjC.Sel("insertItem:atIndex:"), item, (IntPtr)insertAt++);
            _dynamicItems.Add(item);
        }

        var snapshot = _app.Requesters;

        if (!snapshot.Available)
        {
            // Should not happen on macOS (attribution needs no rights); surface it
            // rather than pretending the list is empty.
            Add($"Unavailable — {snapshot.Unavailable}", dimmed: true);
            SetTitle(_requestersHeader, "Holding display awake");
            return;
        }

        var display = snapshot.Display.ToList();
        var ignored = display.Count(r => _app.Settings.IsBlacklisted(r.ShortName));
        var active = display.Count - ignored;

        if (display.Count == 0)
            Add("None", dimmed: true);

        foreach (var r in display)
        {
            var isIgnored = _app.Settings.IsBlacklisted(r.ShortName);

            var label = r.Reason is null
                ? $"{r.ShortName}   [{r.Kind}]"
                : $"{r.ShortName}   [{r.Kind}] — {r.Reason}";

            Add(isIgnored ? $"{label}   · blacklisted" : label, dimmed: isIgnored);
        }

        SetTitle(_requestersHeader, (active, ignored) switch
        {
            (0, 0) => "Holding display awake",
            (_, 0) => $"Holding display awake  ({active})",
            (0, _) => $"Holding display awake  ({ignored} blacklisted)",
            _ => $"Holding display awake  ({active} · {ignored} blacklisted)",
        });
    }

    private void ToggleStartup()
    {
        var enable = !MacAutoStart.IsEnabled;
        var error = MacAutoStart.Apply(enable);

        if (error is not null)
        {
            CrashLog.Write("MacAutoStart.Apply", new InvalidOperationException(error));
            return;
        }

        _app.Settings.StartWithWindows = enable;   // same JSON key as Windows, same meaning
        _app.Settings.Save();
    }

    /// <summary>
    /// The menu bar image: the app's own artwork (Resources/MenuBarIcon.png, @2x beside
    /// it) marked as a template, so AppKit takes the mask from its alpha channel and
    /// tints it for the current menu bar instead of drawing fixed colours — required on
    /// Tahoe's transparent bar, and what makes it follow light/dark and the Reduce
    /// Transparency settings.
    ///
    /// imageNamed: only finds it inside an .app bundle, so a bare-binary run (dev,
    /// harness) falls back to the SF Symbol "display".
    /// </summary>
    private static IntPtr MenuBarImage()
    {
        var name = CF.CreateString("MenuBarIcon");
        try
        {
            var image = ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("imageNamed:"), name);
            if (image != IntPtr.Zero)
            {
                ObjC.SendVoid(image, ObjC.Sel("setTemplate:"), true);
                return image;
            }
        }
        finally
        {
            CF.CFRelease(name);
        }

        var symbol = CF.CreateString("display");
        try
        {
            return ObjC.Send(ObjC.Class("NSImage"),
                ObjC.Sel("imageWithSystemSymbolName:accessibilityDescription:"), symbol, IntPtr.Zero);
        }
        finally
        {
            CF.CFRelease(symbol);
        }
    }

    // ------------------------------------------------------------------ plumbing

    private IntPtr AddItem(string title, Action? handler)
    {
        var item = MakeItem(title, handler, indent: 0);
        ObjC.SendVoid(_menu, ObjC.Sel("addItem:"), item);
        return item;
    }

    private IntPtr AddSeparator()
    {
        var separator = ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem"));
        ObjC.SendVoid(_menu, ObjC.Sel("addItem:"), separator);
        return separator;
    }

    private IntPtr MakeItem(string title, Action? handler, nint indent)
    {
        var cfTitle = CF.CreateString(title);
        var empty = CF.CreateString("");
        try
        {
            var item = ObjC.Send(ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("alloc")),
                ObjC.Sel("initWithTitle:action:keyEquivalent:"),
                cfTitle, handler is null ? IntPtr.Zero : ObjC.Sel("menuAction:"), empty);

            if (indent > 0) ObjC.SendVoid(item, ObjC.Sel("setIndentationLevel:"), indent);

            if (handler is null)
            {
                ObjC.SendVoid(item, ObjC.Sel("setEnabled:"), false);
            }
            else
            {
                ObjC.SendVoid(item, ObjC.Sel("setTarget:"), _target);
                Handlers[item] = handler;
            }

            return item;
        }
        finally
        {
            CF.CFRelease(cfTitle);
            CF.CFRelease(empty);
        }
    }

    private static void SetTitle(IntPtr item, string title)
    {
        var cf = CF.CreateString(title);
        try
        {
            ObjC.SendVoid(item, ObjC.Sel("setTitle:"), cf);
        }
        finally
        {
            CF.CFRelease(cf);
        }
    }

    // ------------------------------------------------------------------ objc target

    private static void EnsureTargetClass()
    {
        if (_targetClass != IntPtr.Zero) return;

        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "MSSTrayTarget", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("menuAction:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuAction, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("menuWillOpen:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuWillOpen, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("menuDidClose:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuDidClose, "v@:@");
        ObjC.objc_registerClassPair(cls);
        _targetClass = cls;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnMenuAction(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        CrashLog.GuardCallback("MacTray.menuAction", () =>
        {
            if (Handlers.TryGetValue(sender, out var handler)) handler();
        });
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnMenuWillOpen(IntPtr self, IntPtr cmd, IntPtr menu)
    {
        CrashLog.GuardCallback("MacTray.menuWillOpen", () =>
        {
            if (_instance is not { } tray) return;
            tray._menuOpen = true;
            tray.UpdateMenu();
        });
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnMenuDidClose(IntPtr self, IntPtr cmd, IntPtr menu)
    {
        CrashLog.GuardCallback("MacTray.menuDidClose", () =>
        {
            if (_instance is { } tray) tray._menuOpen = false;
        });
    }

    public void Dispose()
    {
        try
        {
            var statusBar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
            ObjC.SendVoid(statusBar, ObjC.Sel("removeStatusItem:"), _statusItem);
            ObjC.SendVoid(_statusItem, ObjC.Sel("release"));
            ObjC.SendVoid(_menu, ObjC.Sel("release"));
            ObjC.SendVoid(_target, ObjC.Sel("release"));
        }
        catch
        {
            // shutting down anyway
        }

        Handlers.Clear();
        if (_instance == this) _instance = null;
    }
}
