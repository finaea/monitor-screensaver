#!/bin/bash
# Produces ./publish/MonitorScreenSaver.app — a menu-bar-only (LSUIElement) bundle,
# ad-hoc signed. Usage: tools/bundle-macos.sh [osx-arm64|osx-x64]
#
# Ad-hoc signing is fine for the local machine; distribution needs a Developer ID
# certificate + notarization (see MACOS-PORT-PLAN.md, "Risks and open questions").
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
out="$root/publish"
app="$out/MonitorScreenSaver.app"

dotnet publish "$root/src/MonitorScreenSaver.Mac/MonitorScreenSaver.Mac.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$out/mac-bin"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp "$out/mac-bin/MonitorScreenSaverMac" "$app/Contents/MacOS/MonitorScreenSaver"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>MonitorScreenSaver</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.finaea.MonitorScreenSaver</string>
    <key>CFBundleName</key>
    <string>MonitorScreenSaver</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.1.0</string>
    <key>CFBundleVersion</key>
    <string>1.1.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <!-- Menu-bar-only: no Dock icon, no app switcher entry - the tray-app posture. -->
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

codesign --force -s - "$app"

echo
echo "Bundled: $app  ($(du -sh "$app" | cut -f1))"
echo "Run with: open '$app'"
