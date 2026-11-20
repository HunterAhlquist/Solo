import RealityKit
import SwiftUI

// The little face that goes with the mark, and through the first run with whoever is
// having it: it is in the mark on the first screen, says what an instance is on the
// second, and comes to rest beside "Restart Onboarding" on the third.
//
// It is drawn in Aseprite (`Models/SoloGuy.aseprite`, sixteen points square, a few frames
// tagged by what the face is doing), and that drawing is all there is of it. The app reads
// the file itself, and makes the face it shows in 3D by raising each frame's outline off
// the page: one solid shape per part of the face, its edges the drawing's own, and not a
// block for every point. A change to the drawing is a change to the face, with nothing to
// export in between.

// MARK: The drawing

/// As much of an Aseprite file as a small drawing with a few tagged frames needs: full
/// colour, its layers flattened, each frame as which of its points are filled.
nonisolated struct AsepriteSheet: Sendable, Equatable {
    struct Frame: Sendable, Equatable {
        /// Row by row from the top, true where the frame has anything.
        var filled: [Bool]
        /// How long it is shown, in thousandths of a second.
        var duration: Int
    }

    var width: Int
    var height: Int
    var frames: [Frame]
    /// The frames each tag covers, by the tag's name.
    var tags: [String: ClosedRange<Int>]

    init(width: Int, height: Int, frames: [Frame], tags: [String: ClosedRange<Int>]) {
        (self.width, self.height, self.frames, self.tags) = (width, height, frames, tags)
    }

    /// Nil for anything that isn't a file this can read through to its end.
    init?(data: Data) {
        let bytes = [UInt8](data)
        func u16(_ at: Int) -> Int? { at + 2 <= bytes.count ? Int(bytes[at]) | Int(bytes[at + 1]) << 8 : nil }
        func i16(_ at: Int) -> Int? { u16(at).map { Int(Int16(truncatingIfNeeded: $0)) } }
        func u32(_ at: Int) -> Int? {
            guard let low = u16(at), let high = u16(at + 2) else { return nil }
            return low | high << 16
        }
        // 0xA5E0 is the file's own mark, and 32 is four bytes of colour to a point.
        guard u16(4) == 0xA5E0, let count = u16(6), let width = u16(8), let height = u16(10), u16(12) == 32,
              width > 0, height > 0, width <= 256, height <= 256 else { return nil }

        struct Cel {
            var layer: Int, x: Int, y: Int, width: Int, height: Int
            var pixels: [UInt8]
        }
        var visible: [Bool] = []
        var cels: [[Int: Cel]] = []
        var links: [(frame: Int, layer: Int, to: Int)] = []
        var durations: [Int] = []
        var tags: [String: ClosedRange<Int>] = [:]

        var position = 128
        for frame in 0..<count {
            guard let size = u32(position), u16(position + 4) == 0xF1FA, let older = u16(position + 6),
                  let duration = u16(position + 8), let newer = u32(position + 12), size >= 16 else { return nil }
            durations.append(duration)
            cels.append([:])
            var at = position + 16
            for _ in 0..<(newer == 0 ? older : newer) {
                guard let length = u32(at), let kind = u16(at + 4), length >= 6, at + length <= bytes.count else { return nil }
                let body = at + 6
                switch kind {
                case 0x2004:   // a layer: only whether it is shown matters here
                    guard let flags = u16(body) else { return nil }
                    visible.append(flags & 1 == 1)
                case 0x2005:   // a cel: a layer's picture in this frame
                    guard let layer = u16(body), let x = i16(body + 2), let y = i16(body + 4), let form = u16(body + 7) else { return nil }
                    if form == 1 {
                        guard let to = u16(body + 16) else { return nil }
                        links.append((frame, layer, to))
                    } else if form == 0 || form == 2 {
                        guard let celWidth = u16(body + 16), let celHeight = u16(body + 18) else { return nil }
                        var pixels = Array(bytes[(body + 20)..<(at + length)])
                        if form == 2 {
                            // Packed with zlib, whose two bytes of heading the system's unpacking doesn't want.
                            guard pixels.count > 2,
                                  let unpacked = try? (Data(pixels.dropFirst(2)) as NSData).decompressed(using: .zlib) else { return nil }
                            pixels = [UInt8](unpacked as Data)
                        }
                        guard pixels.count >= celWidth * celHeight * 4 else { return nil }
                        cels[frame][layer] = Cel(layer: layer, x: x, y: y, width: celWidth, height: celHeight, pixels: pixels)
                    }
                case 0x2018:   // the tags
                    guard let number = u16(body) else { return nil }
                    var tag = body + 10
                    for _ in 0..<number {
                        guard let from = u16(tag), let to = u16(tag + 2), let letters = u16(tag + 17),
                              tag + 19 + letters <= bytes.count, from <= to else { return nil }
                        tags[String(decoding: bytes[(tag + 19)..<(tag + 19 + letters)], as: UTF8.self)] = from...to
                        tag += 19 + letters
                    }
                default:
                    break
                }
                at += length
            }
            position += size
        }
        // A cel that is another frame's over again.
        for link in links where cels.indices.contains(link.to) {
            cels[link.frame][link.layer] = cels[link.to][link.layer]
        }

        self.width = width
        self.height = height
        self.tags = tags.filter { $0.value.upperBound < count }
        frames = zip(cels, durations).map { cels, duration in
            var filled = [Bool](repeating: false, count: width * height)
            for cel in cels.values where visible.indices.contains(cel.layer) && visible[cel.layer] {
                for row in 0..<cel.height {
                    for column in 0..<cel.width where cel.pixels[(row * cel.width + column) * 4 + 3] > 0 {
                        let x = cel.x + column, y = cel.y + row
                        if x >= 0, y >= 0, x < width, y < height { filled[y * width + x] = true }
                    }
                }
            }
            return Frame(filled: filled, duration: duration)
        }
    }

    /// The smallest box that holds everything any frame fills: left, top, and how many
    /// points across and down. The face is measured by this, so it doesn't shift as it
    /// changes expression.
    var bounds: (x: Int, y: Int, width: Int, height: Int) {
        var low = (width, height), high = (-1, -1)
        for frame in frames {
            for index in frame.filled.indices where frame.filled[index] {
                let x = index % width, y = index / width
                low = (min(low.0, x), min(low.1, y))
                high = (max(high.0, x), max(high.1, y))
            }
        }
        guard high.0 >= low.0 else { return (0, 0, width, height) }
        return (low.0, low.1, high.0 - low.0 + 1, high.1 - low.1 + 1)
    }
}

