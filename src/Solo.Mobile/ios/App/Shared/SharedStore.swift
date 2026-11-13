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

    /// Stores `token` as the instance's session, or with nil forgets it. Either
    /// way the summary drawn with the old one goes, so another session never
    /// shows it. Without the lock a session isn't stored, and the widget asks
    /// you to sign in; forgetting one goes ahead regardless, since leaving a
    /// session behind after signing out is the worse failure, and no widget
    /// can write without the lock either.
    static func setToken(_ token: String?, for instance: URL) {
        if locked({ write(token, for: instance) }) == nil, token == nil {
            write(nil, for: instance)
        }
    }

    /// The instance's session as a widget fetched with it: `body` runs only if
    /// `token` is still the stored session, and holds the lock while it does,
    /// so the app can't sign out or in between that check and what `body`
    /// writes. Nil when the session changed in the meantime, or the lock can't
    /// be taken: a widget never writes without it.
    static func withSession<T>(_ token: String, of instance: URL, _ body: (Session) -> T) -> T? {
        locked { Self.token(for: instance) == token ? body(Session(instance: instance)) : nil } ?? nil
    }

    /// What a widget may do with a session it holds (`withSession`).
    struct Session {
        let instance: URL

        func cache(_ summary: WorkSummary) {
            guard let file = SharedStore.summaryFile(instance) else { return }
            try? JSONEncoder().encode(Cached(summary: summary, fetchedAt: Date())).write(to: file, options: .atomic)
        }

        var cached: Cached? { SharedStore.cached(for: instance) }

        /// The instance stopped accepting the session.
        func forget() { SharedStore.write(nil, for: instance) }
    }

    private static func write(_ token: String?, for instance: URL) {
        if let file = summaryFile(instance) {
            try? FileManager.default.removeItem(at: file)
        }
        guard let token else {
            SecItemDelete(keychainQuery(instance) as CFDictionary)
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

    /// One lock for every change to a session or its summary, held across the
    /// app and its widgets, which run in separate processes: an exclusive
    /// `flock` on a file in the group's container. Each holder only touches
    /// the Keychain and one small file, so it's held for moments, never across
    /// a network call or a suspension. Nil, without running `body`, when the
    /// lock can't be taken; there's no unlocked fallback, and no stand-in for
    /// the container, which a stand-in lock file wouldn't share.
    private static func locked<T>(_ body: () -> T) -> T? {
        guard let path = container?.appendingPathComponent("sessions.lock").path else { return nil }
        let fd = open(path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var status: Int32
        repeat { status = flock(fd, LOCK_EX) } while status != 0 && errno == EINTR
        guard status == 0 else { return nil }
        defer { flock(fd, LOCK_UN) }
        return body()
    }

    /// The group's container; nil when the entitlement is missing.
    private static var container: URL? {
        FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group)
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
        summaryFile(instance).flatMap { try? Data(contentsOf: $0) }.flatMap { try? JSONDecoder().decode(Cached.self, from: $0) }
    }

    private static func summaryFile(_ instance: URL) -> URL? {
        let name = Data(instance.absoluteString.utf8).base64EncodedString()
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "+", with: "-")
        return container?.appendingPathComponent("summary-\(name).json")
    }
}
