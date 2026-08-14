# MonitorScreenSaver — technical notes

How the thing actually works, what was measured, and the traps found along the way.
For install and day-to-day use, see the [README](README.md).

---

## What counts as activity

The whole point is to match Windows' policy rather than guess at it. Windows keeps a display
on for two independent reasons, and this app reimplements both.

### Category 1 — the idle timer got reset

Per the [SetThreadExecutionState docs](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate),
the system *"automatically detects activities such as local keyboard or mouse input, server
activity, and changing window focus."*

| Signal | How | Default |
|---|---|---|
| Keyboard / mouse | `GetLastInputInfo` | always on |
| Window focus changes | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` — `GetLastInputInfo` does **not** report these | on |
| "Server activity" | not detectable from user mode | — |

### Category 2 — an app is holding the display awake

Apps like Parsec, Steam, OBS, Zoom and video players file a DISPLAY power request via
`SetThreadExecutionState(ES_DISPLAY_REQUIRED)` or `PowerSetRequest(PowerRequestDisplayRequired)`.
`ES_DISPLAY_REQUIRED` *"forces the display to be on by resetting the display idle timer."*

This app reads the aggregate state with
[`CallNtPowerInformation(SystemExecutionState)`](https://learn.microsoft.com/en-us/windows/win32/api/powerbase/nf-powerbase-callntpowerinformation),
which returns any combination of `ES_SYSTEM_REQUIRED` / `ES_DISPLAY_REQUIRED` / `ES_USER_PRESENT`.
**No elevation required**, and it picks up requests from both the legacy and modern APIs —
verified by the self-test on every run.

Because a held request *continuously resets* the timer, the engine models it the same way: it
keeps pushing the activity baseline forward while the request is held, so releasing it starts a
fresh full timeout instead of blanking instantly.

---

## Overlay rendering

| Mode | What it does | Rendering |
|---|---|---|
| **True black** (default) | Fully opaque black. OLED pixels emit nothing, so burn-in accrual stops outright. | Ordinary opaque window, hardware rendered |
| **Dim** | Partially opaque black at a chosen percentage; the screen stays readable underneath. | `AllowsTransparency` window, software-rendered per-pixel alpha |
| **Video** | A muted looping video. Weaker burn-in protection than black — motion spreads wear, but pixels stay lit. | Opaque window hosting a `MediaElement` (Media Foundation, DXVA) |

Implementation notes worth keeping:

- **`SetWindowLongPtr(WS_EX_LAYERED)` does not stick on a WPF window.** `HwndTarget`
  rewrites the extended style while realising the window, so the uniform-alpha route
  (`SetLayeredWindowAttributes`, which would have stayed DWM-composited) is unavailable.
  WPF's own `AllowsTransparency` is the only supported path.
- **`AllowsTransparency` is a create-time property**, and it costs a software-rendered
  surface — roughly 29 MB on a 5120x1440 panel. So true black and video keep an ordinary
  opaque window and pay nothing, and crossing into or out of dim recreates the overlay
  rather than trying to mutate it. `OverlayWindow.TryApply` returns false to signal that;
  the manager rebuilds exactly the windows that refused, not the whole set.
- **In-place vs rebuild:** dim level and video stretch change in place; any mode change,
  a dim change crossing the 100% (opaque) boundary, or a different video file rebuilds
  that one window. A fresh `MediaElement` per file beats reusing one — it releases the
  old decoder topology outright.

### Per-display configuration

`MonitorConfig` (mode, dim %, video path, stretch) is resolved per display:
`AppSettings.ConfigFor(stableId)` returns the display's override when **per-display
config** is on and one exists, otherwise the shared root config. Overrides are seeded
from the shared config on first touch (`OverrideFor`), keyed by the same stable hardware
id used for the managed-display list, so they survive replug and renumbering. The root
`Mode`/`DimPercent` JSON properties are unchanged from earlier versions, so pre-existing
settings files load as-is.

### Video mode details

- `MediaElement` with `LoadedBehavior=Manual`: play starts on overlay show, `Stop()` on
  hide (a hidden window would otherwise keep decoding), rewind-and-play on `MediaEnded`
  for a seamless-enough loop.
- Always muted (`IsMuted` + `Volume=0`). A screensaver must not make noise — and the
  audio-activity option reads post-mute peaks, so the video can never hold the screens
  awake through the audio path either.
- Any container/codec Media Foundation can decode. MP4/H.264/WMV are always there;
  HEVC and some MKV/WebM depend on installed codecs. `MediaFailed` logs to error.log and
  degrades that overlay to plain black — same for a missing file, which on OLED is the
  correct fallback anyway.
- Aspect handling is `Stretch.Uniform` (Fit), `UniformToFill` (Fill) or `Fill` (Stretch),
  so any source resolution/ratio maps onto any panel, portrait included.
- **Self-request guard:** media pipelines can file their own `ES_DISPLAY_REQUIRED`
  ("Playing video"). Honouring our own request would unblank the screens we just
  covered, so while blanked with a video overlay visible, display requests are not
  treated as fresh activity (`BlankingEngine.VideoOverlayVisible`). Requests cannot be
  attributed without admin, so this also ignores foreign requests that begin mid-blank —
  accepted: those arrive with input (Parsec connect, incoming call) which wakes as usual.

### Beyond Windows (opt-in, clearly labelled in the UI)

- **Never blank during exclusive fullscreen** — `SHQueryUserNotificationState` returning
  `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE`. Windows does *not* do this;
  it exists because an overlay over an exclusive-fullscreen swapchain is a functional hazard.
- **Never blank while audio is playing** — WASAPI endpoint peak meters
  (`IAudioMeterInformation` on every ACTIVE render endpoint, max instantaneous peak
  > 0.001 counts as audible). Same signal oled_aegis keys on. Endpoint meters read the
  post-mix, post-mute signal, so muted streams — including our own video overlay — never
  count. Meters are cached and re-enumerated every 5 s or on any COM failure. Audio only
  *prevents* blanking, it never unblanks: the activity tick is not pushed while blanked,
  so starting music with dark screens leaves them dark. Off by default — someone
  listening to music typically wants the screens blanked.

---

## Who's holding the display awake

Getting **names** needs administrator rights — `powercfg /requests` is admin-only, and so is
the [native info class behind it](https://github.com/diversenok/Powercfg/blob/master/Readme.md).
Without elevation you still get the correct yes/no (which is all the blanking logic needs),
plus a one-click "Restart elevated" if you want the names.

For unattended use, **Start elevated** registers a logon scheduled task with
`RunLevel=HIGHEST`, so you get elevation from boot with no UAC prompt.

### Restarting elevated

"Restart elevated" replaces the running process, which has two traps, both handled:

- **The single-instance mutex.** The replacement starts while its predecessor still owns
  the mutex, so a naive check makes it exit instantly and leaves nothing running at all.
  The old process now releases the mutex before spawning, and the replacement is passed
  `--relaunch` so it waits up to 15s to claim it instead of giving up. A plain second
  launch still exits immediately, as it should.
- **It looks like nothing happened.** The replacement is a tray app, so without a window
  the whole operation is invisible. A `--relaunch` start therefore opens the settings
  window, and the tray icon is hidden before spawning so two never appear at once.

Declining the UAC prompt (`ERROR_CANCELLED`, 1223) is treated as a normal cancel: the
original process reclaims the mutex, restores its tray icon and carries on.

---

## Resilience

| Event | Handling |
|---|---|
| Resume from sleep | `WM_POWERBROADCAST` / `PBT_APMRESUME*` → reset baseline, rebuild overlays (adapters often renumber across suspend) |
| Monitor hotplug / resolution change | `WM_DISPLAYCHANGE` → re-enumerate, recreate overlays whose geometry moved |
| Session lock / unlock | `WM_WTSSESSION_CHANGE` → hide on lock, fresh baseline on unlock |
| Topmost z-order loss | 3-second watchdog re-asserts `SetWindowPos(HWND_TOPMOST)` and bounds |
| Restart / logon | Registry `Run` key, or scheduled task when elevated |

Displays are keyed by hardware id (`MONITOR\ACR0C5D\{4d36e96e-…}\0005`), not by
`\\.\DISPLAY1`, so your selection survives replugging and renumbering.

---

## Windows power settings this app depends on

MonitorScreenSaver only gets to act inside the window before Windows' own display timeout fires, so
that timeout has to be longer than the app's idle timeout:

```powershell
powercfg /change monitor-timeout-ac 1800    # 30 min
powercfg /change monitor-timeout-dc 1800
```

**Long-but-finite beats `Never`.** The console-lock timeout below has to stay *smaller* than
this one to keep working, and `Never` may or may not suppress it (see the unresolved note at
the end of the next section). With a 30-minute backstop, MonitorScreenSaver handles the everyday
5-minute case and Windows only takes over after a full half hour of no lock and no input —
rare enough that eating one rearrangement is cheap.

---

## The lock screen (measured)

**MonitorScreenSaver cannot help here at all.** The Windows lock screen runs on the Winlogon
desktop, so no user-mode window can draw over it. Whatever happens on that screen is entirely
Windows' DPMS, and the only lever is a power setting.

That lever is a **separate** setting from "Turn off display after":

| | GUID | Default |
|---|---|---|
| Desktop, logged in | `3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e` — *Turn off display after* | per plan |
| Lock screen | `8EC4B3A5-6868-48c2-BE75-4F3044BE88A7` — *Console lock display off timeout* | **60s**, all plans |

It is **hidden from Power Options** by `Attributes = 1` at
`HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{7516b95f-…}\{8EC4B3A5-…}`.
Set that value to `2` and it appears under Power Options → Display, right below "Turn off
display after". Set it back to `1` to re-hide; the value you chose stays in effect either way.
It also has no `powercfg` alias — you must use the raw GUID:

```powershell
powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO 8EC4B3A5-6868-48c2-BE75-4F3044BE88A7 300
powercfg /setdcvalueindex SCHEME_CURRENT SUB_VIDEO 8EC4B3A5-6868-48c2-BE75-4F3044BE88A7 300
powercfg /setactive SCHEME_CURRENT
```

### What `--watch` actually recorded

Display timeout 300s, console lock at its 60s default:

```
T+225s  SessionLocked
T+281s  UserInactive             <- 56s after lock
T+286s  DISPLAY OFF              <- 61s after lock
T+291s  DISPLAY ON
T+303s  SessionUnlocked
T+303s  DisplayTopologyChanged   <- the window-shuffle trigger
```

Two conclusions:

1. **The 60s lock-screen timeout is real** and fires independently of the 300s desktop timeout.
2. **Every lock-screen blank costs a DisplayPort re-enumeration on unlock.** That is the same
   Rapid HPD mechanism this whole app exists to avoid — you just meet it on the way back from
   the lock screen instead of from the desktop.

### The trade

There is no free option, because Windows owns that screen:

| Console lock value | OLED | Shuffle on unlock |
|---|---|---|
| `0` (Never) | bad — static clock and wallpaper parked on the panel | none |
| `60` (default) | good | after every absence over a minute |
| `180`–`300` | fine — bounded exposure | only after real absences |

Pick roughly the length of a typical short break. 180–300s is the sane middle.

> **Unresolved:** sources disagree on whether setting *Turn off display after* to `Never`
> also disables the console lock timeout — [ghacks](https://www.ghacks.net/2018/06/02/configure-the-lockscreen-display-timeout-on-windows/)
> says it fires "regardless of power settings", [tenforums](https://www.tenforums.com/tutorials/65592-change-lock-screen-display-off-timeout-windows-10-a.html)
> says Never suppresses it. Untested here, and moot for the recommended config: with a
> 30-minute backstop, 60s < 30min, which is the combination measured working above.

---

## macOS

The macOS head is the same engine with a different platform layer, not a rewrite. One
solution, three projects: portable `Core` (the policy engine and settings, behind seam
interfaces), the WPF `Windows` head, and a `Mac` head that talks to the OS through raw
objc/CoreFoundation interop. The engine's decision — categories 1 and 2 — is written once.
Full design, verification log and every trap found on the way live in
[MACOS-PORT-PLAN.md](MACOS-PORT-PLAN.md).

What each seam binds to:

| Seam | Windows | macOS |
|---|---|---|
| Idle | `GetLastInputInfo` | `CGEventSourceSecondsSinceLastEventType` (HID state, no permission) |
| Display held awake | `SystemExecutionState` | `IOPMCopyAssertionsStatus` |
| Who is holding it | `powercfg /requests` (**admin**) | `IOPMCopyAssertionsByProcess` (**no admin, ever**) |
| Displays | GDI + EDID | `CGGetActiveDisplayList` + display UUID + `NSScreen.localizedName` |
| Sleep / hotplug / lock | `SystemEvents` | `IORegisterForSystemPower`, `CGDisplayRegisterReconfigurationCallback`, `com.apple.screenIsLocked` |
| Audio | WASAPI peak meters | `kAudioDevicePropertyDeviceIsRunningSomewhere` (coarser: running, not audible) |
| Overlay | non-activating topmost `Window` | non-activating `NSPanel` at screensaver level |
| Video | MediaElement (Media Foundation) | `AVQueuePlayer` + `AVPlayerLooper` |
| Tray | `NotifyIcon` + custom-drawn menu | `NSStatusItem` + native `NSMenu` |
| Settings window | WPF | Avalonia, same tokens and layout |
| Start at login | `Run` key / logon task | `SMAppService` (launchd) |

Five things that cost real time, kept here so nobody re-derives them:

- **The status item is not this app's window.** On macOS 26 `NSStatusItem` is rendered and
  owned by ControlCenter; the button's own `NSWindow` never gets a window-server device, so
  `CGWindowList` on our own pid shows nothing and the item looks "missing" while working
  perfectly. Verify with `NSStatusItem.isVisible`, never through our window list.
- **The cursor cannot be put below the overlay.** The header puts the cursor at
  `kCGMaximumWindowLevel − 1`, but a window one level above it still renders under the cursor:
  modern WindowServer composites the cursor above all windows. Six approaches were tested; the
  only one that works from a non-activating background app is the private CGS connection
  property `SetsCursorInBackground`, after which `CGDisplayHideCursor` takes effect. Failure is
  caught and degrades to a visible cursor.
- **Two clocks make idle jitter.** `LastInputMs` is "now minus idle" across two independently
  rounding clocks, so consecutive reads wobbled ±1 ms with no input — enough for the engine's
  manual-blank hold to cancel itself instantly. The mac clock absorbs sub-100 ms jumps; real
  input moves whole seconds.
- **`proc_name` is denied for other users' processes**, so holder names fall back to
  `sysctl kinfo_proc.p_comm` — the same source `ps` uses.
- **Avalonia is not WPF about layout.** `ScrollViewer.Padding` is subtracted when content is
  arranged but not when it is measured, and a vertical `StackPanel` arranges an over-desiring
  child at its desired width instead of clamping like WPF. Together those pushed a card past
  the window edge. The inset is a `Margin` now, and `HorizontalScrollBarVisibility` is set
  explicitly because Avalonia defaults it to `Auto` (infinite measure width, nothing wraps)
  where WPF defaults to `Disabled`.

Avalonia is initialised with `SetupWithoutStarting()` rather than a desktop lifetime: AppKit
keeps owning the loop (`[NSApp run]`), and Avalonia's dispatcher rides the same main
CFRunLoop, so one loop pumps the engine timer, the status item and the settings window. It is
also initialised lazily on the first *Settings…*, so a session that never opens settings never
loads a UI framework at all.

---

## Build

```powershell
dotnet build                 # dev build
.\tools\publish.ps1          # single self-contained exe in .\publish
.\tools\make-icon.ps1        # regenerate Assets\MonitorScreenSaver.ico + Assets\icon.png
```

Requires the .NET 9 SDK. The Windows head targets `net9.0-windows`, `win-x64`.

```bash
dotnet build MonitorScreenSaver.sln   # all three projects (works on macOS too)
tools/bundle-macos.sh [osx-x64]       # ./publish/MonitorScreenSaver.app
tools/make-icns.sh                    # regenerate the .icns + menu bar art
```

The mac head targets `net9.0`, `osx-arm64`/`osx-x64`, and its bundle is ad-hoc signed unless
`SIGN_IDENTITY` names a Developer ID (which also switches on the hardened runtime and the
three entitlements CoreCLR needs to JIT under it). One architecture per run: `lipo` cannot
merge two single-file .NET executables, because the payload is appended after the Mach-O image
and gets dropped.

Two things the bundle script has to do that are easy to miss:

- **Copy the native dylibs next to the executable.** A single-file publish does *not* embed
  native libraries, so `libSkiaSharp`/`libHarfBuzzSharp`/`libAvaloniaNative` are separate
  files. Without them the app runs perfectly until someone opens *Settings…*, then throws
  `DllNotFoundException`.
- **Publish into a clean directory.** An incremental publish over an existing one keeps the
  executable and silently drops those loose dylibs.

Two csproj settings that are load-bearing, both commented in place:

- **Do not set `InvariantGlobalization=true`.** It saves ~10 MB but WPF's text stack needs
  the culture data: `MS.Internal.FontCache.MajorLanguages` fails its type initializer on the
  first `TextBlock` measure, which takes down any window containing text. The empty overlay
  windows survive, so it presents as "the settings window won't open".
- **`EnableCompressionInSingleFile` is off.** It shrinks the exe by ~40% but the bundle is
  decompressed into memory at startup, measured at +75 MB of private bytes.

### Icon generation

`tools/make-icon.ps1` composites `hoshinosleep.png` with the amber dim badge and writes an
8-size `.ico` (16 → 256) plus a 256px PNG for the README. Three things it has to get right:

- **Frames are 32bpp uncompressed DIBs, not PNG.** GDI+ cannot decode PNG-compressed `.ico`
  entries and `NotifyIcon` goes through GDI+, so PNG frames would leave the tray blank.
- **Downscaling halves repeatedly before the final resample.** One 446 → 16 bicubic step
  samples too sparsely and the face turns to noise.
- **All resampling happens premultiplied.** Otherwise the transparent-black surround bleeds
  a dark halo into the white sticker outline.

`tools/make-icns.sh` is the mac twin: it takes the same `Assets/icon.png` and writes
`MonitorScreenSaver.icns` plus 18/36 px menu bar art into
`src/MonitorScreenSaver.Mac/Assets`. Two notes:

- **`LSUIElement` suppresses the running Dock icon, not the bundle icon.** Without an `.icns`
  and `CFBundleIconFile`, macOS draws the generic placeholder on the Dock tile of a minimised
  settings window, in the app switcher and in Finder.
- **The status item art is shipped in colour and marked `isTemplate`.** AppKit takes the mask
  from the alpha channel and tints it for the current menu bar, so no separate monochrome
  asset is needed — and the icon then follows light/dark automatically, which a colour image
  would not. The iconset stops at 256 px because the artwork does (447 px master); everything
  macOS draws except Finder at maximum zoom is covered without upscaling.

---

## Self-test

```powershell
MonitorScreenSaver.exe --selftest report.txt
```

Headless — no tray, no windows, no engine. 103 checks covering:

- WPF text layout forced through every font family the UI uses, in both formatting modes,
  plus every Theme.xaml brush and style (this is what catches globalization/font regressions
  that break the settings window while leaving the empty overlay windows working)
- tray icon resource loads through both the WPF and GDI+ decoders (pack:// URIs plus
  single-file publishing is a classic silent failure)
- display enumeration and EDID friendly names
- overlay placement verified against `GetWindowRect` on every monitor, in both true-black
  and dim modes, including that switching between them correctly demands a rebuild
- video-mode overlays: opaque, placed correctly, missing file degrades to black, stretch
  changes in place while a different file or mode demands a rebuild
- the WASAPI audio probe: endpoint enumeration plus a live peak reading through the same
  interop the audio-activity option uses
- power-request detection through **both** the legacy and modern APIs
- idle arithmetic including the 32-bit `GetLastInputInfo` tick wrap
- the `powercfg /requests` parser
- settings round-trip, per-display config resolution, and autostart queries

Exit code 0 = all passed.

> `SystemExecutionState` does not always update synchronously with a set/clear call — a
> same-instant read can still see the old value. The engine polls (250 ms) so this is
> harmless in production, but the self-test polls too, or it flakes.

### On macOS

```bash
MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver selftest report.txt
```

75 checks, same shape, same exit code. Section-for-section parity where the concept exists,
and it takes a **real** display assertion with `IOPMAssertionCreateWithName` to prove
detection, attribution and the blacklist decision end to end — the direct twin of the Windows
`SetThreadExecutionState` check. Three sections have no Windows counterpart, and each exists
because the thing it checks already broke once:

- **The settings window's rendering stack**, realised off-screen: one of every themed control
  templated and laid out. This is what catches a missing native dylib (the app runs until
  someone opens *Settings…*) and a Fluent theme resource overridden with the wrong CLR type,
  which throws only when the control that reads it is first realised. A `Slider` that is never
  shown is a `Slider` that is never tested, and Dim mode is not reachable without changing
  settings.
- **The status item**, through `NSStatusItem.isVisible` — never through our own window list,
  for the ControlCenter reason above.
- **The private cursor property**, so a macOS release that revokes it shows up here rather
  than as a bright arrow on someone's blanked OLED.

> Registering a window with the window server is asynchronous, and the first panel a process
> creates is the slowest. The overlay-placement check polls for it; pumping a fixed slice and
> hoping passed on the built-in display and failed on both externals.

## Watch mode

```powershell
MonitorScreenSaver.exe --watch watch.log
```

Logs every display-power and session transition Windows reports, with timestamps, plus a
10-second heartbeat carrying the execution state. Subscribes to both
`GUID_SESSION_DISPLAY_STATUS` (documented for interactive apps) and
`GUID_CONSOLE_DISPLAY_STATE` (kernel-level) so the two can be cross-checked — useful because
it is not obvious which still gets delivered while the session is locked. In practice both do.

This is how the lock-screen timings above were measured. Start it, lock the machine, wait,
unlock, then read the log. Runs until killed; no tray, no engine, no overlays.

```bash
MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver watch [path]
```

The mac twin logs sleep/wake, display-topology and lock/unlock transitions with the same
timestamps, opening with the system's own power settings (`pmset -g custom`) and the display
list, then a 10-second heartbeat carrying idle time, the assertion flags, audio, fullscreen,
the frontmost app and the current display holders. The `displaysleep` value in that header is
the number that matters: if it is shorter than the app's idle timeout, macOS powers the panel
off before we ever blank it. Default log path is
`~/Library/Application Support/MonitorScreenSaver/watch.log`.

---

## Footprint

| Build | Working set | Commit charge |
|---|---|---|
| Release, single-file **compressed** | ~331 MB | ~233 MB |
| Release, single-file (shipped config) | **~241 MB** | ~204 MB |

Measured with the settings window open, which is the worst case. An earlier revision recorded
~198 MB, but that number is not comparable — it was taken while the settings window was
silently failing to render (see `InvariantGlobalization` above), so nothing was actually
being laid out. Workstation non-concurrent GC accounts for most of the saving over default.

### Working set is the wrong number to quote

Splitting a live process (settings window closed, via `\Process(MonitorScreenSaver)\Working Set -
Private`) shows how little of it is really this app's:

| | |
|---|---|
| Working set | 140.1 MB |
| — private resident | **15.8 MB** |
| — shared, file-backed | 124.2 MB |
| Commit charge (private bytes) | 215.0 MB — of which ~199 MB is committed but never resident |

The shared 124 MB is CoreCLR, WPF, shell libraries and the GPU driver stack, mapped from disk
and charged to every process that maps them. Across 112 loaded modules totalling 434.2 MB of
address space, **20 are graphics modules accounting for 329.4 MB** — WPF composites through
Direct3D, so any window pulls in the vendor D3D runtime and shader compilers:

| Module | Mapped | What it is |
|---|---|---|
| `nvgpucomp64.dll` | 94.1 MB | NVIDIA shader compiler |
| `igd12dxva64.dll` | 83.1 MB | Intel D3D12 / video acceleration |
| `igc64.dll` | 68.9 MB | Intel graphics shader compiler |
| `nvd3dumx.dll` | 41.4 MB | NVIDIA D3D user-mode driver |

Both vendors are mapped on the test machine; it has displays across the integrated and
discrete GPUs. None of this is memory MonitorScreenSaver allocates, and none of it is avoidable while
the UI is WPF.

Single-file publish also extracts 8.0 MB of native libraries (5 files) to
`%TEMP%\.net\MonitorScreenSaver\<hash>\` on first run. Disk, not memory.

The floor here is WPF + WinForms both being loaded; WinForms is present solely for the tray
`NotifyIcon`. Replacing it with a raw `Shell_NotifyIcon` P/Invoke and a WPF `ContextMenu`
would cut further and let the tray menu use the same dark theme as the settings window.

---

## Diagnostics

Unhandled exceptions, swallowed exceptions and failed callbacks all append to
`%APPDATA%\MonitorScreenSaver\error.log`. Check there first — a window that fails to build looks
identical to "nothing happened" otherwise.

Two failure modes are worth knowing about:

- **Exceptions must never escape a P/Invoke callback.** Window procedures, `SetWinEventHook`
  callbacks and `EnumDisplayMonitors` callbacks are invoked by native code; an exception
  crossing back raises `STATUS_FATAL_USER_CALLBACK_EXCEPTION` (`0xC000041D`) and kills the
  process instantly, without `DispatcherUnhandledException` ever seeing it. All three are
  wrapped in `CrashLog.GuardCallback`.
- **Catching without logging hides real bugs.** The settings window failing to open was
  invisible for several revisions because the dispatcher handler set `Handled = true` and
  said nothing.

---

## Layout

One solution, three projects. Everything in `Core` is platform-free and shared; each head
binds the seams its OS provides.

```
src/MonitorScreenSaver.Core/            net9.0 — no OS calls at all
  Platform.cs                 the seam interfaces + EnginePlatform bundle
  BlankingEngine.cs           the policy — category 1 + category 2 decision
  OverlayManager.cs           keeps overlays in sync with topology + per-display config
  AppSettings.cs              settings + MonitorConfig, per-display resolution
  PowerModels.cs              requester/execution-state/snapshot models
  DisplayModels.cs            display target + pixel rect
  SystemEvents.cs             the event kinds a head can raise
  CrashLog.cs                 error.log + callback guards

