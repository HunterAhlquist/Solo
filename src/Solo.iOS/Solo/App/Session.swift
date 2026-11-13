import SwiftUI

/// Everything that exists only while someone is signed in to an instance.
@Observable
final class Session {
    let api: APIClient
    let hub: HubClient
    let config: AuthConfig
    private(set) var user: User

    let toasts = ToastCenter()
    let navigator = Navigator()
    let projects: ProjectNavStore
    let notifications: NotificationStore
    let assigned: AssignedTasksStore
    let extensions: ExtensionCatalog

    /// True while requests are failing to reach the instance.
    private(set) var isOffline = false
    private(set) var serverVersion: InstanceVersion?
    /// True while the instance is restarting to load an extension update.
    private(set) var restarting = false

    init(api: APIClient, config: AuthConfig, user: User) {
        self.api = api
        self.config = config
        self.user = user
        hub = HubClient(api: api)
        projects = ProjectNavStore(api: api)
        notifications = NotificationStore(api: api, hub: hub, toasts: toasts)
        assigned = AssignedTasksStore(api: api, hub: hub, userId: user.id)
        extensions = ExtensionCatalog(api: api)
        api.onReachabilityChanged = { [weak self] reachable in
            guard let self, self.isOffline == reachable else { return }
            self.isOffline = !reachable
            if reachable {
                self.hub.resume()
            }
        }
    }

    var siteName: String { config.siteName }

    /// What a signed-in launch needs before the first screen is useful.
    func start() async {
        async let extensions: Void = extensions.load()
        async let projects: Void = projects.refreshProjects()
        async let version = try? api.instanceVersion()
        _ = await (extensions, projects)
        serverVersion = await version
        await notifications.start()
        if let fresh = try? await api.currentUser() {
            user = fresh
        }
    }

    func stop() async {
        await hub.shutDown()
    }

    /// The server stops right after answering an extension update, so every
    /// request fails until it is back. This waits that out — asking the
    /// instance itself, never the cache — and then reloads what a new set of
    /// extensions could have changed.
    func awaitRestart() async {
        restarting = true
        defer { restarting = false }
        try? await Task.sleep(for: .seconds(3))
        for _ in 0..<150 {
            if let version: InstanceVersion = try? await api.send("GET", "api/config/version") {
                serverVersion = version
                break
            }
            try? await Task.sleep(for: .seconds(2))
        }
        await extensions.load()
        await projects.refreshProjects()
        hub.resume()
    }

    // MARK: Saving

    /// Runs a save and says what went wrong if it did. A 409 means someone
    /// saved first: the view is refreshed and the person is told, which with
    /// presence warnings should be rare.
    @discardableResult
    func save<T>(_ operation: () async throws -> T, refresh: (() async -> Void)? = nil) async -> T? {
        do {
            return try await operation()
        } catch let error as APIError where error.isConflict {
            await refresh?()
            toasts.show(error.detail ?? "Updated by someone else — your view was refreshed.", .info)
        } catch let error as APIError where error.isUnauthorized {
            toasts.show("Your session expired. Sign in again, then retry.", .warning)
        } catch is CancellationError {
        } catch {
            toasts.error(error, fallback: "That didn't save. Try again.")
        }
        return nil
    }

    /// For saves with nothing to hand back.
    @discardableResult
    func perform(_ operation: () async throws -> Void, refresh: (() async -> Void)? = nil) async -> Bool {
        await save({ try await operation(); return true }, refresh: refresh) ?? false
    }
}
