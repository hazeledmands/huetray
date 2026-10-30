import Foundation

/// The parts of the helper's config.json the menu needs. The helper owns the file: it creates
/// the API token and saves pairings; the menu only reads it.
struct Config: Decodable {
    var bridgeIp: String?
    var bridgeName: String?
    var appKey: String?
    var apiPort = 8765
    var apiToken = ""
    var menu = MenuConfig()

    var isPaired: Bool { !(bridgeIp ?? "").isEmpty && !(appKey ?? "").isEmpty }
    var bridgeLabel: String { bridgeName ?? bridgeIp ?? "Hue Bridge" }

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        bridgeIp = try c.decodeIfPresent(String.self, forKey: .bridgeIp)
        bridgeName = try c.decodeIfPresent(String.self, forKey: .bridgeName)
        appKey = try c.decodeIfPresent(String.self, forKey: .appKey)
        apiPort = try c.decodeIfPresent(Int.self, forKey: .apiPort) ?? apiPort
        apiToken = try c.decodeIfPresent(String.self, forKey: .apiToken) ?? apiToken
        menu = try c.decodeIfPresent(MenuConfig.self, forKey: .menu) ?? menu
    }

    enum CodingKeys: String, CodingKey { case bridgeIp, bridgeName, appKey, apiPort, apiToken, menu }

    /// Where the helper keeps it (.NET's ApplicationData folder on macOS).
    static let url = FileManager.default.homeDirectoryForCurrentUser
        .appending(path: "Library/Application Support/hue-tray/config.json")

    static func load() -> Config {
        guard let data = try? Data(contentsOf: url) else { return Config() }
        return (try? JSONDecoder().decode(Config.self, from: data)) ?? Config()
    }

    static func modified() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date
    }
}

/// Which rooms, zones, lights and scenes the menu shows. See MenuConfig in core/Config.cs.
struct MenuConfig: Decodable {
    var rooms: [String] = []
    var lights: [String] = []
    var scenes: [String] = []

    init() {}

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        rooms = try c.decodeIfPresent([String].self, forKey: .rooms) ?? []
        lights = try c.decodeIfPresent([String].self, forKey: .lights) ?? []
        scenes = try c.decodeIfPresent([String].self, forKey: .scenes) ?? []
    }

    enum CodingKeys: String, CodingKey { case rooms, lights, scenes }
}