/// The edge of a drawing made of square points, as closed lines round each of its parts:
/// what a shape cut out of the drawing would be cut along.
nonisolated enum PixelOutline {
    /// Each closed line as its corners, in the drawing's own coordinates with y downward.
    /// The outside of a part goes clockwise and a hole in one the other way, so filled
    /// together they are the drawing. Two points that meet only at a corner are two parts.
    static func loops(filled: [Bool], width: Int, height: Int) -> [[SIMD2<Int>]] {
        func has(_ x: Int, _ y: Int) -> Bool { x >= 0 && y >= 0 && x < width && y < height && filled[y * width + x] }
        // Every side of a filled point that has nothing on the other side of it, going
        // round the point clockwise.
        var leaving: [SIMD2<Int>: [SIMD2<Int>]] = [:]
        for y in 0..<height {
            for x in 0..<width where has(x, y) {
                if !has(x, y - 1) { leaving[[x, y], default: []].append([x + 1, y]) }
                if !has(x + 1, y) { leaving[[x + 1, y], default: []].append([x + 1, y + 1]) }
                if !has(x, y + 1) { leaving[[x + 1, y + 1], default: []].append([x, y + 1]) }
                if !has(x - 1, y) { leaving[[x, y + 1], default: []].append([x, y]) }
            }
        }

        var loops: [[SIMD2<Int>]] = []
        while let start = leaving.keys.min(by: { ($0.y, $0.x) < ($1.y, $1.x) }) {
            var loop: [SIMD2<Int>] = []
            var here = start, heading = SIMD2<Int>(0, 0)
            repeat {
                guard var ways = leaving[here], !ways.isEmpty else { break }
                // Where two parts meet at a corner there are two ways on. The one that
                // turns right stays with the part being gone round.
                let right = SIMD2<Int>(-heading.y, heading.x)
                let pick = ways.firstIndex { $0 &- here == right } ?? 0
                let next = ways.remove(at: pick)
                leaving[here] = ways.isEmpty ? nil : ways
                // A corner is where the line turns; a point it only passes through is left out.
                if next &- here != heading { loop.append(here) }
                heading = next &- here
                here = next
            } while here != start
            // It starts at the topmost, leftmost corner there is, which is always a turn.
            if loop.count >= 4 { loops.append(loop) }
        }
        return loops
    }
}

