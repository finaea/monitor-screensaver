#!/bin/bash
# The macOS twin of tools/make-icon.ps1: regenerates every icon the mac head ships, into
# src/MonitorScreenSaver.Mac/Assets.
#
#   MonitorScreenSaver.icns   the app icon — the Dock tile of a minimised window, the app
#                             switcher, Finder, Get Info. Without it macOS draws the generic
#                             placeholder, since LSUIElement apps still need a bundle icon.
#   MenuBarIcon.png/@2x.png   the status item glyph.
#
# The drawing is all in tools/make-mac-icons.swift (see its header for why the artwork is
# composited and the glyph hand-drawn rather than both being resampled from icon.png); this
# script exists for the one step that needs a command line tool, iconutil. Outputs are
# committed, exactly like MonitorScreenSaver.ico, so a build never runs either script.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/src/MonitorScreenSaver.Mac/Assets"
staging="$(mktemp -d)"
iconset="$staging/MonitorScreenSaver.iconset"

mkdir -p "$out"

"$root/tools/make-mac-icons.swift" --iconset "$iconset"

iconutil -c icns "$iconset" -o "$out/MonitorScreenSaver.icns"

rm -rf "$staging"

echo "  $out/MonitorScreenSaver.icns  ($(du -h "$out/MonitorScreenSaver.icns" | cut -f1))"
