<div align="center">

<img src="src/MonitorScreenSaver.Windows/Assets/icon.png" alt="MonitorScreenSaver" width="128">

# MonitorScreenSaver

**Play a screensaver video or display black screen on OLED monitor without turning it to sleep.**

Windows 10/11 · macOS 13+ · sits in the tray / menu bar

</div>

---

This app is built to allow OLED monitors to display black screen or screensaver video when the user is not actively using it without getting the monitor to sleep (by windows). This app allows idle monitors to display in **true black** by emitting nothing with 0 rgb values (black pixels). Windows' only support the feature to *power the display off* — and
a powered-off monitor does not come back instantly. On DisplayPort the link drops entirely,
so Windows sees an unplug: it re-detects your displays, re-trains the link, and shuffles every
window around while you sit there waiting. Microsoft's own name for that is
[Rapid Hot Plug Detect](https://devblogs.microsoft.com/directx/avoid-unexpected-app-rearrangement/). This app intends to address few pain points and make few quality of life changes.

- **Stops OLED from burning in** — Dim/Motion display reduces burn-in effect.
- **Instantly bring the display up** — Less than 500ms to get the display back up 
- **Only the monitors you choose.** — Select and configure each monitor individually (true black/dim/video)
- **It follows Windows' own rules** — Plenty of configurations to decide what's considered an activity.
- **Check what's holding the display** — Provides a way to check what software is holding the display, and blacklist the ones to prevent them from holding the display.


<div align="center">
<img src="config.png" alt="MonitorScreenSaver settings window" width="620">
</div>

---

## Install

### Windows

Grab `MonitorScreenSaver.exe` from [Releases](../../releases) and run it. Single file, no installer,
nothing to unpack — it's self-contained, so you don't need .NET installed.

Or build it yourself:

```powershell
git clone <this repo>
cd MonitorScreenSaver
.\tools\publish.ps1        # produces .\publish\MonitorScreenSaver.exe
```

Needs the .NET 9 SDK to build. Nothing to install to run.

### macOS

Grab the disk image for your Mac from [Releases](../../releases) — `…-macos-arm64.dmg` for
Apple silicon, `…-macos-x64.dmg` for Intel. There is no universal build, because `lipo` cannot
merge two single-file .NET executables. If you're not sure which you have,  → *About This Mac*:
"Apple M*x*" is arm64, "Intel" is x64.

1. **Double-click the `.dmg`.** A window opens with the app on the left and an **Applications**
   shortcut on the right.
2. **Drag MonitorScreenSaver onto Applications.** Do it this way, in the Finder — don't run it
   from inside the disk image or from Downloads. *Start at login* records the bundle's absolute
   path, and an app outside `/Applications` is liable to App Translocation, where macOS runs it
   from a randomised temporary mount that won't exist next boot.
3. **Eject the disk image** — ⌘E, or drag it out of the Finder sidebar.
4. **Let the first launch fail.** The build is ad-hoc signed rather than notarized, so Gatekeeper
   refuses it: *"Apple could not verify… it may contain malware"*. That's expected. Since macOS
   Sequoia, right-click → *Open* no longer gets around it. Instead, after the refusal open
   **System Settings → Privacy & Security**, scroll to **Security** near the bottom, click
   **Open Anyway** next to the MonitorScreenSaver line, authenticate, then launch it again and
   confirm **Open**. You only do this once.

   Or skip the whole dance with one command:

   ```bash
   xattr -dr com.apple.quarantine /Applications/MonitorScreenSaver.app
   ```
5. **Look in the menu bar, not the Dock.** It's a menu-bar app (`LSUIElement`), so there is no
   Dock icon and no window on launch — find the small monitor glyph at the top right and click
   it. *Settings…* opens the window from the screenshot above.

Doing this properly — so none of step 4 is needed — takes a $99/year Apple Developer ID that
this project doesn't have. See [macOS](#macos) below.

Or build it yourself:

```bash
git clone <this repo>
cd MonitorScreenSaver
tools/bundle-macos.sh              # produces ./publish/MonitorScreenSaver.app
tools/make-dmg.sh                  # optional: wraps it in a release .dmg
open publish/MonitorScreenSaver.app
```

Pass `osx-x64` for an Intel build (`tools/bundle-macos.sh osx-x64`). A self-built bundle is
ad-hoc signed too, but Gatekeeper doesn't quarantine what never came off the network, so it
launches without step 4 on the machine that built it.

---

## Using it

It lives in the tray. Double-click the tray icon (or right-click → Settings) for the window
in the screenshot above.

| | |
|---|---|
| **Displays to blank** | Toggle the monitors you want covered. Everything else is left alone. Your picks are stored per physical monitor, so unplugging and replugging doesn't scramble them. |
| **Overlay** | **True black** — fully opaque, pixels emit nothing, burn-in stops. **Dim** — partially see-through at a percentage you pick, so the screen stays readable. Dim only *slows* burn-in; anything under 100% is still emitting light. **Video** — a muted looping video as a screensaver instead of black (see below). |
| **Per-display config** | One shared look for every display, or flip on **Configure each display individually** and give each monitor its own mode — true black on the OLED, a video on the side panel, dim on the third. |
| **Idle timeout** | How long before the picked monitors go black. 1 / 3 / 5 / 10 / 30 min, or type your own. |
| **Status** | Live: whether you're counted as awake, seconds until blanking, and which power requests are currently in force. |
| **Blank now / Pause** | Blank immediately, or switch the whole thing off without quitting. |
| **Blank now shortcut** | **Ctrl+Alt+Shift+B** by default, system-wide: blanks from wherever you are. Change it in *Settings → Blank now shortcut* (click the button, press the combination) or clear it there. |
| **Start with Windows** | Registry `Run` key. Optionally **Start elevated** instead, which registers a logon task so you get admin from boot with no UAC prompt. |

Blanking clears the instant you touch the keyboard or mouse, or switch windows — except right
after you asked for it, since the keystroke or click that asked is itself input. The shortcut
stays blanked while you hold it down; the next thing you press after letting go wakes the
screens.

The app refuses a shortcut it can see is taken, and the settings window says why: one modifier,
Command/Shift-only, anything using the Windows key (Windows reserves that for itself), a
combination near every app already uses like Ctrl+Shift+T, or one another program has already
registered. What nothing can check is what a combination means *inside* another app, because a
system-wide shortcut takes the keystroke before the app in front sees it — which is why the
default carries three modifiers.

> **Note:** set Windows' own *Turn off my screen after* (Settings → System → Power) to
> something **longer than MonitorScreenSaver's idle timeout**, otherwise Windows powers your monitors
> off first and the app never gets a chance. 30 minutes is a good backstop — prefer that over
> `Never`, as an insurance in case this app fails.

---

## macOS

Same app, same engine, same settings window — it lives in the **menu bar** instead of the
notification area. Everything in *Using it* above applies; this section is only the
differences.

**Better on macOS:** the whole administrator story disappears. Seeing which app is holding
your display awake, and blacklisting it, needs admin rights on Windows because
`powercfg /requests` is admin-only. macOS reports the same thing through
`IOPMCopyAssertionsByProcess`, which works as a normal user — so there is no elevation
banner, no "Restart elevated", no logon task, and the holder list simply always works.

| | |
|---|---|
| **Menu bar, not tray** | The menu is a real `NSMenu`, so it looks like a macOS menu rather than the custom-drawn Windows one. Same items in the same order, including the live countdown and the inline holder list. Its icon is a monitor with `SS` on the screen, drawn as a template image, so it follows light/dark and Reduce Transparency like every other menu bar extra. |
| **Start at login** | Registered with launchd through `SMAppService`, and visible to you in System Settings → General → Login Items. Only works from inside the `.app` bundle, so move it somewhere permanent (e.g. `/Applications`) before switching it on — it registers the path it was launched from. |
| **Video formats** | Plays what AVFoundation decodes: MP4/M4V/MOV/TS. **WMV, AVI, MKV and WebM do not work** — that is the one feature the Windows build has and this one doesn't. |
| **Fonts** | No Segoe UI or Cascadia Mono on macOS, so the settings window uses San Francisco and SF Mono. Same sizes and layout, slightly different letterforms. |
| **The Dock** | Opening the settings window puts the app in the Dock, which is what lets the window come to the front and take keystrokes; closing it takes the app back out, still running in the menu bar. Minimising leaves a Dock tile like any other window — click it to bring the window back. |
| **Blank now shortcut** | Same feature, written **⌃⌥⇧B** — the Windows default `Ctrl+Alt+Shift+B` is the same keystroke, and the setting is one shared value. The difference is what the OS admits to: see below. |
| **The cursor** | Hidden while the screens are blanked, because a lit arrow parked on a blanked OLED is the exact thing this app exists to prevent. That needs a private API — if a future macOS breaks it, blanking still works and the cursor just stays visible. |

Set macOS's own display-sleep timer **longer** than the app's idle timeout
(`pmset -g custom`, or System Settings → Lock Screen), for the same reason as on Windows:
whichever timer is shorter wins, and if macOS powers the panel off first the app never gets a
chance.

Two things macOS does *not* let any app cover: the **login/lock screen** (it belongs to
`loginwindow`), and anything drawn above the screensaver window level by another app — a
desktop-pet or overlay utility can float above the blanking overlay.

About that shortcut: Windows tells you when another program already holds a combination, and
macOS does not — it hands it out and never mentions it. So on macOS the app leans on what it
can check up front (one modifier, Command/Shift-only, a macOS-reserved combination, or one you
have assigned in System Settings → Keyboard → Shortcuts) and the settings window says which. If
a combination turns out to be taken anyway, the symptom is that nothing happens when you press
it — pick another one, and blank from the menu bar meanwhile.

Diagnostics live on the binary inside the bundle:

```bash
publish/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver selftest report.txt
publish/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver watch
publish/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver hotkey
```

`selftest` runs ~65 checks against your actual machine — displays, overlay placement against
the window server, power-assertion detection and attribution, the settings window's rendering
stack, the menu bar item, the cursor path — and exits 0 when they all pass. More displays mean
more checks, since several sections run per display. `watch` logs every
power, display-topology and lock transition with a heartbeat of everything the engine reads.
`hotkey` holds the shortcut and prints every press without blanking anything, which is how to
tell "not registered" apart from "registered, but something else is eating the keystroke".

### Packaging a release

`tools/make-dmg.sh` turns `publish/MonitorScreenSaver.app` into a disk image with the app,
an `/Applications` symlink and a background poster, one file per architecture.

A zip would be smaller (38 MB against 43 MB) and build in two seconds rather than twenty,
and neither format changes anything about Gatekeeper — quarantine propagates through both.
The disk image is chosen for where the app ends up: the `/Applications` symlink makes the
Finder drag the obvious move, which both keeps *Start at login* pointing at a path that
still exists and clears App Translocation.

Everything cosmetic about that window — its size, the icon positions, the background — is
stored in a `.DS_Store` that only Finder can write, so the script mounts a read-write image
and drives Finder over AppleScript to produce it. The background itself comes from
`tools/make-mac-icons.swift`; the two hold one shared layout, so icon coordinates have to
move together.

The poster's colours are not a free choice. Finder draws icon labels with no shadow or
plate behind them and gives `.DS_Store` no say in their colour, so the strip they land on
is held at the one luminance that clears 4.5:1 against black *and* white text. On macOS
26.6 the ink measured dark in both appearances, but Apple has never documented that, so the
background does not bet on it.

---

## What's holding the display awake

The app includes optional choices to set what counts as activity:

| Option | What it does | Default |
|---|---|---|
| **Keyboard and mouse input** | Any keypress or mouse movement resets the idle timer. The baseline Windows signal. | Always on — can't be turned off |
| **Window focus changes** | Switching windows counts as activity. Windows counts it too, but its input timer doesn't report it, so it's tracked separately. | On |
| **Apps requesting the display stay on** | Honour any app asking Windows to keep the display on (video players, Parsec, Steam, OBS — see below). This is what makes blanking match Windows instead of guessing. | On |
| **Never blank during exclusive fullscreen** | Don't blank while an exclusive-fullscreen app is up. Windows doesn't do this itself — it's here because an overlay on top of an exclusive-fullscreen game can misbehave. | On |
| **Never blank while audio is playing** | Anything audible on any output device counts as activity; a muted stream doesn't. | Off — opt-in, so screens stay dark while music plays |

The first three mirror what Windows itself considers activity; the last two go beyond it and
are labelled that way in the settings window.

Any app can ask Windows to keep the screen on via `ES_DISPLAY_REQUIRED` flag, and Windows honours it. MonitorScreenSaver reads the
same request and honours it too, so it won't blank over your movie. Broadly, the things that
do this are:

- **Video playback** — players and browsers, while something is actually playing
- **Remote desktop and game streaming** — e.g. Parsec
- **Games and launchers** — e.g. Steam
- **Calls, screen sharing and recording** — e.g. Zoom, OBS
- **Background system work** — some services and drivers, which is why the list occasionally
  shows something you've never heard of

This is just a rough guide, not a rule — whether a given app does it is entirely up to the app requesting to keep the display up.

### Checking what's holding it right now

Two places, both live:

- **Settings window → HOLDING THE DISPLAY AWAKE** — a card listing each holder with a
  `PROCESS` / `SERVICE` / `DRIVER` tag and its stated reason. **Refresh** re-queries.
- **Tray menu → Holding display awake** — same list, showing on the menu
  after right clicking the tray icon

**Seeing the app names requires administrator rights.** That's a Windows restriction, not a
choice here — the data comes from `powercfg /requests`, which is admin-only. What changes
without admin:

| | Standard user | Administrator |
|---|---|---|
| Blanking behaves correctly | ✅ | ✅ |
| "Is *something* holding the display awake?" | ✅ yes/no | ✅ |
| **Which app** is holding it | ❌ | ✅ named list |
| **Blacklisting** a holder | ❌ kept but not applied | ✅ |

The yes/no is all the blanking logic actually needs, so running as a standard user costs you
nothing functionally — only the names. The chip next to the title reads `standard` or
`elevated` so you can tell at a glance which mode you're in.

**To get the names**, either:

1. Click **Restart elevated** in the banner on that card — one UAC prompt, the app relaunches
   with admin and reopens the settings window. Good for a one-off "what the hell is keeping my
   screen on right now".
2. Or turn on **Start elevated (scheduled task)** in the startup section — registers a logon
   task that runs with admin from boot, so you get names permanently with **no UAC prompt**
   every time. Good if you want this always on.

You can also just check it yourself in an admin terminal, without the app:

```powershell
powercfg /requests
```

### Blacklisting a holder

You can blacklist a selected process/app to prevent it to hold the display. It's done in the
**settings window**: every holder row has a **Blacklist** button, and a **Blacklisted**
section underneath lists what you've blacklisted with an **Unblacklist** button each — so
you can also remove an entry for something that isn't running right now.

The **tray menu** only reports. A blacklisted app still shows in its holder list, greyed out
and tagged `blacklisted`, but nothing in that list is clickable: it used to be, and the
result was the same process appearing twice — once greyed as live status, once bright
underneath as a remove button — which just read as a duplicate.


One catch: matching a request to an app name needs the same admin-only `powercfg /requests`
data as the list itself, so **the blacklist only takes effect while running elevated**.
Without admin your entries are kept (and editable) but can't be applied.

### Video screensaver

Any managed display can play a **muted, looping video** instead of going black. Pick a file
with Browse, then choose how it fills the screen:

- **Fit** — keeps the aspect ratio, letterboxes with black bars
- **Fill** — keeps the aspect ratio, crops to cover the whole screen
- **Stretch** — ignores the aspect ratio and distorts to fit

Any resolution and aspect ratio works — the video is scaled to the monitor, portrait panels
included. Support common media format such as MP4, M4V, MOV, WMV, AVI, HEVC, WEBM, MKV, TS etc. More on the Store codec extensions in [Microsoft's codec page](https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456).

Two caveats:

- **A playing video protects less than true black.** It still helps — motion spreads the
  wear instead of parking your taskbar on the same pixels — but pixels stay lit and the GPU
  keeps decoding. How much it helps depends on the clip: more motion (pixels changing
  colour rather than holding one) and lower overall brightness both mean less burn-in
  accrual. A dark, moving clip is a decent compromise; a bright static-ish loop is barely
  better than the desktop.
- While a video screensaver is on screen, apps asking Windows to "keep the display on" are
  ignored — the video's own playback can file exactly that request, and honouring it would
  wake the screens we just covered. Touching the mouse or keyboard wakes them as always.

---

## Known limits

### The lock screen

**MonitorScreenSaver can't touch it.** The Windows lock screen runs on a separate desktop that no
normal app can draw over. Whatever your screens do there is pure Windows, and the only lever
is a power setting — a *different* one from "turn off display after", defaulting to **60
seconds** and hidden from the Power Options UI. More in [TECHNICAL.md](TECHNICAL.md#the-lock-screen-measured).

### Memory

Task Manager will show ~140 MB, or ~240 MB with the settings window open. That number is
misleading — **only about 16 MB of it is actually private to MonitorScreenSaver.** Measured on a
running instance with the window closed:

| | |
|---|---|
| Working set (what Task Manager shows) | 140 MB |
| — private to this process | **16 MB** |
| — shared, file-backed DLLs | 124 MB |

That 124 MB is the .NET runtime, WPF, and your GPU vendor's Direct3D drivers — mapped from
disk and shared with every other process already using them, so it doesn't disappear if you
close MonitorScreenSaver. WPF draws through Direct3D, so opening any window pulls the graphics stack
in; on the machine this was measured on that's 20 driver modules and 329 MB of *address
space*, which is reservation, not memory consumed.

It's still a WPF app rather than a tiny native tray utility, and it does load WinForms purely
for the tray icon. Full breakdown in
[TECHNICAL.md](TECHNICAL.md#footprint).

---

## Something went wrong

Check the error log first — a window that fails to build looks exactly like "nothing happened"
otherwise:

| | |
|---|---|
| Windows | `%APPDATA%\MonitorScreenSaver\error.log` |
| macOS | `~/Library/Application Support/MonitorScreenSaver/error.log` |

There's also a built-in diagnostic that runs against your actual machine — display
enumeration, overlay placement, power-request detection, the lot:

```powershell
MonitorScreenSaver.exe --selftest report.txt          # Windows, ~136 checks
```

```bash
MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver selftest report.txt   # macOS, ~65 checks
```

Exit code 0 means everything passed. If you're filing an issue, attach that file.

---

## Technical notes

How it decides what counts as activity, why the overlay is built the way it is, what was
actually measured on the lock screen, and the traps found along the way:
**[TECHNICAL.md](TECHNICAL.md)**.