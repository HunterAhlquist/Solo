import Foundation
import Observation

/// One board's state: the render payload, the project's members, and who else
/// is here. Change events carry field names, not values, so a change is
/// answered with a refetch — one task where the event names one, the whole
/// board (debounced) for anything else.
@Observable
final class BoardStore {
    private(set) var board: BoardDetail?
    private(set) var members: [ProjectMember] = []
    private(set) var presence: [PresenceEntry] = []
    /// Entities a remote change just landed on, for the one-shot outline pulse.
    private(set) var pulsing: Set<UUID> = []
    /// Bumped whenever someone else adds, edits or removes a comment.
    private(set) var commentsRevision = 0
    private(set) var loadError: String?

    @ObservationIgnored let session: Session
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var refetch: Task<Void, Never>?
    @ObservationIgnored private var attached = false

    private var api: APIClient { session.api }
    private var hub: HubClient { session.hub }

    init(session: Session) {
        self.session = session
    }

    // MARK: Derived

    var isOpen: Bool { board?.isOpen ?? false }

    /// The caller's standing in this project: owner (or super admin) may
    /// moderate, owner or ReadWrite may write.
    var access: (canModerate: Bool, canWrite: Bool) {
        let me = members.first { $0.userId == session.user.id }
        let isOwner = me?.isOwner ?? false
        return (session.user.isSuperAdmin || isOwner, isOwner || me?.role == .readWrite)
    }

    /// A closed board is read-only for everyone, comments included.
    var canWrite: Bool { access.canWrite && isOpen }

    var orderedTasks: [TaskItem] { (board?.tasks ?? []).sorted { $0.rank < $1.rank } }

    func label(_ id: UUID?) -> SoloLabel? {
        guard let id else { return nil }
        return board?.labels.first { $0.id == id }
    }

    func labels(_ kind: LabelKind) -> [SoloLabel] {
        (board?.labels ?? []).filter { $0.kind == kind }
    }

    func members(_ userIds: [UUID]) -> [ProjectMember] {
        members.filter { member in member.userId.map(userIds.contains) ?? false }
    }

    func serial(_ task: TaskItem) -> String {
        guard let board else { return "Task" }
        return Aliases.serial(board.projectAlias, board.alias, task.number)
    }

    /// Everyone else on an entity; the caller is never their own presence.
    func presence(on entityType: String, _ entityId: UUID) -> [PresenceEntry] {
        presence.filter { $0.entityType == entityType && $0.entityId == entityId && $0.userId != session.user.id }
    }

    func editors(on entityType: String, _ entityId: UUID) -> [PresenceEntry] {
        presence(on: entityType, entityId).filter { $0.mode == .editing }
    }

    // MARK: Loading

    func load(projectAlias: String, boardAlias: String) async {
        do {
            let fetched = try await api.board(projectAlias: projectAlias, boardAlias: boardAlias)
            try await attach(fetched)
        } catch {
            loadError = (error as? LocalizedError)?.errorDescription ?? "Couldn't open that board."
        }
    }

    func load(boardId: UUID) async {
        do {
            try await attach(try await api.board(boardId))
        } catch {
            loadError = (error as? LocalizedError)?.errorDescription ?? "Couldn't open that board."
        }
    }

    private func attach(_ fetched: BoardDetail) async throws {
        members = try await api.members(fetched.projectId)
        board = fetched
        loadError = nil
        if !attached {
            attached = true
            subscriptions = [
                hub.entityChanged.subscribe { [weak self] change in
                    Task { await self?.changed(change) }
                },
                hub.presenceChanged.subscribe { [weak self] snapshot in
                    guard let self, snapshot.boardId == self.board?.id else { return }
                    self.presence = snapshot.entries
                },
                hub.reconnected.subscribe { [weak self] in
                    Task { await self?.refresh() }
                },
            ]
        }
        await hub.joinBoard(fetched.id)
    }

    func refresh() async {
        guard let id = board?.id, let fetched = try? await api.board(id) else { return }
        board = fetched
    }

    func refreshMembers() async {
        guard let projectId = board?.projectId, let fetched = try? await api.members(projectId) else { return }
        members = fetched
    }

    func detach() async {
        subscriptions = []
        attached = false
        refetch?.cancel()
        if let id = board?.id {
            await hub.leaveBoard(id)
        }
    }

    // MARK: Local application

    func apply(_ task: TaskItem) {
        guard var current = board else { return }
        guard task.boardId == current.id else {
            // Moved to another board: it has left this one.
            current.tasks.removeAll { $0.id == task.id }
            board = current
            return
        }
        if let index = current.tasks.firstIndex(where: { $0.id == task.id }) {
            current.tasks[index] = task
        } else {
            current.tasks.append(task)
        }
        board = current
        session.assigned.apply(task)
    }

    func apply(_ epic: Epic) {
        guard var current = board else { return }
        if let index = current.epics.firstIndex(where: { $0.id == epic.id }) {
            current.epics[index] = epic
        } else {
            current.epics.append(epic)
        }
        current.epics.sort { $0.rank < $1.rank }
        board = current
    }

    func removeTask(_ id: UUID) {
        guard var current = board else { return }
        current.tasks.removeAll { $0.id == id }
        board = current
        session.assigned.remove(id)
    }

    func refetchTask(_ id: UUID) async {
        if let task = try? await api.task(id) {
            apply(task)
        }
    }

    // MARK: Live updates

    private func changed(_ change: EntityChangedEvent) async {
        guard let current = board else { return }
        switch change.entityType {
        case EntityTypes.task where change.kind == .deleted:
            removeTask(change.entityId)
        case EntityTypes.task:
            await refetchRemote(change.entityId)
        case EntityTypes.subTask:
            if let parent = current.tasks.first(where: { $0.subTasks.contains { $0.id == change.entityId } }) {
                await refetchRemote(parent.id)
            } else {
                scheduleRefetch(pulsing: change.entityId)
            }
        case EntityTypes.comment:
            commentsRevision += 1
        default:
            scheduleRefetch(pulsing: change.entityId)
        }
    }

    private func refetchRemote(_ taskId: UUID) async {
        do {
            apply(try await api.task(taskId))
            pulse(taskId)
        } catch {
            await refresh()
        }
    }

    /// Remote changes apply silently; the card flashes its outline once. So does a card the
    /// page has just scrolled to, to say which one it was.
    func pulse(_ id: UUID) {
        pulsing.insert(id)
        Task {
            try? await Task.sleep(for: .milliseconds(650))
            pulsing.remove(id)
        }
    }

    private func scheduleRefetch(pulsing id: UUID) {
        refetch?.cancel()
        refetch = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled else { return }
            await self?.refresh()
            self?.pulse(id)
        }
    }
}
