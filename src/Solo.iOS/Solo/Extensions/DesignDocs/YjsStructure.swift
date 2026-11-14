import Foundation

// A collaborator's cursor is not sent as "character 214". Yjs sends it as a
// relative position: the identity of the character the cursor sits before —
// which client typed it, and that client's count at the time — so that it
// stays put while text is typed around it. Turning that into a place in the
// text, and a place in the text into that, takes the document's structure:
// which characters there are, in what order, and which have been deleted.
//
// YSwift keeps that to itself (Yrs has "sticky indexes"; its Swift bindings
// don't expose them), so this reads it out of the one thing the bindings will
// hand over: the document encoded as an update. It decodes the update format
// and puts the items in order with Yjs's own rule for concurrent insertions
// (YATA), which is how every client arrives at the same text.
//
// It is only ever used to place cursors, and it checks itself: the text it
// reconstructs is compared with the document's own, and when they differ no
// cursor is shown or sent.

/// The identity of one UTF-16 unit of a shared text: who typed it, and their count when they did.
nonisolated struct YjsID: Hashable, Sendable {
    var client: UInt64
    var clock: UInt64
}

/// Reads lib0's variable-length encoding, which Yjs updates and awareness messages are written in.
nonisolated struct Lib0Reader {
    private let bytes: [UInt8]
    private(set) var offset = 0
    private(set) var failed = false

    init(_ bytes: [UInt8]) {
        self.bytes = bytes
    }

    var isAtEnd: Bool { offset >= bytes.count }

    mutating func byte() -> UInt8 {
        guard offset < bytes.count else {
            failed = true
            return 0
        }
        defer { offset += 1 }
        return bytes[offset]
    }

    mutating func varUInt() -> UInt64 {
        var value: UInt64 = 0, shift: UInt64 = 0
        while true {
            let next = byte()
            if failed { return 0 }
            value |= UInt64(next & 0x7F) << shift
            if next < 0x80 { return value }
            shift += 7
            if shift > 63 {
                failed = true
                return 0
            }
        }
    }

    /// A signed integer: the first byte carries the sign and six bits, the rest seven each.
    mutating func skipVarInt() {
        var next = byte()
        while next >= 0x80, !failed { next = byte() }
    }

    mutating func skip(_ count: Int) {
        guard count >= 0, offset + count <= bytes.count else {
            failed = true
            return
        }
        offset += count
    }

    mutating func string() -> String {
        let length = Int(clamping: varUInt())
        guard !failed, offset + length <= bytes.count else {
            failed = true
            return ""
        }
        defer { offset += length }
        return String(decoding: bytes[offset..<offset + length], as: UTF8.self)
    }

    mutating func id() -> YjsID {
        let client = varUInt()
        return YjsID(client: client, clock: varUInt())
    }

    /// Steps over one value of lib0's "any" encoding, which map entries and embeds are written in.
    mutating func skipAny(depth: Int = 0) {
        guard depth < 64 else {
            failed = true
            return
        }
        switch byte() {
        case 127, 126, 121, 120: break
        case 125: skipVarInt()
        case 124: skip(4)
        case 123, 122: skip(8)
        case 119: _ = string()
        case 118:
            let count = Int(clamping: varUInt())
            for _ in 0..<count where !failed {
                _ = string()
                skipAny(depth: depth + 1)
            }
        case 117:
            let count = Int(clamping: varUInt())
            for _ in 0..<count where !failed { skipAny(depth: depth + 1) }
        case 116: skip(Int(clamping: varUInt()))
        default: failed = true
        }
    }
}

nonisolated struct Lib0Writer {
    private(set) var bytes: [UInt8] = []

    mutating func varUInt(_ value: UInt64) {
        var rest = value
        while rest >= 0x80 {
            bytes.append(UInt8(rest & 0x7F) | 0x80)
            rest >>= 7
        }
        bytes.append(UInt8(rest))
    }

    mutating func string(_ value: String) {
        let utf8 = Array(value.utf8)
        varUInt(UInt64(utf8.count))
        bytes.append(contentsOf: utf8)
    }
}

