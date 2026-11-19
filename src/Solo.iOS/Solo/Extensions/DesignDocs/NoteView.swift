import PhotosUI
import SwiftUI

/// One note, live: it reads as what its Markdown means, and a tap in the text
/// starts typing there — there is no separate mode for editing. Everyone
/// else's changes arrive as they are made.
struct NoteView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.openURL) private var openURL
    @Environment(\.horizontalSizeClass) private var sizeClass
    var store: DocVaultStore
    var note: DocNote
    var mayWrite: Bool
    var author: String?
    var onOpenNote: (UUID) -> Void
    /// How far in from the page's trailing edge the design doc's tree would come if it were
    /// out, where it is not out beside the note already: where the note's ground fades to.
    var trailingPane: CGFloat = 0
    /// How wide the app's sidebar is when it is out, as last seen; a usual width until then.
    @State private var sidebarWidth: CGFloat = 300

    @State private var sync: DocSync?
    @State private var proxy = NoteEditorProxy()
    /// True while the note is being typed in: from the tap that puts the caret down until the keyboard goes away.
    @State private var typing = false
    @State private var prompt: TextPrompt?
    @State private var pickingNote = false
    @State private var pickingTask = false
    /// The page's to say, since on a phone what asks for them is in the page's menu.
    @Binding var showingAttachments: Bool
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
        GeometryReader { geometry in
            if let sync {
                // Edge to edge, under the bars: the note is one sheet that passes behind their glass.
                SyncedTextEditor(sync: sync, proxy: proxy, revision: sync.remoteRevision, editable: sync.canWrite, fallback: note.markdown,
                                 accent: PlatformColor(theme.primary), media: editorMedia, mediaRevision: mediaRevision,
                                 presence: sync.awareness.revision, header: AnyView(header.environment(\.theme, theme).environment(session)), insets: geometry.safeAreaInsets,
                                 onLink: follow) {
                    typing = $0
                }
                .ignoresSafeArea()
                .background {
                    NoteGround(leading: geometry.safeAreaInsets.leading, trailing: geometry.safeAreaInsets.trailing,
                               sidebar: sidebarWidth, pane: trailingPane, fades: sizeClass == .regular)
                        .ignoresSafeArea()
                }
                .onChange(of: geometry.safeAreaInsets.leading, initial: true) { _, inset in
                    if inset > 0 { sidebarWidth = inset }
                }
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            // The tools for writing are there while writing, and out of the way of reading.
            if typing, canWrite {
                NoteFormatBar(proxy: proxy, onLinkNote: { pickingNote = true }, onLinkTask: { pickingTask = true },
                              onSketch: newSketchPrompt, onImage: { pickingPhoto = true },
                              onAttachments: { showingAttachments = true })
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(.snappy(duration: 0.25), value: typing)
        .toolbar {
            // A phone's bar has room for little, and a menu of everything else at its end: there
            // the note's attachments are in that menu (`DesignDocsView.toolbar`), and this one
            // is the outline and nothing more — and is not there for a note with no headings
            // to go to.
            let narrow = sizeClass == .compact
            ToolbarItemGroup(placement: .primaryAction) {
                let outline = DocMarkdown.outline(text)
                if !narrow || !outline.isEmpty {
                    Menu(narrow ? "Outline" : "Note", systemImage: "list.bullet.indent") {
                        if !outline.isEmpty {
                            Section("Outline") {
                                ForEach(Array(outline.enumerated()), id: \.offset) { _, entry in
                                    Button(String(repeating: "   ", count: max(0, entry.heading.level - 1)) + entry.heading.text) {
                                        proxy.scroll(toLine: entry.line)
                                    }
                                }
                            }
                        }
                        if !narrow {
                            Button("Attachments", systemImage: "paperclip") { showingAttachments = true }
                        }
                    }
                }
                if typing {
                    // Also the way out with a hardware keyboard, which has nothing to dismiss.
                    Button("Done") { proxy.dismissKeyboard() }
                        .fontWeight(.semibold)
                }
            }
        }
        .task {
            let fresh = DocSync(session: session, projectId: store.projectId, documentId: note.id, mayWrite: mayWrite,
                                vaultId: store.vaultId)
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
            if writable == false, typing { proxy.dismissKeyboard() }
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
                                onInsert: canWrite ? { proxy.insert("![[\($0)]]", ownLine: true) } : nil,
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

    // MARK: Header

    /// When the note was last edited, and by whom, where it has been.
    private var edited: String? {
        note.lastEditedAt.map { "Edited \($0.relative)\(author.map { " by \($0)" } ?? "")" }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(note.name)
                .font(.title.weight(.bold))
                .foregroundStyle(theme.textPrimary)
                .lineLimit(2)
            // How the note stands changes as it is typed in — saved, sending, saved — and a
            // status that wrapped on a narrow screen moved the whole note down a line and
            // back under the hand writing it. So each thing said here is a line of its own
            // and there are always two of them, each behind an icon: how it stands, then who
            // last edited it and when, with room for a name.
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    if let sync {
                        Label(sync.statusText, systemImage: statusSymbol(sync))
                            .lineLimit(1)
                    }
                    if uploading {
                        ProgressView().controlSize(.mini)
                    }
                    Spacer(minLength: 0)
                    if let sync { NotePresence(entries: sync.present, slots: sync.colorSlots) }
                }
                // There before anyone has edited the note, unseen: the line it will be said
                // on is already taken.
                Label(edited ?? " ", systemImage: "clock")
                    .lineLimit(1)
                    .opacity(edited == nil ? 0 : 1)
                    .accessibilityHidden(edited == nil)
            }
            .labelStyle(NoteStatusLabelStyle())
            .font(.caption)
            .foregroundStyle(theme.textSecondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // In line with the text below, which sits this far in.
        .padding(.horizontal, 19)
        .padding(.top, 8)
    }

    /// An icon in a column of its own width, so the two lines of a note's status start in
    /// line with one another whatever their icons are.
    private struct NoteStatusLabelStyle: LabelStyle {
        func makeBody(configuration: Configuration) -> some View {
            HStack(spacing: 6) {
                configuration.icon.frame(width: 16)
                configuration.title
            }
        }
    }

    private func statusSymbol(_ sync: DocSync) -> String {
        if sync.deleted { return "trash" }
        if !sync.synced { return "arrow.triangle.2.circlepath" }
        if sync.hasPending { return "arrow.up.circle" }
        return sync.canWrite ? "checkmark.circle" : "eye"
    }

    // MARK: Links

    /// Follows a link tapped in the note: to another note, a heading of this
    /// one, a task, somewhere else in the app, or out to the web.
    private func follow(_ destination: String) {
        // `[[Note]]`, and a plain link to a note's file, name a note in this design doc.
        if destination.hasPrefix("wiki:") || destination.wholeMatch(of: /[^:#?]+\.md(?:#.*)?/.ignoresCase()) != nil {
            var target = destination.hasPrefix("wiki:") ? String(destination.dropFirst(5)) : destination
            var heading: String?
            if let hash = target.firstIndex(of: "#") {
                heading = String(target[target.index(after: hash)...])
                target = String(target[..<hash])
            }
            guard let found = store.resolveNote(target, from: note) else {
                session.toasts.show("That note doesn't exist in this design doc.", .info)
                return
            }
            if found.id == note.id {
                scroll(toHeading: heading)
            } else {
                onOpenNote(found.id)
            }
            return
        }
        guard let url = URL(string: destination, relativeTo: session.api.baseURL)?.absoluteURL else { return }
        // A note's own address: stay in this design doc when it is one of its notes.
        if url.host() == session.api.baseURL.host(), let address = NoteAddress.read(url.path()), store.note(address.id) != nil {
            if address.id == note.id {
                scroll(toHeading: url.fragment(percentEncoded: false))
            } else {
                onOpenNote(address.id)
            }
            return
        }
        if ExtensionLinks.open(url, session: session) { return }
        if let route = SoloLinks.route(for: url, instance: session.api.baseURL) {
            session.navigator.open(route)
        } else if let scheme = url.scheme?.lowercased(), ["http", "https", "mailto"].contains(scheme) {
            openURL(url)
        }
    }

    private func scroll(toHeading wanted: String?) {
        guard let wanted, let entry = DocMarkdown.outline(text).first(where: {
            $0.heading.slug == wanted || $0.heading.text.caseInsensitiveCompare(wanted) == .orderedSame
        }) else { return }
        proxy.scroll(toLine: entry.line)
    }

    // MARK: Writing

    private func newSketchPrompt() {
        prompt = TextPrompt(title: "New sketch", placeholder: "Name") { name in
            guard let sketch = await store.createSketch(in: note.id, name: name) else { return }
            proxy.insert("![[\(sketch.path)]]", ownLine: true)
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
        guard let stored = await store.uploadImage(to: note.id, name: "Photo.jpg", data: encoded, contentType: "image/jpeg")
        else { return }
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
struct NotePresence: View {
    @Environment(\.theme) private var theme
    @Environment(\.colorScheme) private var colorScheme
    var entries: [TopicPresenceEntry]
    /// Each person's colour slot, where it is known.
    var slots: [UUID: Int] = [:]

    var body: some View {
        if !entries.isEmpty {
            HStack(spacing: 4) {
                ForEach(entries.prefix(4), id: \.userId) { entry in
                    // Ringed in the colour their caret and their pen are drawn in, as on the web:
                    // the ring is what says whose a cursor is.
                    UserAvatar(userId: entry.userId, name: entry.displayName, hasPicture: true, size: 22)
                        .padding(2.5)
                        .overlay(Circle().strokeBorder(ring(entry.userId), lineWidth: 2.5))
                }
            }
            .accessibilityElement()
            .accessibilityLabel("\(entries.count) other \(entries.count == 1 ? "person" : "people") here: "
                + entries.map { ($0.displayName ?? "Someone") + ($0.mode == .editing ? ", editing" : "") }.joined(separator: "; "))
        }
    }

    private func ring(_ userId: UUID) -> Color {
        guard let slot = slots[userId] else { return theme.textSecondary }
        return Color(platform: PresenceColor.color(PresenceColor.css(slot: slot), dark: colorScheme == .dark))
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
            GroupedList {
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
            GroupedList {
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
                .tint(theme.fill)
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
        var embeds = await store.embeds()
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
            var done = false
            if let image = item.image {
                done = await store.deleteImage(image.id)
            } else if let sketch = item.sketch {
                done = await store.deleteSketch(sketch.id)
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

    @State private var picture: PlatformImage?
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
                            Button("Close") { dismiss() }.buttonStyle(.clearGlass)
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

/// Where a note's column of text is in the view that holds it: as wide as reads well and no
/// wider, centred in what can be seen of the view. The editors lay the text out by this and
/// the note's ground is drawn by it, so that the two agree.
nonisolated enum NoteColumn {
    static let measure: CGFloat = 820

    /// The column's two edges in a view of this width with this much of its sides stood over.
    static func span(width: CGFloat, leading: CGFloat, trailing: CGFloat) -> ClosedRange<CGFloat> {
        let room = max(width - leading - trailing, 0)
        let margin = max((room - measure) / 2, 0)
        let start = leading + margin
        return start...max(width - trailing - margin, start)
    }
}

/// What a note is read on: the page's own white or black at three quarters, so the theme's
/// picture is quiet behind the words. It is whole across the column of text and fades away
/// on each side, gone by where the app's sidebar and the design doc's tree come to when they
/// are out — whether they are or not, so that putting one away does not move it. Where the
/// column already reaches that far, it fades over a short way past the column instead of
/// stopping at an edge. A phone has neither beside a note, and is covered from side to side.
private struct NoteGround: View {
    @Environment(\.theme) private var theme
    /// How much of the view's sides is stood over now.
    var leading: CGFloat
    var trailing: CGFloat
    /// How far in the sidebar and the tree come when they are out.
    var sidebar: CGFloat
    var pane: CGFloat
    var fades: Bool

    private static let strength = 0.75
    /// The least it fades over.
    private static let least: CGFloat = 40

    var body: some View {
        let ground = theme.isDark ? Color.black : Color.white
        GeometryReader { proxy in
            let width = proxy.size.width
            if fades, width > 0 {
                let column = NoteColumn.span(width: width, leading: leading, trailing: trailing)
                let from = min(max(leading, sidebar), column.lowerBound - Self.least)
                let to = max(width - max(trailing, pane), column.upperBound + Self.least)
                LinearGradient(stops: Self.stops(ground, from: from, column: column, to: to),
                               startPoint: .leading, endPoint: .trailing)
                    .frame(width: to - from)
                    .offset(x: from)
            } else {
                ground.opacity(Self.strength)
            }
        }
        // Not cut to the note's own room: with the tree out beside the note that room ends
        // where the column does, and the fade is finished under the tree's glass. Cut there,
        // it stopped at a line.
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    /// Nothing at either end, whole across the column, eased between: a straight fade shows
    /// as a line where it starts.
    private static func stops(_ ground: Color, from: CGFloat, column: ClosedRange<CGFloat>, to: CGFloat) -> [Gradient.Stop] {
        let span = to - from
        let steps = 8
        var stops: [Gradient.Stop] = []
        for step in 0...steps {
            let t = Double(step) / Double(steps)
            let eased = t * t * (3 - 2 * t)
            stops.append(.init(color: ground.opacity(strength * eased), location: (column.lowerBound - from) * t / span))
        }
        for step in 0...steps {
            let t = Double(step) / Double(steps)
            let eased = 1 - t * t * (3 - 2 * t)
            stops.append(.init(color: ground.opacity(strength * eased),
                               location: ((column.upperBound - from) + (to - column.upperBound) * t) / span))
        }
        return stops
    }
}
