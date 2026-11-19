import Foundation
import Observation
import SwiftUI

/// One roadmap's state — a project's, or the signed-in person's own — and
/// everything that can be done to it. A change is drawn at once and then
/// saved; a failed save puts it back. Everyone else's changes arrive over the
/// roadmap's live topic as a nudge to read again.
@Observable
final class RoadmapStore {
    let session: Session
    private(set) var scope: RoadmapScope

    private(set) var timeline: RoadmapTimeline?
    private(set) var entries: [RoadmapEntry] = []
    private(set) var milestones: [RoadmapMilestone] = []
    private(set) var members: [UUID: ProjectMember] = [:]
    private(set) var labels: [UUID: SoloLabel] = [:]
    private(set) var present: [TopicPresenceEntry] = []
    private(set) var project: ProjectSummary?
    private(set) var loadError: String?
    /// Deleted by someone else while it was open.
    private(set) var gone = false
    private(set) var today = Day.today
    private(set) var window = TimelineWindow(today: .today, settings: .standard)

    /// The bars the selection bar acts on together.
    var selected: Set<UUID> = []
    /// A bar is in hand: the server's copy waits until it is put down.
    var gesturing = false {
        didSet { if !gesturing { Task { await catchUp() } } }
    }

    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var labelBoards: Set<UUID> = []
    @ObservationIgnored private var saving = 0
    @ObservationIgnored private var edits = 0
    @ObservationIgnored private var stale = false
    @ObservationIgnored private var catchingUp = false
    @ObservationIgnored private var liveTopic: (projectId: UUID, topic: String)?

    private var api: APIClient { session.api }
    var isPersonal: Bool { scope.isPersonal }

    init(session: Session, scope: RoadmapScope) {
        self.session = session
        self.scope = scope
    }

    // MARK: Derived

    /// The board's write rule: an owner, or a read/write member. A person's own roadmap is always theirs to arrange.
    var canWrite: Bool { isPersonal || (project?.canWrite ?? false) }
    /// Deleting a roadmap is the project's owners', and a super admin's whatever their role here.
    var canDelete: Bool { !isPersonal && session.manages(project) }
    var settings: RoadmapSettings { timeline?.settings ?? .standard }
    var layout: TimelineLayout { TimelineRules.layout(entries, window: window) }

    func place(_ entry: RoadmapEntry) -> BarPlacement { window.place(TimelineRules.span(entry)) }

    func serial(_ entry: RoadmapEntry) -> String {
        Aliases.serial(entry.projectAlias?.nilIfBlank ?? timeline?.projectAlias ?? "", entry.boardAlias, entry.task.number)
    }

    func assignees(_ task: TaskItem) -> [ProjectMember] { task.assigneeIds.compactMap { members[$0] } }

    func label(_ task: TaskItem) -> SoloLabel? { task.labelId.flatMap { labels[$0] } }

    var missed: [RoadmapEntry] {
        entries.filter { TimelineRules.isMissed($0.task, today: today) }
            .sorted { ($0.task.deadlineAt ?? today) < ($1.task.deadlineAt ?? today) }
    }

    /// The day the view opens on.
    var startingDay: Day { TimelineRules.startingDay(entries.map(TimelineRules.span), today: today) }

    /// What a drag or a menu on this bar takes along: the whole selection when the bar is in it, else the bar alone.
    func group(of entry: RoadmapEntry) -> [RoadmapEntry] {
        selected.contains(entry.id) ? entries.filter { selected.contains($0.id) } : [entry]
    }

    // MARK: Loading

    func load() async {
        do {
            if case .project(let projectId, _) = scope {
                if session.projects.project(projectId) == nil {
                    await session.projects.refreshProjects()
                }
                project = session.projects.project(projectId)
            }
            let fetched = try await api.roadmapTimeline(scope)
            apply(fetched)
            loadError = nil
            await startLive()
            await loadPeopleAndLabels()
        } catch let error as APIError where error.isNotFound {
            loadError = "That roadmap doesn't exist, or you don't have access to its project."
        } catch {
            loadError = (error as? LocalizedError)?.errorDescription ?? "Couldn't load the roadmap."
        }
    }

    func reload() async {
        guard let fetched = try? await api.roadmapTimeline(scope) else { return }
        apply(fetched)
        await loadLabels()
    }

