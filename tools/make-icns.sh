#!/bin/bash
# The macOS twin of tools/make-icon.ps1. Generates, into src/MonitorScreenSaver.Mac/Assets:
#
#   MonitorScreenSaver.icns   the app icon (Dock tile of a minimised window, app switcher,
#                             Finder, Get Info). Without it macOS draws the generic
#                             placeholder, since LSUIElement apps still need a bundle icon.
#   MenuBarIcon.png/@2x.png   the status item image, used as a template: AppKit takes the
#                             mask from the alpha channel and tints it for the current menu
#                             bar, so the artwork is shipped as-is rather than recoloured.
#
# Source is the same Assets/icon.png the Windows head's .ico is built from, so both
# platforms stay one piece of artwork. Run it after make-icon.ps1 changes the art;
# the outputs are committed, exactly like MonitorScreenSaver.ico.
#
# Resolution ceiling: icon.png is 256x256 (the hoshinosleep master is 447x447), so the
# iconset stops at 256 rather than upscaling. That covers every size macOS actually
# draws except Finder at maximum icon zoom. Fixing that needs bigger artwork, not a
# bigger script.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/src/MonitorScreenSaver.Windows/Assets/icon.png"
out="$root/src/MonitorScreenSaver.Mac/Assets"
iconset="$(mktemp -d)/MonitorScreenSaver.iconset"

[ -f "$src" ] || { echo "source artwork not found: $src" >&2; exit 1; }

mkdir -p "$out" "$iconset"

# name=pixels. iconutil accepts a partial set; these are the sizes we can fill crisply.
for entry in \
    icon_16x16=16 \
    icon_16x16@2x=32 \
    icon_32x32=32 \
    icon_32x32@2x=64 \
    icon_128x128=128 \
    icon_128x128@2x=256 \
    icon_256x256=256
do
    name="${entry%=*}"
    px="${entry#*=}"
    sips -z "$px" "$px" "$src" --out "$iconset/$name.png" > /dev/null
done

iconutil -c icns "$iconset" -o "$out/MonitorScreenSaver.icns"

# Status item art. 18 pt is the menu bar's usable height; @2x for Retina.
sips -z 18 18 "$src" --out "$out/MenuBarIcon.png"    > /dev/null
sips -z 36 36 "$src" --out "$out/MenuBarIcon@2x.png" > /dev/null

rm -rf "$(dirname "$iconset")"

echo "Wrote:"
for f in MonitorScreenSaver.icns MenuBarIcon.png MenuBarIcon@2x.png; do
    echo "  $out/$f  ($(du -h "$out/$f" | cut -f1))"
done
