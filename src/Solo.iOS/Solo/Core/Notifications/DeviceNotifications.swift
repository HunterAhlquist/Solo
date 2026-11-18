import CryptoKit
import Foundation
import UserNotifications

/// Notifications on the device, as far as they go without a push service. Nothing leaves the
/// instance or the device for them: an instance someone hosts has nobody to send through, so
/// the app does what it can on its own.
///
/// - **Deadline reminders are scheduled ahead.** The instance says which it will raise and
///   when (`api/notifications/upcoming`), and the device is given them to show at that time
///   itself, the app open or not.
/// - **What is in the inbox is shown when the app hears of it**: over its live connection
///   while it runs, and on an iPhone or iPad at a background refresh (`BackgroundSync`),
///   which is when the system chooses and often hours apart. An assignment made while the
///   app is closed arrives then, and not before.
///
/// Whichever of those hears of something first is the only one to say it: each asks `claim`
/// before announcing an entry, and a deadline entry counts as said when this device showed
/// the reminder it was raised from.
///
/// Every request's identifier names its kind and the instance it came from, so one instance's
/// schedule replaces only its own and a tap knows which instance to open.
@MainActor
final class DeviceNotifications: NSObject, UNUserNotificationCenterDelegate {
    static let shared = DeviceNotifications()

    /// The system keeps 64 pending requests for an app and drops the rest without a word;
    /// this leaves room for the ones shown at once.
    static let pendingLimit = 60

    /// A notification that was tapped: an inbox entry, or a reminder's task.
    struct Tap: Equatable {
        var instance: URL
        var entry: UUID?
        var task: UUID?
    }

    /// A tap waiting for its instance to be the one showing: on a cold launch, or while the
    /// app changes instance to follow it.
    private(set) var pendingTap: Tap?
    /// Called with a tap; returns whether there was a session to take it.
    var deliver: ((Tap) -> Bool)?

    func install() {
        UNUserNotificationCenter.current().delegate = self
    }

    /// The tap waiting for `instance`, if any, now taken.
    func takeTap(for instance: URL) -> Tap? {
        guard let tap = pendingTap, tap.instance == instance else { return nil }
        pendingTap = nil
        return tap
    }

    // MARK: Showing and scheduling

    static func allowed() async -> Bool {
        switch await UNUserNotificationCenter.current().notificationSettings().authorizationStatus {
        case .authorized, .provisional, .ephemeral: true
        default: false
        }
    }

    /// Shows an inbox entry now.
    static func show(_ item: NotificationItem, on instance: URL) async {
        guard await allowed() else { return }
        let content = UNMutableNotificationContent()
        content.title = item.title
        content.body = item.body ?? ""
        content.sound = .default
        content.threadIdentifier = key(of: instance)
        content.userInfo = ["instance": address(of: instance), "id": item.id.uuidString]
        let request = UNNotificationRequest(identifier: identifier("show", instance, item.id.uuidString.lowercased()),
                                            content: content, trigger: nil)
        try? await UNUserNotificationCenter.current().add(request)
    }

    /// Replaces the instance's pending reminders with `reminders`, soonest first, as many as
    /// fit beside the other instances'. Returns how many it scheduled.
    @discardableResult
    static func schedule(_ reminders: [UpcomingReminder], on instance: URL) async -> Int {
        let center = UNUserNotificationCenter.current()
        let prefix = identifier("reminder", instance, "")
        let pending = await center.pendingNotificationRequests()
        let mine = pending.map(\.identifier).filter { $0.hasPrefix(prefix) }
        center.removePendingNotificationRequests(withIdentifiers: mine)
        guard await allowed() else { return 0 }

        let room = max(0, pendingLimit - (pending.count - mine.count))
        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        let now = Date()
        var added = 0
        // What this device scheduled for the instance, by key, with when each fires. The
        // ones that fired stay on record (and go when the system kept one quiet: see
        // `willPresent`); the pending ones are whatever this call adds.
        var record = NotificationLedger.fired(reminded(instance), now: now.timeIntervalSince1970)
        for reminder in reminders.filter({ $0.fireAt > now }).sorted(by: { $0.fireAt < $1.fireAt }).prefix(room) {
            let content = UNMutableNotificationContent()
            content.title = reminder.title
            content.body = reminder.body ?? ""
            content.sound = .default
            content.threadIdentifier = key(of: instance)
            content.userInfo = ["instance": address(of: instance), "taskId": reminder.taskId.uuidString]
            var when = utc.dateComponents([.year, .month, .day, .hour, .minute, .second], from: reminder.fireAt)
            when.timeZone = utc.timeZone
            let request = UNNotificationRequest(identifier: prefix + reminder.key, content: content,
                                                trigger: UNCalendarNotificationTrigger(dateMatching: when, repeats: false))
            if (try? await center.add(request)) != nil {
                added += 1
                record[reminder.key] = reminder.fireAt.timeIntervalSince1970
            }
        }
        UserDefaults.standard.set(record, forKey: remindedKey(instance))
        return added
    }

