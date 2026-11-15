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
    /// A newer server release, when there is one and this person is the one who could install it.
    private(set) var serverUpdate: ServerUpdate?
    /// True while the instance is restarting to load an extension update.
    private(set) var restarting = false
    @ObservationIgnored private var subscriptions: [EventSubscription] = []

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
        // Advisory, and only for a super admin: updating the server is theirs to do.
        // Cached on the instance for hours, so this is a local round trip.
        if user.isSuperAdmin {
            serverUpdate = (try? await api.updates())?.server
        }
        // The live feed coming back usually means the instance restarted, which is
        // the one time its extensions can have changed: ask again what it runs.
        subscriptions = [
            hub.reconnected.subscribe { [weak self] in
                Task {
                    await self?.extensions.load()
                    await self?.projects.refreshProjects()
                }
            },
        ]

        #if DEBUG
        // Simulator verification: start on a given page instead of tapping there.
        // "DMD", "DMD-PW26" or "DMD-PW26-2"; or a tab's name.
        if let target = ProcessInfo.processInfo.environment["SOLO_OPEN"] {
            let parts = target.split(separator: "-").map(String.init)
            let pieces = target.split(separator: ":").map(String.init)
            if target.hasPrefix("web:") {
                // Any address of the web client's, shown as an extension's page is.
                navigator.open(.pluginPage(path: String(target.dropFirst(4)), title: "Page"))
            } else if pieces.count == 3, pieces[0] == "roadmap", let id = UUID(uuidString: pieces[2]) {
                navigator.open(.roadmap(project: pieces[1], roadmapId: id))
            } else if pieces.count >= 3, pieces[0] == "docs", let id = UUID(uuidString: pieces[2]) {
                navigator.open(.designDocs(project: pieces[1], vaultId: id, noteId: pieces.count > 3 ? UUID(uuidString: pieces[3]) : nil))
            } else if pieces.count == 2, pieces[0] == "route" {
                switch pieces[1] {
                case "mcp": navigator.open(.mcpAccess)
                case "admin": navigator.open(.admin)
                case "devices": navigator.open(.devices)
                case "notifications": navigator.open(.notificationSettings)
                case "extensions": navigator.open(.adminExtensions)
                case "themes": navigator.open(.adminThemes)
                case "users": navigator.open(.adminUsers)
                case "email": navigator.open(.adminEmail)
                case "updates": navigator.open(.adminUpdates)
                case "about": navigator.open(.about)
                case "kitspike": navigator.open(.kitSpike)
                default: break
                }
            } else if let tab = AppTab(rawValue: target) {
                navigator.popToRoot(tab)
            } else if parts.count == 3, let number = Int(parts[2]) {
                navigator.open(.board(project: parts[0], board: parts[1], task: number))
            } else if parts.count == 2 {
                navigator.open(.board(project: parts[0], board: parts[1]))
            } else if parts.count == 1 {
                navigator.open(.project(alias: parts[0]))
            }
        }
        #endif
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
