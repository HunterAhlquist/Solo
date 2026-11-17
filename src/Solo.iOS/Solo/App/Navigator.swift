import SwiftUI

/// Everywhere a link can lead. Aliases rather than ids, as the web app's routes
/// are: a route then means the same thing on every client and survives a
/// refetch.
nonisolated enum Route: Hashable, Sendable {
    case project(alias: String)
    /// `task` opens that task's detail once the board has loaded.
    case board(project: String, board: String, task: Int? = nil)
    case notificationSettings
    case devices
    case appearance
    case admin
    case adminUsers
    case adminEmail
    case adminExtensions
    case adminUpdates
    case adminThemes
    case about
    /// The instances kept on this device: a page on a Mac, where it is a section of Settings.
    case instances
    /// A page of an extension the app has no native half for: the web client's, shown inside the
    /// app. `path` is relative to the instance; `project` is the project it belongs to, if any.
    case pluginPage(path: String, title: String, project: String? = nil)
    /// A page a baked-in extension owns.
    case mcpAccess
    case roadmap(project: String, roadmapId: UUID)
    case designDocs(project: String, vaultId: UUID, noteId: UUID? = nil)
}

nonisolated enum AppTab: String, Hashable, CaseIterable, Sendable {
    case work, projects, inbox, account
    /// What can be worked on with the instance out of reach (OfflineMode.swift). Not a tab
    /// anyone chooses: it is where the app is while there is nothing else it can show.
    case offline

    var title: String {
        switch self {
        case .work: "Your Tasks"
        case .projects: "Projects"
        case .inbox: "Inbox"
        case .account: "Account"
        case .offline: "Offline"
        }
    }

    var symbol: String {
        switch self {
        case .work: "person.crop.rectangle.stack"
        case .projects: "square.grid.2x2"
        case .inbox: "bell"
        case .account: "person.crop.circle"
        case .offline: "icloud.slash"
        }
    }
}

/// One navigation model for both layouts. On an iPhone each tab keeps its own
/// stack; on an iPad the sidebar chooses the tab and its stack fills the detail
/// column. A link therefore lands in the same place whichever is showing.
@Observable
final class Navigator {
    var tab: AppTab = .work
    var paths: [AppTab: [Route]] = [:]

    #if os(macOS)
    /// On a Mac the account's section — the account itself, its devices, the instance's admin
    /// pages, what extensions add beside them — is not in the main window: it is the Settings
    /// window, which has a navigator of its own. This is that one, for the main window's; the
    /// Settings window's own has none, and shows the section itself.
    @ObservationIgnored var settings: Navigator?
    /// Counts up each time something in the main window asks for a page of Settings: what the
    /// window listens for to open it, since only a view can.
    private(set) var settingsAsked = 0
    #endif

    /// True while the instance is out of reach (`Session.offlineMode`). Then the only places
    /// to go are the ones `worksOffline` allows, and they are under `.offline`, whose first
    /// page lists them; anywhere else is refused, and said to be through `refused`.
    var offline = false {
        didSet {
            // Back in reach with nothing open that was opened out of it: where it was before.
            if !offline, tab == .offline, self[.offline].isEmpty { leaveOffline() }
        }
    }
    @ObservationIgnored var worksOffline: (Route) -> Bool = { _ in false }
    @ObservationIgnored var refused: (Route) -> Void = { _ in }
    /// The section that was showing when the app went to `.offline`, to go back to.
    @ObservationIgnored private var resume: AppTab?

    /// A tab's stack, empty until something is pushed.
    subscript(tab: AppTab) -> [Route] {
        get { paths[tab] ?? [] }
        set {
            paths[tab] = newValue
            // The last page that was opened out of reach, gone back from once back in reach:
            // what is under it is a list of what to do without the instance.
            if tab == .offline, self.tab == .offline, !offline, newValue.isEmpty { leaveOffline() }
        }
    }

    private func leaveOffline() {
        paths[.offline] = nil
        tab = resume ?? .work
        resume = nil
    }

    /// The route on top of the visible stack, which is what the sidebar highlights.
    var current: Route? { paths[tab]?.last }

    func popToRoot(_ tab: AppTab) {
        show(tab, [])
    }

    /// Opens a route as a fresh stack under Projects: a board always has its
    /// project beneath it, so Back leads somewhere that makes sense.
    func open(_ route: Route) {
        switch route {
        case .project:
            show(.projects, [route])
        case .board(let project, _, _), .roadmap(let project, _), .designDocs(let project, _, _),
             .pluginPage(_, _, let project?):
            show(.projects, [.project(alias: project), route])
        case .mcpAccess, .notificationSettings, .devices, .appearance, .admin, .adminUsers, .adminEmail,
             .adminExtensions, .adminUpdates, .adminThemes, .about, .instances, .pluginPage:
            show(.account, [route])
        }
    }

    private func show(_ tab: AppTab, _ path: [Route]) {
        #if os(macOS)
        if tab == .account, let settings {
            settings.show(tab, path)
            settingsAsked += 1
            return
        }
        #endif
        if offline {
            // The one page of it that works, on its own: what is under it online is not there.
            if let last = path.last, worksOffline(last) {
                showOffline([last])
            } else if let last = path.last, tab != .projects || path.count > 1 {
                refused(last)
            } else {
                // A section's first page, or a project's: the nearest there is to either.
                showOffline([])
            }
            return
        }
        if tab != .offline { paths[.offline] = nil }
        self.tab = tab
        paths[tab] = path
    }

    private func showOffline(_ path: [Route]) {
        if tab != .offline { resume = tab }
        tab = .offline
        paths[.offline] = path
    }

    /// One page back along what is showing, as the bar's Back is.
    func back() {
        guard paths[tab]?.isEmpty == false else { return }
        paths[tab]?.removeLast()
    }

    /// Pushes onto whatever is showing, for a link followed from inside a page.
    func push(_ route: Route) {
        guard !offline || worksOffline(route) else {
            refused(route)
            return
        }
        paths[tab, default: []].append(route)
    }

    func open(_ reference: TaskReference) {
        open(.board(project: reference.projectAlias, board: reference.boardAlias, task: reference.number))
    }
}
