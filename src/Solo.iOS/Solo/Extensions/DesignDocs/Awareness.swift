import Foundation
import Observation
import SwiftUI

// Who is where in a note or a sketch, live. This is Yjs's awareness protocol,
// as the web editor speaks it: each client keeps one small JSON state — who
// they are, where their caret or pen is — and sends it whole whenever it
// changes, numbered so that a late message never undoes a newer one. Nothing
// here is saved; a state lasts as long as its owner keeps repeating it.

/// One collaborator, as their client describes them.
nonisolated struct AwarenessUser: Equatable, Sendable {
    var userId: UUID?
    var name: String
    /// A CSS colour, as the web writes it.
    var color: String?

    init(userId: UUID?, name: String, color: String?) {
        self.userId = userId
        self.name = name
        self.color = color
    }

    init?(json: Any?) {
        guard let object = json as? [String: Any] else { return nil }
        userId = (object["userId"] as? String).flatMap(UUID.init)
        name = (object["name"] as? String)?.trimmingCharacters(in: .whitespaces).nilIfBlank ?? "Collaborator"
        color = object["color"] as? String
    }

    /// One or two letters to stand for the person, as the web's cursor shows.
    var initials: String {
        let words = name.split(separator: " ")
        guard let first = words.first?.first else { return "?" }
        let letters = words.count > 1 ? [first, words.last?.first].compactMap { $0 } : [first]
        return String(letters).uppercased()
    }
}

/// A collaborator's state, as last heard.
nonisolated struct AwarenessPeer: Sendable {
    var client: UInt64
    var clock: UInt64
    var user: AwarenessUser
    /// A note: where their selection starts and ends.
    var anchor: YjsRelativePosition?
    var head: YjsRelativePosition?
    /// A sketch: where their pen is on the page, in page pixels, and the mark it would leave.
    var pointer: Pointer?
    /// A sketch: the stroke they are in the middle of, as far as it has been sent.
    var stroke: HeldStroke?
    var heard = Date()

    struct Pointer: Equatable, Sendable {
        var x: Double
        var y: Double
        var tool: String
        var width: Double
    }

    /// A piece of a stroke in progress: its samples from `base` on.
    struct HeldStroke: Sendable {
        var op: SketchOp
        var base: Int?
    }
}

nonisolated enum AwarenessCodec {
    /// The states in an awareness message: whose, which revision of it, and the state as JSON text.
    static func decode(_ bytes: [UInt8]) -> [(client: UInt64, clock: UInt64, state: String)] {
        var reader = Lib0Reader(bytes)
        let count = reader.varUInt()
        guard !reader.failed, count < 10_000 else { return [] }
        var states: [(UInt64, UInt64, String)] = []
        for _ in 0..<count {
            let client = reader.varUInt(), clock = reader.varUInt()
            let state = reader.string()
            guard !reader.failed else { return [] }
            states.append((client, clock, state))
        }
        return states
    }

    static func encode(client: UInt64, clock: UInt64, state: String) -> [UInt8] {
        var writer = Lib0Writer()
        writer.varUInt(1)
        writer.varUInt(client)
        writer.varUInt(clock)
        writer.string(state)
        return writer.bytes
    }

    static func peer(client: UInt64, clock: UInt64, state: String) -> AwarenessPeer? {
        guard let object = (try? JSONSerialization.jsonObject(with: Data(state.utf8))) as? [String: Any],
              let user = AwarenessUser(json: object["user"]) else { return nil }
        var peer = AwarenessPeer(client: client, clock: clock, user: user)
        if let cursor = object["cursor"] as? [String: Any] {
            peer.anchor = YjsRelativePosition(json: cursor["anchor"])
            peer.head = YjsRelativePosition(json: cursor["head"])
        }
        if let pointer = object["pointer"] as? [String: Any],
           let x = (pointer["x"] as? NSNumber)?.doubleValue, let y = (pointer["y"] as? NSNumber)?.doubleValue,
           x.isFinite, y.isFinite {
            peer.pointer = .init(x: x, y: y, tool: pointer["tool"] as? String ?? "pen",
                                 width: (pointer["width"] as? NSNumber)?.doubleValue ?? 1)
        }
        if let stroke = object["stroke"] as? [String: Any], let id = stroke["id"] as? String,
           let points = stroke["points"] as? [[NSNumber]] {
            let op = SketchOp(id: id, tool: stroke["tool"] as? String, ink: stroke["ink"] as? String,
                              level: (stroke["level"] as? NSNumber)?.intValue, seed: (stroke["seed"] as? NSNumber)?.intValue,
                              pointerType: stroke["pointerType"] as? String, points: points.map { $0.map(\.doubleValue) })
            peer.stroke = .init(op: op, base: (stroke["base"] as? NSNumber)?.intValue)
        }
        return peer
    }
}

