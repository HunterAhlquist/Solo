import Foundation

// Extensions this app has no native half for. Their pages are the web
// client's, shown inside the app (HostedPage.swift); what is here is the part
// that needs no web view: which pages there are, who may see them, and which
// of the web client's addresses the app has screens of its own for.

/// A page an extension offers in its manifest's `nav`, as the web's sidebar lists it.
nonisolated struct PluginPage: Hashable, Identifiable, Sendable {
    var extensionId: String
    var label: String
    var icon: String?
    /// As the manifest wrote it: `/roadmap/{projectAlias}`, or a route with no project in it.
    var route: String
    var requires: String?

    var id: String { extensionId + ":" + route }

    private static let projectToken = "{projectAlias}"

    /// One entry per project, in that project's group; otherwise once for the instance.
    var isProjectLevel: Bool { route.range(of: Self.projectToken, options: .caseInsensitive) != nil }

    /// The address to load, relative to the instance.
    func path(project alias: String? = nil) -> String {
        var path = route
        while path.hasPrefix("/") { path.removeFirst() }
        guard let alias else { return path }
        return path.replacingOccurrences(of: Self.projectToken, with: alias, options: .caseInsensitive)
    }

    /// The manifest's `requires`, checked against the same role the web's sidebar uses, so an
    /// extension can't list a page here that the API would refuse.
    func allows(_ project: ProjectSummary) -> Bool {
        switch requires {
        case "Owner": project.isOwner
        case "Write": project.canWrite
        default: true
        }
    }

    /// The manifest names an icon as a project or a board does; a name the app doesn't know is the puzzle piece.
    var symbol: String { SoloIcons.symbol(icon, fallback: "puzzlepiece.extension") }
}

/// The web client's addresses this app shows natively. A hosted page is told them, so that a
/// link to one comes back to the app instead of drawing the web's version inside the page.
nonisolated enum HostedRoutes {
    /// Where an address leads in the app.
    enum Destination: Equatable {
        case tab(AppTab)
        case route(Route)
    }

    /// The baked-in extensions' pages are the app's only where the instance has the extension.
    struct Baked: Equatable, Sendable {
        var roadmap = false
        var designDocs = false
        var mcp = false
    }

    private static let alias = "([^/?#]+)"
    private static let uuid = "([0-9a-fA-F-]{36})"
    private static let tail = "(?:[?#].*)?$"

    /// Regular expressions for the paths the app owns, relative to the instance, as the page tests them.
    static func patterns(_ baked: Baked) -> [String] {
        var patterns = [
            "^/?\(tail)",
            "^projects/?\(tail)",
            "^projects/\(alias)/?\(tail)",
            "^projects/\(alias)/boards/\(alias)/?\(tail)",
            "^notifications/?\(tail)",
        ]
        if baked.roadmap { patterns.append("^projects/\(alias)/roadmaps/\(uuid)/?\(tail)") }
        if baked.designDocs { patterns.append("^docs/\(alias)/\(uuid)/?\(tail)") }
        if baked.mcp { patterns.append("^mcp/?\(tail)") }
        return patterns
    }

    /// Where a path the page handed over leads, or nil when it is not one of the app's after all.
    static func destination(for path: String, _ baked: Baked) -> Destination? {
        guard patterns(baked).contains(where: { path.range(of: $0, options: .regularExpression) != nil }) else { return nil }
        let parts = path.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let query = parts.count > 1 ? String(parts[1].split(separator: "#").first ?? "") : ""
        let pieces = (parts.first ?? "").split(separator: "#").first?
            .split(separator: "/").map { $0.removingPercentEncoding ?? String($0) } ?? []

        switch pieces.count {
        case 0:
            return .tab(.work)
        case 1 where pieces[0] == "projects":
            return .tab(.projects)
        case 1 where pieces[0] == "notifications":
            return .tab(.inbox)
        case 1 where pieces[0] == "mcp":
            return .route(.mcpAccess)
        case 2 where pieces[0] == "projects":
            return .route(.project(alias: pieces[1]))
        case 3 where pieces[0] == "docs":
            return UUID(uuidString: pieces[2]).map { .route(.designDocs(project: pieces[1], vaultId: $0)) }
        case 4 where pieces[2] == "boards":
            return .route(.board(project: pieces[1], board: pieces[3], task: taskNumber(in: query)))
        case 4 where pieces[2] == "roadmaps":
            return UUID(uuidString: pieces[3]).map { .route(.roadmap(project: pieces[1], roadmapId: $0)) }
        default:
            return nil
        }
    }

    /// A board link's `?task=`: the task's number, or its serial, which ends in the number.
    private static func taskNumber(in query: String) -> Int? {
        for pair in query.split(separator: "&") {
            let item = pair.split(separator: "=", maxSplits: 1)
            guard item.count == 2, item[0] == "task" else { continue }
            let value = item[1].removingPercentEncoding ?? String(item[1])
            return Int(value) ?? value.split(separator: "-").last.flatMap { Int($0) }
        }
        return nil
    }
}
