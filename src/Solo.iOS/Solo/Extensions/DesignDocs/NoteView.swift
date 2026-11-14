import PhotosUI
import SwiftUI

/// One note: read as rendered Markdown, or edited as its source — either way
/// live, with everyone else's changes arriving as they are made.
struct NoteView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var note: DocNote
    var mayWrite: Bool
    var author: String?
    var onOpenNote: (UUID) -> Void

    @State private var sync: DocSync?
    @State private var editing = false
    @State private var proxy = NoteEditorProxy()
    @State private var keyboardUp = false
    @State private var anchor: String?
    @State private var prompt: TextPrompt?
    @State private var pickingNote = false
    @State private var pickingTask = false
    @State private var showingAttachments = false
    @State private var pickingPhoto = false
    @State private var photo: PhotosPickerItem?
    @State private var uploading = false
    @State private var newSketch: SketchModel?

    /// The note's text as it stands: the shared document once it has arrived,
    /// the last saved copy until then.
    private var text: String {
        guard let sync else { return note.markdown }
        _ = sync.remoteRevision
        if let imported = sync.unseededMarkdown { return imported }
        let live = sync.shared.string()
        return live.isEmpty && !sync.synced ? note.markdown : live
    }

    private var canWrite: Bool { sync?.canWrite ?? false }

    var body: some View {
        let text = text
        Group {
            if editing, let sync {
                VStack(spacing: 0) {
                    statusBar(sync)
                    SyncedTextEditor(sync: sync, proxy: proxy, revision: sync.remoteRevision, editable: sync.canWrite,
                                     accent: UIColor(theme.primary), media: editorMedia, mediaRevision: mediaRevision) {
                        keyboardUp = $0
                    }
                }
                .safeAreaInset(edge: .bottom, spacing: 0) {
                    if sync.canWrite {
                        NoteFormatBar(proxy: proxy, onLinkNote: { pickingNote = true }, onLinkTask: { pickingTask = true },
                                      onSketch: newSketchPrompt, onImage: { pickingPhoto = true },
                                      onAttachments: { showingAttachments = true }, keyboardUp: keyboardUp)
                    }
                }
            } else {
                reading(text)
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                if !editing {
                    let headings = DocMarkdown.headings(text)
                    Menu("Note", systemImage: "list.bullet.indent") {
                        if !headings.isEmpty {
                            Section("Outline") {
                                ForEach(Array(headings.enumerated()), id: \.offset) { _, heading in
                                    Button(String(repeating: "   ", count: max(0, heading.level - 1)) + heading.text) {
                                        anchor = heading.slug
                                    }
                                }
                            }
                        }
                        Button("Attachments", systemImage: "paperclip") { showingAttachments = true }
                    }
                }
                if canWrite || editing {
                    Button(editing ? "Done" : "Edit", systemImage: editing ? "checkmark" : "square.and.pencil") {
                        setEditing(!editing)
                    }
                    .labelStyle(.titleOnly)
                    .fontWeight(editing ? .semibold : .regular)
                }
            }
        }
        .task {
            let fresh = DocSync(session: session, projectId: store.projectId, documentId: note.id, mayWrite: mayWrite)
            let api = session.api, projectId = store.projectId, vaultId = store.vaultId, noteId = note.id
            fresh.onSavedCopy = { [store] markdown, seq in
                try? await api.saveDocCopy(projectId, vaultId, noteId, markdown: markdown, seq: seq)
                // The instance tells everyone else the tree changed, not the one who changed it.
                await store.load()
            }
            sync = fresh
            await fresh.start()
        }
        .onDisappear {
            let leaving = sync
            Task { await leaving?.stop() }
        }
        .onChange(of: sync?.canWrite) { _, writable in
            if writable == false, editing, sync?.deleted == true { setEditing(false) }
        }
        .textPrompt($prompt)
        .sheet(isPresented: $pickingNote) {
            DocLinkPicker(projectId: store.projectId, preferredVault: store.vaultId) { proxy.insert($0) }
        }
        .sheet(isPresented: $pickingTask) {
            TaskPickerSheet(title: "Link a task", projectId: store.projectId, onPickTask: { task, board in
                let serial = TaskReference(projectAlias: board.projectAlias, boardAlias: board.alias, number: task.number).serial
                proxy.insert("[\(serial)](/docs/task/\(task.id.compact))")
            })
        }
        .sheet(isPresented: $showingAttachments) {
            DocAttachmentsSheet(store: store, note: note, canWrite: canWrite, liveText: text,
                                onInsert: editing && canWrite ? { proxy.insert("![[\($0)]]", ownLine: true) } : nil,
                                onDeleted: { targets in removeEmbeds(targets.map { "![[\($0)]]" }) })
        }
        .photosPicker(isPresented: $pickingPhoto, selection: $photo, matching: .images)
        .onChange(of: photo) { _, item in
            guard let item else { return }
            photo = nil
            Task { await upload(item) }
        }
        .fullScreenCover(item: $newSketch, onDismiss: { store.sketchChanged() }) { SketchEditor(model: $0) }
    }

    // MARK: Pictures

    /// What a row of this note's pictures needs: the vault, and the ways it changes the note.
    private var mediaContext: NoteMediaContext {
        NoteMediaContext(store: store, note: note, canWrite: canWrite, removeEmbeds: removeEmbeds, openNote: onOpenNote)
    }

    /// Everything that changes what the editor's rows show.
    private var mediaRevision: Int {
        var hasher = Hasher()
        hasher.combine(store.images.map(\.id))
        hasher.combine(store.sketches.map(\.id))
        hasher.combine(canWrite)
        hasher.combine(theme.primary)
        return hasher.finalize()
    }

    /// The editor is UIKit, so its rows are handed to it ready-made, with the
    /// environment a view of this app expects.
    private var editorMedia: NoteEditorMedia {
        let context = mediaContext, session = session, theme = theme
        return NoteEditorMedia(
            size: { row in
                guard let refs = DocMarkdown.mediaRow(row), let entries = context.entries(refs) else { return nil }
                return NoteMediaStack.size(count: entries.count)
            },
            row: { row in
                guard let refs = DocMarkdown.mediaRow(row), let entries = context.entries(refs) else { return nil }
                return AnyView(NoteMediaStack(entries: entries, context: context)
                    .environment(session)
                    .environment(session.navigator)
                    .environment(session.toasts)
                    .environment(\.theme, theme)
                    .tint(theme.primary))
            })
    }

    // MARK: Reading

    private func reading(_ text: String) -> some View {
        ScrollViewReader { scroller in
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    header
                    if text.nilIfBlank == nil {
                        empty
                    } else {
                        NoteReader(text: text, note: note, media: mediaContext, mayWrite: mayWrite, anchor: $anchor,
                                   onOpenNote: onOpenNote)
                    }
                }
                .frame(maxWidth: 760, alignment: .leading)
                .padding(.horizontal, 20)
                .padding(.top, 12)
                .padding(.bottom, 40)
                .frame(maxWidth: .infinity)
            }
            .onChange(of: anchor) { _, slug in
                guard let slug else { return }
                withAnimation(.snappy) { scroller.scrollTo(slug, anchor: .top) }
                anchor = nil
            }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(note.name)
                .font(.largeTitle.weight(.bold))
                .foregroundStyle(theme.textPrimary)
            HStack(spacing: 8) {
                if let sync {
                    Label(sync.statusText, systemImage: statusSymbol(sync))
                        .labelStyle(.titleAndIcon)
                }
                if let edited = note.lastEditedAt {
                    Text("·")
                    Text("Edited \(edited.relative)\(author.map { " by \($0)" } ?? "")")
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
                if let sync { NotePresence(entries: sync.present) }
            }
            .font(.caption)
            .foregroundStyle(theme.textSecondary)
        }
        .id("solo-doc-header")
    }

    private var empty: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(sync?.synced == true ? "This note is empty." : "Loading…")
                .foregroundStyle(theme.textSecondary)
            if canWrite {
                Button("Start writing", systemImage: "square.and.pencil") { setEditing(true) }
                    .buttonStyle(.glass)
            }
        }
    }

    private func statusSymbol(_ sync: DocSync) -> String {
        if sync.deleted { return "trash" }
        if !sync.synced { return "arrow.triangle.2.circlepath" }
        if sync.hasPending { return "arrow.up.circle" }
        return sync.canWrite ? "checkmark.circle" : "eye"
    }

    // MARK: Editing

    private func statusBar(_ sync: DocSync) -> some View {
        HStack(spacing: 8) {
            Label(sync.statusText, systemImage: statusSymbol(sync))
            if uploading {
                ProgressView().controlSize(.mini)
            }
            Spacer(minLength: 0)
            NotePresence(entries: sync.present)
        }
        .font(.caption)
        .foregroundStyle(theme.textSecondary)
        .padding(.horizontal, 18)
        .padding(.vertical, 6)
    }

    private func setEditing(_ value: Bool) {
        if value {
            editing = true
            // The text view exists a moment after the state changes.
            Task {
                try? await Task.sleep(for: .milliseconds(150))
                proxy.focus()
            }
        } else {
            proxy.dismissKeyboard()
            editing = false
        }
    }

    private func newSketchPrompt() {
        prompt = TextPrompt(title: "New sketch", placeholder: "Name") { name in
            guard let sketch = await session.save({
                try await session.api.createDocSketch(store.projectId, store.vaultId, noteId: note.id, name: name)
            }) else { return }
            proxy.insert("![[\(sketch.path)]]", ownLine: true)
            await store.load()
            newSketch = SketchModel(session: session, projectId: store.projectId, vaultId: store.vaultId, sketch: sketch,
                                    mayWrite: mayWrite)
        }
    }

    /// The instance takes PNG, JPEG and WebP up to 2,560 pixels a side; a photo
    /// from the library is usually larger and often HEIC, so it is redrawn.
    private func upload(_ item: PhotosPickerItem) async {
        uploading = true
        defer { uploading = false }
        guard let data = try? await item.loadTransferable(type: Data.self), let encoded = DocImageEncoder.jpeg(from: data) else {
            session.toasts.show("That image couldn't be read.", .warning)
            return
        }
        guard let stored = await session.save({
            try await session.api.uploadDocImage(store.projectId, store.vaultId, noteId: note.id, path: "Photo.jpg",
                                                 data: encoded, contentType: "image/jpeg")
        }) else { return }
        await store.load()
        proxy.insert("![[\(stored.path)]]", ownLine: true)
    }

    /// Takes embeds out of the note, each given as the exact text the note wrote for it.
    private func removeEmbeds(_ sources: [String]) {
        guard let sync, sync.canWrite, !sources.isEmpty else { return }
        var changed = false
        for source in Set(sources) where !source.isEmpty {
            while true {
                let current = sync.shared.string()
                guard let range = current.range(of: source) else { break }
                let start = current.utf16.distance(from: current.utf16.startIndex, to: range.lowerBound)
                sync.shared.replaceText(start: start, length: source.utf16.count, with: "")
                changed = true
            }
        }
        if changed { sync.changedOutsideEditor() }
    }
}

