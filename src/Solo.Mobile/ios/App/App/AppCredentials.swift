import Foundation
import Security

/// A copy of each instance's `solo_` session, for `BackgroundSync`: the page
/// keeps its own in the web view's storage, which nothing runs while the app is
/// closed. The page hands it over whenever it changes (`soloApp.keepSession`),
/// so the copy is the session the page has, and goes when the page signs out,
/// when the instance is removed, or when the instance stops accepting it.
///
/// In the Keychain, readable after the first unlock so a refresh can run while
/// the device is locked, and never leaving this device in a backup.
enum AppCredentials {
    private static let service = (Bundle.main.bundleIdentifier ?? "com.hunterahlquist.solo") + ".session"

    static func token(for instance: URL) -> String? {
        var query = baseQuery(for: instance)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data
        else { return nil }
        return String(data: data, encoding: .utf8)
    }

    static func store(_ token: String, for instance: URL) {
        let data = Data(token.utf8)
        let update = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ] as [String: Any]
        if SecItemUpdate(baseQuery(for: instance) as CFDictionary, update as CFDictionary) == errSecItemNotFound {
            SecItemAdd(baseQuery(for: instance).merging(update) { $1 } as CFDictionary, nil)
        }
    }

    static func remove(for instance: URL) {
        SecItemDelete(baseQuery(for: instance) as CFDictionary)
    }

    private static func baseQuery(for instance: URL) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: instance.absoluteString,
        ]
    }
}
