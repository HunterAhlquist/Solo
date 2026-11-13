import Foundation
import Observation

/// Your tasks: everything assigned to the signed-in person on an open board,
/// nudged by its own topic whenever one of them, or anything around one, changes.
@Observable
final class AssignedTasksStore {
    private(set) var work: AssignedTasks?
    /// Tasks a remote change just landed on, for the one-shot outline pulse.
    private(set) var pulsing: Set<UUID> = []

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private let hub: HubClient
    @ObservationIgnored private let userId: UUID
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var refetch: Task<Void, Never>?
    @ObservationIgnored private var started = false

    init(api: APIClient, hub: HubClient, userId: UUID) {
        self.api = api
        self.hub = hub
        self.userId = userId
    }

    func load() async throws {
        work = try await api.assignedTasks()
        guard !started else { return }
        started = true
        subscriptions = [
            hub.topicMessage.subscribe { [weak self] message in
                guard message.topic == HubTopics.assignedTasks else { return }
                Task { await self?.nudged(message) }
            },
            hub.reconnected.subscribe { [weak self] in self?.scheduleRefetch() },
        ]
        await hub.subscribeUserTopic(HubTopics.assignedTasks)
    }

    func refresh() async {
        if let fetched = try? await api.assignedTasks() {
            work = fetched
        }
    }

    /// Folds one task's new state in, as a save or a nudge delivers it.
    func apply(_ task: TaskItem) {
        guard var current = work else { return }
        guard task.assigneeIds.contains(userId) else {
            remove(task.id)
            return
        }
        // A board or epic this payload has never carried: only a refetch brings
        // the names the card needs.
        if !current.boards.contains(where: { $0.id == task.boardId })
            || (task.epicId.map { id in !(current.epics ?? []).contains { $0.id == id } } ?? false) {
            scheduleRefetch()
            return
        }
        if let index = current.tasks.firstIndex(where: { $0.id == task.id }) {
            current.tasks[index] = task
        } else if task.status != .finished {
            current.tasks.append(task)
        } else {
            return
        }
        work = current
    }

    func remove(_ taskId: UUID) {
        guard var current = work, current.tasks.contains(where: { $0.id == taskId }) else { return }
        current.tasks.removeAll { $0.id == taskId }
        work = current
    }

    private func nudged(_ message: TopicMessage) async {
        guard let current = work, let entityId = message.data.flatMap(UUID.init) else { return }
        switch message.kind {
        case EntityTypes.task:
            await refetchTask(entityId)
        case EntityTypes.subTask:
            if let parent = current.tasks.first(where: { $0.subTasks.contains { $0.id == entityId } }) {
                await refetchTask(parent.id)
            } else {
                scheduleRefetch()
            }
        default:
            scheduleRefetch()
        }
    }

    private func refetchTask(_ id: UUID) async {
        do {
            apply(try await api.task(id))
            pulse(id)
        } catch is APIError {
            remove(id)
        } catch {}
    }

    private func pulse(_ id: UUID) {
        pulsing.insert(id)
        Task {
            try? await Task.sleep(for: .milliseconds(650))
            pulsing.remove(id)
        }
    }

    private func scheduleRefetch() {
        refetch?.cancel()
        refetch = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(150))
            guard !Task.isCancelled else { return }
            await self?.refresh()
        }
    }
}
