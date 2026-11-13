import Foundation

// The Roadmaps extension's wire types and endpoints (api/ext/roadmap/…),
// and the timeline's rules in days: where a task's bar runs, which line it is
// drawn on, which stripe a day falls in, what counts as missed. The rules are
// plain functions so a test can pin each one down.

nonisolated enum RoadmapLimits {
    static let title = 300
    static let milestoneName = 100
    static let defaultMilestoneColor = "#e53935"
    static let boundDays = 1...3650
    static let sprintWeeks = 1...52
    static let maxEntriesAtOnce = 500
    /// How many days a task with no deadline is planned over on someone's own roadmap.
    static let personalPlanDays = 7
    static let milestoneSwatches = [defaultMilestoneColor, "#fb8c00", "#fdd835", "#43a047", "#00acc1", "#1e88e5",
                                    "#8e24aa", "#d81b60"]
}

nonisolated enum RoadmapTopics {
    /// A project's roadmaps were created, renamed or deleted.
    static let list = "roadmap:list"
    /// A person's own roadmap: a user topic, so only their other devices hear it.
    static let personal = "roadmap:mine"

    static func roadmap(_ id: UUID) -> String {
        "roadmap:\(id.uuidString.lowercased().replacingOccurrences(of: "-", with: ""))"
    }

    static let deleted = "deleted"
}

nonisolated struct RoadmapSummary: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var title: String
    var version: UInt32
}

nonisolated struct RoadmapSettings: Codable, Hashable, Sendable {
    var pastDays: Int
    var futureDays: Int
    var sprintStartDay: Weekday
    var sprintLengthWeeks: Int
    var version: UInt32

    static let standard = RoadmapSettings(pastDays: 365, futureDays: 730, sprintStartDay: .monday,
                                          sprintLengthWeeks: 2, version: 0)
}

/// Everything the timeline draws, in one read.
nonisolated struct RoadmapTimeline: Codable, Sendable {
    var id: UUID
    var projectId: UUID
    var projectAlias: String
    var title: String
    var version: UInt32
    var settings: RoadmapSettings
    var entries: [RoadmapEntry]
    var milestones: [RoadmapMilestone]
}

/// A named deadline on a roadmap, drawn as a line at the end of its day.
nonisolated struct RoadmapMilestone: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var day: Day
    var color: String
    var version: UInt32
}

/// A bar's days on a person's own roadmap, inclusive: their plan for the task,
/// which the task's own dates don't follow.
nonisolated struct RoadmapSchedule: Codable, Hashable, Sendable {
    var start: Day
    var end: Day
}

/// One task on a roadmap. Entries sharing a rank share a row.
nonisolated struct RoadmapEntry: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var rank: Double
    /// The day it was added on — where a task with no deadline is drawn.
    var anchorDay: Day
    var boardAlias: String
    var boardName: String
    var task: TaskItem
    var projectAlias: String?
    var projectName: String?
    var schedule: RoadmapSchedule?
}

nonisolated struct TaskRoadmap: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var title: String
    var projectAlias: String
}

nonisolated private struct EntryRequest: Encodable, Sendable {
    var taskId: UUID
    var anchorDay: Day
    var joinId: UUID?
}

nonisolated private struct RerankEntriesRequest: Encodable, Sendable {
    var entryIds: [UUID]?
    var precedingId: UUID?
    var followingId: UUID?
    var joinId: UUID?
}

nonisolated struct MilestoneRequest: Encodable, Sendable {
    var name: String
    var day: Day
    var color: String
    var version: UInt32?
}

/// Which roadmap a call is about: a project's, or the caller's own.
nonisolated enum RoadmapScope: Hashable, Sendable {
    case project(projectId: UUID, roadmapId: UUID)
    case personal

    var base: String {
        switch self {
        case .project(let projectId, let roadmapId): "api/ext/roadmap/projects/\(projectId.api)/roadmaps/\(roadmapId.api)"
        case .personal: "api/ext/roadmap/me"
        }
    }

    var isPersonal: Bool { self == .personal }
}

extension APIClient {
    func roadmaps(_ projectId: UUID) async throws -> [RoadmapSummary] {
        try await get("api/ext/roadmap/projects/\(projectId.api)/roadmaps")
    }

    func createRoadmap(_ projectId: UUID, title: String) async throws -> RoadmapSummary {
        try await send("POST", "api/ext/roadmap/projects/\(projectId.api)/roadmaps", body: ["title": title])
    }

    func renameRoadmap(_ projectId: UUID, _ roadmap: UUID, title: String, version: UInt32) async throws -> RoadmapSummary {
        try await send("PUT", "api/ext/roadmap/projects/\(projectId.api)/roadmaps/\(roadmap.api)",
                       body: NameVersionRequestTitle(title: title, version: version))
    }

