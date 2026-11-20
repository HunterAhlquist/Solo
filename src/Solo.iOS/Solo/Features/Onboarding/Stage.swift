import RealityKit
import SwiftUI

// The small 3D stage the first-run screens stand their models on: a few solid things in
// front of a camera, each of which can be taken hold of, pulled a little way, and let go.
// Solid to each other as well: a piece pulled into the next one pushes it aside.
//
// RealityKit and nothing drawn to look like it: the models are lit, they turn, and a piece
// pulled toward the edge is seen from its side. It is the framework's plain view with a
// camera of its own, no session and nothing asked of the device's.
//
// The models are made in Blender. Their sources are `Models/*.blend` beside the project,
// and what the app carries is each one exported (`Models/export_usdz.py`) into the folder
// next to this file. An object's name there is the name it is found by here.

// MARK: Motion

/// How a piece is moved: pulled a little way by a finger, and drawn back to its place by a
/// spring. The spring only says how hard to push. The pushing is done to a body in the
/// framework's physics, which is what stops one piece at another: a bar pulled across the
/// one in front leans on it and moves it, where a position set outright would pass through.
nonisolated enum PieceSpring {
    /// The farthest a piece is pulled, however far the finger goes: slightly, and no further.
    static let reach: Float = 0.2

    /// Where a drag of this length would have a piece. It follows the finger at first and
    /// gives less and less, the way something on elastic does, and comes a little toward
    /// the eye as it goes.
    static func pull(for drag: SIMD2<Float>) -> SIMD3<Float> {
        let length = simd_length(drag)
        guard length > 0.0001 else { return .zero }
        let eased = reach * (1 - exp(-length / reach))
        let flat = drag / length * eased
        return SIMD3(flat.x, flat.y, eased * 0.3)
    }

    /// How a piece at `position` should be speeding up to get to `target`. In the hand it
    /// follows without overshooting; let go, it is loose enough to pass home once or twice
    /// before it settles there.
    static func push(toward target: SIMD3<Float>, from position: SIMD3<Float>, velocity: SIMD3<Float>, held: Bool) -> SIMD3<Float> {
        let stiffness: Float = held ? 300 : 150
        let damping: Float = held ? 34 : 9
        return stiffness * (target - position) - damping * velocity
    }

    /// The same for which way it faces: the turn that would take it there, the short way round.
    static func twist(toward target: simd_quatf, from facing: simd_quatf, spin: SIMD3<Float>) -> SIMD3<Float> {
        var error = target * facing.inverse
        if error.real < 0 { error = simd_quatf(vector: -error.vector) }
        let sine = simd_length(error.imag)
        guard sine > 0.00001 else { return -14 * spin }
        let angle = 2 * atan2(sine, error.real)
        return 120 * angle * (error.imag / sine) - 14 * spin
    }
}

/// A piece of a model that can be pulled about: where it lives on the stage, and what is
/// asked of it now.
nonisolated struct SpringBack: Component {
    /// Its place and the way it faces, on the stage and not in whatever it hangs from: a
    /// pull is measured on the stage — right is right — and a model's own axes are whatever
    /// its file left them. One from Blender arrives lying on its back under a parent that
    /// stands it up.
    var home: SIMD3<Float>
    var facing: simd_quatf
    /// How far from home a finger wants it, and nil once it is let go.
    var held: SIMD3<Float>?
    /// A slow rise and fall while it is left alone, so the stage is never quite still.
    var drift: Float = 0
    /// How far it turns to one side and the other as it idles, in radians: a model on its
    /// own, showing its sides, where the stage as a whole stays put.
    var rock: Float = 0
    /// Where in that rise and fall this piece is, so that no two keep time.
    var phase: Float = 0
    var clock: Float = 0

    /// Where it should be now, and which way it should face being where it is.
    func aim(at position: SIMD3<Float>) -> (position: SIMD3<Float>, facing: simd_quatf) {
        let bob = SIMD3<Float>(0, drift * sin(clock * 0.9 + phase), 0)
        // Off to one side it turns to face where it has gone, as a card on a string would.
        // It leans by where it is and not by where it was asked to be, so a piece that was
        // only pushed there leans too.
        let offset = simd_clamp(position - home, SIMD3(repeating: -PieceSpring.reach), SIMD3(repeating: PieceSpring.reach))
        let lean = simd_quatf(angle: offset.x * 2.2, axis: [0, 1, 0]) * simd_quatf(angle: -offset.y * 2.2, axis: [1, 0, 0])
        let turn = simd_quatf(angle: rock * sin(clock * 0.6 + phase), axis: [0, 1, 0])
        return (home + (held ?? .zero) + bob, lean * turn * facing)
    }
}

/// The whole stage turning a little from side to side, which is what shows it has depth.
nonisolated struct Sway: Component {
    var yaw: Float
    var pitch: Float
    var clock: Float = 0
}

