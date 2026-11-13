import SwiftUI

/// What the detail panel is showing.
enum BoardSelection: Hashable {
    case task(UUID)
    case epic(UUID)
}

/// A top-level row of a board: a group of epics, or an epic that sits in none.
/// Groups and ungrouped epics share one rank order.
enum BoardItem: Identifiable {
    case group(EpicGroup)
    case epic(Epic)

    var id: UUID {
        switch self {
        case .group(let group): group.id
        case .epic(let epic): epic.id
        }
    }

    var rank: Double {
        switch self {
        case .group(let group): group.rank
        case .epic(let epic): epic.rank
        }
    }
}

/// The board page's state and everything it can do. The store holds what the
/// server said; this holds how the page is folded and what is open, and turns
/// gestures into saves.
@Observable
final class BoardModel {
    let store: BoardStore
    let session: Session

    var selection: BoardSelection?
    var prompt: TextPrompt?
    var confirmation: Confirmation?
    /// The column a phone is showing.
    var column: TaskState = .unstarted

    private(set) var collapsedEpics: Set<UUID> = []
    private var groupCollapsed: [UUID: Bool] = [:]
    private var expandedFinished: Set<UUID> = []
    /// Stands in for "no epic" where a set needs a key.
    private static let adHocKey = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!

    private var api: APIClient { session.api }

    init(session: Session) {
        self.session = session
        store = BoardStore(session: session)
    }

    // MARK: Layout

    var board: BoardDetail? { store.board }
    var canWrite: Bool { store.canWrite }

    var topLevel: [BoardItem] {
        guard let board else { return [] }
        let items = (board.groups ?? []).map(BoardItem.group) + board.epics.filter { $0.groupId == nil }.map(BoardItem.epic)
        return items.sorted { ($0.rank, $0.id.uuidString) < ($1.rank, $1.id.uuidString) }
    }

    func epics(in group: EpicGroup) -> [Epic] {
        (board?.epics ?? []).filter { $0.groupId == group.id }.sorted { $0.rank < $1.rank }
    }

    /// Every task in a zone, in rank order — folded ones included.
    func tasks(epicId: UUID?, status: TaskState) -> [TaskItem] {
        store.orderedTasks.filter { $0.epicId == epicId && $0.status == status }
    }

    func tasks(epicId: UUID?) -> [TaskItem] {
        store.orderedTasks.filter { $0.epicId == epicId }
    }

    /// The cards a zone shows. Past 25, a Finished zone folds the overflow away.
    func cards(epicId: UUID?, status: TaskState) -> [TaskItem] {
        let all = tasks(epicId: epicId, status: status)
        guard status == .finished, !expandedFinished.contains(epicId ?? Self.adHocKey) else { return all }
        return Array(all.prefix(BoardDrag.finishedVisibleLimit))
    }

    func hiddenCount(epicId: UUID?, status: TaskState) -> Int {
        tasks(epicId: epicId, status: status).count - cards(epicId: epicId, status: status).count
    }

    func expandFinished(_ epicId: UUID?) {
        expandedFinished.insert(epicId ?? Self.adHocKey)
    }

    func count(_ status: TaskState) -> Int {
        (board?.tasks ?? []).count { $0.status == status }
    }

    var isEmpty: Bool {
        guard let board else { return false }
        return board.epics.isEmpty && (board.groups ?? []).isEmpty && board.tasks.isEmpty
    }

    func isCollapsed(_ epic: Epic) -> Bool { collapsedEpics.contains(epic.id) }

    func toggle(_ epic: Epic) {
        if collapsedEpics.remove(epic.id) == nil {
            collapsedEpics.insert(epic.id)
        }
    }

    /// A group starts folded when nothing in it is left to do, and after that
    /// stays however the person left it.
    func isCollapsed(_ group: EpicGroup) -> Bool {
        if let known = groupCollapsed[group.id] { return known }
        let epicIds = Set(epics(in: group).map(\.id))
        return !(board?.tasks ?? []).contains { task in
            task.status != .finished && task.epicId.map(epicIds.contains) == true
        }
    }

    func toggle(_ group: EpicGroup) {
        groupCollapsed[group.id] = !isCollapsed(group)
    }

    // MARK: Opening

    func open(_ task: TaskItem) {
        selection = .task(task.id)
        Task { await session.hub.setPresence(EntityTypes.task, task.id, .viewing) }
    }

    func open(_ epic: Epic) {
        selection = .epic(epic.id)
        Task { await session.hub.setPresence(EntityTypes.epic, epic.id, .viewing) }
    }

