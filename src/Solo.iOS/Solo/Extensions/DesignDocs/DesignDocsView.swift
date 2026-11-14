import SwiftUI

/// A design doc: its tree of notes beside the note being read. Where there is
/// room for both they share the page; on a phone the tree is the page and a
/// note is pushed over it.
struct DesignDocsView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.theme) private var theme
    var projectAlias: String
    var vaultId: UUID
    var noteId: UUID?

    @State private var store: DocVaultStore?
    @State private var project: ProjectSummary?
    @State private var authors: [UUID: String] = [:]
    @State private var selected: UUID?
    @State private var treeShown = true
    @State private var treeSheet = false
    @State private var problem: String?
    @State private var prompt: TextPrompt?
    @State private var confirmation: Confirmation?

    private var wide: Bool { sizeClass == .regular }

    private func canEdit(_ project: ProjectSummary, _ store: DocVaultStore) -> Bool {
        (project.isOwner || project.role == .readWrite) && store.vault?.isClosed == false
    }

    /// Creating, renaming, closing and deleting a design doc is a super admin's who owns the project.
    private var canManage: Bool { session.user.isSuperAdmin && project?.isOwner == true }

    /// The note on show: the one asked for, else the one last read here, else the first.
    private func currentNote(_ store: DocVaultStore) -> DocNote? {
        if wide {
            return store.note(selected) ?? store.note(noteId) ?? store.note(store.rememberedNote) ?? firstNote(store, in: nil)
        }
        return store.note(noteId)
    }

    private func firstNote(_ store: DocVaultStore, in folder: UUID?) -> DocNote? {
        for node in store.children(of: folder) {
            if !node.isFolder { return store.note(node.id) }
            if let inside = firstNote(store, in: node.id) { return inside }
        }
        return nil
    }

    var body: some View {
        Group {
            if let store, let project, store.tree != nil {
                content(store, project)
            } else if let problem = problem ?? store?.loadError {
                ContentUnavailableView(problem, systemImage: "doc.questionmark")
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .themedBackground()
        .navigationTitle(store?.vault?.name ?? "Design Docs")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbar }
        .task(id: vaultId) { await load() }
        .onDisappear {
            let leaving = store
            Task { await leaving?.stop() }
        }
        .onChange(of: store?.deleted) { _, deleted in
            guard deleted == true else { return }
            session.toasts.show("That design doc was deleted.", .info)
            navigator.open(.project(alias: projectAlias))
        }
        .textPrompt($prompt)
        .confirmation($confirmation)
    }

    @ViewBuilder
    private func content(_ store: DocVaultStore, _ project: ProjectSummary) -> some View {
        let editable = canEdit(project, store)
        let note = currentNote(store)
        Group {
            if wide {
                HStack(spacing: 0) {
                    if treeShown {
                        tree(store, editable: editable, selected: note?.id)
                            .frame(width: 300)
                            .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
                            .padding(.leading, 14)
                            .padding(.vertical, 10)
                            .transition(.move(edge: .leading).combined(with: .opacity))
                    }
                    page(store, note, editable: editable)
                }
            } else if noteId != nil {
                page(store, note, editable: editable)
            } else {
                tree(store, editable: editable, selected: nil)
            }
        }
        .onChange(of: note?.id, initial: true) { _, id in
            guard let id, let shown = store.note(id) else { return }
            store.rememberedNote = id
            store.reveal(shown)
        }
        .sheet(isPresented: $treeSheet) {
            NavigationStack {
                tree(store, editable: editable, selected: note?.id) { id in
                    treeSheet = false
                    replaceTop(id)
                }
                .themedBackground()
                .navigationTitle(store.vault?.name ?? "Notes")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { treeSheet = false }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func page(_ store: DocVaultStore, _ note: DocNote?, editable: Bool) -> some View {
        if let note {
            NoteView(store: store, note: note, mayWrite: editable, author: note.lastEditedBy.map { authors[$0] ?? "a former member" },
                     onOpenNote: open)
                .id(note.id)
        } else if store.tree?.notes.isEmpty == true {
            ContentUnavailableView {
                Label("No notes yet", systemImage: "doc.text")
            } description: {
                Text(editable ? "Add the first note to start this design doc." : "Nothing has been written here yet.")
            } actions: {
                if editable {
                    Button("New Note", systemImage: "plus") { addNote(store) }.buttonStyle(.glass)
                }
            }
        } else {
            ContentUnavailableView("That note isn't in this design doc", systemImage: "doc.questionmark",
                                   description: Text("It may have been deleted or moved to another one."))
        }
    }

    private func tree(_ store: DocVaultStore, editable: Bool, selected: UUID?, onOpen: ((UUID) -> Void)? = nil) -> some View {
        DocTreeView(store: store, canEdit: editable, selected: selected, onOpen: onOpen ?? open)
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .secondaryAction) {
            if let store, let project {
                if canEdit(project, store) {
                    Button("New Note", systemImage: "doc.badge.plus") { addNote(store) }
                }
                if canManage, let vault = store.vault {
                    Button("Rename Design Doc", systemImage: "pencil") { rename(store, vault) }
                    Button(vault.isClosed ? "Reopen Design Doc" : "Close Design Doc",
                           systemImage: vault.isClosed ? "arrow.uturn.backward" : "archivebox") {
                        Task { await store.changeVault(name: vault.name, isClosed: !vault.isClosed) }
                    }
                    if vault.isClosed {
                        Button("Delete Design Doc", systemImage: "trash", role: .destructive) { confirmDelete(store) }
                    }
                }
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            if store?.tree != nil {
                if wide {
                    Button("Notes", systemImage: "sidebar.left") {
                        withAnimation(.snappy) { treeShown.toggle() }
                    }
                    .accessibilityHint(treeShown ? "Hides the note tree" : "Shows the note tree")
                } else if noteId != nil {
                    Button("Notes", systemImage: "folder") { treeSheet = true }
                }
            }
        }
    }

    // MARK: Loading and moving about

    private func load() async {
        if session.projects.project(alias: projectAlias) == nil {
            await session.projects.refreshProjects()
        }
        guard let found = session.projects.project(alias: projectAlias) else {
            problem = "Project not found."
            return
        }
        project = found
        session.projects.recordVisit(found.id)
        let active = store ?? DocVaultStore(session: session, projectId: found.id, vaultId: vaultId)
        store = active
        await active.start()
        if authors.isEmpty, let details = try? await session.api.project(found.id) {
            authors = Dictionary(details.members.compactMap { member in member.userId.map { ($0, member.name) } },
                                 uniquingKeysWith: { first, _ in first })
        }
    }

    /// Following a link or picking from the tree: in place where the tree is beside the note, pushed where it isn't.
    private func open(_ id: UUID) {
        if wide {
            selected = id
        } else {
            navigator.push(.designDocs(project: projectAlias, vaultId: vaultId, noteId: id))
        }
    }

    /// Picking from the tree while already in a note swaps the note rather than stacking another.
    private func replaceTop(_ id: UUID) {
        var path = navigator[navigator.tab]
        guard !path.isEmpty else { return }
        path[path.count - 1] = .designDocs(project: projectAlias, vaultId: vaultId, noteId: id)
        navigator[navigator.tab] = path
    }

    private func addNote(_ store: DocVaultStore) {
        prompt = TextPrompt(title: "New note", placeholder: "Name") { name in
            if let created = await store.addNote(name: name, parent: nil) { open(created.id) }
        }
    }

    private func rename(_ store: DocVaultStore, _ vault: DocVault) {
        prompt = TextPrompt(title: "Rename design doc", placeholder: "Name", initial: vault.name, action: "Rename") { name in
            guard name != vault.name else { return }
            await store.changeVault(name: name, isClosed: vault.isClosed)
        }
    }

    private func confirmDelete(_ store: DocVaultStore) {
        confirmation = Confirmation(
            title: "Delete design doc",
            message: "Permanently delete every note, image and sketch in this closed design doc?") {
            if await store.deleteVault() {
                navigator.open(.project(alias: projectAlias))
            }
        }
    }
}

// MARK: - The tree

/// A design doc's folders and notes. Folders remember whether they were left
/// open; a long press on anything renames, moves or deletes it.
struct DocTreeView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var canEdit: Bool
    var selected: UUID?
    var onOpen: (UUID) -> Void

    @State private var prompt: TextPrompt?
    @State private var confirmation: Confirmation?

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 4) {
                Text("Notes").font(.headline).foregroundStyle(theme.textPrimary)
                if store.vault?.isClosed == true {
                    Text("Closed · read-only")
                        .font(.caption2.weight(.medium))
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(theme.surface, in: .capsule)
                        .foregroundStyle(theme.textSecondary)
                }
                Spacer(minLength: 0)
                if canEdit {
                    Button("New note", systemImage: "doc.badge.plus") { add(folder: false, in: nil) }
                    Button("New folder", systemImage: "folder.badge.plus") { add(folder: true, in: nil) }
                }
            }
            .labelStyle(.iconOnly)
            .buttonStyle(.borderless)
            .padding(.leading, 18)
            .padding(.trailing, 10)
            .frame(height: 48)

            if store.tree?.notes.isEmpty == true, store.tree?.folders.isEmpty == true {
                Text("No notes yet.")
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .padding(.top, 24)
            } else {
                List {
                    DocBranch(store: store, parent: nil, canEdit: canEdit, selected: selected, onOpen: onOpen,
                              prompt: $prompt, confirmation: $confirmation)
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
                .environment(\.defaultMinListRowHeight, 40)
            }
        }
        .textPrompt($prompt)
        .confirmation($confirmation)
    }

    private func add(folder: Bool, in parent: UUID?) {
        prompt = DocBranch.addPrompt(store: store, folder: folder, parent: parent, onOpen: onOpen)
    }
}

