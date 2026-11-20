import Metal
import RealityKit
import simd

// Refraction for the glass pieces of a stage, and a glow for the pieces that give light.
//
// RealityKit has no material that bends what is behind it, and a shader of the app's own
// is not shown what has already been drawn. It has no bloom either. What it does offer is a
// pass over the finished frame, and both are made there.
//
// The glass. The pieces are in the scene with nothing to them but their shine, and the pass
// does the rest: for every point of the picture it follows the line of sight through each
// piece of glass it meets, nearest first. Each one lays its colour over the point, by as
// much as it is not seen through, and bends the line for everything further back — a
// little toward its own middle, so what is behind it is seen larger, and at its rim
// outward, where a slab of glass draws in what lies beyond its edge. What is behind all of
// them is then read from where the line ends up.
//
// The colour is laid by the pass and not left to the scene because the pass can't tell a
// piece's own colour from what shows through it: bending a frame in which the glass was
// already tinted bent the tint as well, and left a band of the background inside each rim.
//
// The pass knows where the glass is as geometry and not as a picture: each piece is a flat
// slab with rounded corners, so where a line of sight meets it, and how far that is from
// its edge, is worked out for each point.
//
// The glow. A piece that gives light is its own colour at full strength in the frame, and
// nothing else near it is. The pass takes the part of the frame the piece is in, keeps what
// is that colour, blurs it small, and lays the blur back over the frame.
//
// What it costs. The frame is copied across whole, which is nearly free, and the shader is
// then run only over the two patches that change: the box the glass fits in, and the box
// the glow reaches. A point outside every piece's own box is passed over without any of
// the arithmetic. It was first run over every point of the frame, most of which no glass
// is near.
//
// The shader is compiled from the text below when a stage first needs it. The project has
// no `.metal` file, which would need Xcode's separate Metal toolchain on every machine that
// builds it. Where it can't be compiled, the stage is told so (`LensBoard.works`) and keeps
// its glass as plain tinted glass.

/// One piece of glass, as the pass sees it: a rounded rectangle lying in the plane z = 0 of
/// a space of its own.
nonisolated struct LensShape: Sendable {
    /// From the camera's space into the lens's, and back.
    var lensFromView: simd_float4x4
    var viewFromLens: simd_float4x4
    /// Half its width, half its height, the radius of its corners.
    var shape: SIMD4<Float>
    /// Its colour at the top and at the bottom, as light and not as sRGB, and in `top`'s
    /// last place how much of the colour there is: the rest is seen through.
    var top: SIMD4<Float>
    var bottom: SIMD4<Float>
    /// Which way is up on it, in its own plane, and how far it runs that way end to end.
    var fade: SIMD4<Float>
    /// The box it fits in on screen, left, top, right, bottom, each from 0 to 1. Worked
    /// out by the pass, which is what knows the projection.
    var window: SIMD4<Float> = [0, 0, 1, 1]
}

/// A piece that gives light: the box it fits in, as the camera sees it, and its light.
nonisolated struct GlowSource: Sendable {
    /// The eight corners of its box, in the camera's space.
    var corners: [SIMD3<Float>]
    /// Its colour as light, and how strongly its glow is laid back over the frame.
    var colour: SIMD3<Float>
    var strength: Float
}

/// What the glass shader is handed. Laid out as its `Uniforms`.
nonisolated struct LensUniforms: Sendable {
    var lenses: (LensShape, LensShape, LensShape, LensShape)
    /// How many lenses, the projection's two scales, how far in from an edge the bending reaches.
    var frame: SIMD4<Float>
    /// How far a point at the very edge is moved, how much the edge is lit, and how much
    /// larger than life the glass shows what is behind its middle.
    var bend: SIMD4<Float>
    /// The two terms of the projection that turn a depth in the frame back into a distance.
    var depth: SIMD4<Float>
    /// Where in the frame the patch being worked on starts, in points of the picture.
    var origin: SIMD4<Float>
    /// The box the glow covers on screen, and how strongly it is laid on.
    var glowWindow: SIMD4<Float>
    var glow: SIMD4<Float>

    static let room = 4
}

