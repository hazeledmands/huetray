// swift-tools-version:6.0
// The Hue Tray menu bar app. build.sh (one folder up) builds this and puts it in Hue Tray.app
// next to hue-tray, the C# helper it runs.
import PackageDescription

let package = Package(
    name: "HueTray",
    platforms: [.macOS(.v13)],
    targets: [.executableTarget(name: "HueTray")]
)
