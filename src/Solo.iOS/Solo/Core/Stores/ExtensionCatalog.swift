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

    /// Extensions the instance runs that this app has no native half for:
    /// named in Settings, so their absence here reads as a limit of the app
    /// rather than of the instance.
    var unsupported: [ClientExtension] {
        installed.filter { ![Self.mcp, Self.roadmap, Self.designDocs].contains($0.id) && $0.clientAssembly != nil }
    }
}