/// What the glow's shaders are handed. Laid out as their `Bloom`.
nonisolated struct BloomUniforms: Sendable {
    /// The box of the frame the glow is taken from.
    var window: SIMD4<Float>
    /// The colour that glows, and how near to it a point has to be to count.
    var key: SIMD4<Float>
    /// One step of the blur, across or down.
    var step: SIMD4<Float>
}

/// Where the glass is now, and what glows. Written by the stage on the main actor each
/// frame, read by the pass wherever RealityKit runs it.
nonisolated final class LensBoard: @unchecked Sendable {
    private let lock = NSLock()
    private var shapes: [LensShape] = []
    private var light: GlowSource?
    private var compiled = false

    /// Whether the pass is there to make the glass. Until it is, and if it never is, the
    /// stage draws its glass itself.
    var works: Bool {
        get { lock.lock(); defer { lock.unlock() }; return compiled }
        set { lock.lock(); compiled = newValue; lock.unlock() }
    }

    func post(_ shapes: [LensShape], glow: GlowSource?) {
        lock.lock()
        self.shapes = shapes
        light = glow
        lock.unlock()
    }

    func read() -> (lenses: [LensShape], glow: GlowSource?) {
        lock.lock()
        defer { lock.unlock() }
        return (shapes, light)
    }
}

nonisolated enum Lens {
    /// The icon's own refraction (`Icons/Solo.icon`, each bar's group): how deep into the
    /// glass it reaches, and how strong it is. The icon states both as shares of itself;
    /// the mark on stage is one unit across, so they are the stage's units as they stand.
    static let depth: Float = 0.08515625
    static let strength: Float = 0.34546875

    /// What those come to on stage. The strength is how much nearer its own middle the
    /// glass shows everything behind it — a third nearer, so a third larger, the way a
    /// thick piece of glass does — and the depth is how wide the band at its rim is, across
    /// which that gives way to the rim drawing in what lies beyond it, from as far out as
    /// the band is wide. A line that passes behind a bar is out of step with itself where
    /// it goes under the edge, which is what says glass.
    static let rimBend = depth
    static let swell = strength

    /// The space a slab's lens lies in, from the slab's own: its thinnest side is the one
    /// looked through, and the other two are the rectangle's.
    static func lensFromLocal(center: SIMD3<Float>, extents: SIMD3<Float>) -> (matrix: simd_float4x4, half: SIMD2<Float>) {
        let through = extents.x <= extents.y && extents.x <= extents.z ? 0 : (extents.y <= extents.z ? 1 : 2)
        let across = (through + 1) % 3, up = (through + 2) % 3
        var rows = [SIMD4<Float>](repeating: .zero, count: 4)
        for (row, axis) in [across, up, through].enumerated() {
            rows[row][axis] = 1
            rows[row].w = -center[axis]
        }
        rows[3] = [0, 0, 0, 1]
        return (simd_float4x4(rows: rows), SIMD2(extents[across], extents[up]) / 2)
    }

    /// The box on screen that holds these points of the camera's space, left, top, right,
    /// bottom, each from 0 to 1 — or all of the screen, if any of them is not in front of
    /// the camera to be placed.
    static func window(of points: [SIMD3<Float>], projection: simd_float4x4) -> SIMD4<Float> {
        var low = SIMD2<Float>(repeating: .infinity), high = SIMD2<Float>(repeating: -.infinity)
        for point in points {
            guard point.z < -0.0001 else { return [0, 0, 1, 1] }
            let across = projection.columns.0.x * point.x / -point.z, up = projection.columns.1.y * point.y / -point.z
            let place = SIMD2(across * 0.5 + 0.5, 0.5 - up * 0.5)
            low = simd_min(low, place)
            high = simd_max(high, place)
        }
        return [low.x, low.y, high.x, high.y]
    }

    /// A box on screen as whole points of a picture this size, with a point to spare all
    /// round, and nothing of it off the picture. Nil when none of it is on.
    static func patch(_ window: SIMD4<Float>, width: Int, height: Int) -> (x: Int, y: Int, width: Int, height: Int)? {
        let left = max(0, Int((window.x * Float(width)).rounded(.down)) - 1)
        let top = max(0, Int((window.y * Float(height)).rounded(.down)) - 1)
        let right = min(width, Int((window.z * Float(width)).rounded(.up)) + 1)
        let bottom = min(height, Int((window.w * Float(height)).rounded(.up)) + 1)
        guard right > left, bottom > top else { return nil }
        return (left, top, right - left, bottom - top)
    }

    /// The square on screen a glow is made in: the piece's own box, made square as the
    /// picture measures it, and half as big again all round for the glow to fade out in.
    static func glowWindow(of piece: SIMD4<Float>, width: Int, height: Int) -> SIMD4<Float> {
        let size = SIMD2(Float(width), Float(height))
        let middle = SIMD2(piece.x + piece.z, piece.y + piece.w) / 2
        let reach = max((piece.z - piece.x) * size.x, (piece.w - piece.y) * size.y) * (0.5 + glowMargin)
        return [middle.x - reach / size.x, middle.y - reach / size.y, middle.x + reach / size.x, middle.y + reach / size.y]
    }

    /// How far beyond a piece its glow is given room, as a share of the piece's size. The
    /// glow itself reaches about a third of that: it is a light on, not a floodlight.
    static let glowMargin: Float = 0.5
    /// The side of the small picture a glow is blurred in.
    static let glowGrain = 96
}

