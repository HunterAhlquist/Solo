import Metal
import RealityKit
import simd

// Refraction for the glass pieces of a stage.
//
// RealityKit has no material that bends what is behind it, and a shader of the app's own
// is not shown what has already been drawn. What it does offer is a pass over the finished
// frame, and that is where the glass is made. The pieces are in the scene with nothing to
// them but their shine, and the pass does the rest: for every point of the picture it
// follows the line of sight through each piece of glass it meets, nearest first. Each one
// lays its colour over the point, by as much as it is not seen through, and bends the line
// for everything further back — a little toward its own middle, so what is behind it is
// seen larger, and at its rim outward, where a slab of glass draws in what lies beyond its
// edge. What is behind all of them is then read from where the line ends up.
//
// The colour is laid by the pass and not left to the scene because the pass can't tell a
// piece's own colour from what shows through it: bending a frame in which the glass was
// already tinted bent the tint as well, and left a band of the background inside each rim.
//
// The pass knows where the glass is as geometry and not as a picture: each piece is a flat
// slab with rounded corners, so where a line of sight meets it, and how far that is from
// its edge, is worked out for each point. The shader is compiled from the text below when
// a stage first needs it. The project has no `.metal` file, which would need Xcode's
// separate Metal toolchain on every machine that builds it. Where it can't be compiled,
// the stage is told so (`LensBoard.works`) and keeps its glass as plain tinted glass.

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
}

/// What the pass is handed each frame. Laid out as the shader's `Uniforms`.
nonisolated struct LensUniforms: Sendable {
    var lenses: (LensShape, LensShape, LensShape, LensShape)
    /// How many lenses, the projection's two scales, how far in from an edge the bending reaches.
    var frame: SIMD4<Float>
    /// How far a point at the very edge is moved, how much the edge is lit, and how much
    /// larger than life the glass shows what is behind its middle.
    var bend: SIMD4<Float>
    /// The two terms of the projection that turn a depth in the frame back into a distance.
    var depth: SIMD4<Float>

    static let room = 4
}

