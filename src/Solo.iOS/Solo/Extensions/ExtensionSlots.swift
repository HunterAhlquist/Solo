import SwiftUI

// Where the baked-in extensions join core's screens. Core names a slot; what
// fills it depends on which extensions the instance reports (ExtensionCatalog),
// so core never asks about a particular extension itself.

/// A section an extension adds between a page's own. It is always in the view
/// tree, so its loading runs, and takes no room until it has something to
/// show — a `.task` hung on an empty `Group` would never start.
struct ExtensionSection<Content: View>: View {
    var shown: Bool
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if shown {
                content.padding(.top, 18)
            }
        }
    }
}

/// Sections an extension adds to a task's detail, under its fields. They sit
/// in a stack without spacing of its own: each brings its gap when it shows.
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
        if session.extensions.hasDesignDocs {
            LinkedDocsSection(task: task)
        }
    }
}

/// Sections an extension adds under a project's entry in the sidebar.
struct SidebarProjectExtensions: View {
    @Environment(Session.self) private var session
    var project: ProjectSummary

    var body: some View {
        if session.extensions.hasDesignDocs {
            DocsSidebarSection(project: project)
        }
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
        if session.extensions.hasDesignDocs {
            DocsHomeSection(project: summary)
        }
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
        if session.extensions.hasDesignDocs {
            DocsCardSection(project: project)
        }
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

/// Buttons extensions add to the Markdown editor's format row.
struct MarkdownEditorExtensions: View {
    @Environment(Session.self) private var session
    /// The project the text belongs to; without one there is nothing to link from.
    var projectId: UUID?
    var insert: (String) -> Void

    var body: some View {
        if session.extensions.hasDesignDocs, let projectId {
            DocLinkButton(projectId: projectId, insert: insert)
        }
    }
}

/// Links extensions own, wherever Markdown is shown.
enum ExtensionLinks {
    /// True when an installed extension recognised the link and is opening it.
    static func open(_ url: URL, session: Session) -> Bool {
        // Only the instance's own addresses: another site's /docs/… is another site's.
        guard url.host() == session.api.baseURL.host(), url.port == session.api.baseURL.port else { return false }
        if session.extensions.hasDesignDocs, DocLinks.open(url, session: session) { return true }
        return false
    }
}