/// Who else has the note open; a ring marks someone typing in it.
private struct NotePresence: View {
    @Environment(\.theme) private var theme
    var entries: [TopicPresenceEntry]

    var body: some View {
        if !entries.isEmpty {
            HStack(spacing: -6) {
                ForEach(entries.prefix(4), id: \.userId) { entry in
                    UserAvatar(userId: entry.userId, name: entry.displayName, hasPicture: true, size: 22)
                        .overlay(Circle().strokeBorder(entry.mode == .editing ? theme.warning : .clear, lineWidth: 2))
                }
            }
            .accessibilityElement()
            .accessibilityLabel("\(entries.count) other \(entries.count == 1 ? "person" : "people") here: "
                + entries.map { ($0.displayName ?? "Someone") + ($0.mode == .editing ? ", editing" : "") }.joined(separator: "; "))
        }
    }
}

// MARK: - Linking a note

/// Picks a note — and optionally one of its headings — and hands back a link
/// to it by address, which keeps working after the note is renamed or moved.
struct DocLinkPicker: View {
    @Environment(Session.self) private var session
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    var projectId: UUID
    var preferredVault: UUID?
    var onPick: (String) -> Void

    @State private var vaults: [DocVault]?
    @State private var vaultId: UUID?
    @State private var notes: [DocNote] = []
    @State private var query = ""