private struct DocBranch: View {
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var parent: UUID?
    var canEdit: Bool
    var selected: UUID?
    var onOpen: (UUID) -> Void
    @Binding var prompt: TextPrompt?
    @Binding var confirmation: Confirmation?

    var body: some View {
        ForEach(store.children(of: parent)) { node in
            if node.isFolder {
                DisclosureGroup(isExpanded: Binding(
                    get: { store.openFolders.contains(node.id) },
                    set: { open in
                        if open { store.openFolders.insert(node.id) } else { store.openFolders.remove(node.id) }
                    })) {
                    DocBranch(store: store, parent: node.id, canEdit: canEdit, selected: selected, onOpen: onOpen,
                              prompt: $prompt, confirmation: $confirmation)
                } label: {
                    Label(node.name, systemImage: "folder")
                        .lineLimit(1)
                        .contextMenu { menu(node) }
                }
                .listRowBackground(Color.clear)
            } else {
                Button { onOpen(node.id) } label: {
                    Label {
                        Text(node.name).foregroundStyle(theme.textPrimary)
                    } icon: {
                        Image(systemName: "doc.text").foregroundStyle(theme.primary)
                    }
                        .lineLimit(1)
                        .fontWeight(node.id == selected ? .semibold : .regular)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .contextMenu { menu(node) }
                .swipeActions(edge: .trailing) {
                    if canEdit {
                        Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(node) }
                    }
                }
                .listRowBackground(
                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(node.id == selected ? theme.accentWash : .clear)
                        .padding(.horizontal, 8)
                )
            }
        }
    }

