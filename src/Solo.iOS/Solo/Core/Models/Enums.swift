import Foundation

// The wire types of the Solo API. They mirror src/Solo.Shared one for one, so a
// field added there is added here under the same name. Everything in this folder
// is `nonisolated`: these are plain values that cross actors freely.

nonisolated enum ProjectRole: String, Codable, Sendable, CaseIterable {
    case read = "Read"
    case readWrite = "ReadWrite"

    var title: String { self == .read ? "Read" : "Read & write" }
}

/// Drives placement in the three fixed Kanban columns.
nonisolated enum TaskState: String, Codable, Sendable, CaseIterable, Identifiable {
    case unstarted = "Unstarted"
    case inProgress = "InProgress"
    case finished = "Finished"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .unstarted: "Unstarted"
        case .inProgress: "In Progress"
        case .finished: "Finished"
        }
    }

    var symbol: String {
        switch self {
        case .unstarted: "circle.dashed"
        case .inProgress: "circle.lefthalf.filled"
        case .finished: "checkmark.circle.fill"
        }
    }

    /// The numeric form, as extension endpoints and drop-zone ids spell it.
    var ordinal: Int {
        switch self {
        case .unstarted: 0
        case .inProgress: 1
        case .finished: 2
        }
    }
}

nonisolated enum LabelKind: String, Codable, Sendable {
    case task = "Task"
    case subTask = "SubTask"
}

/// REST spells enums as names; the hub spells them as numbers. This one only
/// travels over the hub.
nonisolated enum PresenceMode: Int, Codable, Sendable {
    case none = 0
    case viewing = 1
    case editing = 2
}

nonisolated enum ChangeKind: Int, Codable, Sendable {
    case created = 0
    case updated = 1
    case deleted = 2
}

nonisolated enum NotificationKind: String, Codable, Sendable {
    case assigned = "Assigned"
    case dueInTwoWeeks = "DueInTwoWeeks"
    case dueThisWeek = "DueThisWeek"

    var symbol: String {
        switch self {
        case .assigned: "person.crop.circle.badge.plus"
        case .dueInTwoWeeks: "calendar.badge.clock"
        case .dueThisWeek: "alarm"
        }
    }
}

nonisolated enum DigestFrequency: String, Codable, Sendable, CaseIterable, Identifiable {
    case off = "Off"
    case asItHappens = "AsItHappens"
    case daily = "Daily"
    case weekly = "Weekly"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .off: "Off"
        case .asItHappens: "As it happens"
        case .daily: "Daily"
        case .weekly: "Weekly"
        }
    }
}

nonisolated enum SmtpSecurity: String, Codable, Sendable, CaseIterable, Identifiable {
    case startTls = "StartTls"
    case sslOnConnect = "SslOnConnect"
    case none = "None"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .startTls: "STARTTLS"
        case .sslOnConnect: "SSL/TLS on connect"
        case .none: "None"
        }
    }
}

/// .NET's DayOfWeek, which the API also spells by name.
nonisolated enum Weekday: String, Codable, Sendable, CaseIterable, Identifiable {
    case sunday = "Sunday", monday = "Monday", tuesday = "Tuesday", wednesday = "Wednesday"
    case thursday = "Thursday", friday = "Friday", saturday = "Saturday"

    var id: String { rawValue }
}

nonisolated enum EntityTypes {
    static let project = "project"
    static let board = "board"
    static let epicGroup = "epicGroup"
    static let epic = "epic"
    static let task = "task"
    static let subTask = "subtask"
    static let label = "label"
    static let comment = "comment"
}

/// Character limits shared with the server's validation.
nonisolated enum FieldLimits {
    static let entityName = 200
    static let itemName = 300
    static let labelName = 100
    static let email = 320
    static let description = 20_000
    static let comment = 5_000
    static let instanceName = 40
    static let tagline = 120
}
