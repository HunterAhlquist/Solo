import BackgroundTasks
import Foundation
import UIKit

/// Notifications while the app is closed, as far as iOS allows without a push
/// service: a background refresh, run when iOS chooses (no sooner than
/// `interval` apart, often hours, never after the app was swiped away until it
/// is opened again), reads every signed-in instance's inbox and upcoming
/// deadline reminders straight from the instance, the way the page would:
///
/// - an unread entry nothing on the device has announced yet shows as a
///   notification, through the same `AppNotifications` the page uses, so a
///   tap opens it the same way, and the page won't announce it again
///   (`AppNotifications.claim`). A deadline entry whose reminder this device
///   scheduled ahead is only recorded: the reminder already says it;
/// - the reminders are rescheduled, replacing the page's schedule for that
///   instance, so a deadline changed elsewhere fires at its new time.
///
/// It signs in with the page's session (`AppCredentials`). An instance that
/// answers 401 has ended it: the copy and everything scheduled for that
/// instance go, and it isn't read again until the page signs in again. The
/// instance's own notification settings already shape both answers, and with
/// notifications off on the device there's nothing to do.
@MainActor
enum BackgroundSync {
    /// Info.plist lists it under BGTaskSchedulerPermittedIdentifiers.
    static let identifier = (Bundle.main.bundleIdentifier ?? "com.hunterahlquist.solo") + ".refresh"
    /// The soonest iOS may run the next one; it decides when it actually does.
    private static let interval: TimeInterval = 15 * 60
    /// Well inside the half minute iOS gives a refresh.
    private static let budget: UInt64 = 20 * 1_000_000_000

