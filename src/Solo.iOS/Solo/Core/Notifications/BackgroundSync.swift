#if os(iOS)
import BackgroundTasks
import Foundation
import UIKit

/// Notifications while the app is closed, as far as iOS allows without a push service: a
/// background refresh, run when iOS chooses (no sooner than `interval` apart, often hours,
/// and never after the app was swiped away until it is opened again), reads every signed-in
/// instance's inbox and upcoming deadline reminders straight from the instance, the way the
/// app does with one showing (`DeviceNotifications.sync`):
///
/// - an unread entry nothing on the device has announced yet shows as a notification, and
///   the app won't announce it again when it is next opened;
/// - the reminders are scheduled again, so a deadline changed elsewhere fires at its new time.
///
/// It signs in with each instance's session, which the Keychain gives up once the device has
/// been unlocked since it started. An instance that answers 401 has ended its session: that
/// goes, with everything scheduled for the instance, and it isn't read again until someone
/// signs in to it. A Mac has no such refresh; there the app does the same while it runs.
@MainActor
enum BackgroundSync {
    /// Info.plist lists it under BGTaskSchedulerPermittedIdentifiers.
    static let identifier = (Bundle.main.bundleIdentifier ?? "com.hunterahlquist.solo.native") + ".refresh"
    /// The soonest iOS may run the next one; it decides when it actually does.
    private static let interval: TimeInterval = 15 * 60
    /// Well inside the half minute iOS gives a refresh.
    private static let budget: Duration = .seconds(20)

    private static var observer: NSObjectProtocol?

    /// At launch, before it finishes, as BGTaskScheduler requires. Each time the app goes
    /// into the background it asks for the next refresh.
    static func install() {
        BGTaskScheduler.shared.register(forTaskWithIdentifier: identifier, using: .main) { task in
            MainActor.assumeIsolated {
                if let task = task as? BGAppRefreshTask {
                    handle(task)
                } else {
                    task.setTaskCompleted(success: false)
                }
            }
        }
        observer = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { _ in
            MainActor.assumeIsolated { schedule() }
        }
    }

    /// Asks for the next refresh, or withdraws the request when no instance is signed in.
    static func schedule() {
        guard AppModel.storedInstances().contains(where: { Keychain.read(account: $0.url.absoluteString) != nil }) else {
            BGTaskScheduler.shared.cancel(taskRequestWithIdentifier: identifier)
            return
        }
        let request = BGAppRefreshTaskRequest(identifier: identifier)
        request.earliestBeginDate = Date(timeIntervalSinceNow: interval)
        // Refused on a simulator, and where Background App Refresh is switched off: there is
        // nothing to do about either, and the app says what it does without one.
        try? BGTaskScheduler.shared.submit(request)
    }

    private static func handle(_ task: BGAppRefreshTask) {
        // The next one first, so a run cut short still leaves one.
        schedule()
        let work = Task { @MainActor in
            let finished = await run()
            task.setTaskCompleted(success: finished)
        }
        let deadline = Task {
            try await Task.sleep(for: budget)
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
        guard await DeviceNotifications.allowed() else { return true }
        var finished = true
        for instance in AppModel.storedInstances().map(\.url) {
            guard !Task.isCancelled else { return false }
            guard let token = Keychain.read(account: instance.absoluteString) else { continue }
            if await !sync(instance, token: token) {
                finished = false
            }
        }
        return finished
    }

    private static func sync(_ instance: URL, token: String) async -> Bool {
        do {
            try await DeviceNotifications.sync(instance, api: APIClient(baseURL: instance, token: token))
            // Signed out while it was being read: nothing of it may be left behind.
            if Keychain.read(account: instance.absoluteString) != token {
                DeviceNotifications.forget(instance)
            }
            return true
        } catch let error as APIError where error.isUnauthorized {
            // Only if it is still the session that was refused: one signed in to since is not.
            if Keychain.read(account: instance.absoluteString) == token {
                Keychain.delete(account: instance.absoluteString)
            }
            DeviceNotifications.forget(instance)
            return true
        } catch {
            return false
        }
    }
}
#endif
