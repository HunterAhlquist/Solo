import Foundation
import Observation

/// The navigation tree: every project, and each project's boards once
/// something has asked for them. Signing in costs one request, not one per
/// project.
@Observable
final class ProjectNavStore {
    private static let orderKey = "solo.projectOrder"
    private static let recentKey = "solo.recentProjects"
    /// How many recently opened projects the sidebar unfolds on its own.
    static let autoExpandCount = 3

    private(set) var projects: [ProjectSummary] = []
    private(set) var boards: [UUID: [Board]] = [:]
    private(set) var hasLoaded = false
    var expanded: Set<UUID> = []

    /// A person's own arrangement of the list, kept on this device.
    private(set) var order: [UUID]
    private(set) var recent: [UUID]

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private var autoExpanded = false
    /// Each instance's arrangement is its own: the keys end in its address.
    @ObservationIgnored private let orderStore: String
    @ObservationIgnored private let recentStore: String

    init(api: APIClient) {
        self.api = api
        orderStore = "\(Self.orderKey).\(api.baseURL.absoluteString)"
        recentStore = "\(Self.recentKey).\(api.baseURL.absoluteString)"
        // From before they were kept by instance. A project's id is its instance's alone, so
        // an arrangement read by the wrong one places nothing.
        order = Self.ids(orderStore) ?? Self.ids(Self.orderKey) ?? []
        recent = Self.ids(recentStore) ?? Self.ids(Self.recentKey) ?? []
    }

    var ordered: [ProjectSummary] {
        guard !order.isEmpty else { return projects }
        let byId = Dictionary(uniqueKeysWithValues: projects.map { ($0.id, $0) })
        let placed = order.compactMap { byId[$0] }
        let placedIds = Set(placed.map(\.id))
        return placed + projects.filter { !placedIds.contains($0.id) }
    }

    func project(alias: String) -> ProjectSummary? {
        projects.first { $0.alias.caseInsensitiveCompare(alias) == .orderedSame }
    }

    func project(_ id: UUID) -> ProjectSummary? {
        projects.first { $0.id == id }
    }

    /// Open boards only: closed ones stay reachable from the project page.
    func openBoards(_ projectId: UUID) -> [Board]? {
        boards[projectId]?.filter(\.isOpen)
    }

    func refreshProjects() async {
        guard let fetched = try? await api.projects() else { return }
        projects = fetched
        hasLoaded = true
        if !autoExpanded {
            autoExpanded = true
            for id in recent.filter({ id in fetched.contains { $0.id == id } }).prefix(Self.autoExpandCount) {
                expand(id)
            }
        }
    }

    func refreshBoards(_ projectId: UUID) async {
        if let fetched = try? await api.boards(projectId) {
            boards[projectId] = fetched
        }
    }

    func ensureBoards(_ projectId: UUID) async {
        if boards[projectId] == nil {
            await refreshBoards(projectId)
        }
    }

    func expand(_ projectId: UUID) {
        expanded.insert(projectId)
        Task { await ensureBoards(projectId) }
    }

    func move(from source: IndexSet, to destination: Int) {
        var list = ordered.map(\.id)
        list.move(fromOffsets: source, toOffset: destination)
        order = list
        UserDefaults.standard.set(list.map(\.uuidString), forKey: orderStore)
    }

    /// Remembers a project as just visited, so the sidebar can unfold recent work.
    func recordVisit(_ projectId: UUID) {
        guard recent.first != projectId else { return }
        recent = [projectId] + recent.filter { $0 != projectId }.prefix(9)
        UserDefaults.standard.set(recent.map(\.uuidString), forKey: recentStore)
    }

    private static func ids(_ key: String) -> [UUID]? {
        UserDefaults.standard.stringArray(forKey: key)?.compactMap(UUID.init)
    }
}
