import SwiftUI

private enum DocCreator {
    /// A new design doc starts empty here; importing an Obsidian vault stays on the web.
    static func prompt(session: Session, project: ProjectSummary) -> TextPrompt {
        TextPrompt(title: "New design doc in \(project.name)", placeholder: "Name", limit: 200) { name in
            // Made on the device where the project already has a design doc downloaded, and sent
            // after; otherwise by the instance.
            guard let created = await session.save({
                if let made = try session.docsOffline.createVault(projectId: project.id, name: name) { return made }
                return try await session.api.createDocVault(project.id, name: name)
            }) else { return }
            DocLists.changed(project.id)
            session.navigator.open(.designDocs(project: project.alias, vaultId: created.id))
        }
    }
}

/// Under a project in the sidebar: its open design docs, and — for whoever may — a way to add one.
struct DocsSidebarSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    @State private var model: DocVaultListModel?
    @State private var prompt: TextPrompt?

    private func isCurrent(_ vault: DocVault) -> Bool {
        if case .designDocs(_, let id, _) = navigator.current { return id == vault.id }
        return false
    }

    var body: some View {
        Group {
            Text("Design Docs")
                .font(.caption.weight(.semibold))
                .foregroundStyle(theme.textSecondary)
            if let model, model.list != nil {
                ForEach(model.open) { vault in
                    SidebarRow(title: vault.name, symbol: "doc.text", selected: isCurrent(vault)) {
                        navigator.open(.designDocs(project: project.alias, vaultId: vault.id))
                    }
                }
                if model.open.isEmpty {
                    Text("No open design docs.").font(.caption).foregroundStyle(theme.textSecondary)
                }
                if model.canManage {
                    Button("New Design Doc", systemImage: "plus") {
                        prompt = DocCreator.prompt(session: session, project: project)
                    }
                    .font(.subheadline)
                    .foregroundStyle(theme.textSecondary)
                }
            } else if model?.failed == true {
                Text("Design docs couldn't load.").font(.caption).foregroundStyle(theme.error)
            }
        }
        .task(id: project.id) {
            let fresh = model ?? DocVaultListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: true)
        }
        .onDisappear { Task { await model?.stop() } }
        .textPrompt($prompt)
    }
}

/// On a project's page: its design docs as cards, and the closed ones folded away beneath.
struct DocsHomeSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    @State private var model: DocVaultListModel?
    @State private var prompt: TextPrompt?
    @State private var showingClosed = false

    private let columns = [GridItem(.adaptive(minimum: 240), spacing: 12, alignment: .top)]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionTitle("Design Docs")
            if let model, model.list != nil {
                if model.open.isEmpty && !model.canManage {
                    Text("No design docs yet.").foregroundStyle(theme.textSecondary)
                }
                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(model.open) { vault in
                        HStack(spacing: 12) {
                            Button {
                                navigator.open(.designDocs(project: project.alias, vaultId: vault.id))
                            } label: {
                                HStack(spacing: 12) {
                                    Image(systemName: "doc.text")
                                        .font(.title3)
                                        .foregroundStyle(theme.primary)
                                        .frame(width: 28)
                                    Text(vault.name).font(.headline).foregroundStyle(theme.textPrimary).lineLimit(1)
                                    Spacer(minLength: 0)
                                }
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            if model.canManage {
                                RowMenu("Design doc actions", systemImage: "ellipsis") {
                                    Button("Rename", systemImage: "pencil") { rename(vault) }
                                    Button("Close", systemImage: "archivebox") { change(vault, name: vault.name, closed: true) }
                                }
                            }
                        }
                        .card(padding: 14)
                    }
                    if model.canManage {
                        Button {
                            prompt = DocCreator.prompt(session: session, project: project)
                        } label: {
                            Label("New Design Doc", systemImage: "plus")
                                .font(.subheadline.weight(.medium))
                                .frame(maxWidth: .infinity, minHeight: 34)
                                .padding(14)
                                .foregroundStyle(theme.textSecondary)
                                .overlay(theme.cardShape.strokeBorder(theme.divider, style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])))
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                }
                if !model.closed.isEmpty {
                    DisclosureGroup("Closed design docs (\(model.closed.count))", isExpanded: $showingClosed) {
                        ForEach(model.closed) { vault in
                            HStack {
                                Label(vault.name, systemImage: "archivebox").lineLimit(1)
                                Spacer(minLength: 8)
                                if model.canManage {
                                    Button("Reopen") { change(vault, name: vault.name, closed: false) }
                                }
                                Button("View") { navigator.open(.designDocs(project: project.alias, vaultId: vault.id)) }
                            }
                            .font(.subheadline)
                            .buttonStyle(.borderless)
                            .padding(.vertical, 4)
                        }
                    }
                    .font(.subheadline)
                    .foregroundStyle(theme.textSecondary)
                    .tint(theme.textSecondary)
                }
            } else if model?.failed == true {
                Text("Design docs couldn't load.").font(.footnote).foregroundStyle(theme.error)
            } else {
                ProgressView()
            }
        }
        .task(id: project.id) {
            let fresh = model ?? DocVaultListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: true)
        }
        .onDisappear { Task { await model?.stop() } }
        .textPrompt($prompt)
    }

    private func rename(_ vault: DocVault) {
        prompt = TextPrompt(title: "Rename design doc", placeholder: "Name", initial: vault.name, action: "Rename", limit: 200) { name in
            guard name != vault.name else { return }
            change(vault, name: name, closed: vault.isClosed)
        }
    }

    private func change(_ vault: DocVault, name: String, closed: Bool) {
        Task {
            do {
                if session.docsOffline.isDownloaded(vault.id) {
                    try session.docsOffline.changeVault(vault.id, name: name, isClosed: closed)
                } else {
                    _ = try await session.api.changeDocVault(project.id, vault, name: name, isClosed: closed)
                }
            } catch let error as APIError where error.isConflict {
                session.toasts.show("Someone else changed this design doc first. It's been refreshed; try again.", .warning)
            } catch {
                session.toasts.error(error)
            }
            DocLists.changed(project.id)
        }
    }
}