/// The characters of one shared text, in order, with who typed each.
nonisolated struct YjsTextStructure: Sendable {
    /// A run of characters typed together and still together.
    private struct Run: Sendable {
        var id: YjsID
        var length: Int
        /// Where the run starts in the text as it reads; a deleted run is where it would have been.
        var index: Int
        var deleted: Bool
    }

    private var runs: [Run] = []
    /// Each client's runs by clock, as positions in `runs`.
    private var byClient: [UInt64: [Int]] = [:]
    private(set) var length = 0
    /// The text the structure spells out, for checking it against the document's own.
    private(set) var text = ""

    /// Reads the structure of the text named `root` out of a whole document encoded as an update.
    /// Nil when the update can't be read or doesn't hold together.
    init?(update: [UInt8], root: String = "markdown") {
        guard let document = YjsDocumentStructure(update: update), let sequence = document.sequence(root: root) else { return nil }
        var units: [UInt16] = []
        for item in sequence {
            let deleted = item.deleted || !item.countable
            runs.append(Run(id: item.id, length: item.length, index: units.count, deleted: deleted))
            byClient[item.id.client, default: []].append(runs.count - 1)
            if !deleted { units.append(contentsOf: item.units) }
        }
        for client in byClient.keys {
            byClient[client]?.sort { runs[$0].id.clock < runs[$1].id.clock }
        }
        length = units.count
        text = String(decoding: units, as: UTF16.self)
    }

    /// Where the character with this identity is in the text. One that has been deleted is where it was.
    func index(of id: YjsID) -> Int? {
        guard let candidates = byClient[id.client] else { return nil }
        var low = 0, high = candidates.count - 1
        while low <= high {
            let middle = (low + high) / 2
            let run = runs[candidates[middle]]
            if id.clock < run.id.clock {
                high = middle - 1
            } else if id.clock >= run.id.clock + UInt64(run.length) {
                low = middle + 1
            } else {
                return run.deleted ? run.index : run.index + Int(id.clock - run.id.clock)
            }
        }
        return nil
    }

    /// The identity of the character at a place in the text; nil at its end, where there is none.
    func id(at index: Int) -> YjsID? {
        guard index >= 0, index < length else { return nil }
        var low = 0, high = runs.count - 1, found: Int?
        // The last run starting at or before the index; deleted runs share a start with what follows them.
        while low <= high {
            let middle = (low + high) / 2
            if runs[middle].index <= index {
                found = middle
                low = middle + 1
            } else {
                high = middle - 1
            }
        }
        guard var position = found else { return nil }
        while position > 0, runs[position].deleted { position -= 1 }
        let run = runs[position]
        guard !run.deleted, index - run.index < run.length else { return nil }
        return YjsID(client: run.id.client, clock: run.id.clock + UInt64(index - run.index))
    }
}