// MARK: The face

/// What the face is doing.
nonisolated enum GuyPose: Equatable, Sendable {
    case smile
    case idle(blinking: Bool)
    case talk(open: Bool)
    /// Somebody has hold of it, or has only just let go.
    case tap
}

enum Guy {
    /// The drawing, read once.
    static let sheet: AsepriteSheet? = Bundle.main.url(forResource: "SoloGuy", withExtension: "aseprite")
        .flatMap { try? Data(contentsOf: $0) }
        .flatMap(AsepriteSheet.init(data:))

    /// Which frame of the drawing a pose is: the tag's first frame, or its second for the
    /// half of it that is a blink or an open mouth.
    nonisolated static func frame(for pose: GuyPose, in sheet: AsepriteSheet) -> Int {
        func tagged(_ name: String, second: Bool) -> Int {
            guard let frames = sheet.tags[name] else { return 0 }
            return second && frames.count > 1 ? frames.lowerBound + 1 : frames.lowerBound
        }
        switch pose {
        case .smile: return tagged("Smile", second: false)
        case .idle(let blinking): return tagged("Idle", second: blinking)
        case .talk(let open): return tagged("Talk", second: open)
        case .tap: return tagged("Tap", second: false)
        }
    }

    /// How wide the face is on a stage, in the stage's units, and so how big one point of
    /// the drawing is: the size the face was when it was blocks in the logo's own model.
    nonisolated static let width: Float = 0.4
    /// How thick it is, in points of the drawing.
    nonisolated static let thickness: Float = 1.6

    /// The face's size on a stage.
    static var size: CGSize {
        guard let bounds = sheet?.bounds else { return CGSize(width: 0.4, height: 0.3) }
        return CGSize(width: CGFloat(width), height: CGFloat(width) * CGFloat(bounds.height) / CGFloat(bounds.width))
    }

    /// A frame's outline as a shape about the middle of the face, in the stage's units, y upward.
    nonisolated static func outline(frame: Int, of sheet: AsepriteSheet) -> Path {
        let bounds = sheet.bounds
        let point = CGFloat(width) / CGFloat(bounds.width)
        let middle = CGPoint(x: CGFloat(bounds.x) + CGFloat(bounds.width) / 2, y: CGFloat(bounds.y) + CGFloat(bounds.height) / 2)
        var path = Path()
        guard sheet.frames.indices.contains(frame) else { return path }
        for loop in PixelOutline.loops(filled: sheet.frames[frame].filled, width: sheet.width, height: sheet.height) {
            let corners = loop.map { CGPoint(x: (CGFloat($0.x) - middle.x) * point, y: (middle.y - CGFloat($0.y)) * point) }
            path.addLines(corners)
            path.closeSubpath()
        }
        return path
    }

    private static var meshes: [Int: MeshResource] = [:]

    /// A frame raised off the page: each part of it one solid, its edge the drawing's.
    static func mesh(frame: Int) -> MeshResource? {
        if let kept = meshes[frame] { return kept }
        guard let sheet else { return nil }
        let point = width / Float(sheet.bounds.width)
        var options = MeshResource.ShapeExtrusionOptions()
        options.extrusionMethod = .linear(depth: point * thickness)
        options.chamferRadius = point * 0.16
        options.chamferResolution = .uniformSegmentsPerSpan(segmentCount: 3)
        guard let mesh = try? MeshResource(extruding: outline(frame: frame, of: sheet), extrusionOptions: options) else { return nil }
        meshes[frame] = mesh
        return mesh
    }

    // MARK: On a stage

