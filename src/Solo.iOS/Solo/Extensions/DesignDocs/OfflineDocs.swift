import Foundation
import Observation
import YSwift

// A design doc can be downloaded, as it can on the web (js/offline/session.js): every note
// and sketch it holds, and its pictures, are kept on the device, and from then on it opens and
// is written in with or without the instance. What is written is kept here first and owed to
// the instance, which is paid whenever it can be reached — over the endpoints the web's own
// download uses (`…/vaults/{id}/offline`), which take a document's changes as Yjs updates and
// answer a repeat of one with the answer they gave the first time.
//
// What is kept, under Application Support, apart for each instance and person:
//   vaults.json            which design docs are downloaded, and what each was last told
//   documents.json         one record a document: how far it has been read, and what is owed
//   documents/<id>.local   the document as it stands here, with what is not yet sent
//   documents/<id>.server  the document as the instance is known to have it
//   documents/<id>.outbox  the update being sent, kept so that a retry sends the same one
//   images/<id>            a picture's bytes
//   commands.json          what was done to a design doc's tree and is still to send
//   commands/<id>.data     a new or replaced picture's bytes, to go with its command
//
// What is done to the tree is in OfflineChanges.swift, and the archive of all of it in
// OfflineRecovery.swift. What the web's has and this has not: importing notes into a
// downloaded design doc.

/// What the app does to a Yjs document's stored state without opening it in an editor.
nonisolated enum YStates {
    /// The updates as one: applied in order to an empty document.
    static func merged(_ updates: [[UInt8]]) -> [UInt8] {
        let doc = SharedDoc()
        doc.apply(updates.filter { !$0.isEmpty })
        return doc.fullState()
    }

    /// What `local` has that `server` has not, as an update.
    static func missing(from server: [UInt8], in local: [UInt8]) -> [UInt8] {
        let ours = SharedDoc(), theirs = SharedDoc()
        ours.apply([local])
        if !server.isEmpty { theirs.apply([server]) }
        return ours.update(since: theirs.stateVector())
    }

    static func markdown(_ state: [UInt8]) -> String {
        let doc = SharedDoc()
        doc.apply([state])
        return doc.string()
    }

    /// A note that has only ever been imported, as the document its text becomes.
    static func seeded(_ markdown: String) -> [UInt8] {
        let doc = SharedDoc()
        doc.replaceText(start: 0, length: 0, with: markdown)
        return doc.fullState()
    }
}

/// A downloaded design doc: where it is, and how it stood when the instance was last read.
nonisolated struct OfflineVault: Codable, Identifiable, Sendable {
    var id: UUID
    var projectId: UUID
    var projectAlias: String
    var name: String
    /// False until everything in it has arrived once.
    var ready = false
    var canWrite = false
    /// The instance no longer lets this person at it, or it is gone. What is kept stays.
    var unavailable = false
    /// Its tree, sketches and pictures as the instance last had them. What is shown is this
    /// with whatever is still to send done to it (`OfflineVaults.view`).
    var base: OfflineBase?
    /// Rows the instance no longer has, set aside as they were: the names and places of
    /// notes deleted there, for an archive of what is on the device (OfflineRecovery.swift).
    var recovery: OfflineBase?
    /// The version of each picture that is kept.
    var images: [String: UInt32] = [:]
    /// What the tree, the documents and the pictures amounted to when the app's own reads of
    /// them were last refreshed.
    var signature = ""
}

/// One document of a downloaded design doc.
nonisolated struct OfflineDocument: Codable, Sendable {
    var id: UUID
    var vaultId: UUID
    /// How far the instance's log of it has been read.
    var seq = "0"
    /// Changed here since an update was last made up to send.
    var dirty = false
    var outbox: Outbox?
    /// Gone from the instance, with work here that was never sent.
    var deletedOnline = false
    /// Shown from an imported note's text by someone who may not write: not yet the real document.
    var readonlySeed = false
    /// A note whose text was sent from here, and whose saved copy — what the tree and links
    /// read — has yet to follow it: the sequence that copy will be current to.
    var copyOwed: String?

    nonisolated struct Outbox: Codable, Sendable {
        var id: UUID
        /// Why the instance refused it, when it did: it is kept, and not sent again unasked.
        var error: String?
    }
}

/// A change the instance refused, for someone to decide about.
struct OfflineProblem: Identifiable {
    var id: UUID
    var name: String
    var message: String
    /// Something done to the tree, as against something written in a document.
    var isCommand = false
}

nonisolated private struct OfflineManifest: Decodable, Sendable {
    var tree: DocTree
    var sketches: [DocSketch]
    var images: [DocImage]
    var documents: [String: String]
    var canWrite: Bool
    var canManage: Bool?
    var projectWrite: Bool?

    var base: OfflineBase {
        OfflineBase(tree: tree, sketches: sketches, images: images, canWrite: canWrite, canManage: canManage ?? false,
                    projectWrite: projectWrite ?? canWrite)
    }

    var signature: String {
        let tree = "\(tree.vault.version)|" + tree.folders.map { "\($0.id.api):\($0.version)" }.sorted().joined(separator: ",")
            + "|" + tree.notes.map { "\($0.id.api):\($0.version):\($0.seq)" }.sorted().joined(separator: ",")
        let rest = sketches.map { "\($0.id.api):\($0.version)" }.sorted().joined(separator: ",")
            + "|" + images.map { "\($0.id.api):\($0.version)" }.sorted().joined(separator: ",")
            + "|" + documents.map { "\($0.key):\($0.value)" }.sorted().joined(separator: ",")
        return tree + "|" + rest + "|\(canWrite)"
    }
}

nonisolated private struct OfflineRead: Decodable, Sendable {
    var seq: String
    var updates: [Data]
    var seedMarkdown: String?
}

nonisolated private struct OfflineWrite: Encodable, Sendable {
    var id: UUID
    var data: Data
    var seedMarkdown: String?
}

nonisolated private struct OfflineWriteResult: Decodable, Sendable {
    var seq: String
    var previousSeq: String
    /// A sketch's row moves on when it is drawn in; a note's does not.
    var previousVersion: UInt32?
    var version: UInt32?
}

