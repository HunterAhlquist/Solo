import Foundation

// One method per endpoint, in the order the API's controllers list them. Every
// mutation that edits an entity sends the `version` it was read at; a 409 back
// means someone saved first (see SaveGuard).

nonisolated private struct Empty: Encodable, Sendable {}

extension APIClient {
    // MARK: Bootstrap

    func authConfig() async throws -> AuthConfig { try await get("api/config/auth") }
    func instanceVersion() async throws -> InstanceVersion { try await get("api/config/version") }
    func currentUser() async throws -> User { try await get("api/users/me") }
    func activeTheme() async throws -> ThemeDocument { try await get("api/themes/active") }
    func clientExtensions() async throws -> [ClientExtension] { try await get("api/extensions") }

    // MARK: Projects

    func projects() async throws -> [ProjectSummary] { try await get("api/projects") }
    func project(_ id: UUID) async throws -> Project { try await get("api/projects/\(id.api)") }
    func project(alias: String) async throws -> Project { try await get("api/projects/by-alias/\(alias.pathEscaped)") }

    func createProject(_ request: ProjectRequest) async throws -> Project {
        try await send("POST", "api/projects", body: request)
    }

    func updateProject(_ id: UUID, _ request: ProjectRequest) async throws -> Project {
        try await send("PUT", "api/projects/\(id.api)", body: request)
    }

    func deleteProject(_ id: UUID) async throws { try await send("DELETE", "api/projects/\(id.api)") }

    // MARK: Members

    func members(_ projectId: UUID) async throws -> [ProjectMember] {
        try await get("api/projects/\(projectId.api)/members")
    }

    func inviteMember(_ projectId: UUID, email: String, role: ProjectRole) async throws -> ProjectMember {
        try await send("POST", "api/projects/\(projectId.api)/members", body: InviteMemberRequest(email: email, role: role))
    }

    func updateMember(_ projectId: UUID, _ memberId: UUID, role: ProjectRole, isOwner: Bool) async throws -> ProjectMember {
        try await send("PUT", "api/projects/\(projectId.api)/members/\(memberId.api)",
                       body: UpdateMemberRequest(role: role, isOwner: isOwner))
    }

    func removeMember(_ projectId: UUID, _ memberId: UUID) async throws {
        try await send("DELETE", "api/projects/\(projectId.api)/members/\(memberId.api)")
    }

    func resendInvite(_ projectId: UUID, _ memberId: UUID) async throws {
        try await send("POST", "api/projects/\(projectId.api)/members/\(memberId.api)/invite")
    }

    // MARK: Boards

    func boards(_ projectId: UUID) async throws -> [Board] { try await get("api/projects/\(projectId.api)/boards") }
    func board(_ id: UUID) async throws -> BoardDetail { try await get("api/boards/\(id.api)") }

    func board(projectAlias: String, boardAlias: String) async throws -> BoardDetail {
        try await get("api/projects/by-alias/\(projectAlias.pathEscaped)/boards/\(boardAlias.pathEscaped)")
    }

    func createBoard(_ projectId: UUID, _ request: BoardRequest) async throws -> Board {
        try await send("POST", "api/projects/\(projectId.api)/boards", body: request)
    }

    func updateBoard(_ id: UUID, _ request: BoardRequest) async throws -> Board {
        try await send("PUT", "api/boards/\(id.api)", body: request)
    }

    func setBoardOpen(_ id: UUID, _ isOpen: Bool) async throws -> Board {
        try await send("PUT", "api/boards/\(id.api)/open", body: ["isOpen": isOpen])
    }

    func deleteBoard(_ id: UUID) async throws { try await send("DELETE", "api/boards/\(id.api)") }

    // MARK: Epic groups

    func createEpicGroup(_ boardId: UUID, name: String) async throws -> EpicGroup {
        try await send("POST", "api/boards/\(boardId.api)/groups", body: NameVersionRequest(name: name))
    }

    func updateEpicGroup(_ group: EpicGroup, name: String) async throws -> EpicGroup {
        try await send("PUT", "api/boards/\(group.boardId.api)/groups/\(group.id.api)",
                       body: NameVersionRequest(name: name, version: group.version))
    }

