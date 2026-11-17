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

    var title: String {
        switch self {
        case .work: "Your Tasks"
        case .projects: "Projects"
        case .inbox: "Inbox"
        case .account: "Account"
        }
    }

    var symbol: String {
        switch self {
        case .work: "person.crop.rectangle.stack"
        case .projects: "square.grid.2x2"
        case .inbox: "bell"
        case .account: "person.crop.circle"
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

    /// A tab's stack, empty until something is pushed.
    subscript(tab: AppTab) -> [Route] {
        get { paths[tab] ?? [] }
        set { paths[tab] = newValue }
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
             .adminExtensions, .adminUpdates, .adminThemes, .about, .pluginPage:
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
        self.tab = tab
        paths[tab] = path
    }

    /// One page back along what is showing, as the bar's Back is.
    func back() {
        guard paths[tab]?.isEmpty == false else { return }
        paths[tab]?.removeLast()
    }

    /// Pushes onto whatever is showing, for a link followed from inside a page.
    func push(_ route: Route) {
        paths[tab, default: []].append(route)
    }

    func open(_ reference: TaskReference) {
        open(.board(project: reference.projectAlias, board: reference.boardAlias, task: reference.number))
    }
}
