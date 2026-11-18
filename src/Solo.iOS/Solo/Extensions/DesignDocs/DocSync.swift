import Foundation
import Observation
import YSwift

/// A shared Yjs document and the handful of things the app does to one.
///
/// Its own type, and nonisolated, because YSwift runs every transaction on a
/// queue of the document's: a closure written inside main-actor code would be
/// checked for the main actor when that queue called it.
nonisolated final class SharedDoc: @unchecked Sendable {
    let document = YDocument()
    let text: YText

    init() {
        text = document.getOrCreateText(named: "markdown")
    }

    func string() -> String {
        let text = text
        return document.transactSync { text.getString(in: $0) }
    }

    func stateVector() -> [UInt8] {
        document.transactSync { $0.transactionStateVector() }
    }

    /// Everything the document has that `vector` has not seen, as one update.
    func update(since vector: [UInt8]) -> [UInt8] {
        document.transactSync { (try? $0.transactionEncodeStateAsUpdateFromSv(stateVector: vector)) ?? [] }
    }

    func fullState() -> [UInt8] {
        document.transactSync { $0.transactionEncodeStateAsUpdate() }
    }

    @discardableResult
    func apply(_ updates: [[UInt8]]) -> Bool {
        document.transactSync { transaction in
            var applied = true
            for update in updates {
                do { try transaction.transactionApplyUpdate(update: update) } catch { applied = false }
            }
            return applied
        }
    }

    /// Replaces `length` UTF-16 units at `start` with `inserted`. Offsets are
    /// UTF-16, as Yjs counts them in every client and as a text view does.
    func replaceText(start: Int, length: Int, with inserted: String) {
        let text = text
        document.transactSync { transaction in
            if length > 0 {
                text.removeRange(start: UInt32(start), length: UInt32(length), in: transaction)
            }
            if !inserted.isEmpty {
                text.insert(inserted, at: UInt32(start), in: transaction)
            }
        }
    }
}

/// Someone else's caret in a note: who, and where their selection starts and ends.
struct NoteCaret: Equatable {
    var client: UInt64
    var user: AwarenessUser
    var anchor: Int
    var head: Int
}

/// Keeps one note or sketch in step with the instance over its live topic.
///
/// The server stores the document as a log of Yjs updates and relays each new
/// one to everyone else on the topic. A client asks for what it is missing
/// (`sync`), applies what comes back, and sends its own edits as updates that
/// stay pending until the server acknowledges each by id — so an edit made
/// while the connection is down is sent again, not lost.
@Observable
final class DocSync {
    /// What to say of where the document's changes have got to.
    var statusText: String {
        kept ? session.docsOffline.status(of: documentId, unsaved: unsaved) : liveStatus
    }

    private var liveStatus = "Connecting…"
    /// True while the document is one of a downloaded design doc's (OfflineDocs.swift). Then
    /// what is written is kept on the device and owed to the instance, which the download
    /// pays: nothing written here is sent over the live connection, though what others write
    /// still arrives over it. It can be written in with the instance out of reach.
    private(set) var kept = false
    /// Changed here and not yet kept on the device: for the status, which a view reads.
    private var unsaved = false
    private(set) var canWrite = false
    private(set) var synced = false
    private(set) var deleted = false
    /// The text a reader is shown when nobody able to write has opened the note since it was imported.
    private(set) var unseededMarkdown: String?
    /// Bumped whenever someone else's change lands, for whatever is showing the document.
    private(set) var remoteRevision = 0
    private(set) var present: [TopicPresenceEntry] = []
    /// Each collaborator's colour slot in the project: the colour of their caret, and of the ring round their face.
    private(set) var colorSlots: [UUID: Int] = [:]

