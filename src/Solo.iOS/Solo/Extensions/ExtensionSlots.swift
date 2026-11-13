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
        EmptyView()
    }
}

/// Sections an extension adds under a project's entry in the sidebar.
struct SidebarProjectExtensions: View {
    @Environment(Session.self) private var session
    var project: ProjectSummary

    var body: some View {
        EmptyView()
    }
}

/// Sections an extension adds to a project's overview, after its boards.
struct ProjectHomeExtensions: View {
    @Environment(Session.self) private var session
    var project: Project
    var canWrite: Bool

    var body: some View {
        EmptyView()
    }
}

/// Lines an extension adds to a project's card on the projects list.
struct ProjectCardExtensions: View {
    @Environment(Session.self) private var session
    var project: ProjectSummary

    var body: some View {
        EmptyView()
    }
}

/// Sections an extension adds above the columns of Your Tasks.
struct DashboardExtensions: View {
    @Environment(Session.self) private var session

    var body: some View {
        EmptyView()
    }
}

/// Instance-level pages an extension adds, listed once rather than per project.
struct InstanceExtensionLinks: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator

    var body: some View {
        EmptyView()
    }
}

/// Settings panels extensions add to the admin pages.
struct AdminExtensionSettings: View {
    @Environment(Session.self) private var session

    var body: some View {
        EmptyView()
    }
}
