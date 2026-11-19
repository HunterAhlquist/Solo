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
    /// The changes to a downloaded design doc that the instance refused, being gone through.
    @State private var reviewing = false
    /// Its recovery archive, asked for from the bar.
    @State private var exporting = false
    /// The open note's attachments, which the note shows (`NoteView`) and, on a phone, this
    /// page's menu asks for.
    @State private var showingAttachments = false

    private var wide: Bool { sizeClass == .regular }
    /// Whether the tree goes over the note and not beside it (`panesFloat`).
    @Environment(\.panesFloat) private var floats

    private func canEdit(_ project: ProjectSummary, _ store: DocVaultStore) -> Bool {
        (project.isOwner || project.role == .readWrite) && store.vault?.isClosed == false
    }

    /// Creating, renaming, closing and deleting a design doc is the project's owners' and a super admin's.
    private var canManage: Bool { session.manages(project) }

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
        .sheet(isPresented: $reviewing) {
            if let vault = store?.vault { OfflineProblemsSheet(vault: vault) }
        }
        .exportsRecovery(of: store?.vault?.id, when: $exporting)
    }

    @ViewBuilder
    private func content(_ store: DocVaultStore, _ project: ProjectSummary) -> some View {
        let editable = canEdit(project, store)
        let note = currentNote(store)
        Group {
            if wide {
                // The tree is on the trailing side, under the button in the bar's trailing
                // corner that puts it away. The leading side is the app's own sidebar's, and
                // its button's: with the tree there too the two stood side by side, each with
                // a button of the same shape, and the tree's button was at the far end of the
                // bar from the tree.
                if floats {
                    // Where there is not room for the note and the tree (`panesFloat`), the
                    // tree goes over the note, which keeps its width and passes behind it.
                    page(store, note, editable: editable)
                        .overlay(alignment: .trailing) {
                            if treeShown { treePanel(store, editable: editable, selected: note?.id) }
                        }
                } else {
                    HStack(spacing: 0) {
                        page(store, note, editable: editable)
                            // The note takes its new width at once while the tree slides. Animated, its
                            // text is laid out again at every width on the way, and the page jitters
                            // for as long as that takes.
                            .animation(nil, value: treeShown)
                        if treeShown { treePanel(store, editable: editable, selected: note?.id) }
                    }
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
        // Over the note, the tree is put away to begin with, and again once a note has been
        // picked from it (`open`): left out, it would stand over what was asked for.
        .onChange(of: floats, initial: true) { _, floats in
            if floats { treeShown = false }
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
                     onOpenNote: open, showingAttachments: $showingAttachments)
                .id(note.id)
        } else if store.tree?.notes.isEmpty == true {
            ContentUnavailableView {
                Label("No notes yet", systemImage: "doc.text")
            } description: {
                Text(editable ? "Add the first note to start this design doc." : "Nothing has been written here yet.")
            } actions: {
                if editable {
                    Button("New Note", systemImage: "plus") { addNote(store) }.buttonStyle(.clearGlass)
                }
            }
        } else {
            ContentUnavailableView("That note isn't in this design doc", systemImage: "doc.questionmark",
                                   description: Text("It may have been deleted or moved to another one."))
        }
    }

    /// The tree as a panel of glass down the trailing side, beside the note or over it.
    private func treePanel(_ store: DocVaultStore, editable: Bool, selected: UUID?) -> some View {
        tree(store, editable: editable, selected: selected)
            .frame(width: 300)
            // The panel's shape cuts what scrolls inside it too: without this a row's
            // highlight shows past the rounded corners as it scrolls out.
            .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
            .glassEffect(.regular, in: RoundedRectangle(cornerRadius: 24, style: .continuous))
            .padding(.trailing, 14)
            .padding(.vertical, 10)
            .transition(.move(edge: .trailing).combined(with: .opacity))
    }

    private func tree(_ store: DocVaultStore, editable: Bool, selected: UUID?, onOpen: ((UUID) -> Void)? = nil) -> some View {
        DocTreeView(store: store, canEdit: editable, selected: selected, onOpen: onOpen ?? open)
    }

    @ToolbarContentBuilder
    private var toolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .secondaryAction) {
            if let store, let project {
                // What is the note's comes first, where a phone has it in this menu: on anything
                // wider it is in the note's own (`NoteView`).
                if !wide, noteId != nil, currentNote(store) != nil {
                    Button("Attachments", systemImage: "paperclip") { showingAttachments = true }
                }
                if canEdit(project, store) {
                    Button("New Note", systemImage: "doc.badge.plus") { addNote(store) }
                }
                if canManage, let vault = store.vault {
                    Button("Rename Design Doc", systemImage: "pencil") { rename(store, vault) }
                    if vault.isClosed {
                        Button("Reopen Design Doc", systemImage: "arrow.uturn.backward") {
                            Task { await store.changeVault(name: vault.name, isClosed: false) }
                        }
                    }
                }
            }
        }
        // The one button that takes a design doc away stands apart from the ones that add to
        // it or rename it, in the colour of something that takes away. It closes the design
        // doc, and on one that is closed it is what deletes it: the two steps the instance
        // asks for, in the one place. A gap, or a bar draws the two groups as one.
        ToolbarSpacer(.fixed, placement: .secondaryAction)
        ToolbarItem(placement: .secondaryAction) {
            if let store, canManage, let vault = store.vault {
                if vault.isClosed {
                    Button(role: .destructive) {
                        confirmDelete(store)
                    } label: {
                        // The colour is said on the label: a bar's button takes no tint.
                        Label("Delete Design Doc", systemImage: "trash").foregroundStyle(theme.error)
                    }
                } else {
                    // The role is for where the bar folds its buttons into a menu, as an
                    // iPad's does: a menu's row takes no colour from its label, only from this.
                    Button(role: .destructive) {
                        Task { await store.changeVault(name: vault.name, isClosed: true) }
                    } label: {
                        Label("Close Design Doc", systemImage: "archivebox").foregroundStyle(theme.error)
                    }
                }
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            // On a phone it is on the page that lists the notes, and not again on each of them.
            if let store, let project, let vault = store.vault, wide || noteId == nil {
                DocDownloadControl(projectId: project.id, projectAlias: project.alias, vault: vault,
                                   review: { reviewing = true }, export: { exporting = true }, confirm: { confirmation = $0 })
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            if store?.tree != nil {
                if wide {
                    // A sidebar's symbol, as the tree is one, but the trailing one with squares
                    // in it: the plain leading one is the app's own sidebar's.
                    Button("Notes", systemImage: "sidebar.squares.trailing") {
                        withAnimation(.snappy) { treeShown.toggle() }
                    }
                    .accessibilityHint(treeShown ? "Hides the note tree" : "Shows the note tree")
                } else if noteId != nil, !underNotes {
                    // Only where Back is not the way to them: a note come to by a link from
                    // somewhere else, with that somewhere under it.
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
            if floats { withAnimation(.snappy) { treeShown = false } }
        } else {
            navigator.push(.designDocs(project: projectAlias, vaultId: vaultId, noteId: id))
        }
    }

    /// Whether the page under this note is this design doc's — its list of notes, or a note
    /// that was come from. Then Back leads to the notes, and nothing else needs to.
    private var underNotes: Bool {
        let path = navigator[navigator.tab]
        guard path.count >= 2, case .designDocs(_, let under, _) = path[path.count - 2] else { return false }
        return under == vaultId
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
        guard let vault = store.vault else { return }
        confirmation = Confirmation(
            title: "Delete \(vault.name)?",
            message: "Every note, image and sketch in this closed design doc is deleted permanently, for everyone. This cannot be undone.",
            action: "Delete Design Doc") {
            if await store.deleteVault() {
                navigator.open(.project(alias: projectAlias))
            }
        }
    }
}

// MARK: - Downloading

/// A design doc's place on this device: a button that downloads it, and once it is here what
/// says how it stands with the instance — in step, owing, out of reach, or refused — with
/// the things to do about that.
private struct DocDownloadControl: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var projectId: UUID
    var projectAlias: String
    var vault: DocVault
    // What these open is the page's to present, with the rest of what it presents: a bar's
    // item comes and goes with the bar, and takes what it is presenting with it.
    var review: () -> Void
    var export: () -> Void
    var confirm: (Confirmation) -> Void

    var body: some View {
        let offline = session.docsOffline
        Group {
            if offline.downloading.contains(vault.id) {
                ProgressView()
                    .controlSize(.small)
                    .accessibilityLabel("Downloading this design doc")
            } else if offline.isDownloaded(vault.id) {
                let pending = offline.pending(in: vault.id)
                let problems = offline.problems(in: vault.id).count
                Menu {
                    Section(summary(pending: pending, problems: problems, reachable: offline.reachable)) {
                        Button("Sync Now", systemImage: "arrow.triangle.2.circlepath") { offline.sync() }
                        if problems > 0 {
                            Button("Review Changes…", systemImage: "exclamationmark.triangle", action: review)
                        }
                    }
                    Button("Export Recovery Zip…", systemImage: "archivebox", action: export)
                    Button("Remove Download", systemImage: "trash", role: .destructive) {
                        confirm(Confirmation(
                            title: "Remove download?",
                            message: "\(vault.name) is taken off this device. The instance keeps its copy.",
                            action: "Remove") {
                            if let refusal = offline.remove(vault.id) { session.toasts.show(refusal, .warning) }
                        })
                    }
                } label: {
                    Label("Downloaded", systemImage: symbol(pending: pending, problems: problems, reachable: offline.reachable))
                        .foregroundStyle(problems > 0 ? theme.warning : theme.primary)
                }
                .help("Downloaded to this device")
            } else {
                Button("Download for Offline", systemImage: "icloud.and.arrow.down") {
                    Task {
                        if let problem = await offline.download(projectId: projectId, projectAlias: projectAlias, vault: vault) {
                            session.toasts.show(problem, .error)
                        } else {
                            session.toasts.show("\(vault.name) is on this device. Changes made offline are sent when the instance is back in reach.", .success)
                        }
                    }
                }
                .help("Download this design doc to read and write it offline")
            }
        }
    }

    private func summary(pending: Int, problems: Int, reachable: Bool) -> String {
        if problems > 0 { return "\(problems) \(problems == 1 ? "change needs" : "changes need") attention. What was written is kept." }
        if pending > 0 { return "\(reachable ? "Sending" : "Offline") · \(pending) \(pending == 1 ? "document" : "documents") changed on this device" }
        return reachable ? "On this device · in step with the instance" : "Offline · on this device"
    }

    /// Out of reach it does not say so again: the bar's own indicator is beside it. It says
    /// what is true of the design doc — here, and with or without something to send.
    private func symbol(pending: Int, problems: Int, reachable: Bool) -> String {
        if problems > 0 { return "exclamationmark.icloud" }
        if pending > 0 { return reachable ? "arrow.triangle.2.circlepath.icloud" : "icloud.and.arrow.up" }
        return "checkmark.icloud"
    }
}

/// The changes the instance refused, each to be sent again or given up.
private struct OfflineProblemsSheet: View {
    @Environment(Session.self) private var session
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    var vault: DocVault
    @State private var confirmation: Confirmation?
    @State private var exporting = false

    var body: some View {
        let offline = session.docsOffline
        let problems = offline.problems(in: vault.id)
        NavigationStack {
            GroupedList {
                if problems.isEmpty {
                    Text("Nothing needs attention.").foregroundStyle(theme.textSecondary).themedRow()
                }
                ForEach(problems) { problem in
                    Section {
                        Text(problem.message)
                            .font(.footnote)
                            .foregroundStyle(theme.textSecondary)
                        Button("Send Again", systemImage: "arrow.up.circle") {
                            Task {
                                do { try await offline.retry(problem) } catch { session.toasts.error(error, fallback: "That couldn't be sent again.") }
                            }
                        }
                        Button(problem.isCommand ? "Discard This Change" : "Discard What I Wrote", systemImage: "trash",
                               role: .destructive) {
                            confirmation = Confirmation(
                                title: problem.isCommand ? "Discard “\(problem.name)”?" : "Discard changes to \(problem.name)?",
                                message: problem.isCommand
                                    ? "This change is given up, with anything made on this device that depended on it, and the design doc is shown as the instance has it."
                                    : "What was written on this device and never sent is lost, and the document is shown as the instance has it.",
                                action: "Discard") {
                                await offline.discard(problem)
                            }
                        }
                    } header: {
                        Text(problem.name)
                    }
                    .themedRow()
                }
                // Last, as the web has it: everything on the device, to take before giving any of it up.
                Section {
                    Button("Export Recovery Zip…", systemImage: "archivebox") { exporting = true }
                } footer: {
                    Text("These changes are still on this device. Export them before discarding work you need.")
                }
                .themedRow()
            }
            .themedList()
            .navigationTitle("Changes Not Sent")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .confirmation($confirmation)
            .exportsRecovery(of: vault.id, when: $exporting)
        }
    }
}

/// A recovery archive, as something the system can be asked to save.
struct RecoveryArchive: FileDocument {
    static let readableContentTypes: [UTType] = [.zip]
    var name: String
    var data: Data

    init(name: String, data: Data) {
        self.name = name
        self.data = data
    }

    init(configuration: ReadConfiguration) throws {
        name = configuration.file.filename ?? "recovery.zip"
        data = configuration.file.regularFileContents ?? Data()
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

/// Makes the archive of a downloaded design doc when asked, then asks where to put it. It is
/// on the page or the sheet that was asked: a sheet can't have the page under it present
/// anything.
private struct RecoveryExport: ViewModifier {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var vaultId: UUID?
    @Binding var requested: Bool
    @State private var archive: RecoveryArchive?
    @State private var saving = false

    func body(content: Content) -> some View {
        content
            .onChange(of: requested) { _, asked in
                guard asked else { return }
                requested = false
                guard let vaultId else { return }
                Task {
                    do {
                        let made = try await session.docsOffline.recoveryArchive(vaultId, dark: theme.isDark)
                        archive = RecoveryArchive(name: made.name, data: made.data)
                        saving = true
                    } catch {
                        session.toasts.error(error, fallback: "The archive couldn't be made.")
                    }
                }
            }
            .fileExporter(isPresented: $saving, document: archive, contentType: .zip,
                          defaultFilename: archive?.name ?? "recovery.zip") { result in
                archive = nil
                if case .failure(let error) = result {
                    session.toasts.error(error, fallback: "The archive couldn't be saved.")
                }
            }
    }
}

private extension View {
    func exportsRecovery(of vault: UUID?, when requested: Binding<Bool>) -> some View {
        modifier(RecoveryExport(vaultId: vault, requested: requested))
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

    @ViewBuilder
    private func headingButton(_ title: String, _ symbol: String, action: @escaping () -> Void) -> some View {
        #if os(macOS)
        Button(title, systemImage: symbol, action: action)
        #else
        Button(action: action) {
            Label(title, systemImage: symbol)
                .font(.title3)
                .frame(width: 44, height: 44)
                .contentShape(.rect)
        }
        #endif
    }

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
                    // Under a finger each is a finger's size, with an icon to match: at the
                    // size of the text beside them they were two marks a few points apart.
                    HStack(spacing: Platform.isMac ? 4 : 0) {
                        headingButton("New note", "doc.badge.plus") { add(folder: false, in: nil) }
                        headingButton("New folder", "folder.badge.plus") { add(folder: true, in: nil) }
                    }
                }
            }
            .labelStyle(.iconOnly)
            .buttonStyle(.borderless)
            .padding(.leading, 18)
            .padding(.trailing, Platform.isMac ? 10 : 8)
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
