import AuthenticationServices
import CryptoKit
import UIKit

/// The desktop app's sign-in, for the iPad: Google won't run its sign-in in an
/// app's web view, so the instance's own `/desktop-auth` page runs in the
/// system sign-in sheet and returns through the app's URL scheme. The code it
/// returns is redeemed, with the PKCE verifier only this object holds, for a
/// `solo_` session the page stores like the desktop app's.
@MainActor
final class AppSignIn: NSObject, ASWebAuthenticationPresentationContextProviding {
    struct Session {
        /// Both already JSON, ready to pass to the page.
        let token: String
        let user: String
    }

    struct Failure: LocalizedError {
        let errorDescription: String?
    }

    nonisolated private static let callbackScheme = "com.hunterahlquist.solo"

    private let instance: URL
    private let window: UIWindow
    private var sheet: ASWebAuthenticationSession?

    init(instance: URL, window: UIWindow) {
        self.instance = instance
        self.window = window
    }

    /// Nil when the person closes the sheet.
    func run() async throws -> Session? {
        let verifier = Self.base64URL(SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) })
        let challenge = Self.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))

        let flow = try await post("api/auth/desktop/start-app", [
            "description": "Solo for \(UIDevice.current.model)",
            "codeChallenge": challenge,
        ])
        guard let state = flow["state"] as? String else { throw Self.unexpected }

        var page = URLComponents(url: instance.appendingPathComponent("desktop-auth"), resolvingAgainstBaseURL: false)!
        page.queryItems = [URLQueryItem(name: "state", value: state)]
        guard let callback = try await present(page.url!) else { return nil }

        let query = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        guard query.first(where: { $0.name == "state" })?.value == state,
              let code = query.first(where: { $0.name == "code" })?.value
        else { throw Self.unexpected }

        let session = try await post("api/auth/desktop/redeem", [
            "state": state,
            "authorizationCode": code,
            "codeVerifier": verifier,
        ])
        guard let token = session["token"] as? String, let user = session["user"] as? [String: Any] else {
            throw Self.unexpected
        }

        return Session(token: try Self.json(token), user: try Self.json(user))
    }

    func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
        window
    }

    private func present(_ url: URL) async throws -> URL? {
        try await withCheckedThrowingContinuation { continuation in
            let completion: (URL?, Error?) -> Void = { callback, error in
                if let callback {
                    continuation.resume(returning: callback)
                } else if (error as? ASWebAuthenticationSessionError)?.code == .canceledLogin {
                    continuation.resume(returning: nil)
                } else {
                    continuation.resume(throwing: error ?? Self.unexpected)
                }
            }

            let sheet: ASWebAuthenticationSession
            if #available(iOS 17.4, *) {
                sheet = ASWebAuthenticationSession(url: url, callback: .customScheme(Self.callbackScheme), completionHandler: completion)
            } else {
                sheet = ASWebAuthenticationSession(url: url, callbackURLScheme: Self.callbackScheme, completionHandler: completion)
            }
            sheet.presentationContextProvider = self
            self.sheet = sheet
            // A sheet that doesn't start never calls back.
            if !sheet.start() {
                continuation.resume(throwing: Failure(errorDescription: "Couldn't open the sign-in sheet."))
            }
        }
    }

    private func post(_ path: String, _ body: [String: String]) async throws -> [String: Any] {
        var request = URLRequest(url: instance.appendingPathComponent(path))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch {
            throw Failure(errorDescription: "Couldn't reach \(instance.host ?? "the instance").")
        }

        let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        guard let status = (response as? HTTPURLResponse)?.statusCode, (200..<300).contains(status) else {
            // A ProblemDetails body says why, e.g. too many pending sign-ins.
            throw Failure(errorDescription: object["detail"] as? String ?? object["title"] as? String
                ?? "Sign-in failed. Try again.")
        }

        return object
    }

    nonisolated private static let unexpected = Failure(errorDescription: "Sign-in failed. Try again.")

    private static func json(_ value: Any) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: value, options: .fragmentsAllowed), as: UTF8.self)
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}
