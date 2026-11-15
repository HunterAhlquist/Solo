import Foundation
import Observation

/// Which first-party extensions this instance has loaded.
///
/// On the web an extension ships its client as an assembly the browser
/// downloads. A native app can't load code, so the first-party extensions'
/// clients are compiled in — and switched on only when the instance reports
/// the matching extension, so an instance without Roadmaps shows no roadmap
/// anywhere, exactly as its web app does.
@Observable
final class ExtensionCatalog {
    static let mcp = "mcp"
    static let roadmap = "roadmap"
    static let designDocs = "design-docs"

    private(set) var installed: [ClientExtension] = []

    @ObservationIgnored private let api: APIClient

    init(api: APIClient) {
        self.api = api
    }

    func load() async {
        if let fetched = try? await api.clientExtensions() {
            installed = fetched
        }
    }

    func has(_ id: String) -> Bool {
        installed.contains { $0.id == id }
    }

    var hasMcp: Bool { has(Self.mcp) }
    var hasRoadmap: Bool { has(Self.roadmap) }
    var hasDesignDocs: Bool { has(Self.designDocs) }

    func version(_ id: String) -> String? {
        installed.first { $0.id == id }?.version
    }

    /// The extensions whose clients are compiled in.
    private static let baked: Set<String> = [mcp, roadmap, designDocs]

    /// Extensions the instance runs that this app has no native half for. Their pages are
    /// shown from the web client; what they add to core's own screens is not shown at all,
    /// which Settings says, so that its absence reads as a limit of the app and not of the instance.
    var unsupported: [ClientExtension] {
        installed.filter { !Self.baked.contains($0.id) && $0.clientAssembly != nil }
    }

    /// The pages those extensions list in their manifests, in the order the instance gave them.
    var pluginPages: [PluginPage] {
        var shown = unsupported
        #if DEBUG
        // Verification: show a baked-in extension's manifest pages from the web as well, so the
        // web path can be tried against real pages. "SOLO_WEB_EXTENSIONS=mcp,roadmap".
        let also = Set((ProcessInfo.processInfo.environment["SOLO_WEB_EXTENSIONS"] ?? "").split(separator: ",").map(String.init))
        shown += installed.filter { also.contains($0.id) }
        #endif
        return shown.flatMap { item in
            item.nav.map { PluginPage(extensionId: item.id, label: $0.label, icon: $0.icon, route: $0.route, requires: $0.requires) }
        }
    }

    /// Pages listed once for the instance, and pages a project lists for someone with this access to it.
    var instancePages: [PluginPage] { pluginPages.filter { !$0.isProjectLevel } }

    func projectPages(for project: ProjectSummary) -> [PluginPage] {
        pluginPages.filter { $0.isProjectLevel && $0.allows(project) }
    }
}