/// The files. Nothing here is large enough, or read often enough, to need more than that.
nonisolated private struct OfflineDisk: Sendable {
    let root: URL

    init(scope: String) {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in scope.utf8 { hash = (hash ^ UInt64(byte)) &* 0x100_0000_01b3 }
        root = URL.applicationSupportDirectory.appending(path: "OfflineDocs/\(String(hash, radix: 16))", directoryHint: .isDirectory)
        for folder in ["documents", "images", "commands"] {
            try? FileManager.default.createDirectory(at: root.appending(path: folder), withIntermediateDirectories: true)
        }
    }

    enum Part: String { case local, server, outbox }

    private func file(_ id: UUID, _ part: Part) -> URL { root.appending(path: "documents/\(id.api).\(part.rawValue)") }
    private func image(_ id: UUID) -> URL { root.appending(path: "images/\(id.api)") }
    private func data(_ id: UUID) -> URL { root.appending(path: "commands/\(id.api).data") }

    func read(_ id: UUID, _ part: Part) -> [UInt8]? { (try? Data(contentsOf: file(id, part))).map { [UInt8]($0) } }
    func write(_ id: UUID, _ part: Part, _ bytes: [UInt8]) { try? Data(bytes).write(to: file(id, part), options: .atomic) }
    func remove(_ id: UUID, _ part: Part) { try? FileManager.default.removeItem(at: file(id, part)) }
    func removeDocument(_ id: UUID) { [Part.local, .server, .outbox].forEach { remove(id, $0) } }

    func readImage(_ id: UUID) -> Data? { try? Data(contentsOf: image(id)) }
    func writeImage(_ id: UUID, _ data: Data) { try? data.write(to: image(id), options: .atomic) }
    func removeImage(_ id: UUID) { try? FileManager.default.removeItem(at: image(id)) }

    func readCommandData(_ id: UUID) -> Data? { try? Data(contentsOf: data(id)) }
    func writeCommandData(_ id: UUID, _ bytes: Data) { try? bytes.write(to: data(id), options: .atomic) }
    func removeCommandData(_ id: UUID) { try? FileManager.default.removeItem(at: data(id)) }

    func load<T: Decodable>(_ name: String, as type: T.Type) -> T? {
        (try? Data(contentsOf: root.appending(path: name))).flatMap { try? JSONDecoder().decode(type, from: $0) }
    }

    func save<T: Encodable>(_ name: String, _ value: T) {
        try? JSONEncoder().encode(value).write(to: root.appending(path: name), options: .atomic)
    }
}

/// The design docs downloaded to this device, and what keeps them in step with the instance.
@Observable
final class OfflineVaults {
    private(set) var vaults: [OfflineVault]
    private(set) var downloading: Set<UUID> = []
    private(set) var syncing = false
    /// Whether the instance answered the last time it was asked.
    private(set) var reachable = true
    /// Moves whenever what is owed, or what was refused, changes: what a view reads to be told.
    private(set) var revision = 0

    /// Told, with a design doc's id, whenever how it is shown has changed: something was done
    /// to its tree here, or the instance was read and had something new.
    @ObservationIgnored let viewChanged = EventSource<UUID>()

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private let disk: OfflineDisk
    @ObservationIgnored private var documents: [UUID: OfflineDocument]
    /// What has been done to the trees of downloaded design docs and not yet sent, oldest first.
    @ObservationIgnored private var commands: [OfflineCommand]
    /// The text of notes written here and not yet copied to the instance, as last read off the device.
    @ObservationIgnored private var derived: [UUID: String] = [:]
    @ObservationIgnored private var open: [UUID: Weak] = [:]
    /// Every document open in an editor, downloaded or not: the ones to tell when a download finishes.
    @ObservationIgnored private var editors: [UUID: Weak] = [:]
    @ObservationIgnored private var running: Task<Void, Never>?
    @ObservationIgnored private var wanted = false
    @ObservationIgnored private var timer: Task<Void, Never>?
    @ObservationIgnored private var subscriptions: [EventSubscription] = []

    private struct Weak {
        weak var sync: DocSync?
    }

    /// What the instance refuses for good: asking again gets the same answer.
    private static let refusals: Set<Int> = [400, 403, 404, 409, 413, 415]

    init(api: APIClient, userId: UUID) {
        self.api = api
        disk = OfflineDisk(scope: api.baseURL.absoluteString + "|" + userId.api)
        vaults = disk.load("vaults.json", as: [OfflineVault].self) ?? []
        documents = Dictionary((disk.load("documents.json", as: [OfflineDocument].self) ?? []).map { ($0.id, $0) }) { first, _ in first }
        commands = (disk.load("commands.json", as: [OfflineCommand].self) ?? []).sorted(by: OfflineCommand.order)
    }

    // MARK: Reading

    /// Whether a design doc is on the device, whole.
    func isDownloaded(_ vaultId: UUID) -> Bool {
        vaults.contains { $0.id == vaultId && $0.ready && $0.base != nil }
    }

    func vault(_ id: UUID) -> OfflineVault? {
        vaults.first { $0.id == id }
    }

    /// A downloaded design doc as it is shown: as the instance last had it, with everything
    /// still to send done to it, and each note that was written here as it now reads.
    func view(_ vaultId: UUID) -> OfflineBase? {
        _ = revision
        guard let vault = vault(vaultId), vault.ready, let base = vault.base else { return nil }
        var view = OfflineTree.projected(base, commands.filter { $0.vaultId == vaultId })
        for index in view.tree.notes.indices {
            guard let text = markdown(of: view.tree.notes[index].id) else { continue }
            view.tree.notes[index].markdown = text
            view.tree.notes[index].headings = DocMarkdown.headings(text)
        }
        return view
    }

    private func markdown(of noteId: UUID) -> String? {
        guard let document = documents[noteId], owes(document) || document.copyOwed != nil else { return nil }
        if let kept = derived[noteId] { return kept }
        guard let local = disk.read(noteId, .local) else { return nil }
        let text = YStates.markdown(local)
        derived[noteId] = text
        return text
    }

    /// A project's design docs as they are listed: the instance's list, with each downloaded
    /// one as it stands here — renamed, closed, deleted or made here and not yet sent.
    func listed(_ list: DocVaultList, projectId: UUID) -> DocVaultList {
        let here = vaults.filter { $0.projectId == projectId }.compactMap { view($0.id) }
        guard !here.isEmpty else { return list }
        let ids = Set(here.map(\.tree.vault.id))
        var merged = list
        merged.vaults = list.vaults.filter { !ids.contains($0.id) } + here.filter { !$0.deleted }.map(\.tree.vault)
        return merged
    }

    private func owes(_ document: OfflineDocument) -> Bool { document.dirty || document.outbox != nil }

    /// How many changes to a design doc the instance has not got: documents written in, and
    /// things done to the tree.
    func pending(in vaultId: UUID) -> Int {
        _ = revision
        return documents.values.filter { $0.vaultId == vaultId && owes($0) }.count + commands.filter { $0.vaultId == vaultId }.count
    }

    func isPending(_ documentId: UUID) -> Bool {
        _ = revision
        return documents[documentId].map(owes) ?? false
    }

    /// Whether a note or a sketch was made here and the instance has yet to be told of it.
    func awaitsCreation(_ documentId: UUID) -> Bool {
        commands.contains { $0.action == .create && $0.targetId == documentId }
    }

    func problems(in vaultId: UUID) -> [OfflineProblem] {
        _ = revision
        let view = view(vaultId)
        var names: [UUID: String] = [:]
        view?.tree.notes.forEach { names[$0.id] = $0.name }
        view?.sketches.forEach { names[$0.id] = $0.name }
        let refused = commands
            .filter { $0.vaultId == vaultId && $0.error != nil }
            .map { OfflineProblem(id: $0.id, name: describe($0), message: $0.error ?? "", isCommand: true) }
        let unsent = documents.values
            .filter { $0.vaultId == vaultId && $0.outbox?.error != nil }
            .map { OfflineProblem(id: $0.id, name: names[$0.id] ?? "A document", message: $0.outbox?.error ?? "") }
            .sorted { $0.name < $1.name }
        return refused + unsent
    }

