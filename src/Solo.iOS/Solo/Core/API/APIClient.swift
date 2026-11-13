import Foundation

/// What the API said when it refused: RFC 7807 problem details where the body
/// has them, the bare status where it does not (a proxy's error page, say).
nonisolated struct APIError: LocalizedError, Sendable {
    var status: Int
    var title: String?
    var detail: String?
    var retryAfter: TimeInterval?

    var isConflict: Bool { status == 409 }
    var isUnauthorized: Bool { status == 401 }
    var isForbidden: Bool { status == 403 }
    var isNotFound: Bool { status == 404 }

    var errorDescription: String? {
        detail ?? title ?? HTTPURLResponse.localizedString(forStatusCode: status).capitalized
    }
}

/// The request never reached the instance, or its answer never reached us.
nonisolated struct ConnectionError: LocalizedError, Sendable {
    var host: String

    var errorDescription: String? { "Couldn't reach \(host). Check your connection and try again." }
}

nonisolated enum SoloJSON {
    static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(date.formatted(.iso8601.year().month().day()
                .timeZone(separator: .omitted).time(includingFractionalSeconds: true)))
        }
        return encoder
    }()

    static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            guard let date = parseDate(text) else {
                throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Not a date: \(text)"))
            }
            return date
        }
        return decoder
    }()

    /// .NET writes up to seven fractional digits and, for an unspecified-kind
    /// DateTime, no zone at all. Foundation's ISO 8601 parsers accept neither,
    /// so the string is normalized to milliseconds and UTC first.
    static func parseDate(_ text: String) -> Date? {
        var value = text
        var zone = "Z"
        if value.hasSuffix("Z") {
            value.removeLast()
        } else if let match = value.firstMatch(of: /[+-]\d{2}:\d{2}$/) {
            zone = String(match.output)
            value.removeSubrange(match.range)
        }

        var fraction = "000"
        if let dot = value.firstIndex(of: ".") {
            fraction = String(value[value.index(after: dot)...].prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)
            value = String(value[..<dot])
        }

        return try? Date("\(value).\(fraction)\(zone)", strategy: .iso8601.year().month().day()
            .timeZone(separator: .colon).time(includingFractionalSeconds: true))
    }

    @concurrent static func decode<T: Decodable & Sendable>(_ type: T.Type, from data: Data) async throws -> T {
        try decoder.decode(type, from: data)
    }
}

/// The one way the app talks to an instance. Every request resolves against the
/// instance's base address, carries the session token, and names this client's
/// hub connection so the server can leave it out of its own change broadcasts.
final class APIClient {
    let baseURL: URL
    private let session: URLSession
    private let cache: ResponseCache

    /// The `solo_` session token, once signed in.
    var token: String?
    /// The live hub connection, when there is one (see HubClient).
    var connectionId: String?
    /// Raised when a request carrying the token comes back 401: the session ended somewhere else.
    var onUnauthorized: (() -> Void)?
    /// Raised as requests succeed or fail to connect, for the connection indicator.
    var onReachabilityChanged: ((Bool) -> Void)?

    init(baseURL: URL, token: String? = nil) {
        // A trailing slash makes relative composition behave: without it the
        // base's last path segment is discarded.
        self.baseURL = baseURL.absoluteString.hasSuffix("/") ? baseURL : URL(string: baseURL.absoluteString + "/")!
        self.token = token
        let configuration = URLSessionConfiguration.default
        configuration.timeoutIntervalForRequest = 20
        configuration.waitsForConnectivity = false
        configuration.httpAdditionalHeaders = ["Accept": "application/json"]
        session = URLSession(configuration: configuration)
        cache = ResponseCache(namespace: self.baseURL.absoluteString)
    }

    func url(_ path: String) -> URL {
        URL(string: path.hasPrefix("/") ? String(path.dropFirst()) : path, relativeTo: baseURL)!.absoluteURL
    }

    var host: String { baseURL.host() ?? baseURL.absoluteString }

    // MARK: Verbs

    func get<T: Decodable & Sendable>(_ path: String, as type: T.Type = T.self) async throws -> T {
        let data = try await data(for: request("GET", path), cacheKey: path)
        return try await SoloJSON.decode(type, from: data)
    }

