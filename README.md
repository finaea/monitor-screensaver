<div align="center">

<img src="src/MonitorScreenSaver.Windows/Assets/icon.png" alt="MonitorScreenSaver" width="128">

# MonitorScreenSaver

**Protects selected OLED displays with true black, dimming, or video without putting them to sleep.**

Windows 10/11 · macOS 13+ · notification area / menu bar

</div>

This app is built to allow OLED monitors to display black screen or screensaver video when the user is not actively using it without getting the monitor to sleep (by windows). This app allows idle monitors to display in **true black** by emitting nothing with 0 rgb values (black pixels). Windows' only support the feature to *power the display off* — and
a powered-off monitor does not come back instantly. The reason for that is that on DisplayPort the link drops entirely,
so Windows sees an unplug: it re-detects your displays, re-trains the link, and shuffles every
window around while you sit there waiting ([Rapid Hot Plug Detect](https://devblogs.microsoft.com/directx/avoid-unexpected-app-rearrangement/)). This app intends to address few pain points and make few quality of life changes.

<div align="center">
<img src="config.png" alt="MonitorScreenSaver settings window" width="620">
</div>

## What MonitorScreenSaver does

- **Protects OLED displays** — True black turns OLED pixels off, while Dim and Video provide less disruptive alternatives.
- **Manages each display separately** — Every monitor can use its own mode, brightness, video, and scaling.
- **Responds to activity** — Keyboard input, mouse movement, window changes, fullscreen apps, audio, and display-awake requests can control when blanking begins.
- **Returns immediately** — Normal keyboard or mouse activity removes the overlay without waiting for the monitor to reconnect.
- **List holding apps** — Settings and the tray or menu-bar menu can show which apps are asking the system to keep displays awake.
- **Provides quick controls** — Displays can be blanked immediately, paused, or controlled with a configurable system-wide shortcut.

## Install

### Windows

Two single-file downloads are available from [Releases](../../releases):

| Download | Best for |
|---|---|
| `MonitorScreenSaver.exe` | Smaller download; requires the .NET 9 Desktop Runtime |
| `MonitorScreenSaverSC.exe` | Larger download; includes the runtime |

1. Download either Windows file.
2. When using the smaller file, install the [.NET 9 Desktop Runtime](https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe).
3. Open the downloaded file.
4. SmartScreen may appear because the app is not code signed. **More info → Run anyway** allows it to open. Windows 11 Smart App Control may block unsigned apps.

MonitorScreenSaver appears in the Windows notification area after launch.

### macOS

1. Download the `arm64` disk image for Apple silicon or `x64` for Intel from [Releases](../../releases). The Mac type is shown under Apple menu → About This Mac.
2. Open the disk image and drag MonitorScreenSaver to Applications.
3. Open MonitorScreenSaver from Applications.
4. If macOS blocks the first launch, System Settings → Privacy & Security → **Open Anyway** allows the app to open.

MonitorScreenSaver appears in the menu bar after launch. The Dock icon appears only while Settings is open.

## Main controls

| Setting | What it does |
|---|---|
| **Displays to blank** | Selects which monitors MonitorScreenSaver can cover. The choices remain linked to each physical monitor after reconnection. |
| **Overlay** | Chooses True black, Dim, or Video. |
| **Configure each display individually** | Gives each selected monitor its own overlay settings. |
| **Idle timeout** | Sets how long MonitorScreenSaver waits before covering the selected displays. |
| **Status** | Shows whether the system is active, the remaining time, and any apps keeping displays awake. |
| **Blank now** | Covers the selected displays immediately. |
| **Pause** | Stops automatic blanking without closing the app. |
| **Blank now shortcut** | Sets a system-wide shortcut. The default is `Ctrl+Alt+Shift+B` on Windows and `⌃⌥⇧B` on macOS. |
| **Start at login** | Starts MonitorScreenSaver automatically after sign-in. |

The overlay disappears when normal keyboard, mouse, or window activity resumes. Holding the Blank now shortcut keeps the displays covered until the shortcut is released.

The operating system’s display-sleep timer should be longer than the MonitorScreenSaver idle timeout. Otherwise, the system may power off the displays before MonitorScreenSaver can cover them.

## Display activity

MonitorScreenSaver can treat the following signals as activity:

| Signal | Default |
|---|---|
| Keyboard and mouse input | Always enabled |
| Window focus changes | Enabled |
| Apps asking the display to stay awake | Enabled |
| Exclusive fullscreen apps | Enabled |
| Audio playback | Disabled |

Video players, calls, screen sharing, remote desktop tools, games, and recording apps often ask the operating system to keep displays awake. MonitorScreenSaver follows those requests by default so it does not cover an active movie, call, or game.

### Apps keeping displays awake

The current list appears in Settings and in the notification-area or menu-bar menu.

On Windows, basic blanking works without administrator access. Showing app names and ignoring selected apps uses `powercfg /requests`, which requires administrator access. **Restart elevated** provides temporary access, while **Start elevated** enables it automatically after sign-in.

On macOS, app names and exceptions work without administrator access.

An app can be blacklisted from Settings when its display-awake request should not delay blanking. The menu reports the current list but does not change it.

## Video mode

Video mode plays a muted loop on a selected display. Three scaling options are available:

- **Fit** keeps the full image and adds black bars when needed.
- **Fill** covers the display and crops the edges when needed.
- **Stretch** fills the display without keeping the original shape.

Windows supports common formats such as MP4, M4V, MOV, WMV, AVI, HEVC, WebM, MKV, and TS when the required Windows codecs are installed. macOS supports MP4, M4V, MOV, and TS through AVFoundation.

A moving video can reduce uneven wear, but it does not protect an OLED display as much as True black because the pixels remain lit.

## Platform notes

- **Windows lock screen** — MonitorScreenSaver cannot cover the Windows lock screen because it runs in a separate protected desktop.
- **macOS login and lock screens** — macOS does not allow the app to cover these screens.
- **Other overlays** — Apps that draw above screensaver-level windows may remain visible over a covered display.
- **Start at login on macOS** — The app must remain in Applications after this option is enabled.

## Troubleshooting

Error logs are stored at:

| Platform | Location |
|---|---|
| Windows | `%APPDATA%\MonitorScreenSaver\error.log` |
| macOS | `~/Library/Application Support/MonitorScreenSaver/error.log` |

A diagnostic report can be created with:

```powershell
MonitorScreenSaver.exe --selftest report.txt
```

```bash
/Applications/MonitorScreenSaver.app/Contents/MacOS/MonitorScreenSaver selftest report.txt
```

A successful check exits with code 0. The generated report can be attached to a GitHub issue when support is needed.

## Building from source

Building requires the .NET 9 SDK.

### Windows

```powershell
git clone https://github.com/finaea/monitor-screensaver.git
cd monitor-screensaver
.\tools\publish.ps1
```

The Windows builds are placed in `publish/`.

### macOS

```bash
git clone https://github.com/finaea/monitor-screensaver.git
cd monitor-screensaver
tools/bundle-macos.sh
open publish/MonitorScreenSaver.app
```

`tools/bundle-macos.sh osx-x64` creates an Intel build. `tools/make-dmg.sh` can package the app as a disk image.

More implementation details are available in [TECHNICAL.md](TECHNICAL.md).

## License

[PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0) — see [LICENSE](LICENSE).

The source may be used, modified, forked, and shared for noncommercial purposes, including personal projects, study, research, charities, schools, and public institutions. Commercial use is not covered; a GitHub issue can be opened to discuss a commercial licence.
