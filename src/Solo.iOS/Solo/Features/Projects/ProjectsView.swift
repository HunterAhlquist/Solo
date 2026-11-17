import SwiftUI

/// Every project the person belongs to, each with its open boards one tap away.
struct ProjectsView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @State private var creating = false

    private let columns = [GridItem(.adaptive(minimum: 300), spacing: 14, alignment: .top)]

    var body: some View {
        let nav = session.projects
        ScrollView {
            if !nav.hasLoaded {
                ProgressView().controlSize(.large).padding(.top, 80)
            } else if nav.projects.isEmpty {
                ContentUnavailableView {
                    Label("No projects yet.", systemImage: "square.grid.2x2")
                } description: {
                    if !session.user.isSuperAdmin {
                        Text("Ask an administrator to invite you to a project.")
                    }
                } actions: {
                    if session.user.isSuperAdmin {
                        Button("Create One") { creating = true }.buttonStyle(.clearGlass)
                    }
                }
                .padding(.top, 60)
            } else {
                LazyVGrid(columns: columns, spacing: 14) {
                    ForEach(nav.ordered) { project in
                        ProjectCard(project: project)
                    }
                }
                .padding(16)
            }
        }
        .themedBackground()
        .navigationTitle("Projects")
        .toolbar {
            if session.user.isSuperAdmin {
                ToolbarItem(placement: .primaryAction) {
                    Button("New Project", systemImage: "plus") { creating = true }
                }
            }
        }
        .task {
            await nav.refreshProjects()
            for project in nav.projects {
                await nav.ensureBoards(project.id)
            }
        }
        .reloads {
            await nav.refreshProjects()
            for project in nav.projects {
                await nav.refreshBoards(project.id)
            }
        }
        .sheet(isPresented: $creating) {
            NameIconSheet(title: "New project") { value in
                guard let project = await session.save({
                    try await session.api.createProject(ProjectRequest(
                        name: value.name, alias: value.alias, icon: value.icon, imageId: value.imageId))
                }) else { return false }
                await nav.refreshProjects()
                navigator.push(.project(alias: project.alias))
                return true
            }
        }
    }
}

private struct ProjectCard: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button {
                navigator.push(.project(alias: project.alias))
            } label: {
                HStack(spacing: 12) {
                    EntityIcon(imageId: project.imageId, icon: project.icon, size: 28)
                        .foregroundStyle(theme.primary)
                        .frame(width: 46, height: 46)
                        .background(theme.primary.opacity(0.12), in: .circle)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(project.name)
                            .font(.headline)
                            .foregroundStyle(theme.textPrimary)
                            .lineLimit(1)
                        HStack(spacing: 6) {
                            if project.isOwner {
                                Badge(text: "Owner", color: theme.accentWash, foreground: theme.primary)
                            }
                            Badge(text: project.role == .readWrite ? "Read/write" : "Read only",
                                  color: theme.backgroundGray, foreground: theme.textSecondary)
                        }
                    }
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right")
                        .font(.footnote.weight(.semibold))
                        .foregroundStyle(theme.textDisabled)
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(project.name) overview")

            VStack(alignment: .leading, spacing: 2) {
                SectionTitle("Boards")
                if let boards = session.projects.openBoards(project.id) {
                    if boards.isEmpty {
                        Text("No open boards.").font(.caption).foregroundStyle(theme.textSecondary).padding(.vertical, 4)
                    }
                    ForEach(boards) { board in
                        Button {
                            navigator.open(.board(project: project.alias, board: board.alias))
                        } label: {
                            HStack(spacing: 8) {
                                EntityIcon(imageId: board.imageId, icon: board.icon, size: 18, fallback: "rectangle.split.3x1")
                                    .foregroundStyle(theme.primary)
                                Text(board.name).foregroundStyle(theme.textPrimary).lineLimit(1)
                                Spacer(minLength: 0)
                            }
                            .font(.subheadline)
                            .padding(.vertical, 7)
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                } else {
                    ProgressView().padding(.vertical, 6)
                }
            }

            ProjectCardExtensions(project: project)
        }
        .card(padding: 14)
    }
}

struct Badge: View {
    var text: String
    var color: Color
    var foreground: Color

    var body: some View {
        Text(text)
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7)
            .padding(.vertical, 2)
            .foregroundStyle(foreground)
            .background(color, in: .capsule)
    }
}
