import Foundation

/// Runs hue-tray, the C# helper in Contents/MacOS: one-off commands like `pair`, and `run`, which
/// keeps the bridge connection and the API going for as long as the app runs.
enum Helper {
    static let url: URL = Bundle.main.url(forAuxiliaryExecutable: "hue-tray")
        ?? URL(fileURLWithPath: ProcessInfo.processInfo.environment["HUE_TRAY_HELPER"] ?? "hue-tray")

    /// The helper's output from `run` goes here.
    static let logURL = FileManager.default.homeDirectoryForCurrentUser.appending(path: "Library/Logs/Hue Tray.log")

    /// Runs a command to the end. Returns its exit status and output (stdout and stderr together).
    static func run(_ args: [String]) async -> (status: Int32, output: String) {
        await Task.detached {
            let process = Process()
            process.executableURL = url
            process.arguments = args
            let pipe = Pipe()
            process.standardOutput = pipe
            process.standardError = pipe
            process.standardInput = FileHandle.nullDevice
            do { try process.run() } catch { return (-1, "Couldn't run \(url.path): \(error.localizedDescription)") }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return (process.terminationStatus, String(decoding: data, as: UTF8.self))
        }.value
    }

    /// The last non-empty line of some output, for showing in a notification or the menu.
    static func lastLine(_ output: String) -> String? {
        output.split(whereSeparator: \.isNewline).last.map(String.init)
    }
}

/// A running `hue-tray run`. Its stdin is a pipe from us, so it quits if the app goes away for any
/// reason (see --exit-with-stdin in core/Commands.cs).
@MainActor
final class HelperProcess {
    private let process = Process()
    private let stdin = Pipe()
    private var stopping = false
    private var exited: [CheckedContinuation<Void, Never>] = []

    /// The last line it printed, which explains why it stopped if it did.
    private(set) var lastLine: String?

    /// Called if it stops by itself, with what it last printed.
    var onExit: ((String?) -> Void)?

    init() throws {
        process.executableURL = Helper.url
        process.arguments = ["run", "--exit-with-stdin"]
        process.standardInput = stdin
        let output = Pipe()
        process.standardOutput = output
        process.standardError = output

        if !FileManager.default.fileExists(atPath: Helper.logURL.path) {
            FileManager.default.createFile(atPath: Helper.logURL.path, contents: nil)
        }
        let log = try? FileHandle(forWritingTo: Helper.logURL)
        log?.seekToEndOfFile()
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            guard !data.isEmpty else {
                handle.readabilityHandler = nil
                try? log?.close()
                return
            }
            log?.write(data)
            if let line = Helper.lastLine(String(decoding: data, as: UTF8.self)) {
                Task { @MainActor in self?.lastLine = line }
            }
        }
        process.terminationHandler = { [weak self] _ in
            Task { @MainActor in self?.terminated() }
        }
        try process.run()
    }

    private func terminated() {
        exited.forEach { $0.resume() }
        exited.removeAll()
        if !stopping { onExit?(lastLine) }
    }

    /// Asks it to finish sending queued changes and quit, and waits (up to 3 s, then kills it).
    func stop() async {
        stopping = true
        guard process.isRunning else { return }
        process.interrupt()
        let pid = process.processIdentifier
        Task {
            try? await Task.sleep(for: .seconds(3))
            if self.process.isRunning { kill(pid, SIGKILL) }
        }
        await withCheckedContinuation { exited.append($0) }
    }
}