    private static var observer: NSObjectProtocol?

    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 15
        configuration.waitsForConnectivity = false
        return URLSession(configuration: configuration)
    }()

    /// At launch, before it finishes, as BGTaskScheduler requires. Each time
    /// the app goes into the background it asks for the next refresh.
    static func install() {
        BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: .main) { task in
            MainActor.assumeIsolated {
                if let task = task as? BGAppRefreshTask {
                    handle(task)
                }
            }
        }
        observer = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { _ in
            MainActor.assumeIsolated { schedule() }
        }
    }

    /// Asks for the next refresh, or withdraws the request when no instance
    /// is signed in on this device.
    static func schedule() {
        guard SavedInstances.all.contains(where: { AppCredentials.token(for: $0.url) != nil }) else {
            BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: identifier)
            return
        }

        let request = BGAppRefreshTaskRequest(identifier: identifier)
        request.earliestBeginDate = Date(timeIntervalSinceNow: interval)
        do {
            try BGTaskScheduler.shared.submit(request)
        } catch {
            NSLog("Solo: background refresh not scheduled (\(error)).")
        }
    }

    private static func handle(_ task: BGAppRefreshTask) {
        // The next one first, so a run cut short still leaves one.
        schedule()
        let work = Task { @MainActor in
            let finished = await run()
            task.setTaskCompleted(success: finished)
        }
        let deadline = Task {
            try await Task.sleep(nanoseconds: budget)
            work.cancel()
        }
        task.expirationHandler = {
            work.cancel()
            deadline.cancel()
        }
        Task {
            _ = await work.value
            deadline.cancel()
        }
    }

    /// One pass over the signed-in instances. False when one couldn't be read.
    static func run() async -> Bool {
        guard await AppNotifications.permission() == "granted" else { return true }
        var finished = true
        for instance in SavedInstances.all.map(\.url) {
            guard !Task.isCancelled else { return false }
            guard let token = AppCredentials.token(for: instance) else { continue }
            if await !sync(instance, token: token) {
                finished = false
            }
        }
        return finished
    }

    private static func sync(_ instance: URL, token: String) async -> Bool {
        // Checked before every write: a sign-out while a request was out
        // forgets the instance, and nothing may put any of it back.
        func current() -> Bool { AppCredentials.token(for: instance) == token }

        do {
            let inbox = try await get("api/notifications", on: instance, token: token) as? [String: Any]
            let unread = (inbox?["items"] as? [[String: Any]] ?? []).compactMap(Entry.init)
            guard current() else { return true }
            let fresh = Set(AppNotifications.claim(on: instance, unread: unread.map(\.key)))
            // The inbox lists the newest first; they show oldest first. A
            // deadline entry whose reminder this device just showed is only
            // recorded (AppNotifications.reminded); any other shows.
            for entry in unread.reversed() where fresh.contains(entry.key) && !entry.reminded(on: instance) {
                guard current() else { return true }
                _ = await AppNotifications.show(on: instance, id: entry.id, title: entry.title, body: entry.body)
            }

            let upcoming = try await get("api/notifications/upcoming", on: instance, token: token) as? [[String: Any]]
            let reminders = (upcoming ?? []).prefix(AppNotifications.reminderCount).compactMap(reminder)
            guard current() else { return true }
            _ = await AppNotifications.schedule(on: instance, reminders)
            // Signed out while it scheduled: undo what it added.
            if !current() {
                AppNotifications.forget(instance)
            }
            return true
        } catch Failure.signedOut {
            AppCredentials.remove(for: instance)
            AppNotifications.forget(instance)
            return true
        } catch {
            NSLog("Solo: background refresh of \(instance.host ?? "an instance") failed (\(error)).")
            return false
        }
    }

    private enum Failure: Error {
        case signedOut
        case status(Int)
    }

    private static func get(_ path: String, on instance: URL, token: String) async throws -> Any {
        var request = URLRequest(url: instance.appendingPathComponent(path))
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 {
            throw Failure.signedOut
        }
        guard (200..<300).contains(status) else { throw Failure.status(status) }
        return try JSONSerialization.jsonObject(with: data)
    }

    /// An unread inbox entry, keyed as the page keys it.
    private struct Entry {
        let id: String
        let key: String
        let title: String
        let body: String?
        let kind: String?
        let taskId: UUID?
        let raisedAt: Date

        init?(_ json: [String: Any]) {
            guard json["readAt"] == nil || json["readAt"] is NSNull,
                  let id = (json["id"] as? String).flatMap(UUID.init(uuidString:)),
                  let raised = (json["createdAt"] as? String).flatMap(BackgroundSync.epochMillis),
                  let title = BackgroundSync.text(json["title"], max: AppNotifications.titleLength)
            else { return nil }
            self.id = id.uuidString.lowercased()
            key = "\(self.id)|\(raised)"
            raisedAt = Date(timeIntervalSince1970: Double(raised) / 1000)
            self.title = title
            body = BackgroundSync.text(json["body"], max: AppNotifications.bodyLength)
            kind = json["kind"] as? String
            taskId = (json["taskId"] as? String).flatMap(UUID.init(uuidString:))
        }

        @MainActor
        func reminded(on instance: URL) -> Bool {
            guard let kind, let taskId, ["DueInTwoWeeks", "DueThisWeek"].contains(kind) else { return false }
            return AppNotifications.reminded(on: instance, kind: kind, taskId: taskId, raisedAt: raisedAt)
        }
    }

    private static func reminder(_ json: [String: Any]) -> AppNotifications.Reminder? {
        guard let key = json["key"] as? String, !key.isEmpty, key.count <= AppNotifications.keyLength,
              let taskId = (json["taskId"] as? String).flatMap(UUID.init(uuidString:)),
              let fireAt = (json["fireAt"] as? String).flatMap(epochMillis),
              let title = text(json["title"], max: AppNotifications.titleLength)
        else { return nil }
        return AppNotifications.Reminder(
            key: key, taskId: taskId, fireAt: Date(timeIntervalSince1970: Double(fireAt) / 1000),
            title: title, body: text(json["body"], max: AppNotifications.bodyLength))
    }

    nonisolated private static func text(_ value: Any?, max: Int) -> String? {
        guard let text = value as? String, !text.isEmpty else { return nil }
        return text.count <= max ? text : String(text.prefix(max - 1)) + "…"
    }

    /// Milliseconds since the epoch of a time as the API writes it
    /// (`2026-11-12T17:25:45.123456+00:00`), cut to the millisecond as .NET's
    /// ToUnixTimeMilliseconds cuts it, so a key made here matches the page's.
    /// The fraction is read by hand: the system formatter takes three digits
    /// at most, and the API writes up to seven.
    nonisolated static func epochMillis(_ text: String) -> Int64? {
        var whole = text
        var millis: Int64 = 0
        if let dot = text.firstIndex(of: ".") {
            let digits = text[text.index(after: dot)...].prefix(while: { $0.isASCII && $0.isNumber })
            millis = Int64(String(digits.prefix(3)).padding(toLength: 3, withPad: "0", startingAt: 0)) ?? 0
            whole = String(text[..<dot]) + text[digits.endIndex...]
        }
        guard let date = ISO8601DateFormatter().date(from: whole) else { return nil }
        return Int64(date.timeIntervalSince1970) * 1000 + millis
    }
}
