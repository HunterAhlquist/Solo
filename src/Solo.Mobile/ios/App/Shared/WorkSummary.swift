import Foundation

/// The signed-in person's work at a glance, as `GET api/users/me/work-summary`
/// answers (`WorkSummaryDto` in Solo.Shared): what the widgets draw.
struct WorkSummary: Codable, Equatable {
    var unstarted: Int
    var inProgress: Int
    /// As the Your Tasks page's Finished column shows them: at most 25.
    var finished: Int
    /// New and in-progress tasks, soonest deadline first, undated last.
    var open: [Task]

    struct Task: Codable, Equatable, Identifiable {
        var id: UUID
        var serial: String
        var name: String
        /// "Unstarted" or "InProgress".
        var status: String
        /// `yyyy-MM-dd`, a date with no time or zone.
        var deadline: String?
        /// Where the task opens, relative to the instance.
        var path: String

        var isStarted: Bool { status == "InProgress" }

        /// Whole days from `today` to the deadline in the device's calendar:
        /// 0 due today, negative overdue, nil without one.
        func daysLeft(from today: Date, calendar: Calendar = .current) -> Int? {
            guard let deadline else { return nil }
            let parts = deadline.split(separator: "-").compactMap { Int($0) }
            guard parts.count == 3,
                  let due = calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2]))
            else { return nil }
            return calendar.dateComponents([.day], from: calendar.startOfDay(for: today), to: due).day
        }
    }
}

enum WorkSummaryError: Error {
    /// The instance no longer accepts the session.
    case signedOut
    case unreachable
}

enum WorkSummaryClient {
    private static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 10
        configuration.timeoutIntervalForResource = 15
        return URLSession(configuration: configuration)
    }()

    /// Straight from the instance, not the app's local proxy, which only runs
    /// while the app does.
    static func fetch(_ instance: URL, token: String) async throws -> WorkSummary {
        var request = URLRequest(url: instance.appendingPathComponent("api/users/me/work-summary"))
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw WorkSummaryError.unreachable
        }
        switch (response as? HTTPURLResponse)?.statusCode ?? 0 {
        case 401: throw WorkSummaryError.signedOut
        case 200..<300: return try JSONDecoder().decode(WorkSummary.self, from: data)
        default: throw WorkSummaryError.unreachable
        }
    }
}