    /// What a command was, in a few words, for whoever is asked to decide about it.
    private func describe(_ command: OfflineCommand) -> String {
        let base = vault(command.vaultId)?.base
        let thing: String
        var name = command.name
        switch command.entity {
        case .folder:
            thing = "folder"
            name = name ?? base?.tree.folders.first { $0.id == command.targetId }?.name
        case .note:
            thing = "note"
            name = name ?? base?.tree.notes.first { $0.id == command.targetId }?.name
        case .sketch:
            thing = "sketch"
            name = name ?? base?.sketches.first { $0.id == command.targetId }?.name
        case .image:
            thing = "picture"
            name = name ?? base?.images.first { $0.id == command.targetId }?.path.split(separator: "/").last.map(String.init)
        case .vault:
            thing = "design doc"
            name = name ?? base?.tree.vault.name
        }
        let verb = switch command.action {
        case .create: "New"
        case .update: "Change to"
        case .delete: "Delete"
        }
        return name.map { "\(verb) \(thing) “\($0)”" } ?? "\(verb) \(thing)"
    }

    /// What to say of a document that is kept here, under its name.
    func status(of documentId: UUID, unsaved: Bool) -> String {
        _ = revision
        if unsaved { return "Saving on this device…" }
        guard let document = documents[documentId] else { return "Kept on this device" }
        if document.outbox?.error != nil { return "Not sent · needs attention" }
        if document.deletedOnline { return "Deleted online · kept on this device" }
        if owes(document) { return reachable ? "Saved on this device · sending…" : "Offline · saved on this device" }
        return reachable ? "Saved · kept on this device" : "Offline · kept on this device"
    }

    func image(_ id: UUID) -> Data? { disk.readImage(id) }

    /// A document as it stands here, for showing it without opening it.
    func state(_ documentId: UUID) -> [UInt8]? {
        guard documents[documentId] != nil else { return nil }
        return disk.read(documentId, .local)
    }

    // MARK: Starting and stopping

    func start(hub: HubClient) {
        guard subscriptions.isEmpty else { return }
        subscriptions = [hub.reconnected.subscribe { [weak self] in self?.sync() }]
        // Every ten seconds, as the web's does while its page is showing. A Mac's window is
        // showing whether or not it is the one in front; an iPhone's app that is not in
        // front is not running, and is asked again when it comes back.
        timer = Task { [weak self] in
            while !Task.isCancelled {
                if Platform.isMac || Platform.isActive { self?.sync() }
                try? await Task.sleep(for: .seconds(10))
            }
        }
    }

    func stop() {
        subscriptions = []
        timer?.cancel()
        running?.cancel()
    }

    // MARK: Downloading

    /// Brings a whole design doc onto the device. Returns what went wrong, or nil.
    func download(projectId: UUID, projectAlias: String, vault: DocVault) async -> String? {
        guard !downloading.contains(vault.id) else { return nil }
        downloading.insert(vault.id)
        defer { downloading.remove(vault.id) }
        if self.vault(vault.id) == nil {
            vaults.append(OfflineVault(id: vault.id, projectId: projectId, projectAlias: projectAlias, name: vault.name))
        }
        do {
            try await pull(vault.id)
            change(vault.id) { $0.ready = true }
            reachable = true
            // What is open in it is on the device from now, and not from when it is next opened.
            editors.values.forEach { $0.sync?.keepOnDevice() }
            revision += 1
            viewChanged.send(vault.id)
            sync()
            return nil
        } catch {
            // Half of one is not one: nothing is shown from it, and asking again takes it up where it stopped.
            saveVaults()
            return (error as? LocalizedError)?.errorDescription ?? "The download didn't finish. Try again."
        }
    }

    /// Takes a design doc off the device. Refused while it holds anything the instance has not got.
    func remove(_ vaultId: UUID) -> String? {
        guard pending(in: vaultId) == 0 else { return "Changes are still waiting to be sent. Sync first, then remove the download." }
        forget(vaultId)
        return nil
    }

    private func forget(_ vaultId: UUID) {
        guard let vault = vault(vaultId) else { return }
        for (id, document) in documents where document.vaultId == vaultId {
            disk.removeDocument(id)
            documents[id] = nil
            derived[id] = nil
            // An editor that has it open goes back to saving straight to the instance.
            open[id]?.sync?.leaveDevice()
            open[id] = nil
        }
        for command in commands where command.vaultId == vaultId { disk.removeCommandData(command.id) }
        commands.removeAll { $0.vaultId == vaultId }
        vault.images.keys.compactMap(UUID.init).forEach(disk.removeImage)
        vaults.removeAll { $0.id == vaultId }
        saveVaults()
        saveDocuments()
        saveCommands()
        revision += 1
        viewChanged.send(vaultId)
    }

    // MARK: An open document

    /// A document being opened in an editor. Answers with how it stands here, and whether it
    /// may be written in, when it is one of a downloaded design doc's.
    func opened(_ sync: DocSync, documentId: UUID, vaultId: UUID) -> (state: [UInt8], seq: Int64, canWrite: Bool)? {
        guard let vault = vault(vaultId), let view = view(vaultId), let document = documents[documentId],
              let state = disk.read(documentId, .local) else { return nil }
        open[documentId] = Weak(sync: sync)
        return (state, Int64(document.seq) ?? 0,
                view.canWrite && !vault.unavailable && !document.deletedOnline && !document.readonlySeed)
    }

    func closed(_ documentId: UUID) {
        open[documentId] = nil
        editors[documentId] = nil
    }

    /// A document has been opened in an editor, whether or not it is kept here.
    func editing(_ sync: DocSync) {
        editors[sync.documentId] = Weak(sync: sync)
    }

    /// A change made in an editor: the document as it now stands is kept, and is owed.
    func changed(_ documentId: UUID, state: [UInt8]) {
        guard documents[documentId] != nil else { return }
        disk.write(documentId, .local, state)
        documents[documentId]?.dirty = true
        derived[documentId] = nil
        saveDocuments()
        revision += 1
        sync()
    }

    /// Changes that came over the live connection: the instance has those, and now so does this.
    func received(_ documentId: UUID, updates: [[UInt8]], state: [UInt8]) {
        guard documents[documentId] != nil else { return }
        disk.write(documentId, .server, YStates.merged([disk.read(documentId, .server) ?? []] + updates))
        disk.write(documentId, .local, state)
        derived[documentId] = nil
    }

    // MARK: Changing the tree

    /// The design doc as shown, if things may be written in it.
    private func writable(_ vaultId: UUID) throws -> OfflineBase {
        guard let vault = vault(vaultId), let view = view(vaultId) else { throw OfflineRefusal.gone }
        guard view.canWrite, !vault.unavailable, !view.tree.vault.isClosed, !view.deleted else { throw OfflineRefusal.readOnly }
        return view
    }

