#!/bin/bash
# Builds Hue Tray, installs it to ~/Applications, sets it to start at login, and launches it.
set -euo pipefail
cd "$(dirname "$0")"

# Stop any running copy first, so it can be replaced.
osascript -e 'quit app id "io.github.hazeledmands.huetray"' 2>/dev/null || true
pkill -x hue-tray 2>/dev/null || true

./build.sh

dest=~/Applications/"Hue Tray.app"
mkdir -p ~/Applications
rm -rf "$dest"
cp -R "../build/macos/Hue Tray.app" "$dest"

"$dest/Contents/MacOS/hue-tray" login on
open "$dest"
echo "Installed $dest"
