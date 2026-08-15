# macOS 26 port — plan

Goal: run MonitorScreenSaver on macOS 26 (Tahoe) with **all existing features and UI identical**,
with the tray living in the **menu bar** (`NSStatusItem`) instead of the Windows notification
area. Share code where it's efficient, split per platform where it isn't.

Everything in this doc marked *(verified)* was checked on this machine (macOS 26.6, build
25G72, Command Line Tools SDK) on 2026-08-14 — see [Appendix](#appendix--verified-on-this-machine).
Anything marked *(spike)* still needs a proof-of-concept before it counts as fact.

---

## Progress

- **2026-08-14 — Phases 1 & 2 done** (uncommitted, working tree only).
  - Solution split into `src/MonitorScreenSaver.Core` (portable engine + models + seam
    interfaces), `src/MonitorScreenSaver.Windows` (the WPF head, moved; platform code
    under `Platform/`) and `src/MonitorScreenSaver.Mac` (interop + platform services +
    headless harness). Both heads build clean on macOS (`dotnet build`, 0 warnings);
    the Windows `--selftest` gate still needs a run on a real Windows machine.
  - Mac services implemented and verified against this machine: idle clock
    (CGEventSource), aggregate + per-process assertions (IOPM — matches
    `pmset -g assertions` row for row, `caffeinate -d` detected as a DISPLAY holder
    and attributed, no elevation), displays (UUID stable ids + NSScreen marketing
    names), sleep/wake + topology + lock/unlock events, audio probe, fullscreen
    heuristic, and the unmodified Core `BlankingEngine` running on a CFRunLoop timer
    (`MonitorScreenSaverMac engine 5`).
  - Deviations from the original plan: Phase 0 spikes were folded into Phase 2 (the
    real services + live harness verify the same facts); `proc_name` turned out to be
    denied for other users' processes, so holder names fall back to
    `sysctl kinfo_proc.p_comm` (how `ps` does it); the foreground watch polls the
    frontmost app at 1 Hz instead of observing NSWorkspace notifications (no observer
    classes, no permissions — revisit if 1 s granularity ever matters).
  - Next: Phase 3 (overlay NSWindows + AVPlayerLooper video), then tray, then the
    Avalonia settings window.

- **2026-08-14 — Phase 3 done** (committed through Phase 2; Phase 3 in working tree).
  - `MacOverlayWindow`: borderless non-activating NSPanel at screensaver level,
    canJoinAllSpaces + stationary + fullScreenAuxiliary. True black = opaque window;
    dim = window alpha (black↔dim morphs in place — the WPF rebuild-on-translucency
    machinery has no macOS counterpart, as predicted); video = AVPlayerLayer +
    AVQueuePlayer + AVPlayerLooper, muted, `preventsDisplaySleepDuringVideoPlayback`
    off. Only to/from-Video mode changes or a different file force a rebuild.
  - Verified on this machine (3 displays, mixed offsets): `overlay black|dim|video`
    each covered all displays with window-server-confirmed placement (the harness
    reads back its own windows via CGWindowList and compares bounds — the mac twin of
    the selftest's GetWindowRect check); video looped gaplessly across the clip end;
    `engine 5` ran the real idle→countdown→blank→cover loop live.
  - Wake-on-input rides the 250 ms idle poll (CGEventSource counts every input), so
    the overlay never needs its own event handling or permissions.
  - Known cosmetic gap: the cursor is not hidden over a blanked screen yet (Windows
    sets Cursor=None; NSCursor hiding is app-wide — revisit in the tray-app phase).
  - Next: Phase 4 (NSStatusItem menu bar tray + LSUIElement app bundle), then the
    Avalonia settings window.

- **2026-08-14 — Phase 4 done.** The mac head is now a working menu bar app.
  - `MacApp` (the App.xaml.cs twin): settings load, engine + overlays + system events
    wired, 3 s watchdog, requester cache, lock-file single instance, `[NSApp run]`.
    First run seeds ManagedDisplayIds with every display until the settings window
    exists (Phase 5); "Settings…" temporarily opens settings.json.
  - `MacTray`: NSStatusItem + NSMenu mirroring the Windows menu item-for-item —
    live-countdown header, inline "Holding display awake" list with click-to-blacklist
    and a blacklisted section, Blank now, Pause, Settings…, Start at login
    (SMAppService), Quit. Menu actions dispatch through a runtime-minted NSObject
    subclass; no Windows elevation rows (attribution is always available).
  - `tools/bundle-macos.sh` produces an ad-hoc-signed LSUIElement .app
    (publish/MonitorScreenSaver.app, ~74 MB self-contained).
  - **macOS 26 gotcha discovered:** status-item windows are rendered and owned by
    ControlCenter, not the creating app — the app-side button window never gets a
    window-server device, and CGWindowList on the app's pid shows nothing. Verified
    the item exists by diffing ControlCenter's layer-25 window count (+1 per display's
    menu bar while running). The future mac selftest must check it this way (or via
    NSStatusItem.isVisible), never via the app's own window list.
  - Remaining gate item (manual): click through the menu — countdown header, holder
    blacklist round-trip, pause/resume, quit. Start-at-login needs the bundle in a
    stable location (SMAppService registers the bundle path).

- **2026-08-14 — two post-Phase-4 fixes** (found via live testing of the menu bar app).
  - **Blank now did nothing.** Measured root cause: `MacActivityClock.LastInputMs` is
    now − idle across two independently-rounding clocks, so consecutive reads wobble
    ±1-2 ms with zero input; the engine's manual-blank hold compares consecutive
    reads for equality and cancelled itself instantly. Fix: the clock absorbs jumps
    under 100 ms and only moves forward past that (real input jumps whole seconds).
    Core untouched; Windows unaffected (GetLastInputInfo returns a stored tick).
  - **Cursor stayed visible over a blanked screen** — static bright pixels on OLED,
    the exact thing the app prevents. All supported routes verified dead from a
    non-activating app on macOS 26 (per-window cursorUpdate tracking: AppKit header
    says ActiveAlways unsupported; transparent NSCursor set: no-op; plain
    CGDisplayHideCursor: returns success, does nothing). Working fix: the private CGS
    connection property `SetsCursorInBackground` + refcounted
    CGDisplayHideCursor/ShowCursor on overlay show/hide — verified hidden during
    blank and restored after, via screencapture. Private API: failure is caught and
    degrades to a visible cursor (cosmetic), and the mac selftest should probe it.
  - Field note: a user-run desktop-mascot app floats above the overlays (it uses a
    high window level too) — overlay level arms races are out of scope; documented as
    a known cohabitation quirk.
  - Next: Phase 5 (Avalonia settings window), Phase 6 (mac selftest/watch parity,
    icns + template menu bar icon, notarization decision, docs).

- **2026-08-14 — Lunar cross-check (alin23/Lunar), for the record.** Lunar's primary
  dimming path is not a window at all: it writes gamma tables
  (`CGSetDisplayTransferByTable`) and drives hardware brightness, falling back to a
  click-through `.hud`-level overlay only on displays where gamma is unsupported
  (Sidecar/AirPlay/virtual — `GammaControl.swift:511-516`), with alpha capped at 0.85 so
  it never reaches true black. "BlackOut" is display mirroring through the private
  framework System Settings uses plus brightness/gamma zero, not an overlay. Nothing in
  the repo hides or dims the cursor, and nothing there contradicts the finding below.
  Gamma-zero is noted as a *possible future* TrueBlack backend (it would swallow the
  cursor and any always-on-top app in one move) but it cannot serve Video mode, it is a
  global resource that fights f.lux-style tools, and it is reset on display reconfig.
- **2026-08-14 — "put the cursor below the overlay" is impossible on macOS 26**
  (empirical). `CGWindowLevel.h` puts the cursor at `kCGMaximumWindowLevel − 1`, so a
  window one level above it should cover it; a Swift test window at 2147483631 with a
  verified-stationary cursor still rendered the cursor on top. Modern WindowServer
  composites the cursor above all windows regardless of level, so the private
  `SetsCursorInBackground` + `CGDisplayHideCursor` route already shipped stays the only
  working approach (six now tested).

- **2026-08-14 — Phase 5 done.** The settings window is real, and identical to the
  Windows one card-for-card.
  - **Lifetime:** Avalonia is set up with `SetupWithoutStarting()`, *not* a desktop
    lifetime — AppKit keeps owning the loop (`[NSApp run]` in `MacApp.Run`) and
    Avalonia's dispatcher rides the same main CFRunLoop. Verified live: the status item,
    the engine timer and the Avalonia window all run under one loop. Avalonia is
    initialised lazily on the first "Settings…", so sessions that never open settings
    never pay for it.
  - `UI/Theme.axaml` + `UI/SettingsWindow.axaml(.cs)` port `Theme.xaml` +
    `ConfigWindow.xaml(.cs)`: same palette, sizes, seven cards and copy, custom 40 px
    title bar (ExtendClientArea + NoChrome, so no traffic lights and our own
    minimise/close, like Windows). Stock controls (TextBox, Slider, ScrollBar) keep
    Fluent's templates recoloured through Fluent's own resource keys; only the pill
    toggle, segmented radio and ghost/primary/caption buttons carry hand-written
    templates, as on Windows.
  - Mac-specific by design: no elevation banner and no "start elevated" toggle
    (assertions need no admin), title chip reads "no admin needed", the status chips name
    the IOKit assertion families instead of the ES_* flags plus the two Windows-only
    signals, "Start at login" via SMAppService, AVFoundation wording and file-picker
    patterns, and the holder list is polled off a content signature (no
    RequestersUpdated event on the mac shell).
  - Fonts are the one agreed visual deviation: no Segoe UI Variable / Cascadia Mono on
    macOS, so UI text inherits San Francisco and monospace asks for SF Mono → Menlo.
  - **Three real bugs the port surfaced, all fixed:**
    - `ScrollViewer.Padding` is subtracted when Avalonia *arranges* content but not when
      it *measures* it, so every card was measured 32 px wider than it was laid out;
      wrapping text then desired more width than the card ever got, and Avalonia's
      StackPanel arranges an over-desiring child at its desired width
      (`StackPanel.ArrangeOverride`: `Math.Max(finalSize.Width, child.DesiredSize.Width)`)
      instead of clamping like WPF — the startup card visibly spilled past the window
      edge. The inset is now the content's `Margin`, which *is* honoured at measure.
      `HorizontalScrollBarVisibility="Disabled"` is also explicit: Avalonia defaults it
      to Auto (infinite measure width, nothing wraps) where WPF defaults to Disabled.
    - Fluent consumes `SliderPre/PostContentMargin` as `RowDefinition.Height`, so
      overriding them as `x:Double` threw `InvalidCastException` the first time a Slider
      was realised — i.e. the moment anyone picked Dim mode. They are `GridLength` now,
      and `TextControlSelectionHighlightColor` is a brush despite its name. Found with a
      throwaway harness that loads the shipped `Theme.axaml`, because Dim mode is not
      reachable without changing settings.
    - `tools/bundle-macos.sh` copied only the single-file executable, but a single-file
      publish does not embed native libraries: the bundle ran fine until someone opened
      Settings…, then threw `DllNotFoundException: libSkiaSharp`. The script now
      publishes into a clean directory (an incremental publish silently drops the loose
      dylibs) and copies + signs libSkiaSharp / libHarfBuzzSharp / libAvaloniaNative
      next to the executable. Bundle is 98 MB (was 80 MB).
  - New dev command `MonitorScreenSaverMac settings` runs the real app shell with the
    window already open — the macOS 26 tray menu is ControlCenter-owned and cannot be
    driven from a script, so this is how the window gets exercised and screenshotted.
  - Verified from the signed bundle: window opens with no errors, live engine status and
    real display names, and the status item is still present (ControlCenter layer-25
    windows 53 → 50 on quit = one menu bar per display).
  - Gate status: every card reviewed against the Windows window via screenshots. Still
    manual: driving the controls by hand (toggles, presets, Browse…, blacklist
    round-trip, Start at login from a bundle in a stable location).
  - Next: Phase 6 (mac selftest/watch parity, `.icns` + template menu bar icon,
    notarization decision, README/TECHNICAL updates).

- **2026-08-14 — Phase 6 done, except the one gate that needs a $99 certificate.**
  - **Icons.** New `tools/make-icns.sh` (the twin of `make-icon.ps1`) builds
    `MonitorScreenSaver.icns` plus 18/36 px menu bar art from the same `Assets/icon.png`,
    into `src/MonitorScreenSaver.Mac/Assets` (committed, like the `.ico`). The bundle now
    has a `Contents/Resources` and `CFBundleIconFile`; without them macOS drew the generic
    placeholder on the Dock tile of a minimised settings window — `LSUIElement` suppresses
    the *running* Dock icon, not the bundle icon. The status item now uses the app's own
    artwork marked `isTemplate`, so AppKit takes the mask from its alpha channel and tints
    it for the menu bar (no separate monochrome asset, and it follows light/dark); an
    unbundled run still falls back to the SF Symbol. The iconset stops at 256 px because
    the artwork does (447 px master) — everything macOS draws except Finder at maximum zoom.
  - **`selftest`: 75 checks, all passing, exit 0**, verified both unbundled and from the
    signed bundle, and on **osx-x64 under Rosetta** (75/75) as well as native arm64.
    Section-for-section parity with the Windows 103-check suite where the concept exists; it
    takes a real assertion via `IOPMAssertionCreateWithName` to prove detection, attribution
    and the blacklist decision end to end. Three sections have no Windows counterpart, each
    covering something that already broke once: the settings window's rendering stack
    realised off-screen (catches both the missing-native-dylib and wrong-resource-type bugs
    from Phase 5), the status item via `NSStatusItem.isVisible` (never our own window list),
    and the private cursor property. Found while writing it: window-server registration is
    async and the *first* panel is slowest, so the placement check polls — a fixed pump
    passed on the built-in display and failed on both externals.
  - **`watch [path]`** replaced the console-only event dump with the Windows shape: a file
    log opening with `pmset -g custom` and the display list, then events plus a 10 s
    heartbeat (idle, assertion flags, audio, fullscreen, frontmost, display holders). The
    `displaysleep` line is the point — on this machine it is 10 min against our 5 min
    timeout, so we win the race; shorter and macOS powers the panel off first.
  - **Packaging.** `bundle-macos.sh` signs with `SIGN_IDENTITY` when set (hardened runtime +
    the three entitlements CoreCLR needs to JIT under it: allow-jit,
    allow-unsigned-executable-memory, disable-library-validation) and ad-hoc otherwise, then
    `codesign --verify --deep --strict`. Deliberately **no universal binary**: `lipo` cannot
    merge two single-file .NET executables (the payload is appended after the Mach-O image
    and is silently dropped), and the Skia/HarfBuzz/AvaloniaNative dylibs we ship are already
    universal. One arch per run; osx-x64 verified working under Rosetta. Also fixed: bash 3.2
    (what macOS ships) errors on empty-array expansion under `set -u`.
  - **Docs.** README gained a macOS section (menu bar vs tray, no-admin story, the
    AVFoundation container gap, SF fonts, Dock-tile minimise, the cursor caveat, Gatekeeper
    right-click-Open, both diagnostics) and per-platform error-log paths. TECHNICAL gained a
    macOS section (the full seam table plus the five traps), mac build/icon/selftest/watch
    subsections, and a rewritten Layout — every path in the old one was stale since the
    Phase 1 split, as was the README's header image, which had been pointing at a moved file.
  - **Open gate: notarization.** Needs an Apple Developer ID ($99/yr) that nobody has bought,
    so "notarized build launches clean on a machine that never saw the dev environment"
    cannot be closed. The signing path is written but **untested**, and the notarize/staple
    commands are documented in the script header rather than run. Until then, another Mac
    needs right-click → Open once.
  - Still manual, still open from Phase 1: the Windows `--selftest` (103 checks) has never
    been run on a real Windows machine.

- **2026-08-14 — Post-Phase-6 fixes from the first real click-through.** Three things the
  user hit that no automated check would have caught, because all three are about how the app
  *presents itself* rather than what it does.
  - **Menu bar glyph is now drawn, not resampled.** The status item was the app icon reduced to
    18 pt, which as a template image (colours discarded, mask taken from the alpha channel) is a
    grey smudge. `tools/make-mac-icons.swift` draws a monitor on a stand with `SS` on its screen
    instead. The 8 pt letters set every other dimension: they have to survive the 1x rep, which
    is what a non-Retina external display draws, and an `S` below ~8 px is a blob. Verified
    against the live menu bar, both reps blown up nearest-neighbour.
  - **The Dock icon no longer outlives the settings window.** Root cause: Avalonia's macOS
    backend claims `NSApplicationActivationPolicyRegular` when it initialises, and nothing ever
    set it back, so the first *Settings…* put the app in the Dock for the rest of the session.
    `LSUIElement` cannot prevent this — it only decides the *initial* policy. Both transitions
    are explicit now (`MacUi.ShowInDock` on show, `HideFromDock` on `Closed`), and the selftest
    checks the round trip. Accessory-only was measured first and rejected: no Dock icon at any
    point, but the window does not activate (`frontmost` stays false — it opens behind, and
    unfocused). Found on the way: the menu bar said **"Avalonia Application"** while the window
    was open, from `Application.Name`, whose default is that literal string and which
    `CFBundleName` does not override; now set in `SettingsApp.Initialize`. Avalonia's own
    hardcoded *About Avalonia* item is still there, one level down in that menu.
  - **The Dock tile has a background.** The artwork is transparent, which among a row of opaque
    tiles reads as a sticker floating on the wallpaper. The `.icns` is now the artwork
    composited onto a near-black rounded tile — Apple's grid (824 of 1024 pt, ~185 pt corners),
    ground `#0E0F14`, the settings window's own background — rendered per size rather than
    resampled from one master. `tools/make-icns.sh` is now a thin `iconutil` wrapper over the
    Swift renderer.

- **2026-08-15 — Blank-now shortcut: macOS done, Windows foundation laid.**
  - **The portable half is in Core** (`Hotkey.cs`): `HotkeySpec` stored as text
    (`"Ctrl+Alt+Shift+B"`) with the key as a *name* rather than a platform code, so one
    settings value means the same keystroke on both heads; the shape rules; and the
    `IGlobalHotkey` seam. `AppSettings.BlankNowHotkey` defaults to `Ctrl+Alt+Shift+B` — three
    modifiers and no Command/Windows key is the one shape that is out of the way on both
    platforms. The Windows head compiles unchanged and ignores the setting until its
    `RegisterHotKey` implementation lands.
  - **macOS is on Carbon `RegisterEventHotKey`** — deprecated, still what every menu bar app
    uses, and the only route that needs no Accessibility grant (an `NSEvent` global monitor
    cannot consume the keystroke; a `CGEventTap` needs the user to grant Accessibility).
  - **The reason conflict detection is four layers deep**, measured on 26.6 with a throwaway C
    harness rather than assumed: `RegisterEventHotKey` returns noErr for ⌘Space, ⌘Tab, ⌘Q and
    ⌘⇧4, and noErr again when *another process* already holds the combination. The only clash
    it admits to is a duplicate inside the same process (−9878). Windows is the opposite —
    documented failure with `GetLastError` 1409. So: shape rules (≥2 modifiers, Control or
    Option among them, F12 out because Windows reserves it for the debugger), a hand-written
    reserved list, the live `com.apple.symbolichotkeys` table, and the registration itself.
    A combination we can see is taken is *refused*, not warned about.
  - **`com.apple.symbolichotkeys` is partial**, measured here: 20 entries because only
    user-changed ones are stored (Spotlight's id 64 is absent), 6 enabled but only 2 carrying a
    readable combination (ids 79-82, "move a space", are on with defaults stored elsewhere).
    Still worth having — the selftest proves the path by refusing this machine's own ⌃⌥␣
    ("Select the next input source", id 61).
  - **Registration is not delivery**, and that was a live false pass: hot key presses arrive as
    Carbon events on the application event target, which `NSApplication`'s loop drains. The new
    `hotkey` diagnostic first used a bare `CFRunLoopRun`, registered successfully, and received
    nothing. With `[NSApp run]` two synthetic ⌃⌥⇧B presses produced two fires. The real app was
    always correct — it has always run `[NSApp run]` — but the harness would have "proved" a
    working shortcut that never fires.
  - **`hotkey [combo]`** joins the diagnostics: holds the shortcut and prints every press
    *without* blanking, which is what makes delivery testable at all (and what separates "not
    registered" from "registered but something else eats it").
  - Still manual: the recorder itself — clicking *Set a shortcut…* and pressing combinations,
    including checking that a system-owned one like ⌘Space never reaches the field because the
    system consumes it first.

- **2026-08-15 — Two fixes taken from reading how other apps do this.** Checked
  [sindresorhus/KeyboardShortcuts](https://github.com/sindresorhus/KeyboardShortcuts) (the
  de-facto modern Swift library), [MASShortcut](https://github.com/shpakovski/MASShortcut) (its
  archived ancestor) and [tauri-apps/global-hotkey](https://github.com/tauri-apps/global-hotkey)
  (cross-platform Rust). All three register with Carbon `RegisterEventHotKey`, so the API choice
  here was the consensus one; the tauri crate adds a `CGEventTap` for media keys only, which
  Carbon cannot claim. Two things they had that we did not:
  - **`CopySymbolicHotKeys` replaces the preference-domain reader as layer 3.** Both mac
    libraries use it (`MASShortcutValidator`, `HotKeyCenter.systemShortcuts`), it is declared in
    `CarbonEvents.h` rather than being private, and it returns the *complete* system table:
    **230 entries, 170 enabled** here, against 2 readable in `com.apple.symbolichotkeys`.
    Spotlight's ⌘Space was previously invisible to us. The domain is still read, but only to
    name a hit — the complete table carries no names by design. Fn is ignored when comparing
    (so `⌃fn+F5` also blocks `⌃F5`), and the stricter check costs nothing in practice: the
    ⌃⌥+letter space this app recommends has exactly one entry in it (`⌃⌥⇧⌘Q`).
  - **Labels are translated through the active keyboard layout.** Key codes are positional, so
    `⌃⌥⇧B` on ANSI is a different letter on Dvorak. The stored name stays ANSI; the *label* now
    comes from `TISGetInputSourceProperty` + `UCKeyTranslate`, with a fallback to the
    ASCII-capable input source because every IME reports no layout data of its own. The selftest
    checks the translation rather than the label, since the label falls back to the ANSI name
    silently and on a US layout the two agree.
  - **Then the shortcut turned out to blank only while the key was held** — the screens came
    straight back on release. Root cause in Core, not in the shortcut code: `BlankNow` latched
    the input tick at the moment of the request, and the key release is a later HID event, so
    the hold cancelled itself milliseconds later. (The same cause was already being papered over
    for the mouse: the settings window delayed its own button by 600 ms so the click would be
    finished first.) The hold now ignores input until input has been quiet for 500 ms, then
    latches the settled tick — so a ten-second hold stays blanked, and the button is instant
    again with the delay removed. Covered by a fake-clock section in the selftest that fails on
    the old behaviour. Note for the Windows head: its overlay raises `WakeRequested` on
    `PreviewKeyDown`, which is a second cancel path the mac overlay does not have (it never
    raises the event) — worth checking there when the shortcut lands.
  - Deliberately **not** adopted: KeyboardShortcuts' graduated `ConflictPolicy` (it *warns*
    rather than blocks on system clashes), its `kEventHotKeyReleased` registration (only needed
    for press-and-hold), its `EventHotKeyID` dispatch (we hold exactly one hot key), and the
    recursive `NSApp.mainMenu` scan both libraries do (this app has no menu of its own worth
    scanning). Also noted for later: KeyboardShortcuts special-cases a macOS 15.0/15.1 bug where
    Option-plus-Shift-only shortcuts silently fail for *sandboxed* apps — not our case, but our
    shape rules would currently permit exactly that combination.

- **2026-08-15 — Release packaging: `tools/make-dmg.sh`.** A disk image rather than a zip,
  and not for the usual reasons — measured here the zip is *better* on every mechanical
  axis (38.4 MB against 43.1 MB, 2.3 s against 16.1 s, one command against three) and
  neither format changes Gatekeeper at all, since quarantine propagates through both. The
  deciding factor is specific to this app: `SMAppService` records an absolute bundle path
  (`sfltool dumpbtm` shows `URL: file:///…/MonitorScreenSaver.app/`), and an app launched
  out of `~/Downloads` is liable to App Translocation, which Apple DTS says is cleared only
  by moving it *in the Finder*. The `/Applications` symlink exists to provoke that move.
  - **Gatekeeper, measured rather than assumed.** A copy stamped with Safari's quarantine
    attribute produced, in `syspolicyd`: `GatekeeperPolicyScanError Code=-67018 "Code did
    not match any currently allowed policy"` → `Prompt shown` → `Adding Gatekeeper denial
    breadcrumb (open)` → `Terminating process due to Gatekeeper rejection`. The process
    starts and is then killed; the *breadcrumb* is what makes "Open Anyway" appear in
    System Settings, which is why it cannot be pre-authorised. `syspolicy_check
    distribution` calls the missing notarization ticket Fatal and the ad-hoc signature a
    Warning.
  - **The poster's colours are a constraint, not a style.** Finder draws icon labels with
    no shadow, halo or plate, and `.DS_Store` cannot set their colour — proven with a disk
    image carrying a black-to-white ramp, where the label over the dark end vanished.
    Screenshotting the finished window in both appearances then showed the ink is dark in
    *both* on 26.6 (darkest pixel 18, byte for byte identical), so a light background would
    be safe today; the panel is nevertheless held at the luminance that clears 4.5:1
    against black *and* white, because the rule is undocumented and Apple's to change.
  - **The bug was in the ruler, not the artwork.** A colour written `#75767F` measured back
    as `#878991` — a whole contrast grade — and the first diagnosis was wrong twice over
    (blamed on `deviceRGB` output, then on `NSGradient`'s convenience initialisers
    interpolating in generic RGB). Both were disproved by isolation: the raw context bytes
    were `#75767F` all along, `NSGradient(colorsAndLocations:)` renders the colour exactly,
    and a `deviceRGB` rep and an explicit sRGB `CGContext` produce byte-identical pixels
    with the same "sRGB IEC61966-2.1" profile. The actual fault is that
    `NSBitmapImageRep.colorAt` drops the colour-space tag and reports lightened values, so
    the *measuring* tool was lying. The acceptance test now reads raw sRGB bytes and
    detects buffer orientation from a known landmark rather than assuming; both "fixes"
    were reverted, since a no-op change carrying a false rationale is worse than none.
  - **Finder needs the window closed and reopened** before a newly set `background picture`
    renders on macOS 26; setting it on the open window silently does nothing, while icon
    positions set in the same script take effect immediately.
  - Still open: notarization (needs the $99 Developer ID; `make-dmg.sh` signs the image
    when `SIGN_IDENTITY` is set, untested), and `CFBundleShortVersionString` is still
    hardcoded to 1.1.0 in `bundle-macos.sh`, which is what names the `.dmg`.

- **2026-08-15 — The tray holder list is read-only now, on both heads.** Reported from use:
  a blacklisted process appeared twice in the menu, once greyed and once not. Both rows were
  intentional and meant different things — the greyed one was live status in the holder list,
  the bright one was a remove button in the `Blacklisted — click to remove` section
  underneath — but as two rows carrying the same process name they read as a duplicate.
  The blacklist section is gone from both menus; the holder list stays, dimmed when
  blacklisted, and nothing in it is clickable. Blacklisting and un-blacklisting were already
  fully covered by the settings windows (a button per holder row plus a `BlacklistPanel`
  listing the blacklist itself), so nothing was stranded — including entries for processes
  that are not currently running, which the menu could never have removed anyway.
  - **AppKit forces a compromise on the "inert but not greyed" rows.** It greys the title of
    any disabled `NSMenuItem`, and an `attributedTitle` carrying an explicit `labelColor`
    is greyed identically — measured by popping a menu with enabled/plain, disabled/plain
    and disabled/attributed rows and screenshotting: the last three render the same grey.
    So a row cannot be both full-contrast and unclickable. Active holders stay *enabled*
    with no action, which is inert in effect but will still highlight under the cursor.
    The alternative was greying every holder, which loses the distinction the whole change
    is about. A custom `NSMenuItem.view` would fix it properly and is not worth the AppKit
    view plumbing in a codebase that talks to the runtime through `objc_msgSend` by hand.

- **2026-08-15 — Windows caught up, and the Phase 1 gate is finally closed.** Everything the
  mac work added that was portable had left the Windows head behind. Run on the real machine
  (Windows 11 26200, 3 displays, unelevated).
  - **`--selftest` passes on Windows: 136 checks, exit 0**, four runs, stable. This is the gate
    that had been open since the Phase 1 split — "the Windows `--selftest` has never been run on
    a real Windows machine" — and it passed unmodified at 103 checks *before* anything here was
    added, so the split itself never regressed the Windows head. The whole solution, mac project
    included, also builds clean on Windows (`dotnet build`, 0 warnings): the mac head is plain
    `net9.0` P/Invoke, so it compiles anywhere even though it only runs on macOS.
  - **The blank-now shortcut now exists on Windows** (`Platform/WindowsHotkey.cs`), which was the
    one feature the shared settings file promised and only one head delivered —
    `AppSettings.BlankNowHotkey` has defaulted to `Ctrl+Alt+Shift+B` since the Core work, and
    Windows had been persisting it and ignoring it. `RegisterHotKey` against a hidden
    `HwndSource`, `MOD_NOREPEAT`, plus the tray item showing the combination while it is held and
    a recorder card in the settings window that mirrors the mac one card-for-card.
  - **The conflict story is the mirror image of macOS, and that shapes the code.** Registration is
    authoritative here (1409 `ERROR_HOTKEY_ALREADY_REGISTERED`), so `Blocker` is thin — shape,
    the Windows key, and a hand-written conventions list — and the settings window *rolls back* a
    combination the OS refuses instead of pre-flighting four layers deep. Proved rather than
    assumed: the selftest takes `Ctrl+Alt+Shift+F19` twice and requires the second to fail with
    1409, then requires it to be free again after release.
  - **The overlay's `PreviewKeyDown` cancel path was flagged in the 2026-08-15 note above as
    "worth checking there when the shortcut lands". It was checked, and it was a real hazard.**
    The plan assumed it was harmless because an overlay never has focus. That assumption is
    false. Two measurements, in order, because the first one misled:
    - Asserting `!win.IsActive` failed *deterministically* on the second of three overlays, every
      run — WPF reported `IsActive=true` and `IsKeyboardFocusWithin=true` with
      `Keyboard.FocusedElement` being the overlay, for a window created straight after another was
      destroyed. That one is a red herring: it is in-process bookkeeping and delivers nothing.
      `IsActive` is not a safe proxy for "can receive keystrokes" on a `WS_EX_NOACTIVATE` window.
    - `GetForegroundWindow` is the real question, and it returned the overlay's own handle on
      **some runs and not others** (1 of 4 on the final build). So the system does hand the
      foreground to a non-activating topmost window when the previous holder has just been
      destroyed, and `WS_EX_NOACTIVATE` + `ShowActivated=false` are the whole of what the app can
      do about it.
    - Consequence: a foreground overlay receives the auto-repeat `WM_KEYDOWN` stream of a *held*
      blank-now shortcut, and waking on it would cancel the blank it just asked for — the mac
      bug, arriving on Windows by a path that bypasses `ManualBlankSettleMs` entirely, since
      `WakeRequested` calls `NoteActivity` and never consults the settle. Fixed downstream:
      `OverlayWindow` ignores `KeyEventArgs.IsRepeat`. A fresh keypress still wakes, which is what
      a screensaver is for. The selftest *reports* the foreground observation instead of asserting
      it, because it varies by environment and asserting would only make the suite flake.
  - **The 600 ms delay on the settings window's *Blank now* is gone** — it predates
    `BlankingEngine.ManualBlankSettleMs` and was the papering-over that the settle window
    replaced, exactly as on macOS. It was also inconsistent with the Windows *tray* item, which
    has always fired instantly.
  - **Self-test parity, where the concept exists on both.** Windows gained the fake-clock
    `ManualBlankHold` (shared Core logic with a known shipped bug, previously tested from one head
    only), the blacklist decision (`BlacklistCovers`, previously untested here — synthetic
    snapshots rather than live, because attribution needs elevation on Windows and a live version
    would silently skip for most people), three display assertions the mac side already had, and
    the hotkey section. Also fixed: a report-write failure message was appended to a local string
    nothing read again, so it was discarded instead of reported.
  - Still Windows-only-missing, deliberately: no `hotkey` diagnostic command. The mac one exists
    because a macOS registration proves nothing about delivery; on Windows a failed registration
    is reported, so the same command would mostly restate what the selftest already checks. The
    residual case it *would* catch is a `WH_KEYBOARD_LL` hook in another process swallowing the
    keystroke ahead of us — rare enough to wait for someone to actually hit it.

---

## TL;DR

Keep it one C# solution, three projects:

```
src/MonitorScreenSaver.Core/       net9.0          — policy engine, settings, models, crash log
src/MonitorScreenSaver.Windows/    net9.0-windows  — the existing WPF/WinForms head, moved as-is
src/MonitorScreenSaver.Mac/        net9.0          — Avalonia settings window + thin objc interop
                                                     for overlays, menu bar, AVFoundation, IOKit
```

No Swift rewrite. The value of this app is the policy engine and the settings model —
[Core/BlankingEngine.cs](Core/BlankingEngine.cs) and [Core/AppSettings.cs](Core/AppSettings.cs)
are already ~90% portable C#. Rewriting them in Swift means maintaining the blanking decision
twice forever.

One genuinely good surprise: **the entire elevation story disappears on macOS.** Holder names
and the blacklist need admin on Windows because `powercfg /requests` is admin-only
([TECHNICAL.md](TECHNICAL.md#whos-holding-the-display-awake)). On macOS the same data comes
from `IOPMCopyAssertionsByProcess` / `pmset -g assertions`, which work as a normal user
*(verified — ran unelevated, got pid + process name + reason strings)*. So "Restart elevated",
the UAC dance, the scheduled-task autostart, and the "names need admin" tray rows are all
Windows-only code that the mac head simply doesn't have.

---

## The one big decision: UI stack for the mac head

**Recommendation: Avalonia for the settings window, raw `NSWindow` interop for overlays,
`NSStatusItem` for the tray.**

Why Avalonia: [UI/ConfigWindow.xaml](UI/ConfigWindow.xaml) (419 lines) +
[UI/Theme.xaml](UI/Theme.xaml) (365 lines) port near-mechanically to Avalonia XAML (same
concepts; styles/triggers syntax differs), so the settings window stays *visually identical*
on both platforms from one design. It's C#, so the 681-line
[ConfigWindow.xaml.cs](UI/ConfigWindow.xaml.cs) codebehind ports rather than being rewritten
in another language.

The catch, honestly:

1. **Two XAML dialects in the repo** (WPF + Avalonia) until/unless the Windows head migrates
   to Avalonia too. That migration is deliberately out of scope — the Windows build must not
   regress, and WPF works today.
2. **Avalonia has no video element.** Doesn't matter: video overlays should be native
   `AVPlayerLayer` regardless (hardware decode, and `AVPlayerLooper` gives gapless looping —
   strictly better than the rewind-on-`MediaEnded` hack at
   [Core/OverlayWindow.cs:122-127](Core/OverlayWindow.cs#L122-L127)).
3. **Overlays shouldn't be Avalonia windows at all.** They're a black rect, an alpha rect, or
   a video layer — no layout engine needed. Raw `NSWindow` via objc interop (~150 lines) avoids
   fighting a UI framework over window level and collection behavior.

Alternatives considered and rejected:

- **Fully native AppKit head** (`net9.0-macos` TFM or hand-rolled interop): most native feel,
  but the settings window is a full rebuild and won't look identical without heavy custom
  drawing. All cost, no reuse.
- **Swift rewrite:** duplicates the policy engine, the settings model, and every future fix.

---

## What's portable today (census)

5,385 lines total. Split:

| Bucket | Files | Lines | Action |
|---|---|---|---|
| Portable as-is | [Core/AppSettings.cs](Core/AppSettings.cs), [Core/CrashLog.cs](Core/CrashLog.cs) | ~390 | Move to Core. `Environment.SpecialFolder.ApplicationData` maps to `~/Library/Application Support` on macOS since .NET 8 ([breaking-change doc](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/getfolderpath-unix)), so [AppSettings.cs:144](Core/AppSettings.cs#L144) works untouched. |
| Portable after seam extraction | [Core/BlankingEngine.cs](Core/BlankingEngine.cs), [Core/OverlayManager.cs](Core/OverlayManager.cs) | ~430 | The decision logic and the overlay set-reconciliation are pure; their OS inputs become injected interfaces (see seam map). `BlankingEngine` also swaps WPF's `DispatcherTimer` ([BlankingEngine.cs:47](Core/BlankingEngine.cs#L47)) for an injected timer. |
| Windows-only, stays in the Windows head | [Core/Native.cs](Core/Native.cs), [Core/PowerRequests.cs](Core/PowerRequests.cs), [Core/DisplayEnumerator.cs](Core/DisplayEnumerator.cs), [Core/SystemEventSink.cs](Core/SystemEventSink.cs), [Core/AudioActivity.cs](Core/AudioActivity.cs), [Core/AutoStart.cs](Core/AutoStart.cs), [Core/OverlayWindow.cs](Core/OverlayWindow.cs), [Core/SelfTest.cs](Core/SelfTest.cs), [Core/WatchMode.cs](Core/WatchMode.cs), [App.xaml.cs](App.xaml.cs), [UI/DarkMenu.cs](UI/DarkMenu.cs) | ~2,900 | Each gets a macOS twin behind the shared interface. |
| UI to port | [UI/ConfigWindow.xaml](UI/ConfigWindow.xaml) + codebehind, [UI/Theme.xaml](UI/Theme.xaml) | ~1,465 | WPF → Avalonia port, one-time. |

The [GlobalUsings.cs](GlobalUsings.cs) WPF/WinForms type-pinning is a Windows-head concern and
doesn't follow Core.

---

## Seam map — every Windows dependency and its macOS twin

| # | Seam (interface) | Windows today | macOS implementation | Parity |
|---|---|---|---|---|
| 1 | `IInputIdle` | `GetLastInputInfo` + 32-bit tick-wrap arithmetic ([BlankingEngine.cs:145-157](Core/BlankingEngine.cs#L145-L157)) | `CGEventSourceSecondsSinceLastEventType(kCGEventSourceStateHIDSystemState, kCGAnyInputEventType)` *(verified: declared in SDK `CGEventSource.h:141`; `kCGAnyInputEventType` in `CGEventTypes.h:491`)*. Reading idle seconds needs no TCC permission — only event *taps* do *(spike to confirm)*. Backup source: `IORegistry` `HIDIdleTime` *(verified readable unelevated via `ioreg`)*. The 49.7-day wrap workaround disappears; `GetTickCount64` → [`Environment.TickCount64`](https://learn.microsoft.com/en-us/dotnet/api/system.environment.tickcount64). | identical |
| 2 | `IForegroundWatch` | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` ([BlankingEngine.cs:159-170](Core/BlankingEngine.cs#L159-L170)) | `NSWorkspace.didActivateApplicationNotification` *(verified: `NSWorkspace.h:294`, macos 10.6+)* | **flexes** — app-level, not window-level. Window-level focus inside one app needs the Accessibility permission (AXObserver). Ship app-level in v1; AX as opt-in later. |
| 3 | `IDisplayHold` (aggregate yes/no) | `CallNtPowerInformation(SystemExecutionState)` → `ES_DISPLAY_REQUIRED` ([PowerRequests.cs:24-44](Core/PowerRequests.cs#L24-L44)) | `IOPMCopyAssertionsStatus`, check `kIOPMAssertionTypePreventUserIdleDisplaySleep` *(verified in SDK `IOPMLib.h:1007`)* | identical |
| 4 | `IHolderList` (names + blacklist) | `powercfg /requests` parser, **admin-only** ([PowerRequests.cs:46+](Core/PowerRequests.cs#L46)) | `IOPMCopyAssertionsByProcess` *(verified in SDK `IOPMLib.h`, available since 10.7; `pmset -g assertions` confirmed to list pid + name + reason unelevated)* | **better** — no elevation, ever. All elevation UI ([App.xaml.cs:314-333](App.xaml.cs#L314-L333), `RelaunchElevated`, `StartElevated`) stays Windows-only. Kernel assertions (seen in `pmset` output) map to the existing `Driver` kind ([PowerRequests.cs:7](Core/PowerRequests.cs#L7)). |
| 5 | `IFullscreenDetect` | `SHQueryUserNotificationState` ([BlankingEngine.cs:174-186](Core/BlankingEngine.cs#L174-L186)) | No equivalent — exclusive fullscreen doesn't exist on macOS (the compositor always owns the screen), so the original hazard ([TECHNICAL.md](TECHNICAL.md), "Beyond Windows") is gone. Keep the toggle for parity; implement as "frontmost window covers an entire screen" via `CGWindowListCopyWindowInfo` *(spike — frame/ownerPID availability without the screen-recording permission needs confirming)*. | flexes — same toggle, heuristic mechanism |
| 6 | `IAudioActivity` | WASAPI endpoint peak meters ([Core/AudioActivity.cs](Core/AudioActivity.cs)) | v1: CoreAudio `kAudioDevicePropertyDeviceIsRunningSomewhere` on output devices — no permission, but "device busy" ≈ "playing", so paused apps can false-positive. v2 candidate: CoreAudio process taps (macOS 14.2+) for true post-mix metering — costs a TCC audio-capture prompt. *(both spike)* Option ships off by default anyway ([AppSettings.cs:219](Core/AppSettings.cs#L219)). | flexes in v1 (coarser signal), fixable in v2 |
| 7 | `IDisplays` | `EnumDisplayMonitors` + DISPLAYCONFIG EDID ids ([Core/DisplayEnumerator.cs](Core/DisplayEnumerator.cs)) | `CGGetActiveDisplayList` for topology; **stable id** = `CGDisplayCreateUUIDFromDisplayID` *(verified: `ColorSyncDevice.h:233`, since 10.4)* — survives replug, same role as the `MONITOR\ACR0C5D\…` id; friendly name = `NSScreen.localizedName` *(verified: `NSScreen.h:57`, 10.15+)*. The physical-pixel `SetWindowPos` DPI dance ([OverlayWindow.cs:227-236](Core/OverlayWindow.cs#L227-L236)) is a WPF problem; AppKit windows place in screen points directly. | identical |
| 8 | `IOverlay` | WPF `OverlayWindow`: opaque black / `AllowsTransparency` dim / `MediaElement` video ([Core/OverlayWindow.cs](Core/OverlayWindow.cs)) | One borderless `NSWindow` per display: `level = NSScreenSaverWindowLevel` *(verified: `NSWindow.h:201` = `kCGScreenSaverWindowLevel` 1000)*, `collectionBehavior = canJoinAllSpaces \| fullScreenAuxiliary \| stationary`, non-activating. **Black** = opaque black window. **Dim** = same window, `alphaValue` — macOS is always composited, so the whole software-render / rebuild-on-opacity-boundary machinery ([OverlayWindow.cs:40-48](Core/OverlayWindow.cs#L40-L48)) collapses: `TryApply` can morph black↔dim in place; only a video file change rebuilds. **Video** = `AVPlayerLayer` + `AVQueuePlayer` + [`AVPlayerLooper`](https://developer.apple.com/documentation/avfoundation/avplayerlooper) (gapless), muted, `videoGravity` maps 1:1 to Fit/Fill/Stretch. Set `preventsDisplaySleepDuringVideoPlayback = false` *(verified: `AVPlayer.h:873`)* — it defaults to on, and it's the exact self-request problem the Windows build guards against ([BlankingEngine.cs:212-218](Core/BlankingEngine.cs#L212-L218)); on macOS we can just turn it off at the source. Wake detection: local `mouseMoved`/`keyDown`/`scrollWheel` on the overlay (no permission needed for events on your own window) with the settle-time/4px-threshold logic from [OverlayWindow.cs:275-291](Core/OverlayWindow.cs#L275-L291) moved into Core; the 250 ms idle poll is the backstop either way. | identical (simpler) |
| 9 | `ISystemEvents` | `WM_POWERBROADCAST` / `WM_DISPLAYCHANGE` / `WM_WTSSESSION_CHANGE` ([Core/SystemEventSink.cs](Core/SystemEventSink.cs)) | `NSWorkspace` `willSleep`/`didWake` + `screensDidSleep`/`screensDidWake`; `NSApplication.didChangeScreenParametersNotification` for topology; lock/unlock via `DistributedNotificationCenter` `com.apple.screenIsLocked` / `com.apple.screenIsUnlocked` — undocumented but long-stable, flagging it honestly *(spike)*. | identical |
| 10 | `IAutoStart` | Registry `Run` key / elevated scheduled task ([Core/AutoStart.cs](Core/AutoStart.cs)) | [`SMAppService.mainApp`](https://developer.apple.com/documentation/servicemanagement/smappservice) register/unregister (macOS 13+); shows up in System Settings → Login Items. No elevated variant needed (see seam 4). Label becomes "Start at login". | identical |
| 11 | Tray | `NotifyIcon` + custom dark renderer ([App.xaml.cs:168-224](App.xaml.cs#L168-L224), [UI/DarkMenu.cs](UI/DarkMenu.cs)) | `NSStatusItem` + `NSMenu`. Same structure as [App.xaml.cs:198-211](App.xaml.cs#L198-L211): status header, "Holding display awake" inline list with click-to-blacklist + blacklisted section, Blank now, Pause, Settings…, Start at login, Quit. Live countdown in the open menu ([App.xaml.cs:137](App.xaml.cs#L137)) works — `NSMenuItem` titles update while open. Prefer direct `NSStatusItem` interop over Avalonia's `TrayIcon` wrapper for that control *(spike decides)*. Menu bar icon ships as a **template image** (monochrome) — required to look right on Tahoe's transparent menu bar. | **flexes** — menu renders native (macOS menus can't be custom dark-painted; that's correct platform behavior) |
| 12 | Single instance | Named mutex `Local\MonitorScreenSaver.SingleInstance` ([App.xaml.cs:13-35](App.xaml.cs#L13-L35)) | .NET named mutexes are claimed to work on Unix *(spike)*; fallback is an `O_EXCL` lock file in Application Support or an `NSRunningApplication` bundle-id check. The 15 s relaunch grace ([App.xaml.cs:102-113](App.xaml.cs#L102-L113)) exists only for the elevation relaunch → Windows-only. | identical |

---

## Feature parity matrix

Every feature from the [README](README.md), and where it lands:

| Feature | macOS status |
|---|---|
| Per-display managed list, stable across replug | identical (seam 7) |
| True black / Dim / Video overlays | identical (seam 8) |
| Per-display config vs shared config | identical — pure Core logic ([AppSettings.cs:129-142](Core/AppSettings.cs#L129-L142)) |
| Idle timeout presets + custom | identical — Core |
| Live status (awake reason, countdown, exec state) | identical — Core |
| Blank now / Pause | identical — Core ([BlankingEngine.cs:104-137](Core/BlankingEngine.cs#L104-L137)) |
| Start with OS | identical, renamed "Start at login" (seam 10) |
| Holder list with PROCESS/SERVICE/DRIVER tags + reasons | **better** — no admin needed (seam 4) |
| Blacklist / unblacklist holders | **better** — works without elevation |
| Restart elevated, admin chip, elevated scheduled task | **gone** — not needed on macOS; UI shows nothing (or "not required") |
| Keyboard/mouse activity | identical (seam 1) |
| Window-focus activity | flexes — app-level activation in v1 (seam 2) |
| Never blank during exclusive fullscreen | flexes — heuristic; the original hazard doesn't exist on macOS (seam 5) |
| Never blank while audio plays | flexes in v1 — coarser "output device busy" signal (seam 6) |
| Video formats | **flexes** — AVFoundation plays MP4/M4V/MOV + H.264/HEVC natively; **WMV/AVI/MKV/WebM won't play** (README currently promises those on Windows via Media Foundation, [README.md:168](README.md#L168)). v1: document the gap, degrade to black exactly like a missing file does today ([OverlayWindow.cs:129-134](Core/OverlayWindow.cs#L129-L134)). Later option: ffmpeg-based fallback. |
| Video stretch Fit/Fill/Stretch | identical — `videoGravity` maps 1:1 |
| Lock screen untouchable | identical limitation — loginwindow owns it, same doc note |
| "Set OS display timeout longer than the app's" guidance | identical concept — System Settings → Lock Screen, or `pmset displaysleep 30`; also set the built-in screen saver to a longer delay or Never |
| `--selftest` real-machine diagnostics | ported — mac-specific check suite, same philosophy as [Core/SelfTest.cs](Core/SelfTest.cs) |
| `--watch` transition logger | ported onto seam 9 notifications |
| error.log | identical — `~/Library/Application Support/MonitorScreenSaver/error.log` |
| Single self-contained binary | flexes — macOS wants a signed/notarized `.app` bundle, not a bare executable |

---

## Phases

Each phase has a hard gate; nothing merges without it.

**Phase 0 — spikes (~1-2 days).** Tiny standalone C# proofs, one per open question: idle
seconds via `CGEventSourceSecondsSinceLastEventType` P/Invoke; `IOPMCopyAssertionsByProcess`
CFDictionary marshaling; `AVPlayerLooper` inside a screensaver-level `NSWindow`; named-mutex
behavior on macOS; `CGWindowListCopyWindowInfo` fullscreen heuristic + permission behavior;
`kAudioDevicePropertyDeviceIsRunningSomewhere` probe; lock/unlock distributed notifications.
Each is <100 lines and kills one *(spike)* flag above.
Note: this machine has no .NET SDK yet (`dotnet --list-sdks` is empty) — install .NET 9 first.

**Phase 1 — split the solution, zero behavior change.** Create Core/Windows projects, move
files, extract the 12 seams as interfaces, inject them into `BlankingEngine` and
`OverlayManager` (which also loses its direct `DisplayEnumerator`/`OverlayWindow` references,
[OverlayManager.cs:41](Core/OverlayManager.cs#L41), [:63](Core/OverlayManager.cs#L63)).
**Gate: `MonitorScreenSaver.exe --selftest` still passes all 103 checks on Windows, and the
settings JSON format is byte-compatible.**

**Phase 2 — macOS platform services.** Implement seams 1-7, 9, 10, 12 via objc/CoreFoundation
P/Invoke. **Gate: a headless console harness prints live status (idle seconds, holder list,
displays, lock events) that matches `pmset -g assertions` / `ioreg` side by side.**

**Phase 3 — overlays.** Seam 8. **Gate: manual matrix — {black, dim, video} × {shared,
per-display} × {hotplug, sleep/wake, lock/unlock, fullscreen Space} on a multi-monitor setup;
wake-on-input under 500 ms.**

**Phase 4 — menu bar.** Seam 11, `LSUIElement = true` (menu-bar-only app, no Dock icon —
the mac equivalent of a tray app). **Gate: menu structure matches the Windows menu item-for-item;
holder click-to-blacklist round-trips through settings.**

**Phase 5 — settings window.** Port `ConfigWindow.xaml` + `Theme.xaml` + codebehind to
Avalonia. Same layout, same dark tokens; the elevation banner/chip section renders as
not-applicable on macOS. Font note: no Segoe UI on macOS — either bundle a font or let the
tokens fall back to SF Pro; decide during the port. **Gate: side-by-side screenshot review
against the Windows window.**

**Phase 6 — diagnostics, packaging, docs.** Mac `--selftest` suite and `--watch`;
`tools/publish-macos.sh` producing a `.app` bundle (Info.plist with `LSUIElement`,
`osx-arm64` + optionally `osx-x64`); `.icns` from the existing art (port of
[tools/make-icon.ps1](tools/make-icon.ps1) + a template-image variant for the menu bar);
codesign + notarize; README/TECHNICAL updates. **Gate: notarized build launches clean on a
machine that never saw the dev environment.**

---

## Risks and open questions

- **Code signing / notarization needs an Apple Developer ID** ($99/yr). Without it, users
  fight Gatekeeper (right-click → Open). Decide before the first release; everything else can
  proceed unsigned for development.
- **Liquid Glass / Tahoe UI drift.** The settings window is a custom dark window on both
  platforms, so it sidesteps most of it, but the menu bar icon must be a template image and
  the native menu will look like a Tahoe menu, not like [DarkMenu.cs](UI/DarkMenu.cs). That's
  the platform-correct outcome; calling it out so "identical UI" is agreed to flex exactly here.
- **Audio activity fidelity** (seam 6) — the v1 signal is coarser than WASAPI peaks. If the
  spike shows too many false positives, the process-tap route costs one TCC prompt.
- **Focus-change granularity** (seam 2) — app-level only in v1. If window-level matters in
  practice, it's an opt-in Accessibility permission away.
- **Lock/unlock notification names are unofficial** (seam 9). Stable for a decade+, but they
  are not API contract; the selftest should assert they still fire.
- **Video container gap** (WMV/AVI/MKV/WebM) is the only user-visible feature regression.
  Document it in the mac README section; revisit ffmpeg later only if users actually ask.
- **Minimum macOS version:** nothing here needs Tahoe. `SMAppService` sets the floor at
  macOS 13; the audio process-tap option (if chosen) raises the audio *feature* to 14.2+.
  Targeting "macOS 13+, tested on 26" costs nothing extra.

---

## Appendix — verified on this machine

Run on 2026-08-14, macOS 26.6 (25G72), non-admin shell (uid 501), SDK =
`/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk`:

| Claim | Evidence |
|---|---|
| Per-process display-sleep holders readable without elevation | `pmset -g assertions` output listed `pid 402(WindowServer): … UserIsActive named: "…Razer Viper V3 HyperSpeed…"` etc., unelevated |
| `IOPMCopyAssertionsByProcess` exists, since 10.7 | SDK `IOKit/pwr_mgt/IOPMLib.h` |
| `kIOPMAssertionTypePreventUserIdleDisplaySleep` exists | SDK `IOPMLib.h:1007` |
| Idle-time API exists | `CGEventSourceSecondsSinceLastEventType` in SDK `CGEventSource.h:141`; `kCGAnyInputEventType` in `CGEventTypes.h:491` |
| Idle time also in IORegistry | `ioreg -c IOHIDSystem` → `"HIDIdleTime" = 167108625`, unelevated |
| `NSWorkspaceDidActivateApplicationNotification` (10.6+) | SDK `NSWorkspace.h:294` |
| `NSScreen.localizedName` (10.15+) | SDK `NSScreen.h:57` |
| `CGDisplayCreateUUIDFromDisplayID` (10.4+) | SDK `ColorSyncDevice.h:233` |
| `NSScreenSaverWindowLevel` = CG level 1000 | SDK `NSWindow.h:201`, `CGWindowLevel.h:79` |
| `AVPlayer.preventsDisplaySleepDuringVideoPlayback` | SDK `AVPlayer.h:873` |
| No .NET SDK installed here yet | `dotnet --list-sdks` returned nothing |
