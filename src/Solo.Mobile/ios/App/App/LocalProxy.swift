import Foundation
import Network

/// The local origin the app loads an instance from, as the desktop app does
/// (src/Solo.Desktop/main/server.js): a loopback HTTP server that forwards
/// every path, the client's own files included, to the instance, so the web
/// view runs the client the instance serves, same-origin.
///
/// Why not load the instance's own address: a WKWebView only gets service
/// workers — which the client's offline mode, and so a vault kept offline,
/// depends on — on an App-Bound Domain, and an app lists those in Info.plist,
/// fixed when it's built. Every instance is somewhere else; `localhost` is one
/// fixed name. And http://localhost is a secure context even when the
/// instance is plain http on the LAN.
///
/// Each instance gets its own port (`InstancePorts`), so its origin, and with
/// it its storage and session, stays its own across launches.
///
/// HTTP requests go upstream through URLSession and stream back as chunked
/// responses. A WebSocket (SignalR's hub) is carried as a raw TCP or TLS
/// tunnel once its head is rewritten, as http-proxy does on the desktop.
/// A network-path change, or the app returning to the foreground, resets the
/// upstream connections and restarts the listener if it died, so nothing
/// waits on a connection that went away with the old network.
final class LocalProxy: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let port: UInt16
    /// The instance, with its path when it's hosted under one.
    let target: URL
    /// What the web view loads: the local origin, with the instance's path.
    var pageURL: URL { URL(string: "http://localhost:\(port)\(target.path)/")! }

    private let queue = DispatchQueue(label: "solo.local-proxy")
    private var listener: NWListener?
    private var session: URLSession?
    private var exchanges: [Int: Exchange] = [:]
    private var tunnels: [ObjectIdentifier: (NWConnection, NWConnection)] = [:]
    private let monitor = NWPathMonitor()
    private var lastPath: NWPath?
    private var stopped = false

    private struct Exchange {
        let connection: NWConnection
        let method: String
        let navigation: Bool
        var bodyless = false
    }

    init(port: UInt16, target: URL) {
        self.port = port
        self.target = target
        super.init()
    }

    /// Starts listening, and waits (briefly) until it does, so the web view
    /// never loads before anything answers.
    func start() throws {
        try listen(wait: true)
        monitor.pathUpdateHandler = { [weak self] path in self?.pathChanged(path) }
        monitor.start(queue: queue)
    }

    func stop() {
        queue.async { [self] in
            stopped = true
            monitor.cancel()
            listener?.cancel()
            listener = nil
            resetUpstream()
        }
    }

    /// For the app returning to the foreground: iOS may have torn down the
    /// listener's socket and the upstream connections while it was suspended.
    func recover() {
        queue.async { [self] in
            guard !stopped else { return }
            resetUpstream()
            if listener?.state != .ready { try? listen(wait: false) }
        }
    }

    // MARK: Listening

    /// `wait` only from outside `queue`, where the state handler runs: start()
    /// waits so the web view never loads before anything answers; a restart
    /// from a failure, a network change or the foreground doesn't need to.
    private func listen(wait: Bool) throws {
        listener?.cancel()
        // Bound to 127.0.0.1 itself: requiredInterfaceType alone still binds
        // every address, and a device on the same network could then use the
        // proxy to reach an instance only this one can (over a VPN, say). The
        // web view's "localhost" falls back from ::1 to 127.0.0.1 at once.
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        parameters.allowLocalEndpointReuse = true
        let listener = try NWListener(using: parameters)
        let ready = DispatchSemaphore(value: 0)
        var failure: Error?
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            switch state {
            case .ready:
                ready.signal()
            case .failed(let error):
                failure = error
                ready.signal()
                // Not on start: a failure later (a network change, a
                // suspension) is retried rather than leaving the app blank.
                self?.queue.asyncAfter(deadline: .now() + 0.5) {
                    guard let self, !self.stopped, self.listener === listener else { return }
                    try? self.listen(wait: false)
                }
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
        self.listener = listener
        listener.start(queue: queue)
        guard wait else { return }
        _ = ready.wait(timeout: .now() + 2)
        if let failure { throw failure }
    }

    private func pathChanged(_ path: NWPath) {
        defer { lastPath = path }
        guard let lastPath, !stopped else { return }
        // A different network, or the network coming back: connections made on
        // the old one may never answer, so start upstream afresh.
        if path.status != lastPath.status || path.availableInterfaces.map(\.name) != lastPath.availableInterfaces.map(\.name) {
            resetUpstream()
            if listener?.state != .ready { try? listen(wait: false) }
        }
    }

    /// Drops every upstream connection: open requests fail (the client retries
    /// and reconnects), tunnels close (SignalR reconnects), and the next request
    /// gets a new URLSession rather than a pooled connection on a dead network.
    private func resetUpstream() {
        session?.invalidateAndCancel()
        session = nil
        for (client, upstream) in tunnels.values {
            client.cancel()
            upstream.cancel()
        }
        tunnels.removeAll()
    }

    private var upstream: URLSession {
        if let session { return session }
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false
        config.httpCookieAcceptPolicy = .never
        config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        config.timeoutIntervalForRequest = 60
        config.waitsForConnectivity = false
        let delegateQueue = OperationQueue()
        delegateQueue.underlyingQueue = queue
        delegateQueue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: config, delegate: self, delegateQueue: delegateQueue)
        self.session = session
        return session
    }

    // MARK: Requests

    private func accept(_ connection: NWConnection) {
        // Belt and braces with the bind above: nothing but loopback is served.
        guard case .hostPort(let host, _) = connection.endpoint, Self.isLoopback(host) else {
            return connection.cancel()
        }
        connection.start(queue: queue)
        receiveHead(connection, buffer: Data())
    }

    private func receiveHead(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            if let end = buffer.range(of: Data("\r\n\r\n".utf8)) {
                let head = String(decoding: buffer[..<end.lowerBound], as: UTF8.self)
                self.handle(connection, head: head, rest: Data(buffer[end.upperBound...]))
            } else if complete || error != nil || buffer.count > 1_000_000 {
                connection.cancel()
            } else {
                self.receiveHead(connection, buffer: buffer)
            }
        }
    }

    private func handle(_ connection: NWConnection, head: String, rest: Data) {
        var lines = head.components(separatedBy: "\r\n")
        let requestLine = lines.removeFirst().split(separator: " ", maxSplits: 2)
        guard requestLine.count >= 2 else { return connection.cancel() }
        let method = String(requestLine[0]), path = String(requestLine[1])
        var headers: [(name: String, value: String)] = []
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers.append((String(line[..<colon]).trimmingCharacters(in: .whitespaces),
                            String(line[line.index(after: colon)...]).trimmingCharacters(in: .whitespaces)))
        }
        func header(_ name: String) -> String? {
            headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
        }

        // Only the app's own page may talk to this server. A remote page can
        // reach 127.0.0.1 via DNS rebinding, but then Host carries its name —
        // the same check as the desktop's, on requests and upgrades alike.
        let host = (header("Host") ?? "").lowercased()
        guard host == "localhost:\(port)" || host == "127.0.0.1:\(port)" else {
            return respond(connection, status: "403 Forbidden", type: "text/plain", body: "")
        }

        if header("Upgrade")?.lowercased() == "websocket" {
            return tunnel(connection, method: method, path: path, headers: headers, rest: rest)
        }

        let length = Int(header("Content-Length") ?? "0") ?? 0
        if rest.count < length {
            connection.receive(minimumIncompleteLength: 1, maximumLength: length - rest.count) { [weak self] data, _, _, error in
                guard let data, error == nil else { return connection.cancel() }
                self?.handle(connection, head: head, rest: rest + data)
            }
            return
        }

        guard let url = URL(string: path, relativeTo: targetOrigin)?.absoluteURL else {
            return respond(connection, status: "400 Bad Request", type: "text/plain", body: "")
        }
        var request = URLRequest(url: url)
        request.httpMethod = method
        for (name, value) in headers where !Self.hopByHop.contains(name.lowercased()) {
            switch name.lowercased() {
            case "origin": request.setValue(targetOrigin.absoluteString.trimmingSuffix("/"), forHTTPHeaderField: name)
            case "referer": request.setValue(rewrite(value), forHTTPHeaderField: name)
            default: request.addValue(value, forHTTPHeaderField: name)
            }
        }
        if length > 0 { request.httpBody = rest.prefix(length) }

        let navigation = method == "GET" &&
            (header("Sec-Fetch-Mode") == "navigate" || (header("Accept") ?? "").contains("text/html"))
        let task = upstream.dataTask(with: request)
        exchanges[task.taskIdentifier] = Exchange(connection: connection, method: method, navigation: navigation)
        task.resume()
    }

    private static func isLoopback(_ host: NWEndpoint.Host) -> Bool {
        switch host {
        case .ipv4(let address): return address.isLoopback
        case .ipv6(let address): return address.isLoopback
        case .name(let name, _): return name == "localhost"
        @unknown default: return false
        }
    }

    private static let hopByHop: Set<String> = [
        "host", "connection", "keep-alive", "proxy-connection", "te", "upgrade",
        "accept-encoding", "content-length", "transfer-encoding",
    ]

    private var targetOrigin: URL {
        var components = URLComponents(url: target, resolvingAgainstBaseURL: false)!
        components.path = "/"
        return components.url!
    }

    private var localOrigin: String { "http://localhost:\(port)" }

    private func rewrite(_ value: String) -> String {
        value.replacingOccurrences(of: localOrigin, with: targetOrigin.absoluteString.trimmingSuffix("/"))
    }

    // MARK: Responses

    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil) // the page follows redirects itself
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse, var exchange = exchanges[dataTask.taskIdentifier] else {
            return completionHandler(.cancel)
        }
        // HEAD, 1xx, 204 and 304 have no body, so no chunked framing either.
        exchange.bodyless = exchange.method == "HEAD" || http.statusCode < 200 || [204, 304].contains(http.statusCode)
        exchanges[dataTask.taskIdentifier] = exchange

        var head = "HTTP/1.1 \(http.statusCode) \(HTTPURLResponse.localizedString(forStatusCode: http.statusCode))\r\n"
        // URLSession has already decoded the body, so its length and encoding
        // no longer describe what is sent.
        let drop: Set<String> = ["content-length", "content-encoding", "transfer-encoding", "connection", "keep-alive"]
        for (key, value) in http.allHeaderFields {
            guard let name = key as? String, let value = value as? String, !drop.contains(name.lowercased()) else { continue }
            let sent = name.lowercased() == "location"
                ? value.replacingOccurrences(of: targetOrigin.absoluteString.trimmingSuffix("/"), with: localOrigin)
                : value
            head += "\(name): \(sent)\r\n"
        }
        head += exchange.bodyless ? "Content-Length: 0\r\nConnection: close\r\n\r\n"
                                  : "Transfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
        exchange.connection.send(content: Data(head.utf8), completion: .idempotent)
        completionHandler(exchange.bodyless ? .cancel : .allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard let exchange = exchanges[dataTask.taskIdentifier], !exchange.bodyless, !data.isEmpty else { return }
        var chunk = Data(String(data.count, radix: 16).utf8) + Data("\r\n".utf8)
        chunk.append(data)
        chunk.append(Data("\r\n".utf8))
        exchange.connection.send(content: chunk, completion: .idempotent)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let exchange = exchanges.removeValue(forKey: task.taskIdentifier) else { return }
        let connection = exchange.connection
        if task.response == nil {
            // The instance never answered. A page load gets something to read;
            // a fetch gets the JSON 502 the client already treats as offline.
            if exchange.navigation {
                let nonce = Self.nonce
                respond(connection, status: "502 Bad Gateway", type: "text/html; charset=utf-8",
                        body: Self.unreachablePage(host: target.host ?? target.absoluteString, nonce: nonce), nonce: nonce)
            } else {
                respond(connection, status: "502 Bad Gateway", type: "application/json",
                        body: "{\"error\":\"Solo instance unreachable\"}")
            }
            return
        }
        if exchange.bodyless {
            connection.send(content: nil, isComplete: true, completion: .contentProcessed { _ in connection.cancel() })
            return
        }
        connection.send(content: Data("0\r\n\r\n".utf8), completion: .contentProcessed { _ in connection.cancel() })
    }

    private func respond(_ connection: NWConnection, status: String, type: String, body: String, nonce: String? = nil) {
        var head = "HTTP/1.1 \(status)\r\nContent-Type: \(type)\r\nContent-Length: \(body.utf8.count)\r\n" +
            "Cache-Control: no-store\r\nConnection: close\r\n"
        if let nonce {
            head += "Content-Security-Policy: default-src 'none'; style-src 'nonce-\(nonce)'; script-src 'nonce-\(nonce)'; " +
                "base-uri 'none'; form-action 'none'; frame-ancestors 'none'\r\n"
        }
        connection.send(content: Data((head + "\r\n" + body).utf8), completion: .contentProcessed { _ in connection.cancel() })
    }

    // MARK: WebSockets

    /// Carries a WebSocket to the instance byte for byte: the upgrade's head is
    /// rewritten for the instance (Host, Origin) and replayed on a TCP or TLS
    /// connection, and from then on both sides are piped to each other.
    private func tunnel(_ client: NWConnection, method: String, path: String,
                        headers: [(name: String, value: String)], rest: Data) {
        guard let host = target.host else { return client.cancel() }
        let secure = target.scheme == "https"
        let port = target.port ?? (secure ? 443 : 80)
        let upstream = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: UInt16(port))!,
                                    using: secure ? .tls : .tcp)
        let authority = target.port.map { "\(host):\($0)" } ?? host
        var head = "\(method) \(path) HTTP/1.1\r\n"
        for (name, value) in headers {
            switch name.lowercased() {
            case "host": head += "Host: \(authority)\r\n"
            case "origin": head += "Origin: \(targetOrigin.absoluteString.trimmingSuffix("/"))\r\n"
            default: head += "\(name): \(value)\r\n"
            }
        }
        head += "\r\n"

        let key = ObjectIdentifier(client)
        tunnels[key] = (client, upstream)
        let close: @Sendable () -> Void = { [weak self] in
            client.cancel()
            upstream.cancel()
            self?.queue.async { self?.tunnels.removeValue(forKey: key) }
        }
        upstream.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                upstream.send(content: Data(head.utf8) + rest, completion: .contentProcessed { error in
                    guard error == nil, let self else { return close() }
                    self.pipe(from: client, to: upstream, close: close)
                    self.pipe(from: upstream, to: client, close: close)
                })
            case .failed, .cancelled:
                close()
            default:
                break
            }
        }
        upstream.start(queue: queue)
    }

    private func pipe(from source: NWConnection, to sink: NWConnection, close: @escaping @Sendable () -> Void) {
        source.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, complete, error in
            if let data, !data.isEmpty {
                sink.send(content: data, completion: .contentProcessed { sendError in
                    if sendError != nil { return close() }
                    if complete { return close() }
                    self?.pipe(from: source, to: sink, close: close)
                })
            } else if complete || error != nil {
                close()
            } else {
                self?.pipe(from: source, to: sink, close: close)
            }
        }
    }

    // MARK: The unreachable page

    private static var nonce: String {
        var bytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
    }

    /// What the app shows when the instance can't be reached and the client's
    /// offline mode hasn't cached it — its service worker answers the page load
    /// before it gets here otherwise. The same page as the desktop app's.
    private static func unreachablePage(host: String, nonce: String) -> String {
        let safeHost = host.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
        return """
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
            <title>Can't reach the instance</title>
            <style nonce="\(nonce)">
            :root { color-scheme: light dark; font-family: -apple-system, system-ui, sans-serif; }
            body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: Canvas; color: CanvasText; }
            main { max-width: 26rem; padding: 2rem; text-align: center; }
            h1 { font-size: 1.25rem; margin: 0 0 .75rem; }
            p { margin: 0 0 1.5rem; line-height: 1.5; opacity: .8; }
            button { font: inherit; padding: .6rem 1.1rem; margin: .25rem; border-radius: 10px; border: 1px solid; background: transparent; color: inherit; }
            </style></head><body><main>
            <h1>Can't reach the instance</h1>
            <p>Solo couldn't reach \(safeHost). Check your connection, or that the instance is running, then try again.</p>
            <button id="retry" type="button">Try again</button>
            <button id="instances" type="button" hidden>Instances</button>
            </main><script nonce="\(nonce)">
            document.getElementById('retry').addEventListener('click', () => location.reload());
            const instances = document.getElementById('instances');
            if (window.soloApp && window.soloApp.openInstances) {
              instances.hidden = false;
              instances.addEventListener('click', () => window.soloApp.openInstances());
            }
            </script></body></html>
            """
    }
}