    @ObservationIgnored let shared = SharedDoc()
    /// Who else is in the document and where: their carets in a note, their pens on a sketch.
    @ObservationIgnored let awareness = Awareness()
    /// Called with the stroke-in-progress this client's state carried, once that state has gone out.
    @ObservationIgnored var onAwarenessSent: (([String: Any]?) -> Void)?
    /// Called when someone new arrives, who has seen none of what was sent before.
    @ObservationIgnored var onPeerJoined: (() -> Void)?
    @ObservationIgnored private let session: Session
    @ObservationIgnored private let projectId: UUID
    @ObservationIgnored let documentId: UUID
    @ObservationIgnored private let topic: String
    @ObservationIgnored private let mayWrite: Bool
    /// The design doc the document is in, when whoever opened it said.
    @ObservationIgnored private let vaultId: UUID?
    /// Called a little after an edit has been saved, with the text and the sequence it is current to.
    @ObservationIgnored var onSavedCopy: ((String, String) async -> Void)?

    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var chunks: [String: [String?]] = [:]
    /// The replay so far, held until the instance says that was all of it.
    @ObservationIgnored private var replay: [[UInt8]] = []
    @ObservationIgnored private var seq: Int64 = 0
    @ObservationIgnored private var checkpoint: Int64 = 0
    @ObservationIgnored private var syncId = UUID()
    @ObservationIgnored private var seedToken: UUID?
    @ObservationIgnored private var sentVector: [UInt8] = []
    @ObservationIgnored private var dirty = false
    @ObservationIgnored private var pending: Packet?
    @ObservationIgnored private var flushTask: Task<Void, Never>?
    @ObservationIgnored private var resendTask: Task<Void, Never>?
    @ObservationIgnored private var copyTask: Task<Void, Never>?
    @ObservationIgnored private var retryTask: Task<Void, Never>?
    @ObservationIgnored private var copyWanted = false
    @ObservationIgnored private var edited = false
    @ObservationIgnored private var started = false
    @ObservationIgnored private var awarenessTask: Task<Void, Never>?
    @ObservationIgnored private var heartbeat: Task<Void, Never>?
    @ObservationIgnored private var presentIds: Set<UUID> = []
    @ObservationIgnored private var color: String?
    /// The text's structure, read out of the document the last time it was asked for after a change.
    @ObservationIgnored private var structure: (version: Int, value: YjsTextStructure?)?
    @ObservationIgnored private var documentVersion = 0

    private struct Packet {
        var id = UUID()
        var data: String
        var seedToken: UUID?
    }

    private static let chunkLength = 32_000

    init(session: Session, projectId: UUID, documentId: UUID, mayWrite: Bool, vaultId: UUID? = nil) {
        self.session = session
        self.projectId = projectId
        self.documentId = documentId
        self.mayWrite = mayWrite
        self.vaultId = vaultId
        topic = DocTopics.doc(documentId)
    }

    var hasPending: Bool {
        kept ? dirty || session.docsOffline.isPending(documentId) : pending != nil || dirty
    }

    // MARK: Lifecycle

