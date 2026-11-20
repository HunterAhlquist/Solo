import Network
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
    #if os(macOS)
    /// Where the Settings window is looking: see `Navigator.settings`.
    let settingsNavigator = Navigator()
    #endif
    let projects: ProjectNavStore
    let notifications: NotificationStore
    let assigned: AssignedTasksStore
    let extensions: ExtensionCatalog
    /// What extensions with no native half add to core's screens, drawn here (KitSurface.swift).
    let kit = KitSurfaceHost()
    /// What the pages the app hosts sign in with, in place of the app's own session.
    let hosted: HostedCredential
    /// The design docs downloaded to this device (OfflineDocs.swift).
    let docsOffline: OfflineVaults

    /// True while requests are failing to reach the instance.
    private(set) var isOffline = false
    /// True once the instance is known to be out of reach: a request failed, and so did asking
    /// it whether it is there. One request failing is a slow page; this is no instance, and
    /// while it lasts the window shows only what can be worked on without one (OfflineMode.swift).
    private(set) var offlineMode = false
    /// Asking the instance whether it is back, for as long as it is not.
    @ObservationIgnored private var watch: Task<Void, Never>?
    /// The device's own connection changing, which is when to ask at once.
    @ObservationIgnored private var network: NWPathMonitor?
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
        hosted = HostedCredential(api: api)
        hub = HubClient(api: api)
        projects = ProjectNavStore(api: api)
        notifications = NotificationStore(api: api, hub: hub, toasts: toasts)
        assigned = AssignedTasksStore(api: api, hub: hub, userId: user.id)
        extensions = ExtensionCatalog(api: api)
        docsOffline = OfflineVaults(api: api, userId: user.id)
        kit.session = self
        #if os(macOS)
        settingsNavigator.tab = .account
        navigator.settings = settingsNavigator
        #endif
        api.onReachabilityChanged = { [weak self] reachable in self?.heard(reachable) }
        navigator.worksOffline = { [weak self] in self?.worksOffline($0) ?? false }
        navigator.refused = { [weak self] _ in
            guard let self else { return }
            self.toasts.show("That needs \(self.siteName), which is out of reach.", .info)
        }
        // Signed in on what was kept from last time, the instance not having answered: the
        // window opens as it is with no instance, without first being drawn as if it had one.
        if api.reachable == false {
            isOffline = true
            setOfflineMode(true)
        }
    }

    // MARK: Out of reach

    private func heard(_ reachable: Bool) {
        if reachable {
            watch?.cancel()
            watch = nil
            if isOffline {
                isOffline = false
                hub.resume()
            }
            setOfflineMode(false)
        } else {
            isOffline = true
            watchForInstance()
        }
    }

    private func setOfflineMode(_ offline: Bool) {
        // An instance restarting to load an extension is out of reach on purpose, and says
        // so over the whole window (`awaitRestart`): that is not this.
        guard offlineMode != offline, !(offline && restarting) else { return }
        withAnimation(.smooth(duration: 0.3)) {
            offlineMode = offline
            navigator.offline = offline
        }
    }

    /// Asks until the instance answers. The first time it doesn't is what makes this offline;
    /// its answering is heard like any other request's, and ends this.
    private func watchForInstance() {
        guard watch == nil else { return }
        watch = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, await !self.api.ping(), !Task.isCancelled else { return }
                self.setOfflineMode(true)
                try? await Task.sleep(for: .seconds(8))
            }
        }
    }

    /// Asks now, for someone who has asked to, or because something says the answer may have
    /// changed. True if the instance answered.
    @discardableResult
    func checkConnection() async -> Bool {
        if await api.ping() { return true }
        setOfflineMode(true)
        return false
    }

    /// Whether a page can be worked on with the instance out of reach: a design doc that is
    /// on this device, and has not been deleted here.
    func worksOffline(_ route: Route?) -> Bool {
        guard case .designDocs(_, let vault, _)? = route else { return false }
        return docsOffline.isDownloaded(vault) && docsOffline.view(vault)?.deleted != true
    }

    /// The section the window's stack shows. Out of reach, that is `.offline` — unless what
    /// was showing is a design doc that is on the device, which is left exactly as it is:
    /// someone writing in it when the connection went is still writing in it.
    var shownTab: AppTab {
        navigator.tab == .offline || (offlineMode && !worksOffline(navigator.current)) ? .offline : navigator.tab
    }

    var siteName: String { config.siteName }

    /// Whether this person may create, close, reopen and delete a project's boards, roadmaps
    /// and design docs: its owners, and a super admin whatever their role there. The API's
    /// owner policy, which an instance from before owners were let in applies more narrowly —
    /// there an owner who isn't a super admin is offered what the instance then refuses.
    func manages(_ project: ProjectSummary?) -> Bool {
        user.isSuperAdmin || project?.isOwner == true
    }

    /// A notification on the device was tapped: an inbox entry is read and opened as the
    /// inbox opens it, a reminder goes to its task. Something that is no longer there — a
    /// task since deleted, an entry since cleared — lands on the inbox.
    func open(_ tap: DeviceNotifications.Tap) async {
        var taskId = tap.task
        if let entry = tap.entry {
            await notifications.refresh()
            if let item = notifications.inbox?.items.first(where: { $0.id == entry }) {
                await notifications.markRead(item)
                taskId = item.taskId
                if taskId == nil, let boardId = item.boardId, let board = try? await api.board(boardId) {
                    navigator.open(.board(project: board.projectAlias, board: board.alias))
                    return
                }
            }
        }
        if let taskId, let task = try? await api.task(taskId), let board = try? await api.board(task.boardId) {
            navigator.open(TaskReference(projectAlias: board.projectAlias, boardAlias: board.alias, number: task.number))
        } else {
            navigator.popToRoot(.inbox)
        }
    }

    /// What a signed-in launch needs before the first screen is useful.
    func start() async {
        async let extensions: Void = extensions.load()
        async let projects: Void = projects.refreshProjects()
        async let version = try? api.instanceVersion()
        _ = await (extensions, projects)
        docsOffline.start(hub: hub)
        // The device joining or leaving a network says nothing of the instance — one on this
        // Mac is there with no network at all — but it is when to ask.
        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] _ in
            Task { @MainActor in await self?.checkConnection() }
        }
        monitor.start(queue: .main)
        network = monitor
        if offlineMode { watchForInstance() }
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
            // The live feed going is the first anyone hears of the instance going, where
            // nothing else was being asked of it: so it is asked.
            hub.dropped.subscribe { [weak self] in
                Task { await self?.checkConnection() }
            },
        ]

        #if DEBUG || PERF
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
        watch?.cancel()
        network?.cancel()
        docsOffline.stop()
        kit.reset()
        hosted.stop()
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
        kit.restart()
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