    func deleteRoadmap(_ projectId: UUID, _ roadmap: UUID) async throws {
        try await send("DELETE", "api/ext/roadmap/projects/\(projectId.api)/roadmaps/\(roadmap.api)")
    }

    func roadmapTimeline(_ scope: RoadmapScope) async throws -> RoadmapTimeline { try await get(scope.base) }

    func addRoadmapEntry(_ scope: RoadmapScope, taskId: UUID, anchorDay: Day, joinId: UUID?) async throws -> RoadmapEntry {
        try await send("POST", "\(scope.base)/entries", body: EntryRequest(taskId: taskId, anchorDay: anchorDay, joinId: joinId))
    }

    func removeRoadmapEntries(_ scope: RoadmapScope, _ ids: [UUID]) async throws {
        try await send("POST", "\(scope.base)/entries/remove", body: ["entryIds": ids])
    }

    /// One entry or several moved together: onto rows of their own between
    /// two neighbours, or — with `joinId` — onto that entry's row.
    func rerankRoadmapEntries(_ scope: RoadmapScope, _ ids: [UUID], preceding: UUID?, following: UUID?, joinId: UUID?) async throws {
        if ids.count == 1 {
            try await send("PUT", "\(scope.base)/entries/\(ids[0].api)/rank",
                           body: RerankEntriesRequest(precedingId: preceding, followingId: following, joinId: joinId))
        } else {
            try await send("PUT", "\(scope.base)/entries/rank",
                           body: RerankEntriesRequest(entryIds: ids, precedingId: preceding, followingId: following, joinId: joinId))
        }
    }

    func setRoadmapAnchor(_ scope: RoadmapScope, _ entryId: UUID, _ day: Day) async throws {
        try await send("PUT", "\(scope.base)/entries/\(entryId.api)/anchor", body: ["anchorDay": day])
    }

    func setPersonalSchedule(_ entryId: UUID, _ plan: RoadmapSchedule) async throws {
        try await send("PUT", "api/ext/roadmap/me/entries/\(entryId.api)/schedule", body: plan)
    }

    func clearPersonalSchedule(_ entryId: UUID) async throws {
        try await send("DELETE", "api/ext/roadmap/me/entries/\(entryId.api)/schedule")
    }

    func addMilestone(_ scope: RoadmapScope, _ request: MilestoneRequest) async throws -> RoadmapMilestone {
        try await send("POST", "\(scope.base)/milestones", body: request)
    }

    func updateMilestone(_ scope: RoadmapScope, _ id: UUID, _ request: MilestoneRequest) async throws -> RoadmapMilestone {
        try await send("PUT", "\(scope.base)/milestones/\(id.api)", body: request)
    }

    func deleteMilestone(_ scope: RoadmapScope, _ id: UUID) async throws {
        try await send("DELETE", "\(scope.base)/milestones/\(id.api)")
    }

    func roadmaps(ofTask taskId: UUID) async throws -> [TaskRoadmap] {
        try await get("api/ext/roadmap/tasks/\(taskId.api)/roadmaps")
    }

    func roadmapSettings() async throws -> RoadmapSettings { try await get("api/ext/roadmap/settings") }

    func saveRoadmapSettings(_ settings: RoadmapSettings) async throws -> RoadmapSettings {
        try await send("PUT", "api/ext/roadmap/settings", body: settings)
    }
}

nonisolated private struct NameVersionRequestTitle: Encodable, Sendable {
    var title: String
    var version: UInt32
}

// MARK: - Timeline rules

/// An inclusive run of days.
nonisolated struct DaySpan: Hashable, Sendable {
    var start: Day
    var end: Day

    func moved(by days: Int) -> DaySpan { DaySpan(start: start.adding(days: days), end: end.adding(days: days)) }
}

nonisolated enum BarEdge: Sendable { case start, end }

/// Where a bar sits in column indexes, clipped to the window.
nonisolated struct BarPlacement: Hashable, Sendable {
    var startIndex: Int
    var endIndex: Int
    var clippedStart: Bool
    var clippedEnd: Bool

    var length: Int { endIndex - startIndex + 1 }

    func overlaps(_ other: BarPlacement) -> Bool { startIndex <= other.endIndex && endIndex >= other.startIndex }
}

