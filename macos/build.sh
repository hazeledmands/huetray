#!/bin/bash
# Builds build/macos/Hue Tray.app: the Swift menu bar app, with hue-tray (the C# helper and
# terminal commands) inside it. Needs the .NET 10 SDK (brew install --cask dotnet-sdk) and Xcode
# or its Command Line Tools (xcode-select --install). Builds for this Mac's processor, or set
# ARCH=arm64 or ARCH=x86_64.
set -euo pipefail
cd "$(dirname "$0")"
arch=${ARCH:-$(uname -m)}
case $arch in
    arm64) rid=osx-arm64 ;;
    x86_64) rid=osx-x64 ;;
    *) echo "Unknown ARCH $arch" >&2; exit 1 ;;
esac
out=../build/macos
app="$out/Hue Tray.app"

# dotnet-install.sh puts the SDK in ~/.dotnet without adding it to PATH.
if ! command -v dotnet >/dev/null && [ -x ~/.dotnet/dotnet ]; then
    export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
fi
if ! command -v dotnet >/dev/null; then
    echo "Needs the .NET 10 SDK: brew install --cask dotnet-sdk" >&2
    exit 1
fi

dotnet publish helper/HueTrayHelper.csproj -c Release -r $rid -o "$out/helper" --nologo
swift build -c release --package-path HueTray --arch "$arch"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp Info.plist "$app/Contents/"
cp "$(swift build -c release --package-path HueTray --arch "$arch" --show-bin-path)/HueTray" "$app/Contents/MacOS/"
cp "$out/helper/hue-tray" "$app/Contents/MacOS/"
# Signed for this Mac only ("ad hoc"), which is enough to run it and to show notifications.
codesign --force --sign - "$app/Contents/MacOS/hue-tray"
codesign --force --sign - "$app"
echo "Built $(cd "$out" && pwd)/Hue Tray.app"
