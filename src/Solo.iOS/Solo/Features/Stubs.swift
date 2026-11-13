import SwiftUI

// Placeholders for screens still to come; each is replaced by its real file.

struct McpAccessView: View { var body: some View { Text("MCP") } }
struct RoadmapView: View {
    var projectAlias: String
    var roadmapId: UUID
    var body: some View { Text("Roadmap") }
}
struct DesignDocsView: View {
    var projectAlias: String
    var vaultId: UUID
    var noteId: UUID?
    var body: some View { Text("Design Docs") }
}