/// The awareness of one document: everyone else's states, and this client's own.
@Observable
final class Awareness {
    /// Everyone else, by client.
    private(set) var peers: [UInt64: AwarenessPeer] = [:]
    /// Changes whenever anyone's state does.
    private(set) var revision = 0

    @ObservationIgnored let client = UInt64.random(in: 1...0x7FFF_FFFF)
    @ObservationIgnored private var clock: UInt64 = 0
    @ObservationIgnored private var local: [String: Any]?
    /// Called when this client's state changed and should be sent.
    @ObservationIgnored var onLocalChange: (() -> Void)?

    /// Strokes in progress keep their samples between messages, which each carry only the newest.
    @ObservationIgnored private var held: [UInt64: (id: String, points: [[Double]?])] = [:]

    /// A state not repeated for this long has lost its owner.
    static let timeout: TimeInterval = 30

    var hasLocalState: Bool { local != nil }

    // MARK: Others

    func apply(_ bytes: [UInt8]) {
        var changed = false
        for (client, clock, state) in AwarenessCodec.decode(bytes) where client != self.client {
            let known = peers[client]?.clock
            // A removal carries the same number as the state it removes.
            guard known == nil || clock > known! || (clock == known! && state == "null") else { continue }
            if let peer = AwarenessCodec.peer(client: client, clock: clock, state: state) {
                peers[client] = merged(peer)
            } else {
                peers[client] = nil
                held[client] = nil
            }
            changed = true
        }
        if changed { revision += 1 }
    }

    /// Fits a stroke's newest samples onto the ones already held, at their own places in it.
    private func merged(_ peer: AwarenessPeer) -> AwarenessPeer {
        var peer = peer
        guard let stroke = peer.stroke else {
            held[peer.client] = nil
            return peer
        }
        var points: [[Double]?] = []
        if let base = stroke.base, base >= 0, let known = held[peer.client], known.id == stroke.op.id {
            points = known.points
            for (offset, point) in (stroke.op.points ?? []).enumerated() {
                let index = base + offset
                if index >= points.count { points.append(contentsOf: Array(repeating: nil, count: index - points.count + 1)) }
                points[index] = point
            }
        } else {
            // No base: a whole stroke, from a client that sends them whole. A base past zero for a
            // stroke not seen before is one joined part-way: its start arrives in later messages.
            let base = max(stroke.base ?? 0, 0)
            points = Array(repeating: nil, count: base) + (stroke.op.points ?? []).map(Optional.some)
        }
        held[peer.client] = (stroke.op.id, points)
        // Only what has arrived without a gap can be drawn.
        peer.stroke?.op.points = Array(points.prefix { $0 != nil }.compactMap { $0 })
        return peer
    }

    /// Forgets everyone who is no longer on the document, or has gone quiet.
    func keep(userIds: Set<UUID>) {
        let now = Date()
        let gone = peers.values.filter { peer in
            peer.user.userId.map { !userIds.contains($0) } ?? false || now.timeIntervalSince(peer.heard) > Self.timeout
        }
        guard !gone.isEmpty else { return }
        for peer in gone {
            peers[peer.client] = nil
            held[peer.client] = nil
        }
        revision += 1
    }