    func closeDetail() {
        guard selection != nil else { return }
        selection = nil
        if let id = board?.id {
            Task { await session.hub.setPresence(EntityTypes.board, id, .none) }
        }
    }

    /// Landing spot for task links: opens that task once the board is in.
    func openTask(number: Int) {
        guard let board else { return }
        if let task = board.tasks.first(where: { $0.number == number }) {
            column = task.status
            open(task)
        } else {
            session.toasts.show("\(Aliases.serial(board.projectAlias, board.alias, number)) isn't on this board — "
                + "the task may have been deleted.", .warning)
        }
    }

    // MARK: Creating

    func promptNewTask(epicId: UUID?) {
        guard let boardId = board?.id else { return }
        prompt = TextPrompt(title: "New task", placeholder: "Task name") { [self] name in
            if let created = await session.save({
                try await api.createTask(boardId, CreateTaskRequest(name: name, epicId: epicId))
            }) {
                store.apply(created)
                column = created.status
            }
        }
    }

    func promptNewEpic(groupId: UUID? = nil) {
        guard let boardId = board?.id else { return }
        prompt = TextPrompt(title: "New epic", placeholder: "Epic name") { [self] name in
            if await session.save({ try await api.createEpic(boardId, name: name, groupId: groupId) }) != nil {
                if let groupId { groupCollapsed[groupId] = false }
                await store.refresh()
            }
        }
    }

    func promptNewGroup() {
        guard let boardId = board?.id else { return }
        prompt = TextPrompt(title: "New group", placeholder: "Group title") { [self] name in
            await session.save { try await api.createEpicGroup(boardId, name: name) }
            await store.refresh()
        }
    }

    func promptRename(_ group: EpicGroup) {
        prompt = TextPrompt(title: "Edit group", placeholder: "Group title", initial: group.name, action: "Save") { [self] name in
            await session.save({ try await api.updateEpicGroup(group, name: name) }, refresh: store.refresh)
            await store.refresh()
        }
    }

    // MARK: Deleting

    func confirmDelete(_ group: EpicGroup) {
        confirmation = Confirmation(
            title: "Delete group",
            message: "Delete the group \"\(group.name)\"? Its epics and tasks will be kept, with the epics returned "
                + "to the board in their current order.") { [self] in
            await session.perform({ try await api.deleteEpicGroup(group) }, refresh: store.refresh)
            await store.refresh()
        }
    }

    func confirmDelete(_ epic: Epic) {
        confirmation = Confirmation(
            title: "Delete epic",
            message: "Delete the epic \"\(epic.name)\"? Its tasks move to the ad-hoc band.") { [self] in
            if selection == .epic(epic.id) { closeDetail() }
            await session.perform { try await api.deleteEpic(epic.id) }
            await store.refresh()
        }
    }

    func confirmDelete(_ task: TaskItem) {
        confirmation = Confirmation(title: "Delete task", message: "Delete the task \"\(task.name)\"?") { [self] in
            if selection == .task(task.id) { closeDetail() }
            if await session.perform({ try await api.deleteTask(task.id) }) {
                store.removeTask(task.id)
            }
        }
    }

    // MARK: Reordering epics and groups

    /// The ids an epic is ranked among: the top level, or its group's epics.
    private func siblings(of epic: Epic) -> [UUID] {
        if let groupId = epic.groupId, let group = board?.groups?.first(where: { $0.id == groupId }) {
            return epics(in: group).map(\.id)
        }
        return topLevel.map(\.id)
    }

    func canMove(_ epic: Epic, by offset: Int) -> Bool {
        let ids = siblings(of: epic)
        guard let index = ids.firstIndex(of: epic.id) else { return false }
        return ids.indices.contains(index + offset)
    }

    func move(_ epic: Epic, by offset: Int) async {
        let ids = siblings(of: epic)
        guard let index = ids.firstIndex(of: epic.id), ids.indices.contains(index + offset) else { return }
        let (preceding, following) = RankNeighbors.compute(ids.filter { $0 != epic.id }, index: index + offset)
        await session.save({
            try await api.moveEpic(epic, groupId: epic.groupId, preceding: preceding, following: following)
        }, refresh: store.refresh)
        await store.refresh()
    }

    /// Into a group (at its end), or out to the top level (at the end).
    func move(_ epic: Epic, toGroup groupId: UUID?) async {
        guard epic.groupId != groupId else { return }
        let destination: [UUID] = groupId.flatMap { id in board?.groups?.first { $0.id == id } }
            .map { epics(in: $0).map(\.id) } ?? topLevel.map(\.id)
        let (preceding, following) = RankNeighbors.compute(destination.filter { $0 != epic.id }, index: destination.count)
        await session.save({
            try await api.moveEpic(epic, groupId: groupId, preceding: preceding, following: following)
        }, refresh: store.refresh)
        if let groupId { groupCollapsed[groupId] = false }
        await store.refresh()
    }