final class StageSystem: System {
    private static let pieces = EntityQuery(where: .has(SpringBack.self))
    private static let stages = EntityQuery(where: .has(Sway.self))

    init(scene: RealityKit.Scene) {}

    func update(context: SceneUpdateContext) {
        // A frame after a stall is not a second of motion.
        let step = Float(min(context.deltaTime, 1.0 / 30))
        for entity in context.entities(matching: Self.pieces, updatingSystemWhen: .rendering) {
            guard var piece = entity.components[SpringBack.self],
                  let stage = PhysicsSimulationComponent.nearestSimulationEntity(for: entity) else { continue }
            piece.clock += step
            entity.components.set(piece)

            let position = entity.position(relativeTo: stage)
            let aim = piece.aim(at: position)
            var motion = entity.components[PhysicsMotionComponent.self] ?? PhysicsMotionComponent()
            motion.linearVelocity += PieceSpring.push(toward: aim.position, from: position, velocity: motion.linearVelocity,
                                                 held: piece.held != nil) * step
            motion.angularVelocity += PieceSpring.twist(toward: aim.facing, from: entity.orientation(relativeTo: stage),
                                                   spin: motion.angularVelocity) * step
            entity.components.set(motion)
        }
        for entity in context.entities(matching: Self.stages, updatingSystemWhen: .rendering) {
            guard var sway = entity.components[Sway.self] else { continue }
            sway.clock += step
            entity.transform.rotation = simd_quatf(angle: sway.yaw * sin(sway.clock * 0.55), axis: [0, 1, 0])
                * simd_quatf(angle: sway.pitch * sin(sway.clock * 0.41 + 1), axis: [1, 0, 0])
            entity.components.set(sway)
        }
    }
}

// MARK: Building

/// The colours a model is made in, for the scheme in effect.
nonisolated struct StagePalette: Equatable, Sendable {
    var dark: Bool
    /// The app's own accent: there is no instance yet to have a theme.
    var accent: CSSColor { dark ? .appAccentDark : .appAccentLight }

    /// The accent let down toward white by `share`, for a paler piece of the same thing.
    func accent(paler share: Double) -> CSSColor {
        let accent = accent
        return CSSColor(red: accent.red + (1 - accent.red) * share, green: accent.green + (1 - accent.green) * share,
                        blue: accent.blue + (1 - accent.blue) * share)
    }

    func gray(_ white: Double) -> CSSColor { CSSColor(red: white, green: white, blue: white) }
}

extension CSSColor {
    var platform: PlatformColor {
        PlatformColor(red: CGFloat(red), green: CGFloat(green), blue: CGFloat(blue), alpha: CGFloat(alpha))
    }
}

enum Stage {
    /// A material that takes the light: a colour, how rough, how much like metal, and, for
    /// a lamp or a screen, how much it gives off by itself.
    static func material(_ color: CSSColor, roughness: Float = 0.45, metallic: Float = 0, glow: Float = 0,
                         opacity: Float = 1) -> PhysicallyBasedMaterial {
        var material = PhysicallyBasedMaterial()
        material.baseColor = .init(tint: color.platform)
        material.roughness = .init(floatLiteral: roughness)
        material.metallic = .init(floatLiteral: metallic)
        if glow > 0 {
            material.emissiveColor = .init(color: color.platform)
            material.emissiveIntensity = glow
        }
        if opacity < 1 {
            material.blending = .transparent(opacity: .init(floatLiteral: opacity))
        }
        return material
    }

    /// A model the app carries, by its file's name. Nil when it can't be read, and the
    /// stage is then empty: the screen it is on still says what it has to.
    static func load(_ name: String) async -> Entity? {
        try? await Entity(named: name, in: .main)
    }

    /// Gives the object called `name` this material, whatever its file gave it: the app's
    /// accent is not the same colour in the dark as in the light, and a file has one.
    static func dress(_ name: String, in model: Entity, _ material: PhysicallyBasedMaterial) {
        guard let entity = model.findEntity(named: name) else { return }
        entity.forEachModel { $0.materials = $0.materials.map { _ in material } }
    }

    /// Makes the object called `name` a piece: a touch finds it, it springs back to where
    /// its file put it, and it is solid, so nothing passes through it on the way. Its body
    /// is the box it fits in, which for a slab or a server is the thing itself.
    static func piece(_ name: String, in model: Entity, drift: Float = 0.006, rock: Float = 0, phase: Float = 0) {
        guard let entity = model.findEntity(named: name) else { return }
        let bounds = entity.visualBounds(relativeTo: entity)
        let shape = ShapeResource.generateBox(size: bounds.extents).offsetBy(translation: bounds.center)
        entity.components.set(InputTargetComponent())
        entity.components.set(CollisionComponent(shapes: [shape]))
        // Nothing falls: the stage has no gravity, and the only pull is toward home.
        var body = PhysicsBodyComponent(shapes: [shape], mass: 1, material: .generate(friction: 0.2, restitution: 0.15), mode: .dynamic)
        body.isAffectedByGravity = false
        body.linearDamping = 0
        body.angularDamping = 0
        entity.components.set(body)
        entity.components.set(PhysicsMotionComponent())
        entity.components.set(SpringBack(home: entity.position(relativeTo: model), facing: entity.orientation(relativeTo: model),
                                         drift: drift, rock: rock, phase: phase))
    }

