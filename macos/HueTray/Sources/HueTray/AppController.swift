import AppKit
import UniformTypeIdentifiers
import UserNotifications

/// The menu bar item and its menu. The same menu as the Windows tray app (windows/TrayApp.cs).
@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSMenuDelegate {
    static let brightnessPresets = [10, 25, 50, 75, 100]
    static let temperatures = [("Warm", 2700), ("Neutral", 4000), ("Cool", 6500)]
    static let loginAgent = FileManager.default.homeDirectoryForCurrentUser
        .appending(path: "Library/LaunchAgents/io.github.hazeledmands.huetray.plist")

    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private var cfg = Config.load()
    private var configDate = Config.modified()
    private var poll: Timer?

    private var helper: HelperProcess?
    private var helperError: String?
    private var restart: Task<Void, Never>?
    private var events: Task<Void, Never>?
    private var refreshQueued = false

    // What the helper last told us.
    private var status = "unpaired"
    private var loaded = false
    private var lights: [Light] = []
    private var rooms: [LightGroup] = []
    private var zones: [LightGroup] = []
    private var scenes: [Scene] = []

    private var pairing = false
    private var foundBridges: [(ip: String, name: String)] = []

    private var api: Api { Api(port: cfg.apiPort, token: cfg.apiToken) }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Only one copy at a time. (A second couldn't open the API port anyway.)
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).count > 1 {
            NSApp.terminate(nil)
            return
        }
        if Bundle.main.bundleIdentifier != nil {
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { _, _ in }
        }
        // Start each run with a fresh log once it gets big.
        if let size = (try? FileManager.default.attributesOfItem(atPath: Helper.logURL.path))?[.size] as? Int, size > 1_000_000 {
            try? FileManager.default.removeItem(at: Helper.logURL)
        }

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        menu.delegate = self
        menu.autoenablesItems = false
        statusItem.menu = menu

        // Every 2 s, pick up hand edits to config.json.
        poll = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { _ in
            Task { @MainActor in await self.pollConfig() }
        }

        Task {
            // The helper creates config.json, with the API token, the first time it runs.
            if !FileManager.default.fileExists(atPath: Config.url.path) {
                _ = await Helper.run(["token"])
                reloadConfig()
            }
            startServices()
            if !cfg.isPaired { await pairClicked() }
        }
    }

    // MARK: The helper

    private func startServices() {
        restart = nil
        loaded = false
        guard cfg.isPaired else {
            status = "unpaired"
            refreshIcon()
            return
        }
        status = "connecting"
        do {
            let helper = try HelperProcess()
            helper.onExit = { [weak self] line in self?.helperExited(line) }
            self.helper = helper
            helperError = nil
        } catch {
            helperError = "Couldn't start the helper: \(error.localizedDescription)"
        }
        events = Task { await followEvents() }
        refreshIcon()
    }

    private func stopServices() async {
        restart?.cancel()
        restart = nil
        events?.cancel()
        events = nil
        await helper?.stop()
        helper = nil
    }

    // It only stops by itself on an error, like the API port being in use. Try again in a bit, in
    // case that clears up.
    private func helperExited(_ line: String?) {
        helper = nil
        events?.cancel()
        helperError = line ?? "The helper stopped"
        Log.write("The helper stopped: \(helperError!)")
        refreshIcon()
        restart = Task {
            try? await Task.sleep(for: .seconds(5))
            if !Task.isCancelled { startServices() }
        }
    }

    /// Follows the helper's /events WebSocket, reconnecting until cancelled. The helper takes a
    /// moment to start, and the socket drops whenever it restarts.
    private func followEvents() async {
        while !Task.isCancelled {
            do {
                try await api.events { type, message in
                    if type == "status", let s = message["status"] as? String { self.statusChanged(s) }
                    self.queueRefresh()
                }
            } catch {}
            try? await Task.sleep(for: .milliseconds(500))
        }
    }

    // State changes can arrive many times a second during a jog spin; fetch once per burst.
    private func queueRefresh() {
        if refreshQueued { return }
        refreshQueued = true
        Task {
            try? await Task.sleep(for: .milliseconds(100))
            refreshQueued = false
            await refresh()
        }
    }

    private func refresh() async {
        let api = self.api
        do {
            async let s: ApiStatus = api.get("status")
            async let l: [Light] = api.get("lights")
            async let r: [LightGroup] = api.get("rooms")
            async let z: [LightGroup] = api.get("zones")
            async let sc: [Scene] = api.get("scenes")
            let (status, lights, rooms, zones, scenes) = try await (s, l, r, z, sc)
            (self.lights, self.rooms, self.zones, self.scenes) = (lights, rooms, zones, scenes)
            if status.status == "connected" { loaded = true }
            statusChanged(status.status)
        } catch {}
        refreshIcon()
    }

    private func statusChanged(_ new: String) {
        let was = status
        status = new
        if new == "unreachable" && was == "connected" {
            notify("Lost \(cfg.bridgeLabel)", "Trying to reconnect…")
        } else if new == "connected" && was == "unreachable" {
            notify("\(cfg.bridgeLabel) is back", "The lights can be controlled again.")
        }
        refreshIcon()
    }

    private func reloadConfig() {
        cfg = Config.load()
        configDate = Config.modified()
    }

    private func pollConfig() async {
        guard Config.modified() != configDate, !pairing else { return }
        reloadConfig()
        await stopServices()
        startServices()
    }

    // MARK: Icon

    private func refreshIcon() {
        guard let button = statusItem?.button else { return }
        let anyOn = loaded && lights.contains { $0.on }
        let image = NSImage(systemSymbolName: anyOn ? "lightbulb.fill" : "lightbulb", accessibilityDescription: "Hue Tray")
        image?.isTemplate = true
        button.image = image
        button.appearsDisabled = status != "connected"
        button.toolTip = "Hue Tray: " + statusText()
    }

    private func statusText() -> String {
        if pairing { return "Pairing…" }
        if let helperError, helper == nil { return "Not running: \(helperError)" }
        switch status {
        case "unpaired": return "Not paired with a bridge"
        case "connecting": return "Connecting to \(cfg.bridgeLabel)…"
        case "unreachable": return "Can't reach \(cfg.bridgeLabel)"
        default: return "Connected to \(cfg.bridgeLabel)"
        }
    }

    /// Shows a notification. Showing a new one replaces the last.
    private func notify(_ title: String, _ body: String) {
        Log.write("\(title): \(body)")
        guard Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: "hue-tray", content: content, trigger: nil))
    }

    // MARK: Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(disabled(statusText()))
        menu.addItem(.separator())

        if loaded {
            menu.addItem(scenesMenu())
            let byId = Dictionary(lights.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
            for group in shown(rooms + zones, cfg.menu.rooms, showAllIfEmpty: true, id: \.id, name: \.name) {
                menu.addItem(groupMenu(group, byId))
            }
            for light in shown(lights, cfg.menu.lights, showAllIfEmpty: false, id: \.id, name: \.name) {
                menu.addItem(lightMenu(light))
            }
            menu.addItem(.separator())
            menu.addItem(item("All Off") { self.send("POST", "off") })
            menu.addItem(.separator())
        }

        if foundBridges.count > 1 {
            for bridge in foundBridges {
                menu.addItem(item("Pair with \(bridge.name) (\(bridge.ip))") { Task { await self.pairWith(bridge) } })
            }
        } else {
            let pair = item(pairing ? "Pairing…" : cfg.isPaired ? "Pair Another Bridge…" : "Pair Bridge…") {
                Task { await self.pairClicked() }
            }
            pair.isEnabled = !pairing
            menu.addItem(pair)
        }
        menu.addItem(item("Open Settings File") { self.openSettings() })
        menu.addItem(item("Copy API Token") {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(self.cfg.apiToken, forType: .string)
        })
        let startAtLogin = FileManager.default.fileExists(atPath: Self.loginAgent.path)
        menu.addItem(item("Start at Login", checked: startAtLogin) {
            Task { _ = await Helper.run(["login", startAtLogin ? "off" : "on"]) }
        })
        menu.addItem(.separator())
        menu.addItem(item("Quit Hue Tray", key: "q") { Task { await self.quit() } })
    }

    /// The items named in `wanted` (by name or id), in that order; or all of them if it's empty and `showAllIfEmpty`.
    private func shown<T>(_ items: [T], _ wanted: [String], showAllIfEmpty: Bool,
                          id: (T) -> String, name: (T) -> String) -> [T] {
        if wanted.isEmpty { return showAllIfEmpty ? items : [] }
        return wanted.flatMap { w in items.filter { id($0) == w || name($0).caseInsensitiveCompare(w) == .orderedSame } }
    }

    private func scenesMenu() -> NSMenuItem {
        let parent = NSMenuItem(title: "Scenes", action: nil, keyEquivalent: "")
        let submenu = NSMenu()
        submenu.autoenablesItems = false
        let wanted = shown(scenes, cfg.menu.scenes, showAllIfEmpty: true, id: \.id, name: \.name)
        let byGroup = Dictionary(grouping: wanted) { $0.group ?? "Other" }
        for group in byGroup.keys.sorted(by: { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }) {
            if submenu.numberOfItems > 0 { submenu.addItem(.separator()) }
            submenu.addItem(header(group))
            for scene in byGroup[group]! {
                let entry = item(scene.name, checked: scene.active) {
                    self.send("POST", "scenes/\(segment(scene.id))/activate")
                }
                entry.indentationLevel = 1
                submenu.addItem(entry)
            }
        }
        if submenu.numberOfItems == 0 { submenu.addItem(disabled("No scenes")) }
        parent.submenu = submenu
        return parent
    }

    private func groupMenu(_ group: LightGroup, _ lights: [String: Light]) -> NSMenuItem {
        let members = group.lightIds.compactMap { lights[$0] }
        let parent = NSMenuItem(title: title(group.name, group.on, group.brightness), action: nil, keyEquivalent: "")
        let submenu = controls(on: group.on, brightness: group.brightness, colorTemp: members.contains { $0.supportsColorTemp }) {
            self.send("PATCH", "\(group.kind)s/\(segment(group.id))", $0)
        }
        if !members.isEmpty {
            submenu.addItem(.separator())
            for light in members { submenu.addItem(lightMenu(light)) }
        }
        parent.submenu = submenu
        return parent
    }

    private func lightMenu(_ light: Light) -> NSMenuItem {
        let parent = NSMenuItem(title: title(light.name, light.on, light.brightness), action: nil, keyEquivalent: "")
        parent.submenu = controls(on: light.on, brightness: light.brightness, colorTemp: light.supportsColorTemp) {
            self.send("PATCH", "lights/\(segment(light.id))", $0)
        }
        return parent
    }

    private func title(_ name: String, _ on: Bool, _ brightness: Double?) -> String {
        on ? "\(name)  (\(Int((brightness ?? 100).rounded()))%)" : "\(name)  (off)"
    }

    /// On, the brightness presets and (if any light supports it) the colour temperatures.
    /// `apply` gets an API change body, like {"brightness": 50}.
    private func controls(on: Bool, brightness: Double?, colorTemp: Bool,
                          apply: @escaping ([String: any Sendable]) -> Void) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.addItem(item("On", checked: on) { apply(["on": !on]) })
        menu.addItem(.separator())
        for level in Self.brightnessPresets {
            let current = on && brightness.map { abs($0 - Double(level)) < 3 } == true
            menu.addItem(item("\(level)%", checked: current) { apply(["brightness": level]) })
        }
        if colorTemp {
            menu.addItem(.separator())
            for (name, kelvin) in Self.temperatures {
                menu.addItem(item("\(name) (\(kelvin) K)") { apply(["colorTemp": kelvin]) })
            }
        }
        return menu
    }

    private func send(_ method: String, _ path: String, _ body: [String: any Sendable]? = nil) {
        let api = self.api
        Task {
            do {
                try await api.send(method, path, body)
            } catch {
                notify("Couldn't do that", error.localizedDescription)
            }
        }
    }

    private func item(_ title: String, checked: Bool = false, key: String = "", _ action: @escaping () -> Void) -> NSMenuItem {
        let item = ActionItem(title: title, action: #selector(ActionItem.run), keyEquivalent: key)
        item.target = item
        item.handler = action
        item.state = checked ? .on : .off
        return item
    }

    private func disabled(_ title: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    private func header(_ title: String) -> NSMenuItem {
        if #available(macOS 14, *) { return .sectionHeader(title: title) }
        return disabled(title)
    }

    // MARK: Pairing

    private func pairClicked() async {
        if pairing { return }
        pairing = true
        refreshIcon()
        defer {
            pairing = false
            refreshIcon()
        }
        notify("Looking for your Hue Bridge…", "This takes a few seconds.")
        let found = await Helper.run(["discover"]).output
            .split(whereSeparator: \.isNewline)
            .map { $0.split(separator: "\t", omittingEmptySubsequences: false).map(String.init) }
            .filter { $0.count >= 2 }
            .map { (ip: $0[0], name: $0[1]) }
        if found.isEmpty {
            notify("No bridge found", "Make sure it's on the same network. If you know its address, put it in bridgeIp in the settings file and choose Pair Bridge again.")
        } else if found.count > 1 {
            foundBridges = found
            notify("Found \(found.count) bridges", "Choose one from the menu.")
        } else {
            await pair(found[0])
        }
    }

    private func pairWith(_ bridge: (ip: String, name: String)) async {
        if pairing { return }
        pairing = true
        foundBridges = []
        refreshIcon()
        await pair(bridge)
        pairing = false
        refreshIcon()
    }

    private func pair(_ bridge: (ip: String, name: String)) async {
        notify("Press the link button", "Press the round button on top of \(bridge.name) within 60 seconds.")
        let result = await Helper.run(["pair", bridge.ip])
        guard result.status == 0 else {
            notify("Couldn't pair", Helper.lastLine(result.output) ?? "Choose Pair Bridge… to try again.")
            return
        }
        reloadConfig()
        await stopServices()
        startServices()
        notify("Paired with \(bridge.name)", "Your rooms and scenes are in the menu.")
    }

    // MARK: Other menu items

    // In the text editor, not whatever opens .json files (often Xcode).
    private func openSettings() {
        let editor = NSWorkspace.shared.urlForApplication(toOpen: .plainText)
            ?? URL(fileURLWithPath: "/System/Applications/TextEdit.app")
        NSWorkspace.shared.open([Config.url], withApplicationAt: editor, configuration: NSWorkspace.OpenConfiguration())
    }

    private func quit() async {
        poll?.invalidate()
        await stopServices()
        NSApp.terminate(nil)
    }
}

/// A menu item that runs a closure.
final class ActionItem: NSMenuItem {
    var handler: (() -> Void)?
    @objc func run() { handler?() }
}

enum Log {
    static func write(_ s: String) {
        FileHandle.standardError.write(Data("[\(Date().formatted(date: .omitted, time: .standard))] \(s)\n".utf8))
    }
}
