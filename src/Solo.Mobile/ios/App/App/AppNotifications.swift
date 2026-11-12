import Foundation
import CryptoKit
import UserNotifications

/// The app's side of core's notifications. A WKWebView has no Notification API,
/// so the page asks for these through fixed messages instead
/// (`SoloViewController`, `window.soloApp.notifications`):
///
/// - `permission` / `request`: the system's authorization, as the browser API
///   words it ("granted", "denied", "default").
/// - `show`: an inbox entry the page heard about while nobody was looking, shown
///   at once.
/// - `schedule`: the instance's deadline reminders still to come
///   (`api/notifications/upcoming`), scheduled ahead so they show while the app
///   is closed. Nothing leaves the device or the instance for this: there is no
///   push service, so an assignment reaches a closed app only by email.
///
/// Every request's identifier names its kind and the instance it came from, so
/// one instance's schedule replaces only its own, and a tap knows which
/// instance to open.
@MainActor
final class AppNotifications: NSObject, UNUserNotificationCenterDelegate {
    static let shared = AppNotifications()

    /// iOS keeps 64 pending requests per app and drops the rest without a word;
    /// this leaves room for the immediate ones.
    static let pendingLimit = 60
    static let titleLength = 500
    static let bodyLength = 1000
    static let keyLength = 200
    static let reminderCount = 200

    /// A tap to hand to the page once its client is listening: on a cold launch,
    /// or after switching to the instance it belongs to.
    struct Tap: Equatable {
        let instance: URL
        let detail: [String: String]
    }

    struct Reminder {
        let key: String
        let taskId: UUID
        let fireAt: Date
        let title: String
        let body: String?
    }

    private(set) var pendingTap: Tap?
    /// Called with a tap for the page; returns whether it took it.
    var deliver: ((Tap) -> Bool)?
    /// Called with a tap for an instance other than the one open.
    var switchTo: ((URL) -> Void)?

    func install() {
        UNUserNotificationCenter.current().delegate = self
    }

    /// The tap waiting for `instance`, if any, now taken.
    func takeTap(for instance: URL) -> Tap? {
        guard let tap = pendingTap, tap.instance == instance else { return nil }
        pendingTap = nil
        return tap
    }

    // MARK: The page's requests

    static func permission() async -> String {
        switch await UNUserNotificationCenter.current().notificationSettings().authorizationStatus {
        case .authorized, .provisional, .ephemeral: "granted"
        case .denied: "denied"
        default: "default"
        }
    }