/// On a project's card in the projects list: its open design docs, one line each.
struct DocsCardSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    @State private var model: DocVaultListModel?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            SectionTitle("Design Docs")
            if let model, model.list != nil {
                if model.open.isEmpty {
                    Text("No open design docs.").font(.caption).foregroundStyle(theme.textSecondary).padding(.vertical, 4)
                }
                ForEach(model.open) { vault in
                    Button {
                        navigator.open(.designDocs(project: project.alias, vaultId: vault.id))
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: "doc.text").foregroundStyle(theme.primary).frame(width: 18)
                            Text(vault.name).foregroundStyle(theme.textPrimary).lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .font(.subheadline)
                        .padding(.vertical, 7)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            } else if model?.failed == true {
                Text("Design docs couldn't load.").font(.caption).foregroundStyle(theme.error)
            } else {
                ProgressView().padding(.vertical, 6)
            }
        }
        .task(id: project.id) {
            let fresh = model ?? DocVaultListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: false)
        }
    }
}

/// On a task's detail: the notes its description links to, by their current
/// names. Nothing at all when it links to none.
struct LinkedDocsSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var task: TaskItem

    @State private var links: [(url: String, name: String)] = []

    var body: some View {
        ExtensionSection(shown: !links.isEmpty) {
            if !links.isEmpty {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) {
                        Text("Linked docs").font(.caption).foregroundStyle(theme.textSecondary)
                        ForEach(links, id: \.url) { link in
                            Button {
                                DocLinks.openNote(link.url, session: session)
                            } label: {
                                Label(link.name, systemImage: "doc.text").font(.caption.weight(.medium))
                            }
                            .buttonStyle(.clearGlass)
                        }
                    }
                }
                .scrollIndicators(.hidden)
            }
        }
        .task(id: task.description) {
            var seen = Set<String>()
            let urls = (task.description ?? "").matches(of: NoteAddress.pattern).map { String($0.output.0) }
                .filter { NoteAddress.read($0) != nil && seen.insert($0).inserted }
            guard !urls.isEmpty else {
                links = []
                return
            }
            let labels = (try? await session.api.docLinkLabels(Array(urls.prefix(200)))) ?? [:]
            links = urls.compactMap { url in labels[url].map { (url, $0) } }
        }
    }
}

/// In the Markdown editor's format row: links a note from the project the text belongs to.
struct DocLinkButton: View {
    var projectId: UUID
    var insert: (String) -> Void

    @State private var picking = false

    var body: some View {
        Button("Link a doc", systemImage: "doc.text") { picking = true }
            .sheet(isPresented: $picking) {
                DocLinkPicker(projectId: projectId, onPick: insert)
            }
    }
}
