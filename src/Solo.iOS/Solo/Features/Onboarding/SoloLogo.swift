import RealityKit
import SwiftUI

/// Solo's mark, as measurements: three bars that climb, on a rounded tile, and a small face
/// that is pleased about it. They are the app icon's own (`Icons/Solo.icon`), read off its
/// layers, so the mark drawn in the app is the one on the Home Screen — flat on the screen
/// that asks for an instance, and in pieces on the one before it.
///
/// Everything is in the tile's width: the tile is 1 across, about the origin, y upward.
nonisolated enum SoloLogo {
    struct Bar: Equatable, Sendable {
        /// The bar's box on the tile.
        var frame: CGRect
        var corner: CGFloat
        /// How much of the accent it is, front to back: the icon's bars are glass, and the
        /// one in front is the faintest.
        var strength: Double
    }

    /// How round the tile's corners are: the system's own for an app icon.
    static let tileCorner: CGFloat = 0.225

    /// Front to back, which is shortest to tallest.
    static let bars: [Bar] = [
        bar(left: 68, top: 520.5, right: 464, bottom: 972.5, corner: 99, strength: 0.45),
        bar(left: 294, top: 269.5, right: 699, bottom: 941.5, corner: 101, strength: 0.7),
        bar(left: 538, top: 27.5, right: 932, bottom: 914.5, corner: 98.5, strength: 1),
    ]

    /// A layer of the icon, from its 1000-wide drawing with y downward. The icon draws its
    /// layers at nine tenths, about the middle, which is what leaves the tile a margin.
    private static func bar(left: CGFloat, top: CGFloat, right: CGFloat, bottom: CGFloat, corner: CGFloat, strength: Double) -> Bar {
        let scale = 0.9 / 1000
        func across(_ value: CGFloat) -> CGFloat { (value - 500) * scale }
        return Bar(frame: CGRect(x: across(left), y: -across(bottom), width: (right - left) * scale, height: (bottom - top) * scale),
                   corner: corner * scale, strength: strength)
    }

    /// The face, as the squares of the icon's own drawing of it: twelve across and nine
    /// down, column then row from the top left.
    static let face: [(column: Int, row: Int)] = {
        let rows: [(Int, [Int])] = [
            (0, [1, 2, 3, 8, 9, 10]),
            (1, [0, 4, 7, 11]),
            (5, Array(1...10)),
            (6, [1, 10]),
            (7, [2, 9]),
            (8, Array(3...8)),
        ]
        return rows.flatMap { row, columns in columns.map { (column: $0, row: row) } }
    }()

    static let faceColumns = 12
    static let faceRows = 9

    /// Where the face sits on the icon, and how wide: low on the tallest bar.
    static let faceOnTile = (center: CGPoint(x: 0.27, y: -0.237), width: CGFloat(0.127))

    /// The middle of one of the face's squares, in a face `width` across about the origin.
    static func faceSquare(column: Int, row: Int, width: CGFloat) -> CGPoint {
        let side = width / CGFloat(faceColumns)
        return CGPoint(x: (CGFloat(column) + 0.5) * side - width / 2,
                       y: CGFloat(faceRows) * side / 2 - (CGFloat(row) + 0.5) * side)
    }
}

// MARK: Flat

/// The mark drawn flat: the tile as glass, the bars in the accent, the face on the tallest.
struct SoloLogoMark: View {
    @Environment(\.theme) private var theme
    var size: CGFloat = 104

    var body: some View {
        Canvas { context, canvas in
            let side = min(canvas.width, canvas.height)
            // The tile's units to the canvas's: its middle to the middle, and y turned over.
            func place(_ rect: CGRect) -> CGRect {
                CGRect(x: canvas.width / 2 + rect.minX * side, y: canvas.height / 2 - rect.maxY * side,
                       width: rect.width * side, height: rect.height * side)
            }
            // Back to front, so the short bar is over the tall one as it is on the icon.
            for bar in SoloLogo.bars.reversed() {
                let shape = Path(roundedRect: place(bar.frame), cornerRadius: bar.corner * side, style: .continuous)
                context.fill(shape, with: .color(theme.primary.opacity(bar.strength)))
            }
            let face = SoloLogo.faceOnTile
            let square = face.width / CGFloat(SoloLogo.faceColumns)
            for cell in SoloLogo.face {
                let middle = SoloLogo.faceSquare(column: cell.column, row: cell.row, width: face.width)
                let rect = CGRect(x: face.center.x + middle.x - square / 2, y: face.center.y + middle.y - square / 2,
                                  width: square, height: square)
                context.fill(Path(place(rect)), with: .color(theme.onPrimary))
            }
        }
        .frame(width: size, height: size)
        .glassEffect(.regular.tint(theme.primary.opacity(0.12)), in: .rect(cornerRadius: size * SoloLogo.tileCorner, style: .continuous))
        .accessibilityHidden(true)
    }
}

// MARK: In pieces

extension SoloLogo {
    /// How much of the stage the pieces take up: the tile, and the face out below it.
    static let stageExtent = CGSize(width: 1.5, height: 1.86)

    /// The mark taken apart (`Models/SoloLogo.blend`): the tile at the back, the bars in
    /// front of it one before another, and the face out on its own below. Nothing is
    /// square to anything else and no two pieces are at one depth, so a turn of the stage
    /// shows every one of them. The model is the shapes; the colours are the app's own
    /// accent for the scheme, each bar a little paler than the one behind as the icon's are.
    @MainActor static func model(_ palette: StagePalette) async -> Entity? {
        guard let model = await Stage.load("SoloLogo") else { return nil }
        Stage.dress("Tile", in: model, Stage.material(palette.gray(palette.dark ? 0.15 : 0.97), roughness: 0.32))
        for (name, bar) in zip(["BarFront", "BarMiddle", "BarBack"], bars) {
            Stage.dress(name, in: model, Stage.material(palette.accent(paler: (1 - bar.strength) * 0.7), roughness: 0.38))
        }
        Stage.dress("Face", in: model, Stage.material(palette.gray(palette.dark ? 1 : 0.16), roughness: 0.3))

        for (index, name) in ["Tile", "BarBack", "BarMiddle", "BarFront"].enumerated() {
            Stage.piece(name, in: model, phase: Float(index) * 1.7)
        }
        Stage.piece("Face", in: model, drift: 0.012, phase: 4.1)
        return model
    }
}