src/MonitorScreenSaver.Windows/         net9.0-windows — the WPF head
  App.xaml.cs                 tray icon, menu, wiring, lifecycle
  Platform/WindowsPlatform.cs the seam implementations
  Platform/PowerRequests.cs   SystemExecutionState + powercfg /requests parser
  Platform/OverlayWindow.cs   one non-activating topmost window: black, dim or video
  Platform/DisplayEnumerator.cs  monitor enumeration + EDID friendly names
  Platform/SystemEventSink.cs sleep / hotplug / lock notifications
  Platform/AudioActivity.cs   WASAPI endpoint peak meters
  Platform/SelfTest.cs        headless diagnostics (--selftest)
  Platform/WatchMode.cs       display-power transition logger (--watch)
  UI/ConfigWindow.xaml        settings window
  UI/Theme.xaml               dark design tokens and control styles

src/MonitorScreenSaver.Mac/             net9.0 — the AppKit head
  Program.cs                  command line: tray, settings, selftest, watch, harnesses
  Interop/                    objc_msgSend, CoreFoundation, CoreGraphics, IOKit, CoreAudio
  Platform/MacApp.cs          the App.xaml.cs twin: wiring, watchdog, [NSApp run]
  Platform/MacPlatform.cs     the seam implementations
  Platform/MacOverlayWindow.cs  non-activating NSPanel: black, dim or AVPlayerLooper video
  Platform/MacTray.cs         NSStatusItem + NSMenu
  Platform/MacSelfTest.cs     headless diagnostics (selftest)
  Platform/MacWatchMode.cs    power/topology/session logger (watch)
  UI/SettingsWindow.axaml     the Avalonia settings window
  UI/Theme.axaml              the same tokens, as Avalonia styles
  Assets/                     .icns + menu bar template art

tools/make-icon.ps1           icon compositor (.ico + README png)
tools/make-icns.sh            the mac twin (.icns + menu bar art)
tools/publish.ps1             single-file Windows release build
tools/bundle-macos.sh         the .app bundle
```