    /// A lane dropped onto another lane's header lands just before it, in that lane's group.
    func dropEpic(_ payload: String, before target: Epic) async {
        guard canWrite, let id = BoardDrag.id(payload, prefix: BoardDrag.epicPrefix), id != target.id,
              let epic = board?.epics.first(where: { $0.id == id }) else { return }
        let ids = siblings(of: target).filter { $0 != epic.id }
        let (preceding, following) = RankNeighbors.compute(ids, index: ids.firstIndex(of: target.id) ?? ids.count)
        await session.save({
            try await api.moveEpic(epic, groupId: target.groupId, preceding: preceding, following: following)
        }, refresh: store.refresh)
        await store.refresh()
    }

    func canMove(_ group: EpicGroup, by offset: Int) -> Bool {
        let ids = topLevel.map(\.id)
        guard let index = ids.firstIndex(of: group.id) else { return false }
        return ids.indices.contains(index + offset)
    }

    func move(_ group: EpicGroup, by offset: Int) async {
        let ids = topLevel.map(\.id)
        guard let index = ids.firstIndex(of: group.id), ids.indices.contains(index + offset) else { return }
        let (preceding, following) = RankNeighbors.compute(ids.filter { $0 != group.id }, index: index + offset)
        await session.save({
            try await api.rerankEpicGroup(group, preceding: preceding, following: following)
        }, refresh: store.refresh)
        await store.refresh()
    }

    // MARK: Moving cards

    /// The task a dropped payload names, when it is one of this board's.
    func task(dropped payload: String) -> TaskItem? {
        guard let board, let reference = TaskReference(serial: payload),
              reference.projectAlias == board.projectAlias, reference.boardAlias == board.alias else { return nil }
        return board.tasks.first { $0.number == reference.number }
    }

    /// A card let go over a zone. `index` counts the cards the zone was showing,
    /// the dragged one included when it came from there. Nothing about a drop
    /// is trusted: the payload is looked up again before anything is written.
    @discardableResult
    func drop(_ payloads: [String], epicId: UUID?, status: TaskState, index: Int?) -> Bool {
        guard canWrite, let payload = payloads.first, let task = task(dropped: payload) else { return false }
        let shown = cards(epicId: epicId, status: status)
        var slot = index ?? shown.count
        if let from = shown.firstIndex(where: { $0.id == task.id }), from < slot {
            slot -= 1
        }
        let others = tasks(epicId: epicId, status: status).filter { $0.id != task.id }
        slot = min(max(slot, 0), others.count)

        let sameZone = task.epicId == epicId && task.status == status
        if sameZone, tasks(epicId: epicId, status: status).firstIndex(where: { $0.id == task.id }) == slot {
            return false
        }

        // Shown where it was dropped at once; the saves below settle its real rank.
        var moved = task
        moved.epicId = epicId
        moved.status = status
        moved.rank = Self.rank(between: slot > 0 ? others[slot - 1].rank : nil,
                               and: slot < others.count ? others[slot].rank : nil)
        withAnimation(.snappy(duration: 0.25)) { store.apply(moved) }

        let (preceding, following) = RankNeighbors.compute(others.map(\.id), index: slot)
        Task {
            // Order matters: the task must belong to the target lane and column
            // before its rank is read against that zone's neighbours.
            if task.epicId != epicId {
                guard let saved = await session.save({ try await api.setTaskEpic(task.id, epicId) },
                                                     refresh: store.refresh) else { return await store.refresh() }
                store.apply(saved)
            }
            if task.status != status {
                guard let saved = await session.save({ try await api.setTaskStatus(task.id, status) },
                                                     refresh: store.refresh) else { return await store.refresh() }
                store.apply(saved)
            }
            if preceding != nil || following != nil {
                if let saved = await session.save({
                    try await api.rerankTask(task.id, preceding: preceding, following: following)
                }, refresh: store.refresh) {
                    store.apply(saved)
                }
            }
        }
        return true
    }

    /// Moves a card to another column without a drag: the menu's way.
    func setStatus(_ task: TaskItem, _ status: TaskState) {
        guard task.status != status else { return }
        drop([store.serial(task)], epicId: task.epicId, status: status, index: nil)
    }

    private static func rank(between lower: Double?, and upper: Double?) -> Double {
        switch (lower, upper) {
        case let (lower?, upper?): (lower + upper) / 2
        case let (lower?, nil): lower + 1
        case let (nil, upper?): upper - 1
        default: 0
        }
    }
}
