import RealityKit
import SwiftUI

/// The three places a Solo instance can live, as models, and what runs it on all of them,
/// as a drawing: a server at home, somebody's cloud, the laptop that was put away.
///
/// The stage is a little under two units square about its middle. Docker's symbol is drawn
/// flat at the middle of it, its name under it, with an arrow out to each model, so where the models stand in
/// their file is written down here as well, for the drawing to point at.
nonisolated enum HostingModels {
    static let extent = CGSize(width: 1.92, height: 1.84)

    /// How far behind the face the models stand: it is below them and off to one side on
    /// this screen, and from in front of them its light reaches their faces.
    static let back: Float = 1.0

    static let server = CGPoint(x: -0.62, y: 0.2)
    static let cloud = CGPoint(x: 0.5, y: 0.62)
    static let laptop = CGPoint(x: 0.54, y: -0.52)

    /// Where Docker's symbol is drawn, and how wide: a little below the middle, between the
    /// three. Its height follows from its drawing, which is 1252 by 757.78.
    static let symbol = CGRect(x: -0.2, y: -0.157, width: 0.32, height: 0.32 * 757.78 / 1252)

    /// The room Docker asks to be left round its symbol: half its width, on every side.
    static var symbolClearSpace: CGRect { symbol.insetBy(dx: -symbol.width / 2, dy: -symbol.width / 2) }

    /// Where the weight of each drawing is, as a share of its width from its left edge: the
    /// middle of its ink and not of its box, measured from the drawings. The whale's tail
    /// stands out to the right of a body that is most of it, and the name ends on an open "r".
    static let symbolWeight: CGFloat = 0.436
    static let logotypeWeight: CGFloat = 0.478
    /// How far down the name's height its small letters begin. Above that is only the stems
    /// of the "d" and the "k", which the eye does not count as the top of the word.
    static let logotypeStems: CGFloat = 0.268
    /// The width of the "o" in the name, which Docker measures its name's clear space in: 548
    /// of the drawing's 3164.78.
    static var logotypeO: CGFloat { logotype.width * 548 / 3164.78 }

    /// Where Docker's name is drawn: under the symbol and as wide as it, placed by eye and not
    /// by box. Its weight is under the symbol's weight, which puts it a little to the left of
    /// the symbol's edges, and its small letters begin one "o" below the symbol, the stems
    /// reaching up into that. Its height follows from its drawing, which is 3164.78 by 757.78.
    ///
    /// That is nearer than the room Docker asks to be left round its symbol. The two are to be
    /// read as one mark here, and at that distance they read as two things.
    static var logotype: CGRect {
        let width = symbol.width
        let height = width * 757.78 / 3164.78
        let o = width * 548 / 3164.78
        return CGRect(x: symbol.minX + symbol.width * symbolWeight - width * logotypeWeight,
                      y: symbol.minY - o - height * (1 - logotypeStems), width: width, height: height)
    }

    /// The room Docker asks to be left round its name: the width of its "o" on every side.
    static var logotypeClearSpace: CGRect { logotype.insetBy(dx: -logotypeO, dy: -logotypeO) }

    /// The symbol and the name together: what is pressed to go to Docker's own pages.
    static var dockerLink: CGRect { symbol.union(logotype) }
    static let dockerHome = URL(string: "https://www.docker.com")!

    /// An arrow from the symbol out to a model: where it leaves, what it bends toward, and
    /// where its head lands. Each leaves from outside the symbol's clear space.
    struct Arrow: Equatable, Sendable {
        var from: CGPoint
        var bend: CGPoint
        var to: CGPoint
    }

    static let arrows: [Arrow] = [
        Arrow(from: CGPoint(x: -0.3, y: -0.37), bend: CGPoint(x: -0.56, y: -0.54), to: CGPoint(x: -0.66, y: -0.16)),
        Arrow(from: CGPoint(x: -0.08, y: 0.25), bend: CGPoint(x: -0.12, y: 0.72), to: CGPoint(x: 0.2, y: 0.7)),
        Arrow(from: CGPoint(x: 0.32, y: -0.04), bend: CGPoint(x: 0.62, y: 0.1), to: CGPoint(x: 0.6, y: -0.2)),
    ]

    /// The three models (`Models/Hosting.blend`), each one a piece. They keep the colours
    /// their file gave them, but for what is the app's accent — the server's lamps, what is
    /// typed on the laptop — and the cloud, which is drawn against a different sky by day.
    @MainActor static let stage = StageScene(extent: extent, sway: 0, back: back) {
        guard let model = await Stage.load("Hosting") else { return nil }
        Stage.piece("Server", in: model, drift: 0.01, rock: 0.2, phase: 0.6)
        Stage.piece("Cloud", in: model, drift: 0.02, rock: 0.16, phase: 2.4)
        Stage.piece("Laptop", in: model, drift: 0.01, rock: 0.2, phase: 4.4)
        return model
    } paint: { model, palette in
        Stage.dress("ServerLamps", in: model, Stage.material(palette.accent, glow: 2.4))
        Stage.dress("LaptopType", in: model, Stage.material(palette.accent(paler: 0.2), glow: 2.2))
        Stage.dress("Cloud", in: model, Stage.material(palette.dark ? CSSColor(red: 0.93, green: 0.96, blue: 1)
                                                                    : CSSColor(red: 0.74, green: 0.84, blue: 0.97), roughness: 0.75))
    }
}