    private func manageable(_ vaultId: UUID) throws -> OfflineBase {
        guard let view = view(vaultId), !view.deleted else { throw OfflineRefusal.gone }
        guard view.canManage else { throw OfflineRefusal.notManager }
        return view
    }

    private func enqueue(_ command: OfflineCommand) {
        commands.append(command)
        saveCommands()
        revision += 1
        viewChanged.send(command.vaultId)
        sync()
    }

    /// A note or a sketch made here starts as an empty document, which it is until written in.
    private func startDocument(_ id: UUID, in vaultId: UUID) {
        let empty = YStates.merged([])
        disk.write(id, .local, empty)
        disk.write(id, .server, empty)
        documents[id] = OfflineDocument(id: id, vaultId: vaultId)
        saveDocuments()
    }

    /// What a deletion names so that the instance can refuse it if anything it takes has
    /// moved on: each row's version, and how far each document had been read here.
    private func grounded(_ command: OfflineCommand, in view: OfflineBase) -> OfflineCommand {
        var command = command
        let removed = OfflineTree.removed(by: command, from: view)
        command.baseVersions = removed.versions
        command.baseDocuments = Dictionary(uniqueKeysWithValues: removed.documents.map { ($0.api, documents[$0]?.seq ?? "0") })
        return command
    }

    func addFolder(_ vaultId: UUID, name: String, parent: UUID?) throws -> DocFolder {
        let view = try writable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        guard !view.tree.folders.contains(where: { $0.name == name && $0.parentId == parent }) else { throw OfflineRefusal.nameTaken }
        let id = UUID()
        enqueue(OfflineCommand(vaultId: vaultId, entity: .folder, action: .create, targetId: id, name: name, parentId: parent))
        return DocFolder(id: id, parentId: parent, name: name, version: 0)
    }

    func addNote(_ vaultId: UUID, name: String, parent: UUID?) throws -> DocNote {
        let view = try writable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        guard !view.tree.notes.contains(where: { $0.name == name && $0.folderId == parent }) else { throw OfflineRefusal.nameTaken }
        let id = UUID()
        startDocument(id, in: vaultId)
        enqueue(OfflineCommand(vaultId: vaultId, entity: .note, action: .create, targetId: id, name: name, parentId: parent))
        return DocNote(id: id, vaultId: vaultId, folderId: parent, name: name, markdown: "", seq: "0", headings: [],
                       lastEditedBy: nil, lastEditedAt: nil, version: 0)
    }

    /// Renames or moves a folder or a note; `parent` nil is the design doc's root.
    func changeItem(_ vaultId: UUID, _ kind: DocItemKind, _ id: UUID, name: String, parent: UUID?) throws {
        let view = try writable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        var command = OfflineCommand(vaultId: vaultId, entity: kind == .folder ? .folder : .note, action: .update, targetId: id,
                                     name: name, parentId: parent)
        if kind == .folder {
            guard let old = view.tree.folders.first(where: { $0.id == id }) else { throw OfflineRefusal.gone }
            var up = parent
            while let folder = up {
                guard folder != id else { throw OfflineRefusal.cycle }
                up = view.tree.folders.first { $0.id == folder }?.parentId
            }
            command.version = old.version
            command.baseName = old.name
            command.baseParentId = old.parentId
        } else {
            guard let old = view.tree.notes.first(where: { $0.id == id }) else { throw OfflineRefusal.gone }
            command.version = old.version
            command.baseName = old.name
            command.baseParentId = old.folderId
        }
        enqueue(command)
    }

    func deleteItem(_ vaultId: UUID, _ kind: DocItemKind, _ id: UUID) throws {
        let view = try writable(vaultId)
        var command = OfflineCommand(vaultId: vaultId, entity: kind == .folder ? .folder : .note, action: .delete, targetId: id)
        if kind == .folder {
            guard let old = view.tree.folders.first(where: { $0.id == id }) else { throw OfflineRefusal.gone }
            command.version = old.version
            command.baseName = old.name
            command.baseParentId = old.parentId
        } else {
            guard let old = view.tree.notes.first(where: { $0.id == id }) else { throw OfflineRefusal.gone }
            command.version = old.version
            command.baseName = old.name
            command.baseParentId = old.folderId
        }
        enqueue(grounded(command, in: view))
    }

    func changeVault(_ vaultId: UUID, name: String, isClosed: Bool) throws {
        let view = try manageable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        enqueue(OfflineCommand(vaultId: vaultId, entity: .vault, action: .update, targetId: vaultId,
                               version: view.tree.vault.version, name: name, isClosed: isClosed))
    }

    func deleteVault(_ vaultId: UUID) throws {
        let view = try manageable(vaultId)
        let command = OfflineCommand(vaultId: vaultId, entity: .vault, action: .delete, targetId: vaultId,
                                     version: view.tree.vault.version)
        enqueue(grounded(command, in: view))
    }

    /// Makes a design doc here, in a project that already has one downloaded — which is
    /// where what this person may do in the project is known from. Nil where it has none:
    /// then it is the instance's to make.
    func createVault(projectId: UUID, name: String) throws -> DocVault? {
        let here = vaults.filter { $0.projectId == projectId && $0.ready }
        guard let template = here.first(where: { $0.base != nil }), let known = template.base else { return nil }
        guard here.contains(where: { $0.base?.canManage == true }) else { throw OfflineRefusal("Only an administrator can create a design doc.") }
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        let made = DocVault(id: UUID(), name: name, isClosed: false, closedAt: nil, version: 0)
        var vault = OfflineVault(id: made.id, projectId: projectId, projectAlias: template.projectAlias, name: name)
        vault.ready = true
        vault.canWrite = known.projectWrite
        vault.base = OfflineBase(tree: DocTree(vault: made, folders: [], notes: []), canWrite: known.projectWrite, canManage: true,
                                 projectWrite: known.projectWrite)
        vaults.append(vault)
        saveVaults()
        enqueue(OfflineCommand(vaultId: made.id, entity: .vault, action: .create, targetId: made.id, name: name))
        return made
    }

    func addSketch(_ vaultId: UUID, noteId: UUID, name: String) throws -> DocSketch {
        let view = try writable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        guard !view.sketches.contains(where: { $0.name == name && $0.noteId == noteId }) else { throw OfflineRefusal.nameTaken }
        let id = UUID()
        // Where the instance will store it, unless someone has made one of that name there
        // meanwhile; then it says where, and the notes that embed it are put right.
        let path = DocMarkdown.attachmentPath(noteId: noteId, name: name + ".sketch")
        startDocument(id, in: vaultId)
        enqueue(OfflineCommand(vaultId: vaultId, entity: .sketch, action: .create, targetId: id, name: name, noteId: noteId, path: path))
        return DocSketch(id: id, noteId: noteId, name: name, pageSize: "A4", orientation: "portrait", pattern: "empty",
                         lastEditedBy: nil, lastEditedAt: nil, version: 0, path: path)
    }

