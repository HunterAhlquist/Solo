import SwiftUI

// Placeholders for screens still to come; each is replaced by its real file.

struct InboxView: View { var body: some View { Text("Inbox") } }
struct AccountView: View { var body: some View { Text("Account") } }
struct NotificationSettingsView: View { var body: some View { Text("Notification settings") } }
struct DevicesView: View { var body: some View { Text("Devices") } }
struct AppearanceView: View { var body: some View { Text("Appearance") } }
struct AdminSettingsView: View { var body: some View { Text("Admin") } }
struct AdminUsersView: View { var body: some View { Text("Users") } }
struct AdminEmailView: View { var body: some View { Text("Email") } }
struct AdminExtensionsView: View { var body: some View { Text("Extensions") } }
struct AdminUpdatesView: View { var body: some View { Text("Updates") } }
struct AdminThemesView: View { var body: some View { Text("Themes") } }
struct AboutView: View { var body: some View { Text("About") } }
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
struct ProjectHomeView: View {
    var alias: String
    var body: some View { Text("Project \(alias)") }
}
