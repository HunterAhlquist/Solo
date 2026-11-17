import AVFoundation
import ImageIO
import SwiftUI
import YSwift

// Sketches are pixel drawings: every client replays the same strokes through
// the same rasterizer and gets the same pixels. This is that rasterizer, kept
// to the web editor's arithmetic (js/sketch/render.js) rule for rule — the
// stamp shapes, the per-sample jitter, the pressure widths — because a sketch
// drawn on one device has to look the same on every other.

/// One operation in a sketch's history: a stroke, or a marker that a cut piece was moved.
///
/// Decoding one never fails. The document is whatever any client with write
/// access put in it, and the Yjs binding traps on a value it can't decode —
/// so a stroke with something unexpected in it has to come out as a stroke
/// with that part missing, not as a crash for everyone who opens the sketch.
nonisolated struct SketchOp: Codable, Sendable, Equatable {
    var id: String
    var tool: String?
    var ink: String?
    var level: Int?
    var seed: Int?
    var pointerType: String?
    var points: [[Double]]?
    /// Set on a marker op: the id of the stroke whose cut pieces it places.
    var relocates: String?

    init(id: String, tool: String? = nil, ink: String? = nil, level: Int? = nil, seed: Int? = nil,
         pointerType: String? = nil, points: [[Double]]? = nil, relocates: String? = nil) {
        self.id = id
        self.tool = tool
        self.ink = ink
        self.level = level
        self.seed = seed
        self.pointerType = pointerType
        self.points = points
        self.relocates = relocates
    }

    init(from decoder: Decoder) throws {
        guard let container = try? decoder.container(keyedBy: CodingKeys.self) else {
            id = ""
            return
        }
        func whole(_ key: CodingKeys) -> Int? {
            if let value = try? container.decode(Int.self, forKey: key) { return value }
            guard let value = try? container.decode(Double.self, forKey: key), value.isFinite, abs(value) < 9e15 else { return nil }
            return Int(value)
        }
        id = (try? container.decode(String.self, forKey: .id)) ?? ""
        tool = try? container.decode(String.self, forKey: .tool)
        ink = try? container.decode(String.self, forKey: .ink)
        level = whole(.level)
        seed = whole(.seed)
        pointerType = try? container.decode(String.self, forKey: .pointerType)
        relocates = try? container.decode(String.self, forKey: .relocates)
        // A point's pressure is absent for hardware that reports none.
        points = (try? container.decode([[Double?]].self, forKey: .points))?.map { $0.map { $0 ?? 0 } }
    }
}

/// What a selection-move leaves behind for a stroke: the pieces that stayed and the pieces that moved.
nonisolated struct SketchCut: Codable, Sendable {
    var stationary: [SketchOp] = []
    var moved: [SketchOp] = []
    var placement: String?

    init(from decoder: Decoder) throws {
        // Older documents store just the list of pieces.
        if let pieces = try? decoder.singleValueContainer().decode([SketchOp].self) {
            stationary = pieces
            return
        }
        guard let container = try? decoder.container(keyedBy: CodingKeys.self) else { return }
        stationary = (try? container.decode([SketchOp].self, forKey: .stationary)) ?? []
        moved = (try? container.decode([SketchOp].self, forKey: .moved)) ?? []
        placement = try? container.decode(String.self, forKey: .placement)
    }
}

/// A text where a document should hold one, and nothing where it holds something else.
nonisolated struct LooseString: Codable, Sendable {
    var value: String?

    init(_ value: String) {
        self.value = value
    }

    init(from decoder: Decoder) throws {
        value = try? decoder.singleValueContainer().decode(String.self)
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(value)
    }
}

