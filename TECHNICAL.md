# MonitorDim — technical notes

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

Two implementation notes worth keeping:

- **`SetWindowLongPtr(WS_EX_LAYERED)` does not stick on a WPF window.** `HwndTarget`
  rewrites the extended style while realising the window, so the uniform-alpha route
  (`SetLayeredWindowAttributes`, which would have stayed DWM-composited) is unavailable.
  WPF's own `AllowsTransparency` is the only supported path.
- **`AllowsTransparency` is a create-time property**, and it costs a software-rendered
  surface — roughly 29 MB on a 5120x1440 panel. So true black keeps an ordinary opaque
  window and pays nothing, and crossing between the two modes recreates the overlay
  rather than trying to mutate it. `OverlayWindow.SetAlpha` returns false to signal that.

### Beyond Windows (opt-in, clearly labelled in the UI)

- **Never blank during exclusive fullscreen** — `SHQueryUserNotificationState` returning
  `QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE`. Windows does *not* do this;
  it exists because an overlay over an exclusive-fullscreen swapchain is a functional hazard.

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

MonitorDim only gets to act inside the window before Windows' own display timeout fires, so
that timeout has to be longer than the app's idle timeout:

```powershell
powercfg /change monitor-timeout-ac 1800    # 30 min
powercfg /change monitor-timeout-dc 1800
```

**Long-but-finite beats `Never`.** The console-lock timeout below has to stay *smaller* than
this one to keep working, and `Never` may or may not suppress it (see the unresolved note at
the end of the next section). With a 30-minute backstop, MonitorDim handles the everyday
5-minute case and Windows only takes over after a full half hour of no lock and no input —
rare enough that eating one rearrangement is cheap.

---

## The lock screen (measured)

**MonitorDim cannot help here at all.** The Windows lock screen runs on the Winlogon
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

## Build

```powershell
dotnet build                 # dev build
.\tools\publish.ps1          # single self-contained exe in .\publish
.\tools\make-icon.ps1        # regenerate Assets\MonitorDim.ico + Assets\icon.png
```

Requires the .NET 9 SDK. Targets `net9.0-windows`, `win-x64`.

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

---

## Self-test

```powershell
MonitorDim.exe --selftest report.txt
```

Headless — no tray, no windows, no engine. 89 checks covering:

- WPF text layout forced through every font family the UI uses, in both formatting modes,
  plus every Theme.xaml brush and style (this is what catches globalization/font regressions
  that break the settings window while leaving the empty overlay windows working)
- tray icon resource loads through both the WPF and GDI+ decoders (pack:// URIs plus
  single-file publishing is a classic silent failure)
- display enumeration and EDID friendly names
- overlay placement verified against `GetWindowRect` on every monitor, in both true-black
  and dim modes, including that switching between them correctly demands a rebuild
- power-request detection through **both** the legacy and modern APIs
- idle arithmetic including the 32-bit `GetLastInputInfo` tick wrap
- the `powercfg /requests` parser
- settings round-trip and autostart queries

Exit code 0 = all passed.

> `SystemExecutionState` does not always update synchronously with a set/clear call — a
> same-instant read can still see the old value. The engine polls (250 ms) so this is
> harmless in production, but the self-test polls too, or it flakes.

## Watch mode

```powershell
MonitorDim.exe --watch watch.log
```

Logs every display-power and session transition Windows reports, with timestamps, plus a
10-second heartbeat carrying the execution state. Subscribes to both
`GUID_SESSION_DISPLAY_STATUS` (documented for interactive apps) and
`GUID_CONSOLE_DISPLAY_STATE` (kernel-level) so the two can be cross-checked — useful because
it is not obvious which still gets delivered while the session is locked. In practice both do.

This is how the lock-screen timings above were measured. Start it, lock the machine, wait,
unlock, then read the log. Runs until killed; no tray, no engine, no overlays.

---

## Footprint

| Build | Working set | Private |
|---|---|---|
| Release, single-file **compressed** | ~331 MB | ~233 MB |
| Release, single-file (shipped config) | **~241 MB** | ~204 MB |

Measured with the settings window open, which is the worst case. An earlier revision recorded
~198 MB, but that number is not comparable — it was taken while the settings window was
silently failing to render (see `InvariantGlobalization` above), so nothing was actually
being laid out. Workstation non-concurrent GC accounts for most of the saving over default.

The floor here is WPF + WinForms both being loaded; WinForms is present solely for the tray
`NotifyIcon`. Replacing it with a raw `Shell_NotifyIcon` P/Invoke and a WPF `ContextMenu`
would cut further and let the tray menu use the same dark theme as the settings window.

---

## Diagnostics

Unhandled exceptions, swallowed exceptions and failed callbacks all append to
`%APPDATA%\MonitorDim\error.log`. Check there first — a window that fails to build looks
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

```
App.xaml.cs               tray icon, menu, wiring, lifecycle
Core/BlankingEngine.cs    the policy — category 1 + category 2 decision
Core/PowerRequests.cs     SystemExecutionState + powercfg /requests parser
Core/OverlayWindow.cs     one black non-activating topmost window
Core/OverlayManager.cs    keeps overlays in sync with the display topology
Core/DisplayEnumerator.cs monitor enumeration + EDID friendly names
Core/SystemEventSink.cs   sleep / hotplug / lock notifications
Core/SelfTest.cs          headless diagnostics (--selftest)
Core/WatchMode.cs         display-power transition logger (--watch)
Core/CrashLog.cs          error.log + P/Invoke callback guards
UI/ConfigWindow.xaml      settings window
UI/Theme.xaml             dark design tokens and control styles
tools/make-icon.ps1       icon compositor
tools/publish.ps1         single-file release build
```
