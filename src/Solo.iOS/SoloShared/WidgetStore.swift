import Foundation
import Security

// What the app and its widgets share. A widget runs in its own process and
// can't see the app's memory or its files; what the two have in common here
// is one keychain access group, named in both targets' entitlements. The app
// puts the little a widget needs there — which instance, a copy of the
// session, the instance's accent — and the widget adds the last summary it
// drew, to fall back on when the instance can't be reached.
//
// The keychain rather than an app group's container because it is all small,
// one piece of it is a credential, and a keychain group needs nothing
// registered: any development profile for the team already allows it.

/// The instance a widget shows, as the app last described it.
nonisolated struct WidgetInstance: Codable, Equatable, Sendable {
    var url: URL
    var name: String
    /// The instance theme's accent for light and for dark, as `#rrggbb`; nil where the theme has none.
    var accentLight: String?
    var accentDark: String?
}

nonisolated enum WidgetStore {
    private static let service = "com.hunterahlquist.solo.widget"

    /// `<team>.<group>`, from Info.plist, where the build setting that knows the team fills it in.
    private static let group = Bundle.main.object(forInfoDictionaryKey: "SoloKeychainGroup") as? String

    // MARK: What the app writes

    static var instance: WidgetInstance? {
        get { read("instance").flatMap { try? JSONDecoder().decode(WidgetInstance.self, from: $0) } }
        set { write("instance", newValue.flatMap { try? JSONEncoder().encode($0) }) }
    }

    /// The session a widget asks the instance with: a copy of the app's, which goes when the app signs out.
    static var token: String? {
        get { read("token").map { String(decoding: $0, as: UTF8.self) } }
        set {
            // A summary belongs to the session it was fetched with.
            if newValue != token { summary = nil }
            write("token", newValue.map { Data($0.utf8) })
        }
    }

    // MARK: What a widget keeps

    /// The last summary drawn, for when the instance doesn't answer.
    static var summary: WorkSummary? {
        get { read("summary").flatMap { try? JSONDecoder().decode(WorkSummary.self, from: $0) } }
        set { write("summary", newValue.flatMap { try? JSONEncoder().encode($0) }) }
    }

    // MARK: Keychain

    private static func query(_ account: String) -> [String: Any] {
        var query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: account]
        if let group, !group.isEmpty { query[kSecAttrAccessGroup as String] = group }
        return query
    }

    private static func read(_ account: String) -> Data? {
        var query = query(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    private static func write(_ account: String, _ data: Data?) {
        SecItemDelete(query(account) as CFDictionary)
        guard let data else { return }
        var item = query(account)
        item[kSecValueData as String] = data
        // A widget refreshes while the device is locked, so: readable after the first unlock.
        // Never synced or restored elsewhere — a session is this device's.
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }
}
