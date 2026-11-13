#if DEBUG
import Foundation
import WebKit

/// Debug builds only, for testing against a plain-HTTP instance on the local
/// network with no Google sign-in: a `solo_` session minted on the instance,
/// so the page starts signed in where Google's sign-in can't run (a plain-HTTP
/// instance). The token and the user come from the git-ignored
/// `ios/dev.xcconfig` (see the README), and belong to the instance built in
/// with `SOLO_INSTANCE_URL`, so only the local origin that proxies it gets
/// them.
enum DevShim {
    static func install(in controller: WKUserContentController, sessionFor instance: URL?) {
        controller.addUserScript(WKUserScript(
            source: instance.flatMap(session(for:)) ?? "",
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true))
    }

    private static func session(for instance: URL) -> String? {
        let info = Bundle.main.infoDictionary ?? [:]
        guard let token = info["SoloDevSessionToken"] as? String, token.hasPrefix("solo_"),
              let user = (info["SoloDevSessionUser"] as? String).flatMap(base64URLDecoded),
              let values = try? JSONSerialization.data(withJSONObject: [
                  "solo.session.token": token, "solo.session.user": user,
              ]),
              let json = String(data: values, encoding: .utf8),
              let scheme = instance.scheme, let host = instance.host
        else { return nil }
        let origin = "\(scheme)://\(host)" + (instance.port.map { ":\($0)" } ?? "")

        // Kept once set, so signing out and back in on the device sticks.
        return """
            if (location.origin === '\(origin)' && !localStorage.getItem('solo.session.token')) {
                for (const [key, value] of Object.entries(\(json))) localStorage.setItem(key, value);
            }

            """
    }

    // base64url, because an xcconfig value ends at `//`, which plain base64 can contain.
    private static func base64URLDecoded(_ value: String) -> String? {
        var base64 = value.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        return Data(base64Encoded: base64).flatMap { String(data: $0, encoding: .utf8) }
    }
}
#endif