    /// The entries in `unread` (the instance's unread inbox) that nothing on this device has
    /// announced yet, now counted as announced. Whoever is about to announce an entry asks
    /// first, so whichever hears of it first is the only one to. The first ask for an
    /// instance only records its backlog: an inbox full of old entries isn't news.
    ///
    /// A deadline entry is news unless this device showed the reminder it was raised from:
    /// that reminder said it already. One still pending is taken down instead, and the
    /// entry says it.
    static func claim(_ unread: [NotificationItem], on instance: URL) async -> Set<UUID> {
        let defaults = UserDefaults.standard
        let seen = defaults.stringArray(forKey: seenKey(instance)).map(Set.init)
        // Only what is still unread can come up again, so that is all it keeps.
        defaults.set(unread.map(NotificationLedger.key), forKey: seenKey(instance))

        let prefix = identifier("reminder", instance, "")
        let center = UNUserNotificationCenter.current()
        let pending = Set(await center.pendingNotificationRequests().map(\.identifier)
            .filter { $0.hasPrefix(prefix) }.map { String($0.dropFirst(prefix.count)) })
        let verdict = NotificationLedger.news(in: unread, seen: seen, reminded: reminded(instance), pending: pending,
                                              now: Date().timeIntervalSince1970)
        center.removePendingNotificationRequests(withIdentifiers: verdict.superseded.map { prefix + $0 })
        return verdict.news
    }

    /// Reads one instance as the app would with it showing: announces what is new in its
    /// inbox and replaces its reminders. For the instances that have no live connection —
    /// every one of them while the app is closed, the ones not showing while it is open.
    /// Returns the inbox, and throws what the instance answered when it refused.
    @discardableResult
    static func sync(_ instance: URL, api: APIClient) async throws -> NotificationInbox {
        let inbox: NotificationInbox = try await api.send("GET", "api/notifications")
        let news = await claim(inbox.items.filter(\.isUnread), on: instance)
        // The inbox lists the newest first; they show oldest first.
        for item in inbox.items.reversed() where news.contains(item.id) {
            await show(item, on: instance)
        }
        // An instance from before the reminders were offered answers 404: its schedule is
        // left as it is, which is empty.
        if let reminders = try? await api.upcomingReminders() {
            await schedule(reminders, on: instance)
        }
        return inbox
    }

    /// Everything this device holds for the instance: its scheduled reminders and what it
    /// has announced. For a sign-out, a session the instance stopped accepting, or the
    /// instance being removed.
    static func forget(_ instance: URL) {
        UserDefaults.standard.removeObject(forKey: seenKey(instance))
        UserDefaults.standard.removeObject(forKey: remindedKey(instance))
        let prefix = identifier("reminder", instance, "")
        Task {
            let center = UNUserNotificationCenter.current()
            let pending = await center.pendingNotificationRequests()
            center.removePendingNotificationRequests(withIdentifiers: pending.map(\.identifier).filter { $0.hasPrefix(prefix) })
        }
    }

    private static func reminded(_ instance: URL) -> [String: Double] {
        UserDefaults.standard.dictionary(forKey: remindedKey(instance)) as? [String: Double] ?? [:]
    }

    private static func seenKey(_ instance: URL) -> String { "solo.notifications.seen.\(key(of: instance))" }
    private static func remindedKey(_ instance: URL) -> String { "solo.notifications.reminded.\(key(of: instance))" }

    // MARK: Delegate