    private func apply(_ fetched: RoadmapTimeline) {
        today = .today
        timeline = fetched
        window = TimelineWindow(today: today, settings: fetched.settings)
        entries = fetched.entries
        milestones = fetched.milestones.sorted { $0.day < $1.day }
        selected.formIntersection(entries.map(\.id))
    }

    /// Who's who and which colour each label is — never allowed to block the grid.
    private func loadPeopleAndLabels() async {
        let fetched: [ProjectMember]?
        if case .project(let projectId, _) = scope {
            fetched = try? await api.members(projectId)
        } else {
            fetched = try? await api.assignedTasks().members
        }
        for member in fetched ?? [] {
            if let userId = member.userId, members[userId] == nil {
                members[userId] = member
            }
        }
        await loadLabels()
    }

    /// Labels are per board: fetch any board's whose tasks are on the roadmap and not yet known.
    private func loadLabels() async {
        for boardId in Set(entries.map(\.task.boardId)).subtracting(labelBoards) {
            labelBoards.insert(boardId)
            guard let fetched = try? await api.boardLabels(boardId) else {
                labelBoards.remove(boardId)
                continue
            }
            for label in fetched {
                labels[label.id] = label
            }
        }
    }

    // MARK: Live

    private func startLive() async {
        guard subscriptions.isEmpty else { return }
        let hub = session.hub
        switch scope {
        case .project(let projectId, let roadmapId):
            let topic = RoadmapTopics.roadmap(roadmapId)
            liveTopic = (projectId, topic)
            subscriptions = [
                hub.topicMessage.subscribe { [weak self] message in
                    guard message.topic == topic, message.projectId == projectId else { return }
                    if message.kind == RoadmapTopics.deleted {
                        self?.gone = true
                    } else {
                        self?.heard()
                    }
                },
                hub.topicPresenceChanged.subscribe { [weak self] snapshot in
                    guard let self, snapshot.topic == topic, snapshot.projectId == projectId else { return }
                    self.present = snapshot.entries.filter { $0.userId != self.session.user.id }
                },
                hub.reconnected.subscribe { [weak self] in self?.heard() },
            ]
            await hub.subscribe(projectId: projectId, topic: topic)
            await hub.setTopicPresence(projectId: projectId, topic: topic, .viewing)
        case .personal:
            // Its rows follow the person's own topic; which tasks are on it
            // follows their assignments, which core's topic reports.
            subscriptions = [
                hub.topicMessage.subscribe { [weak self] message in
                    guard message.topic == RoadmapTopics.personal || message.topic == HubTopics.assignedTasks else { return }
                    self?.heard()
                },
                hub.reconnected.subscribe { [weak self] in self?.heard() },
            ]
            await hub.subscribeUserTopic(RoadmapTopics.personal)
            await hub.subscribeUserTopic(HubTopics.assignedTasks)
        }
    }

    func stopLive() async {
        guard !subscriptions.isEmpty else { return }
        subscriptions = []
        let hub = session.hub
        if let liveTopic {
            await hub.setTopicPresence(projectId: liveTopic.projectId, topic: liveTopic.topic, .none)
            await hub.unsubscribe(projectId: liveTopic.projectId, topic: liveTopic.topic)
        } else {
            await hub.unsubscribeUserTopic(RoadmapTopics.personal)
            await hub.unsubscribeUserTopic(HubTopics.assignedTasks)
        }
    }

    /// Tells the others here whether this person is looking, or in the middle of changing something.
    func report(editing: Bool) {
        guard let liveTopic else { return }
        Task { await session.hub.setTopicPresence(projectId: liveTopic.projectId, topic: liveTopic.topic, editing ? .editing : .viewing) }
    }

    private func heard() {
        stale = true
        Task { await catchUp() }
    }

    /// Takes the server's copy once nothing of this person's is in flight.
    /// Mid-gesture the rows are theirs to move; with a save on its way the
    /// server's copy doesn't have it yet and would snap the row back until it
    /// lands. Whatever is heard meanwhile folds into one read once both settle.
    private func catchUp() async {
        guard stale, !catchingUp, !gesturing, saving == 0, timeline != nil else { return }
        stale = false
        catchingUp = true
        let before = edits
        do {
            let fetched = try await api.roadmapTimeline(scope)
            if before != edits || gesturing || saving > 0 {
                stale = true
            } else {
                apply(fetched)
                await loadLabels()
            }
        } catch let error as APIError where error.isNotFound {
            gone = !isPersonal
        } catch {
            // Behind until the next change: a warning for every missed nudge
            // would be noise about something the page can't fix.
        }
        catchingUp = false
        if stale { await catchUp() }
    }