/// The scrollable window of days, from `today − past` to `today + future`.
nonisolated struct TimelineWindow: Hashable, Sendable {
    var first: Day
    var last: Day

    init(today: Day, settings: RoadmapSettings) {
        first = today.adding(days: -settings.pastDays)
        last = today.adding(days: settings.futureDays)
    }

    var days: Int { first.days(until: last) + 1 }
    func index(of day: Day) -> Int { first.days(until: day) }
    func day(at index: Int) -> Day { first.adding(days: index) }

    /// How far a span may move by up to `days` and stay inside the window.
    func clampMove(_ span: DaySpan, by days: Int) -> Int {
        days < 0 ? min(max(days, span.start.days(until: first)), 0) : max(min(days, span.end.days(until: last)), 0)
    }

    /// A span wholly outside the window keeps a one-day stub at the edge it is
    /// beyond, so the row still says where its work went.
    func place(_ span: DaySpan) -> BarPlacement {
        let start = index(of: span.start), end = index(of: span.end)
        return BarPlacement(startIndex: min(max(start, 0), days - 1), endIndex: min(max(end, 0), days - 1),
                            clippedStart: start < 0, clippedEnd: end > days - 1)
    }
}

/// One line of the grid: some or all of one row's bars, left to right.
nonisolated struct TimelineLine: Sendable {
    var row: Int
    var entries: [RoadmapEntry]
}

nonisolated struct TimelineLayout: Sendable {
    var lines: [TimelineLine]
    /// Each row's entries, the rows in roadmap order.
    var rows: [[RoadmapEntry]]
    private var placed: [UUID: (line: Int, row: Int)] = [:]

    init(lines: [TimelineLine], rows: [[RoadmapEntry]]) {
        self.lines = lines
        self.rows = rows
        for (index, line) in lines.enumerated() {
            for entry in line.entries {
                placed[entry.id] = (index, line.row)
            }
        }
    }

    func line(of entryId: UUID) -> Int? { placed[entryId]?.line }
    func row(of entryId: UUID) -> Int? { placed[entryId]?.row }

    /// The first grid line of a row, and one past its last.
    func lineRange(ofRow row: Int) -> Range<Int>? {
        let indexes = lines.indices.filter { lines[$0].row == row }
        guard let first = indexes.first, let last = indexes.last else { return nil }
        return first..<(last + 1)
    }
}

nonisolated enum TimelineRules {
    /// Lines the grid shows even when the roadmap has fewer, so an empty one still has a surface to press.
    static let minimumRows = 9
    /// Empty lines kept below the last task: where a new row is added.
    static let spareRows = 2

    static func rows(forLines lines: Int) -> Int { max(minimumRows, lines + spareRows) }

    /// A task's start: its own, or the day it was created — the same fallback its board card uses.
    static func effectiveStart(_ task: TaskItem) -> Day { task.startAt ?? Day(task.createdAt) }

    /// The days a task covers. With no deadline it is a one-day pill on the
    /// day it was added; one created after its own deadline starts on it.
    static func span(_ task: TaskItem, anchorDay: Day) -> DaySpan {
        guard let end = task.deadlineAt else { return DaySpan(start: anchorDay, end: anchorDay) }
        let start = effectiveStart(task)
        return DaySpan(start: min(start, end), end: end)
    }

    /// The days an entry's bar covers: where the person put it on their own roadmap, else the task's.
    static func span(_ entry: RoadmapEntry) -> DaySpan {
        entry.schedule.map { DaySpan(start: $0.start, end: $0.end) } ?? span(entry.task, anchorDay: entry.anchorDay)
    }

    /// A one-day pill: no deadline, and nobody has planned it either.
    static func isPill(_ entry: RoadmapEntry) -> Bool { entry.schedule == nil && entry.task.deadlineAt == nil }

    /// How many days past the task's deadline a personal plan runs.
    static func daysPastDeadline(_ entry: RoadmapEntry) -> Int {
        guard let plan = entry.schedule, let deadline = entry.task.deadlineAt else { return 0 }
        return max(deadline.days(until: plan.end), 0)
    }

    /// The board's rule: the deadline day has ended and the task isn't finished.
    static func isMissed(_ task: TaskItem, today: Day) -> Bool {
        guard let deadline = task.deadlineAt else { return false }
        return deadline < today && task.status != .finished
    }

    /// The dates a task with none gets when it is added on `day`: that day
    /// alone, or — on a day already past — from it through today, so it
    /// doesn't arrive already missed.
    static func scheduleOnAdd(_ day: Day, today: Day) -> DaySpan { DaySpan(start: day, end: max(day, today)) }

    /// The day the view opens on: of the rows in flight today, the one that
    /// started longest ago, or today itself.
    static func startingDay(_ spans: [DaySpan], today: Day) -> Day {
        spans.filter { $0.start <= today && today <= $0.end }.map(\.start).min() ?? today
    }

    /// The dates to save after an edge is dragged to `day`. The start can't
    /// pass the deadline, so a bar is always at least a day long. A pill gains
    /// a deadline either way.
    static func resize(_ entry: RoadmapEntry, edge: BarEdge, to day: Day) -> (start: Day?, deadline: Day) {
        let current = span(entry)
        let anchor = entry.anchorDay
        let start = entry.schedule?.start ?? entry.task.startAt
        switch (edge, isPill(entry)) {
        case (.end, true): return (anchor, max(day, anchor))
        case (.end, false): return (start, max(day, current.start))
        case (.start, true): return (min(day, anchor), anchor)
        case (.start, false): return (min(day, current.end), current.end)
        }
    }

    /// Where every entry is drawn. Entries sharing a rank share a row, and a
    /// row is one line while its bars don't overlap; bars that do take lines
    /// below it, as few as will hold them. A row is packed left to right, so
    /// which bar goes below follows from the dates alone and every client
    /// draws the same row the same way.
    static func layout(_ entries: [RoadmapEntry], window: TimelineWindow) -> TimelineLayout {
        var lines: [TimelineLine] = []
        var rows: [[RoadmapEntry]] = []
        let grouped = Dictionary(grouping: entries, by: \.rank)
        for rank in grouped.keys.sorted() {
            let row = grouped[rank] ?? []
            var ends: [Int] = []
            var onLines: [[RoadmapEntry]] = []
            let placed = row.map { ($0, window.place(span($0))) }
                .sorted { ($0.1.startIndex, $0.1.endIndex, $0.0.id.uuidString) < ($1.1.startIndex, $1.1.endIndex, $1.0.id.uuidString) }
            for (entry, place) in placed {
                if let at = ends.firstIndex(where: { $0 < place.startIndex }) {
                    ends[at] = place.endIndex
                    onLines[at].append(entry)
                } else {
                    ends.append(place.endIndex)
                    onLines.append([entry])
                }
            }
            lines += onLines.map { TimelineLine(row: rows.count, entries: $0) }
            rows.append(row)
        }
        return TimelineLayout(lines: lines, rows: rows)
    }

    /// Whether bars can share one line: none of `moving` overlaps another of them, or any of `staying`.
    static func fits(_ moving: [BarPlacement], beside staying: [BarPlacement]) -> Bool {
        var placed = staying
        for bar in moving {
            if placed.contains(where: bar.overlaps) { return false }
            placed.append(bar)
        }
        return true
    }
}