nonisolated extension SharedDoc {
    /// A sketch's strokes in drawing order, with cut-and-moved pieces put where they went.
    func sketchStrokes() -> [SketchOp] {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        let cutMap: YMap<SketchCut> = document.getOrCreateMap(named: "cuts")
        let (all, cuts) = document.transactSync { transaction in
            (ops.toArray(transaction: transaction), cutMap.toMap(transaction: transaction))
        }
        var result: [SketchOp] = []
        func append(_ stroke: SketchOp, _ seen: Set<String>) {
            if let relocated = stroke.relocates {
                if let cut = cuts[relocated], cut.placement == stroke.id {
                    cut.moved.forEach { append($0, seen) }
                }
                return
            }
            guard let replacement = cuts[stroke.id], !seen.contains(stroke.id) else {
                result.append(stroke)
                return
            }
            replacement.stationary.forEach { append($0, seen.union([stroke.id])) }
        }
        all.forEach { append($0, []) }
        return result
    }

    /// The page settings the sketch carries with it, which win over the instance's copy of them.
    func sketchMeta() -> [String: String] {
        let map: YMap<LooseString> = document.getOrCreateMap(named: "meta")
        return document.transactSync { map.toMap(transaction: $0) }.compactMapValues(\.value)
    }

    func setSketchMeta(_ values: [String: String]) {
        let map: YMap<LooseString> = document.getOrCreateMap(named: "meta")
        document.transactSync(origin: Self.localOrigin) { transaction in
            for (key, value) in values {
                map.updateValue(LooseString(value), forKey: key, transaction: transaction)
            }
        }
    }

    /// What a change made here is marked with, so the sketch's undo history can tell this
    /// person's changes from everyone else's. YSwift keeps its initialisers to itself, but an
    /// origin is Codable, which is a way in.
    static let localOrigin: Origin? = try? JSONDecoder().decode(Origin.self, from: Data(#"{"origin":[115,111,108,111]}"#.utf8))

    func appendSketchOp(_ op: SketchOp) {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        document.transactSync(origin: Self.localOrigin) { ops.append(op, transaction: $0) }
    }

    /// Takes everything off the page: the ink, and the text boxes the web editor adds — which
    /// this app cannot draw, but can remove, and which Undo brings back with the rest.
    func clearSketch() {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        let texts: YMap<LooseString> = document.getOrCreateMap(named: "texts")
        document.transactSync(origin: Self.localOrigin) { transaction in
            let count = Int(ops.length(transaction: transaction))
            if count > 0 { ops.removeRange(start: 0, length: count, transaction: transaction) }
            texts.removeAll(transaction: transaction)
        }
    }

    /// The history of what this person does to the sketch, for Undo and Redo.
    func sketchHistory() -> SketchHistory {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        let cuts: YMap<SketchCut> = document.getOrCreateMap(named: "cuts")
        let texts: YMap<LooseString> = document.getOrCreateMap(named: "texts")
        let meta: YMap<LooseString> = document.getOrCreateMap(named: "meta")
        return SketchHistory(manager: document.undoManager(trackedRefs: [ops, texts, meta, cuts]))
    }
}

/// Undo and Redo for a sketch: Yjs's own undo manager over the same parts of the document the
/// web editor's watches, so the two take back the same things — a stroke, a cleared page, a
/// change of paper — and never anything somebody else did.
nonisolated final class SketchHistory: @unchecked Sendable {
    private let manager: YUndoManager<NSObject>

    init(manager: YUndoManager<NSObject>) {
        self.manager = manager
        if let origin = SharedDoc.localOrigin { manager.addOrigin(origin) }
    }

    /// Ends the step being recorded, so the next change is undone on its own rather than with this one.
    func closeStep() { manager.wrap() }

    func undo() -> Bool { (try? manager.undo()) ?? false }
    func redo() -> Bool { (try? manager.redo()) ?? false }
}

nonisolated enum SketchInk: String, CaseIterable, Identifiable, Sendable {
    case theme, red, blue, green, purple

    var id: String { rawValue }
    var index: UInt8 { UInt8(Self.allCases.firstIndex(of: self)! + 1) }

    func color(themeInk: Color) -> Color {
        switch self {
        case .theme: themeInk
        case .red: Color(hex: "#b52c32")
        case .blue: Color(hex: "#2164b4")
        case .green: Color(hex: "#23763e")
        case .purple: Color(hex: "#8043ad")
        }
    }
}

nonisolated struct SketchPage: Equatable, Sendable {
    var size: String
    var orientation: String
    var pattern: String

    static let sizes = ["A3", "A4", "A5", "Letter"]
    static let patterns: [(id: String, title: String)] = [("empty", "Blank"), ("grid", "Grid"), ("notebook", "Lined"), ("dots", "Dots")]
    private static let points: [String: (Int, Int)] = ["A4": (595, 842), "A5": (420, 595), "A3": (842, 1191), "Letter": (612, 792)]

    /// The instance's record of the page, overridden by what the sketch itself says.
    init(_ sketch: DocSketch, meta: [String: String] = [:]) {
        let wanted = meta["pageSize"] ?? sketch.pageSize
        size = Self.points[wanted] == nil ? "A4" : wanted
        orientation = (meta["orientation"] ?? sketch.orientation) == "landscape" ? "landscape" : "portrait"
        let paper = meta["pattern"] ?? sketch.pattern
        pattern = Self.patterns.contains { $0.id == paper } ? paper : "empty"
    }

    var width: Int { orientation == "landscape" ? Self.points[size]!.1 : Self.points[size]!.0 }
    var height: Int { orientation == "landscape" ? Self.points[size]!.0 : Self.points[size]!.1 }
}

/// The page's ink, one byte a pixel: 0 for none, else an ink's index.
nonisolated struct SketchRaster: Sendable {
    var width: Int
    var height: Int
    var pixels: [UInt8]

    init(page: SketchPage) {
        width = page.width
        height = page.height
        pixels = [UInt8](repeating: 0, count: page.width * page.height)
    }

    static let widths = [1, 2, 3, 5, 8]

    static func pressureWidth(level: Int, pressure: Double, pointerType: String?) -> Int {
        let base = widths[min(max(level, 0), 4)]
        if pointerType == "mouse" { return base }
        return Int((Double(base) + Double(8 - base) * min(max(pressure, 0), 1) + 0.5).rounded(.down))
    }

    /// The stamp's wobble for one sample: a hash of the stroke's seed and the sample's index, as 32-bit integer arithmetic.
    static func jitter(seed: Int, index: Int, frame: Int = 0) -> (Int, Int) {
        var value = Int32(truncatingIfNeeded: seed) ^ Int32(truncatingIfNeeded: Int64(index) &* 374_761_393)
            ^ Int32(truncatingIfNeeded: Int64(frame) &* 668_265_263)
        let shifted = Int32(bitPattern: UInt32(bitPattern: value) >> 13)
        value = (value ^ shifted) &* 1_274_126_177
        let unsigned = UInt32(bitPattern: value)
        return (Int(unsigned % 3) - 1, Int((unsigned >> 8) % 3) - 1)
    }

    /// JavaScript's Math.round: halves go up.
    private static func round(_ value: Double) -> Int { Int((value + 0.5).rounded(.down)) }

    private mutating func stamp(_ x: Double, _ y: Double, size: Int, ink: UInt8) {
        let first = -(size / 2)
        let radius = Double(size) / 2
        let px = Self.round(x), py = Self.round(y)
        for dy in 0..<size {
            for dx in 0..<size {
                let cx = dx + first, cy = dy + first
                if size > 2, Double(abs(cx) + abs(cy)) > radius * 1.5 { continue }
                let tx = px + cx, ty = py + cy
                if tx >= 0, ty >= 0, tx < width, ty < height {
                    pixels[ty * width + tx] = ink
                }
            }
        }
    }

    /// `frame` picks which of the line's three wobbles is drawn: every sample is nudged by a
    /// pixel one way or another, differently in each, and showing them in turn is what makes a
    /// line shiver in the editor.
    mutating func paint(_ stroke: SketchOp, frame: Int = 0) {
        guard let points = stroke.points else { return }
        let ink: UInt8 = stroke.tool == "eraser" ? 0 : (SketchInk(rawValue: stroke.ink ?? "theme")?.index ?? 1)
        let seed = stroke.seed ?? 0
        let level = stroke.level ?? 0
        var index = 0
        var previous: (Double, Double)?
        var budget = 1 << 22

        for i in points.indices {
            let a = points[max(0, i - 1)], b = points[i]
            guard a.count >= 3, b.count >= 3, (a + b).allSatisfy(\.isFinite) else { continue }
            let steps = min(4096, max(1, Int((hypot(b[0] - a[0], b[1] - a[1])).rounded(.up))))
            var n = i == 0 ? 0 : 1
            while n <= steps, budget > 0 {
                budget -= 1
                let t = Double(n) / Double(steps)
                let x = a[0] + (b[0] - a[0]) * t, y = a[1] + (b[1] - a[1]) * t, pressure = a[2] + (b[2] - a[2]) * t
                let (jx, jy) = Self.jitter(seed: seed, index: index, frame: frame)
                index += 1
                let size = Self.pressureWidth(level: level, pressure: pressure, pointerType: stroke.pointerType)
                let target = (x + Double(jx), y + Double(jy))
                let start = previous ?? target
                let stampSteps = min(4096, max(1, Int(hypot(target.0 - start.0, target.1 - start.1).rounded(.up))))
                for s in 0...stampSteps {
                    let f = Double(s) / Double(stampSteps)
                    stamp(start.0 + (target.0 - start.0) * f, start.1 + (target.1 - start.1) * f, size: size, ink: ink)
                }
                previous = target
                n += 1
            }
        }
    }

    /// The page as an image: paper, ruling, then ink.
    func image(page: SketchPage, paper: CSSColor, themeInk: CSSColor) -> CGImage? {
        func rgba(_ color: CSSColor) -> [UInt8] {
            [UInt8(color.red * 255), UInt8(color.green * 255), UInt8(color.blue * 255), 255]
        }
        let palette: [[UInt8]] = [rgba(paper), rgba(themeInk), rgba(CSSColor("#b52c32")!), rgba(CSSColor("#2164b4")!),
                                  rgba(CSSColor("#23763e")!), rgba(CSSColor("#8043ad")!)]
        // The ruling is #d0d0d0 at half strength over the paper.
        let rule = rgba(CSSColor(red: (paper.red + 0.816) / 2, green: (paper.green + 0.816) / 2, blue: (paper.blue + 0.816) / 2))
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        for y in 0..<height {
            for x in 0..<width {
                let ink = Int(pixels[y * width + x])
                var color = palette[min(ink, 5)]
                if ink == 0 {
                    let ruled: Bool = switch page.pattern {
                    case "grid": (y % 24 == 0 && y > 0) || (x % 24 == 0 && x > 0)
                    case "notebook": y % 24 == 0 && y > 0
                    case "dots": x % 12 == 0 && y % 12 == 0 && x > 0 && y > 0
                    default: false
                    }
                    if ruled { color = rule }
                }
                let at = (y * width + x) * 4
                bytes[at] = color[0]; bytes[at + 1] = color[1]; bytes[at + 2] = color[2]; bytes[at + 3] = 255
            }
        }
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }

    @concurrent static func render(_ strokes: [SketchOp], page: SketchPage, frame: Int = 0) async -> SketchRaster {
        var raster = SketchRaster(page: page)
        strokes.forEach { raster.paint($0, frame: frame) }
        return raster
    }

    /// How many wobbles a line has, and how long each is shown: the web editor's "boil".
    static let boilFrames = 3
    static let boilInterval: Duration = .milliseconds(250)
}

/// A sketch, kept live: its strokes, the page drawn from them, and drawing on it.
@Observable
final class SketchModel: Identifiable {
    let sketch: DocSketch
    let sync: DocSync
    private(set) var page: SketchPage
    private(set) var image: CGImage?
    private(set) var strokeCount = 0

    @ObservationIgnored private let session: Session
    @ObservationIgnored private let projectId: UUID
    @ObservationIgnored private let vaultId: UUID
    /// The page as drawn so far: once, or once per wobble of the line when it is shown shivering.
    @ObservationIgnored private var frames: [SketchRaster]
    @ObservationIgnored private var frame = 0
    /// Whether lines shiver, as they do in the web editor. Off for Reduce Motion.
    @ObservationIgnored var boils = false {
        didSet {
            if boils != oldValue { rebuild() }
        }
    }
    @ObservationIgnored private let history: SketchHistory
    /// How many steps there are to take back and to put back. The history itself only says
    /// whether an undo did anything, so the count is kept alongside it.
    private var undoDepth = 0
    private var redoDepth = 0
    @ObservationIgnored private var renderTask: Task<Void, Never>?
    /// The stroke in hand, and the ones other people are in the middle of.
    @ObservationIgnored private var held: SketchOp?
    @ObservationIgnored private var othersStrokes: [SketchOp] = []
    /// Strokes someone has just finished, shown until they arrive as part of the sketch.
    @ObservationIgnored private var lingering: [SketchOp] = []
    /// The strokes the page as rendered already has.
    @ObservationIgnored private var drawn: Set<String> = []
    /// How much of the stroke in hand the others have been sent.
    @ObservationIgnored private var delivered: (id: String?, count: Int) = (nil, 0)
    /// The page being cleared: nothing is drawn until it has gone.
    private(set) var clearing = false
    /// The explosion that takes a cleared page with it, while it plays.
    private(set) var blast: SketchBlast?
    @ObservationIgnored private var effect: SketchBlast.Effect?
    /// The page and its default ink follow the appearance, as they follow the theme on the web.
    @ObservationIgnored var colors = SketchColors.light {
        didSet {
            if colors != oldValue { compose() }
        }
    }

    init(session: Session, projectId: UUID, vaultId: UUID, sketch: DocSketch, mayWrite: Bool) {
        self.session = session
        self.projectId = projectId
        self.vaultId = vaultId
        self.sketch = sketch
        let page = SketchPage(sketch)
        self.page = page
        frames = [SketchRaster(page: page)]
        sync = DocSync(session: session, projectId: projectId, documentId: sketch.id, mayWrite: mayWrite)
        history = sync.shared.sketchHistory()
        watchPresence()
    }

    var canUndo: Bool { undoDepth > 0 && !clearing }
    var canRedo: Bool { redoDepth > 0 && !clearing }

    /// A change was made here: it is a step of its own, and there is nothing left to redo.
    private func recorded() {
        history.closeStep()
        undoDepth += 1
        redoDepth = 0
        sync.changedLocally(promptly: true)
    }

    /// Replays the whole history. Run when the document changes underneath: at load, and when someone else draws.
    func rebuild() {
        let strokes = sync.shared.sketchStrokes()
        let page = SketchPage(sketch, meta: sync.shared.sketchMeta())
        if page != self.page {
            self.page = page
        }
        renderTask?.cancel()
        let count = boils ? SketchRaster.boilFrames : 1
        renderTask = Task { [weak self] in
            let rasters = await withTaskGroup(of: (Int, SketchRaster).self) { group in
                for number in 0..<count {
                    group.addTask { (number, await SketchRaster.render(strokes, page: page, frame: number)) }
                }
                var rendered: [Int: SketchRaster] = [:]
                for await (number, raster) in group { rendered[number] = raster }
                return (0..<count).compactMap { rendered[$0] }
            }
            guard !Task.isCancelled, let self, rasters.count == count else { return }
            self.frames = rasters
            self.strokeCount = strokes.count
            self.drawn = Set(strokes.map(\.id))
            self.lingering.removeAll { self.drawn.contains($0.id) }
            self.compose()
        }
    }

    /// The page as it is shown: what has been drawn, with every stroke still in the air over it —
    /// the one in hand here, and the ones other people are in the middle of.
    private func compose() {
        let floating = ([held] + othersStrokes + lingering).compactMap { $0 }
        let number = frame % frames.count
        guard !floating.isEmpty else {
            image = frames[number].image(page: page, paper: colors.paper, themeInk: colors.ink)
            return
        }
        var raster = frames[number]
        floating.forEach { raster.paint($0, frame: number) }
        image = raster.image(page: page, paper: colors.paper, themeInk: colors.ink)
    }

    /// Shows the line's next wobble. Called on a timer while the editor is open.
    func boil() {
        guard frames.count > 1, strokeCount > 0 || held != nil || !othersStrokes.isEmpty else { return }
        frame = (frame + 1) % frames.count
        compose()
    }

    /// The stroke in hand, drawn over a copy of the page; nothing is saved until it is lifted.
    func preview(_ stroke: SketchOp) {
        held = stroke
        compose()
        publish(stroke)
    }

    /// Puts the page back as it was before a stroke that was given up.
    func discardPreview() {
        held = nil
        compose()
        publish(nil)
    }

    func commit(_ stroke: SketchOp) {
        held = nil
        for number in frames.indices { frames[number].paint(stroke, frame: number) }
        compose()
        sync.shared.appendSketchOp(stroke)
        strokeCount += 1
        recorded()
        publish(nil)
    }

    // MARK: Everyone else's pens

    /// Other people's pens on the page, for drawing their cursors.
    var pointers: [SketchPointer] {
        _ = sync.awareness.revision
        return sync.awareness.peers.values.compactMap { peer in
            peer.pointer.map { SketchPointer(client: peer.client, user: peer.user, pointer: $0) }
        }
        .sorted { $0.client < $1.client }
    }

    /// Someone's pen moved, or their stroke grew: the page is put together again with it.
    func othersChanged() {
        let strokes = sync.awareness.peers.values.sorted { $0.client < $1.client }
            .compactMap { $0.stroke?.op }.filter { !($0.points ?? []).isEmpty }
        guard strokes != othersStrokes else { return }
        // A stroke stops being "in progress" a moment before it arrives as part of the sketch.
        // It stays on the page across that gap rather than blinking out and back.
        let finished = othersStrokes.filter { old in !strokes.contains { $0.id == old.id } && !drawn.contains(old.id) }
        othersStrokes = strokes
        if !finished.isEmpty {
            lingering.append(contentsOf: finished)
            let ids = Set(finished.map(\.id))
            Task { [weak self] in
                // One that was given up instead never arrives: it goes after a beat.
                try? await Task.sleep(for: .milliseconds(700))
                guard let self, self.lingering.contains(where: { ids.contains($0.id) }) else { return }
                self.lingering.removeAll { ids.contains($0.id) }
                self.compose()
            }
        }
        compose()
    }

    /// Tells the others where this pen is and what it has drawn so far of the stroke in hand.
    /// A stroke is sent a piece at a time — the samples not yet delivered, oldest first, at most
    /// as many as one message may carry — so that someone arriving mid-stroke is caught up.
    private func publish(_ stroke: SketchOp?) {
        guard sync.canWrite else { return }
        let awareness = sync.awareness
        guard let stroke, let last = stroke.points?.last, last.count >= 2 else {
            awareness.set("stroke", nil)
            awareness.set("pointer", nil)
            return
        }
        let width = SketchRaster.pressureWidth(level: stroke.level ?? 0, pressure: last.count > 2 ? last[2] : 0,
                                               pointerType: stroke.pointerType)
        awareness.set("pointer", ["x": last[0], "y": last[1], "tool": stroke.tool ?? "pen", "width": width])
        awareness.set("stroke", presence(of: stroke))
    }

    private func presence(of stroke: SketchOp) -> [String: Any] {
        let points = stroke.points ?? []
        let base = min(max(delivered.id == stroke.id ? delivered.count : 0, 0), points.count)
        func round(_ value: Double, _ scale: Double) -> Double { value.isFinite ? (value * scale).rounded() / scale : 0 }
        var message: [String: Any] = ["id": stroke.id, "base": base]
        message["tool"] = stroke.tool
        message["ink"] = stroke.ink
        message["level"] = stroke.level
        message["seed"] = stroke.seed
        message["pointerType"] = stroke.pointerType
        // Whole pixels are what is drawn; hundredths keep the shape and halve the message.
        message["points"] = points[base..<min(base + Self.presenceLimit, points.count)].map { point in
            [round(point[0], 100), round(point[1], 100), round(point.count > 2 ? point[2] : 0, 1000)]
        }
        return message
    }

    /// The most samples of a stroke one presence message carries, as on the web.
    private static let presenceLimit = 512

    /// Starts listening for what the presence channel says back.
    private func watchPresence() {
        sync.onAwarenessSent = { [weak self] sent in
            guard let self, let sent, let id = sent["id"] as? String, id == self.held?.id,
                  let base = sent["base"] as? Int, let points = sent["points"] as? [Any] else { return }
            self.delivered = (id, max(self.delivered.id == id ? self.delivered.count : 0, base + points.count))
            // More was drawn while that was on its way: send the next piece.
            if let held = self.held, self.delivered.count < (held.points?.count ?? 0), self.sync.canWrite {
                self.sync.awareness.set("stroke", self.presence(of: held))
            }
        }
        sync.onPeerJoined = { [weak self] in
            // Someone new has none of the stroke in hand: start it again for them.
            guard let self else { return }
            self.delivered = (nil, 0)
            if let held = self.held, self.sync.canWrite {
                self.sync.awareness.set("stroke", self.presence(of: held))
            }
        }
    }

    func undo() {
        guard canUndo else { return }
        guard history.undo() else {
            undoDepth = 0
            return
        }
        undoDepth -= 1
        redoDepth += 1
        sync.changedLocally()
        rebuild()
    }

    func redo() {
        guard canRedo else { return }
        guard history.redo() else {
            redoDepth = 0
            return
        }
        redoDepth -= 1
        undoDepth += 1
        sync.changedLocally()
        rebuild()
    }

    // MARK: Clearing the page

    /// Fetches the explosion and its sound from the instance, where the extension keeps them,
    /// so that clearing doesn't have to wait for them. Without them a page still clears.
    func prepareEffects() async {
        guard effect == nil else { return }
        let content = "api/extensions/design-docs/content/"
        async let picture = try? session.api.bytes(content + "explosion.gif").0
        async let sound = try? session.api.bytes(content + "explosion.wav").0
        effect = await SketchBlast.Effect(gif: picture, sound: sound)
    }

    /// Clears the page the way the web editor does: with a bang, the ink going at the height
    /// of it. One step of the history, so Undo brings everything back.
    func clearPage(reduceMotion: Bool) {
        guard sync.canWrite, !clearing else { return }
        clearing = true
        effect?.playSound()
        // Felt with the sound, Reduce Motion or not.
        Haptics.shared.play(.explosion)
        let frames = effect?.frames ?? []
        Task {
            // With Reduce Motion, or with no picture to show, the page is simply cleared.
            if reduceMotion || frames.isEmpty {
                erase()
                try? await Task.sleep(for: .milliseconds(100))
            } else {
                let duration = effect?.duration ?? 1.19
                blast = SketchBlast(frames: frames, duration: duration)
                try? await Task.sleep(for: .seconds(duration / 2))
                erase()
                try? await Task.sleep(for: .seconds(duration / 2))
                blast = nil
            }
            clearing = false
        }
    }

    private func erase() {
        guard sync.canWrite else { return }
        // A step of its own at both ends: not folded into the stroke before it.
        history.closeStep()
        sync.shared.clearSketch()
        recorded()
        rebuild()
    }

    /// Changes the paper. The sketch carries the setting, so everyone drawing
    /// on it sees the page change; the instance keeps a copy for the previews
    /// and the attachment list, as the web editor's does.
    func setPage(size: String? = nil, orientation: String? = nil, pattern: String? = nil) {
        guard sync.canWrite else { return }
        let wanted = ["pageSize": size ?? page.size, "orientation": orientation ?? page.orientation, "pattern": pattern ?? page.pattern]
        sync.shared.setSketchMeta(wanted)
        recorded()
        rebuild()
        Task {
            await session.perform {
                guard let current = try await session.api.docSketches(projectId, vaultId).first(where: { $0.id == sketch.id })
                else { return }
                _ = try await session.api.changeDocSketch(projectId, vaultId, current, pageSize: wanted["pageSize"]!,
                                                         orientation: wanted["orientation"]!, pattern: wanted["pattern"]!)
            }
        }
    }
}

/// Someone else's pen on the page.
struct SketchPointer: Equatable {
    var client: UInt64
    var user: AwarenessUser
    var pointer: AwarenessPeer.Pointer
}

/// One playing of the explosion over the page.
struct SketchBlast: Equatable {
    var token = UUID()
    var frames: [PlatformImage]
    var duration: TimeInterval

    static func == (a: SketchBlast, b: SketchBlast) -> Bool { a.token == b.token }

    /// The explosion's frames and its sound, as fetched.
    final class Effect {
        let frames: [PlatformImage]
        let duration: TimeInterval
        private let player: AVAudioPlayer?

        init(gif: Data?, sound: Data?) {
            var frames: [PlatformImage] = []
            var duration: TimeInterval = 0
            if let gif, let source = CGImageSourceCreateWithData(gif as CFData, nil) {
                for index in 0..<CGImageSourceGetCount(source) {
                    guard let image = CGImageSourceCreateImageAtIndex(source, index, nil) else { continue }
                    frames.append(PlatformImage.picture(image))
                    let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
                    let timing = properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
                    let delay = (timing?[kCGImagePropertyGIFUnclampedDelayTime] as? Double)
                        ?? (timing?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
                    // Browsers play a frame that asks for no time at all for a tenth of a second.
                    duration += delay < 0.011 ? 0.1 : delay
                }
            }
            self.frames = frames
            self.duration = duration
            player = sound.flatMap { try? AVAudioPlayer(data: $0) }
            player?.prepareToPlay()
        }

        func playSound() {
            player?.currentTime = 0
            player?.play()
        }
    }
}

nonisolated struct SketchColors: Equatable, Sendable {
    var paper: CSSColor
    var ink: CSSColor

    static let light = SketchColors(paper: CSSColor(red: 1, green: 1, blue: 1), ink: CSSColor(red: 0.07, green: 0.07, blue: 0.07))
    static let dark = SketchColors(paper: CSSColor(red: 0.11, green: 0.11, blue: 0.118), ink: CSSColor(red: 0.95, green: 0.95, blue: 0.97))

    static func of(_ scheme: ColorScheme) -> SketchColors { scheme == .dark ? dark : light }
}

/// A sketch as it was last stored: its page, and the ink on it.
nonisolated struct SketchSnapshot: Sendable {
    var page: SketchPage
    var raster: SketchRaster

    /// Built from a sketch's stored updates, for a preview that never joins the sketch's live topic.
    @concurrent static func load(_ updates: [String], sketch: DocSketch) async -> SketchSnapshot {
        let shared = SharedDoc()
        shared.apply(updates.compactMap { Data(base64Encoded: $0).map { [UInt8]($0) } })
        let page = SketchPage(sketch, meta: shared.sketchMeta())
        var raster = SketchRaster(page: page)
        shared.sketchStrokes().forEach { raster.paint($0) }
        return SketchSnapshot(page: page, raster: raster)
    }
}

/// The sketch at full size, with a pen, an eraser, inks and widths. Strokes
/// are the same operations the web editor writes, so the two draw on one page.
struct SketchEditor: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    @Environment(\.colorScheme) private var colorScheme
    var model: SketchModel

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var tool = "pen"
    @State private var ink: SketchInk = .theme
    @State private var level = 1
    @State private var confirmingClear = false
    /// Whether a finger draws or moves the page. Until someone chooses, it is what the system
    /// says of the Pencil: fingers draw unless Settings keeps drawing for the Pencil alone.
    @AppStorage("sketch.fingerDraws") private var fingerChoice: Bool?
    @State private var moved = false
    @State private var fitRequest = 0

    private var fingerDraws: Bool {
        #if os(iOS)
        fingerChoice ?? !UIPencilInteraction.prefersPencilOnlyDrawing
        #else
        true
        #endif
    }

    var body: some View {
        NavigationStack {
            ZStack(alignment: .bottomTrailing) {
                GeometryReader { proxy in
                    SketchCanvas(model: model, image: model.image, page: model.page,
                                 canDraw: model.sync.canWrite && !model.clearing, tool: tool, ink: ink, level: level,
                                 blast: model.blast, pointers: model.pointers, dark: colorScheme == .dark,
                                 fingerDraws: fingerDraws, bars: proxy.safeAreaInsets, fitRequest: fitRequest) {
                        // A double-tap on the Pencil: to the eraser, and back to the pen.
                        tool = tool == "pen" ? "eraser" : "pen"
                    } onMoved: { moved = $0 }
                        // The page goes under the bars, which are glass; it is fitted clear of them.
                        .ignoresSafeArea()
                }
                if moved {
                    // Once the page has been moved, zoomed or turned: the way back.
                    Button("Fit Page", systemImage: "arrow.down.right.and.arrow.up.left") { fitRequest += 1 }
                        .labelStyle(.iconOnly)
                        .font(.body.weight(.medium))
                        .frame(width: 44, height: 44)
                        .glassEffect(.regular.interactive(), in: .circle)
                        .padding(.horizontal, 16)
                        .padding(.bottom, 10)
                        .transition(.scale(scale: 0.6).combined(with: .opacity))
                }
            }
                .animation(.snappy, value: moved)
                .themedBackground()
                // What a Mac calls the window; the bar shows the name with who else is drawing.
                .navigationTitle(model.sketch.name)
                .titleIsInBar()
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Done") { dismiss() }
                    }
                    ToolbarItem(placement: .principal) {
                        HStack(spacing: 10) {
                            VStack(spacing: 0) {
                                Text(model.sketch.name).font(.headline).lineLimit(1)
                                Text(model.sync.statusText).font(.caption2).foregroundStyle(theme.textSecondary)
                            }
                            // Who else is drawing, each ringed in the colour of their pen.
                            NotePresence(entries: model.sync.present, slots: model.sync.colorSlots)
                        }
                    }
                    if model.sync.canWrite {
                        ToolbarItemGroup(placement: .primaryAction) {
                            Menu("Page", systemImage: "doc.badge.gearshape") {
                                Picker("Paper", selection: Binding(get: { model.page.pattern }, set: { model.setPage(pattern: $0) })) {
                                    ForEach(SketchPage.patterns, id: \.id) { Text($0.title).tag($0.id) }
                                }
                                Picker("Size", selection: Binding(get: { model.page.size }, set: { model.setPage(size: $0) })) {
                                    ForEach(SketchPage.sizes, id: \.self) { Text($0).tag($0) }
                                }
                                Picker("Orientation", selection: Binding(get: { model.page.orientation },
                                                                         set: { model.setPage(orientation: $0) })) {
                                    Label("Portrait", systemImage: "rectangle.portrait").tag("portrait")
                                    Label("Landscape", systemImage: "rectangle").tag("landscape")
                                }
                                #if os(iOS)
                                Divider()
                                // Off, one finger moves the page and only the Pencil draws, as on the web.
                                Toggle("Draw with Finger", systemImage: "hand.draw",
                                       isOn: Binding(get: { fingerDraws }, set: { fingerChoice = $0 }))
                                    .menuToggles()
                                #endif
                                Divider()
                                Button("Clear Sketch…", systemImage: "trash", role: .destructive) { confirmingClear = true }
                                    .disabled(model.clearing)
                            }
                            Button("Undo", systemImage: "arrow.uturn.backward") { model.undo() }
                                .disabled(!model.canUndo)
                            Button("Redo", systemImage: "arrow.uturn.forward") { model.redo() }
                                .disabled(!model.canRedo)
                        }
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    if model.sync.canWrite {
                        tools
                    }
                }
        }
        .confirmationDialog("Clear this sketch?", isPresented: $confirmingClear, titleVisibility: .visible) {
            Button("Clear Sketch", role: .destructive) { model.clearPage(reduceMotion: reduceMotion) }
        } message: {
            Text("Undo can restore it.")
        }
        .task(id: model.sync.canWrite) {
            if model.sync.canWrite { await model.prepareEffects() }
        }
        .task(id: reduceMotion) {
            // Lines shiver here as they do in the web editor: three wobbles of the same line, in turn.
            model.boils = !reduceMotion
            while !Task.isCancelled, !reduceMotion {
                try? await Task.sleep(for: SketchRaster.boilInterval)
                model.boil()
            }
        }
        .task {
            model.colors = .of(colorScheme)
            await model.sync.start()
        }
        .onChange(of: colorScheme) { _, scheme in model.colors = .of(scheme) }
        .onChange(of: model.sync.remoteRevision) { _, _ in model.rebuild() }
        .onChange(of: model.sync.awareness.revision) { _, _ in model.othersChanged() }
        .onChange(of: model.sync.synced) { _, synced in
            if synced { model.rebuild() }
        }
        .onDisappear { Task { await model.sync.stop() } }
    }

    private var tools: some View {
        HStack(spacing: 14) {
            Picker("Tool", selection: $tool) {
                Label("Pen", systemImage: "pencil.tip").tag("pen")
                Label("Eraser", systemImage: "eraser").tag("eraser")
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .labelStyle(.iconOnly)
            .frame(width: 104)

            HStack(spacing: 10) {
                ForEach(SketchInk.allCases) { option in
                    let chosen = ink == option && tool == "pen"
                    Button {
                        ink = option
                        tool = "pen"
                    } label: {
                        Circle().fill(option.color(themeInk: model.colors.ink.color))
                            .frame(width: 22, height: 22)
                            .padding(4)
                            .overlay(Circle().strokeBorder(theme.primary, lineWidth: chosen ? 2.5 : 0))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("\(option.rawValue.capitalized) ink")
                    .accessibilityAddTraits(chosen ? .isSelected : [])
                }
            }

            Menu {
                Picker("Width", selection: $level) {
                    ForEach(0..<5, id: \.self) { Text("\(SketchRaster.widths[$0]) px").tag($0) }
                }
            } label: {
                Label("\(SketchRaster.widths[level]) px", systemImage: "lineweight")
                    .font(.subheadline.weight(.medium))
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .glassEffect(.regular.interactive(), in: .capsule)
        .padding(.bottom, 8)
    }
}

#if os(iOS)
/// The page itself: the picture of it, everyone else's pens over it, and the stroke being
/// drawn. It takes no touches — the viewport around it does, and says which of them draw.
final class SketchPageView: UIView {
    weak var model: SketchModel?
    var canDraw = false
    var tool = "pen"
    var ink: SketchInk = .theme
    var level = 1

    private var active: SketchOp?
    /// The touch that is drawing, while one is.
    private(set) weak var drawing: UITouch?
    var drawingWithPencil: Bool { drawing?.type == .pencil }
    private var blastView: UIImageView?
    private var playing: UUID?
    private var markers: [UInt64: SketchPointerView] = [:]
    private var zoom: CGFloat = 1
    private var angle: CGFloat = 0

    /// Draws everyone else's pens where they are on the page.
    func show(_ pointers: [SketchPointer], dark: Bool) {
        var shown = Set<UInt64>()
        for entry in pointers {
            let spot = CGPoint(x: entry.pointer.x, y: entry.pointer.y)
            let color = PresenceColor.color(entry.user.color, dark: dark)
            let marker = markers[entry.client] ?? SketchPointerView()
            marker.show(entry, color: color, zoom: zoom, angle: angle)
            if marker.superview == nil {
                marker.center = spot
                addSubview(marker)
                markers[entry.client] = marker
            } else if marker.center != spot {
                // A short glide between the places it is heard from, as the web draws it.
                UIView.animate(withDuration: 0.08, delay: 0, options: [.beginFromCurrentState, .curveEaseOut]) {
                    marker.center = spot
                }
            }
            shown.insert(entry.client)
        }
        for (client, marker) in markers where !shown.contains(client) {
            marker.removeFromSuperview()
            markers[client] = nil
        }
    }

    /// Cursors keep their size on screen, and stay upright, however the page is zoomed and turned.
    func viewChanged(zoom: CGFloat, angle: CGFloat) {
        guard zoom != self.zoom || angle != self.angle else { return }
        self.zoom = zoom
        self.angle = angle
        markers.values.forEach { $0.viewChanged(zoom: zoom, angle: angle) }
    }

    /// Plays the explosion over the page — stretched to it, as on the web, and so zoomed and
    /// moved with it — or takes it away once it is over.
    func play(_ blast: SketchBlast?) {
        guard blast?.token != playing else { return }
        playing = blast?.token
        blastView?.removeFromSuperview()
        blastView = nil
        guard let blast else { return }
        abandon()
        let view = UIImageView(frame: bounds)
        view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.contentMode = .scaleToFill
        view.layer.magnificationFilter = .nearest
        view.animationImages = blast.frames
        view.animationDuration = blast.duration
        view.animationRepeatCount = 1
        addSubview(view)
        view.startAnimating()
        blastView = view
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        // Pixels stay pixels however far in the page is zoomed.
        layer.magnificationFilter = .nearest
        layer.minificationFilter = .trilinear
        layer.shadowColor = UIColor.black.cgColor
        layer.shadowOpacity = 0.18
        layer.shadowRadius = 10
        layer.shadowOffset = CGSize(width: 0, height: 2)
        // Said outright, so that the shadow is not worked out from the page itself: that is done
        // by drawing the page aside at the size it is zoomed to, which is the first thing to
        // give out as it grows (`ZoomLimit`).
        layer.shadowPath = UIBezierPath(rect: bounds).cgPath
        isAccessibilityElement = true
        accessibilityLabel = "Sketch page"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// The paper changed size or was turned: its shadow is the new page's.
    override var bounds: CGRect {
        didSet { if bounds.size != oldValue.size { layer.shadowPath = UIBezierPath(rect: bounds).cgPath } }
    }

    /// A touch as a point of a stroke: page pixels, and how hard it is pressed
    /// where the hardware says. A finger has no pressure, so it draws the chosen width.
    private func point(_ touch: UITouch) -> [Double] {
        let location = touch.preciseLocation(in: self)
        let pressure = touch.type == .pencil && touch.maximumPossibleForce > 0
            ? Double(min(max(touch.force / touch.maximumPossibleForce, 0), 1)) : 0
        return [Double(location.x), Double(location.y), pressure]
    }

    /// A touch starts a stroke.
    func begin(_ touch: UITouch) {
        guard canDraw, active == nil, let model else { return }
        drawing = touch
        let kind = switch touch.type {
        case .pencil: "pen"
        case .indirectPointer: "mouse"
        default: "touch"
        }
        active = SketchOp(id: UUID().uuidString.lowercased(), tool: tool, ink: ink.rawValue, level: level,
                          seed: Int.random(in: 0..<2_147_483_647), pointerType: kind, points: [point(touch)])
        model.sync.report(editing: true)
        if let active { model.preview(active) }
    }

    /// The stroke follows its touch, through every sample taken since the last frame.
    func extend(_ touch: UITouch, with event: UIEvent?) {
        guard touch === drawing, active != nil, let model else { return }
        for sample in event?.coalescedTouches(for: touch) ?? [touch] {
            active?.points?.append(point(sample))
        }
        if let active { model.preview(active) }
    }

    /// The touch lifted: the stroke is on the page.
    func finish() {
        guard let stroke = active, let model else { return }
        active = nil
        drawing = nil
        model.commit(stroke)
        model.sync.report(editing: false)
    }

    /// The stroke was not one after all — a second finger landed, or the system took the touch.
    func abandon() {
        guard active != nil else { return }
        active = nil
        drawing = nil
        model?.discardPreview()
        model?.sync.report(editing: false)
    }
}

/// Someone else's pen: an arrow with their initials in its tail, in their colour, and a ring
/// the size of the mark their pen or eraser would leave.
final class SketchPointerView: UIView {
    private let ring = CAShapeLayer()
    private let arrow = CAShapeLayer()
    private let label = UILabel()
    private let cursor = UIView(frame: CGRect(x: 0, y: 0, width: 36, height: 40))
    private var width: CGFloat = 1

    // The web's cursor: a 36 by 40 shape whose tip is at (2, 2) and whose tail is a circle about (23, 27).
    private static let tip = CGPoint(x: 2, y: 2)
    private static let middle = CGPoint(x: 23, y: 27)
    private static let radius: CGFloat = 11

    init() {
        super.init(frame: .zero)
        isUserInteractionEnabled = false
        ring.fillColor = UIColor.clear.cgColor
        layer.addSublayer(ring)

        arrow.path = Self.outline()
        arrow.strokeColor = UIColor.white.withAlphaComponent(0.9).cgColor
        arrow.lineWidth = 1
        arrow.lineJoin = .round
        cursor.layer.addSublayer(arrow)
        label.frame = CGRect(x: Self.middle.x - Self.radius, y: Self.middle.y - Self.radius, width: Self.radius * 2, height: Self.radius * 2)
        label.font = .systemFont(ofSize: 9, weight: .bold)
        label.textColor = .white
        label.textAlignment = .center
        cursor.addSubview(label)
        // Scaled about its tip, which is the point it marks.
        cursor.layer.anchorPoint = CGPoint(x: Self.tip.x / 36, y: Self.tip.y / 40)
        cursor.layer.position = .zero
        addSubview(cursor)
        isAccessibilityElement = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// The arrow's two edges leave its tip and meet the circle where they touch it.
    private static func outline() -> CGPath {
        let vx = tip.x - middle.x, vy = tip.y - middle.y
        let distance = vx * vx + vy * vy
        let along = radius * radius / distance, across = radius * sqrt(distance - radius * radius) / distance
        let first = CGPoint(x: middle.x + along * vx - across * vy, y: middle.y + along * vy + across * vx)
        let second = CGPoint(x: middle.x + along * vx + across * vy, y: middle.y + along * vy - across * vx)
        let path = CGMutablePath()
        path.move(to: tip)
        path.addLine(to: first)
        let start = atan2(first.y - middle.y, first.x - middle.x), end = atan2(second.y - middle.y, second.x - middle.x)
        // The long way round, away from the tip.
        path.addArc(center: middle, radius: radius, startAngle: start, endAngle: end, clockwise: true)
        path.closeSubpath()
        return path
    }

    func show(_ entry: SketchPointer, color: UIColor, zoom: CGFloat, angle: CGFloat) {
        arrow.fillColor = color.cgColor
        ring.strokeColor = color.cgColor
        label.text = entry.user.initials
        accessibilityLabel = "\(entry.user.name)'s pen"
        ring.isHidden = !["pen", "eraser"].contains(entry.pointer.tool)
        width = max(CGFloat(entry.pointer.width), 1)
        viewChanged(zoom: zoom, angle: angle)
    }

    /// Undoes the page's zoom and turn for the arrow, about its tip, so that it reads the same
    /// wherever the page has been taken; the ring is on the page, and goes with it.
    func viewChanged(zoom: CGFloat, angle: CGFloat) {
        let scale = 1 / max(zoom, 0.01)
        cursor.transform = CGAffineTransform(rotationAngle: -angle).scaledBy(x: scale, y: scale)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        ring.lineWidth = scale
        ring.path = CGPath(ellipseIn: CGRect(x: -width / 2, y: -width / 2, width: width, height: width), transform: nil)
        CATransaction.commit()
    }
}
#endif
