#!/bin/bash
# Builds both macOS release dmgs in one go:
#
#   tools/release-macos.sh
#
# x64 FIRST, arm64 SECOND — the order is the point. bundle-macos.sh overwrites the
# same publish/MonitorScreenSaver.app on every run (one arch per run, see its
# header), and the start-at-login item records an absolute path into publish/ —
# so whatever arch was built last is what launches at every login. When that
# leftover was the x64 build, it ran under Rosetta and macOS Tahoe 26.4+
# periodically nagged that the app "is built for an Intel-based Mac". Ending on
# arm64 always leaves a native bundle behind.
set -euo pipefail

cd "$(dirname "$0")/.."

# bundle-macos.sh needs the dotnet CLI; non-login shells (ssh, agents) often
# don't have it on PATH — fall back to the default install location.
command -v dotnet >/dev/null 2>&1 || PATH="$HOME/.dotnet:$PATH"

tools/bundle-macos.sh osx-x64
tools/make-dmg.sh
tools/bundle-macos.sh osx-arm64
tools/make-dmg.sh

echo
echo "Done. publish/MonitorScreenSaver.app is the arm64 build — the login item launches native."
