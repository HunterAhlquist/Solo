import Foundation

// MARK: - Users & projects

nonisolated struct User: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var email: String
    var displayName: String?
    var avatarUrl: String?
    var isSuperAdmin: Bool

    var name: String { displayName ?? email }
}

nonisolated struct Project: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var version: UInt32
    var members: [ProjectMember]
}

nonisolated struct ProjectSummary: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var role: ProjectRole
    var isOwner: Bool

    /// Owner or ReadWrite: the project's write rule, as the API applies it.
    var canWrite: Bool { isOwner || role == .readWrite }
}

nonisolated struct ProjectMember: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var userId: UUID?
    var email: String
    var displayName: String?
    var avatarUrl: String?
    var role: ProjectRole
    var isOwner: Bool
    var isPending: Bool

    var name: String { displayName ?? email }
    /// What an avatar is keyed by: the user once they have signed in, the invite until then.
    var avatarId: UUID { userId ?? id }
}

nonisolated struct ProjectRequest: Encodable, Sendable {
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var version: UInt32?
}

nonisolated struct InviteMemberRequest: Encodable, Sendable {
    var email: String
    var role: ProjectRole
}

nonisolated struct UpdateMemberRequest: Encodable, Sendable {
    var role: ProjectRole
    var isOwner: Bool
}

// MARK: - Boards

nonisolated struct Board: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var projectId: UUID
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var isOpen: Bool
    var version: UInt32
}

/// Everything needed to draw the Kanban view in one payload.
nonisolated struct BoardDetail: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var projectId: UUID
    var projectAlias: String
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var isOpen: Bool
    var version: UInt32
    var epics: [Epic]
    var tasks: [TaskItem]
    var labels: [SoloLabel]
    /// Absent from a host older than epic groups.
    var groups: [EpicGroup]?

    var summary: Board {
        Board(id: id, projectId: projectId, name: name, alias: alias, icon: icon, imageId: imageId,
              isOpen: isOpen, version: version)
    }
}

nonisolated struct BoardRequest: Encodable, Sendable {
    var name: String
    var alias: String
    var icon: String?
    var imageId: UUID?
    var version: UInt32?
}

/// Place the entity between its two new siblings; nil preceding is the head, nil following the tail.
nonisolated struct RerankRequest: Encodable, Sendable {
    var precedingId: UUID?
    var followingId: UUID?
    var version: UInt32?
}

// MARK: - Epics, tasks, sub-tasks

nonisolated struct EpicGroup: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var boardId: UUID
    var name: String
    var rank: Double
    var version: UInt32
}

nonisolated struct Epic: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var boardId: UUID
    var name: String
    var description: String?
    var rank: Double
    var version: UInt32
    var assigneeIds: [UUID]
    var groupId: UUID?
}

/// Named TaskItem because `Task` is Swift's.
nonisolated struct TaskItem: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var boardId: UUID
    var epicId: UUID?
    var number: Int
    var name: String
    var labelId: UUID?
    var description: String?
    var status: TaskState
    var createdAt: Date
    var deadlineAt: Day?
    var rank: Double
    var version: UInt32
    var assigneeIds: [UUID]
    var subTasks: [SubTask]
    var startAt: Day?

    var finishedSubTasks: Int { subTasks.count(where: \.isFinished) }
}

nonisolated struct SubTask: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var taskId: UUID
    var name: String
    var labelId: UUID?
    var isFinished: Bool
    var rank: Double
    var version: UInt32
}

nonisolated struct CreateEpicRequest: Encodable, Sendable {
    var groupId: UUID?
    var name: String
    var description: String?
}

nonisolated struct UpdateEpicRequest: Encodable, Sendable {
    var name: String
    var description: String?
    var version: UInt32
}

nonisolated struct MoveEpicRequest: Encodable, Sendable {
    var groupId: UUID?
    var precedingId: UUID?
    var followingId: UUID?
    var version: UInt32
}

nonisolated struct NameVersionRequest: Encodable, Sendable {
    var name: String
    var version: UInt32?
}

nonisolated struct CreateTaskRequest: Encodable, Sendable {
    var name: String
    var epicId: UUID?
    var labelId: UUID?
    var description: String?
    var deadlineAt: Day?
    var startAt: Day?
}

/// Deliberately carries no start date: this PUT replaces every field it names,
/// and the start moves only through the schedule endpoint.
nonisolated struct UpdateTaskRequest: Encodable, Sendable {
    var name: String
    var labelId: UUID?
    var description: String?
    var deadlineAt: Day?
    var version: UInt32
}

nonisolated struct SetTaskScheduleRequest: Encodable, Sendable {
    var startAt: Day?
    var deadlineAt: Day?
    var version: UInt32
}

nonisolated struct SubTaskRequest: Encodable, Sendable {
    var name: String
    var labelId: UUID?
    var version: UInt32?
}

// MARK: - Labels & comments

