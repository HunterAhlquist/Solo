import SwiftUI
import UniformTypeIdentifiers

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
                            // The panel's shape cuts what scrolls inside it too: without this a row's
                            // highlight shows past the rounded corners as it scrolls out.
                            .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
                            .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
                            .padding(.leading, 14)
                            .padding(.vertical, 10)
                            .transition(.move(edge: .leading).combined(with: .opacity))
                    }
                    page(store, note, editable: editable)
                        // The note takes its new width at once while the tree slides. Animated, its
                        // text is laid out again at every width on the way, and the page jitters
                        // for as long as that takes.
                        .animation(nil, value: treeShown)
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

extension UTType {
    /// A note or folder being dragged about its design doc. Declared in Info.plist.
    static let soloDocNode = UTType(exportedAs: "com.hunterahlquist.solo.doc-node")
}

/// What a dragged note or folder carries: which one it is, for the tree to move it by — and,
/// for a note, a link to it as text, which is what a note being written (or any other app)
/// takes when it is dropped there.
enum DocDrag {
    static func provider(_ node: DocNode) -> NSItemProvider {
        let provider = NSItemProvider()
        provider.suggestedName = node.name
        provider.registerDataRepresentation(forTypeIdentifier: UTType.soloDocNode.identifier, visibility: .ownProcess) { done in
            done(Data(node.id.uuidString.utf8), nil)
            return nil
        }
        if !node.isFolder {
            provider.registerObject(link(to: node) as NSString, visibility: .all)
        }
        return provider
    }

    /// A link by address, as the picker writes one: it goes on working when the note is renamed or moved.
    static func link(to node: DocNode) -> String {
        let label = node.name.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "[", with: "\\[")
            .replacingOccurrences(of: "]", with: "\\]")
        return "[\(label)](\(NoteAddress.url(node.id)))"
    }

    /// Reads which node was dropped and hands it over on the main actor.
    static func dropped(_ providers: [NSItemProvider], then act: @escaping @MainActor (UUID) -> Void) -> Bool {
        guard let provider = providers.first(where: { $0.hasItemConformingToTypeIdentifier(UTType.soloDocNode.identifier) })
        else { return false }
        _ = provider.loadDataRepresentation(forTypeIdentifier: UTType.soloDocNode.identifier) { data, _ in
            guard let id = data.flatMap({ String(data: $0, encoding: .utf8) }).flatMap(UUID.init) else { return }
            Task { @MainActor in act(id) }
        }
        return true
    }
}

/// A design doc's folders and notes. Folders remember whether they were left
/// open. Anything can be picked up and carried: onto a folder to put it there,
/// onto the heading to bring it to the top, or — a note — into a note being
/// written, to link it. A long press also renames and deletes.
struct DocTreeView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var canEdit: Bool
    var selected: UUID?
    var onOpen: (UUID) -> Void

    @State private var prompt: TextPrompt?
    @State private var confirmation: Confirmation?
    /// True while something dragged is over the heading, which stands for the top of the design doc.
    @State private var overTop = false

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
            .background {
                if overTop {
                    RoundedRectangle(cornerRadius: 14, style: .continuous).fill(theme.accentWash).padding(.horizontal, 8).padding(.vertical, 4)
                }
            }
            .contentShape(.rect)
            .onDrop(of: canEdit ? [.soloDocNode] : [], isTargeted: $overTop) { providers in
                DocDrag.dropped(providers) { id in
                    guard let node = store.node(id), store.canMove(node, to: nil) else { return }
                    Task { await store.move(node, to: nil) }
                }
            }
            .animation(.easeOut(duration: 0.15), value: overTop)

            if store.tree?.notes.isEmpty == true, store.tree?.folders.isEmpty == true {
                Text("No notes yet.")
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .padding(.top, 24)
            } else {
                // Rows in a stack, not a list: a list keeps drops to itself, and its rows never hear of one.
                ScrollView {
                    LazyVStack(spacing: 2) {
                        DocBranch(store: store, parent: nil, canEdit: canEdit, selected: selected, onOpen: onOpen,
                                  prompt: $prompt, confirmation: $confirmation)
                    }
                    .padding(.horizontal, 8)
                    .padding(.bottom, 16)
                }
            }
        }
        .textPrompt($prompt)
        .confirmation($confirmation)
    }

    private func add(folder: Bool, in parent: UUID?) {
        prompt = DocBranch.addPrompt(store: store, folder: folder, parent: parent, onOpen: onOpen)
    }
}

/// Lets a row be picked up. A note can be carried by anyone who can read it — to link it
/// elsewhere; moving it is the drop's business, and checked there.
private struct DocNodeDrag: ViewModifier {
    var node: DocNode
    var enabled: Bool

    func body(content: Content) -> some View {
        if enabled || !node.isFolder {
            content.onDrag { DocDrag.provider(node) }
        } else {
            content
        }
    }
}

/// A folder's row: a tap opens or closes it, something carried can be dropped on it, and it
/// can be carried itself.
private struct DocFolderRow<Menu: View>: View {
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var node: DocNode
    var canEdit: Bool
    @ViewBuilder var menu: Menu
    @State private var targeted = false

    private var isOpen: Bool { store.openFolders.contains(node.id) }

    var body: some View {
        Button {
            withAnimation(.snappy(duration: 0.2)) {
                if isOpen { store.openFolders.remove(node.id) } else { store.openFolders.insert(node.id) }
            }
        } label: {
            HStack(spacing: 8) {
                Label(node.name, systemImage: targeted || isOpen ? "folder.fill" : "folder")
                    .lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: "chevron.right")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(theme.textSecondary)
                    .rotationEffect(.degrees(isOpen ? 90 : 0))
            }
            .padding(.horizontal, 10)
            .frame(minHeight: 40)
            .background(targeted ? theme.accentWash : .clear, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .contextMenu { menu }
        .modifier(DocNodeDrag(node: node, enabled: canEdit))
        .onDrop(of: canEdit ? [.soloDocNode] : [], isTargeted: $targeted) { providers in
            DocDrag.dropped(providers) { id in
                guard let dragged = store.node(id), store.canMove(dragged, to: node.id) else { return }
                Task { await store.move(dragged, to: node.id) }
            }
        }
        .animation(.easeOut(duration: 0.15), value: targeted)
        .accessibilityValue(isOpen ? "Open" : "Closed")
    }
}

private struct DocBranch: View {
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var parent: UUID?
    /// How many folders deep this branch is, which is how far its rows are set in.
    var depth = 0
    var canEdit: Bool
    var selected: UUID?
    var onOpen: (UUID) -> Void
    @Binding var prompt: TextPrompt?
    @Binding var confirmation: Confirmation?

    var body: some View {
        ForEach(store.children(of: parent)) { node in
            if node.isFolder {
                // A row of its own rather than a disclosure group's label: the group's label
                // doesn't take a drop, and a folder is where things are dropped.
                DocFolderRow(store: store, node: node, canEdit: canEdit) { menu(node) }
                    .padding(.leading, CGFloat(depth) * 18)
                if store.openFolders.contains(node.id) {
                    DocBranch(store: store, parent: node.id, depth: depth + 1, canEdit: canEdit, selected: selected,
                              onOpen: onOpen, prompt: $prompt, confirmation: $confirmation)
                }
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
                        .padding(.horizontal, 10)
                        .frame(minHeight: 40)
                        .background(node.id == selected ? theme.accentWash : .clear,
                                    in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .padding(.leading, CGFloat(depth) * 18)
                .contextMenu { menu(node) }
                .modifier(DocNodeDrag(node: node, enabled: canEdit))
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