    /// Wraps a save: counted while it is in flight, and followed by a catch-up.
    private func save<T>(_ operation: () async throws -> T) async throws -> T {
        saving += 1
        edits += 1
        defer {
            saving -= 1
            Task { await catchUp() }
        }
        return try await operation()
    }

    private func replace(_ entryId: UUID, _ change: (inout RoadmapEntry) -> Void) {
        if let index = entries.firstIndex(where: { $0.id == entryId }) {
            change(&entries[index])
        }
    }

    // MARK: Moving along the timeline

    /// Every bar in the group moves the same number of days: the task's dates
    /// on a project's roadmap, the person's plan on their own, the anchor of a
    /// task with no deadline.
    func shift(_ moving: [RoadmapEntry], by days: Int) async {
        guard canWrite, days != 0 else { return }
        // All at once, and reported once rather than once per bar.
        let saves = moving.map { entry in Task { await self.shiftOne(entry, by: days) } }
        var failures: [String] = []
        for save in saves {
            if let failure = await save.value { failures.append(failure) }
        }
        if let first = failures.first {
            session.toasts.show(moving.count == 1 ? first
                : "\(failures.count) of \(moving.count) tasks couldn't move: \(first)", .warning)
        }
    }

    private func shiftOne(_ entry: RoadmapEntry, by days: Int) async -> String? {
        if TimelineRules.isPill(entry) {
            return await saveAnchor(entry, entry.anchorDay.adding(days: days))
        }
        let moved = TimelineRules.span(entry).moved(by: days)
        return await saveSchedule(entry, start: moved.start, deadline: moved.end)
    }

    /// An edge of a bar dropped on `day`.
    func resize(_ entry: RoadmapEntry, edge: BarEdge, to day: Day) async {
        guard canWrite else { return }
        let dates = TimelineRules.resize(entry, edge: edge, to: day)
        if let failure = await saveSchedule(entry, start: dates.start, deadline: dates.deadline) {
            session.toasts.show(failure, .warning)
        }
    }

    private func saveSchedule(_ entry: RoadmapEntry, start: Day?, deadline: Day) async -> String? {
        if isPersonal {
            return await savePlan(entry, RoadmapSchedule(start: start ?? TimelineRules.span(entry).start, end: deadline))
        }
        let before = entry.task
        guard before.startAt != start || before.deadlineAt != deadline else { return nil }
        replace(entry.id) {
            $0.task.startAt = start
            $0.task.deadlineAt = deadline
        }
        do {
            let saved = try await save { try await api.setTaskSchedule(before, start: start, deadline: deadline) }
            replace(entry.id) { $0.task = saved }
            session.assigned.apply(saved)
            return nil
        } catch let error as APIError where error.isConflict {
            let fresh = (try? await api.task(before.id)) ?? before
            replace(entry.id) { $0.task = fresh }
            return "Someone else changed this task. Try again."
        } catch {
            replace(entry.id) { $0.task = before }
            return (error as? LocalizedError)?.errorDescription ?? "That didn't save."
        }
    }

    /// The person's plan for a bar on their own roadmap. The task's dates
    /// don't move, so a plan past the deadline is drawn as such, not refused.
    private func savePlan(_ entry: RoadmapEntry, _ plan: RoadmapSchedule) async -> String? {
        guard entry.schedule != plan else { return nil }
        replace(entry.id) { $0.schedule = plan }
        do {
            try await save { try await api.setPersonalSchedule(entry.id, plan) }
            return nil
        } catch {
            replace(entry.id) { $0.schedule = entry.schedule }
            return "Couldn't move \(serial(entry)): \((error as? LocalizedError)?.errorDescription ?? "try again")"
        }
    }

    private func saveAnchor(_ entry: RoadmapEntry, _ day: Day) async -> String? {
        replace(entry.id) { $0.anchorDay = day }
        do {
            try await save { try await api.setRoadmapAnchor(scope, entry.id, day) }
            return nil
        } catch {
            replace(entry.id) { $0.anchorDay = entry.anchorDay }
            return "Couldn't move \(serial(entry)): \((error as? LocalizedError)?.errorDescription ?? "try again")"
        }
    }