// MARK: The drawing

/// What is flat on the hosting stage: Docker's symbol with its name under it, and an arrow
/// from the symbol to each model.
///
/// The symbol and the name are Docker's own drawings, from its brand guidelines
/// (docker.com/company/newsroom/media-resources), and each is used the way they ask of it:
/// as it is drawn, in one of its two colours — the light one on a dark ground, the dark one
/// on a light — with nothing laid over it, and with its clear space kept from everything but
/// the other. The guidelines have a mark of the two together, side by side, and ask that its
/// parts not be moved about; one over the other, as close as they are here, is not an
/// arrangement they give. It is how this screen was asked to show them.
struct HostingDiagram: View {
    @Environment(\.theme) private var theme
    @Environment(\.colorScheme) private var scheme
    /// Shown without the models: a symbol stands where each would be.
    @Environment(\.onboardingIsFlat) private var flat

    /// Docker's "Zero" and "Ether".
    private var ink: Color {
        scheme == .dark ? Color(.sRGB, red: 1, green: 0.992, blue: 0.98) : Color(.sRGB, red: 0.063, green: 0.063, blue: 0.063)
    }

    var body: some View {
        GeometryReader { geometry in
            let view = geometry.size
            let place = { (point: CGPoint) in Stage.point(point, extent: HostingModels.extent, view: view) }
            let unit = place(CGPoint(x: 1, y: 0)).x - place(.zero).x

            Canvas { context, _ in
                // In the accent, and all of it: an arrow is part of what the screen says.
                let stroke = StrokeStyle(lineWidth: max(2, unit * 0.022), lineCap: .round, lineJoin: .round)
                for arrow in HostingModels.arrows {
                    var line = Path()
                    line.move(to: place(arrow.from))
                    line.addQuadCurve(to: place(arrow.to), control: place(arrow.bend))
                    context.stroke(line, with: .color(theme.primary), style: stroke)

                    // The head points the way the line was going as it arrived.
                    let end = place(arrow.to), toward = place(arrow.bend)
                    let angle = atan2(end.y - toward.y, end.x - toward.x)
                    let reach = unit * 0.075
                    var head = Path()
                    head.move(to: CGPoint(x: end.x - reach * cos(angle - 0.5), y: end.y - reach * sin(angle - 0.5)))
                    head.addLine(to: end)
                    head.addLine(to: CGPoint(x: end.x - reach * cos(angle + 0.5), y: end.y - reach * sin(angle + 0.5)))
                    context.stroke(head, with: .color(theme.primary), style: stroke)
                }
            }

            // Read out and pressed as one thing, by what is laid over them (`DockerLink`).
            let symbol = HostingModels.symbol
            Image("DockerSymbol")
                .resizable()
                .foregroundStyle(ink)
                .frame(width: symbol.width * unit, height: symbol.height * unit)
                .position(place(CGPoint(x: symbol.midX, y: symbol.midY)))
                .accessibilityHidden(true)

            let logotype = HostingModels.logotype
            Image("DockerLogotype")
                .resizable()
                .foregroundStyle(ink)
                .frame(width: logotype.width * unit, height: logotype.height * unit)
                .position(place(CGPoint(x: logotype.midX, y: logotype.midY)))
                .accessibilityHidden(true)

            if flat {
                ForEach([("xserve", HostingModels.server), ("cloud.fill", HostingModels.cloud), ("laptopcomputer", HostingModels.laptop)], id: \.0) { name, at in
                    Image(systemName: name)
                        .font(.system(size: unit * 0.3))
                        .foregroundStyle(theme.textSecondary)
                        .position(place(at))
                        .accessibilityHidden(true)
                }
            }
        }
    }
}

/// What is pressed to go to Docker's own pages: its symbol and its name, and the space between.
///
/// Laid over the room the models are in, since that room takes every touch in it for the
/// models. A touch here is not one of those: no model stands on the symbol.
struct DockerLink: View {
    @Environment(\.openURL) private var openURL

    var body: some View {
        GeometryReader { geometry in
            let view = geometry.size
            let link = HostingModels.dockerLink
            let middle = Stage.point(CGPoint(x: link.midX, y: link.midY), extent: HostingModels.extent, view: view)
            let unit = Stage.point(CGPoint(x: 1, y: 0), extent: HostingModels.extent, view: view).x
                - Stage.point(.zero, extent: HostingModels.extent, view: view).x

            Button {
                openURL(HostingModels.dockerHome)
            } label: {
                Color.clear.contentShape(.rect)
            }
            .buttonStyle(.plain)
            // A little past the drawing on every side: a name a few points tall is a small thing to hit.
            .frame(width: link.width * unit + 16, height: link.height * unit + 16)
            .position(middle)
            .accessibilityLabel("Docker")
            .accessibilityHint("Opens docker.com")
            .accessibilityAddTraits(.isLink)
            #if os(macOS)
            .pointerStyle(.link)
            #endif
        }
    }
}