    /// A sketch's name and paper, as the instance keeps them for its previews and lists.
    func changeSketch(_ vaultId: UUID, _ id: UUID, name: String? = nil, pageSize: String, orientation: String, pattern: String) throws {
        let view = try writable(vaultId)
        guard let old = view.sketches.first(where: { $0.id == id }) else { throw OfflineRefusal("The sketch no longer exists.") }
        let name = name ?? old.name
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        enqueue(OfflineCommand(vaultId: vaultId, entity: .sketch, action: .update, targetId: id, version: old.version, name: name,
                               noteId: old.noteId, pageSize: pageSize, orientation: orientation, pattern: pattern))
    }

    func deleteSketch(_ vaultId: UUID, _ id: UUID) throws {
        let view = try writable(vaultId)
        guard let old = view.sketches.first(where: { $0.id == id }) else { throw OfflineRefusal("The sketch no longer exists.") }
        let command = OfflineCommand(vaultId: vaultId, entity: .sketch, action: .delete, targetId: id, version: old.version,
                                     noteId: old.noteId)
        enqueue(grounded(command, in: view))
    }

    /// A picture put in a note: kept here, shown at once, and sent with its command.
    func addImage(_ vaultId: UUID, noteId: UUID, name: String, data: Data, contentType: String) throws -> DocImage {
        let view = try writable(vaultId)
        guard OfflineTree.validName(name) else { throw OfflineRefusal.badName }
        let id = UUID()
        // The instance's own rule for two of one name: the second is told apart by its id.
        var path = DocMarkdown.attachmentPath(noteId: noteId, name: name)
        if view.images.contains(where: { $0.path == path }) {
            path = DocMarkdown.attachmentPath(noteId: noteId, name: "\(id.compact)-\(name)")
        }
        let image = DocImage(id: id, path: path, contentType: contentType, width: 0, height: 0, bytes: data.count, version: 0)
        var command = OfflineCommand(vaultId: vaultId, entity: .image, action: .create, targetId: id, name: name, noteId: noteId)
        command.image = image
        command.hasData = true
        disk.writeImage(id, data)
        disk.writeCommandData(command.id, data)
        enqueue(command)
        return image
    }

    func replaceImage(_ vaultId: UUID, _ id: UUID, data: Data, contentType: String) throws -> DocImage {
        let view = try writable(vaultId)
        guard var image = view.images.first(where: { $0.id == id }) else { throw OfflineRefusal("The image no longer exists.") }
        var command = OfflineCommand(vaultId: vaultId, entity: .image, action: .update, targetId: id, version: image.version,
                                     name: image.path.split(separator: "/").last.map(String.init))
        image.contentType = contentType
        image.bytes = data.count
        command.image = image
        command.hasData = true
        disk.writeImage(id, data)
        disk.writeCommandData(command.id, data)
        enqueue(command)
        return image
    }

    func deleteImage(_ vaultId: UUID, _ id: UUID) throws {
        let view = try writable(vaultId)
        guard let old = view.images.first(where: { $0.id == id }) else { throw OfflineRefusal("The image no longer exists.") }
        enqueue(OfflineCommand(vaultId: vaultId, entity: .image, action: .delete, targetId: id, version: old.version))
    }

    // MARK: What was refused

    /// Sends a refused change again. Something written in a document goes as it is. Something
    /// done to the tree is done again to the tree as it now stands — which is what refusing it
    /// was for someone to look at — and a deletion is not: that is to be made again, knowingly.
    func retry(_ problem: OfflineProblem) async throws {
        guard problem.isCommand else {
            documents[problem.id]?.outbox?.error = nil
            saveDocuments()
            revision += 1
            sync()
            return
        }
        guard let command = commands.first(where: { $0.id == problem.id }) else { return }
        try await pull(command.vaultId)
        guard let index = commands.firstIndex(where: { $0.id == problem.id }), let base = vault(command.vaultId)?.base else { return }
        var current: (version: UInt32, name: String?, parent: UUID?)?
        switch command.entity {
        case .vault: current = (base.tree.vault.version, base.tree.vault.name, nil)
        case .folder: current = base.tree.folders.first { $0.id == command.targetId }.map { ($0.version, $0.name, $0.parentId) }
        case .note: current = base.tree.notes.first { $0.id == command.targetId }.map { ($0.version, $0.name, $0.folderId) }
        case .sketch: current = base.sketches.first { $0.id == command.targetId }.map { ($0.version, $0.name, nil) }
        case .image: current = base.images.first { $0.id == command.targetId }.map { ($0.version, nil, nil) }
        }
        if command.action != .create, current == nil {
            throw OfflineRefusal("It was deleted on the instance. Discard this change.")
        }
        if command.action == .delete {
            throw OfflineRefusal("Look at the design doc as it is now and delete it again; discard this change first.")
        }
        if let current {
            commands[index].version = current.version
            commands[index].baseName = current.name
            commands[index].baseParentId = current.parent
        }
        commands[index].error = nil
        saveCommands()
        revision += 1
        sync()
    }

    /// Gives up a refused change, and takes things as the instance has them.
    func discard(_ problem: OfflineProblem) async {
        if problem.isCommand {
            discardCommand(problem.id)
        } else {
            await discardDocument(problem.id)
        }
    }

    private func discardCommand(_ id: UUID) {
        guard let command = commands.first(where: { $0.id == id }) else { return }
        var dropped: Set<UUID> = [id]
        if command.action == .create {
            // What was made here and never arrived takes with it whatever was then done to it.
            for later in commands where later.targetId == command.targetId || later.noteId == command.targetId {
                dropped.insert(later.id)
            }
            if command.entity == .note || command.entity == .sketch {
                disk.removeDocument(command.targetId)
                documents[command.targetId] = nil
                derived[command.targetId] = nil
                open[command.targetId]?.sync?.discardedHere()
            }
        }
        if command.entity == .image {
            // The picture goes back to the one the instance has, or goes.
            disk.removeImage(command.targetId)
            change(command.vaultId) { $0.images[command.targetId.api] = nil }
        }
        if command.entity == .vault, command.action == .create {
            forget(command.vaultId)
            return
        }
        dropped.forEach(disk.removeCommandData)
        commands.removeAll { dropped.contains($0.id) }
        saveCommands()
        saveDocuments()
        revision += 1
        viewChanged.send(command.vaultId)
        sync()
    }

    private func discardDocument(_ documentId: UUID) async {
        guard let document = documents[documentId], let vault = vault(document.vaultId) else { return }
        disk.remove(documentId, .outbox)
        derived[documentId] = nil
        open[documentId]?.sync?.discardedHere()
        do {
            let read: OfflineRead = try await api.send("GET", "\(path(vault))/documents/\(documentId.api)?after=0")
            let state = YStates.merged(read.updates.map { [UInt8]($0) })
            disk.write(documentId, .server, state)
            disk.write(documentId, .local, state)
            documents[documentId] = OfflineDocument(id: documentId, vaultId: vault.id, seq: read.seq)
        } catch let error as APIError where error.isNotFound {
            disk.removeDocument(documentId)
            documents[documentId] = nil
        } catch {
            // Out of reach: what the instance has is what was last read of it.
            if let server = disk.read(documentId, .server) { disk.write(documentId, .local, server) }
            documents[documentId]?.outbox = nil
            documents[documentId]?.dirty = false
        }
        saveDocuments()
        revision += 1
        viewChanged.send(vault.id)
    }

