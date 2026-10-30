import Foundation

// The helper's HTTP API (see core/Api/ApiServer.cs and the README). These match its JSON.

struct Light: Decodable {
    let id: String
    let name: String
    let on: Bool
    let brightness: Double?
    let kelvin: Int?
    let supportsColor: Bool
    let supportsColorTemp: Bool
    let room: String?
}

/// A room or a zone.
struct LightGroup: Decodable {
    let id: String
    let kind: String
    let name: String
    let on: Bool
    let brightness: Double?
    let lightIds: [String]
}

struct Scene: Decodable {
    let id: String
    let name: String
    let groupId: String?
    let group: String?
    let active: Bool
}

struct ApiStatus: Decodable {
    let status: String   // unpaired, connecting, connected or unreachable
    let bridge: String
    let error: String?
}

struct ApiError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

struct Api: Sendable {
    let port: Int
    let token: String

    private var base: URL { URL(string: "http://127.0.0.1:\(port)/api/v1/")! }

    func get<T: Decodable>(_ path: String) async throws -> T {
        try JSONDecoder().decode(T.self, from: try await request("GET", path, nil))
    }

    /// `body` is the JSON body, if any.
    func send(_ method: String, _ path: String, _ body: [String: any Sendable]? = nil) async throws {
        _ = try await request(method, path, body.map { try JSONSerialization.data(withJSONObject: $0) })
    }

    private func request(_ method: String, _ path: String, _ body: Data?) async throws -> Data {
        var request = URLRequest(url: URL(string: path, relativeTo: base)!, timeoutInterval: 5)
        request.httpMethod = method
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if let body {
            request.httpBody = body
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await URLSession.shared.data(for: request)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(code) else {
            let error = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
            throw ApiError(message: error ?? "HTTP \(code)")
        }
        return data
    }

    /// The /events WebSocket: calls `received` with each message's type until the socket closes.
    @MainActor func events(_ received: (String, [String: Any]) -> Void) async throws {
        var request = URLRequest(url: URL(string: "ws://127.0.0.1:\(port)/api/v1/events")!)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        let socket = URLSession.shared.webSocketTask(with: request)
        socket.resume()
        defer { socket.cancel(with: .goingAway, reason: nil) }
        while !Task.isCancelled {
            guard case .string(let text) = try await socket.receive(),
                  let message = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any],
                  let type = message["type"] as? String else { continue }
            received(type, message)
        }
    }
}

/// Escapes a name or id for use in a path (names can have spaces and slashes).
func segment(_ s: String) -> String {
    s.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? s
}
