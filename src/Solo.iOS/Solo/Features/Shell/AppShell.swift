import SwiftUI

/// The signed-in app. A sidebar and a detail column where there is room for
/// both, a tab bar where there is not; the same Navigator drives either, so
/// rotating or resizing loses nothing.
struct AppShell: View {
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(Session.self) private var session

    var body: some View {
        Group {
            if sizeClass == .regular {
                SplitShell()
            } else {
                TabShell()
            }
        }
        // The page that runs extensions' contributions, kept in the window and out of sight.
        .background(alignment: .topLeading) {
            KitSurfaceAnchor(webView: session.kit.webView)
                .frame(width: 1, height: 1)
                .accessibilityHidden(true)
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
        case .pluginPage(let path, let title, _):
            PluginPageView(path: path, title: title)
        case .mcpAccess:
            McpAccessView()
        case .roadmap(let project, let roadmapId):
            RoadmapView(projectAlias: project, roadmapId: roadmapId)
        case .designDocs(let project, let vaultId, let noteId):
            DesignDocsView(projectAlias: project, vaultId: vaultId, noteId: noteId)
        }
    }
}

/// A navigation stack that shows one of the navigator's sections.
///
/// A phone has one of these per tab, each showing its own section for good.
/// An iPad has one, in the detail column, and which section it shows changes
/// with the sidebar — it is one stack and not one per section because the
/// column has a single navigation controller underneath whatever SwiftUI puts
/// in it: a second stack handed a path while the first still had pages pushed
/// inherits those pages, loses one of its own, and reports that as the reader
/// having gone back.
private struct SectionStack: View {
    @Environment(Navigator.self) private var navigator
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// The section wanted. The one showing follows it — see `show`.
    var tab: AppTab

    /// The stack's own path, mirrored to and from the navigator. It is state
    /// the stack owns, and a NavigationPath rather than an array of routes:
    /// bound to a typed array, a stack in a split view's detail column traps
    /// (`comparisonTypeMismatch`) as soon as its path changes.
    @State private var path = NavigationPath()
    /// What `path` currently holds, since a NavigationPath can't be read back.
    @State private var shown: [Route] = []
    /// The section whose root is at the bottom of the stack.
    @State private var root: AppTab?
    /// True while the stack is being taken somewhere else, when its count says nothing about the reader.
    @State private var settling = false

    private struct Target: Equatable {
        var tab: AppTab
        var routes: [Route]
    }

    var body: some View {
        let root = root ?? tab
        NavigationStack(path: $path) {
            TabRoot(tab: root)
                .id(root)
                // Beside a sidebar a large title is a blank band above the page; the bar carries it
                // instead. On a phone the title stays large but shares its row with the bar's buttons,
                // which gives the page a row back.
                .toolbarTitleDisplayMode(sizeClass == .regular ? .inline : .inlineLarge)
                // Each route is its own page: a board is not the last board shown something new.
                .navigationDestination(for: Route.self) { RouteView(route: $0).id($0) }
        }
        .onChange(of: Target(tab: tab, routes: navigator[tab]), initial: true) { _, target in
            show(target)
        }
        .onChange(of: path.count) { _, count in
            // Every push goes through the navigator, so the only change that
            // starts here is a pop: Back, or the swipe.
            guard !settling, count < shown.count else { return }
            shown = Array(shown.prefix(count))
            navigator[root] = shown
        }
    }

    private func show(_ target: Target) {
        let root = root ?? tab
        guard target.tab != root || target.routes != shown else { return }
        // Further along the way already shown, or back along it: the stack does that itself, and animates it.
        if target.tab == root, target.routes.starts(with: shown) || shown.starts(with: target.routes) {
            shown = target.routes
            path = NavigationPath(target.routes)
            return
        }
        // Somewhere else altogether — another section, or another project's board from the
        // sidebar. Back to the root first, then the new root and its pages on the next turn:
        // the stack gets from nothing to anywhere cleanly, and from one place to another it doesn't.
        settling = true
        var quiet = Transaction()
        quiet.disablesAnimations = true
        withTransaction(quiet) {
            shown = []
            path = NavigationPath()
            self.root = target.tab
        }
        DispatchQueue.main.async {
            // Wherever the navigator points by now, which may not be where it pointed a moment ago.
            let routes = navigator[tab]
            withTransaction(quiet) {
                self.root = tab
                shown = routes
                path = NavigationPath(routes)
            }
            DispatchQueue.main.async { settling = false }
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
                SectionStack(tab: .work).animatesTabChanges()
            }
            Tab(AppTab.projects.title, systemImage: AppTab.projects.symbol, value: AppTab.projects) {
                SectionStack(tab: .projects).animatesTabChanges()
            }
            Tab(AppTab.inbox.title, systemImage: AppTab.inbox.symbol, value: AppTab.inbox) {
                SectionStack(tab: .inbox).animatesTabChanges()
            }
            .badge(session.notifications.unreadCount)
            Tab(AppTab.account.title, systemImage: AppTab.account.symbol, value: AppTab.account) {
                SectionStack(tab: .account).animatesTabChanges()
            }
        }
        .tabBarMinimizeBehavior(.onScrollDown)
    }
}