    func removeAll() {
        guard !peers.isEmpty else { return }
        peers = [:]
        held = [:]
        revision += 1
    }

    // MARK: This client

    /// Starts or stops having a state at all: only someone who may write is shown to the others.
    func setUser(_ user: [String: Any]?) {
        if let user {
            var state = local ?? [:]
            state["user"] = user
            local = state
        } else {
            guard local != nil else { return }
            local = nil
        }
        clock += 1
        onLocalChange?()
    }

    /// Sets one field of this client's state; nil clears it. Nothing happens without a state to set it in.
    func set(_ field: String, _ value: Any?) {
        guard var state = local else { return }
        let old = state[field], new: Any = value ?? NSNull()
        if let old = old as? NSObject, let fresh = new as? NSObject, old == fresh { return }
        if old == nil, value == nil { return }
        state[field] = new
        local = state
        clock += 1
        onLocalChange?()
    }

    func field(_ name: String) -> Any? {
        guard let value = local?[name], !(value is NSNull) else { return nil }
        return value
    }

    /// This client's state as a message — a removal, when it has none.
    func message() -> [UInt8] {
        let state = local.flatMap { try? JSONSerialization.data(withJSONObject: $0) }.map { String(decoding: $0, as: UTF8.self) }
        return AwarenessCodec.encode(client: client, clock: clock, state: state ?? "null")
    }

    /// Marks this client's state as said again, so that it outlasts the others' timeout.
    func renew() {
        guard local != nil else { return }
        clock += 1
    }
}

/// A collaborator's colour. The web hands each person a hue and lets its theme
/// pick the lightness; this reads the hue back and picks a lightness the same way.
nonisolated enum PresenceColor {
    /// The CSS the web's clients expect for a colour slot.
    static func css(slot: Int) -> String {
        let hue = (Double(slot) * 137.508).truncatingRemainder(dividingBy: 360)
        return "oklch(var(--design-docs-presence-lightness,65%) .15 \(hue))"
    }

    /// The colour a CSS string names, on a dark or a light page: sRGB components, zero to one.
    static func components(_ css: String?, dark: Bool) -> (red: Double, green: Double, blue: Double) {
        guard let css else { return fallback }
        if css.hasPrefix("#"), let rgb = hex(css) { return rgb }
        // oklch(<lightness> <chroma> <hue>): the lightness is the theme's to choose.
        guard let match = css.firstMatch(of: /oklch\(\s*(?:var\([^)]*\)|[\d.]+%?)\s+([\d.]+)\s+(-?[\d.]+)/),
              let chroma = Double(match.1), let hue = Double(match.2) else { return fallback }
        return oklch(lightness: dark ? 0.75 : 0.5, chroma: chroma, hue: hue)
    }

    private static let fallback = (red: 0.635, green: 0.322, blue: 0.839)

    private static func hex(_ css: String) -> (red: Double, green: Double, blue: Double)? {
        let digits = css.dropFirst()
        guard digits.count == 6, let value = UInt32(digits, radix: 16) else { return nil }
        return (Double(value >> 16 & 0xFF) / 255, Double(value >> 8 & 0xFF) / 255, Double(value & 0xFF) / 255)
    }

    static func oklch(lightness: Double, chroma: Double, hue: Double) -> (red: Double, green: Double, blue: Double) {
        let angle = hue * .pi / 180
        let a = chroma * cos(angle), b = chroma * sin(angle)
        let l = pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3)
        let m = pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3)
        let s = pow(lightness - 0.0894841775 * a - 1.2914855480 * b, 3)
        func encoded(_ linear: Double) -> Double {
            let value = min(max(linear, 0), 1)
            return value <= 0.0031308 ? 12.92 * value : 1.055 * pow(value, 1 / 2.4) - 0.055
        }
        return (encoded(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                encoded(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                encoded(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s))
    }

    static func color(_ css: String?, dark: Bool) -> UIColor {
        let rgb = components(css, dark: dark)
        return UIColor(red: rgb.red, green: rgb.green, blue: rgb.blue, alpha: 1)
    }
}