    static func request() async -> String {
        _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])
        return await permission()
    }

    static func show(on instance: URL, id: String, title: String, body: String?) async -> Bool {
        guard await permission() == "granted" else { return false }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body ?? ""
        content.sound = .default
        content.threadIdentifier = key(of: instance)
        content.userInfo = ["instance": instance.absoluteString, "id": id]
        let request = UNNotificationRequest(identifier: identifier("show", instance, id), content: content, trigger: nil)
        return (try? await UNUserNotificationCenter.current().add(request)) != nil
    }

    /// Replaces the instance's pending reminders with `reminders`, soonest
    /// first, as many as fit beside the other instances'. Returns how many.
    static func schedule(on instance: URL, _ reminders: [Reminder]) async -> Int {
        let center = UNUserNotificationCenter.current()
        let prefix = identifier("reminder", instance, "")
        let pending = await center.pendingNotificationRequests()
        let mine = pending.map(\.identifier).filter { $0.hasPrefix(prefix) }
        center.removePendingNotificationRequests(withIdentifiers: mine)
        guard await permission() == "granted" else { return 0 }

        let room = max(0, pendingLimit - (pending.count - mine.count))
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let now = Date()
        var added = 0
        // What this device has scheduled for the instance: the ones that
        // fired stay recorded, the ones just replaced are what was added now.
        var record = (UserDefaults.standard.dictionary(forKey: remindedKey(instance)) as? [String: Double] ?? [:])
            .filter { $0.value <= now.timeIntervalSince1970 && $0.value > now.timeIntervalSince1970 - recordDays * 86_400 }
        defer { UserDefaults.standard.set(record, forKey: remindedKey(instance)) }
        for reminder in reminders.filter({ $0.fireAt > now }).sorted(by: { $0.fireAt < $1.fireAt }).prefix(room) {
            let content = UNMutableNotificationContent()
            content.title = reminder.title
            content.body = reminder.body ?? ""
            content.sound = .default
            content.threadIdentifier = key(of: instance)
            content.userInfo = ["instance": instance.absoluteString, "taskId": reminder.taskId.uuidString]
            var when = utc.dateComponents([.year, .month, .day, .hour, .minute, .second], from: reminder.fireAt)
            when.timeZone = utc.timeZone
            let request = UNNotificationRequest(
                identifier: prefix + reminder.key, content: content,
                trigger: UNCalendarNotificationTrigger(dateMatching: when, repeats: false))
            if (try? await center.add(request)) != nil {
                added += 1
                record[reminder.key] = reminder.fireAt.timeIntervalSince1970
            }
        }
        return added
    }

    /// How long a fired reminder stays on record: past the second reminder,
    /// a week before the deadline, the server raises nothing more for it.
    private static let recordDays: Double = 30

    /// Whether a reminder of `kind` for `taskId` that this device scheduled
    /// has fired: then the inbox entry the server raises for the same deadline
    /// is no news. One still pending counts as not, since the server raising
    /// it means it's no longer upcoming, and the next `schedule` removes it.
    /// Reminder keys are `<kind>:<task id>:<deadline>`.
    static func reminded(on instance: URL, kind: String, taskId: UUID) -> Bool {
        let prefix = "\(kind):\(taskId.uuidString.lowercased()):"
        let now = Date().timeIntervalSince1970
        return (UserDefaults.standard.dictionary(forKey: remindedKey(instance)) as? [String: Double] ?? [:])
            .contains { $0.key.lowercased().hasPrefix(prefix) && $0.value <= now }
    }

    private static func remindedKey(_ instance: URL) -> String {
        "solo.notifications.reminded.\(key(of: instance))"
    }

    /// The keys in `unread` (`<id>|<raised at, ms>`, the instance's unread
    /// inbox entries) that nothing on this device has announced yet, now
    /// counted as announced. The page and `BackgroundSync` both ask before
    /// announcing an entry, so whichever hears of it first is the only one to.
    /// The first ask for an instance only records its backlog: an inbox
    /// full of old entries isn't news.
    static func claim(on instance: URL, unread: [String]) -> [String] {
        let defaults = UserDefaults.standard
        let storeKey = seenKey(instance)
        let seen = defaults.stringArray(forKey: storeKey).map(Set.init)
        // Only what's still unread can come up again, so that's all it keeps.
        defaults.set(unread, forKey: storeKey)
        guard let seen else { return [] }
        return unread.filter { !seen.contains($0) }
    }

    /// Everything this device holds for the instance: its scheduled reminders
    /// and what it has announced. For a sign-out, a session the instance
    /// stopped accepting, or the instance being removed.
    static func forget(_ instance: URL) {
        UserDefaults.standard.removeObject(forKey: seenKey(instance))
        UserDefaults.standard.removeObject(forKey: remindedKey(instance))
        let prefix = identifier("reminder", instance, "")
        Task {
            let center = UNUserNotificationCenter.current()
            let pending = await center.pendingNotificationRequests()
            center.removePendingNotificationRequests(
                withIdentifiers: pending.map(\.identifier).filter { $0.hasPrefix(prefix) })
        }
    }

    private static func seenKey(_ instance: URL) -> String {
        "solo.notifications.seen.\(key(of: instance))"
    }

    // MARK: Delegate

    /// In the foreground the page shows its own snackbar for anything new, a
    /// reminder included once the server raises it, so the system stays quiet.
    /// A `show` is the exception: the page asks for one only when nobody is
    /// looking at it, such as beside another app in Split View.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        completionHandler(notification.request.identifier.hasPrefix("solo|show|") ? [.banner, .list, .sound] : [])
    }

    /// The completion-handler form, called back on the main thread: UIKit
    /// asserts that a response completes there, and the async form returns
    /// wherever it last ran, which crashed a tap that launched the app.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let info = response.notification.request.content.userInfo
        var tap: Tap?
        if response.actionIdentifier == UNNotificationDefaultActionIdentifier,
           let address = info["instance"] as? String, let instance = URL(string: address) {
            var detail: [String: String] = [:]
            if let id = info["id"] as? String { detail["id"] = id }
            if let taskId = info["taskId"] as? String { detail["taskId"] = taskId }
            tap = Tap(instance: instance, detail: detail)
        }
        nonisolated(unsafe) let done = completionHandler
        DispatchQueue.main.async {
            MainActor.assumeIsolated {
                if let tap { self.received(tap) }
                done()
            }
        }
    }

    private func received(_ tap: Tap) {
        // An instance since removed from the list just opens the app.
        guard SavedInstances.all.contains(where: { $0.url == tap.instance }) else { return }
        if deliver?(tap) == true {
            return
        }
        pendingTap = tap
        if SavedInstances.current != tap.instance {
            switchTo?(tap.instance)
        }
    }

    // MARK: Identifiers

    /// `solo|<kind>|<instance key>|<id>`: the instance as a short hash, so no
    /// part of its address can collide with the separators.
    private static func identifier(_ kind: String, _ instance: URL, _ id: String) -> String {
        "solo|\(kind)|\(key(of: instance))|\(id)"
    }

    private static func key(of instance: URL) -> String {
        SHA256.hash(data: Data(instance.absoluteString.utf8)).prefix(8).map { String(format: "%02x", $0) }.joined()
    }
}
