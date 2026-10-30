// Hue Tray for macOS: controls Philips Hue lights from the menu bar. See README.md in the repo root.
//
// This is only the menu. The bridge connection, the command queue and the HTTP API are the same C#
// code as on Windows, running as a helper (`hue-tray run`, inside the app bundle). The menu reads
// and changes the lights through that API, like any other client.

import AppKit

let app = NSApplication.shared
let controller = MainActor.assumeIsolated { AppController() }
app.delegate = controller
app.setActivationPolicy(.accessory)
app.run()