nonisolated struct SoloLabel: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var projectId: UUID
    var boardId: UUID?
    var kind: LabelKind
    var name: String
    var color: String
    var icon: String?
    var imageId: UUID?
    var version: UInt32
}

nonisolated struct LabelRequest: Encodable, Sendable {
    var kind: LabelKind?
    var name: String
    var color: String
    var icon: String?
    var imageId: UUID?
    var version: UInt32?
}

nonisolated struct Comment: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var authorId: UUID
    var authorDisplayName: String?
    var authorAvatarUrl: String?
    var body: String
    var createdAt: Date
    var epicId: UUID?
    var taskId: UUID?
    var version: UInt32
}

nonisolated struct CommentRequest: Encodable, Sendable {
    var body: String
    var version: UInt32?
}

// MARK: - Your tasks

/// The signed-in user's own work across every project, with what is needed to
/// draw those tasks the way a board does.
nonisolated struct AssignedTasks: Codable, Hashable, Sendable {
    var projects: [ProjectSummary]
    var boards: [Board]
    var labels: [SoloLabel]
    var members: [ProjectMember]
    var tasks: [TaskItem]
    var epics: [Epic]?
    var epicGroups: [EpicGroup]?

    static let empty = AssignedTasks(projects: [], boards: [], labels: [], members: [], tasks: [])
}

// MARK: - Audit & images

nonisolated struct AuditEntry: Codable, Hashable, Sendable, Identifiable {
    var id: Int64
    var timestamp: Date
    var userId: UUID?
    var userDisplayName: String?
    var tableName: String
    var rowId: String
    var columnName: String?
    var previousValue: String?
    var annotations: [String: String]?
}

nonisolated struct PagedResult<Item: Codable & Sendable & Hashable>: Codable, Sendable, Hashable {
    var items: [Item]
    var page: Int
    var pageSize: Int
    var totalCount: Int
}

nonisolated struct ImageAsset: Codable, Hashable, Sendable {
    var id: UUID
    var contentType: String
}

// MARK: - Serial numbers & references

/// Every project and board carries a short handle (A-Z0-9, max 8) that composes
/// task serial numbers such as DMD-PW26-12.
nonisolated enum Aliases {
    static let maxLength = 8
    static let patternError = "Use 1-8 characters: capital letters and digits only."

    static func normalize(_ alias: String) -> String {
        alias.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
    }

    static func isValid(_ alias: String) -> Bool {
        alias.wholeMatch(of: /[A-Z0-9]{1,8}/) != nil
    }

    static func serial(_ projectAlias: String, _ boardAlias: String, _ number: Int) -> String {
        "\(projectAlias)-\(boardAlias)-\(number)"
    }

    /// A handle suggested from a name: its initials, or its first letters when it is one word.
    static func suggest(from name: String) -> String {
        let words = name.uppercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber })
            .map { String($0.unicodeScalars.filter { $0.isASCII }) }.filter { !$0.isEmpty }
        guard let first = words.first else { return "" }
        let text = words.count == 1 ? String(first.prefix(4)) : words.compactMap(\.first).map(String.init).joined()
        return String(text.prefix(maxLength))
    }
}

nonisolated struct TaskReference: Hashable, Sendable {
    var projectAlias: String
    var boardAlias: String
    var number: Int

    var serial: String { Aliases.serial(projectAlias, boardAlias, number) }

    init(projectAlias: String, boardAlias: String, number: Int) {
        self.projectAlias = Aliases.normalize(projectAlias)
        self.boardAlias = Aliases.normalize(boardAlias)
        self.number = number
    }

    /// Parses PROJECT-BOARD-N. Aliases cannot contain hyphens, so the split is unambiguous.
    init?(serial: String) {
        let parts = serial.split(separator: "-", omittingEmptySubsequences: false)
        guard parts.count == 3, let number = Int(parts[2]), number > 0 else { return nil }
        let project = Aliases.normalize(String(parts[0])), board = Aliases.normalize(String(parts[1]))
        guard Aliases.isValid(project), Aliases.isValid(board) else { return nil }
        self.init(projectAlias: project, boardAlias: board, number: number)
    }

    /// The `||PROJECT-BOARD-N||` token descriptions and comments carry.
    nonisolated(unsafe) static let token = /\|\|([A-Za-z0-9]{1,8})-([A-Za-z0-9]{1,8})-([0-9]{1,9})\|\|/

    var markdownToken: String { "||\(serial)||" }
}

// MARK: - Ranking

nonisolated enum RankNeighbors {
    /// The pair either side of an insertion slot among a zone's *other* items.
    static func compute(_ others: [UUID], index: Int) -> (preceding: UUID?, following: UUID?) {
        guard !others.isEmpty else { return (nil, nil) }
        let slot = min(max(index, 0), others.count)
        return (slot > 0 ? others[slot - 1] : nil, slot < others.count ? others[slot] : nil)
    }
}