private extension String {
    func trimmingSuffix(_ suffix: String) -> String {
        hasSuffix(suffix) ? String(dropLast(suffix.count)) : self
    }
}

/// One local port per instance, kept for good: the port is part of the page's
/// origin, and the origin holds the instance's storage and session. Ports are
/// handed out from 41321 up, like the desktop app's.
enum InstancePorts {
    private static let key = "solo.instances.ports"
    static let first: UInt16 = 41321

    static func port(for instance: URL) -> UInt16 {
        var ports = (UserDefaults.standard.dictionary(forKey: key) as? [String: Int]) ?? [:]
        if let port = ports[instance.absoluteString] { return UInt16(port) }
        var port = Int(first)
        let taken = Set(ports.values)
        while taken.contains(port) { port += 1 }
        ports[instance.absoluteString] = port
        UserDefaults.standard.set(ports, forKey: key)
        return UInt16(port)
    }

    /// When the port can't be had (another app is listening on it), the
    /// instance moves to the next free one, at the cost of signing in again.
    static func reassign(_ instance: URL) -> UInt16 {
        var ports = (UserDefaults.standard.dictionary(forKey: key) as? [String: Int]) ?? [:]
        let current = ports[instance.absoluteString] ?? Int(first)
        let taken = Set(ports.values)
        var port = current + 1
        while taken.contains(port) { port += 1 }
        ports[instance.absoluteString] = port
        UserDefaults.standard.set(ports, forKey: key)
        return UInt16(port)
    }
}
