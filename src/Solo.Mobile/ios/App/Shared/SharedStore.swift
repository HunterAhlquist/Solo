import Foundation
import Security

/// What the app and its widgets share, through the App Group both belong to:
/// the instance list, a copy of each instance's session, and the last work
/// summary each widget drew. The app writes the first two; the widgets read
/// them, and forget a session the instance stops accepting.
///
/// The session copy lives in the Keychain under the group, readable after the
/// first unlock so a widget can refresh on a locked device, and never in a
/// backup. It goes with the page's session (`window.soloApp.keepSession`), on
/// removing the instance, and on a 401.
enum SharedStore {
    /// `group.<bundle id>`, from Info.plist (`SoloAppGroup`) so the preview and
    /// store builds each have their own.
    static let group: String =
        Bundle.main.object(forInfoDictionaryKey: "SoloAppGroup") as? String ?? "group.com.hunterahlquist.solo"

    private static var defaults: UserDefaults { UserDefaults(suiteName: group) ?? .standard }
    private static let instancesKey = "solo.shared.instances"
    private static let currentKey = "solo.shared.current"

    struct Instance: Codable, Hashable {
        let url: URL
        let name: String
    }

    static var instances: [Instance] {
        get { defaults.data(forKey: instancesKey).flatMap { try? JSONDecoder().decode([Instance].self, from: $0) } ?? [] }
        set { defaults.set(try? JSONEncoder().encode(newValue), forKey: instancesKey) }
    }

    static var current: URL? {
        get { defaults.url(forKey: currentKey) }
        set { defaults.set(newValue, forKey: currentKey) }
    }

    // MARK: Sessions

    static func token(for instance: URL) -> String? {
        var query = keychainQuery(instance)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Stores `token` as the instance's session, or with nil forgets it, along
    /// with the summary drawn with it.
    static func setToken(_ token: String?, for instance: URL) {
        guard let token else {
            SecItemDelete(keychainQuery(instance) as CFDictionary)
            try? FileManager.default.removeItem(at: summaryFile(instance))
            return
        }
        let update = [
            kSecValueData as String: Data(token.utf8),
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ] as [String: Any]
        if SecItemUpdate(keychainQuery(instance) as CFDictionary, update as CFDictionary) == errSecItemNotFound {
            SecItemAdd(keychainQuery(instance).merging(update) { $1 } as CFDictionary, nil)
        }
    }

    private static func keychainQuery(_ instance: URL) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "solo.session",
            kSecAttrAccount as String: instance.absoluteString,
            kSecAttrAccessGroup as String: group,
        ]
    }

    // MARK: The last summary

    struct Cached: Codable {
        let summary: WorkSummary
        let fetchedAt: Date
    }

    static func cached(for instance: URL) -> Cached? {
        (try? Data(contentsOf: summaryFile(instance))).flatMap { try? JSONDecoder().decode(Cached.self, from: $0) }
    }

    static func cache(_ summary: WorkSummary, for instance: URL) {
        try? JSONEncoder().encode(Cached(summary: summary, fetchedAt: Date())).write(to: summaryFile(instance), options: .atomic)
    }

    private static func summaryFile(_ instance: URL) -> URL {
        let folder = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group)
            ?? FileManager.default.temporaryDirectory
        let name = Data(instance.absoluteString.utf8).base64EncodedString()
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "+", with: "-")
        return folder.appendingPathComponent("summary-\(name).json")
    }
}