/// Where the sprint stripes fall, worked out from the settings alone: the
/// bands count from a fixed reference — the first sprint start day on or after
/// 1 January of year 1 — so the pattern is the same on every instance and
/// every client.
nonisolated enum RoadmapSprints {
    /// `Day.dayNumber` counts from 1970; .NET's DateOnly counts from year 1.
    private static let epochOffset = 719_162

    private static func dotNetDayNumber(_ day: Day) -> Int { day.dayNumber + epochOffset }

    private static func weekdayIndex(_ weekday: Weekday) -> Int { Weekday.allCases.firstIndex(of: weekday) ?? 1 }

    /// A day number (in .NET's count) that starts a sprint. Day 0 there is a Monday.
    private static func anchor(_ startDay: Weekday) -> Int { (weekdayIndex(startDay) - 1 + 7) % 7 }

    /// The first day of the sprint `day` falls in.
    static func sprintStart(_ day: Day, startDay: Weekday, weeks: Int) -> Day {
        let length = weeks * 7
        let offset = dotNetDayNumber(day) - anchor(startDay)
        return day.adding(days: -(((offset % length) + length) % length))
    }

    /// Which stripe a day is in; odd bands are the darker ones.
    static func band(_ day: Day, settings: RoadmapSettings) -> Int {
        let length = settings.sprintLengthWeeks * 7
        let offset = dotNetDayNumber(day) - anchor(settings.sprintStartDay)
        return Int((Double(offset) / Double(length)).rounded(.down))
    }

    /// A milestone on one of its sprint's first three days counts as due in the sprint before.
    static let milestoneLeadDays = 3

    static func milestoneDueFrom(_ day: Day, settings: RoadmapSettings) -> Day {
        let start = sprintStart(day, startDay: settings.sprintStartDay, weeks: settings.sprintLengthWeeks)
        return start.days(until: day) < milestoneLeadDays ? start.adding(days: -settings.sprintLengthWeeks * 7) : start
    }

    /// A milestone is due from the start of its sprint through its own day; after that it is history.
    static func isMilestoneDue(_ day: Day, today: Day, settings: RoadmapSettings) -> Bool {
        today <= day && today >= milestoneDueFrom(day, settings: settings)
    }
}

nonisolated enum RoadmapDates {
    static func day(_ day: Day) -> String { day.shortFormatted }

    static func range(_ start: Day, _ end: Day) -> String {
        if start == end { return day(start) }
        if start.year != end.year { return "\(start.formatted) – \(end.formatted)" }
        return start.month == end.month ? "\(start.day)–\(day(end))" : "\(day(start)) – \(day(end))"
    }
}