    func start() async {
        guard !started else { return }
        started = true
        let hub = session.hub
        subscriptions = [
            hub.topicMessage.subscribe { [weak self] message in
                guard let self, message.topic == self.topic, message.projectId == self.projectId else { return }
                self.receive(message.kind, message.data)
            },
            hub.topicPresenceChanged.subscribe { [weak self] snapshot in
                guard let self, snapshot.topic == self.topic, snapshot.projectId == self.projectId else { return }
                self.present = snapshot.entries.filter { $0.userId != self.session.user.id }
                self.presenceChanged(Set(snapshot.entries.map(\.userId)))
            },
            hub.reconnected.subscribe { [weak self] in self?.connect() },
        ]
        awareness.onLocalChange = { [weak self] in self?.scheduleAwareness() }
        // A downloaded design doc's document is shown from the device at once, before the
        // instance has said anything, and whether or not it ever does.
        session.docsOffline.editing(self)
        keepOnDevice()
        await hub.subscribe(projectId: projectId, topic: topic)
        await hub.setTopicPresence(projectId: projectId, topic: topic, .viewing)
        connect()
        if mayWrite {
            // The same colour the web shows this person in. Without it they are still shown, in the default.
            color = (try? await session.api.docColorSlot(projectId)).map(PresenceColor.css)
            if canWrite { announce() }
        }
        await loadColorSlots()
        heartbeat = Task { [weak self] in
            // A state lasts thirty seconds unheard; said again every fifteen, it lasts.
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(15))
                guard let self, !Task.isCancelled else { return }
                self.awareness.keep(userIds: self.presentIds)
                if self.awareness.hasLocalState {
                    self.awareness.renew()
                    await self.sendAwareness()
                }
            }
        }
    }

    // MARK: Awareness

    /// This person, as the others' cursors describe them.
    private func announce() {
        let user = session.user
        var description: [String: Any] = ["userId": user.id.api, "name": user.name]
        if let color { description["color"] = color }
        awareness.setUser(description)
    }

    private func loadColorSlots() async {
        guard let slots = try? await session.api.docColorSlots(projectId) else { return }
        colorSlots = Dictionary(slots.compactMap { slot in slot.userId.map { ($0, slot.slot) } }) { first, _ in first }
    }

    private func presenceChanged(_ ids: Set<UUID>) {
        let joined = !ids.subtracting(presentIds).subtracting([session.user.id]).isEmpty
        presentIds = ids
        awareness.keep(userIds: ids)
        // Someone whose colour isn't known yet has only just been given one.
        if ids.contains(where: { colorSlots[$0] == nil }) {
            Task { await loadColorSlots() }
        }
        // Someone new has heard nothing yet: say it all again.
        if joined, canWrite {
            onPeerJoined?()
            awareness.renew()
            scheduleAwareness()
        }
    }

    /// Gathers changes for a moment, so a pen moving sends a few states a second rather than hundreds.
    private func scheduleAwareness() {
        guard awarenessTask == nil else { return }
        awarenessTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(60))
            guard let self else { return }
            self.awarenessTask = nil
            await self.sendAwareness()
        }
    }

    private func sendAwareness() async {
        guard started, session.hub.isConnected else { return }
        let stroke = awareness.field("stroke") as? [String: Any]
        let body = ["data": Data(awareness.message()).base64EncodedString()]
        let data = (try? JSONSerialization.data(withJSONObject: body)).map { String(decoding: $0, as: UTF8.self) } ?? "{}"
        // Presence is disposable: one that doesn't get through is simply superseded by the next.
        let result = await session.hub.sendTopicMessage(projectId: projectId, topic: topic, kind: "awareness", data: data)
        if result == .accepted { onAwarenessSent?(stroke) }
    }

    // MARK: Carets

    /// The note's text as Yjs holds it: every character with its identity. Nil when it can't be
    /// read, or doesn't read back as the text the document says it is — then no caret is placed.
    private func textStructure() -> YjsTextStructure? {
        if let structure, structure.version == documentVersion { return structure.value }
        var value = YjsTextStructure(update: shared.fullState())
        if value?.text != shared.string() { value = nil }
        structure = (documentVersion, value)
        return value
    }

    /// Where everyone else's carets and selections are in the note's text, as UTF-16 offsets.
    func carets() -> [NoteCaret] {
        _ = awareness.revision
        let candidates = awareness.peers.values.filter { $0.head != nil }
        guard !candidates.isEmpty, let structure = textStructure() else { return [] }
        return candidates.compactMap { peer in
            func offset(_ position: YjsRelativePosition?) -> Int? {
                guard let position else { return nil }
                guard let item = position.item else { return structure.length }
                return structure.index(of: item)
            }
            guard let head = offset(peer.head) else { return nil }
            return NoteCaret(client: peer.client, user: peer.user, anchor: offset(peer.anchor) ?? head, head: head)
        }
        .sorted { $0.client < $1.client }
    }

    /// Tells the others where this caret is; nil when the note isn't being typed in.
    func publishCaret(_ selection: NSRange?) {
        guard canWrite, awareness.hasLocalState else { return }
        guard let selection, let structure = textStructure(), NSMaxRange(selection) <= structure.length else {
            awareness.set("cursor", nil)
            return
        }
        let anchor = YjsRelativePosition(item: structure.id(at: selection.location))
        let head = YjsRelativePosition(item: structure.id(at: NSMaxRange(selection)))
        awareness.set("cursor", ["anchor": anchor.json, "head": head.json])
    }

    // MARK: Kept on the device

    /// Takes the document up from the device, where its design doc is downloaded. Asked when
    /// it is opened, and again whenever something is written or the connection is lost, since
    /// the download may have been made while it was open.
    func keepOnDevice() {
        guard started, !kept, !deleted, let vaultId,
              let here = session.docsOffline.opened(self, documentId: documentId, vaultId: vaultId) else { return }
        kept = true
        documentVersion += 1
        if shared.apply([here.state]) { remoteRevision += 1 }
        // Anything written and not yet sent stays owed: it is kept, and sent, with the next change.
        if !dirty { sentVector = shared.stateVector() }
        // What the instance is asked for is only what has come since this was last read.
        seq = max(seq, here.seq)
        checkpoint = max(checkpoint, here.seq)
        synced = true
        if !session.hub.isConnected { setWritable(here.canWrite) }
    }

    /// Changes the download read from the instance, for the open document to take as it
    /// takes anyone else's.
    func absorb(_ updates: [[UInt8]]) {
        apply(updates, fromDevice: true)
    }

    /// The download says whether the design doc may be written in, having asked the instance.
    func setKeptWritable(_ value: Bool) {
        guard kept, !session.hub.isConnected || !value else { return }
        setWritable(value)
    }

    /// The download was removed: from here on the document is saved straight to the instance.
    func leaveDevice() {
        guard kept else { return }
        kept = false
        connect()
    }

    /// What was written here and never sent has been given up, or the document replaced by
    /// the one the instance has. An open document cannot unwrite what it holds, so it stops
    /// taking changes until it is opened again.
    func discardedHere() {
        dirty = false
        unsaved = false
        deleted = true
        setWritable(false)
        liveStatus = "Changes discarded · reopen to continue"
        kept = false
    }

    func stop() async {
        guard started else { return }
        started = false
        flushNow()
        session.docsOffline.closed(documentId)
        // What was just sent gets a moment to be acknowledged, so the saved
        // copy the tree and links read can go with it rather than wait for
        // the next person to open the note.
        var waited = 0
        while pending != nil, session.hub.isConnected, waited < 20 {
            try? await Task.sleep(for: .milliseconds(100))
            waited += 1
        }
        copyTask?.cancel()
        // A downloaded design doc's copies are the download's to save, once it has sent the text.
        if copyWanted, !kept, !hasPending, canWrite {
            copyWanted = false
            await onSavedCopy?(shared.string(), String(seq))
        }
        // Viewers' previews of a sketch refresh when an editor that changed it leaves, not per stroke.
        if edited, session.hub.isConnected {
            await send("closed", [:])
        }
        // Leaving takes this caret off everyone's page at once rather than when it times out.
        if awareness.hasLocalState {
            awareness.setUser(nil)
            await sendAwareness()
        }
        subscriptions = []
        [flushTask, resendTask, copyTask, retryTask, awarenessTask, heartbeat].forEach { $0?.cancel() }
        awarenessTask = nil
        await session.hub.setTopicPresence(projectId: projectId, topic: topic, .none)
        await session.hub.unsubscribe(projectId: projectId, topic: topic)
    }

    func report(editing: Bool) {
        Task { await session.hub.setTopicPresence(projectId: projectId, topic: topic, editing ? .editing : .viewing) }
    }

    /// Asks for everything after the last completed replay. Also what a reconnect does.
    private func connect() {
        guard !deleted else { return }
        // Kept on the device, it is whole and may be written in whatever the connection says.
        if !kept {
            synced = false
            setWritable(false)
        }
        chunks = [:]
        // Asked for again from the last whole replay, so what came of this one comes again.
        replay = []
        syncId = UUID()
        resendTask?.cancel()
        liveStatus = session.hub.isConnected ? "Synchronizing…" : "Offline · changes retained"
        Task { await send("sync", ["afterSeq": String(checkpoint), "batch": true, "syncId": syncId.api]) }
    }

    private func setWritable(_ value: Bool) {
        canWrite = value && !deleted && mayWrite
        // Only someone who can change the document is shown in it.
        if canWrite {
            if !awareness.hasLocalState { announce() }
        } else {
            awareness.setUser(nil)
        }
    }

    // MARK: Local changes

    /// Call after changing the document here. Edits are gathered for a moment
    /// and sent as one update, so typing is a few messages rather than one per key.
    ///
    /// `promptly` is for a change that is already whole — a finished stroke — which the others
    /// should have at once rather than after a pause that only typing needs.
    func changedLocally(promptly: Bool = false) {
        documentVersion += 1
        dirty = true
        if kept { unsaved = true }
        edited = true
        copyWanted = true
        flushTask?.cancel()
        flushTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(promptly ? 20 : 250))
            guard !Task.isCancelled else { return }
            self?.flushNow()
        }
    }

    /// For a change made to the document without going through the editor,
    /// which then has to pick it up as it would someone else's.
    func changedOutsideEditor() {
        remoteRevision += 1
        changedLocally()
    }

    private func flushNow() {
        flushTask?.cancel()
        keepOnDevice()
        if kept {
            // Kept, not sent: the download sends it, now or whenever the instance is next in reach.
            guard dirty else { return }
            sentVector = shared.stateVector()
            dirty = false
            unsaved = false
            session.docsOffline.changed(documentId, state: shared.fullState())
            return
        }
        guard dirty, pending == nil, synced, canWrite else { return }
        let update = shared.update(since: sentVector)
        sentVector = shared.stateVector()
        dirty = false
        guard update.count > 2 else { return }
        let packet = Packet(data: Data(update).base64EncodedString(), seedToken: seedToken)
        pending = packet
        liveStatus = "Saving…"
        transmit(packet)
    }

    private func transmit(_ packet: Packet) {
        Task {
            var base: [String: Any] = ["id": packet.id.api]
            if let token = packet.seedToken { base["seedToken"] = token.api }
            let parts = stride(from: 0, to: packet.data.count, by: Self.chunkLength).map { offset -> String in
                let start = packet.data.index(packet.data.startIndex, offsetBy: offset)
                let end = packet.data.index(start, offsetBy: Self.chunkLength, limitedBy: packet.data.endIndex) ?? packet.data.endIndex
                return String(packet.data[start..<end])
            }
            if parts.count == 1 {
                base["data"] = packet.data
                await send("update", base)
            } else {
                for (index, part) in parts.enumerated() {
                    var piece = base
                    piece["data"] = part
                    piece["index"] = index
                    piece["count"] = parts.count
                    await send("update-part", piece)
                }
            }
        }
        // Unacknowledged after a while, it goes again: the server answers a repeat with the same ack.
        resendTask?.cancel()
        resendTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(5))
            guard !Task.isCancelled, let self, self.pending?.id == packet.id, self.synced else { return }
            self.transmit(packet)
        }
    }

    @discardableResult
    private func send(_ kind: String, _ body: [String: Any]) async -> LiveSendResult {
        let data = (try? JSONSerialization.data(withJSONObject: body)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
        let result = await session.hub.sendTopicMessage(projectId: projectId, topic: topic, kind: kind, data: data)
        if result != .accepted, kind != "closed" {
            interrupted()
        }
        return result
    }

    private func interrupted() {
        guard !deleted else { return }
        keepOnDevice()
        if kept {
            // Nothing is lost by it: the document is on the device. A reconnect asks again.
            return
        }
        setWritable(false)
        synced = false
        liveStatus = "Sync interrupted; edits retained…"
        retryTask?.cancel()
        retryTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(1500))
            guard !Task.isCancelled else { return }
            self?.connect()
        }
    }

    // MARK: From the server

    private func receive(_ kind: String, _ raw: String?) {
        guard let raw, let payload = (try? JSONSerialization.jsonObject(with: Data(raw.utf8))) as? [String: Any] else {
            if kind == "deleted" { markDeleted() }
            return
        }
        switch kind {
        case "state", "state-batch", "update":
            if let id = payload["syncId"] as? String, id.lowercased() != syncId.api { return }
            guard let count = payload["count"] as? Int, let index = payload["index"] as? Int, (1...512).contains(count),
                  (0..<count).contains(index), let data = payload["data"] as? String, let sequence = payload["seq"] as? String
            else { return }
            let key = "\(kind):\(sequence)"
            var parts = chunks[key] ?? Array(repeating: nil, count: count)
            guard parts.count == count else { return }
            parts[index] = data
            guard !parts.contains(where: { $0 == nil }) else {
                chunks[key] = parts
                return
            }
            chunks[key] = nil
            guard let bytes = Data(base64Encoded: parts.compactMap { $0 }.joined()) else { return }
            let updates = kind == "state-batch" ? Self.unpack(bytes) : [[UInt8](bytes)]
            // A replay is the document's past, a megabyte at a time, and not something to
            // watch: applied as it came, a sketch was drawn from each piece in turn, with
            // every stroke since cleared away, before the one it is now. It is kept back and
            // applied whole at "synced", as the web's sketches are. What someone writes in
            // the meantime waits with it, since a document not yet whole can't show it.
            guard synced, kind == "update" else {
                replay.append(contentsOf: updates)
                return
            }
            apply(updates)
            seq = max(seq, Int64(sequence) ?? 0)

        case "synced":
            if let id = payload["syncId"] as? String, id.lowercased() != syncId.api { return }
            // A piece of the replay never arrived: ask again rather than call it complete.
            if chunks.keys.contains(where: { $0.hasPrefix("state") }) {
                interrupted()
                return
            }
            if !replay.isEmpty {
                let whole = replay
                replay = []
                apply(whole)
            }
            let head = Int64(payload["seq"] as? String ?? "") ?? seq
            seq = max(seq, head)
            checkpoint = head
            synced = true
            setWritable(payload["canWrite"] as? Bool ?? false)
            if !dirty { sentVector = shared.stateVector() }
            if let packet = pending {
                transmit(packet)
            } else {
                flushNow()
            }
            settle()

        case "ack":
            guard let id = payload["id"] as? String, id.lowercased() == pending?.id.api else { return }
            pending = nil
            resendTask?.cancel()
            seq = max(seq, Int64(payload["seq"] as? String ?? "") ?? 0)
            seedToken = nil
            flushNow()
            settle()

        case "seed":
            // First writer to open a note that has only ever been imported:
            // its saved Markdown becomes the shared document, under a lease.
            seedToken = (payload["seedToken"] as? String).flatMap(UUID.init)
            synced = true
            if shared.string().isEmpty, let markdown = payload["markdown"] as? String, !markdown.isEmpty {
                shared.replaceText(start: 0, length: 0, with: markdown)
                documentVersion += 1
                remoteRevision += 1
            }
            let packet = Packet(data: Data(shared.fullState()).base64EncodedString(), seedToken: seedToken)
            sentVector = shared.stateVector()
            dirty = false
            pending = packet
            transmit(packet)
            Task { await send("sync", ["afterSeq": "0", "batch": true, "syncId": syncId.api]) }

        case "seeding":
            retryTask?.cancel()
            retryTask = Task { [weak self] in
                try? await Task.sleep(for: .seconds(1))
                guard !Task.isCancelled, let self else { return }
                await self.send("sync", ["afterSeq": "0", "batch": true, "syncId": self.syncId.api])
            }

        case "unseeded":
            unseededMarkdown = payload["markdown"] as? String
            synced = true
            liveStatus = "Read-only"

        case "refused":
            setWritable(false)
            let reason = payload["reason"] as? String ?? ""
            liveStatus = reason == "seed-expired" ? "Seed lease expired. Reopen this note before editing." : "Read-only · \(reason)"

        case "deleted":
            markDeleted()

        case "awareness":
            if let data = (payload["data"] as? String).flatMap({ Data(base64Encoded: $0) }) {
                awareness.apply([UInt8](data))
            }

        default:
            break
        }
    }

    private func apply(_ updates: [[UInt8]], fromDevice: Bool = false) {
        documentVersion += 1
        guard shared.apply(updates) else { return }
        if !dirty, pending == nil {
            sentVector = shared.stateVector()
        }
        remoteRevision += 1
        // What came over the live connection is the instance's, and is kept as that.
        if kept, !fromDevice {
            session.docsOffline.received(documentId, updates: updates, state: shared.fullState())
        }
    }

    private func settle() {
        liveStatus = hasPending ? "Saving…" : canWrite ? "Saved online" : "Read-only"
        guard copyWanted, !kept, !hasPending, canWrite else { return }
        copyTask?.cancel()
        copyTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled, let self, !self.hasPending, self.synced else { return }
            self.copyWanted = false
            await self.onSavedCopy?(self.shared.string(), String(self.seq))
        }
    }

    /// Deleted elsewhere: nothing can be saved again, whatever reconnects.
    private func markDeleted() {
        deleted = true
        synced = false
        pending = nil
        dirty = false
        setWritable(false)
        liveStatus = "Deleted · read-only"
    }

    /// A batch is a count, then each update behind its length — all little-endian 32-bit.
    nonisolated static func unpack(_ data: Data) -> [[UInt8]] {
        let bytes = [UInt8](data)
        func int(at offset: Int) -> Int? {
            guard offset + 4 <= bytes.count else { return nil }
            return Int(bytes[offset]) | Int(bytes[offset + 1]) << 8 | Int(bytes[offset + 2]) << 16 | Int(bytes[offset + 3]) << 24
        }
        guard let count = int(at: 0), count <= (bytes.count - 4) / 5 else { return [] }
        var updates: [[UInt8]] = []
        var offset = 4
        for _ in 0..<count {
            guard let length = int(at: offset), length > 0, offset + 4 + length <= bytes.count else { return [] }
            updates.append(Array(bytes[(offset + 4)..<(offset + 4 + length)]))
            offset += 4 + length
        }
        return offset == bytes.count ? updates : []
    }
}