// MARK: - iPad

private struct SplitShell: View {
    @Environment(Navigator.self) private var navigator
    @State private var columns: NavigationSplitViewVisibility = .automatic
    @State private var panels = DetailPanelCenter()

    var body: some View {
        NavigationSplitView(columnVisibility: $columns) {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 260, ideal: 300, max: 360)
        } detail: {
            SectionStack(tab: navigator.tab)
        }
        .navigationSplitViewStyle(.balanced)
        // Over the whole split view rather than the detail column: the panel stands above the
        // column's navigation bar, which nothing inside the column can do.
        .overlay { DetailPanelColumn() }
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width in
            // Never more than two fifths of the screen: beside an open sidebar there is little to share.
            panels.width = min(448, max(320, width * 0.4))
        }
        // A swipe in from the screen's edge brings the sidebar back, as the system's own sidebars
        // do — instead of going back a page, which is what a stack in the detail column would
        // otherwise make of it. With the sidebar showing, the edge is the sidebar's and this does nothing.
        .gesture(SidebarEdgeSwipe {
            withAnimation { columns = .all }
        })
        .environment(panels)
    }
}

/// A pan in from the screen's leading edge. Every other pan — the stack's
/// swipe back among them — waits for it to fail, so the screen's edge is its
/// alone. A pan that starts anywhere else fails it at once and is unaffected.
private struct SidebarEdgeSwipe: UIGestureRecognizerRepresentable {
    var action: () -> Void

    func makeUIGestureRecognizer(context: Context) -> Recognizer {
        let recognizer = Recognizer()
        recognizer.edges = .left
        return recognizer
    }

    func handleUIGestureRecognizerAction(_ recognizer: Recognizer, context: Context) {
        if recognizer.state == .began { action() }
    }

    final class Recognizer: UIScreenEdgePanGestureRecognizer {
        override func shouldBeRequiredToFail(by other: UIGestureRecognizer) -> Bool {
            other is UIPanGestureRecognizer
        }
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

        }
        .listStyle(.sidebar)
        // What is always wanted stays put at the foot of the column, as the title does at its
        // head; the projects scroll between the two.
        .safeAreaBar(edge: .bottom, spacing: 0) {
            VStack(spacing: 2) {
                InstanceExtensionLinks(inSidebar: true)
                SidebarRow(title: session.user.name, symbol: "person.crop.circle",
                           selected: navigator.tab == .account && navigator.current == nil) {
                    navigator.popToRoot(.account)
                }
                if session.user.isSuperAdmin {
                    SidebarRow(title: "Admin Settings", symbol: "gearshape", badge: session.serverUpdate == nil ? 0 : 1,
                               selected: isAdminRoute) {
                        navigator.open(.admin)
                    }
                }
            }
            .environment(\.sidebarRowStandsAlone, true)
            // Lines the footer's icons and selection up with the list's.
            .padding(.horizontal, 20)
            .padding(.top, 6)
            .padding(.bottom, 4)
        }
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
    @Environment(\.sidebarRowStandsAlone) private var standsAlone

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
            .padding(.horizontal, standsAlone ? 12 : 0)
            .frame(minHeight: standsAlone ? 44 : nil)
            .background {
                if standsAlone, selected {
                    RoundedRectangle(cornerRadius: 12, style: .continuous).fill(theme.accentWash)
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

extension EnvironmentValues {
    /// True for sidebar rows outside the list — the footer — which have to draw their own selection.
    @Entry var sidebarRowStandsAlone = false
}

