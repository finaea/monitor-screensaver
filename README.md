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

Grab `MonitorScreenSaver.exe` from [Releases](../../releases) and run it. Single file, no installer,
nothing to unpack — it's self-contained, so you don't need .NET installed.

Or build it yourself:

```powershell
git clone <this repo>
cd MonitorScreenSaver
.\tools\publish.ps1        # produces .\publish\MonitorScreenSaver.exe
```

Needs the .NET 9 SDK to build. Nothing to install to run.

On **macOS** there is no release build yet, so build it yourself:

```bash
git clone <this repo>
cd MonitorScreenSaver
tools/bundle-macos.sh              # produces ./publish/MonitorScreenSaver.app
open publish/MonitorScreenSaver.app
```

Pass `osx-x64` for an Intel build (`tools/bundle-macos.sh osx-x64`). The bundle is ad-hoc
signed, which is fine on the machine that built it — on any other Mac Gatekeeper will refuse
the first launch, so right-click → **Open** once. See [macOS](#macos) below.

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
| **Start with Windows** | Registry `Run` key. Optionally **Start elevated** instead, which registers a logon task so you get admin from boot with no UAC prompt. |

Blanking clears the instant you touch the keyboard or mouse, or switch windows.

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
| **The cursor** | Hidden while the screens are blanked, because a lit arrow parked on a blanked OLED is the exact thing this app exists to prevent. That needs a private API — if a future macOS breaks it, blanking still works and the cursor just stays visible. |

Set macOS's own display-sleep timer **longer** than the app's idle timeout
(`pmset -g custom`, or System Settings → Lock Screen), for the same reason as on Windows:
whichever timer is shorter wins, and if macOS powers the panel off first the app never gets a
chance.

Two things macOS does *not* let any app cover: the **login/lock screen** (it belongs to
`loginwindow`), and anything drawn above the screensaver window level by another app — a
desktop-pet or overlay utility can float above the blanking overlay.

Diagnostics live on the binary inside the bundle:

```bash
publish/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver selftest report.txt
publish/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver watch
```

`selftest` runs ~65 checks against your actual machine — displays, overlay placement against
the window server, power-assertion detection and attribution, the settings window's rendering
stack, the menu bar item, the cursor path — and exits 0 when they all pass. More displays mean
more checks, since several sections run per display. `watch` logs every
power, display-topology and lock transition with a heartbeat of everything the engine reads.

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

You can blacklist a selected process/app to prevent it to hold the display:

- **Tray menu** — click a holder in the list to blacklist it.
- **Settings window** — every holder row has a **Blacklist** button.

A blacklisted app still shows in the holder list, just grayed out and tagged `blacklisted`,
and a **Blacklisted** section appears underneath — click an entry there (or **Unblacklist**
in the settings window) to undo it at any time.


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
for the tray icon. If you want something that idles at 5 MB, this isn't it. Full breakdown in
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
MonitorScreenSaver.exe --selftest report.txt          # Windows, 103 checks
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