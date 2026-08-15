#!/bin/bash
# Packages ./publish/MonitorScreenSaver.app into a release disk image.
#
#   tools/bundle-macos.sh osx-arm64 && tools/make-dmg.sh
#
# Produces ./publish/MonitorScreenSaver-<version>-macos-<arch>.dmg: the app, an
# /Applications symlink to drag it onto, and a background poster behind them.
#
# Why a disk image and not a zip. A zip is smaller (38 MB vs 43 MB here) and builds in
# two seconds instead of twenty, and neither format changes anything about Gatekeeper —
# quarantine propagates through both. The difference is where the app ends up. Start at
# login is SMAppService.mainAppService (MacAutoStart.cs), and it records an absolute path:
#
#     URL: file:///Users/…/MonitorScreenSaver.app/
#
# An app run straight out of ~/Downloads is also liable to App Translocation, where macOS
# executes it from a randomised read-only mount instead — and per Apple DTS that is only
# cleared "if the user moves the app using the Finder". A login item pointing into a
# translocated path breaks at the next reboot. The /Applications symlink exists to make
# that Finder move the obvious thing to do.
#
# The window layout below and the artwork in tools/make-mac-icons.swift are one design:
# the icon positions here must match dmgAppX/dmgApplicationsX/dmgIconY there, or the icons
# will not sit on the panel drawn for them.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
app="$root/publish/MonitorScreenSaver.app"
assets="$root/src/MonitorScreenSaver.Mac/Assets"

[ -d "$app" ] || { echo "error: $app not found — run tools/bundle-macos.sh first" >&2; exit 1; }
[ -f "$assets/DmgBackground.png" ] || { echo "error: run tools/make-mac-icons.swift first" >&2; exit 1; }

version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$app/Contents/Info.plist")
case "$(lipo -archs "$app/Contents/MacOS/MonitorScreenSaver")" in
    *arm64*) arch=arm64 ;;
    *x86_64*) arch=x64 ;;
    *) arch=unknown ;;
esac

vol="MonitorScreenSaver"
dmg="$root/publish/MonitorScreenSaver-$version-macos-$arch.dmg"

# --------------------------------------------------------------- window layout

# Content area, matching dmgWidth/dmgHeight in tools/make-mac-icons.swift. Finder's
# `bounds` is the whole window, so the title bar has to be added on top of the content
# height or the background image sits 28 pt lower than the icons it was drawn around.
win_w=600
win_h=400
titlebar=28
win_x=240
win_y=180

icon_y=190
app_x=160
applications_x=440

# --------------------------------------------------------------- staging

staging=$(mktemp -d)
work=$(mktemp -d)
trap 'rm -rf "$staging" "$work"' EXIT

ditto "$app" "$staging/MonitorScreenSaver.app"
ln -s /Applications "$staging/Applications"

# A multi-representation TIFF rather than two PNGs: it is the only container Finder reads
# both densities out of, so the poster stays sharp on a Retina display.
mkdir -p "$staging/.background"
tiffutil -cathidpicheck "$assets/DmgBackground.png" "$assets/DmgBackground@2x.png" \
    -out "$staging/.background/background.tiff" >/dev/null

# The mounted volume's own icon, so it is the app rather than a generic white disk in the
# Finder sidebar and on the desktop.
if [ -f "$assets/MonitorScreenSaver.icns" ]; then
    cp "$assets/MonitorScreenSaver.icns" "$staging/.VolumeIcon.icns"
fi

# --------------------------------------------------------------- read-write image

# Sized with slack: -srcfolder alone fits the content exactly, leaving no room for the
# .DS_Store that this whole script exists to write.
size_kb=$(( $(du -sk "$staging" | cut -f1) + 40000 ))
rw="$work/rw.dmg"

hdiutil detach "/Volumes/$vol" -quiet 2>/dev/null || true
hdiutil create -volname "$vol" -srcfolder "$staging" -ov -format UDRW -fs HFS+ \
    -size "${size_kb}k" "$rw" >/dev/null

device=$(hdiutil attach "$rw" -noautoopen -owners on | grep -E '^/dev/' | head -1 | awk '{print $1}')
mount="/Volumes/$vol"

[ -d "$mount" ] || { echo "error: $vol did not mount" >&2; exit 1; }

# The C attribute is what tells Finder to use .VolumeIcon.icns at all.
[ -f "$mount/.VolumeIcon.icns" ] && SetFile -a C "$mount"

# --------------------------------------------------------------- layout

# Everything here lands in the volume's .DS_Store, which only Finder can write — hdiutil
# has no flag for window size, icon positions or the background picture. Hence driving the
# GUI. The close/open around the settings is not superstition: on macOS 26 the background
# picture does not take effect on the window that was open when it was set.
osascript <<APPLESCRIPT >/dev/null
tell application "Finder"
    tell disk "$vol"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {$win_x, $win_y, $((win_x + win_w)), $((win_y + win_h + titlebar))}

        set opts to the icon view options of container window
        set arrangement of opts to not arranged
        set icon size of opts to 128
        set text size of opts to 12
        set label position of opts to bottom
        set background picture of opts to file ".background:background.tiff"

        set position of item "MonitorScreenSaver.app" of container window to {$app_x, $icon_y}
        set position of item "Applications" of container window to {$applications_x, $icon_y}

        close
        open
        delay 1
        close
    end tell
end tell
APPLESCRIPT

sync
hdiutil detach "$device" -quiet

# --------------------------------------------------------------- compress and sign

rm -f "$dmg"
hdiutil convert "$rw" -format UDZO -imagekey zlib-level=9 -o "$dmg" >/dev/null

# Signing the disk image as well as the app inside it is what lets `stapler staple` attach
# a notarization ticket to the .dmg itself — a zip cannot hold one (`stapler` supports
# "UDIF disk images, code-signed executable bundles, and signed flat installer packages"),
# so with a zip the ticket has to go on the .app before compressing.
identity="${SIGN_IDENTITY:--}"
if [ "$identity" != "-" ]; then
    codesign --force -s "$identity" --timestamp "$dmg"
    echo "signed with: $identity"
fi

echo
echo "Built: $dmg  ($(du -h "$dmg" | cut -f1))  [$arch]"
echo "Check: open '$dmg'"
