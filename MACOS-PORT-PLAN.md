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
