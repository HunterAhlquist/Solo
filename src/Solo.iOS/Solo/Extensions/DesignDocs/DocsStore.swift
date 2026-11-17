import SwiftUI

/// Told when this device changes a project's design docs, so every list of them reloads at once.
enum DocLists {
    static let didChange = EventSource<UUID>()

    static func changed(_ projectId: UUID) {
        didChange.send(projectId)
    }
}

/// A project's design docs, for the places that list them.
@Observable
final class DocVaultListModel {
    private(set) var list: DocVaultList?
    private(set) var failed = false

    @ObservationIgnored private let session: Session
    @ObservationIgnored private let projectId: UUID
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var live = false

    init(session: Session, projectId: UUID) {
        self.session = session
        self.projectId = projectId
    }

    var open: [DocVault] { list?.vaults.filter { !$0.isClosed } ?? [] }
    var closed: [DocVault] { list?.vaults.filter(\.isClosed) ?? [] }
    var canManage: Bool { list?.canManage ?? false }

    /// `live` subscribes to the project's list topic; a card in a long list of
    /// projects skips it, a connection only holding so many topics.
    func start(live: Bool) async {
        await load()
        guard subscriptions.isEmpty else { return }
        subscriptions = [
            DocLists.didChange.subscribe { [weak self] changed in
                guard let self, changed == self.projectId else { return }
                Task { await self.load() }
            },
        ]
        if live {
            self.live = true
            subscriptions.append(session.hub.topicMessage.subscribe { [weak self] message in
                guard let self, message.topic == DocTopics.vaults, message.projectId == self.projectId else { return }
                Task { await self.load() }
            })
            await session.hub.subscribe(projectId: projectId, topic: DocTopics.vaults)
        }
    }

    func stop() async {
        subscriptions = []
        if live {
            live = false
            await session.hub.unsubscribe(projectId: projectId, topic: DocTopics.vaults)
        }
    }

    func load() async {
        do {
            list = try await session.api.docVaults(projectId)
            failed = false
        } catch {
            failed = list == nil
        }
    }
}

/// A folder or a note, as the tree shows it.
struct DocNode: Identifiable, Hashable {
    var id: UUID
    var isFolder: Bool
    var parentId: UUID?
    var name: String
    var version: UInt32

    var kind: DocItemKind { isFolder ? .folder : .note }
}

/// One design doc: its tree of folders and notes, its attachments, and the
/// lookups that turn what a note says (`[[Other note]]`, `![[drawing.sketch]]`)
/// into the thing it means.
@Observable
final class DocVaultStore {
    let projectId: UUID
    let vaultId: UUID

    private(set) var tree: DocTree?
    private(set) var images: [DocImage] = []
    private(set) var sketches: [DocSketch] = []
    private(set) var loadError: String?
    private(set) var deleted = false
    /// Bumped when a sketch somewhere in the vault was drawn on, so its previews redraw.
    private(set) var sketchRevision = 0
    var openFolders: Set<UUID> {
        didSet { UserDefaults.standard.set(openFolders.map(\.uuidString), forKey: foldersKey) }
    }

    @ObservationIgnored private let session: Session
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var pictures: [String: PlatformImage] = [:]
    @ObservationIgnored private var started = false

    init(session: Session, projectId: UUID, vaultId: UUID) {
        self.session = session
        self.projectId = projectId
        self.vaultId = vaultId
        let stored = UserDefaults.standard.stringArray(forKey: "solo.docs.\(vaultId.compact).folders") ?? []
        openFolders = Set(stored.compactMap(UUID.init))
    }

    private var foldersKey: String { "solo.docs.\(vaultId.compact).folders" }
    private var noteKey: String { "solo.docs.\(vaultId.compact).note" }

    /// The note last read in this vault on this device.
    var rememberedNote: UUID? {
        get { UserDefaults.standard.string(forKey: noteKey).flatMap(UUID.init) }
        set { UserDefaults.standard.set(newValue?.uuidString, forKey: noteKey) }
    }

    var vault: DocVault? { tree?.vault }

    // MARK: Loading

    func start() async {
        guard !started else { return }
        started = true
        let topic = DocTopics.vault(vaultId)
        subscriptions = [
            session.hub.topicMessage.subscribe { [weak self] message in
                guard let self, message.topic == topic, message.projectId == self.projectId else { return }
                if message.kind == "deleted" {
                    self.deleted = true
                    return
                }
                if message.kind == "sketch" {
                    self.sketchRevision += 1
                }
                Task { await self.load() }
            },
            session.hub.reconnected.subscribe { [weak self] in
                Task { await self?.load() }
            },
        ]
        await session.hub.subscribe(projectId: projectId, topic: topic)
        await load()
    }

