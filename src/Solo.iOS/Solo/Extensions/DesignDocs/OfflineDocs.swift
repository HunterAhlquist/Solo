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
//
// What the web's has and this has not: creating, renaming, moving and deleting notes,
// folders, sketches and pictures while out of reach — those still need the instance — and
// the recovery archive.

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

/// A downloaded design doc, as it was last told.
nonisolated struct OfflineVault: Codable, Identifiable, Equatable, Sendable {
    var id: UUID
    var projectId: UUID
    var projectAlias: String
    var name: String
    /// False until everything in it has arrived once.
    var ready = false
    var canWrite = false
    /// The instance no longer lets this person at it, or it is gone. What is kept stays.
    var unavailable = false
    /// Every note and sketch it holds, by id, with its name.
    var names: [String: String] = [:]
    var notes: [String] = []
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
}

nonisolated private struct OfflineManifest: Decodable, Sendable {
    var tree: DocTree
    var sketches: [Row]
    var images: [DocImage]
    var documents: [String: String]
    var canWrite: Bool

    nonisolated struct Row: Decodable, Sendable {
        var id: UUID
        var name: String
        var version: UInt32
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
}

/// The files. Nothing here is large enough, or read often enough, to need more than that.
nonisolated private struct OfflineDisk: Sendable {
    let root: URL

    init(scope: String) {
        var hash: UInt64 = 0xcbf2_9ce4_8422_2325
        for byte in scope.utf8 { hash = (hash ^ UInt64(byte)) &* 0x100_0000_01b3 }
        root = URL.applicationSupportDirectory.appending(path: "OfflineDocs/\(String(hash, radix: 16))", directoryHint: .isDirectory)
        for folder in ["documents", "images"] {
            try? FileManager.default.createDirectory(at: root.appending(path: folder), withIntermediateDirectories: true)
        }
    }

    enum Part: String { case local, server, outbox }

    private func file(_ id: UUID, _ part: Part) -> URL { root.appending(path: "documents/\(id.api).\(part.rawValue)") }
    private func image(_ id: UUID) -> URL { root.appending(path: "images/\(id.api)") }

    func read(_ id: UUID, _ part: Part) -> [UInt8]? { (try? Data(contentsOf: file(id, part))).map { [UInt8]($0) } }
    func write(_ id: UUID, _ part: Part, _ bytes: [UInt8]) { try? Data(bytes).write(to: file(id, part), options: .atomic) }
    func remove(_ id: UUID, _ part: Part) { try? FileManager.default.removeItem(at: file(id, part)) }
    func removeDocument(_ id: UUID) { [Part.local, .server, .outbox].forEach { remove(id, $0) } }

    func readImage(_ id: UUID) -> Data? { try? Data(contentsOf: image(id)) }
    func writeImage(_ id: UUID, _ data: Data) { try? data.write(to: image(id), options: .atomic) }
    func removeImage(_ id: UUID) { try? FileManager.default.removeItem(at: image(id)) }

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

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private let disk: OfflineDisk
    @ObservationIgnored private var documents: [UUID: OfflineDocument]
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
    }

    // MARK: Reading

    func isDownloaded(_ vaultId: UUID) -> Bool {
        vaults.contains { $0.id == vaultId && $0.ready }
    }

    func vault(_ id: UUID) -> OfflineVault? {
        vaults.first { $0.id == id }
    }

    private func owes(_ document: OfflineDocument) -> Bool { document.dirty || document.outbox != nil }

    /// How many documents of a design doc have changes the instance has not got.
    func pending(in vaultId: UUID) -> Int {
        _ = revision
        return documents.values.filter { $0.vaultId == vaultId && owes($0) }.count
    }

    func isPending(_ documentId: UUID) -> Bool {
        _ = revision
        return documents[documentId].map(owes) ?? false
    }

    func problems(in vaultId: UUID) -> [OfflineProblem] {
        _ = revision
        let names = vault(vaultId)?.names ?? [:]
        return documents.values
            .filter { $0.vaultId == vaultId && $0.outbox?.error != nil }
            .map { OfflineProblem(id: $0.id, name: names[$0.id.api] ?? "A document", message: $0.outbox?.error ?? "") }
            .sorted { $0.name < $1.name }
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
        guard let vault = vault(vaultId) else { return nil }
        for (id, document) in documents where document.vaultId == vaultId {
            disk.removeDocument(id)
            documents[id] = nil
            // An editor that has it open goes back to saving straight to the instance.
            open[id]?.sync?.leaveDevice()
            open[id] = nil
        }
        vault.images.keys.compactMap(UUID.init).forEach(disk.removeImage)
        vaults.removeAll { $0.id == vaultId }
        saveVaults()
        saveDocuments()
        revision += 1
        return nil
    }

    // MARK: An open document

    /// A document being opened in an editor. Answers with how it stands here, and whether it
    /// may be written in, when it is one of a downloaded design doc's.
    func opened(_ sync: DocSync, documentId: UUID, vaultId: UUID) -> (state: [UInt8], seq: Int64, canWrite: Bool)? {
        guard let vault = vault(vaultId), vault.ready, let document = documents[documentId],
              let state = disk.read(documentId, .local) else { return nil }
        open[documentId] = Weak(sync: sync)
        return (state, Int64(document.seq) ?? 0,
                vault.canWrite && !vault.unavailable && !document.deletedOnline && !document.readonlySeed)
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
        saveDocuments()
        revision += 1
        sync()
    }

    /// Changes that came over the live connection: the instance has those, and now so does this.
    func received(_ documentId: UUID, updates: [[UInt8]], state: [UInt8]) {
        guard documents[documentId] != nil else { return }
        disk.write(documentId, .server, YStates.merged([disk.read(documentId, .server) ?? []] + updates))
        disk.write(documentId, .local, state)
    }

    // MARK: What was refused

    /// Sends a refused change again, as it is.
    func retry(_ documentId: UUID) {
        documents[documentId]?.outbox?.error = nil
        saveDocuments()
        revision += 1
        sync()
    }

    /// Gives up what was written here and never sent, and takes the document as the instance has it.
    func discard(_ documentId: UUID) async {
        guard let document = documents[documentId], let vault = vault(document.vaultId) else { return }
        disk.remove(documentId, .outbox)
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
                try await pull(id)
                try await push(id)
                try await copyNotes(id)
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
            var document = documents[id] ?? OfflineDocument(id: id, vaultId: vaultId)
            document.seq = read.seq
            document.deletedOnline = false
            document.readonlySeed = readonlySeed
            documents[id] = document
        }

        // What is no longer on the instance. With nothing unsent it goes; with something, it
        // stays, for whoever wrote it to decide about.
        for (id, document) in documents where document.vaultId == vaultId && !ids.contains(id) {
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
                disk.removeDocument(id)
                documents[id] = nil
            }
        }
        saveDocuments()

        var images = vault.images
        for image in manifest.images where images[image.id.api] != image.version {
            disk.writeImage(image.id, try await api.docImageData(vault.projectId, vault.id, image.id))
            images[image.id.api] = image.version
        }
        let current = Set(manifest.images.map(\.id.api))
        for key in images.keys where !current.contains(key) {
            UUID(uuidString: key).map(disk.removeImage)
            images[key] = nil
        }

        var names: [String: String] = [:]
        manifest.tree.notes.forEach { names[$0.id.api] = $0.name }
        manifest.sketches.forEach { names[$0.id.api] = $0.name }
        let signature = manifest.signature
        let stale = vault.signature != signature
        change(vaultId) {
            $0.name = manifest.tree.vault.name
            $0.canWrite = manifest.canWrite
            $0.unavailable = false
            $0.names = names
            $0.notes = manifest.tree.notes.map(\.id.api)
            $0.images = images
            $0.signature = signature
        }
        for (key, document) in documents where document.vaultId == vaultId {
            open[key]?.sync?.setKeptWritable(manifest.canWrite && !document.deletedOnline && !document.readonlySeed)
        }

        // The app reads a design doc through its ordinary requests, whose last answers are
        // kept and stand in when the instance is out of reach. So those are asked again
        // whenever the design doc has changed, and what they then keep is the design doc as
        // it was downloaded. A sketch's picture is drawn from its stored document, which is
        // one of those answers too.
        if stale {
            _ = try? await api.docTree(vault.projectId, vault.id)
            _ = try? await api.docImages(vault.projectId, vault.id)
            _ = try? await api.docSketches(vault.projectId, vault.id)
            _ = try? await api.docAttachments(vault.projectId, vault.id)
            _ = try? await api.docVaults(vault.projectId)
            _ = try? await api.docColorSlots(vault.projectId)
        }
    }

    /// Sends what is owed, a document at a time. Each update is kept under an id until it is
    /// answered, so one that got there and was never heard of is not sent as another.
    private func push(_ vaultId: UUID) async throws {
        guard let vault = vault(vaultId), vault.canWrite, !vault.unavailable else { return }
        for id in documents.values.filter({ $0.vaultId == vaultId }).map(\.id) {
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
                disk.write(id, .server, YStates.merged([disk.read(id, .server) ?? [], data]))
                disk.remove(id, .outbox)
                documents[id]?.outbox = nil
                // Only when nothing else was written in between has everything up to it been read.
                if documents[id]?.seq == result.previousSeq { documents[id]?.seq = result.seq }
                if vault.notes.contains(id.api) { documents[id]?.copyOwed = result.seq }
            } catch let error as APIError where Self.refusals.contains(error.status) {
                documents[id]?.outbox?.error = error.errorDescription ?? "The instance refused this change."
            }
            saveDocuments()
            revision += 1
        }
    }

    /// A note's saved copy — its text as the tree, links and attachments read it — follows
    /// what was written here, once that has been sent.
    private func copyNotes(_ vaultId: UUID) async throws {
        guard let vault = vault(vaultId), vault.canWrite, !vault.unavailable else { return }
        for id in documents.values.filter({ $0.vaultId == vaultId && $0.copyOwed != nil }).map(\.id) {
            guard let document = documents[id], !owes(document), let seq = document.copyOwed,
                  let local = disk.read(id, .local) else { continue }
            do {
                try await api.saveDocCopy(vault.projectId, vault.id, id, markdown: YStates.markdown(local), seq: seq)
            } catch let error as APIError where Self.refusals.contains(error.status) {
                // Someone else's copy is newer, or the note has gone: either way it is not owed.
            }
            documents[id]?.copyOwed = nil
            saveDocuments()
        }
    }
}
