import SwiftUI

/// The signed-in app. A sidebar and a detail column where there is room for
/// both, a tab bar where there is not; the same Navigator drives either, so
/// rotating or resizing loses nothing.
struct AppShell: View {
    @Environment(\.horizontalSizeClass) private var sizeClass

    var body: some View {
        if sizeClass == .regular {
            SplitShell()
        } else {
            TabShell()
        }
    }
}

/// The root page of each section, and what every route leads to.
struct TabRoot: View {
    var tab: AppTab

    var body: some View {
        switch tab {
        case .work: DashboardView()
        case .projects: ProjectsView()
        case .inbox: InboxView()
        case .account: AccountView()
        }
    }
}

struct RouteView: View {
    var route: Route

    var body: some View {
        switch route {
        case .project(let alias):
            ProjectHomeView(alias: alias)
        case .board(let project, let board, let task):
            BoardView(projectAlias: project, boardAlias: board, taskNumber: task)
        case .notificationSettings:
            NotificationSettingsView()
        case .devices:
            DevicesView()
        case .appearance:
            AppearanceView()
        case .admin:
            AdminSettingsView()
        case .adminUsers:
            AdminUsersView()
        case .adminEmail:
            AdminEmailView()
        case .adminExtensions:
            AdminExtensionsView()
        case .adminUpdates:
            AdminUpdatesView()
        case .adminThemes:
            AdminThemesView()
        case .about:
            AboutView()
        case .mcpAccess:
            McpAccessView()
        case .roadmap(let project, let roadmapId):
            RoadmapView(projectAlias: project, roadmapId: roadmapId)
        case .designDocs(let project, let vaultId, let noteId):
            DesignDocsView(projectAlias: project, vaultId: vaultId, noteId: noteId)
        }
    }
}

/// One section's navigation stack.
private struct SectionStack: View {
    @Environment(Navigator.self) private var navigator
    var tab: AppTab

    /// The stack's own path, mirrored to and from the navigator. It is state
    /// the stack owns, and a NavigationPath rather than an array of routes:
    /// bound to a typed array, a stack in a split view's detail column traps
    /// (`comparisonTypeMismatch`) as soon as its path changes.
    @State private var path = NavigationPath()
    /// What `path` currently holds, since a NavigationPath can't be read back.
    @State private var shown: [Route] = []

    var body: some View {
        NavigationStack(path: $path) {
            TabRoot(tab: tab)
                .navigationDestination(for: Route.self) { RouteView(route: $0) }
        }
        .onChange(of: navigator[tab], initial: true) { _, routes in
            guard routes != shown else { return }
            shown = routes
            path = NavigationPath(routes)
        }
        .onChange(of: path.count) { _, count in
            // Every push goes through the navigator, so the only change that
            // starts here is a pop: Back, or the swipe.
            guard count < shown.count else { return }
            shown = Array(shown.prefix(count))
            navigator[tab] = shown
        }
    }
}

/// A row that pushes a route. Used in place of a value link, because the
/// navigator has to hear about every push.
struct RouteLink<Content: View>: View {
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var route: Route
    @ViewBuilder var label: Content

    var body: some View {
        Button {
            navigator.push(route)
        } label: {
            HStack {
                label
                Spacer(minLength: 8)
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(theme.textDisabled)
            }
            .contentShape(.rect)
        }
        .foregroundStyle(theme.textPrimary)
    }
}

// MARK: - iPhone

private struct TabShell: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator

    var body: some View {
        @Bindable var navigator = navigator
        TabView(selection: $navigator.tab) {
            Tab(AppTab.work.title, systemImage: AppTab.work.symbol, value: AppTab.work) {
                SectionStack(tab: .work)
            }
            Tab(AppTab.projects.title, systemImage: AppTab.projects.symbol, value: AppTab.projects) {
                SectionStack(tab: .projects)
            }
            Tab(AppTab.inbox.title, systemImage: AppTab.inbox.symbol, value: AppTab.inbox) {
                SectionStack(tab: .inbox)
            }
            .badge(session.notifications.unreadCount)
            Tab(AppTab.account.title, systemImage: AppTab.account.symbol, value: AppTab.account) {
                SectionStack(tab: .account)
            }
        }
        .tabBarMinimizeBehavior(.onScrollDown)
    }
}

// MARK: - iPad

private struct SplitShell: View {
    @Environment(Navigator.self) private var navigator
    @State private var columns: NavigationSplitViewVisibility = .automatic

    var body: some View {
        NavigationSplitView(columnVisibility: $columns) {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 260, ideal: 300, max: 360)
        } detail: {
            SectionStack(tab: navigator.tab)
                .id(navigator.tab)
        }
        .navigationSplitViewStyle(.balanced)
    }
}