    @ViewBuilder
    private func menu(_ node: DocNode) -> some View {
        if canEdit {
            if node.isFolder {
                Button("New Note", systemImage: "doc.badge.plus") {
                    prompt = Self.addPrompt(store: store, folder: false, parent: node.id, onOpen: onOpen)
                }
                Button("New Folder", systemImage: "folder.badge.plus") {
                    prompt = Self.addPrompt(store: store, folder: true, parent: node.id, onOpen: onOpen)
                }
            }
            Button("Rename", systemImage: "pencil") { rename(node) }
            let destinations = store.destinations(for: node)
            if node.parentId != nil || !destinations.isEmpty {
                Menu("Move to", systemImage: "folder") {
                    if node.parentId != nil {
                        Button("Top of the design doc") { Task { await store.move(node, to: nil) } }
                    }
                    ForEach(destinations, id: \.id) { destination in
                        Button(destination.path) { Task { await store.move(node, to: destination.id) } }
                    }
                }
            }
            Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(node) }
        }
    }

    static func addPrompt(store: DocVaultStore, folder: Bool, parent: UUID?, onOpen: @escaping (UUID) -> Void) -> TextPrompt {
        TextPrompt(title: folder ? "New folder" : "New note", placeholder: "Name") { name in
            if folder {
                await store.addFolder(name: name, parent: parent)
            } else if let created = await store.addNote(name: name, parent: parent) {
                onOpen(created.id)
            }
        }
    }

    private func rename(_ node: DocNode) {
        prompt = TextPrompt(title: "Rename", placeholder: "Name", initial: node.name, action: "Rename") { name in
            guard name != node.name else { return }
            // Wiki links name a note by its path, so a rename orphans them; say so before it happens.
            let references = node.isFolder ? 0 : await store.renameImpact(node.id)
            guard references > 0 else {
                await store.rename(node, to: name)
                return
            }
            confirmation = Confirmation(
                title: "Links keep their old path",
                message: "\(references) \(references == 1 ? "note" : "notes") may link to this name. "
                    + "Renaming leaves those wiki links unresolved.",
                action: "Rename") {
                await store.rename(node, to: name)
            }
        }
    }

    private func confirmDelete(_ node: DocNode) {
        confirmation = Confirmation(
            title: "Delete \(node.name)",
            message: node.isFolder ? "Delete this folder and all of its notes and sketches?" : "Delete this note and its sketches?") {
            await store.delete(node)
        }
    }
}
