import SwiftUI

/// A project's own page: its boards, whatever its extensions add, and — for
/// those allowed — its members, label defaults, audit trail and settings.
struct ProjectHomeView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme

    var alias: String

    @State private var project: Project?
    @State private var section: Pane = .work
    @State private var failed: String?

    enum Pane: String, CaseIterable, Identifiable {
        case work, members, labels, audit, settings

        var id: String { rawValue }

        var title: String {
            switch self {
            case .work: "Boards"
            case .members: "Members"
            case .labels: "Label Defaults"
            case .audit: "Audit"
            case .settings: "Settings"
            }
        }

        var symbol: String {
            switch self {
            case .work: "rectangle.split.3x1"
            case .members: "person.2"
            case .labels: "tag"
            case .audit: "clock.arrow.circlepath"
            case .settings: "gearshape"
            }
        }
    }

    private var access: (isOwner: Bool, canWrite: Bool) {
        let me = project?.members.first { $0.userId == session.user.id }
        let isOwner = me?.isOwner ?? false
        return (session.user.isSuperAdmin || isOwner, isOwner || me?.role == .readWrite)
    }

    /// Tabs follow the API's rules, so nothing offered here comes back 403.
    private var sections: [Pane] {
        var list: [Pane] = [.work, .members]
        if access.canWrite { list.append(.labels) }
        if access.isOwner { list += [.audit, .settings] }
        return list
    }

    var body: some View {
        Group {
            if let project {
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        switch section {
                        case .work:
                            BoardsSection(project: project, canWrite: access.canWrite)
                            ProjectHomeExtensions(project: project, canWrite: access.canWrite)
                        case .members:
                            MembersSection(projectId: project.id, isOwner: access.isOwner) { await load() }
                        case .labels:
                            LabelsSection(scope: .project(project.id), canWrite: access.canWrite)
                        case .audit:
                            AuditSection(projectId: project.id, members: project.members)
                        case .settings:
                            ProjectSettingsSection(project: project) { updated in
                                let renamed = updated.alias != project.alias
                                self.project = updated
                                if renamed {
                                    navigator.open(.project(alias: updated.alias))
                                }
                            }
                        }
                    }
                    .padding(16)
                    .frame(maxWidth: 1100, alignment: .leading)
                    .frame(maxWidth: .infinity)
                }
                .safeAreaInset(edge: .top, spacing: 0) {
                    ChipPicker(options: sections, selection: $section) { Label($0.title, systemImage: $0.symbol) }
                        .padding(.bottom, 6)
                }
            } else if let failed {
                ContentUnavailableView("Couldn't load that project", systemImage: "exclamationmark.triangle",
                                       description: Text(failed))
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .navigationTitle(project?.name ?? "Project")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if let project {
                ToolbarItem(placement: .principal) {
                    HStack(spacing: 8) {
                        EntityIcon(imageId: project.imageId, icon: project.icon, size: 22).foregroundStyle(theme.primary)
                        Text(project.name).font(.headline).lineLimit(1)
                    }
                }
            }
        }
        .task(id: alias) { await load() }
        .refreshable { await load() }
    }

    private func load() async {
        do {
            let fetched = try await session.api.project(alias: alias)
            project = fetched
            session.projects.recordVisit(fetched.id)
            if !sections.contains(section) { section = .work }
        } catch {
            failed = (error as? LocalizedError)?.errorDescription ?? "Try again."
        }
    }
}

/// A row of choices that scrolls when they don't fit, for the places a
/// segmented control would crush its labels.
struct ChipPicker<Option: Hashable & Identifiable, ChipLabel: View>: View {
    @Environment(\.theme) private var theme
    var options: [Option]
    @Binding var selection: Option
    @ViewBuilder var label: (Option) -> ChipLabel

    var body: some View {
        ScrollView(.horizontal) {
            GlassEffectContainer(spacing: 8) {
                HStack(spacing: 8) {
                    ForEach(options) { option in
                        let selected = option == selection
                        Button {
                            withAnimation(.snappy) { selection = option }
                        } label: {
                            label(option)
                                .font(.subheadline.weight(.medium))
                                .padding(.horizontal, 14)
                                .padding(.vertical, 9)
                                .foregroundStyle(selected ? theme.onPrimary : theme.textPrimary)
                        }
                        .buttonStyle(.plain)
                        .glassEffect(selected ? .regular.tint(theme.primary).interactive() : .regular.interactive(), in: .capsule)
                        .accessibilityAddTraits(selected ? .isSelected : [])
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 4)
            }
        }
        .scrollIndicators(.hidden)
    }
}

// MARK: - Boards

/// Fold, don't hide: closed boards live in a faded foldout with one-tap reopen.
/// Creating, closing and reopening are the super admin's; settings and labels
/// are the writers'.
struct BoardsSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme

    var project: Project
    var canWrite: Bool

    @State private var boards: [Board]?
    @State private var creating = false
    @State private var editing: Board?
    @State private var labelling: Board?
    @State private var showClosed = false

