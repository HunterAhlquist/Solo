import SwiftUI

// Where the baked-in extensions join core's screens. Core names a slot; what
// fills it depends on which extensions the instance reports (ExtensionCatalog),
// so core never asks about a particular extension itself.

/// Sections an extension adds to a task's detail, under its fields.
struct TaskDetailExtensions: View {
    @Environment(Session.self) private var session
    var task: TaskItem
    var store: BoardStore

    var body: some View {
        if session.extensions.hasMcp {
            LlmWorkSection(task: task, store: store)
        }
        if session.extensions.hasRoadmap {
            RoadmapMembershipsSection(task: task)
        }
    }
}

/// Sections an extension adds under a project's entry in the sidebar.
struct SidebarProjectExtensions: View {
    @Environment(Session.self) private var session
    var project: ProjectSummary

    var body: some View {
        if session.extensions.hasRoadmap {
            RoadmapSidebarSection(project: project)
        }
    }
}

/// Sections an extension adds to a project's overview, after its boards.
struct ProjectHomeExtensions: View {
    @Environment(Session.self) private var session
    var project: Project
    var canWrite: Bool

    /// The sections are written against the project as the list knows it.
    private var summary: ProjectSummary {
        let me = project.members.first { $0.userId == session.user.id }
        return ProjectSummary(id: project.id, name: project.name, alias: project.alias, icon: project.icon,
                              imageId: project.imageId, role: me?.role ?? .read, isOwner: me?.isOwner ?? false)
    }

    var body: some View {
        if session.extensions.hasRoadmap {
            RoadmapHomeSection(project: summary, canWrite: canWrite)
        }
    }
}

/// Lines an extension adds to a project's card on the projects list.
struct ProjectCardExtensions: View {
    @Environment(Session.self) private var session
    var project: ProjectSummary

    var body: some View {
        if session.extensions.hasRoadmap {
            RoadmapCardSection(project: project)
        }
    }
}

/// Sections an extension adds above the columns of Your Tasks.
struct DashboardExtensions: View {
    @Environment(Session.self) private var session
    /// The page's own task panel, which a section opens tasks in.
    @Binding var openTask: UUID?

    var body: some View {
        if session.extensions.hasRoadmap {
            RoadmapDashboardSection(openTask: $openTask)
        }
    }
}

/// Instance-level pages an extension adds, listed once rather than per project.
struct InstanceExtensionLinks: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    /// In the sidebar a link replaces what is showing; in a list it pushes.
    var inSidebar = false

    var body: some View {
        if session.extensions.hasMcp {
            if inSidebar {
                SidebarRow(title: "MCP Access", symbol: "cpu",
                           selected: navigator.tab == .account && navigator.current == .mcpAccess) {
                    navigator.open(.mcpAccess)
                }
            } else {
                RouteLink(route: .mcpAccess) {
                    Label("MCP Access", systemImage: "cpu")
                }
            }
        }
    }
}

/// Settings panels extensions add to the admin pages.
struct AdminExtensionSettings: View {
    @Environment(Session.self) private var session

    var body: some View {
        if session.extensions.hasRoadmap {
            RoadmapSettingsPanel()
        }
    }
}