    /// Puts bars back on their tasks' dates, and keeps them following those.
    func followTaskDates(_ targets: [RoadmapEntry]) async {
        for entry in targets where entry.schedule != nil {
            replace(entry.id) { $0.schedule = nil }
            do {
                try await save { try await api.clearPersonalSchedule(entry.id) }
            } catch {
                replace(entry.id) { $0.schedule = entry.schedule }
                session.toasts.error(error, fallback: "Couldn't reset \(serial(entry)).")
            }
        }
    }

    // MARK: Moving between rows

    /// A bar dropped up or down, with the rest of the selection when it is
    /// part of one. With `join` they go on row `row` beside its bars, if none
    /// overlaps another; without, they land on rows of their own just above
    /// row `row` (the row count: below the last).
    func dropOnRow(_ dropped: RoadmapEntry, row: Int, join: Bool) async {
        guard canWrite else { return }
        let rows = layout.rows
        let moving = group(of: dropped)
        let movingIds = Set(moving.map(\.id))
        let staying = rows.map { $0.filter { !movingIds.contains($0.id) } }
        var joinId: UUID?, preceding: UUID?, following: UUID?

        if join {
            guard rows.indices.contains(row), let beside = staying[row].first,
                  TimelineRules.fits(moving.map(place), beside: staying[row].map(place)) else { return }
            joinId = beside.id
        } else {
            // Named by rows that stay, as the server finds them: a row whose
            // every bar is moving is gone from between them.
            let at = min(max(row, 0), rows.count)
            preceding = staying.prefix(at).last { !$0.isEmpty }?.first?.id
            following = staying.dropFirst(at).first { !$0.isEmpty }?.first?.id
            let alone = movingIds.count == 1 || Set(moving.map(\.rank)).count == 1
            if alone, let from = layout.row(of: dropped.id), rows[from].count == moving.count, at == from || at == from + 1 {
                return
            }
        }

        do {
            try await save {
                try await api.rerankRoadmapEntries(scope, Array(movingIds), preceding: preceding, following: following, joinId: joinId)
            }
            // The server decides the ranks; read them back rather than guess.
            stale = true
        } catch {
            let what = moving.count == 1 ? serial(dropped) : "\(moving.count) tasks"
            session.toasts.show("Couldn't move \(what): \((error as? LocalizedError)?.errorDescription ?? "try again")", .error)
        }
    }

    // MARK: Adding and removing

    /// Puts a task on the roadmap: beside `beside` on its row when its bar
    /// fits there, else on a row of its own at the end. A task with no dates
    /// gets them from the chosen day — on a person's own roadmap, a week's
    /// plan of theirs instead, the task left undated. One that has dates keeps
    /// them: nobody's deadline moves without them seeing it.
    func add(_ task: TaskItem, on day: Day, beside: UUID? = nil) async -> UUID? {
        guard canWrite else { return nil }
        let span: DaySpan = task.deadlineAt != nil ? TimelineRules.span(task, anchorDay: day)
            : isPersonal ? DaySpan(start: day, end: day.adding(days: RoadmapLimits.personalPlanDays - 1))
            : TimelineRules.scheduleOnAdd(day, today: today)
        var joinId: UUID?
        if let beside, let row = layout.row(of: beside),
           TimelineRules.fits([window.place(span)], beside: layout.rows[row].map(place)) {
            joinId = beside
        }

        let entry: RoadmapEntry
        do {
            entry = try await save { try await api.addRoadmapEntry(scope, taskId: task.id, anchorDay: day, joinId: joinId) }
        } catch {
            session.toasts.error(error)
            return nil
        }
        entries.append(entry)
        await loadLabels()

        var note: String?
        if isPersonal {
            note = entry.schedule.map { "It has no deadline, so it's planned for \(RoadmapDates.range($0.start, $0.end)) on your roadmap only." }
                ?? "It follows the task's dates (\(RoadmapDates.range(span.start, span.end)))."
        } else if entry.task.deadlineAt == nil {
            if let scheduled = try? await save({ try await api.setTaskSchedule(entry.task, start: span.start, deadline: span.end) }) {
                replace(entry.id) { $0.task = scheduled }
                if span.end != span.start {
                    note = "\(RoadmapDates.day(day)) has passed, so it runs through today."
                }
            }
        } else {
            note = "It keeps its existing dates (\(RoadmapDates.range(span.start, span.end)))."
        }
        if beside != nil && joinId == nil {
            note = [note, "It overlaps that row's tasks, so it's on a row of its own."].compactMap { $0 }.joined(separator: " ")
        }
        if let note {
            session.toasts.show("Added. \(note)", .info)
        }
        return entry.id
    }

