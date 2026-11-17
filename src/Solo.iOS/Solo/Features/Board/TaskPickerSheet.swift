import SwiftUI

/// Finds a task by name, serial number or epic, across a project's boards or —
/// with no project given — across every project the person belongs to.
struct TaskPickerSheet: View {
    @Environment(Session.self) private var session
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    var title = "Choose a task"
    /// Nil searches every project.
    var projectId: UUID?
    var exclude: Set<UUID> = []
    /// Only tasks assigned to this person, for a roadmap of their own.
    var assignedTo: UUID?
    var onPick: (TaskReference, TaskItem, BoardDetail) -> Void

    @State private var boards: [BoardDetail]?
    @State private var query = ""
    @State private var includeFinished = false
    @State private var includeClosed = false

    init(title: String = "Choose a task", projectId: UUID? = nil, exclude: Set<UUID> = [],
         onPick: @escaping (TaskReference) -> Void) {
        self.title = title
        self.projectId = projectId
        self.exclude = exclude
        self.onPick = { reference, _, _ in onPick(reference) }
    }

    init(title: String = "Choose a task", projectId: UUID? = nil, exclude: Set<UUID> = [], assignedTo: UUID? = nil,
         onPickTask: @escaping (TaskItem, BoardDetail) -> Void) {
        self.title = title
        self.projectId = projectId
        self.exclude = exclude
        self.assignedTo = assignedTo
        self.onPick = { _, task, board in onPickTask(task, board) }
    }

    private struct Lane: Identifiable {
        var id: String
        var epic: Epic?
        var tasks: [TaskItem]
    }

    private struct Result: Identifiable {
        var board: BoardDetail
        var lanes: [Lane]
        var id: UUID { board.id }
    }

    var body: some View {
        NavigationStack {
            GroupedList {
                if let boards {
                    let results = filter(boards)
                    if results.isEmpty {
                        if query.isEmpty {
                            ContentUnavailableView(
                                assignedTo == nil ? "Nothing left to add" : "Every task assigned to you is already here",
                                systemImage: "checkmark.circle",
                                description: Text("Finished tasks and closed boards are hidden. The filter can show them."))
                                .listRowBackground(Color.clear)
                        } else {
                            ContentUnavailableView.search(text: query)
                                .listRowBackground(Color.clear)
                        }
                    }
                    ForEach(results) { result in
                        Section {
                            ForEach(result.lanes) { lane in
                                if let epic = lane.epic {
                                    Text(epic.name)
                                        .font(.caption.weight(.semibold))
                                        .foregroundStyle(theme.textSecondary)
                                        .themedRow()
                                }
                                ForEach(lane.tasks) { task in
                                    Button {
                                        let reference = TaskReference(projectAlias: result.board.projectAlias,
                                                                      boardAlias: result.board.alias, number: task.number)
                                        dismiss()
                                        onPick(reference, task, result.board)
                                    } label: {
                                        HStack(spacing: 10) {
                                            Image(systemName: task.status.symbol)
                                                .foregroundStyle(task.status == .finished ? theme.success : theme.textSecondary)
                                            VStack(alignment: .leading, spacing: 2) {
                                                Text(task.name).foregroundStyle(theme.textPrimary)
                                                Text(Aliases.serial(result.board.projectAlias, result.board.alias, task.number))
                                                    .font(.caption.monospaced())
                                                    .foregroundStyle(theme.textSecondary)
                                            }
                                        }
                                    }
                                    .themedRow()
                                }
                            }
                        } header: {
                            Text(projectId == nil ? "\(result.board.projectAlias) · \(result.board.name)" : result.board.name)
                        }
                    }
                } else {
                    ProgressView().frame(maxWidth: .infinity).listRowBackground(Color.clear)
                }
            }
            .themedList()
            .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always), prompt: "Name, serial or epic")
            .navigationTitle(title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Menu("Filter", systemImage: "line.3.horizontal.decrease") {
                        Toggle("Finished tasks", isOn: $includeFinished)
                        Toggle("Closed boards", isOn: $includeClosed)
                    }
                    .menuToggles()
                }
            }
            .task { await load() }
        }
    }

    private func load() async {
        let api = session.api
        do {
            let projectIds = projectId.map { [$0] } ?? session.projects.projects.map(\.id)
            var loaded: [BoardDetail] = []
            for id in projectIds {
                for board in try await api.boards(id) {
                    loaded.append(try await api.board(board.id))
                }
            }
            boards = loaded
        } catch {
            boards = []
            session.toasts.error(error, fallback: "Couldn't load tasks.")
        }
    }

    private func filter(_ boards: [BoardDetail]) -> [Result] {
        let text = query.trimmingCharacters(in: .whitespaces)
        return boards.filter { includeClosed || $0.isOpen }.compactMap { board in
            let epics = Self.epicsInBoardOrder(board)
            let known = Dictionary(uniqueKeysWithValues: epics.map { ($0.id, $0) })
            let shown = board.tasks
                .filter { !exclude.contains($0.id) && (includeFinished || $0.status != .finished) }
                .filter { task in assignedTo.map(task.assigneeIds.contains) ?? true }
                .filter { task in
                    text.isEmpty || task.name.localizedCaseInsensitiveContains(text)
                        || Aliases.serial(board.projectAlias, board.alias, task.number).localizedCaseInsensitiveContains(text)
                        || (task.epicId.flatMap { known[$0] }?.name.localizedCaseInsensitiveContains(text) ?? false)
                }
                .sorted { $0.rank < $1.rank }
            let grouped = Dictionary(grouping: shown) { task in task.epicId.flatMap { known[$0] }?.id }
            var lanes = epics.map { Lane(id: $0.id.uuidString, epic: $0, tasks: grouped[$0.id] ?? []) }
            lanes.append(Lane(id: "adhoc", epic: nil, tasks: grouped[nil] ?? []))
            lanes.removeAll { $0.tasks.isEmpty }
            return lanes.isEmpty ? nil : Result(board: board, lanes: lanes)
        }
    }

    /// Epics in the order the board shows them: groups and loose epics share one rank order.
    static func epicsInBoardOrder(_ board: BoardDetail) -> [Epic] {
        let top: [(id: UUID, rank: Double, isGroup: Bool)] =
            (board.groups ?? []).map { ($0.id, $0.rank, true) }
            + board.epics.filter { $0.groupId == nil }.map { ($0.id, $0.rank, false) }
        return top.sorted { ($0.rank, $0.id.uuidString) < ($1.rank, $1.id.uuidString) }.flatMap { item in
            item.isGroup
                ? board.epics.filter { $0.groupId == item.id }.sorted { $0.rank < $1.rank }
                : board.epics.filter { $0.id == item.id }
        }
    }
}
