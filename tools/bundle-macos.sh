#!/bin/bash
# Produces ./publish/MonitorScreenSaver.app — a menu-bar-only (LSUIElement) bundle.
#
#   tools/bundle-macos.sh [osx-arm64|osx-x64]
#
# One architecture per run, overwriting the same .app: zip the result before building
# the other one. A single universal bundle is deliberately not offered — lipo cannot
# merge two single-file .NET executables (the payload is appended after the Mach-O
# image, so lipo silently drops it), and un-single-filing the publish to merge ~15
# runtime dylibs buys nothing for a tool most people run on the Mac they built it on.
# The Avalonia/Skia dylibs we ship are already universal either way.
#
# Signing: ad-hoc by default, which is fine on the machine that built it. For
# distribution set SIGN_IDENTITY to a Developer ID Application identity:
#
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" tools/bundle-macos.sh
#
# then notarize and staple (both steps UNTESTED here — nobody has bought the $99
# certificate yet, see MACOS-PORT-PLAN.md "Risks and open questions"):
#
#   ditto -c -k --keepParent publish/MonitorScreenSaver.app /tmp/MonitorScreenSaver.zip
#   xcrun notarytool submit /tmp/MonitorScreenSaver.zip --keychain-profile NOTARY --wait
#   xcrun stapler staple publish/MonitorScreenSaver.app
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
out="$root/publish"
app="$out/MonitorScreenSaver.app"

# Read the version out of the csproj rather than repeating it here. This is what names the
# .dmg (make-dmg.sh reads CFBundleShortVersionString back off the built plist), so a stale
# literal here used to ship a release under the previous version's filename.
csproj="$root/src/MonitorScreenSaver.Mac/MonitorScreenSaver.Mac.csproj"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$csproj" | head -1)
[ -n "$version" ] || { echo "error: no <Version> in $csproj" >&2; exit 1; }

# Always publish into a clean directory: an incremental publish over an existing one
# leaves the executable in place but drops the loose native .dylib files next to it.
rm -rf "$out/mac-bin"

dotnet publish "$root/src/MonitorScreenSaver.Mac/MonitorScreenSaver.Mac.csproj" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$out/mac-bin"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp "$out/mac-bin/MonitorScreenSaverMac" "$app/Contents/MacOS/MonitorScreenSaver"

# The settings window's native dependencies (Skia, HarfBuzz, AvaloniaNative). A single-file
# publish does NOT embed native libraries, so without these the app runs fine until the
# moment someone opens Settings… and then throws DllNotFoundException. Next to the
# executable is where the .NET host looks, so no rpath or extraction dance is needed.
cp "$out/mac-bin"/*.dylib "$app/Contents/MacOS/"

# Icons (tools/make-icns.sh). The app icon is what the Dock tile of a minimised window,
# the app switcher and Finder draw — LSUIElement suppresses the running Dock icon, not
# the bundle icon, so without this macOS falls back to the generic placeholder. The
# status item art is looked up by name at runtime (NSImage imageNamed:), and the tray
# falls back to an SF Symbol when running unbundled.
mkdir -p "$app/Contents/Resources"
assets="$root/src/MonitorScreenSaver.Mac/Assets"
if [ -f "$assets/MonitorScreenSaver.icns" ]; then
    cp "$assets/MonitorScreenSaver.icns" "$assets"/MenuBarIcon*.png "$app/Contents/Resources/"
else
    echo "warning: $assets/MonitorScreenSaver.icns missing — run tools/make-icns.sh" >&2
fi

# Unquoted heredoc: $version is the only expansion in here, and it has to expand.
cat > "$app/Contents/Info.plist" <<PLIST
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
    <key>CFBundleIconFile</key>
    <string>MonitorScreenSaver</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$version</string>
    <key>CFBundleVersion</key>
    <string>$version</string>
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

identity="${SIGN_IDENTITY:--}"
entitlements=""

if [ "$identity" != "-" ]; then
    # Notarization requires the hardened runtime, and CoreCLR needs these three
    # exceptions to survive it: it JITs, so it writes then executes memory, and the
    # dylibs beside the executable are not signed by the same team as the app.
    entitlements="$out/entitlements.plist"
    cat > "$entitlements" <<'ENTITLEMENTS'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key>
    <true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
    <true/>
    <key>com.apple.security.cs.disable-library-validation</key>
    <true/>
</dict>
</plist>
ENTITLEMENTS
    echo "signing with: $identity (hardened runtime)"
fi

# A function rather than an array of flags: macOS ships bash 3.2, where expanding an
# empty array under `set -u` is an error.
sign() {
    if [ "$identity" = "-" ]; then
        codesign --force -s - "$1"
    else
        codesign --force -s "$identity" --options runtime --timestamp \
            --entitlements "$entitlements" "$1"
    fi
}

# Nested code has to be signed before the bundle that contains it.
for lib in "$app/Contents/MacOS"/*.dylib; do
    sign "$lib"
done
sign "$app"

codesign --verify --deep --strict "$app"

echo
echo "Bundled: $app  ($(du -sh "$app" | cut -f1))  [$rid, $(lipo -archs "$app/Contents/MacOS/MonitorScreenSaver")]"
echo "Run with: open '$app'"
echo "Verify:   '$app/Contents/MacOS/MonitorScreenSaver' selftest"
echo "Package:  tools/make-dmg.sh    (release .dmg with an /Applications symlink)"