/// The pass, as RealityKit asks for it.
nonisolated struct GlassLens: PostProcessEffect {
    let board: LensBoard
    private var pass: GlassPass?

    init(board: LensBoard) { self.board = board }

    mutating func prepare(for device: any MTLDevice) {
        pass = GlassPass(device: device)
        board.works = pass != nil
    }

    mutating func postProcess(context: borrowing PostProcessEffectContext<any MTLCommandBuffer>) {
        let posted = board.read()
        if let pass {
            pass.encode(into: context.commandBuffer, source: context.sourceColorTexture, depth: context.sourceDepthTexture,
                        target: context.targetColorTexture, projection: context.projection, lenses: posted.lenses, glow: posted.glow)
        } else if let copy = context.commandBuffer.makeBlitCommandEncoder() {
            // Nothing to make it with: the frame goes through as it came.
            copy.copy(from: context.sourceColorTexture, to: context.targetColorTexture)
            copy.endEncoding()
        }
    }
}

/// The work of the pass, apart from RealityKit, so that it can be run on a frame of a
/// test's own.
nonisolated struct GlassPass: @unchecked Sendable {
    private let bend: any MTLComputePipelineState
    private let bright: any MTLComputePipelineState
    private let blur: any MTLComputePipelineState
    /// The two small pictures a glow is blurred between.
    private let haze: (any MTLTexture, any MTLTexture)

    init?(device: any MTLDevice) {
        let small = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba16Float, width: Lens.glowGrain, height: Lens.glowGrain, mipmapped: false)
        small.usage = [.shaderRead, .shaderWrite]
        small.storageMode = .private
        guard let library = try? device.makeLibrary(source: Self.source, options: nil),
              let bend = library.makeFunction(name: "bend").flatMap({ try? device.makeComputePipelineState(function: $0) }),
              let bright = library.makeFunction(name: "bright").flatMap({ try? device.makeComputePipelineState(function: $0) }),
              let blur = library.makeFunction(name: "blur").flatMap({ try? device.makeComputePipelineState(function: $0) }),
              let first = device.makeTexture(descriptor: small), let second = device.makeTexture(descriptor: small)
        else { return nil }
        self.bend = bend
        self.bright = bright
        self.blur = blur
        haze = (first, second)
    }

    func encode(into buffer: any MTLCommandBuffer, source: any MTLTexture, depth: any MTLTexture, target: any MTLTexture,
                projection: simd_float4x4, lenses: [LensShape], glow: GlowSource?) {
        // All of the frame as it is. What follows changes two patches of it.
        if let copy = buffer.makeBlitCommandEncoder() {
            copy.copy(from: source, to: target)
            copy.endEncoding()
        }

        // Nearest first: a line of sight is bent by each glass it meets, in that order.
        var shapes = Array(lenses.sorted { $0.viewFromLens.columns.3.z > $1.viewFromLens.columns.3.z }.prefix(LensUniforms.room))
        for index in shapes.indices {
            let half = shapes[index].shape
            let corners = [SIMD2(-half.x, -half.y), SIMD2(half.x, -half.y), SIMD2(-half.x, half.y), SIMD2(half.x, half.y)].map {
                simd_make_float3(shapes[index].viewFromLens * SIMD4($0.x, $0.y, 0, 1))
            }
            shapes[index].window = Lens.window(of: corners, projection: projection)
        }
        let glass = shapes.isEmpty ? nil : shapes.dropFirst().reduce(shapes[0].window) { all, shape in
            [min(all.x, shape.window.x), min(all.y, shape.window.y), max(all.z, shape.window.z), max(all.w, shape.window.w)]
        }
        let halo = glow.map { Lens.glowWindow(of: Lens.window(of: $0.corners, projection: projection), width: target.width, height: target.height) }

        let patches = [glass, halo].compactMap { $0.flatMap { Lens.patch($0, width: target.width, height: target.height) } }
        guard !patches.isEmpty, let encoder = buffer.makeComputeCommandEncoder() else { return }

        if let glow, let halo {
            // The part of the frame the piece is in, kept where it is the piece's colour and
            // made small; then blurred across and down, between two small pictures.
            var bloom = BloomUniforms(window: halo, key: [glow.colour.x, glow.colour.y, glow.colour.z, 0.3], step: .zero)
            encoder.setComputePipelineState(bright)
            encoder.setTexture(source, index: 0)
            encoder.setTexture(haze.0, index: 1)
            encoder.setBytes(&bloom, length: MemoryLayout<BloomUniforms>.stride, index: 0)
            dispatch(encoder, bright, width: Lens.glowGrain, height: Lens.glowGrain)

            encoder.setComputePipelineState(blur)
            let step = 1 / Float(Lens.glowGrain)
            for round in 0..<2 {
                bloom.step = round.isMultiple(of: 2) ? [step, 0, 0, 0] : [0, step, 0, 0]
                encoder.setTexture(round.isMultiple(of: 2) ? haze.0 : haze.1, index: 0)
                encoder.setTexture(round.isMultiple(of: 2) ? haze.1 : haze.0, index: 1)
                encoder.setBytes(&bloom, length: MemoryLayout<BloomUniforms>.stride, index: 0)
                dispatch(encoder, blur, width: Lens.glowGrain, height: Lens.glowGrain)
            }
        }

        let empty = LensShape(lensFromView: matrix_identity_float4x4, viewFromLens: matrix_identity_float4x4, shape: .zero,
                              top: .zero, bottom: .zero, fade: .zero)
        let filled = shapes + Array(repeating: empty, count: LensUniforms.room - shapes.count)
        var uniforms = LensUniforms(
            lenses: (filled[0], filled[1], filled[2], filled[3]),
            frame: [Float(shapes.count), projection.columns.0.x, projection.columns.1.y, Lens.depth],
            bend: [Lens.rimBend, 0.1, Lens.swell, 0],
            depth: [projection.columns.2.z, projection.columns.3.z, 0, 0],
            origin: .zero,
            glowWindow: halo ?? .zero,
            glow: [halo == nil ? 0 : glow?.strength ?? 0, 0, 0, 0])

        encoder.setComputePipelineState(bend)
        encoder.setTexture(source, index: 0)
        encoder.setTexture(target, index: 1)
        encoder.setTexture(depth, index: 2)
        encoder.setTexture(haze.0, index: 3)
        // Each patch is worked out in full, glass and glow both, so where the two overlap
        // the second pass over those points writes what the first did.
        for patch in patches {
            uniforms.origin = [Float(patch.x), Float(patch.y), 0, 0]
            encoder.setBytes(&uniforms, length: MemoryLayout<LensUniforms>.stride, index: 0)
            dispatch(encoder, bend, width: patch.width, height: patch.height)
        }
        encoder.endEncoding()
    }

    private func dispatch(_ encoder: any MTLComputeCommandEncoder, _ pipeline: any MTLComputePipelineState, width: Int, height: Int) {
        let across = pipeline.threadExecutionWidth, down = max(1, pipeline.maxTotalThreadsPerThreadgroup / across)
        encoder.dispatchThreadgroups(MTLSize(width: (width + across - 1) / across, height: (height + down - 1) / down, depth: 1),
                                     threadsPerThreadgroup: MTLSize(width: across, height: down, depth: 1))
    }

    static let source = """
    #include <metal_stdlib>
    using namespace metal;

    struct Lens { float4x4 lensFromView; float4x4 viewFromLens; float4 shape; float4 top; float4 bottom; float4 fade; float4 window; };
    struct Uniforms { Lens lenses[4]; float4 frame; float4 bend; float4 depth; float4 origin; float4 glowWindow; float4 glow; };
    struct Bloom { float4 window; float4 key; float4 step; };

    // How far a point is from the edge of a rounded rectangle about the origin: less than
    // nothing inside it.
    static float edge(float2 p, float2 extent, float corner) {
        float2 q = abs(p) - extent + corner;
        return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - corner;
    }

    // Which way is straight out of it from there: away from the nearer side, or, in a
    // corner, away from the middle of the corner's curve.
    static float2 outward(float2 p, float2 extent, float corner) {
        float2 q = abs(p) - extent + corner;
        float2 side = float2(p.x < 0 ? -1.0 : 1.0, p.y < 0 ? -1.0 : 1.0);
        if (q.x > 0 && q.y > 0) { return side * normalize(q); }
        return q.x > q.y ? float2(side.x, 0) : float2(0, side.y);
    }

    kernel void bend(texture2d<float, access::sample> source [[texture(0)]],
                     texture2d<float, access::write> target [[texture(1)]],
                     depth2d<float, access::sample> depths [[texture(2)]],
                     texture2d<float, access::sample> haze [[texture(3)]],
                     constant Uniforms &u [[buffer(0)]],
                     uint2 grid [[thread_position_in_grid]]) {
        uint2 at = grid + uint2(u.origin.xy);
        if (at.x >= target.get_width() || at.y >= target.get_height()) { return; }
        constexpr sampler bilinear(filter::linear, address::clamp_to_edge);
        constexpr sampler nearest(filter::nearest, address::clamp_to_edge);
        float2 size = float2(target.get_width(), target.get_height());
        float2 uv = (float2(at) + 0.5) / size;

        // Where the line of sight is looking by now, what the glass it has come through has
        // laid over it, and how much of what is behind still shows.
        float2 from = uv;
        float4 laid = float4(0);
        float through = 1;
        // How far off the nearest solid thing is at this point, once it is asked for.
        float solid = -1;

        int count = int(u.frame.x);
        for (int index = 0; index < count; index++) {
            // Outside the box a piece fits in, it is not there to be met.
            float4 window = u.lenses[index].window;
            if (from.x < window.x || from.y < window.y || from.x > window.z || from.y > window.w) { continue; }

            // The line of sight, in the camera's space, which looks down -z.
            float2 ndc = float2(from.x * 2 - 1, 1 - from.y * 2);
            float3 sight = float3(ndc.x / u.frame.y, ndc.y / u.frame.z, -1);
            float3 eye = u.lenses[index].lensFromView[3].xyz;
            float3 way = (u.lenses[index].lensFromView * float4(sight, 0)).xyz;
            if (fabs(way.z) < 0.00001) { continue; }
            float reach = -eye.z / way.z;
            if (reach <= 0) { continue; }
            float2 p = eye.xy + reach * way.xy;
            float4 shape = u.lenses[index].shape;
            float inside = -edge(p, shape.xy, shape.z);
            if (inside <= 0) { continue; }

            // Glass behind something solid is not there to be looked through.
            if (solid < 0) { solid = u.depth.y / (u.depth.x + depths.sample(nearest, uv)); }
            if (solid < reach - 0.08) { continue; }

            // A point of the picture is this much of the glass across: its edge is eased
            // over one of them, as the edge of anything drawn is.
            float grain = 2 * reach / (u.frame.z * size.y);
            float cover = clamp(inside / grain, 0.0, 1.0);

            float near = 1 - clamp(inside / u.frame.w, 0.0, 1.0);
            float pull = near * near;

            float4 fade = u.lenses[index].fade;
            float along = clamp(dot(p, fade.xy) / fade.z + 0.5, 0.0, 1.0);
            float4 top = u.lenses[index].top;
            float3 tint = mix(u.lenses[index].bottom.rgb, top.rgb, along) + pull * pull * u.bend.y;
            float share = top.a * cover;
            laid += through * share * float4(tint, 1);
            through *= 1 - share;

            // Through its middle the glass shows things larger, from nearer its centre; at
            // its rim it shows what is just beyond the rim, drawn in.
            float2 bent = p - p * u.bend.z * (1 - pull) + outward(p, shape.xy, shape.z) * pull * u.bend.x;
            float4 seen = u.lenses[index].viewFromLens * float4(bent, 0, 1);
            float2 back = float2(u.frame.y * seen.x, u.frame.z * seen.y) / -seen.z;
            from = mix(from, float2(back.x * 0.5 + 0.5, 0.5 - back.y * 0.5), cover);
        }

        // A point no glass was met at is the frame's own, exactly.
        float4 colour = through < 1 ? laid + through * source.sample(bilinear, from) : source.read(at);

        if (u.glow.x > 0) {
            float2 within = (uv - u.glowWindow.xy) / (u.glowWindow.zw - u.glowWindow.xy);
            if (within.x > 0 && within.y > 0 && within.x < 1 && within.y < 1) {
                // Laid on as light is: it brightens what is there, and shows over nothing.
                float3 light = clamp(haze.sample(bilinear, within).rgb * u.glow.x, 0.0, 1.0);
                colour.rgb += light * (1 - colour.rgb);
                colour.a += (1 - colour.a) * max(light.r, max(light.g, light.b));
            }
        }
        target.write(colour, at);
    }

    // The part of the frame a glowing piece is in, made small, with only what is the
    // piece's own colour kept.
    kernel void bright(texture2d<float, access::sample> source [[texture(0)]],
                       texture2d<float, access::write> small [[texture(1)]],
                       constant Bloom &b [[buffer(0)]],
                       uint2 at [[thread_position_in_grid]]) {
        if (at.x >= small.get_width() || at.y >= small.get_height()) { return; }
        constexpr sampler bordered(filter::linear, address::clamp_to_zero);
        float2 size = float2(small.get_width(), small.get_height());
        float3 kept = float3(0);
        // Four looks at each point, since each stands for several of the frame's.
        for (int look = 0; look < 4; look++) {
            float2 nudge = float2(look % 2, look / 2) * 0.5 + 0.25;
            float2 uv = mix(b.window.xy, b.window.zw, (float2(at) + nudge) / size);
            float4 colour = source.sample(bordered, uv);
            if (colour.a < 0.01) { continue; }
            float3 own = colour.rgb / colour.a;
            float like = 1 - smoothstep(b.key.w * 0.5, b.key.w, distance(own, b.key.rgb));
            kept += own * like * colour.a;
        }
        small.write(float4(kept / 4, 1), at);
    }

    // One sweep of a blur, across or down.
    kernel void blur(texture2d<float, access::sample> from [[texture(0)]],
                     texture2d<float, access::write> to [[texture(1)]],
                     constant Bloom &b [[buffer(0)]],
                     uint2 at [[thread_position_in_grid]]) {
        if (at.x >= to.get_width() || at.y >= to.get_height()) { return; }
        constexpr sampler bordered(filter::linear, address::clamp_to_zero);
        float2 uv = (float2(at) + 0.5) / float2(to.get_width(), to.get_height());
        float3 sum = float3(0);
        float weights = 0;
        for (int tap = -6; tap <= 6; tap++) {
            float weight = exp(-float(tap * tap) / 18.0);
            sum += from.sample(bordered, uv + b.step.xy * float(tap)).rgb * weight;
            weights += weight;
        }
        to.write(float4(sum / weights, 1), at);
    }
    """
}