    private var shown: [DocNote] {
        let wanted = query.trimmingCharacters(in: .whitespaces)
        return notes.filter { wanted.isEmpty || $0.name.localizedCaseInsensitiveContains(wanted) }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    var body: some View {
        NavigationStack {
            List {
                if let vaults {
                    if vaults.isEmpty {
                        Text("This project has no open design docs.").foregroundStyle(theme.textSecondary)
                    } else {
                        if vaults.count > 1 {
                            Picker("Design doc", selection: $vaultId) {
                                ForEach(vaults) { Text($0.name).tag(UUID?.some($0.id)) }
                            }
                        }
                        Section {
                            ForEach(shown) { note in
                                HStack {
                                    Button {
                                        choose(note, nil)
                                    } label: {
                                        Label(note.name, systemImage: "doc.text")
                                            .foregroundStyle(theme.textPrimary)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                            .contentShape(.rect)
                                    }
                                    .buttonStyle(.plain)
                                    if !note.headings.isEmpty {
                                        Menu("Link to a heading in \(note.name)", systemImage: "number") {
                                            ForEach(note.headings, id: \.slug) { heading in
                                                Button(heading.text) { choose(note, heading) }
                                            }
                                        }
                                        .labelStyle(.iconOnly)
                                    }
                                }
                            }
                            if shown.isEmpty {
                                Text(notes.isEmpty ? "No notes here yet." : "No notes match.")
                                    .foregroundStyle(theme.textSecondary)
                            }
                        } footer: {
                            Text("Tap a note to link to it, or # to link to one of its headings.")
                        }
                        .themedRow()
                    }
                } else {
                    ProgressView()
                }
            }
            .themedList()
            .searchable(text: $query, prompt: "Find a note")
            .navigationTitle("Link a doc")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
            }
        }
        .task {
            let open = ((try? await session.api.docVaults(projectId))?.vaults ?? []).filter { !$0.isClosed }
            vaults = open
            vaultId = open.first { $0.id == preferredVault }?.id ?? open.first?.id
        }
        .task(id: vaultId) {
            guard let vaultId else { return }
            notes = (try? await session.api.docTree(projectId, vaultId))?.notes ?? []
        }
    }

