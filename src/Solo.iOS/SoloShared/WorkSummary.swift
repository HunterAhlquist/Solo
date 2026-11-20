import Foundation

/// The signed-in person's work at a glance: what the widgets draw. The same
/// shape the Capacitor app's widgets read, but worked out here from the
/// assigned-tasks payload every instance already serves, so it needs nothing
/// new of the server.
nonisolated struct WorkSummary: Codable, Equatable, Sendable {
    var unstarted: Int
    var inProgress: Int
    var finished: Int
    /// New and in-progress tasks, soonest deadline first, undated last.
    var open: [Item]

    nonisolated struct Item: Codable, Equatable, Identifiable, Sendable {
        var id: UUID
        /// `PROJECT-BOARD-12`, which is also how the app is told to open it.
        var serial: String
        var name: String
        var started: Bool
        /// `yyyy-MM-dd`: a date with no time or zone.
        var deadline: String?

        /// Whole days from `today` to the deadline in the device's calendar:
        /// 0 due today, negative overdue, nil without one.
        func daysLeft(from today: Date, calendar: Calendar = .current) -> Int? {
            guard let deadline else { return nil }
            let parts = deadline.prefix(10).split(separator: "-").compactMap { Int($0) }
            guard parts.count == 3,
                  let due = calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2]))
            else { return nil }
            return calendar.dateComponents([.day], from: calendar.startOfDay(for: today), to: due).day
        }
    }

    /// The most open tasks a summary lists. A column can hold more than are listed; the
    /// widgets draw the rest without detail.
    static let openListed = 100

    /// One task as the summary needs it.
    nonisolated struct Source: Sendable {
        var id: UUID
        var serial: String
        var name: String
        /// `Unstarted`, `InProgress` or `Finished`.
        var status: String
        var deadline: String?
    }

    init(unstarted: Int, inProgress: Int, finished: Int, open: [Item]) {
        self.unstarted = unstarted
        self.inProgress = inProgress
        self.finished = finished
        self.open = open
    }

    init(tasks: [Source]) {
        unstarted = tasks.count { $0.status == "Unstarted" }
        inProgress = tasks.count { $0.status == "InProgress" }
        finished = tasks.count { $0.status == "Finished" }
        open = tasks.filter { $0.status != "Finished" }
            .sorted { first, second in
                // Dated before undated, sooner before later, and a steady order among equals.
                switch (first.deadline, second.deadline) {
                case let (a?, b?) where a != b: return a < b
                case (_?, nil): return true
                case (nil, _?): return false
                default: return first.serial.localizedStandardCompare(second.serial) == .orderedAscending
                }
            }
            .prefix(Self.openListed)
            .map { Item(id: $0.id, serial: $0.serial, name: $0.name, started: $0.status == "InProgress", deadline: $0.deadline) }
    }

    /// Reads a summary out of what `GET api/users/me/tasks` answers.
    init(assignedTasks data: Data) throws {
        struct Payload: Decodable {
            struct Project: Decodable { var id: UUID; var alias: String }
            struct Board: Decodable { var id: UUID; var projectId: UUID; var alias: String }
            struct Task: Decodable { var id: UUID; var boardId: UUID; var number: Int; var name: String; var status: String; var deadlineAt: String? }
            var projects: [Project]
            var boards: [Board]
            var tasks: [Task]
        }
        let payload = try JSONDecoder().decode(Payload.self, from: data)
        let projects = Dictionary(payload.projects.map { ($0.id, $0.alias) }) { first, _ in first }
        let boards = Dictionary(payload.boards.map { ($0.id, $0) }) { first, _ in first }
        self.init(tasks: payload.tasks.compactMap { task in
            guard let board = boards[task.boardId], let project = projects[board.projectId] else { return nil }
            return Source(id: task.id, serial: "\(project)-\(board.alias)-\(task.number)", name: task.name,
                          status: task.status, deadline: task.deadlineAt)
        })
    }
}

nonisolated enum WorkSummaryError: Error {
    /// The instance no longer accepts the session.
    case signedOut
    case unreachable
}

nonisolated enum WorkSummaryClient {
    static func fetch(_ instance: URL, token: String) async throws -> WorkSummary {
        var request = URLRequest(url: instance.appendingPathComponent("api/users/me/tasks"))
        request.timeoutInterval = 12
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let data: Data, response: URLResponse
        do {
            (data, response) = try await URLSession(configuration: .ephemeral, delegate: RedirectGuard(), delegateQueue: nil)
                .data(for: request)
        } catch {
            throw WorkSummaryError.unreachable
        }
        switch (response as? HTTPURLResponse)?.statusCode ?? 0 {
        case 401: throw WorkSummaryError.signedOut
        case 200..<300:
            do { return try WorkSummary(assignedTasks: data) } catch { throw WorkSummaryError.unreachable }
        default: throw WorkSummaryError.unreachable
        }
    }
}

/// How a widget opens the app at a page: `com.hunterahlquist.solo.native://work`, or `…://task/SERIAL`.
nonisolated enum WidgetLink {
    static let scheme = "com.hunterahlquist.solo.native"

    enum Destination: Equatable {
        case yourTasks
        case task(String)
    }

    static var yourTasks: URL { URL(string: "\(scheme)://work")! }

    static func task(_ serial: String) -> URL {
        var components = URLComponents()
        components.scheme = scheme
        components.host = "task"
        components.path = "/" + serial
        return components.url ?? yourTasks
    }

    /// Nil for anything that isn't one of the two.
    static func parse(_ url: URL) -> Destination? {
        guard url.scheme == scheme else { return nil }
        switch url.host() {
        case "work": return .yourTasks
        case "task":
            let serial = url.path().trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            // A serial is letters, digits and hyphens; nothing else is taken for one.
            guard !serial.isEmpty, serial.wholeMatch(of: /[A-Za-z0-9]+(?:-[A-Za-z0-9]+)+/) != nil else { return nil }
            return .task(serial)
        default: return nil
        }
    }
}
