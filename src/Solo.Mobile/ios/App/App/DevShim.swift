#if DEBUG
import Foundation
import WebKit

/// Debug builds only, for testing against a plain-HTTP instance on the local
/// network with no Google sign-in:
///
/// - `crypto.randomUUID()` exists only on secure pages, and Design Docs needs
///   it for every stroke and sync packet. `getRandomValues` works anywhere.
/// - A `solo_` session minted on the instance, so the page starts signed in
///   where Google's sign-in can't run (a plain-HTTP origin). The token and
///   the user come from the git-ignored `ios/dev.xcconfig` (see the README).
enum DevShim {
    static func install(in controller: WKUserContentController) {
        controller.addUserScript(WKUserScript(
            source: randomUUID + session(),
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true))
    }

    private static let randomUUID = """
        if (!crypto.randomUUID) {
            Object.defineProperty(crypto, 'randomUUID', { value: () => {
                const b = crypto.getRandomValues(new Uint8Array(16));
                b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
                const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
                return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
            } });
        }

        """

    private static func session() -> String {
        let info = Bundle.main.infoDictionary ?? [:]
        guard let token = info["SoloDevSessionToken"] as? String, token.hasPrefix("solo_"),
              let user = (info["SoloDevSessionUser"] as? String).flatMap(base64URLDecoded),
              let values = try? JSONSerialization.data(withJSONObject: [
                  "solo.session.token": token, "solo.session.user": user,
              ]),
              let json = String(data: values, encoding: .utf8)
        else { return "" }

        // Kept once set, so signing out and back in on the device sticks.
        return """
            if (!localStorage.getItem('solo.session.token')) {
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