    /// How the face is to look just now.
    struct Look: Equatable {
        var pose = GuyPose.smile
        /// From 0, standing off the page and turned as `tilt` has it, to 1, flat on it and
        /// square to the eye: what a picture of it is.
        var flat: Float = 0
        var tilt = simd_quatf(ix: 0, iy: 0, iz: 0, r: 1)
        /// Not to be seen, though it is still on the stage.
        var hidden = false
    }

    /// The face as a group on the stage, the size of the place it is given.
    static func stage(_ look: Look) -> StageScene {
        StageScene(extent: size, sway: 0, load: {
            let root = Entity()
            let guy = Entity()
            guy.name = "Guy"
            let body = ModelEntity()
            body.name = "GuyBody"
            if let sheet, let mesh = mesh(frame: frame(for: .smile, in: sheet)) {
                body.model = ModelComponent(mesh: mesh, materials: [SimpleMaterial()])
            }
            guy.addChild(body)
            root.addChild(guy)
            // One body through every expression: the box all of them fit in.
            let half = SIMD3(Float(size.width) / 2, Float(size.height) / 2, width / 12)
            Stage.piece(guy, in: root, box: BoundingBox(min: -half, max: half), drift: 0.008, rock: 0.1, phase: 1.3)
            return root
        }, paint: { model, palette in
            // Lit from inside: its own colour given off, with enough of the room's light
            // still on it to show it has sides.
            Stage.dress("GuyBody", in: model, Stage.material(colour(dark: palette.dark), roughness: 0.3, glow: 0.85))
        }, glow: { palette in
            StageGlow(name: "Guy", colour: colour(dark: palette.dark),
                      strength: look.hidden ? 0 : (palette.dark ? 0.5 : 0.55) * (1 - look.flat))
        }, refresh: { model in
            guard let sheet, let guy = model.findEntity(named: "Guy"), let body = model.findEntity(named: "GuyBody") as? ModelEntity,
                  let mesh = mesh(frame: frame(for: look.pose, in: sheet)) else { return }
            // Every frame, so only when it is a different expression.
            if body.model?.mesh !== mesh { body.model?.mesh = mesh }
            // Flat, it has no depth to speak of, and its face is exactly on the plane the
            // stage is framed to, which is where a picture of it would be.
            let depth = max(0.02, 1 - look.flat)
            body.scale = [1, 1, depth]
            body.position.z = -mesh.bounds.max.z * depth
            guy.components.set(OpacityComponent(opacity: look.hidden ? 0 : 1))
            guy.components[SpringBack.self]?.facing = look.tilt
            guy.components[SpringBack.self]?.rock = 0.1 * (1 - look.flat)
            guy.components[SpringBack.self]?.drift = 0.008 * (1 - look.flat)
        })
    }

    /// The face's light: white in the dark, as it is on the icon, and by day the accent,
    /// since something dark gives off nothing and white would be lost on a light ground.
    nonisolated static func colour(dark: Bool) -> CSSColor { dark ? CSSColor(red: 1, green: 1, blue: 1) : .appAccentLight }
}

/// The face as a flat picture: the drawing itself, a square for each of its points.
struct GuyMark: View {
    @Environment(\.colorScheme) private var scheme
    var pose = GuyPose.smile

    var body: some View {
        Canvas { context, size in
            guard let sheet = Guy.sheet else { return }
            let bounds = sheet.bounds, frame = sheet.frames[Guy.frame(for: pose, in: sheet)]
            let point = min(size.width / CGFloat(bounds.width), size.height / CGFloat(bounds.height))
            let origin = CGPoint(x: (size.width - point * CGFloat(bounds.width)) / 2, y: (size.height - point * CGFloat(bounds.height)) / 2)
            var squares = Path()
            for y in 0..<bounds.height {
                for x in 0..<bounds.width where frame.filled[(bounds.y + y) * sheet.width + bounds.x + x] {
                    squares.addRect(CGRect(x: origin.x + CGFloat(x) * point, y: origin.y + CGFloat(y) * point, width: point, height: point))
                }
            }
            context.fill(squares, with: .color(Guy.colour(dark: scheme == .dark).color))
        }
        .aspectRatio(Guy.size.width / Guy.size.height, contentMode: .fit)
        .accessibilityHidden(true)
    }
}