    func deleteEpicGroup(_ group: EpicGroup) async throws {
        try await send("DELETE", "api/boards/\(group.boardId.api)/groups/\(group.id.api)?version=\(group.version)")
    }

    func rerankEpicGroup(_ group: EpicGroup, preceding: UUID?, following: UUID?) async throws -> EpicGroup {
        try await send("PUT", "api/boards/\(group.boardId.api)/groups/\(group.id.api)/rank",
                       body: RerankRequest(precedingId: preceding, followingId: following, version: group.version))
    }

    // MARK: Epics

    func epics(_ boardId: UUID) async throws -> [Epic] { try await get("api/boards/\(boardId.api)/epics") }

    func createEpic(_ boardId: UUID, name: String, groupId: UUID? = nil) async throws -> Epic {
        try await send("POST", "api/boards/\(boardId.api)/epics", body: CreateEpicRequest(groupId: groupId, name: name))
    }

    func updateEpic(_ epic: Epic, name: String, description: String?) async throws -> Epic {
        try await send("PUT", "api/epics/\(epic.id.api)",
                       body: UpdateEpicRequest(name: name, description: description, version: epic.version))
    }

    func deleteEpic(_ id: UUID) async throws { try await send("DELETE", "api/epics/\(id.api)") }

    func moveEpic(_ epic: Epic, groupId: UUID?, preceding: UUID?, following: UUID?) async throws -> Epic {
        try await send("PUT", "api/epics/\(epic.id.api)/group",
                       body: MoveEpicRequest(groupId: groupId, precedingId: preceding, followingId: following, version: epic.version))
    }

    func setEpicAssignees(_ id: UUID, _ userIds: [UUID]) async throws -> Epic {
        try await send("PUT", "api/epics/\(id.api)/assignees", body: ["userIds": userIds])
    }

    // MARK: Tasks

    func task(_ id: UUID) async throws -> TaskItem { try await get("api/tasks/\(id.api)") }
    func assignedTasks() async throws -> AssignedTasks { try await get("api/users/me/tasks") }

    func createTask(_ boardId: UUID, _ request: CreateTaskRequest) async throws -> TaskItem {
        try await send("POST", "api/boards/\(boardId.api)/tasks", body: request)
    }