    // MARK: Taking it away

    /// Everything of a downloaded design doc that is on the device, as a zip (see
    /// OfflineRecovery.swift): what to take before giving up work the instance refused.
    /// `dark` is the paper its sketches' pictures are drawn on.
    func recoveryArchive(_ vaultId: UUID, dark: Bool) async throws -> (name: String, data: Data) {
        guard let vault = vault(vaultId), let base = vault.base else { throw OfflineRefusal.gone }
        let queued = commands.filter { $0.vaultId == vaultId }
        // What the instance no longer has is still what was on the device.
        let source = OfflineTree.projected(OfflineRecovery.restoring(base, vault.recovery), queued)
        let kept = documents.values.filter { $0.vaultId == vaultId }

        var files = OfflineRecovery.notes(of: source) { note in
            disk.read(note.id, .local).map(YStates.markdown) ?? note.markdown
        }
        for image in source.images {
            if let data = disk.readImage(image.id) { files[image.path] = data }
        }
        let colors = dark ? SketchColors.dark : SketchColors.light
        for sketch in source.sketches {
            guard let state = disk.read(sketch.id, .local) else { continue }
            let snapshot = await SketchSnapshot.load([Data(state).base64EncodedString()], sketch: sketch)
            if let drawn = snapshot.raster.image(page: snapshot.page, paper: colors.paper, themeInk: colors.ink),
               let png = PlatformImage.picture(drawn).pngRepresentation {
                files[sketch.path + ".png"] = png
            }
        }

        let record = OfflineRecoveryRecord(
            vault: vault,
            documents: kept.compactMap { document in
                disk.read(document.id, .local).map {
                    .init(id: document.id, vaultId: vaultId, seq: document.seq, deleted: document.deletedOnline, state: Data($0))
                }
            },
            queue: queued.map {
                .init(id: $0.id, vaultId: vaultId, created: $0.created, documentId: nil, data: nil,
                      command: OfflineCommandWire($0, data: $0.hasData ? disk.readCommandData($0.id) : nil), error: $0.error)
            } + kept.filter(owes).compactMap { document in
                // What was being sent, or what would have been: the document less what the instance has.
                let data = disk.read(document.id, .outbox) ?? disk.read(document.id, .local).map {
                    YStates.missing(from: disk.read(document.id, .server) ?? [], in: $0)
                }
                return data.map {
                    .init(id: document.outbox?.id ?? document.id, vaultId: vaultId, created: nil, documentId: document.id,
                          data: Data($0), command: nil, error: document.outbox?.error)
                }
            })
        // Written as it would have been sent, so a command in it is the request the instance never got.
        files["solo-recovery.json"] = try SoloJSON.encoder.encode(record)
        return (OfflineRecovery.archiveName(source.tree.vault.name), try OfflineRecovery.zip(files))
    }

    // MARK: Keeping in step

    /// Reads what has changed on the instance and sends what has changed here. Asked for again
    /// while it is running, it runs once more when it is done.
    func sync() {
        wanted = true
        guard running == nil, vaults.contains(where: \.ready) else { return }
        running = Task { [weak self] in
            while let self, self.wanted, !Task.isCancelled {
                self.wanted = false
                await self.run()
            }
            self?.running = nil
        }
    }

    private func run() async {
        syncing = true
        defer { syncing = false }
        for id in vaults.filter(\.ready).map(\.id) {
            do {
                // One made here is not on the instance to be read until its making has been sent.
                let making = commands.contains { $0.vaultId == id && $0.entity == .vault && $0.action == .create && $0.error == nil }
                if !making { try await pull(id) }
                let sent = try await push(id)
                // Deleted, and so no longer here.
                guard vault(id) != nil else { continue }
                if sent || making { try await pull(id) }
                if try await copyNotes(id) { try await pull(id) }
                reachable = true
            } catch is ConnectionError {
                reachable = false
                revision += 1
                return
            } catch let error as APIError where error.isForbidden || error.isNotFound {
                // Taken away, or deleted: what is here stays, and nothing more is sent.
                change(id) { $0.unavailable = true; $0.canWrite = false }
                for (key, document) in documents where document.vaultId == id {
                    open[key]?.sync?.setKeptWritable(false)
                }
                viewChanged.send(id)
            } catch {
                // Anything else is the instance's trouble of the moment: try again next time.
            }
        }
        revision += 1
    }

    private func path(_ vault: OfflineVault) -> String {
        "api/ext/design-docs/projects/\(vault.projectId.api)/vaults/\(vault.id.api)/offline"
    }

    private func change(_ vaultId: UUID, _ edit: (inout OfflineVault) -> Void) {
        guard let index = vaults.firstIndex(where: { $0.id == vaultId }) else { return }
        edit(&vaults[index])
        saveVaults()
    }

    private func saveVaults() { disk.save("vaults.json", vaults) }
    private func saveDocuments() { disk.save("documents.json", Array(documents.values)) }
    private func saveCommands() { disk.save("commands.json", commands) }