    /// Takes tasks off this roadmap only; they stay on their boards.
    func remove(_ targets: [RoadmapEntry]) async {
        guard canWrite, !targets.isEmpty else { return }
        let before = entries
        let ids = Set(targets.map(\.id))
        entries.removeAll { ids.contains($0.id) }
        selected.subtract(ids)
        do {
            // The page splits a selection larger than one request may carry.
            for chunk in stride(from: 0, to: targets.count, by: RoadmapLimits.maxEntriesAtOnce) {
                let part = targets[chunk..<min(chunk + RoadmapLimits.maxEntriesAtOnce, targets.count)].map(\.id)
                try await save { try await api.removeRoadmapEntries(scope, part) }
            }
        } catch {
            entries = before
            session.toasts.error(error, fallback: "Couldn't remove that from the roadmap.")
        }
    }

    /// Keeps a row in step with what the task panel did to its task.
    func taskChanged(_ taskId: UUID) async {
        guard entries.contains(where: { $0.task.id == taskId }) else { return }
        if let fresh = try? await api.task(taskId) {
            for index in entries.indices where entries[index].task.id == taskId {
                entries[index].task = fresh
            }
        } else {
            await reload()
        }
    }

    // MARK: Milestones

    func saveMilestone(_ existing: RoadmapMilestone?, name: String, day: Day, color: String) async {
        guard canWrite else { return }
        let before = milestones
        do {
            if let existing {
                // Drawn where it is going at once; the server's copy replaces it.
                putMilestone(RoadmapMilestone(id: existing.id, name: name, day: day, color: color, version: existing.version))
                let saved = try await save {
                    try await api.updateMilestone(scope, existing.id, MilestoneRequest(name: name, day: day, color: color, version: existing.version))
                }
                putMilestone(saved)
            } else {
                putMilestone(try await save {
                    try await api.addMilestone(scope, MilestoneRequest(name: name, day: day, color: color))
                })
            }
        } catch let error as APIError where error.isConflict {
            milestones = before
            session.toasts.show("Someone else changed this milestone first. It's been refreshed; try again.", .warning)
            await reload()
        } catch {
            milestones = before
            session.toasts.error(error)
        }
    }

    func deleteMilestone(_ milestone: RoadmapMilestone) async {
        let before = milestones
        milestones.removeAll { $0.id == milestone.id }
        do {
            try await save { try await api.deleteMilestone(scope, milestone.id) }
        } catch {
            milestones = before
            session.toasts.error(error)
        }
    }

    private func putMilestone(_ milestone: RoadmapMilestone) {
        milestones.removeAll { $0.id == milestone.id }
        milestones.append(milestone)
        milestones.sort { $0.day < $1.day }
    }

    // MARK: The roadmap itself

    func rename(_ title: String) async {
        guard case .project(let projectId, let roadmapId) = scope, let current = timeline, title != current.title else { return }
        do {
            let saved = try await save { try await api.renameRoadmap(projectId, roadmapId, title: title, version: current.version) }
            timeline?.title = saved.title
            timeline?.version = saved.version
            RoadmapLists.changed(projectId)
        } catch let error as APIError where error.isConflict {
            session.toasts.show("Someone else changed this roadmap first. It's been refreshed; try again.", .warning)
            await reload()
        } catch {
            session.toasts.error(error)
        }
    }

    func delete() async -> Bool {
        guard case .project(let projectId, let roadmapId) = scope else { return false }
        guard await session.perform({ try await api.deleteRoadmap(projectId, roadmapId) }) else { return false }
        RoadmapLists.changed(projectId)
        return true
    }
}

/// Tells every list of a project's roadmaps — sidebar, project page, project
/// card — that this device just changed them, without waiting for the hub.
enum RoadmapLists {
    static let didChange = EventSource<UUID>()

    static func changed(_ projectId: UUID) {
        didChange.send(projectId)
    }
}