    private func choose(_ note: DocNote, _ heading: DocHeading?) {
        let label = (heading.map { "\(note.name) › \($0.text)" } ?? note.name)
            .replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "[", with: "\\[")
            .replacingOccurrences(of: "]", with: "\\]")
        onPick("[\(label)](\(NoteAddress.url(note.id, heading: heading?.slug)))")
        dismiss()
    }
}

// MARK: - Attachments

/// One image or sketch in the vault, with where it is used.
private struct AttachmentItem: Identifiable {
    var id: UUID
    var image: DocImage?
    var sketch: DocSketch?
    var path: String
    var ownerId: UUID?
    var ownerName: String?
    var embeddedIn: Set<UUID> = []
    /// The raw embed targets in the open note that mean this attachment.
    var targets: [String] = []

    var name: String { path.split(separator: "/").last.map(String.init) ?? path }
}

/// Every image and sketch in the design doc: what this note uses, what the
/// rest of the vault holds, and what nothing embeds any more.
struct DocAttachmentsSheet: View {
    @Environment(Session.self) private var session
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    var store: DocVaultStore
    var note: DocNote
    var canWrite: Bool
    /// The open note's text, which is ahead of its saved copy.
    var liveText: String
    var onInsert: ((String) -> Void)?
    var onDeleted: ([String]) -> Void

    @State private var items: [AttachmentItem]?
    @State private var confirmation: Confirmation?
    @State private var viewing: DocImage?
    @State private var drawing: SketchModel?

    var body: some View {
        NavigationStack {
            List {
                if let items {
                    let here = items.filter { $0.ownerId == note.id || $0.embeddedIn.contains(note.id) }
                    let elsewhere = items.filter { !($0.ownerId == note.id || $0.embeddedIn.contains(note.id)) }
                    if items.isEmpty {
                        Text("This design doc has no images or sketches yet.").foregroundStyle(theme.textSecondary)
                    }
                    if !here.isEmpty {
                        Section("In this note") { ForEach(here) { row($0) } }.themedRow()
                    }
                    if !elsewhere.isEmpty {
                        Section("Elsewhere in this design doc") { ForEach(elsewhere) { row($0) } }.themedRow()
                    }
                } else {
                    ProgressView()
                }
            }
            .themedList()
            .navigationTitle("Attachments")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .confirmation($confirmation)
            .fullScreenCover(item: $viewing) { image in
                DocPictureLoader(image: image, store: store)
            }
            .fullScreenCover(item: $drawing) { SketchEditor(model: $0) }
        }
        .task { await load() }
    }