    /// Reads the design doc as the instance has it now: its tree, every document that has
    /// moved on since it was last read, and every picture that is new or changed.
    private func pull(_ vaultId: UUID) async throws {
        guard let vault = vault(vaultId) else { return }
        // Asked of the instance itself, never the cache: an answer from the cache is no answer.
        let manifest: OfflineManifest = try await api.send("GET", path(vault))
        let ids = Set(manifest.documents.keys.compactMap(UUID.init))

        for (key, seq) in manifest.documents {
            guard let id = UUID(uuidString: key) else { continue }
            let kept = documents[id]
            let held = kept != nil && disk.read(id, .local) != nil
            if held, kept?.seq == seq, !(kept?.readonlySeed == true && manifest.canWrite) { continue }
            let after = held && kept?.readonlySeed != true ? kept?.seq ?? "0" : "0"
            var read: OfflineRead = try await api.send("GET", "\(path(vault))/documents/\(id.api)?after=\(after)")
            var readonlySeed = false
            if let seed = read.seedMarkdown {
                // A note that has only ever been imported has no document yet. Whoever may
                // write makes it one, from its text, and everyone has the same one from then on.
                let data = YStates.seeded(seed)
                if manifest.canWrite {
                    do {
                        let _: OfflineWriteResult = try await api.send(
                            "POST", "\(path(vault))/documents/\(id.api)",
                            body: OfflineWrite(id: UUID(), data: Data(data), seedMarkdown: seed))
                    } catch let error as APIError where error.isConflict {
                        // Someone else made it first.
                    }
                    read = try await api.send("GET", "\(path(vault))/documents/\(id.api)?after=0")
                } else {
                    read.updates = [Data(data)]
                    readonlySeed = true
                }
            }
            let updates = read.updates.map { [UInt8]($0) }
            if kept?.readonlySeed == true, !readonlySeed {
                // What was shown was made from the text: the real document takes its place.
                let state = YStates.merged(updates)
                disk.write(id, .server, state)
                disk.write(id, .local, state)
                open[id]?.sync?.discardedHere()
            } else if !updates.isEmpty || !held {
                disk.write(id, .server, YStates.merged([disk.read(id, .server) ?? []] + updates))
                if let editor = open[id]?.sync {
                    editor.absorb(updates)
                    disk.write(id, .local, editor.shared.fullState())
                } else {
                    disk.write(id, .local, YStates.merged([disk.read(id, .local) ?? []] + updates))
                }
            }
            derived[id] = nil
            var document = documents[id] ?? OfflineDocument(id: id, vaultId: vaultId)
            document.seq = read.seq
            document.deletedOnline = false
            document.readonlySeed = readonlySeed
            documents[id] = document
        }

        // What is no longer on the instance — and was not made here and is yet to get there.
        // With nothing unsent it goes; with something, it stays, for whoever wrote it to decide about.
        let queued = commands.filter { $0.vaultId == vaultId }
        for (id, document) in documents where document.vaultId == vaultId && !ids.contains(id) {
            if queued.contains(where: { $0.action == .create && $0.targetId == id }) { continue }
            if owes(document) {
                documents[id]?.deletedOnline = true
                documents[id]?.dirty = false
                if documents[id]?.outbox == nil, let local = disk.read(id, .local) {
                    disk.write(id, .outbox, YStates.missing(from: disk.read(id, .server) ?? [], in: local))
                    documents[id]?.outbox = .init(id: UUID())
                }
                documents[id]?.outbox?.error = "It was deleted on the instance. What was written here is kept until you discard it."
                open[id]?.sync?.setKeptWritable(false)
            } else {
                // Kept, as the web keeps it: what was read of it is still on the device, and
                // goes in an archive of the design doc. It goes when the download does.
                documents[id]?.deletedOnline = true
                derived[id] = nil
            }
        }
        saveDocuments()

        // A picture with something still to send about it is as it is here, not as it is there.
        var images = vault.images
        let changing = Set(queued.filter { $0.entity == .image }.map(\.targetId))
        for image in manifest.images where images[image.id.api] != image.version && !changing.contains(image.id) {
            disk.writeImage(image.id, try await api.docImageData(vault.projectId, vault.id, image.id))
            images[image.id.api] = image.version
        }
        let current = Set(manifest.images.map(\.id.api))
        for key in images.keys where !current.contains(key) {
            guard let id = UUID(uuidString: key), !changing.contains(id) else { continue }
            disk.removeImage(id)
            images[key] = nil
        }

        let signature = manifest.signature
        let stale = vault.signature != signature
        let aside = vault.base.flatMap { OfflineRecovery.settingAside($0, gone: manifest.base, into: vault.recovery) } ?? vault.recovery
        change(vaultId) {
            $0.recovery = aside
            $0.name = manifest.tree.vault.name
            $0.canWrite = manifest.canWrite
            $0.unavailable = false
            $0.base = manifest.base
            $0.images = images
            $0.signature = signature
        }
        let view = view(vaultId)
        for (key, document) in documents where document.vaultId == vaultId {
            open[key]?.sync?.setKeptWritable((view?.canWrite ?? manifest.canWrite) && !document.deletedOnline && !document.readonlySeed)
        }

        // The rest of the app reads a design doc through its ordinary requests in a few
        // places — the list of design docs, a link to a note, what embeds a picture — whose
        // last answers are kept and stand in when the instance is out of reach. So those are
        // asked again whenever the design doc has changed, and what they then keep is the
        // design doc as it was downloaded.
        if stale {
            _ = try? await api.docTree(vault.projectId, vault.id)
            _ = try? await api.docImages(vault.projectId, vault.id)
            _ = try? await api.docSketches(vault.projectId, vault.id)
            _ = try? await api.docAttachments(vault.projectId, vault.id)
            _ = try? await api.docVaults(vault.projectId)
            _ = try? await api.docColorSlots(vault.projectId)
            revision += 1
            viewChanged.send(vaultId)
        }
    }

    /// Sends what is owed. First what was done to the tree, in the order it was done, since
    /// a note has to be there before what is written in it can be; then each document's
    /// changes. Everything goes under an id that is kept until it is answered, so one that got
    /// there and was never heard of is not sent as another. Returns whether anything was sent.
    private func push(_ vaultId: UUID) async throws -> Bool {
        guard let vault = vault(vaultId), !vault.unavailable else { return false }
        var sent = false

        for id in commands.filter({ $0.vaultId == vaultId && $0.error == nil }).map(\.id) {
            guard let command = commands.first(where: { $0.id == id }), command.error == nil else { continue }
            var data: Data?
            if command.hasData {
                data = disk.readCommandData(id)
                if data == nil {
                    refuse(id, "The picture is no longer on this device.")
                    continue
                }
            }
            do {
                let result = try await send(command, data: data, to: vault)
                sent = true
                acknowledge(command, result)
                if command.entity == .vault, command.action == .delete { return true }
            } catch let error as APIError where Self.refusals.contains(error.status) {
                refuse(id, error.errorDescription ?? "The instance refused this change.")
            }
        }

        guard let base = self.vault(vaultId)?.base, base.canWrite else { return sent }
        // Only what the instance has: a note made here whose making was refused is not there to write in.
        let there = Set(base.tree.notes.map(\.id) + base.sketches.map(\.id))
        for id in documents.values.filter({ $0.vaultId == vaultId }).map(\.id) where there.contains(id) {
            guard let document = documents[id], document.outbox?.error == nil, !document.deletedOnline else { continue }
            if document.outbox == nil {
                guard document.dirty, let local = disk.read(id, .local) else { continue }
                let data = YStates.missing(from: disk.read(id, .server) ?? [], in: local)
                documents[id]?.dirty = false
                // An update of two bytes says nothing.
                if data.count > 2 {
                    disk.write(id, .outbox, data)
                    documents[id]?.outbox = .init(id: UUID())
                }
                saveDocuments()
            }
            guard let box = documents[id]?.outbox, let data = disk.read(id, .outbox) else { continue }
            do {
                let result: OfflineWriteResult = try await api.send(
                    "POST", "\(path(vault))/documents/\(id.api)", body: OfflineWrite(id: box.id, data: Data(data)))
                sent = true
                disk.write(id, .server, YStates.merged([disk.read(id, .server) ?? [], data]))
                disk.remove(id, .outbox)
                documents[id]?.outbox = nil
                // Only when nothing else was written in between has everything up to it been read.
                if documents[id]?.seq == result.previousSeq { documents[id]?.seq = result.seq }
                if base.tree.notes.contains(where: { $0.id == id }) { documents[id]?.copyOwed = result.seq }
                moved(id, result)
            } catch let error as APIError where Self.refusals.contains(error.status) {
                documents[id]?.outbox?.error = error.errorDescription ?? "The instance refused this change."
            }
            saveDocuments()
            revision += 1
        }
        return sent
    }

