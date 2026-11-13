import SwiftUI

/// The landing page: one person's work across every project, laid out as a
/// board. The same cards and the same columns, moved with the same drag — but
/// nothing is created here. A task belongs to a board, and this page only
/// shows where each one came from and lets the person move it along.
struct DashboardView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.horizontalSizeClass) private var sizeClass

    @State private var column: TaskState = .unstarted
    @State private var finishedExpanded = false
    @State private var openTask: UUID?
    @State private var failed: String?

    private struct Card: Identifiable {
        var task: TaskItem
        var board: Board
        var project: ProjectSummary
        var origin: TaskOrigin
        var id: UUID { task.id }
        var serial: String { Aliases.serial(project.alias, board.alias, task.number) }
    }

    var body: some View {
        let store = session.assigned
        Group {
            if let work = store.work {
                let cards = cards(from: work)
                if sizeClass == .regular {
                    ScrollView {
                        VStack(spacing: 16) {
                            DashboardExtensions(openTask: $openTask)
                            HStack(alignment: .top, spacing: 10) {
                                ForEach(TaskState.allCases) { status in
                                    VStack(spacing: 8) {
                                        HStack(spacing: 6) {
                                            Image(systemName: status.symbol)
                                            Text(status.title)
                                            Text("\(cards.count { $0.task.status == status })")
                                                .monospacedDigit().foregroundStyle(theme.textSecondary)
                                        }
                                        .font(.footnote.weight(.semibold))
                                        .frame(maxWidth: .infinity)
                                        .padding(.vertical, 9)
                                        .glassEffect(.regular, in: .capsule)
                                        zone(cards, status, work)
                                    }
                                }
                            }
                            if cards.isEmpty { empty }
                        }
                        .padding(16)
                    }
                } else {
                    TabView(selection: $column) {
                        ForEach(TaskState.allCases) { status in
                            ScrollView {
                                VStack(spacing: 14) {
                                    DashboardExtensions(openTask: $openTask)
                                    zone(cards, status, work)
                                    if cards.isEmpty { empty }
                                }
                                .padding(.horizontal, 14)
                                .padding(.bottom, 24)
                            }
                            .tag(status)
                        }
                    }
                    .tabViewStyle(.page(indexDisplayMode: .never))
                    .safeAreaInset(edge: .top, spacing: 8) {
                        ColumnSwitcher(selection: $column, count: { status in cards.count { $0.task.status == status } }) {
                            payloads, status in
                            drop(payloads, status, cards)
                        }
                        .padding(.horizontal, 14)
                    }
                }
            } else if let failed {
                ContentUnavailableView("Couldn't load your tasks", systemImage: "exclamationmark.triangle",
                                       description: Text(failed))
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .navigationTitle("Your Tasks")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) { ConnectionIndicator() }
        }
        .task {
            do {
                try await store.load()
            } catch {
                failed = (error as? LocalizedError)?.errorDescription ?? "Try again."
            }
        }
        .refreshable { await store.refresh() }
        .taskDetail($openTask)
    }

    private var empty: some View {
        ContentUnavailableView("Nothing is assigned to you.", systemImage: "person.crop.rectangle.stack")
            .padding(.top, 24)
    }

    private func cards(from work: AssignedTasks) -> [Card] {
        let boards = Dictionary(uniqueKeysWithValues: work.boards.map { ($0.id, $0) })
        let projects = Dictionary(uniqueKeysWithValues: work.projects.map { ($0.id, $0) })
        let epics = Dictionary(uniqueKeysWithValues: (work.epics ?? []).map { ($0.id, $0) })
        let groups = Dictionary(uniqueKeysWithValues: (work.epicGroups ?? []).map { ($0.id, $0) })
        return work.tasks.compactMap { task -> Card? in
            guard let board = boards[task.boardId], let project = projects[board.projectId] else { return nil }
            let epic = task.epicId.flatMap { epics[$0] }
            let group = epic?.groupId.flatMap { groups[$0] }
            let epicLine = epic.map { epic in group.map { "\($0.name) • \(epic.name)" } ?? epic.name }
            return Card(task: task, board: board, project: project,
                        origin: TaskOrigin(projectName: project.name, boardName: board.name,
                                           route: .board(project: project.alias, board: board.alias), epicLine: epicLine))
        }
        .sorted {
            ($0.project.name.lowercased(), $0.board.name.lowercased(), $0.task.rank)
                < ($1.project.name.lowercased(), $1.board.name.lowercased(), $1.task.rank)
        }
    }

    @ViewBuilder
    private func zone(_ all: [Card], _ status: TaskState, _ work: AssignedTasks) -> some View {
        let inColumn = all.filter { $0.task.status == status }
        let folded = status == .finished && !finishedExpanded
        let shown = folded ? Array(inColumn.prefix(BoardDrag.finishedVisibleLimit)) : inColumn
        DashboardZone(status: status, onDrop: { drop($0, status, all) }) {
            ForEach(shown) { card in
                let view = TaskCardView(
                    task: card.task, projectId: card.project.id,
                    label: card.task.labelId.flatMap { id in work.labels.first { $0.id == id } },
                    assignees: work.members.filter { member in member.userId.map(card.task.assigneeIds.contains) ?? false },
                    pulse: session.assigned.pulsing.contains(card.task.id),
                    origin: card.origin, canWrite: card.project.canWrite)
                Button { openTask = card.task.id } label: { view }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button("Open", systemImage: "arrow.up.forward.app") { openTask = card.task.id }
                        Button("Go to Board", systemImage: "rectangle.split.3x1") {
                            session.navigator.open(card.origin.route)
                        }
                        if card.project.canWrite {
                            Menu("Move to", systemImage: "arrow.left.arrow.right") {
                                ForEach(TaskState.allCases.filter { $0 != card.task.status }) { status in
                                    Button(status.title, systemImage: status.symbol) { setStatus(card, status) }
                                }
                            }
                        }
                    }
                    .draggable(card.serial) {
                        view.frame(width: 240).environment(\.theme, theme).environment(session)
                            .environment(session.navigator)
                    }
            }
            if shown.count < inColumn.count {
                ShowAllButton(hidden: inColumn.count - shown.count) { withAnimation { finishedExpanded = true } }
            }
        }
    }

    private func drop(_ payloads: [String], _ status: TaskState, _ cards: [Card]) -> Bool {
        guard let payload = payloads.first, let card = cards.first(where: { $0.serial == payload }),
              card.project.canWrite, card.task.status != status else { return false }
        setStatus(card, status)
        return true
    }

    /// Only the column changes here: a card's place among its neighbours is
    /// its board's business.
    private func setStatus(_ card: Card, _ status: TaskState) {
        var moved = card.task
        moved.status = status
        withAnimation(.snappy(duration: 0.25)) { session.assigned.apply(moved) }
        Task {
            if let saved = await session.save({ try await session.api.setTaskStatus(card.task.id, status) },
                                              refresh: session.assigned.refresh) {
                session.assigned.apply(saved)
            } else {
                await session.assigned.refresh()
            }
        }
    }
}

private struct DashboardZone<Content: View>: View {
    @Environment(\.theme) private var theme
    var status: TaskState
    var onDrop: ([String]) -> Bool
    @ViewBuilder var content: Content
    @State private var targeted = false

    var body: some View {
        VStack(spacing: 8) {
            content
            Color.clear.frame(height: 24)
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .padding(6)
        .glassEffect(targeted ? .regular.tint(theme.accentWash) : .regular, in: theme.cardShape)
        .dropDestination(for: String.self) { payloads, _ in onDrop(payloads) } isTargeted: { targeted = $0 }
        .animation(.easeOut(duration: 0.15), value: targeted)
    }
}
