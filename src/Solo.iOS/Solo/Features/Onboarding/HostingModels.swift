import RealityKit
import SwiftUI

/// The three places a Solo instance can live, as models, and what runs it on all of them,
/// as a drawing: a server at home, somebody's cloud, the laptop that was put away.
///
/// The stage is a little under two units square about its middle. Docker's mark is drawn flat at the
/// middle of it, with an arrow out to each model, so where the models stand in their file
/// is written down here as well, for the drawing to point at.
nonisolated enum HostingModels {
    static let extent = CGSize(width: 1.92, height: 1.84)

    static let server = CGPoint(x: -0.62, y: 0.2)
    static let cloud = CGPoint(x: 0.5, y: 0.62)
    static let laptop = CGPoint(x: 0.54, y: -0.52)

    /// How wide Docker's mark is drawn, and where: a little below the middle, between the three.
    static let docker = (center: CGPoint(x: -0.04, y: -0.06), width: CGFloat(0.46))

    /// An arrow from the mark out to a model: where it leaves, what it bends toward, and
    /// where its head lands.
    struct Arrow: Equatable, Sendable {
        var from: CGPoint
        var bend: CGPoint
        var to: CGPoint
    }

    static let arrows: [Arrow] = [
        Arrow(from: CGPoint(x: -0.2, y: -0.3), bend: CGPoint(x: -0.52, y: -0.5), to: CGPoint(x: -0.66, y: -0.16)),
        Arrow(from: CGPoint(x: -0.08, y: 0.2), bend: CGPoint(x: -0.12, y: 0.72), to: CGPoint(x: 0.2, y: 0.7)),
        Arrow(from: CGPoint(x: 0.24, y: -0.02), bend: CGPoint(x: 0.6, y: 0.1), to: CGPoint(x: 0.6, y: -0.2)),
    ]

    /// The three models (`Models/Hosting.blend`), each one a piece. They keep the colours
    /// their file gave them, but for what is the app's accent — the server's lamps, what is
    /// typed on the laptop — and the cloud, which is drawn against a different sky by day.
    @MainActor static func model(_ palette: StagePalette) async -> Entity? {
        guard let model = await Stage.load("Hosting") else { return nil }
        Stage.dress("ServerLamps", in: model, Stage.material(palette.accent, glow: 2.4))
        Stage.dress("LaptopType", in: model, Stage.material(palette.accent(paler: 0.2), glow: 2.2))
        Stage.dress("Cloud", in: model, Stage.material(palette.dark ? CSSColor(red: 0.93, green: 0.96, blue: 1)
                                                                    : CSSColor(red: 0.74, green: 0.84, blue: 0.97), roughness: 0.75))
        Stage.piece("Server", in: model, drift: 0.01, rock: 0.2, phase: 0.6)
        Stage.piece("Cloud", in: model, drift: 0.02, rock: 0.16, phase: 2.4)
        Stage.piece("Laptop", in: model, drift: 0.01, rock: 0.2, phase: 4.4)
        return model
    }
}

// MARK: The drawing

/// What is flat on the hosting stage: Docker's mark, and an arrow from it to each model.
struct HostingDiagram: View {
    @Environment(\.theme) private var theme

    var body: some View {
        Canvas { context, canvas in
            func place(_ point: CGPoint) -> CGPoint { Stage.point(point, extent: HostingModels.extent, view: canvas) }
            let unit = place(CGPoint(x: 1, y: 0)).x - place(.zero).x

            for arrow in HostingModels.arrows {
                var line = Path()
                line.move(to: place(arrow.from))
                line.addQuadCurve(to: place(arrow.to), control: place(arrow.bend))
                let stroke = StrokeStyle(lineWidth: max(2, unit * 0.022), lineCap: .round, lineJoin: .round)
                context.stroke(line, with: .color(theme.textSecondary), style: stroke)

                // The head points the way the line was going as it arrived.
                let end = place(arrow.to), toward = place(arrow.bend)
                let angle = atan2(end.y - toward.y, end.x - toward.x)
                let reach = unit * 0.075
                var head = Path()
                head.move(to: CGPoint(x: end.x - reach * cos(angle - 0.5), y: end.y - reach * sin(angle - 0.5)))
                head.addLine(to: end)
                head.addLine(to: CGPoint(x: end.x - reach * cos(angle + 0.5), y: end.y - reach * sin(angle + 0.5)))
                context.stroke(head, with: .color(theme.textSecondary), style: stroke)
            }

            let docker = HostingModels.docker
            let width = docker.width * unit
            let middle = place(docker.center)
            let frame = CGRect(x: middle.x - width / 2, y: middle.y - width * 0.42, width: width, height: width * 0.72)
            context.fill(DockerMark().path(in: frame), with: .color(DockerMark.blue))
            context.draw(Text("docker").font(.system(size: max(11, width * 0.2), weight: .bold, design: .rounded))
                            .foregroundStyle(DockerMark.blue),
                         at: CGPoint(x: middle.x, y: frame.maxY + width * 0.13))
        }
        .accessibilityHidden(true)
    }
}

/// Docker's whale and the containers on its back, drawn plainly: it says what a Solo
/// instance runs in, the way the word would.
nonisolated struct DockerMark: Shape {
    static let blue = Color(.sRGB, red: 0.114, green: 0.388, blue: 0.929)

    func path(in rect: CGRect) -> Path {
        // Drawn on a grid 24 across and 17.3 down.
        let scale = rect.width / 24
        func point(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * scale, y: rect.minY + y * scale) }
        var path = Path()

        // The containers: five along the deck, three on those, one on top.
        let side: CGFloat = 2.25, pitch: CGFloat = 2.75
        for (row, columns) in [(0, [3]), (1, [1, 2, 3]), (2, [0, 1, 2, 3, 4])] {
            for column in columns {
                let origin = point(2.6 + CGFloat(column) * pitch, 1.4 + CGFloat(row) * pitch)
                path.addRoundedRect(in: CGRect(x: origin.x, y: origin.y, width: side * scale, height: side * scale),
                                    cornerSize: CGSize(width: 0.3 * scale, height: 0.3 * scale))
            }
        }

        // The whale: a flat back, a tail lifted at the right, a full belly.
        path.move(to: point(0.5, 10.4))
        path.addLine(to: point(18.6, 10.4))
        path.addCurve(to: point(20.5, 8.8), control1: point(19.5, 10.4), control2: point(20.1, 9.6))
        path.addCurve(to: point(23.6, 8.6), control1: point(21.5, 9.4), control2: point(22.8, 9.3))
        path.addCurve(to: point(20.4, 11.3), control1: point(23.2, 10.2), control2: point(22, 11.1))
        path.addCurve(to: point(11, 16.9), control1: point(19, 14.7), control2: point(16, 16.9))
        path.addCurve(to: point(0.5, 10.4), control1: point(5, 16.9), control2: point(1, 14.3))
        path.closeSubpath()
        return path
    }
}
