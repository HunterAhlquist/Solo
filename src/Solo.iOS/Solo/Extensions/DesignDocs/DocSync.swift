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

/// Keeps one note or sketch in step with the instance over its live topic.
///
/// The server stores the document as a log of Yjs updates and relays each new
/// one to everyone else on the topic. A client asks for what it is missing
/// (`sync`), applies what comes back, and sends its own edits as updates that
/// stay pending until the server acknowledges each by id — so an edit made
/// while the connection is down is sent again, not lost.
@Observable
final class DocSync {
    private(set) var statusText = "Connecting…"
    private(set) var canWrite = false
    private(set) var synced = false
    private(set) var deleted = false
    /// The text a reader is shown when nobody able to write has opened the note since it was imported.
    private(set) var unseededMarkdown: String?
    /// Bumped whenever someone else's change lands, for whatever is showing the document.
    private(set) var remoteRevision = 0
    private(set) var present: [TopicPresenceEntry] = []

    @ObservationIgnored let shared = SharedDoc()
    @ObservationIgnored private let session: Session
    @ObservationIgnored private let projectId: UUID
    @ObservationIgnored let documentId: UUID
    @ObservationIgnored private let topic: String
    @ObservationIgnored private let mayWrite: Bool
    /// Called a little after an edit has been saved, with the text and the sequence it is current to.
    @ObservationIgnored var onSavedCopy: ((String, String) async -> Void)?

    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var chunks: [String: [String?]] = [:]
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

    private struct Packet {
        var id = UUID()
        var data: String
        var seedToken: UUID?
    }

    private static let chunkLength = 32_000

    init(session: Session, projectId: UUID, documentId: UUID, mayWrite: Bool) {
        self.session = session
        self.projectId = projectId
        self.documentId = documentId
        self.mayWrite = mayWrite
        topic = DocTopics.doc(documentId)
    }

    var hasPending: Bool { pending != nil || dirty }

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
            },
            hub.reconnected.subscribe { [weak self] in self?.connect() },
        ]
        await hub.subscribe(projectId: projectId, topic: topic)
        await hub.setTopicPresence(projectId: projectId, topic: topic, .viewing)
        connect()
    }

    func stop() async {
        guard started else { return }
        started = false
        flushNow()
        // What was just sent gets a moment to be acknowledged, so the saved
        // copy the tree and links read can go with it rather than wait for
        // the next person to open the note.
        var waited = 0
        while pending != nil, session.hub.isConnected, waited < 20 {
            try? await Task.sleep(for: .milliseconds(100))
            waited += 1
        }
        copyTask?.cancel()
        if copyWanted, !hasPending, canWrite {
            copyWanted = false
            await onSavedCopy?(shared.string(), String(seq))
        }
        // Viewers' previews of a sketch refresh when an editor that changed it leaves, not per stroke.
        if edited, session.hub.isConnected {
            await send("closed", [:])
        }
        subscriptions = []
        [flushTask, resendTask, copyTask, retryTask].forEach { $0?.cancel() }
        await session.hub.setTopicPresence(projectId: projectId, topic: topic, .none)
        await session.hub.unsubscribe(projectId: projectId, topic: topic)
    }

    func report(editing: Bool) {
        Task { await session.hub.setTopicPresence(projectId: projectId, topic: topic, editing ? .editing : .viewing) }
    }

    /// Asks for everything after the last completed replay. Also what a reconnect does.
    private func connect() {
        guard !deleted else { return }
        synced = false
        setWritable(false)
        chunks = [:]
        syncId = UUID()
        resendTask?.cancel()
        statusText = session.hub.isConnected ? "Synchronizing…" : "Offline · changes retained"
        Task { await send("sync", ["afterSeq": String(checkpoint), "batch": true, "syncId": syncId.api]) }
    }

    private func setWritable(_ value: Bool) {
        canWrite = value && !deleted && mayWrite
    }

    // MARK: Local changes

    /// Call after changing the document here. Edits are gathered for a moment
    /// and sent as one update, so typing is a few messages rather than one per key.
    func changedLocally() {
        dirty = true
        edited = true
        copyWanted = true
        flushTask?.cancel()
        flushTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(250))
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
        guard dirty, pending == nil, synced, canWrite else { return }
        let update = shared.update(since: sentVector)
        sentVector = shared.stateVector()
        dirty = false
        guard update.count > 2 else { return }
        let packet = Packet(data: Data(update).base64EncodedString(), seedToken: seedToken)
        pending = packet
        statusText = "Saving…"
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
        setWritable(false)
        synced = false
        statusText = "Sync interrupted; edits retained…"
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
            apply(updates)
            seq = max(seq, Int64(sequence) ?? 0)

        case "synced":
            if let id = payload["syncId"] as? String, id.lowercased() != syncId.api { return }
            // A piece of the replay never arrived: ask again rather than call it complete.
            if chunks.keys.contains(where: { $0.hasPrefix("state") }) {
                interrupted()
                return
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
            statusText = "Read-only"

        case "refused":
            setWritable(false)
            let reason = payload["reason"] as? String ?? ""
            statusText = reason == "seed-expired" ? "Seed lease expired. Reopen this note before editing." : "Read-only · \(reason)"

        case "deleted":
            markDeleted()

        default:
            break
        }
    }

    private func apply(_ updates: [[UInt8]]) {
        guard shared.apply(updates) else { return }
        if !dirty, pending == nil {
            sentVector = shared.stateVector()
        }
        remoteRevision += 1
    }

    private func settle() {
        statusText = hasPending ? "Saving…" : canWrite ? "Saved online" : "Read-only"
        guard copyWanted, !hasPending, canWrite else { return }
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
        statusText = "Deleted · read-only"
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