    /// How much of the stage, top to bottom, a view of this shape shows when `extent` of it
    /// has to fit: all of its height, or more where the view is too narrow for its width.
    nonisolated static func visibleHeight(extent: CGSize, view: CGSize) -> CGFloat {
        guard view.width > 0, view.height > 0 else { return extent.height }
        return max(extent.height, extent.width * view.height / view.width)
    }

    /// How far back the camera stands for that, with its angle measured top to bottom.
    nonisolated static func cameraDistance(extent: CGSize, view: CGSize, fieldOfView degrees: Double) -> Float {
        Float(visibleHeight(extent: extent, view: view) / (2 * tan(degrees * .pi / 360)))
    }

    /// Where a point of the stage at the depth of its middle lands in the view. What is
    /// drawn flat with a stage — an arrow from one model to another — is placed by this.
    nonisolated static func point(_ place: CGPoint, extent: CGSize, view: CGSize) -> CGPoint {
        let scale = view.height / visibleHeight(extent: extent, view: view)
        return CGPoint(x: view.width / 2 + place.x * scale, y: view.height / 2 - place.y * scale)
    }
}

// MARK: The view

/// A stage of models: built once for the scheme, framed to fit, and its pieces draggable.
struct StageView: View {
    /// How much of the stage has to be in view, in its own units, about its middle.
    var extent: CGSize
    /// How far the whole thing turns from side to side as it idles, in degrees.
    var sway: Float = 7
    var build: (StagePalette) async -> Entity?

    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var camera = PerspectiveCamera()
    @State private var held: Entity?

    private static let fieldOfView = 28.0

    var body: some View {
        GeometryReader { geometry in
            let distance = Stage.cameraDistance(extent: extent, view: geometry.size, fieldOfView: Self.fieldOfView)
            RealityView { content in
                StageSystem.registerSystem()
                content.camera = .virtual

                // The physics is the stage's own: its pieces are carried round with it as it
                // turns, and nothing in it has weight.
                let root = Entity()
                var physics = PhysicsSimulationComponent()
                physics.gravity = .zero
                root.components.set(physics)
                root.components.set(Sway(yaw: reduceMotion ? 0 : sway * .pi / 180, pitch: reduceMotion ? 0 : sway * .pi / 360))
                if let model = await build(StagePalette(dark: scheme == .dark)) { root.addChild(model) }
                content.add(root)

                // A key light from over the left shoulder, and a softer one to lift what it leaves.
                let key = DirectionalLight()
                key.light.intensity = 1700
                key.look(at: .zero, from: [-1.4, 2.2, 3], relativeTo: nil)
                content.add(key)
                let fill = DirectionalLight()
                fill.light.intensity = 500
                fill.look(at: .zero, from: [2.4, -0.6, 2], relativeTo: nil)
                content.add(fill)

                camera.camera.fieldOfViewInDegrees = Float(Self.fieldOfView)
                camera.position = [0, 0, distance]
                content.add(camera)
            } update: { _ in
                camera.position = [0, 0, distance]
            }
            .gesture(
                DragGesture(minimumDistance: 0)
                    .targetedToAnyEntity()
                    .onChanged { value in
                        guard let piece = value.entity.piece else { return }
                        if held == nil { Haptics.shared.play(.pickUp) }
                        held = piece
                        // A point on screen is this much of the stage at the depth it stands at.
                        let scale = 2 * distance * Float(tan(Self.fieldOfView * .pi / 360)) / Float(max(geometry.size.height, 1))
                        let drag = SIMD2(Float(value.gestureValue.translation.width), Float(-value.gestureValue.translation.height)) * scale
                        piece.components[SpringBack.self]?.held = PieceSpring.pull(for: drag)
                    }
                    .onEnded { value in
                        (held ?? value.entity.piece)?.components[SpringBack.self]?.held = nil
                        held = nil
                    })
        }
        // A model is made in the scheme's colours, so another scheme is another model.
        .id(scheme)
        .accessibilityHidden(true)
    }
}

extension Entity {
    /// Every part of this that is drawn, itself included.
    func forEachModel(_ change: (inout ModelComponent) -> Void) {
        if var model = components[ModelComponent.self] {
            change(&model)
            components.set(model)
        }
        for child in children { child.forEachModel(change) }
    }
}

private extension Entity {
    /// The piece this is, or is part of: a touch lands on the smallest thing under it.
    var piece: Entity? {
        var entity: Entity? = self
        while let candidate = entity {
            if candidate.components.has(SpringBack.self) { return candidate }
            entity = candidate.parent
        }
        return nil
    }
}
