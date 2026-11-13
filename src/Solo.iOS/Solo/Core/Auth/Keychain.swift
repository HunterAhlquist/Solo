import Foundation
import Security

/// The session token's home. Twenty lines over the Security framework rather
/// than a dependency: one generic-password item per instance is all Solo keeps.
nonisolated enum Keychain {
    private static let service = "com.hunterahlquist.solo.session"

    static func read(account: String) -> String? {
        var query = base(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    static func write(_ value: String, account: String) {
        delete(account: account)
        var query = base(account)
        query[kSecValueData as String] = Data(value.utf8)
        // Readable after the first unlock, so a background refresh can still
        // reach the instance, and never synced to another device: a session is
        // this device's.
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(query as CFDictionary, nil)
    }

    static func delete(account: String) {
        SecItemDelete(base(account) as CFDictionary)
    }

    private static func base(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }
}