    private func refuse(_ id: UUID, _ message: String) {
        guard let index = commands.firstIndex(where: { $0.id == id }) else { return }
        commands[index].error = message
        saveCommands()
        revision += 1
    }

    private func send(_ command: OfflineCommand, data: Data?, to vault: OfflineVault) async throws -> OfflineResult? {
        let path = "\(path(vault))/commands"
        let wire = OfflineCommandWire(command, data: data)
        if command.action == .delete {
            try await api.send("POST", path, body: wire)
            return nil
        }
        switch command.entity {
        case .folder: return .folder(try await api.send("POST", path, body: wire))
        case .note: return .note(try await api.send("POST", path, body: wire))
        case .sketch: return .sketch(try await api.send("POST", path, body: wire))
        case .image: return .image(try await api.send("POST", path, body: wire))
        case .vault: return .vault(try await api.send("POST", path, body: wire))
        }
    }

    /// A command has been done on the instance: it comes off the list, the design doc as the
    /// instance has it takes it on, and what is still to send about the same thing is told
    /// what the thing has become — its new version, its name if the instance had to change it.
    private func acknowledge(_ command: OfflineCommand, _ result: OfflineResult?) {
        commands.removeAll { $0.id == command.id }
        disk.removeCommandData(command.id)

        // Stored somewhere other than expected, because the name was taken there meanwhile:
        // the notes that embed it by where it is are pointed at where it is.
        if command.action == .create, let stored = result?.path,
           let expected = command.entity == .sketch ? command.path : command.image?.path, expected != stored {
            repointEmbeds(in: command.vaultId, note: command.noteId, from: expected, to: stored)
        }

        if command.entity == .vault, command.action == .delete {
            forget(command.vaultId)
            return
        }
        // An editor opened on it before the instance had it was told nothing when it asked.
        if command.action == .create, command.entity == .note || command.entity == .sketch {
            open[command.targetId]?.sync?.reachedInstance()
        }
        change(command.vaultId) { vault in
            guard var base = vault.base else { return }
            OfflineTree.apply(command, to: &base, result: result)
            vault.base = base
            vault.name = base.tree.vault.name
            if command.entity == .image {
                vault.images[command.targetId.api] = command.action == .delete ? nil : result?.version
            }
        }

        if let result {
            for index in commands.indices where commands[index].baseVersions?[command.targetId.api] != nil {
                commands[index].baseVersions?[command.targetId.api] = result.version
            }
            if let next = commands.firstIndex(where: { $0.targetId == command.targetId }) {
                if commands[next].name == command.name, let name = result.name { commands[next].name = name }
                if commands[next].image != nil, let path = result.path { commands[next].image?.path = path }
                commands[next].version = result.version
                commands[next].baseName = result.name
                commands[next].baseParentId = result.parentId
            }
        }

        if command.action == .delete {
            if command.entity == .image { disk.removeImage(command.targetId) }
            // What went with it. Anything written in those and never sent goes too: it was
            // deleted here, by whoever wrote it.
            let base = vault(command.vaultId)?.base
            let there = Set((base?.tree.notes.map(\.id) ?? []) + (base?.sketches.map(\.id) ?? []))
            for (id, document) in documents where document.vaultId == command.vaultId && !there.contains(id) {
                if commands.contains(where: { $0.action == .create && $0.targetId == id }) || document.deletedOnline { continue }
                // Still on the device, for an archive, and no longer anything to send.
                disk.remove(id, .outbox)
                documents[id]?.outbox = nil
                documents[id]?.dirty = false
                documents[id]?.copyOwed = nil
                documents[id]?.deletedOnline = true
                derived[id] = nil
                open[id]?.sync?.discardedHere()
            }
        }
        saveCommands()
        saveDocuments()
        revision += 1
        viewChanged.send(command.vaultId)
    }

    /// A document's changes arrived. A sketch's row moves on with them, so whatever is still
    /// to send that names the sketch's version, or how far the document had been read, is
    /// told the new ones — where it named the old.
    private func moved(_ documentId: UUID, _ result: OfflineWriteResult) {
        let key = documentId.api
        var changed = false
        for index in commands.indices {
            if commands[index].baseDocuments?[key] == result.previousSeq {
                commands[index].baseDocuments?[key] = result.seq
                changed = true
            }
            guard let previous = result.previousVersion, let version = result.version else { continue }
            if commands[index].baseVersions?[key] == previous {
                commands[index].baseVersions?[key] = version
                changed = true
            }
            if commands[index].targetId == documentId, commands[index].version == previous {
                commands[index].version = version
                changed = true
            }
        }
        if changed { saveCommands() }
    }

    /// Changes every embed of one stored path into another, in a note or in every note of a
    /// design doc. A note that is open is changed where it is open; the rest on the device.
    private func repointEmbeds(in vaultId: UUID, note: UUID?, from old: String, to new: String) {
        let notes = note.map { [$0] } ?? vault(vaultId)?.base?.tree.notes.map(\.id) ?? []
        for id in notes {
            guard documents[id] != nil, let local = disk.read(id, .local) else { continue }
            if let editor = open[id]?.sync {
                let edits = OfflineTree.embedEdits(in: editor.shared.string(), from: old, to: new)
                guard !edits.isEmpty else { continue }
                edits.forEach { editor.shared.replaceText(start: $0.start, length: $0.length, with: $0.text) }
                editor.changedOutsideEditor()
            } else {
                let doc = SharedDoc()
                doc.apply([local])
                let edits = OfflineTree.embedEdits(in: doc.string(), from: old, to: new)
                guard !edits.isEmpty else { continue }
                edits.forEach { doc.replaceText(start: $0.start, length: $0.length, with: $0.text) }
                disk.write(id, .local, doc.fullState())
                documents[id]?.dirty = true
                derived[id] = nil
            }
        }
    }

    /// A note's saved copy — its text as the tree, links and attachments read it — follows
    /// what was written here, once that has been sent. Returns whether any was saved, since
    /// saving one moves the note's row on.
    private func copyNotes(_ vaultId: UUID) async throws -> Bool {
        guard let vault = vault(vaultId), vault.canWrite, !vault.unavailable else { return false }
        var saved = false
        for id in documents.values.filter({ $0.vaultId == vaultId && $0.copyOwed != nil }).map(\.id) {
            guard let document = documents[id], !owes(document), let seq = document.copyOwed,
                  let local = disk.read(id, .local) else { continue }
            do {
                try await api.saveDocCopy(vault.projectId, vault.id, id, markdown: YStates.markdown(local), seq: seq)
                saved = true
            } catch let error as APIError where Self.refusals.contains(error.status) {
                // Someone else's copy is newer, or the note has gone: either way it is not owed.
            }
            documents[id]?.copyOwed = nil
            derived[id] = nil
            saveDocuments()
        }
        return saved
    }
}