    func stop() async {
        guard started else { return }
        started = false
        subscriptions = []
        await session.hub.unsubscribe(projectId: projectId, topic: DocTopics.vault(vaultId))
    }

    func load() async {
        do {
            let fresh = try await session.api.docTree(projectId, vaultId)
            tree = fresh
            loadError = nil
            // Attachments are only worth asking for once there is a note to show them in.
            if let lists = try? await (session.api.docImages(projectId, vaultId), session.api.docSketches(projectId, vaultId)) {
                images = lists.0
                sketches = lists.1
            }
        } catch let error as APIError where error.status == 404 {
            deleted = tree != nil
            loadError = "This design doc isn't available."
        } catch {
            if tree == nil { loadError = "The design doc couldn't load." }
        }
    }

    // MARK: The tree

    func note(_ id: UUID?) -> DocNote? {
        guard let id else { return nil }
        return tree?.notes.first { $0.id == id }
    }

    /// What sits directly in a folder (nil is the vault itself): folders first, each by name.
    func children(of parent: UUID?) -> [DocNode] {
        guard let tree else { return [] }
        let folders = tree.folders.filter { $0.parentId == parent }
            .map { DocNode(id: $0.id, isFolder: true, parentId: $0.parentId, name: $0.name, version: $0.version) }
        let notes = tree.notes.filter { $0.folderId == parent }
            .map { DocNode(id: $0.id, isFolder: false, parentId: $0.folderId, name: $0.name, version: $0.version) }
        let byName: (DocNode, DocNode) -> Bool = { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        return folders.sorted(by: byName) + notes.sorted(by: byName)
    }

    /// A folder or a note by its id, as the tree shows it.
    func node(_ id: UUID) -> DocNode? {
        guard let tree else { return nil }
        if let folder = tree.folders.first(where: { $0.id == id }) {
            return DocNode(id: folder.id, isFolder: true, parentId: folder.parentId, name: folder.name, version: folder.version)
        }
        return tree.notes.first { $0.id == id }
            .map { DocNode(id: $0.id, isFolder: false, parentId: $0.folderId, name: $0.name, version: $0.version) }
    }

    /// Whether a node may be put in a folder — or, with nil, at the top. Not where it already
    /// is, and a folder not into itself or anything inside it.
    func canMove(_ node: DocNode, to parent: UUID?) -> Bool {
        guard node.parentId != parent, node.id != parent else { return false }
        guard let parent else { return true }
        return !(node.isFolder && ancestors(of: parent).contains(node.id))
    }

    /// The folders above one, nearest first.
    func ancestors(of folder: UUID?) -> [UUID] {
        var result: [UUID] = []
        var next = folder
        while let id = next, !result.contains(id) {
            result.append(id)
            next = tree?.folders.first { $0.id == id }?.parentId
        }
        return result
    }

    func folderPath(_ id: UUID?) -> String {
        let names = ancestors(of: id).reversed().compactMap { folder in tree?.folders.first { $0.id == folder }?.name }
        return names.isEmpty ? "" : names.joined(separator: "/") + "/"
    }

    func path(of note: DocNote) -> String { folderPath(note.folderId) + note.name + ".md" }

    func reveal(_ note: DocNote) {
        let above = ancestors(of: note.folderId)
        if !above.allSatisfy(openFolders.contains) {
            openFolders.formUnion(above)
        }
    }

    // MARK: What a note's text points at

    private static func canonical(_ value: String) -> String? {
        var parts: [Substring] = []
        for part in value.replacingOccurrences(of: "\\", with: "/").split(separator: "/", omittingEmptySubsequences: false) {
            if part.isEmpty || part == "." { continue }
            if part == ".." {
                if parts.isEmpty { return nil }
                parts.removeLast()
            } else {
                parts.append(part)
            }
        }
        return parts.joined(separator: "/")
    }

    /// The note a wiki link names, as the web editor resolves it: by full path,
    /// then relative to the linking note's folder, then by a name only one note has.
    func resolveNote(_ target: String, from note: DocNote) -> DocNote? {
        guard let tree else { return nil }
        if target.isEmpty { return note }
        var path = target.removingPercentEncoding ?? target
        if !path.lowercased().hasSuffix(".md") { path += ".md" }
        let exact = Self.canonical(path), relative = Self.canonical(folderPath(note.folderId) + path)
        if let found = tree.notes.first(where: { self.path(of: $0) == exact }) ?? tree.notes.first(where: { self.path(of: $0) == relative }) {
            return found
        }
        let name = path.split(separator: "/").last.map(String.init) ?? path
        let matches = tree.notes.filter { $0.name + ".md" == name }
        return matches.count == 1 ? matches[0] : nil
    }

    /// The image an embed names: exact path, then beside the note, then an unambiguous file name.
    func resolveImage(_ target: String, from note: DocNote?) -> DocImage? {
        if target.hasPrefix("/") || target.contains("://") { return nil }
        if let exact = Self.canonical(target), let found = images.first(where: { $0.path == exact }) {
            return found
        }
        if let note, let relative = Self.canonical(folderPath(note.folderId) + target),
           let found = images.first(where: { $0.path == relative }) {
            return found
        }
        let name = target.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? target
        let matches = images.filter { $0.path.split(separator: "/").last.map(String.init) == name }
        return matches.count == 1 ? matches[0] : nil
    }

    /// The sketch an embed names: by full path from anywhere, or by its name from the note that owns it.
    func resolveSketch(_ target: String, from noteId: UUID?) -> DocSketch? {
        let name = target.lowercased().hasSuffix(".sketch") ? String(target.dropLast(7)) : target
        return sketches.first { $0.path == name + ".sketch" } ?? sketches.first { $0.noteId == noteId && $0.name == name }
    }

    // MARK: Pictures

    func picture(_ image: DocImage) async -> PlatformImage? {
        let key = "\(image.id.compact):\(image.version)"
        if let cached = pictures[key] { return cached }
        guard let data = try? await session.api.docImageData(projectId, vaultId, image.id), let decoded = PlatformImage(data: data)
        else { return nil }
        pictures[key] = decoded
        return decoded
    }

    /// A sketch as a picture: its page, replayed from its stored strokes.
    func sketchPicture(_ sketch: DocSketch, dark: Bool) async -> PlatformImage? {
        let key = "\(sketch.id.compact):\(sketch.version):\(sketchRevision):\(dark)"
        if let cached = pictures[key] { return cached }
        guard let state = try? await session.api.docState(projectId, sketch.id) else { return nil }
        let snapshot = await SketchSnapshot.load(state.updates.map(\.data), sketch: sketch)
        let colors = dark ? SketchColors.dark : SketchColors.light
        guard let drawn = snapshot.raster.image(page: snapshot.page, paper: colors.paper, themeInk: colors.ink) else { return nil }
        let picture = PlatformImage.picture(drawn)
        pictures[key] = picture
        return picture
    }

    /// A sketch was drawn on from this device: its pictures are stale.
    func sketchChanged() {
        sketchRevision += 1
    }

    // MARK: Changes

    private func run(_ operation: () async throws -> Void) async -> Bool {
        let done = await session.perform(operation, refresh: { await self.load() })
        await load()
        return done
    }

    func addFolder(name: String, parent: UUID?) async {
        if await run({ _ = try await session.api.addDocFolder(projectId, vaultId, name: name, parentId: parent) }), let parent {
            openFolders.insert(parent)
        }
    }

    func addNote(name: String, parent: UUID?) async -> DocNote? {
        let created = await session.save({ try await session.api.addDocNote(projectId, vaultId, name: name, parentId: parent) })
        await load()
        if let parent, created != nil { openFolders.insert(parent) }
        return created
    }

    func rename(_ node: DocNode, to name: String) async {
        _ = await run { try await session.api.changeDocItem(projectId, vaultId, node.kind, node.id, name: name,
                                                             parentId: node.parentId, version: node.version) }
    }

    func move(_ node: DocNode, to parent: UUID?) async {
        if let parent { openFolders.insert(parent) }
        _ = await run { try await session.api.changeDocItem(projectId, vaultId, node.kind, node.id, name: node.name,
                                                             parentId: parent, version: node.version) }
    }

    func delete(_ node: DocNode) async {
        _ = await run { try await session.api.deleteDocItem(projectId, vaultId, node.kind, node.id, version: node.version) }
    }

    /// How many notes link to this one by name; renaming it leaves those links pointing nowhere.
    func renameImpact(_ noteId: UUID) async -> Int {
        (try? await session.api.docRenameImpact(projectId, vaultId, noteId)) ?? 0
    }

    func changeVault(name: String, isClosed: Bool) async {
        guard let vault else { return }
        _ = await run { _ = try await session.api.changeDocVault(projectId, vault, name: name, isClosed: isClosed) }
        DocLists.changed(projectId)
    }

    func deleteVault() async -> Bool {
        guard let vault else { return false }
        let done = await session.perform { try await session.api.deleteDocVault(projectId, vault) }
        if done { DocLists.changed(projectId) }
        return done
    }
}

nonisolated private struct RenameImpact: Codable, Sendable {
    var references: Int
}

nonisolated struct DocUpdateRow: Codable, Sendable {
    var data: String
}

nonisolated struct DocState: Codable, Sendable {
    var seedMarkdown: String?
    var updates: [DocUpdateRow]
}

extension APIClient {
    private func vaultPath(_ projectId: UUID, _ vaultId: UUID) -> String {
        "api/ext/design-docs/projects/\(projectId.api)/vaults/\(vaultId.api)"
    }