    private func row(_ item: AttachmentItem) -> some View {
        Button {
            if let image = item.image {
                viewing = image
            } else if let sketch = item.sketch {
                drawing = SketchModel(session: session, projectId: store.projectId, vaultId: store.vaultId, sketch: sketch,
                                      mayWrite: canWrite)
            }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: item.sketch == nil ? "photo" : "scribble.variable")
                    .font(.title3)
                    .foregroundStyle(theme.primary)
                    .frame(width: 30)
                VStack(alignment: .leading, spacing: 2) {
                    Text(item.name).foregroundStyle(theme.textPrimary).lineLimit(1)
                    Text(detail(item)).font(.caption).foregroundStyle(theme.textSecondary).lineLimit(1)
                }
                Spacer(minLength: 0)
                if item.embeddedIn.isEmpty {
                    Text("Not embedded")
                        .font(.caption2.weight(.medium))
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .background(theme.surface, in: .capsule)
                        .foregroundStyle(theme.textSecondary)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .swipeActions(edge: .trailing) {
            if canWrite {
                Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(item) }
            }
        }
        .swipeActions(edge: .leading) {
            if let onInsert {
                Button("Insert", systemImage: "text.insert") {
                    onInsert(item.path)
                    dismiss()
                }
                .tint(theme.primary)
            }
        }
        .contextMenu {
            if let onInsert {
                Button("Insert into note", systemImage: "text.insert") {
                    onInsert(item.path)
                    dismiss()
                }
            }
            if canWrite {
                Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(item) }
            }
        }
    }

    private func detail(_ item: AttachmentItem) -> String {
        var parts: [String] = []
        if let image = item.image, image.width > 0 { parts.append("\(image.width) × \(image.height)") }
        if item.sketch != nil { parts.append("Sketch") }
        if let owner = item.ownerName, item.ownerId != note.id { parts.append("from \(owner)") }
        return parts.joined(separator: " · ")
    }

    private func load() async {
        await store.load()
        let list = try? await session.api.docAttachments(store.projectId, store.vaultId)
        var embeds = list?.embeds ?? [:]
        embeds[note.id] = DocMarkdown.embedTargets(liveText)

        var found: [UUID: AttachmentItem] = [:]
        for image in store.images {
            let owner = DocMarkdown.owner(ofPath: image.path).flatMap { store.note($0) }
            found[image.id] = AttachmentItem(id: image.id, image: image, path: image.path, ownerId: owner?.id, ownerName: owner?.name)
        }
        for sketch in store.sketches {
            found[sketch.id] = AttachmentItem(id: sketch.id, sketch: sketch, path: sketch.path, ownerId: sketch.noteId,
                                              ownerName: store.note(sketch.noteId)?.name)
        }
        for (noteId, raws) in embeds {
            for raw in raws {
                let target = DocMarkdown.target(raw)
                let hit = target.lowercased().hasSuffix(".sketch")
                    ? store.resolveSketch(target, from: noteId)?.id
                    : store.resolveImage(target, from: store.note(noteId))?.id
                guard let hit else { continue }
                found[hit]?.embeddedIn.insert(noteId)
                if noteId == note.id { found[hit]?.targets.append(raw) }
            }
        }
        items = found.values.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private func confirmDelete(_ item: AttachmentItem) {
        let others = item.embeddedIn.filter { $0 != note.id }.count
        var consequence = "It isn't embedded in any note."
        if !item.embeddedIn.isEmpty {
            consequence = (item.embeddedIn.contains(note.id) ? "Its embeds in this note are removed. " : "")
                + (others > 0 ? "\(others) other \(others == 1 ? "note shows" : "notes show") it as unavailable." : "")
        }
        confirmation = Confirmation(
            title: item.sketch == nil ? "Delete image" : "Delete sketch",
            message: "Delete \"\(item.name)\" permanently? It is deleted from the design doc and cannot be recovered. \(consequence)",
            action: "Delete permanently") {
            let done = await session.perform {
                if let image = item.image {
                    try await session.api.deleteDocImage(store.projectId, store.vaultId, image)
                } else if let sketch = item.sketch {
                    try await session.api.deleteDocSketch(store.projectId, store.vaultId, sketch)
                }
            }
            if done { onDeleted(item.targets) }
            await load()
        }
    }
}

/// Loads an image from the vault for the full-screen viewer.
private struct DocPictureLoader: View {
    @Environment(\.dismiss) private var dismiss
    var image: DocImage
    var store: DocVaultStore

    @State private var picture: UIImage?
    @State private var failed = false

    var body: some View {
        Group {
            if let picture {
                DocPictureViewer(picture: picture, name: image.path.split(separator: "/").last.map(String.init) ?? "Image")
            } else {
                ZStack {
                    Color.black.ignoresSafeArea()
                    if failed {
                        VStack(spacing: 12) {
                            Text("That image couldn't load.").foregroundStyle(.white)
                            Button("Close") { dismiss() }.buttonStyle(.glass)
                        }
                    } else {
                        ProgressView().tint(.white)
                    }
                }
            }
        }
        .task {
            picture = await store.picture(image)
            failed = picture == nil
        }
    }
}
