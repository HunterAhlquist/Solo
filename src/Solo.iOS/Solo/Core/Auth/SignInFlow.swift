import AuthenticationServices
import CryptoKit
import Network
import UIKit

/// Signs in through the instance's own `/desktop-auth` page, shown in the
/// system sign-in sheet. Whatever the provider is, the page runs its flow on
/// the instance's registered origin and hands back a one-time code, which is
/// redeemed with a PKCE verifier only this object holds for a `solo_` session.
///
/// Two ways home, because instances differ in age:
/// - `start-app` returns through the app's private URL scheme. Newer instances.
/// - `start` returns to a loopback port, as the desktop app does. Any instance
///   that supports the desktop app at all; the app listens on 127.0.0.1 for
///   the one request while the sheet is up.
final class SignInFlow: NSObject, ASWebAuthenticationPresentationContextProviding {
    struct Failure: LocalizedError {
        var errorDescription: String?
    }

    private static let callbackScheme = "com.hunterahlquist.solo"
    private static let unexpected = Failure(errorDescription: "Sign-in failed. Try again.")

    private let api: APIClient
    private var sheet: ASWebAuthenticationSession?
    private var pending: CheckedContinuation<URL?, Error>?

    init(api: APIClient) {
        self.api = api
    }

    /// Nil when the person closes the sheet.
    func run() async throws -> SessionGrant? {
        let verifier = Self.base64URL(Data((0..<32).map { _ in UInt8.random(in: .min ... .max) }))
        let challenge = Self.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
        let description = "Solo for \(UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone")"

        var listener: LoopbackListener?
        let flow: DesktopAuthFlow
        do {
            flow = try await api.send("POST", "api/auth/desktop/start-app",
                                      body: ["description": description, "codeChallenge": challenge])
        } catch let error as APIError where error.isNotFound || error.status == 405 {
            // An instance from before the app flow: come home over loopback.
            let loopback = try await LoopbackListener.start()
            listener = loopback
            flow = try await api.send("POST", "api/auth/desktop/start", body: LoopbackStart(
                description: description, codeChallenge: challenge, loopbackPort: Int(loopback.port)))
        }
        defer { listener?.stop() }

        var page = URLComponents(url: api.url("desktop-auth"), resolvingAgainstBaseURL: false)!
        page.queryItems = [URLQueryItem(name: "state", value: flow.state)]

        let callback = try await callback(from: page.url!, listener: listener)
        guard let callback else { return nil }

        let query = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard query.first(where: { $0.name == "state" })?.value == flow.state,
              let code = query.first(where: { $0.name == "code" })?.value
        else { throw Self.unexpected }

        return try await api.send("POST", "api/auth/desktop/redeem",
                                  body: ["state": flow.state, "authorizationCode": code, "codeVerifier": verifier])
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        return scenes.flatMap(\.windows).first(where: \.isKeyWindow) ?? ASPresentationAnchor(windowScene: scenes[0])
    }

    /// Shows the sheet and waits for the way home, whichever way that is: the
    /// sheet's own scheme callback, or the loopback listener hearing the
    /// browser arrive. First one wins; the sheet is closed either way.
    private func callback(from page: URL, listener: LoopbackListener?) async throws -> URL? {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL?, Error>) in
            pending = continuation

            // The system doesn't promise which thread this arrives on.
            let sheet = ASWebAuthenticationSession(url: page, callback: .customScheme(Self.callbackScheme)) {
                @Sendable callback, error in
                let cancelled = (error as? ASWebAuthenticationSessionError)?.code == .canceledLogin
                Task { @MainActor in
                    if let callback {
                        self.finish(.success(callback))
                    } else if cancelled {
                        self.finish(.success(nil))
                    } else {
                        self.finish(.failure(error ?? Self.unexpected))
                    }
                }
            }
            sheet.presentationContextProvider = self
            self.sheet = sheet

            if let listener {
                Task { @MainActor in
                    if let url = try? await listener.callback() {
                        self.finish(.success(url))
                    }
                }
            }

            // A sheet that doesn't start never calls back.
            if !sheet.start() {
                finish(.failure(Failure(errorDescription: "Couldn't open the sign-in sheet.")))
            }
        }
    }

    private func finish(_ result: Result<URL?, Error>) {
        guard let continuation = pending else { return }
        pending = nil
        sheet?.cancel()
        sheet = nil
        continuation.resume(with: result)
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

nonisolated private struct LoopbackStart: Encodable, Sendable {
    var description: String
    var codeChallenge: String
    var loopbackPort: Int
}

/// Answers exactly one HTTP request on 127.0.0.1: the browser arriving at
/// `/desktop-auth/callback?code=…&state=…`. PKCE is what makes this safe to
/// leave open to anything else on the device: a code without the verifier
/// redeems nothing.
nonisolated final class LoopbackListener: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "solo.signin.loopback")
    private var waiter: CheckedContinuation<URL?, Error>?
    private var result: Result<URL?, Error>?

    private(set) var port: UInt16 = 0

    private init(listener: NWListener) {
        self.listener = listener
    }

    static func start() async throws -> LoopbackListener {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        parameters.acceptLocalOnly = true
        let instance = LoopbackListener(listener: try NWListener(using: parameters))
        try await instance.listen()
        return instance
    }

    private func listen() async throws {
        try await withCheckedThrowingContinuation { (ready: CheckedContinuation<Void, Error>) in
            nonisolated(unsafe) var resumed = false // only touched on `queue`
            listener.stateUpdateHandler = { [weak self] state in
                guard let self, !resumed else { return }
                switch state {
                case .ready:
                    resumed = true
                    self.port = self.listener.port?.rawValue ?? 0
                    ready.resume()
                case .failed(let error):
                    resumed = true
                    ready.resume(throwing: error)
                default:
                    break
                }
            }
            listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
            listener.start(queue: queue)
        }
    }

    /// The callback URL, once the browser arrives. Nil if stopped first.
    func callback() async throws -> URL? {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                queue.async {
                    if let result = self.result {
                        continuation.resume(with: result)
                    } else {
                        self.waiter = continuation
                    }
                }
            }
        } onCancel: {
            finish(.success(nil))
        }
    }

    func stop() {
        listener.cancel()
        finish(.success(nil))
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, _, _ in
            guard let self, let data, let request = String(data: data, encoding: .utf8),
                  let line = request.split(separator: "\r\n").first else {
                connection.cancel()
                return
            }

            // "GET /desktop-auth/callback?code=…&state=… HTTP/1.1"
            let parts = line.split(separator: " ")
            let target = parts.count > 1 ? String(parts[1]) : ""
            let isCallback = target.hasPrefix("/desktop-auth/callback")
            let page = isCallback
                ? "<!doctype html><meta name=viewport content='width=device-width'><title>Solo</title>"
                    + "<body style='font:17px -apple-system;text-align:center;padding:3em 1em'>Signed in. Returning to Solo…"
                : "Not found"
            let response = "HTTP/1.1 \(isCallback ? "200 OK" : "404 Not Found")\r\nContent-Type: text/html; charset=utf-8\r\n"
                + "Content-Length: \(page.utf8.count)\r\nConnection: close\r\n\r\n\(page)"
            connection.send(content: Data(response.utf8), completion: .contentProcessed { _ in connection.cancel() })

            if isCallback, let url = URL(string: "http://127.0.0.1:\(self.port)\(target)") {
                self.finish(.success(url))
            }
        }
    }

    private func finish(_ result: Result<URL?, Error>) {
        queue.async {
            guard self.result == nil else { return }
            self.result = result
            self.waiter?.resume(with: result)
            self.waiter = nil
        }
    }
}