    func docImages(_ projectId: UUID, _ vaultId: UUID) async throws -> [DocImage] {
        try await get("\(vaultPath(projectId, vaultId))/images")
    }

    func docSketches(_ projectId: UUID, _ vaultId: UUID) async throws -> [DocSketch] {
        try await get("\(vaultPath(projectId, vaultId))/sketches")
    }

    func docRenameImpact(_ projectId: UUID, _ vaultId: UUID, _ noteId: UUID) async throws -> Int {
        let impact: RenameImpact = try await get("\(vaultPath(projectId, vaultId))/notes/\(noteId.api)/rename-impact")
        return impact.references
    }

    /// This person's colour among the project's collaborators: a slot the instance hands out
    /// once and keeps, so they are the same colour in every note and to every client.
    func docColorSlot(_ projectId: UUID) async throws -> Int {
        let claimed: ClaimedColor = try await send("POST", "api/ext/design-docs/projects/\(projectId.api)/colors", body: [String: String]())
        return claimed.slot
    }

    /// Everyone's colour slot in a project, for outlining who is present in the colour of their caret.
    func docColorSlots(_ projectId: UUID) async throws -> [ClaimedColor] {
        try await get("api/ext/design-docs/projects/\(projectId.api)/colors")
    }

    /// A document's stored updates, for showing it without joining its live topic.
    func docState(_ projectId: UUID, _ documentId: UUID) async throws -> DocState {
        try await get("api/ext/design-docs/projects/\(projectId.api)/documents/\(documentId.api)")
    }
}

/// Follows links the extension owns, wherever they turn up: a note's address,
/// a task linked from a note, a design doc's page.
enum DocLinks {
    /// Opens the note a link addresses, wherever it lives. False when the link isn't a note's.
    @discardableResult
    static func openNote(_ link: String, session: Session) -> Bool {
        guard let address = NoteAddress.read(link) else { return false }
        Task {
            guard let place = try? await session.api.locateDocNote(address.id) else {
                session.toasts.show("That note isn't available.", .warning)
                return
            }
            session.navigator.open(.designDocs(project: place.projectAlias, vaultId: place.vaultId, noteId: place.noteId))
        }
        return true
    }

    /// True when the link was one of ours and is being opened.
    static func open(_ url: URL, session: Session) -> Bool {
        let path = url.path()
        if openNote(path + (url.fragment().map { "#\($0)" } ?? ""), session: session) { return true }
        let parts = path.split(separator: "/").map(String.init)
        guard parts.first == "docs" else { return false }
        if parts.count == 3, parts[1] == "task", let id = NoteAddress.decode(parts[2]) {
            Task { await openTask(id, session: session) }
            return true
        }
        if parts.count == 3, let vault = UUID(uuidString: parts[2]) {
            session.navigator.open(.designDocs(project: Aliases.normalize(parts[1]), vaultId: vault))
            return true
        }
        return false
    }

    static func openTask(_ id: UUID, session: Session) async {
        guard let task = try? await session.api.task(id), let board = try? await session.api.board(task.boardId) else {
            session.toasts.show("That task isn't available.", .warning)
            return
        }
        session.navigator.open(.board(project: board.projectAlias, board: board.alias, task: task.number))
    }
}

/// The colour slot the instance gave this person in a project.
nonisolated struct ClaimedColor: Decodable, Sendable {
    var userId: UUID?
    var slot: Int
}
