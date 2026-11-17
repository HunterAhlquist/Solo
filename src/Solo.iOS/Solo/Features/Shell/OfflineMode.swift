import SwiftUI

// With the instance out of reach, the window is only what can be worked on without it.
//
// The rest of the app can still be read, from what its last answers were (`ResponseCache`),
// but nothing in it can be changed, and a window full of boards that refuse every drag is
// worse than none. What can be changed is what has been brought onto the device for that:
// the design docs that were downloaded (OfflineDocs.swift). So while `Session.offlineMode`
// lasts the sidebar lists those and nothing else, and the page is either one of them or the
// list of them (`AppTab.offline`); a phone, with no sidebar, has the list and no tabs.
//
// Where the app was is kept, and it goes back there when the instance answers. A design doc
// that was open when the connection went is not touched by any of this: it stays the page it
// was, with whatever was being written in it.

/// A project's design docs that are on this device.
struct OfflineProject: Identifiable {
    var id: UUID
    var alias: String
    /// What the project was last known as, where it is still known at all.
    var summary: ProjectSummary?
    var docs: [DocVault]
    /// Whether a new design doc may be made in it here, to be sent after.
    var canCreate: Bool

    var name: String { summary?.name ?? alias }
}

extension Session {
    /// What there is to work on with the instance out of reach, by project: each downloaded
    /// design doc as it stands here, less the ones deleted here.
    var offlineProjects: [OfflineProject] {
        var found: [OfflineProject] = []
        for vault in docsOffline.vaults where vault.ready {
            guard let view = docsOffline.view(vault.id), !view.deleted else { continue }
            if let index = found.firstIndex(where: { $0.id == vault.projectId }) {
                found[index].docs.append(view.tree.vault)
                found[index].canCreate = found[index].canCreate || view.canManage
            } else {
                found.append(OfflineProject(id: vault.projectId, alias: vault.projectAlias,
                                            summary: projects.projects.first { $0.id == vault.projectId },
                                            docs: [view.tree.vault], canCreate: view.canManage))
            }
        }
        for index in found.indices {
            found[index].docs.sort { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
        return found.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// A new design doc in a project that has one on the device: made here, and sent when
    /// the instance is back.
    func offlineDocPrompt(in project: OfflineProject) -> TextPrompt {
        TextPrompt(title: "New design doc in \(project.name)", placeholder: "Name", limit: 200) { [self] name in
            guard let created = await save({ try docsOffline.createVault(projectId: project.id, name: name) }), let created else { return }
            DocLists.changed(project.id)
            navigator.open(.designDocs(project: project.alias, vaultId: created.id))
        }
    }
}

/// Says that the instance is out of reach and what that means here, and asks it again.
private struct OfflineStatus: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    /// In the sidebar, where there is a third of the room.
    var compact = false
    @State private var asking = false

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 8 : 10) {
            Label("Offline", systemImage: "icloud.slash")
                .font(compact ? .subheadline.weight(.semibold) : .headline)
                .foregroundStyle(theme.warning)
            Text(compact
                ? "\(session.siteName) can't be reached."
                : "\(session.siteName) can't be reached. What is on this device can still be worked on, and is sent when the instance is back.")
                .font(compact ? .caption : .subheadline)
                .foregroundStyle(theme.textSecondary)
                .fixedSize(horizontal: false, vertical: true)
            Button {
                asking = true
                Task {
                    // Long enough to be seen to have asked, where the answer is no at once.
                    async let pause: Void? = try? Task.sleep(for: .milliseconds(600))
                    let reached = await session.checkConnection()
                    _ = await pause
                    asking = false
                    if !reached { session.toasts.show("Still can't reach \(session.siteName).", .info) }
                }
            } label: {
                HStack(spacing: 6) {
                    if asking { ProgressView().controlSize(.small) }
                    Text(asking ? "Trying…" : "Try Again")
                }
            }
            .buttonStyle(.clearGlass)
            .controlSize(compact ? .small : .regular)
            .disabled(asking)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The first page while the instance is out of reach: what there is to work on.
struct OfflineHome: View {
    @Environment(AppModel.self) private var model
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @State private var prompt: TextPrompt?

    private let columns = [GridItem(.adaptive(minimum: 300), spacing: 14, alignment: .top)]

    var body: some View {
        let projects = session.offlineProjects
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                OfflineStatus()
                    .card(padding: 14)
                if projects.isEmpty {
                    ContentUnavailableView {
                        Label("Nothing on this device", systemImage: "icloud.and.arrow.down")
                    } description: {
                        Text("A design doc can be downloaded from the cloud in its bar while \(session.siteName) is in reach, and worked on here when it is not.")
                    }
                    .padding(.top, 40)
                } else {
                    LazyVGrid(columns: columns, spacing: 14) {
                        ForEach(projects) { project in
                            OfflineProjectCard(project: project) { prompt = session.offlineDocPrompt(in: project) }
                        }
                    }
                }
            }
            .padding(16)
        }
        .themedBackground()
        .navigationTitle("Offline")
        // Another instance may be there when this one is not. A Mac has them in its File menu.
        #if os(iOS)
        .toolbar {
            if !model.otherInstances.isEmpty {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu("Instances", systemImage: "server.rack") {
                        ForEach(model.otherInstances) { instance in
                            Button(model.label(for: instance), systemImage: "server.rack") {
                                Task { await model.switchTo(instance.url) }
                            }
                        }
                    }
                }
            }
        }
        #endif
        .textPrompt($prompt)
    }
}