    func send<T: Decodable & Sendable>(_ method: String, _ path: String, body: (any Encodable & Sendable)? = nil,
                                       as type: T.Type = T.self) async throws -> T {
        let data = try await data(for: request(method, path, body: body))
        return try await SoloJSON.decode(type, from: data)
    }

    func send(_ method: String, _ path: String, body: (any Encodable & Sendable)? = nil) async throws {
        _ = try await data(for: request(method, path, body: body))
    }

    func bytes(_ path: String) async throws -> (Data, String?) {
        var request = request("GET", path)
        request.setValue("*/*", forHTTPHeaderField: "Accept")
        let (data, response) = try await perform(request)
        return (data, response.value(forHTTPHeaderField: "Content-Type"))
    }

    func upload<T: Decodable & Sendable>(_ method: String = "POST", _ path: String, data body: Data, contentType: String,
                                         as type: T.Type = T.self) async throws -> T {
        var request = request(method, path)
        request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        return try await SoloJSON.decode(type, from: try await data(for: request))
    }

    /// A multipart form with one file part, which is how images are uploaded.
    func uploadFile<T: Decodable & Sendable>(_ path: String, field: String = "file", fileName: String, contentType: String,
                                             data file: Data, as type: T.Type = T.self) async throws -> T {
        let boundary = "solo-\(UUID().uuidString)"
        var body = Data()
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(field)\"; filename=\"\(fileName)\"\r\n".data(using: .utf8)!)
        body.append("Content-Type: \(contentType)\r\n\r\n".data(using: .utf8)!)
        body.append(file)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)
        return try await upload("POST", path, data: body, contentType: "multipart/form-data; boundary=\(boundary)")
    }

    func clearCache() async {
        await cache.clear()
    }

    // MARK: Plumbing

    func request(_ method: String, _ path: String, body: (any Encodable & Sendable)? = nil) -> URLRequest {
        var request = URLRequest(url: url(path))
        request.httpMethod = method
        if let token {
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let connectionId {
            request.setValue(connectionId, forHTTPHeaderField: "X-Connection-Id")
        }
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try? SoloJSON.encoder.encode(body)
        }
        return request
    }

    /// `cacheKey` is set for reads: their last good answer is kept on disk and
    /// stands in when the instance can't be reached, so the app opens to what
    /// it last showed rather than to nothing.
    private func data(for request: URLRequest, cacheKey: String? = nil) async throws -> Data {
        do {
            let (data, _) = try await perform(request)
            if let cacheKey {
                await cache.store(data, for: cacheKey)
            }
            return data
        } catch let error as ConnectionError {
            if let cacheKey, let cached = await cache.load(cacheKey) {
                return cached
            }
            throw error
        }
    }

    private func perform(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch let error as URLError where error.code != .cancelled {
            onReachabilityChanged?(false)
            throw ConnectionError(host: host)
        }

        guard let http = response as? HTTPURLResponse else {
            throw ConnectionError(host: host)
        }
        onReachabilityChanged?(true)

        guard (200..<300).contains(http.statusCode) else {
            let problem = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            let retry = http.value(forHTTPHeaderField: "Retry-After").flatMap(TimeInterval.init)
            let error = APIError(status: http.statusCode, title: problem?["title"] as? String,
                                 detail: problem?["detail"] as? String, retryAfter: retry)
            if error.isUnauthorized, request.value(forHTTPHeaderField: "Authorization") != nil {
                onUnauthorized?()
            }
            throw error
        }
        return (data, http)
    }
}

/// The last good answer to each read, on disk. Not a sync engine: it only keeps
/// the app legible while the instance is out of reach.
actor ResponseCache {
    private let directory: URL

    init(namespace: String) {
        let root = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        directory = root.appending(path: "responses/\(Self.fileName(namespace))", directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    func store(_ data: Data, for key: String) {
        try? data.write(to: file(key), options: .atomic)
    }

    func load(_ key: String) -> Data? {
        try? Data(contentsOf: file(key))
    }

    func clear() {
        try? FileManager.default.removeItem(at: directory)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    private func file(_ key: String) -> URL {
        directory.appending(path: Self.fileName(key))
    }

    private static func fileName(_ text: String) -> String {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in text.utf8 {
            hash = (hash ^ UInt64(byte)) &* 0x100_0000_01b3
        }
        return String(hash, radix: 16)
    }
}