    private var canManage: Bool { session.user.isSuperAdmin }
    private let columns = [GridItem(.adaptive(minimum: 240), spacing: 12, alignment: .top)]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionTitle("Boards")
            if let boards {
                let open = boards.filter(\.isOpen).sorted { $0.name < $1.name }
                let closed = boards.filter { !$0.isOpen }.sorted { $0.name < $1.name }

                if open.isEmpty && !canManage {
                    Text("No boards yet.").foregroundStyle(theme.textSecondary)
                }
                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(open) { board in
                        boardCard(board)
                    }
                    if canManage {
                        Button { creating = true } label: {
                            Label("New Board", systemImage: "plus")
                                .font(.subheadline.weight(.medium))
                                .frame(maxWidth: .infinity, minHeight: 34)
                                .padding(14)
                                .foregroundStyle(theme.textSecondary)
                                .overlay(theme.cardShape.strokeBorder(theme.divider, style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])))
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                }

                if !closed.isEmpty {
                    DisclosureGroup(isExpanded: $showClosed) {
                        VStack(spacing: 0) {
                            ForEach(closed) { board in
                                HStack(spacing: 10) {
                                    EntityIcon(imageId: board.imageId, icon: board.icon, size: 20, fallback: "rectangle.split.3x1")
                                    Text(board.name).lineLimit(1)
                                    Spacer()
                                    if canManage {
                                        Button("Reopen") { Task { await setOpen(board, true) } }
                                            .buttonStyle(.glass)
                                    }
                                    Button("View") { navigator.open(.board(project: project.alias, board: board.alias)) }
                                        .buttonStyle(.glass)
                                }
                                .font(.subheadline)
                                .padding(.vertical, 8)
                            }
                        }
                        .padding(.top, 6)
                    } label: {
                        Label("Closed boards (\(closed.count))", systemImage: "archivebox")
                            .font(.subheadline)
                    }
                    .tint(theme.textSecondary)
                    .opacity(showClosed ? 1 : 0.7)
                    .padding(.top, 4)
                }
            } else {
                ProgressView()
            }
        }
        .task(id: project.id) { await load() }
        .sheet(isPresented: $creating) {
            NameIconSheet(title: "New board") { value in
                guard let board = await session.save({
                    try await session.api.createBoard(project.id, BoardRequest(
                        name: value.name, alias: value.alias, icon: value.icon, imageId: value.imageId))
                }) else { return false }
                await reload()
                navigator.open(.board(project: project.alias, board: board.alias))
                return true
            }
        }
        .sheet(item: $editing) { board in
            NameIconSheet(title: "Board settings",
                          initial: NameIconValue(name: board.name, alias: board.alias, icon: board.icon, imageId: board.imageId)) { value in
                let saved = await session.save({
                    try await session.api.updateBoard(board.id, BoardRequest(
                        name: value.name, alias: value.alias, icon: value.icon, imageId: value.imageId, version: board.version))
                }, refresh: load)
                await reload()
                return saved != nil
            }
        }
        .sheet(item: $labelling) { board in
            NavigationStack {
                ScrollView {
                    LabelsSection(scope: .board(board.id), canWrite: canWrite)
                        .padding(16)
                }
                .themedBackground()
                .navigationTitle("\(board.name) labels")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { labelling = nil }
                    }
                }
            }
        }
    }

    private func boardCard(_ board: Board) -> some View {
        HStack(spacing: 12) {
            Button {
                navigator.open(.board(project: project.alias, board: board.alias))
            } label: {
                HStack(spacing: 12) {
                    EntityIcon(imageId: board.imageId, icon: board.icon, size: 26, fallback: "rectangle.split.3x1")
                        .foregroundStyle(theme.primary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(board.name).font(.headline).foregroundStyle(theme.textPrimary).lineLimit(1)
                        Text(board.alias).font(.caption.monospaced()).foregroundStyle(theme.textSecondary)
                    }
                    Spacer(minLength: 0)
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            if canWrite || canManage {
                Menu("Board actions", systemImage: "ellipsis") {
                    if canWrite {
                        Button("Board Settings", systemImage: "gearshape") { editing = board }
                        Button("Labels", systemImage: "tag") { labelling = board }
                    }
                    if canManage {
                        Divider()
                        Button("Close Board", systemImage: "archivebox") { Task { await setOpen(board, false) } }
                    }
                }
                .labelStyle(.iconOnly)
                .frame(width: 32, height: 32)
            }
        }
        .card(padding: 14)
    }

    private func load() async {
        boards = (try? await session.api.boards(project.id)) ?? boards ?? []
    }

    /// The sidebar lists the same boards, so it is told whenever they change.
    private func reload() async {
        await load()
        await session.projects.refreshBoards(project.id)
    }

    private func setOpen(_ board: Board, _ isOpen: Bool) async {
        if await session.save({ try await session.api.setBoardOpen(board.id, isOpen) }) != nil {
            await reload()
        }
    }
}