    func updateTask(_ task: TaskItem, name: String? = nil, labelId: UUID?? = nil, description: String?? = nil,
                    deadline: Day?? = nil) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(task.id.api)", body: UpdateTaskRequest(
            name: name ?? task.name,
            labelId: labelId ?? task.labelId,
            description: description ?? task.description,
            deadlineAt: deadline ?? task.deadlineAt,
            version: task.version))
    }

    func setTaskSchedule(_ task: TaskItem, start: Day?, deadline: Day?) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(task.id.api)/schedule",
                       body: SetTaskScheduleRequest(startAt: start, deadlineAt: deadline, version: task.version))
    }

    func deleteTask(_ id: UUID) async throws { try await send("DELETE", "api/tasks/\(id.api)") }

    func setTaskStatus(_ id: UUID, _ status: TaskState) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(id.api)/status", body: ["status": status])
    }

    func setTaskEpic(_ id: UUID, _ epicId: UUID?) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(id.api)/epic", body: ["epicId": epicId])
    }

    func moveTask(_ id: UUID, toBoard boardId: UUID, epicId: UUID?) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(id.api)/board", body: ["boardId": boardId, "epicId": epicId])
    }

    func rerankTask(_ id: UUID, preceding: UUID?, following: UUID?) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(id.api)/rank", body: RerankRequest(precedingId: preceding, followingId: following))
    }

    func setTaskAssignees(_ id: UUID, _ userIds: [UUID]) async throws -> TaskItem {
        try await send("PUT", "api/tasks/\(id.api)/assignees", body: ["userIds": userIds])
    }

    // MARK: Sub-tasks

    func createSubTask(_ taskId: UUID, name: String, labelId: UUID? = nil) async throws -> SubTask {
        try await send("POST", "api/tasks/\(taskId.api)/subtasks", body: SubTaskRequest(name: name, labelId: labelId))
    }

    func updateSubTask(_ subTask: SubTask, name: String, labelId: UUID?) async throws -> SubTask {
        try await send("PUT", "api/subtasks/\(subTask.id.api)",
                       body: SubTaskRequest(name: name, labelId: labelId, version: subTask.version))
    }

    func deleteSubTask(_ id: UUID) async throws { try await send("DELETE", "api/subtasks/\(id.api)") }

    func setSubTaskFinished(_ id: UUID, _ isFinished: Bool) async throws -> SubTask {
        try await send("PUT", "api/subtasks/\(id.api)/finished", body: ["isFinished": isFinished])
    }

    func rerankSubTask(_ id: UUID, preceding: UUID?, following: UUID?) async throws -> SubTask {
        try await send("PUT", "api/subtasks/\(id.api)/rank", body: RerankRequest(precedingId: preceding, followingId: following))
    }

    // MARK: Labels

    func projectLabels(_ projectId: UUID) async throws -> [SoloLabel] { try await get("api/projects/\(projectId.api)/labels") }
    func boardLabels(_ boardId: UUID) async throws -> [SoloLabel] { try await get("api/boards/\(boardId.api)/labels") }

    /// `scope` is the project for a label default, the board for one of its own labels.
    func createLabel(_ scope: LabelScope, _ request: LabelRequest) async throws -> SoloLabel {
        try await send("POST", "\(scope.path)/labels", body: request)
    }

    func updateLabel(_ scope: LabelScope, _ id: UUID, _ request: LabelRequest) async throws -> SoloLabel {
        try await send("PUT", "\(scope.path)/labels/\(id.api)", body: request)
    }

    func deleteLabel(_ scope: LabelScope, _ id: UUID) async throws {
        try await send("DELETE", "\(scope.path)/labels/\(id.api)")
    }

    func resetBoardLabels(_ boardId: UUID) async throws -> [SoloLabel] {
        try await send("POST", "api/boards/\(boardId.api)/labels/reset")
    }

    // MARK: Comments

    func comments(taskId: UUID) async throws -> [Comment] { try await get("api/tasks/\(taskId.api)/comments") }
    func comments(epicId: UUID) async throws -> [Comment] { try await get("api/epics/\(epicId.api)/comments") }

    func addComment(taskId: UUID, body: String) async throws -> Comment {
        try await send("POST", "api/tasks/\(taskId.api)/comments", body: CommentRequest(body: body))
    }

    func addComment(epicId: UUID, body: String) async throws -> Comment {
        try await send("POST", "api/epics/\(epicId.api)/comments", body: CommentRequest(body: body))
    }

    func updateComment(_ comment: Comment, body: String) async throws -> Comment {
        try await send("PUT", "api/comments/\(comment.id.api)", body: CommentRequest(body: body, version: comment.version))
    }

    func deleteComment(_ id: UUID) async throws { try await send("DELETE", "api/comments/\(id.api)") }

    // MARK: Images & audit

    func imageURL(_ id: UUID) -> URL { url("api/images/\(id.api)") }
    func avatarURL(_ userId: UUID) -> URL { url("api/users/\(userId.api)/avatar") }

    func uploadImage(_ data: Data, fileName: String, contentType: String) async throws -> ImageAsset {
        try await uploadFile("api/images", fileName: fileName, contentType: contentType, data: data)
    }

    func audit(_ projectId: UUID, table: String? = nil, userId: UUID? = nil, from: Date? = nil, to: Date? = nil,
               page: Int = 1, pageSize: Int = 50) async throws -> PagedResult<AuditEntry> {
        var query = [URLQueryItem(name: "page", value: "\(page)"), URLQueryItem(name: "pageSize", value: "\(pageSize)")]
        if let table = table?.nilIfBlank { query.append(.init(name: "table", value: table)) }
        if let userId { query.append(.init(name: "userId", value: userId.api)) }
        if let from { query.append(.init(name: "from", value: from.ISO8601Format())) }
        if let to { query.append(.init(name: "to", value: to.ISO8601Format())) }
        var components = URLComponents()
        components.queryItems = query
        return try await get("api/projects/\(projectId.api)/audit?\(components.percentEncodedQuery ?? "")")
    }

    // MARK: Notifications

    func notifications() async throws -> NotificationInbox { try await get("api/notifications") }
    /// Asked of the instance itself: a schedule made from the cache would be an old one.
    func upcomingReminders() async throws -> [UpcomingReminder] { try await send("GET", "api/notifications/upcoming") }
    func markNotificationRead(_ id: UUID) async throws { try await send("POST", "api/notifications/\(id.api)/read") }
    func markAllNotificationsRead() async throws { try await send("POST", "api/notifications/read") }
    func clearNotifications() async throws { try await send("POST", "api/notifications/clear") }
    func notificationPreferences() async throws -> NotificationPreferences { try await get("api/notifications/preferences") }
    func timeZones() async throws -> [String] { try await get("api/notifications/time-zones") }

    func updateNotificationPreferences(_ preferences: NotificationPreferences) async throws -> NotificationPreferences {
        try await send("PUT", "api/notifications/preferences", body: preferences)
    }

    // MARK: Sessions

    func sessions() async throws -> [AuthSession] { try await get("api/auth/sessions") }
    func revokeSession(_ id: UUID) async throws { try await send("DELETE", "api/auth/sessions/\(id.api)") }
    func revokeOtherSessions() async throws { try await send("DELETE", "api/auth/sessions") }
    func hubTicket() async throws -> String { try await send("POST", "api/auth/hub-ticket") }

    // MARK: Administration

    func instanceSettings() async throws -> InstanceSettings { try await get("api/settings") }

    func updateInstanceSettings(_ request: UpdateInstanceSettingsRequest) async throws -> InstanceSettings {
        try await send("PUT", "api/settings", body: request)
    }

    func themes() async throws -> ThemeScanResult { try await get("api/themes") }
    func rescanThemes() async throws -> ThemeScanResult { try await send("POST", "api/themes/rescan") }

    func adminUsers() async throws -> [AdminUser] { try await get("api/admin/users") }

    func setSuperAdmin(_ userId: UUID, _ isSuperAdmin: Bool) async throws -> AdminUser {
        try await send("PUT", "api/admin/users/\(userId.api)/super-admin", body: ["isSuperAdmin": isSuperAdmin])
    }

    func signOutEverywhere(_ userId: UUID) async throws -> SignOutEverywhereResult {
        try await send("POST", "api/admin/users/\(userId.api)/sign-out-everywhere")
    }

    func emailSettings() async throws -> EmailSettings { try await get("api/settings/email") }

    func updateEmailSettings(_ request: UpdateEmailSettingsRequest) async throws -> EmailSettings {
        try await send("PUT", "api/settings/email", body: request)
    }

    func sendTestEmail(_ request: UpdateEmailSettingsRequest) async throws -> TestEmailResult {
        try await send("POST", "api/settings/email/test", body: request)
    }

    func extensionAdmin() async throws -> ExtensionAdmin { try await get("api/extensions/admin") }

    func purgeExtension(_ id: String) async throws {
        try await send("DELETE", "api/extensions/\(id.pathEscaped)/data", body: ["confirmExtensionId": id])
    }

    func updates(refresh: Bool = false) async throws -> UpdateStatus {
        try await get(refresh ? "api/updates?refresh=true" : "api/updates")
    }

    func updateExtensions(_ ids: [String]) async throws -> ExtensionBulkUpdateResult {
        try await send("POST", "api/updates/extensions", body: ["extensionIds": ids])
    }
}

/// Where a label lives: a project's defaults, or one board's own set.
nonisolated enum LabelScope: Hashable, Sendable {
    case project(UUID)
    case board(UUID)

    var path: String {
        switch self {
        case .project(let id): "api/projects/\(id.api)"
        case .board(let id): "api/boards/\(id.api)"
        }
    }
}

extension UUID {
    /// Lower-case, as .NET writes a Guid, so ids compare equal as strings too.
    nonisolated var api: String { uuidString.lowercased() }
}

extension String {
    nonisolated var pathEscaped: String {
        addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(.init(charactersIn: "/"))) ?? self
    }
}