/// Where the glass is now. Written by the stage on the main actor each frame, read by the
/// pass wherever RealityKit runs it.
nonisolated final class LensBoard: @unchecked Sendable {
    private let lock = NSLock()
    private var shapes: [LensShape] = []
    private var compiled = false

    /// Whether the pass is there to make the glass. Until it is, and if it never is, the
    /// stage draws its glass itself.
    var works: Bool {
        get { lock.lock(); defer { lock.unlock() }; return compiled }
        set { lock.lock(); compiled = newValue; lock.unlock() }
    }

    func post(_ shapes: [LensShape]) {
        lock.lock()
        self.shapes = shapes
        lock.unlock()
    }

    func read() -> [LensShape] {
        lock.lock()
        defer { lock.unlock() }
        return shapes
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
}

/// The pass itself.
nonisolated struct GlassLens: PostProcessEffect {
    let board: LensBoard
    private var pipeline: (any MTLComputePipelineState)?

    init(board: LensBoard) { self.board = board }

    mutating func prepare(for device: any MTLDevice) {
        guard let library = try? device.makeLibrary(source: Self.source, options: nil),
              let kernel = library.makeFunction(name: "bend") else { return }
        pipeline = try? device.makeComputePipelineState(function: kernel)
        board.works = pipeline != nil
    }

    mutating func postProcess(context: borrowing PostProcessEffectContext<any MTLCommandBuffer>) {
        let source = context.sourceColorTexture, target = context.targetColorTexture
        // Nearest first: a line of sight is bent by the first glass it meets.
        let shapes = board.read()
            .sorted { $0.viewFromLens.columns.3.z > $1.viewFromLens.columns.3.z }
            .prefix(LensUniforms.room)
        guard let pipeline, !shapes.isEmpty, let encoder = context.commandBuffer.makeComputeCommandEncoder() else {
            // Nothing to bend, or nothing to bend it with: the frame goes through as it came.
            if let copy = context.commandBuffer.makeBlitCommandEncoder() {
                copy.copy(from: source, to: target)
                copy.endEncoding()
            }
            return
        }
        let empty = LensShape(lensFromView: matrix_identity_float4x4, viewFromLens: matrix_identity_float4x4, shape: .zero,
                              top: .zero, bottom: .zero, fade: .zero)
        let filled = Array(shapes) + Array(repeating: empty, count: LensUniforms.room - shapes.count)
        let projection = context.projection
        var uniforms = LensUniforms(
            lenses: (filled[0], filled[1], filled[2], filled[3]),
            frame: [Float(shapes.count), projection.columns.0.x, projection.columns.1.y, Lens.depth],
            bend: [Lens.rimBend, 0.1, Lens.swell, 0],
            depth: [projection.columns.2.z, projection.columns.3.z, 0, 0])

        encoder.setComputePipelineState(pipeline)
        encoder.setTexture(source, index: 0)
        encoder.setTexture(target, index: 1)
        encoder.setTexture(context.sourceDepthTexture, index: 2)
        encoder.setBytes(&uniforms, length: MemoryLayout<LensUniforms>.stride, index: 0)
        let width = pipeline.threadExecutionWidth, height = max(1, pipeline.maxTotalThreadsPerThreadgroup / width)
        encoder.dispatchThreadgroups(
            MTLSize(width: (target.width + width - 1) / width, height: (target.height + height - 1) / height, depth: 1),
            threadsPerThreadgroup: MTLSize(width: width, height: height, depth: 1))
        encoder.endEncoding()
    }

    private static let source = """
    #include <metal_stdlib>
    using namespace metal;

    struct Lens { float4x4 lensFromView; float4x4 viewFromLens; float4 shape; float4 top; float4 bottom; float4 fade; };
    struct Uniforms { Lens lenses[4]; float4 frame; float4 bend; float4 depth; };

    // How far a point is from the edge of a rounded rectangle about the origin: less than
    // nothing inside it.
    static float edge(float2 p, float2 extent, float corner) {
        float2 q = abs(p) - extent + corner;
        return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - corner;
    }

    kernel void bend(texture2d<float, access::sample> source [[texture(0)]],
                     texture2d<float, access::write> target [[texture(1)]],
                     depth2d<float, access::sample> depths [[texture(2)]],
                     constant Uniforms &u [[buffer(0)]],
                     uint2 at [[thread_position_in_grid]]) {
        if (at.x >= target.get_width() || at.y >= target.get_height()) { return; }
        constexpr sampler bilinear(filter::linear, address::clamp_to_edge);
        constexpr sampler nearest(filter::nearest, address::clamp_to_edge);
        float2 size = float2(target.get_width(), target.get_height());
        float2 uv = (float2(at) + 0.5) / size;

        // How far off the nearest solid thing is at this point. Glass behind something
        // solid is not there to be looked through.
        float solid = u.depth.y / (u.depth.x + depths.sample(nearest, uv));

        // Where the line of sight is looking by now, what the glass it has come through has
        // laid over it, and how much of what is behind still shows.
        float2 from = uv;
        float4 laid = float4(0);
        float through = 1;

        int count = int(u.frame.x);
        for (int index = 0; index < count; index++) {
            Lens lens = u.lenses[index];
            // The line of sight, in the camera's space, which looks down -z.
            float2 ndc = float2(from.x * 2 - 1, 1 - from.y * 2);
            float3 sight = float3(ndc.x / u.frame.y, ndc.y / u.frame.z, -1);
            float3 eye = (lens.lensFromView * float4(0, 0, 0, 1)).xyz;
            float3 way = (lens.lensFromView * float4(sight, 0)).xyz;
            if (fabs(way.z) < 0.00001) { continue; }
            float reach = -eye.z / way.z;
            if (reach <= 0 || solid < reach - 0.08) { continue; }
            float2 p = eye.xy + reach * way.xy;
            float inside = -edge(p, lens.shape.xy, lens.shape.z);
            if (inside <= 0) { continue; }

            // A point of the picture is this much of the glass across: its edge is eased
            // over one of them, as the edge of anything drawn is.
            float grain = 2 * reach / (u.frame.z * size.y);
            float cover = clamp(inside / grain, 0.0, 1.0);

            float near = 1 - clamp(inside / u.frame.w, 0.0, 1.0);
            float pull = near * near;

            float along = clamp(dot(p, lens.fade.xy) / lens.fade.z + 0.5, 0.0, 1.0);
            float3 tint = mix(lens.bottom.rgb, lens.top.rgb, along) + pull * pull * u.bend.y;
            float share = lens.top.a * cover;
            laid += through * share * float4(tint, 1);
            through *= 1 - share;

            float step = 0.002;
            float2 outward = float2(
                edge(p + float2(step, 0), lens.shape.xy, lens.shape.z) - edge(p - float2(step, 0), lens.shape.xy, lens.shape.z),
                edge(p + float2(0, step), lens.shape.xy, lens.shape.z) - edge(p - float2(0, step), lens.shape.xy, lens.shape.z));
            float steep = length(outward);
            if (steep > 0.000001) {
                // Through its middle the glass shows things larger, from nearer its centre;
                // at its rim it shows what is just beyond the rim, drawn in.
                float2 bent = p - p * u.bend.z * (1 - pull) + outward / steep * pull * u.bend.x;
                float4 seen = lens.viewFromLens * float4(bent, 0, 1);
                float2 back = float2(u.frame.y * seen.x, u.frame.z * seen.y) / -seen.z;
                from = mix(from, float2(back.x * 0.5 + 0.5, 0.5 - back.y * 0.5), cover);
            }
        }

        target.write(laid + through * source.sample(bilinear, from), at);
    }
    """
}