    /// With the app in front, the app says what is new itself: a toast for what arrives on
    /// the instance showing, a reminder included once the instance raises it. So a reminder
    /// stays quiet here. A `show` is asked for only when nobody is looking at the instance
    /// it is from — the app behind another, or another instance showing — and is let through.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void
    ) {
        let id = notification.request.identifier
        if id.hasPrefix("solo|reminder|") {
            // Kept quiet, so it hasn't said anything: the entry the instance raises for it
            // is news to the person still.
            Self.unrecord(id)
        }
        completionHandler(id.hasPrefix("solo|show|") ? [.banner, .list, .sound] : [])
    }

    /// Takes a reminder (`solo|reminder|<instance key>|<reminder key>`) off the record of
    /// what this device showed.
    nonisolated private static func unrecord(_ identifier: String) {
        let parts = identifier.split(separator: "|", maxSplits: 3).map(String.init)
        guard parts.count == 4 else { return }
        let storeKey = "solo.notifications.reminded.\(parts[2])"
        var record = UserDefaults.standard.dictionary(forKey: storeKey) ?? [:]
        record.removeValue(forKey: parts[3])
        UserDefaults.standard.set(record, forKey: storeKey)
    }

    /// The completion-handler form, called back on the main thread: the system asserts that
    /// a response completes there, and the async form returns wherever it last ran.
    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void
    ) {
        let info = response.notification.request.content.userInfo
        var tap: Tap?
        if response.actionIdentifier == UNNotificationDefaultActionIdentifier,
           let address = info["instance"] as? String, let instance = URL(string: address) {
            tap = Tap(instance: instance, entry: (info["id"] as? String).flatMap(UUID.init(uuidString:)),
                      task: (info["taskId"] as? String).flatMap(UUID.init(uuidString:)))
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
        if deliver?(tap) != true {
            pendingTap = tap
        }
    }

    /// The app took a tap it can't follow yet — its instance isn't the one showing.
    func hold(_ tap: Tap) {
        pendingTap = tap
    }

    // MARK: Identifiers

    /// `solo|<kind>|<instance key>|<id>`: the instance as a short hash, so no part of its
    /// address can collide with the separators.
    private static func identifier(_ kind: String, _ instance: URL, _ id: String) -> String {
        "solo|\(kind)|\(key(of: instance))|\(id)"
    }

    private static func key(of instance: URL) -> String {
        SHA256.hash(data: Data(address(of: instance).utf8)).prefix(8).map { String(format: "%02x", $0) }.joined()
    }

    /// An instance's address as everything here names it. The app keeps an instance as it
    /// was entered and its API client adds the slash that relative paths need, and the two
    /// are one instance: a reminder scheduled through one must be the reminder a refresh
    /// through the other replaces, and a tap on it must find the instance it came from.
    nonisolated static func address(of instance: URL) -> String {
        var address = instance.absoluteString
        while address.hasSuffix("/") { address.removeLast() }
        return address
    }
}

/// What the device has already said, and what that makes news. Kept apart from the system's
/// notification centre so the rules can be tested without one.
nonisolated enum NotificationLedger {
    /// How long a reminder that fired stays on record: past the second reminder, a week
    /// before the deadline, the instance raises nothing more for it.
    static let recordDays: Double = 30

    /// An unread entry as the record names it: its id and when it was raised, so one cleared
    /// and raised again is a new entry.
    static func key(_ item: NotificationItem) -> String {
        "\(item.id.uuidString.lowercased())|\(Int64((item.createdAt.timeIntervalSince1970 * 1000).rounded()))"
    }

    /// Of the reminders on record, the ones that have fired and are still worth remembering.
    static func fired(_ reminded: [String: Double], now: Double) -> [String: Double] {
        reminded.filter { $0.value <= now && $0.value > now - recordDays * 86_400 }
    }

    /// The entries in `unread` nothing has announced, and the pending reminders they make
    /// redundant. `seen` is nil the first time an instance is asked about; `pending` is the
    /// keys of the reminders still scheduled for it.
    static func news(in unread: [NotificationItem], seen: Set<String>?, reminded: [String: Double],
                     pending: Set<String>, now: Double) -> (news: Set<UUID>, superseded: [String]) {
        guard let seen else { return ([], []) }
        var news: Set<UUID> = []
        var superseded: [String] = []
        for item in unread where !seen.contains(key(item)) {
            if let reminder = item.key?.nilIfBlank {
                if pending.contains(reminder) {
                    // The entry is here before its reminder went off: it says it instead.
                    superseded.append(reminder)
                } else if let fired = reminded[reminder], fired <= now {
                    continue
                }
            }
            news.insert(item.id)
        }
        return (news, superseded)
    }
}