/// The app's map: every project, and inside each one its open boards and
/// whatever its extensions add. Creating things happens where they live.
struct SidebarView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @State private var creatingProject = false
    @State private var newBoardProject: ProjectSummary?

    var body: some View {
        let nav = session.projects
        List {
            Section {
                SidebarRow(title: "Your Tasks", symbol: AppTab.work.symbol,
                           selected: navigator.tab == .work && navigator.current == nil) {
                    navigator.popToRoot(.work)
                }
                SidebarRow(title: "All Projects", symbol: AppTab.projects.symbol,
                           selected: navigator.tab == .projects && navigator.current == nil) {
                    navigator.popToRoot(.projects)
                }
                SidebarRow(title: "Inbox", symbol: AppTab.inbox.symbol, badge: session.notifications.unreadCount,
                           selected: navigator.tab == .inbox) {
                    navigator.popToRoot(.inbox)
                }
            }

            Section("Projects") {
                if nav.hasLoaded && nav.projects.isEmpty {
                    Text("No projects yet.")
                        .font(.footnote)
                        .foregroundStyle(theme.textSecondary)
                }
                ForEach(nav.ordered) { project in
                    DisclosureGroup(isExpanded: expansion(project.id)) {
                        ProjectBranch(project: project, onAddBoard: { newBoardProject = project })
                    } label: {
                        HStack(spacing: 10) {
                            EntityIcon(imageId: project.imageId, icon: project.icon, size: 22)
                                .foregroundStyle(theme.primary)
                            Text(project.name).lineLimit(1)
                        }
                    }
                }
                .onMove { nav.move(from: $0, to: $1) }

                if session.user.isSuperAdmin {
                    Button("Add Project", systemImage: "folder.badge.plus") { creatingProject = true }
                        .foregroundStyle(theme.textSecondary)
                }
            }

            Section {
                InstanceExtensionLinks(inSidebar: true)
                SidebarRow(title: session.user.name, symbol: "person.crop.circle",
                           selected: navigator.tab == .account && navigator.current == nil) {
                    navigator.popToRoot(.account)
                }
                if session.user.isSuperAdmin {
                    SidebarRow(title: "Admin Settings", symbol: "gearshape", selected: isAdminRoute) {
                        navigator.open(.admin)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        .navigationTitle(session.siteName)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                ConnectionIndicator()
            }
        }

        .refreshable {
            await nav.refreshProjects()
            for id in nav.expanded {
                await nav.refreshBoards(id)
            }
        }
        .sheet(isPresented: $creatingProject) {
            NameIconSheet(title: "New project") { value in
                await createProject(value)
            }
        }
        .sheet(item: $newBoardProject) { project in
            NameIconSheet(title: "New board") { value in
                await createBoard(value, in: project)
            }
        }
    }

    private var isAdminRoute: Bool {
        switch navigator.current {
        case .admin, .adminUsers, .adminEmail, .adminExtensions, .adminUpdates, .adminThemes: navigator.tab == .account
        default: false
        }
    }

    private func expansion(_ id: UUID) -> Binding<Bool> {
        Binding(
            get: { session.projects.expanded.contains(id) },
            set: { expanded in
                if expanded {
                    session.projects.expand(id)
                } else {
                    session.projects.expanded.remove(id)
                }
            })
    }

    private func createProject(_ value: NameIconValue) async -> Bool {
        guard let project = await session.save({
            try await session.api.createProject(ProjectRequest(name: value.name, alias: value.alias, icon: value.icon,
                                                               imageId: value.imageId))
        }) else { return false }
        await session.projects.refreshProjects()
        navigator.open(.project(alias: project.alias))
        return true
    }

    private func createBoard(_ value: NameIconValue, in project: ProjectSummary) async -> Bool {
        guard let board = await session.save({
            try await session.api.createBoard(project.id, BoardRequest(name: value.name, alias: value.alias,
                                                                      icon: value.icon, imageId: value.imageId))
        }) else { return false }
        await session.projects.refreshBoards(project.id)
        navigator.open(.board(project: project.alias, board: board.alias))
        return true
    }
}

/// What unfolds under a project: its open boards, each extension's section, and the way to its overview.
private struct ProjectBranch: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary
    var onAddBoard: () -> Void

    var body: some View {
        if let boards = session.projects.openBoards(project.id) {
            ForEach(boards) { board in
                SidebarRow(title: board.name, selected: isCurrent(board)) {
                    navigator.open(.board(project: project.alias, board: board.alias))
                } icon: {
                    EntityIcon(imageId: board.imageId, icon: board.icon, size: 18, fallback: "rectangle.split.3x1")
                }
            }
            if boards.isEmpty {
                Text("No open boards.")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
            }
            if session.user.isSuperAdmin {
                Button("Add Board", systemImage: "plus", action: onAddBoard)
                    .font(.subheadline)
                    .foregroundStyle(theme.textSecondary)
            }
            SidebarProjectExtensions(project: project)
            SidebarRow(title: "Project Overview", symbol: "slider.horizontal.3",
                       selected: navigator.tab == .projects && navigator.current == .project(alias: project.alias)) {
                navigator.open(.project(alias: project.alias))
            }
        } else {
            ProgressView()
                .task { await session.projects.ensureBoards(project.id) }
        }
    }

    private func isCurrent(_ board: Board) -> Bool {
        if case .board(let projectAlias, let boardAlias, _) = navigator.current {
            return projectAlias == project.alias && boardAlias == board.alias
        }
        return false
    }
}

/// A sidebar entry. Selection is drawn here rather than left to the list,
/// because what is selected is a route, not a row.
struct SidebarRow<Icon: View>: View {
    @Environment(\.theme) private var theme
    var title: String
    var badge: Int = 0
    var selected: Bool
    var action: () -> Void
    @ViewBuilder var icon: Icon

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                icon
                    .foregroundStyle(theme.primary)
                    .frame(width: 24)
                Text(title)
                    .fontWeight(selected ? .semibold : .regular)
                    .foregroundStyle(theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 0)
                if badge > 0 {
                    Text("\(badge)")
                        .font(.caption.weight(.semibold).monospacedDigit())
                        .padding(.horizontal, 7)
                        .padding(.vertical, 2)
                        .foregroundStyle(theme.primary)
                        .background(theme.accentWash, in: .capsule)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .listRowBackground(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(selected ? theme.accentWash : .clear)
                .padding(.horizontal, 4))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

extension SidebarRow where Icon == Image {
    init(title: String, symbol: String, badge: Int = 0, selected: Bool, action: @escaping () -> Void) {
        self.init(title: title, badge: badge, selected: selected, action: action) {
            Image(systemName: symbol)
        }
    }
}
