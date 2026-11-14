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
        document.transactSync { transaction in
            for (key, value) in values {
                map.updateValue(LooseString(value), forKey: key, transaction: transaction)
            }
        }
    }

    func appendSketchOp(_ op: SketchOp) {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        document.transactSync { ops.append(op, transaction: $0) }
    }

    /// Takes back the newest stroke with this id, if it is still the last one drawn.
    func removeSketchOp(id: String) -> Bool {
        let ops: YArray<SketchOp> = document.getOrCreateArray(named: "ops")
        return document.transactSync { transaction in
            let all = ops.toArray(transaction: transaction)
            guard let index = all.lastIndex(where: { $0.id == id }) else { return false }
            ops.remove(at: index, transaction: transaction)
            return true
        }
    }
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

    mutating func paint(_ stroke: SketchOp) {
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
                let (jx, jy) = Self.jitter(seed: seed, index: index)
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

    @concurrent static func render(_ strokes: [SketchOp], page: SketchPage) async -> SketchRaster {
        var raster = SketchRaster(page: page)
        strokes.forEach { raster.paint($0) }
        return raster
    }
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
    @ObservationIgnored private var committed: SketchRaster
    @ObservationIgnored private var mine: [String] = []
    @ObservationIgnored private var renderTask: Task<Void, Never>?
    /// The page and its default ink follow the appearance, as they follow the theme on the web.
    @ObservationIgnored var colors = SketchColors.light {
        didSet {
            if colors != oldValue { image = committed.image(page: page, paper: colors.paper, themeInk: colors.ink) }
        }
    }

    init(session: Session, projectId: UUID, vaultId: UUID, sketch: DocSketch, mayWrite: Bool) {
        self.session = session
        self.projectId = projectId
        self.vaultId = vaultId
        self.sketch = sketch
        let page = SketchPage(sketch)
        self.page = page
        committed = SketchRaster(page: page)
        sync = DocSync(session: session, projectId: projectId, documentId: sketch.id, mayWrite: mayWrite)
    }

    var canUndo: Bool { !mine.isEmpty }

    /// Replays the whole history. Run when the document changes underneath: at load, and when someone else draws.
    func rebuild() {
        let strokes = sync.shared.sketchStrokes()
        let page = SketchPage(sketch, meta: sync.shared.sketchMeta())
        if page != self.page {
            self.page = page
        }
        renderTask?.cancel()
        renderTask = Task { [weak self] in
            let raster = await SketchRaster.render(strokes, page: page)
            guard !Task.isCancelled, let self else { return }
            self.committed = raster
            self.strokeCount = strokes.count
            self.image = raster.image(page: page, paper: self.colors.paper, themeInk: self.colors.ink)
        }
    }

    /// The stroke in hand, drawn over a copy of the page; nothing is saved until it is lifted.
    func preview(_ stroke: SketchOp) {
        var raster = committed
        raster.paint(stroke)
        image = raster.image(page: page, paper: colors.paper, themeInk: colors.ink)
    }

    /// Puts the page back as it was before a stroke that was given up.
    func discardPreview() {
        image = committed.image(page: page, paper: colors.paper, themeInk: colors.ink)
    }

    func commit(_ stroke: SketchOp) {
        committed.paint(stroke)
        image = committed.image(page: page, paper: colors.paper, themeInk: colors.ink)
        sync.shared.appendSketchOp(stroke)
        mine.append(stroke.id)
        strokeCount += 1
        sync.changedLocally()
    }

    func undo() {
        guard let id = mine.popLast(), sync.shared.removeSketchOp(id: id) else { return }
        sync.changedLocally()
        rebuild()
    }

    /// Changes the paper. The sketch carries the setting, so everyone drawing
    /// on it sees the page change; the instance keeps a copy for the previews
    /// and the attachment list, as the web editor's does.
    func setPage(size: String? = nil, orientation: String? = nil, pattern: String? = nil) {
        guard sync.canWrite else { return }
        let wanted = ["pageSize": size ?? page.size, "orientation": orientation ?? page.orientation, "pattern": pattern ?? page.pattern]
        sync.shared.setSketchMeta(wanted)
        sync.changedLocally()
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

    @State private var tool = "pen"
    @State private var ink: SketchInk = .theme
    @State private var level = 1

    var body: some View {
        NavigationStack {
            SketchCanvas(model: model, image: model.image, page: model.page, canDraw: model.sync.canWrite, tool: tool, ink: ink,
                         level: level)
                .ignoresSafeArea(edges: .bottom)
                .themedBackground()
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Done") { dismiss() }
                    }
                    ToolbarItem(placement: .principal) {
                        VStack(spacing: 0) {
                            Text(model.sketch.name).font(.headline).lineLimit(1)
                            Text(model.sync.statusText).font(.caption2).foregroundStyle(theme.textSecondary)
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
                            }
                            Button("Undo", systemImage: "arrow.uturn.backward") { model.undo() }
                                .disabled(!model.canUndo)
                        }
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    if model.sync.canWrite {
                        tools
                    }
                }
        }
        .task {
            model.colors = .of(colorScheme)
            await model.sync.start()
        }
        .onChange(of: colorScheme) { _, scheme in model.colors = .of(scheme) }
        .onChange(of: model.sync.remoteRevision) { _, _ in model.rebuild() }
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

/// The page under the hand. One finger or the Pencil draws; two fingers move
/// and zoom the page, which is the scroll view's job, so a stroke in progress
/// is dropped the moment a second finger lands.
struct SketchCanvas: UIViewRepresentable {
    var model: SketchModel
    var image: CGImage?
    var page: SketchPage
    var canDraw: Bool
    var tool: String
    var ink: SketchInk
    var level: Int

    func makeUIView(context: Context) -> SketchScrollView {
        let view = SketchScrollView(page: CGSize(width: page.width, height: page.height))
        view.pageView.model = model
        return view
    }

    func updateUIView(_ view: SketchScrollView, context: Context) {
        view.setPage(CGSize(width: self.page.width, height: self.page.height))
        let page = view.pageView
        page.layer.contents = image
        page.canDraw = canDraw
        page.tool = tool
        page.ink = ink
        page.level = level
    }
}

final class SketchScrollView: UIScrollView, UIScrollViewDelegate {
    let pageView: SketchPageView
    private var fitted: CGSize = .zero

    init(page: CGSize) {
        pageView = SketchPageView(frame: CGRect(origin: .zero, size: page))
        super.init(frame: .zero)
        delegate = self
        addSubview(pageView)
        contentSize = page
        // One finger belongs to the pen.
        panGestureRecognizer.minimumNumberOfTouches = 2
        delaysContentTouches = false
        showsVerticalScrollIndicator = false
        showsHorizontalScrollIndicator = false
        backgroundColor = .clear
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// The paper changed size or turned: lay the new page out and fit it afresh.
    func setPage(_ size: CGSize) {
        guard pageView.bounds.size != size else { return }
        zoomScale = 1
        pageView.frame = CGRect(origin: .zero, size: size)
        contentSize = size
        fitted = .zero
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard bounds.width > 0, bounds.height > 0 else { return }
        if fitted != bounds.size {
            // Fit the whole page when first shown and whenever the space changes while it is still fitted.
            let page = pageView.bounds.size
            let inset: CGFloat = 16
            let fit = min((bounds.width - inset * 2) / page.width, (bounds.height - adjustedContentInset.top
                - adjustedContentInset.bottom - inset * 2) / page.height)
            let wasFitted = fitted == .zero || abs(zoomScale - minimumZoomScale) < 0.001
            minimumZoomScale = max(fit, 0.05)
            maximumZoomScale = max(fit * 8, 4)
            if wasFitted { zoomScale = minimumZoomScale }
            fitted = bounds.size
        }
        center()
    }

    func viewForZooming(in scrollView: UIScrollView) -> UIView? { pageView }

    func scrollViewDidZoom(_ scrollView: UIScrollView) { center() }

    /// A page smaller than the view sits in the middle of it rather than in a corner.
    private func center() {
        let visible = bounds.inset(by: adjustedContentInset).size
        let horizontal = max(0, (visible.width - contentSize.width) / 2)
        let vertical = max(0, (visible.height - contentSize.height) / 2)
        contentInset = UIEdgeInsets(top: vertical, left: horizontal, bottom: vertical, right: horizontal)
    }
}

final class SketchPageView: UIView {
    weak var model: SketchModel?
    var canDraw = false
    var tool = "pen"
    var ink: SketchInk = .theme
    var level = 1

    private var active: SketchOp?
    private weak var drawing: UITouch?

    override init(frame: CGRect) {
        super.init(frame: frame)
        // Pixels stay pixels however far in the page is zoomed.
        layer.magnificationFilter = .nearest
        layer.minificationFilter = .trilinear
        layer.shadowColor = UIColor.black.cgColor
        layer.shadowOpacity = 0.18
        layer.shadowRadius = 10
        layer.shadowOffset = CGSize(width: 0, height: 2)
        isMultipleTouchEnabled = true
        isAccessibilityElement = true
        accessibilityLabel = "Sketch page"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// A touch as a point of a stroke: page pixels, and how hard it is pressed
    /// where the hardware says. A finger has no pressure, so it draws the chosen width.
    private func point(_ touch: UITouch) -> [Double] {
        let location = touch.preciseLocation(in: self)
        let pressure = touch.type == .pencil && touch.maximumPossibleForce > 0
            ? Double(min(max(touch.force / touch.maximumPossibleForce, 0), 1)) : 0
        return [Double(location.x), Double(location.y), pressure]
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard canDraw, let model else { return }
        if (event?.allTouches?.count ?? 1) > 1 {
            abandon()
            return
        }
        guard active == nil, let touch = touches.first else { return }
        drawing = touch
        active = SketchOp(id: UUID().uuidString.lowercased(), tool: tool, ink: ink.rawValue, level: level,
                          seed: Int.random(in: 0..<2_147_483_647), pointerType: touch.type == .pencil ? "pen" : "touch",
                          points: [point(touch)])
        model.sync.report(editing: true)
        if let active { model.preview(active) }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let touch = drawing, touches.contains(touch), active != nil, let model else { return }
        for sample in event?.coalescedTouches(for: touch) ?? [touch] {
            active?.points?.append(point(sample))
        }
        if let active { model.preview(active) }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let touch = drawing, touches.contains(touch), let stroke = active, let model else { return }
        active = nil
        drawing = nil
        model.commit(stroke)
        model.sync.report(editing: false)
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        abandon()
    }

    private func abandon() {
        guard active != nil else { return }
        active = nil
        drawing = nil
        model?.discardPreview()
        model?.sync.report(editing: false)
    }
}