private struct OfflineProjectCard: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: OfflineProject
    var onCreate: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                EntityIcon(imageId: project.summary?.imageId, icon: project.summary?.icon, size: 24)
                    .foregroundStyle(theme.primary)
                    .frame(width: 40, height: 40)
                    .background(theme.primary.opacity(0.12), in: .circle)
                Text(project.name)
                    .font(.headline)
                    .foregroundStyle(theme.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            VStack(alignment: .leading, spacing: 2) {
                SectionTitle("Design Docs")
                ForEach(project.docs) { doc in
                    Button {
                        navigator.open(.designDocs(project: project.alias, vaultId: doc.id))
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: "doc.text").foregroundStyle(theme.primary)
                            Text(doc.name).foregroundStyle(theme.textPrimary).lineLimit(1)
                            if doc.isClosed {
                                Badge(text: "Closed", color: theme.backgroundGray, foreground: theme.textSecondary)
                            }
                            Spacer(minLength: 0)
                            OfflineDocState(vaultId: doc.id)
                            Image(systemName: "chevron.right")
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(theme.textDisabled)
                        }
                        .font(.subheadline)
                        .padding(.vertical, 7)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                if project.canCreate {
                    Button("New Design Doc", systemImage: "plus", action: onCreate)
                        .font(.subheadline)
                        .foregroundStyle(theme.textSecondary)
                        .buttonStyle(.plain)
                        .padding(.vertical, 7)
                }
            }
        }
        .card(padding: 14)
    }
}

/// What a downloaded design doc has that the instance has not: how much, or that some of it
/// was refused. Nothing where there is nothing.
private struct OfflineDocState: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var vaultId: UUID

    var body: some View {
        let problems = session.docsOffline.problems(in: vaultId).count
        let pending = session.docsOffline.pending(in: vaultId)
        if problems > 0 {
            Label("\(problems) to review", systemImage: "exclamationmark.triangle")
                .font(.caption)
                .foregroundStyle(theme.warning)
        } else if pending > 0 {
            Label("\(pending) to send", systemImage: "icloud.and.arrow.up")
                .font(.caption)
                .foregroundStyle(theme.textSecondary)
        }
    }
}

/// The sidebar while the instance is out of reach: the same design docs, to go between.
struct OfflineSidebar: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @State private var prompt: TextPrompt?

    private func isCurrent(_ doc: DocVault) -> Bool {
        if case .designDocs(_, let id, _) = navigator.current { return id == doc.id }
        return false
    }

    var body: some View {
        let projects = session.offlineProjects
        List {
            Section {
                OfflineStatus(compact: true)
                    .padding(.vertical, 6)
                SidebarRow(title: "On This Device", symbol: "internaldrive", selected: session.shownTab == .offline && navigator[.offline].isEmpty) {
                    navigator.popToRoot(.offline)
                }
            }
            ForEach(projects) { project in
                Section(project.name) {
                    ForEach(project.docs) { doc in
                        SidebarRow(title: doc.name, symbol: "doc.text", badge: session.docsOffline.pending(in: doc.id),
                                   selected: isCurrent(doc)) {
                            navigator.open(.designDocs(project: project.alias, vaultId: doc.id))
                        }
                    }
                    if project.canCreate {
                        Button("New Design Doc", systemImage: "plus") { prompt = session.offlineDocPrompt(in: project) }
                            .font(.subheadline)
                            .foregroundStyle(theme.textSecondary)
                    }
                }
            }
        }
        .listStyle(.sidebar)
        // As the sidebar it stands in for is drawn on a Mac: see `SidebarView`.
        #if os(macOS)
        .buttonStyle(.plain)
        .labelStyle(AsWrittenLabelStyle())
        #endif
        .navigationTitle(session.siteName)
        .textPrompt($prompt)
    }
}
