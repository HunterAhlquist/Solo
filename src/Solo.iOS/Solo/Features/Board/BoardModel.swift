import SwiftUI

/// What the detail panel is showing.
enum BoardSelection: Hashable {
    case task(UUID)
    case epic(UUID)
}

/// A top-level row of a board: a group of epics, or an epic that sits in none.
/// Groups and ungrouped epics share one rank order.
nonisolated enum BoardItem: Identifiable, Sendable {
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

/// Which part of a group's outline a row carries. A group is drawn a row at a time — its
/// title, then each of its epics — so that the page builds only the epics in view; the
/// outline around them is drawn in the same pieces, which meet.
nonisolated enum GroupPiece: Sendable {
    /// The title, with epics under it.
    case top
    case middle
    /// The last epic.
    case bottom
    /// The title alone: a group that is folded, or has no epics.
    case whole
}

/// One row of the board page, top to bottom.
nonisolated enum BoardRow: Identifiable, Sendable {
    case group(EpicGroup, GroupPiece)
    /// An epic's lane, and its place in its group's outline if it is in one.
    case lane(Epic, GroupPiece?)

    var id: UUID {
        switch self {
        case .group(let group, _): group.id
        case .lane(let epic, _): epic.id
        }
    }

    /// The group a row is of: a title's own, and a lane's if it is in one.
    var groupId: UUID? {
        switch self {
        case .group(let group, _): group.id
        case .lane(let epic, _): epic.groupId
        }
    }

    /// The rows of a page, from its groups and loose epics in their order. A group's title is
    /// always a row; its epics follow as rows of their own unless it is folded.
    nonisolated static func rows(_ items: [BoardItem], epics: (EpicGroup) -> [Epic],
                                 collapsed: (EpicGroup) -> Bool) -> [BoardRow] {
        items.flatMap { item -> [BoardRow] in
            switch item {
            case .epic(let epic):
                return [.lane(epic, nil)]
            case .group(let group):
                let inside = collapsed(group) ? [] : epics(group)
                guard let last = inside.last else { return [.group(group, .whole)] }
                return [.group(group, .top)] + inside.map { .lane($0, $0.id == last.id ? .bottom : .middle) }
            }
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
    /// A card that has just changed column, for a phone to follow there.
    private(set) var landed: Landing?
    /// The zones a card has landed in. A zone builds its cards as they come into view; on a
    /// phone, whose page goes to a landed card, such a zone is built whole instead, so that the
    /// card is there to go to.
    private var wholeZones: Set<ZoneKey> = []

    struct ZoneKey: Hashable {
        var epicId: UUID?
        var status: TaskState
    }

    func isWhole(epicId: UUID?, status: TaskState) -> Bool {
        wholeZones.contains(ZoneKey(epicId: epicId, status: status))
    }

    struct Landing: Equatable {
        var taskId: UUID
        var status: TaskState
        var token = UUID()
    }

    /// A lane or a group that has just been put down, for the page to go to: the page opens
    /// again around it, and what was put down among a few names may then be a long way off.
    private(set) var laneLanded: Landed?

    struct Landed: Equatable {
        /// The epic, or the group.
        var id: UUID
        var token = UUID()
    }

    /// The page has gone to the card: it flashes once to say which it was.
    func landingFollowed() {
        guard let landed else { return }
        self.landed = nil
        store.pulse(landed.taskId)
    }

    /// What is in hand on this board, and where it would land.
    let drag = CardDragWatch()

    private(set) var collapsedEpics: Set<UUID> = []
    private var groupCollapsed: [UUID: Bool] = [:]
    private var expandedFinished: Set<UUID> = []
    private var openFinished: Set<UUID> = []
    /// Stands in for "no epic" where a set needs a key.
    private static let adHocKey = UUID(uuidString: "00000000-0000-0000-0000-000000000000")!

    private var api: APIClient { session.api }

    init(session: Session) {
        self.session = session
        store = BoardStore(session: session)
        drag.resolve = { [weak self] in self?.lift(named: $0) }
    }

    /// What a thing carried over the board is, from the name it goes by: one of this board's
    /// cards, lanes or groups, and where it stands; or nothing, for anything else.
    private func lift(named payload: String) -> CardDragWatch.Lift? {
        guard canWrite else { return nil }
        if let id = BoardDrag.id(payload, prefix: BoardDrag.epicPrefix) {
            // A lane is carried whole, as it stands.
            guard board?.epics.contains(where: { $0.id == id }) == true, let frame = drag.rows[id] else { return nil }
            return .epic(id, frame: frame)
        }
        if let id = BoardDrag.id(payload, prefix: BoardDrag.groupPrefix) {
            // A group by its title, and out by the padding of the outline that goes with it.
            guard board?.groups?.contains(where: { $0.id == id }) == true, let frame = drag.rows[id] else { return nil }
            return .group(id, frame: frame.insetBy(dx: -10, dy: -10))
        }
        // Where the copy of it in the column it is now in says it stands.
        guard let task = task(dropped: payload),
              let frame = drag.frame(of: task.id, in: ZoneKey(epicId: task.epicId, status: task.status)) else { return nil }
        return .card(task.id, frame: frame)
    }

    // MARK: Layout

    var board: BoardDetail? { store.board }
    var canWrite: Bool { store.canWrite }

    var topLevel: [BoardItem] {
        guard let board else { return [] }
        let items = (board.groups ?? []).map(BoardItem.group) + board.epics.filter { $0.groupId == nil }.map(BoardItem.epic)
        return items.sorted { ($0.rank, $0.id.uuidString) < ($1.rank, $1.id.uuidString) }
    }

    /// The page's rows: every group's title and every lane that is showing, each a row of its
    /// own, so that a group of many epics is built an epic at a time as it comes into view.
    var rows: [BoardRow] {
        BoardRow.rows(topLevel, epics: epics(in:), collapsed: isCollapsed)
    }

    func epics(in group: EpicGroup) -> [Epic] {
        (board?.epics ?? []).filter { $0.groupId == group.id }.sorted { $0.rank < $1.rank }
    }

    /// Every task in a zone, in rank order — folded ones included.
    func tasks(epicId: UUID?, status: TaskState) -> [TaskItem] {
        store.tasks(epicId: epicId, status: status)
    }

    /// How many tasks a lane has, whatever their status.
    func taskCount(epicId: UUID?) -> Int { store.taskCount(epicId: epicId) }

    /// The cards a zone shows. Past 25, a Finished zone folds the overflow away.
    func cards(epicId: UUID?, status: TaskState) -> [TaskItem] {
        let all = tasks(epicId: epicId, status: status)
        guard status == .finished, !expandedFinished.contains(epicId ?? Self.adHocKey) else { return all }
        return Array(all.prefix(BoardDrag.finishedVisibleLimit))
    }

    func hiddenCount(epicId: UUID?, status: TaskState) -> Int {
        guard status == .finished, !expandedFinished.contains(epicId ?? Self.adHocKey) else { return 0 }
        return max(tasks(epicId: epicId, status: status).count - BoardDrag.finishedVisibleLimit, 0)
    }

    func expandFinished(_ epicId: UUID?) {
        expandedFinished.insert(epicId ?? Self.adHocKey)
    }

    /// Whether a lane's Finished column, when it is one that overflows and so starts put away,
    /// has been opened.
    func isFinishedOpen(_ epicId: UUID?) -> Bool { openFinished.contains(epicId ?? Self.adHocKey) }

    func toggleFinished(_ epicId: UUID?) {
        let key = epicId ?? Self.adHocKey
        if openFinished.remove(key) == nil { openFinished.insert(key) }
    }

    func count(_ status: TaskState) -> Int { store.taskCount(status) }

    var isEmpty: Bool {
        guard let board else { return false }
        return board.epics.isEmpty && (board.groups ?? []).isEmpty && board.tasks.isEmpty
    }

    /// Every lane is folded while one is in hand, or a group is: what is being put in order is
    /// then a short list of names, and not a page of cards to be carried past.
    func isCollapsed(_ epic: Epic) -> Bool {
        drag.foldsLanes || collapsedEpics.contains(epic.id)
    }

    /// Whether a lane shows only its header: an epic's that is folded, or the lane of tasks in
    /// no epic while a lane or a group is in hand, which has no fold of its own to be in.
    func isFolded(_ epic: Epic?) -> Bool {
        epic.map(isCollapsed) ?? drag.foldsLanes
    }

    func toggle(_ epic: Epic) {
        if collapsedEpics.remove(epic.id) == nil {
            collapsedEpics.insert(epic.id)
        }
    }

    /// A group starts folded when nothing in it is left to do, and after that
    /// stays however the person left it.
    func isCollapsed(_ group: EpicGroup) -> Bool {
        // Every group is folded while one is in hand.
        if drag.foldsGroups { return true }
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

    /// Which berth the lane or the group in hand is over, for a place on the page.
    func berth(at point: CGPoint) -> CardDragWatch.Berth? {
        guard let carried = drag.epic ?? drag.group else { return nil }
        let places = rows.filter { $0.id != carried }.map { LaneBerths.Row(id: $0.id, group: $0.groupId) }
        return LaneBerths.berth(at: point.y, rows: places, frames: drag.rows, joins: drag.carriesEpic)
    }

    /// The lane or the group in hand was let go: it is put at the berth it was over. It is
    /// shown there at once, as a card is, and the save settles its real rank; what is seen is
    /// the thing in hand going to the room made for it, and then being the row that is there.
    func landLane() -> Bool {
        guard canWrite, let lifted = drag.lifted, let berth = drag.berth else { return false }
        let save: (() async -> Void)?
        let carried: UUID
        switch lifted {
        case .epic(let id, _):
            guard let epic = board?.epics.first(where: { $0.id == id }) else { return false }
            save = move(epic, to: berth)
            carried = id
        case .group(let id, _):
            guard let group = board?.groups?.first(where: { $0.id == id }) else { return false }
            save = move(group, to: berth)
            carried = id
        case .card:
            return false
        }
        drag.settle()
        if let save { Task { await save() } }
        Task {
            // As long as it takes to get there.
            try? await Task.sleep(for: .milliseconds(240))
            drag.landed()
            laneLanded = Landed(id: carried)
        }
        return true
    }

    /// The rank of a row of the page, by the epic's id or the group's.
    private func rank(of id: UUID?) -> Double? {
        guard let id else { return nil }
        return board?.epics.first { $0.id == id }?.rank ?? board?.groups?.first { $0.id == id }?.rank
    }

    /// Puts a lane at a berth, here and now, and gives back the save that makes it so; or
    /// nothing when that is where it is.
    private func move(_ epic: Epic, to berth: CardDragWatch.Berth) -> (() async -> Void)? {
        let groupId: UUID?
        let before: UUID?
        switch berth {
        case .before(let id):
            // Before a lane is in that lane's group. Before a group's title is among the groups, in none.
            groupId = board?.epics.first { $0.id == id }?.groupId
            before = id
        case .inside(let id):
            groupId = id
            before = nil
        case .last:
            groupId = nil
            before = nil
        }
        let all = groupId.flatMap { id in board?.groups?.first { $0.id == id } }.map { epics(in: $0).map(\.id) }
            ?? topLevel.map(\.id)
        let others = all.filter { $0 != epic.id }
        let index = before.flatMap { others.firstIndex(of: $0) } ?? others.count
        if epic.groupId == groupId, all.firstIndex(of: epic.id) == index { return nil }
        let (preceding, following) = RankNeighbors.compute(others, index: index)

        var moved = epic
        moved.groupId = groupId
        moved.rank = Self.rank(between: rank(of: preceding), and: rank(of: following))
        store.apply(moved)
        // A group it was put into is open when the page opens again, or it would have vanished.
        if let groupId { groupCollapsed[groupId] = false }
        return { [self] in
            if let saved = await session.save({
                try await api.moveEpic(epic, groupId: groupId, preceding: preceding, following: following)
            }, refresh: store.refresh) {
                store.apply(saved)
            } else {
                await store.refresh()
            }
        }
    }

    /// Puts a group at a berth among the groups and the lanes in none, which are one list, and
    /// gives back the save that makes it so; or nothing when that is where it is.
    private func move(_ group: EpicGroup, to berth: CardDragWatch.Berth) -> (() async -> Void)? {
        let all = topLevel.map(\.id)
        let others = all.filter { $0 != group.id }
        let index: Int
        switch berth {
        case .before(let id): index = others.firstIndex(of: id) ?? others.count
        case .inside: return nil
        case .last: index = others.count
        }
        if all.firstIndex(of: group.id) == index { return nil }
        let (preceding, following) = RankNeighbors.compute(others, index: index)

        var moved = group
        moved.rank = Self.rank(between: rank(of: preceding), and: rank(of: following))
        store.apply(moved)
        return { [self] in
            if let saved = await session.save({
                try await api.rerankEpicGroup(group, preceding: preceding, following: following)
            }, refresh: store.refresh) {
                store.apply(saved)
            } else {
                await store.refresh()
            }
        }
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

    /// One of this board's tasks.
    func task(_ id: UUID) -> TaskItem? {
        board?.tasks.first { $0.id == id }
    }

    /// A card let go over a place in a zone: it is moved there, and when it is the card that
    /// was in hand it is sent to where it now is, to be put down there.
    func land(_ payload: String, epicId: UUID?, status: TaskState, index: Int?) -> Bool {
        let carried = drag.card
        let moved = drop([payload], epicId: epicId, status: status, index: index)
        // Put back where it came from, it is put down there all the same, and not dropped.
        let home = !moved && carried != nil && task(dropped: payload)?.id == carried
        guard moved || home else { return false }
        if let carried, let task = task(carried),
           let at = cards(epicId: task.epicId, status: task.status).firstIndex(where: { $0.id == carried }) {
            drag.settle(at: CardDragWatch.Slot(epicId: task.epicId, status: task.status, index: at))
        }
        return true
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
        if task.status != status {
            // A card dropped past the fold of a long Finished zone would land out of sight.
            if status == .finished, slot >= BoardDrag.finishedVisibleLimit { expandFinished(epicId) }
            wholeZones.insert(ZoneKey(epicId: epicId, status: status))
            landed = Landing(taskId: task.id, status: status)
        }

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