/// A Yjs document's items, decoded from an update and put in order.
nonisolated final class YjsDocumentStructure {
    final class Item {
        var id: YjsID
        var length: Int
        var origin: YjsID?
        var rightOrigin: YjsID?
        var parent: Parent?
        var parentSub: String?
        var units: [UInt16]
        var countable: Bool
        var deleted: Bool
        var left: Item?
        var right: Item?
        var integrated = false

        init(id: YjsID, length: Int, origin: YjsID?, rightOrigin: YjsID?, parent: Parent?, parentSub: String?,
             units: [UInt16], countable: Bool, deleted: Bool) {
            self.id = id
            self.length = length
            self.origin = origin
            self.rightOrigin = rightOrigin
            self.parent = parent
            self.parentSub = parentSub
            self.units = units
            self.countable = countable
            self.deleted = deleted
        }
    }

    enum Parent: Hashable {
        case root(String)
        case item(YjsID)
    }

    /// A list an item can be in: a type's sequence, or one key of a map.
    private struct ListKey: Hashable {
        var parent: Parent
        var sub: String?
    }

    /// Each client's items, in clock order, without gaps where they are text.
    private var items: [UInt64: [Item]] = [:]
    private var starts: [ListKey: Item] = [:]

    init?(update: [UInt8]) {
        guard decode(update), integrate() else { return nil }
    }

    /// The items of a root type's sequence, first to last.
    func sequence(root: String) -> [Item]? {
        var result: [Item] = []
        var cursor = starts[ListKey(parent: .root(root), sub: nil)]
        var steps = 0
        while let item = cursor {
            result.append(item)
            cursor = item.right
            steps += 1
            // A loop would mean the structure was put together wrongly.
            if steps > 4_000_000 { return nil }
        }
        return result
    }

    // MARK: Decoding

    private func decode(_ update: [UInt8]) -> Bool {
        var reader = Lib0Reader(update)
        let clients = reader.varUInt()
        guard !reader.failed, clients < 1_000_000 else { return false }
        for _ in 0..<clients {
            let count = reader.varUInt()
            let client = reader.varUInt()
            var clock = reader.varUInt()
            guard !reader.failed, count < 50_000_000 else { return false }
            var list: [Item] = []
            for _ in 0..<count {
                let info = reader.byte()
                switch info & 0x1F {
                case 0, 10:
                    // Collected garbage, or a gap: clocks pass, nothing is there.
                    clock += reader.varUInt()
                default:
                    let hasOrigin = info & 0x80 != 0, hasRightOrigin = info & 0x40 != 0
                    let origin = hasOrigin ? reader.id() : nil
                    let rightOrigin = hasRightOrigin ? reader.id() : nil
                    var parent: Parent?
                    var parentSub: String?
                    if !hasOrigin, !hasRightOrigin {
                        parent = reader.varUInt() == 1 ? .root(reader.string()) : .item(reader.id())
                        if info & 0x20 != 0 { parentSub = reader.string() }
                    }
                    var units: [UInt16] = []
                    var length = 1
                    var countable = true
                    var deleted = false
                    switch info & 0x1F {
                    case 1:
                        length = Int(clamping: reader.varUInt())
                        deleted = true
                    case 2:
                        length = Int(clamping: reader.varUInt())
                        for _ in 0..<length where !reader.failed { _ = reader.string() }
                    case 3:
                        reader.skip(Int(clamping: reader.varUInt()))
                    case 4:
                        units = Array(reader.string().utf16)
                        length = units.count
                    case 5:
                        _ = reader.string()
                    case 6:
                        _ = reader.string()
                        _ = reader.string()
                        countable = false
                    case 7:
                        let kind = reader.varUInt()
                        if kind == 3 || kind == 5 { _ = reader.string() }
                    case 8:
                        length = Int(clamping: reader.varUInt())
                        for _ in 0..<length where !reader.failed { reader.skipAny() }
                    case 9:
                        _ = reader.string()
                        reader.skipAny()
                    default:
                        return false
                    }
                    guard !reader.failed, length > 0 else { return false }
                    // Anything that isn't text stands in for itself, one unit apiece, so indexes still add up.
                    if units.count != length { units = Array(repeating: 0xFFFC, count: length) }
                    list.append(Item(id: YjsID(client: client, clock: clock), length: length, origin: origin,
                                     rightOrigin: rightOrigin, parent: parent, parentSub: parentSub, units: units,
                                     countable: countable, deleted: deleted))
                    clock += UInt64(length)
                }
                if reader.failed { return false }
            }
            items[client, default: []].append(contentsOf: list)
        }
        for client in items.keys {
            items[client]?.sort { $0.id.clock < $1.id.clock }
        }

        // What has been deleted but not yet collected is still there as itself, and listed here.
        let deleters = reader.varUInt()
        guard !reader.failed else { return false }
        for _ in 0..<deleters {
            let client = reader.varUInt()
            let ranges = reader.varUInt()
            guard !reader.failed else { return false }
            for _ in 0..<ranges {
                let clock = reader.varUInt()
                let length = reader.varUInt()
                guard !reader.failed else { return false }
                markDeleted(client: client, from: clock, to: clock + length)
            }
        }
        return true
    }

    private func markDeleted(client: UInt64, from: UInt64, to: UInt64) {
        guard to > from, items[client] != nil else { return }
        // Cut at both ends so whole items fall inside the range.
        _ = cleanStart(YjsID(client: client, clock: from))
        _ = cleanStart(YjsID(client: client, clock: to))
        for item in items[client] ?? [] where item.id.clock >= from && item.id.clock < to {
            item.deleted = true
        }
    }

    // MARK: Finding and splitting

    private func position(of id: YjsID) -> Int? {
        guard let list = items[id.client] else { return nil }
        var low = 0, high = list.count - 1
        while low <= high {
            let middle = (low + high) / 2
            let item = list[middle]
            if id.clock < item.id.clock {
                high = middle - 1
            } else if id.clock >= item.id.clock + UInt64(item.length) {
                low = middle + 1
            } else {
                return middle
            }
        }
        return nil
    }

    private func item(containing id: YjsID) -> Item? {
        position(of: id).flatMap { items[id.client]?[$0] }
    }

    /// The item that starts exactly at `id`, cutting the one that holds it in two if need be.
    private func cleanStart(_ id: YjsID) -> Item? {
        guard let position = position(of: id), let item = items[id.client]?[position] else { return nil }
        guard item.id.clock < id.clock else { return item }
        let keep = Int(id.clock - item.id.clock)
        // The second half follows the first directly: its origin is the first half's last unit.
        let rest = Item(id: id, length: item.length - keep, origin: YjsID(client: id.client, clock: id.clock - 1),
                        rightOrigin: item.rightOrigin, parent: item.parent, parentSub: item.parentSub,
                        units: Array(item.units[keep...]), countable: item.countable, deleted: item.deleted)
        item.units = Array(item.units[..<keep])
        item.length = keep
        rest.integrated = item.integrated
        if item.integrated {
            rest.left = item
            rest.right = item.right
            item.right?.left = rest
            item.right = rest
        }
        items[id.client]?.insert(rest, at: position + 1)
        return rest
    }

    /// The item that ends exactly at `id`.
    private func cleanEnd(_ id: YjsID) -> Item? {
        guard let item = item(containing: id) else { return nil }
        if id.clock + 1 < item.id.clock + UInt64(item.length) {
            _ = cleanStart(YjsID(client: id.client, clock: id.clock + 1))
        }
        return self.item(containing: id)
    }

    // MARK: Ordering

    /// Puts every item in its list. An item can only be placed once what it was typed between is
    /// in place, so clients are taken a step at a time until none can move.
    private func integrate() -> Bool {
        // How far each client's items have been placed, as a clock: items are cut as they go, so positions shift.
        var done: [UInt64: UInt64] = [:]
        let clients = Array(items.keys)
        var progressed = true
        while progressed {
            progressed = false
            for client in clients {
                while let next = nextUnplaced(client: client, after: done[client]) {
                    guard ready(next.origin, done), ready(next.rightOrigin, done), readyParent(next.parent, done) else { break }
                    guard place(next) else { return false }
                    done[client] = next.id.clock + UInt64(next.length)
                    progressed = true
                }
            }
        }
        // Anything left over was waiting on something that never came: the update was not a whole document.
        return items.values.allSatisfy { $0.allSatisfy(\.integrated) }
    }

    private func nextUnplaced(client: UInt64, after clock: UInt64?) -> Item? {
        guard let list = items[client] else { return nil }
        guard let clock else { return list.first { !$0.integrated } }
        // Clocks may skip — collected garbage leaves a gap — so the next item is the first at or past the mark.
        var low = 0, high = list.count
        while low < high {
            let middle = (low + high) / 2
            if list[middle].id.clock < clock { low = middle + 1 } else { high = middle }
        }
        return low < list.count ? list[low] : nil
    }

    private func ready(_ id: YjsID?, _ done: [UInt64: UInt64]) -> Bool {
        guard let id else { return true }
        // Something collected as garbage is never placed; nothing can be waiting on it.
        guard let holder = item(containing: id) else { return true }
        return holder.integrated || (done[id.client] ?? 0) > id.clock
    }

    private func readyParent(_ parent: Parent?, _ done: [UInt64: UInt64]) -> Bool {
        guard case .item(let id) = parent else { return true }
        return ready(id, done)
    }

    private func sameOrigin(_ a: YjsID?, _ b: YjsID?) -> Bool { a == b }

    /// Yjs's rule for where an item goes among others typed at the same place at the same time.
    private func place(_ item: Item) -> Bool {
        var left = item.origin.flatMap { cleanEnd($0) }
        let right = item.rightOrigin.flatMap { cleanStart($0) }
        if item.parent == nil {
            item.parent = left?.parent ?? right?.parent
            item.parentSub = left?.parentSub ?? right?.parentSub
        }
        guard let parent = item.parent else {
            // Its neighbours were collected with their parent: it belongs to nothing that can be read.
            item.integrated = true
            return true
        }
        let key = ListKey(parent: parent, sub: item.parentSub)

        if (left == nil && (right == nil || right?.left != nil)) || (left != nil && left?.right !== right) {
            var cursor = left != nil ? left?.right : starts[key]
            var conflicting = Set<ObjectIdentifier>(), before = Set<ObjectIdentifier>()
            var guardCount = 0
            while let other = cursor, other !== right {
                before.insert(ObjectIdentifier(other))
                conflicting.insert(ObjectIdentifier(other))
                if sameOrigin(item.origin, other.origin) {
                    if other.id.client < item.id.client {
                        left = other
                        conflicting.removeAll()
                    } else if sameOrigin(item.rightOrigin, other.rightOrigin) {
                        break
                    }
                } else if let origin = other.origin, let holder = self.item(containing: origin),
                          before.contains(ObjectIdentifier(holder)) {
                    if !conflicting.contains(ObjectIdentifier(holder)) {
                        left = other
                        conflicting.removeAll()
                    }
                } else {
                    break
                }
                cursor = other.right
                guardCount += 1
                if guardCount > 4_000_000 { return false }
            }
        }

        if let left {
            item.right = left.right
            left.right = item
            item.left = left
        } else {
            item.right = starts[key]
            starts[key] = item
        }
        item.right?.left = item
        item.integrated = true
        return true
    }
}

/// A Yjs relative position, as it travels in a collaborator's awareness state.
nonisolated struct YjsRelativePosition: Equatable, Sendable {
    /// The character the position is before; nil for the end of the text.
    var item: YjsID?

    init(item: YjsID?) {
        self.item = item
    }

    init?(json: Any?) {
        guard let object = json as? [String: Any] else { return nil }
        if let item = object["item"] as? [String: Any] {
            guard let client = (item["client"] as? NSNumber)?.uint64Value, let clock = (item["clock"] as? NSNumber)?.uint64Value
            else { return nil }
            self.item = YjsID(client: client, clock: clock)
        } else if object["tname"] is String {
            item = nil
        } else {
            return nil
        }
    }

    /// As Yjs writes one for a position in the root text `markdown`.
    var json: [String: Any] {
        var object: [String: Any] = ["type": NSNull(), "tname": "markdown", "assoc": 0]
        object["item"] = item.map { ["client": NSNumber(value: $0.client), "clock": NSNumber(value: $0.clock)] } ?? NSNull()
        return object
    }
}
