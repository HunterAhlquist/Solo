import RealityKit
import SwiftUI

/// Solo's mark in pieces (`Models/SoloLogo.blend`): the tile at the back, the three bars
/// in front of it one before another, and the face out on its own below. Nothing is square
/// to anything else and no two pieces are at one depth, so a turn of the stage shows every
/// one of them.
///
/// The model is the shapes. The colours are the app icon's own, read off its layers
/// (`Icons/Solo.icon/icon.json`), so the bars are the glass they are on the Home Screen:
/// each a fade of colour, and each seen through by as much as its layer is.
nonisolated enum SoloLogo {
    /// A bar, front to back: its object in the model, and how much of its layer shows — the
    /// layer's opacity in the icon, which for the front one is less by day than by night.
    static let bars: [(name: String, opacity: (light: Float, dark: Float))] = [
        ("BarFront", (0.2, 0.3)),
        ("BarMiddle", (0.6, 0.6)),
        ("BarBack", (0.8, 0.8)),
    ]

    /// The fade every bar is filled with, top to bottom, in Display P3 as the icon has it.
    static func fill(dark: Bool) -> (top: CSSColor, bottom: CSSColor) {
        dark ? (CSSColor(red: 1, green: 0.90636, blue: 0), CSSColor(red: 0.40943, green: 0.38161, blue: 0))
             : (CSSColor(red: 0.94039, green: 0.72495, blue: 0.49502), CSSColor(red: 1, green: 0.44275, blue: 0))
    }

    /// The view is centred down and to the right of the tile: the face is out that way,
    /// and so is the glow round it.
    @MainActor static let stage = StageScene(extent: CGSize(width: 1.5, height: 1.86), middle: [0.12, -0.1], load: {
        guard let model = await Stage.load("SoloLogo") else { return nil }
        for (index, name) in ["Tile", "BarBack", "BarMiddle", "BarFront"].enumerated() {
            Stage.piece(name, in: model, phase: Float(index) * 1.7)
        }
        Stage.piece("Face", in: model, drift: 0.012, phase: 4.1)
        return model
    }, paint: { model, palette in
        Stage.dress("Tile", in: model, Stage.material(palette.gray(palette.dark ? 0.15 : 0.97), roughness: 0.32))
        // The face is lit from inside: its own colour given off, with enough of the room's
        // light still on it to tell its blocks apart.
        Stage.dress("Face", in: model, Stage.material(face(dark: palette.dark), roughness: 0.3, glow: 0.85))
    }, glass: { palette in
        let fill = fill(dark: palette.dark)
        return bars.map {
            StageGlass(name: $0.name, corner: corner, top: fill.top, bottom: fill.bottom,
                       opacity: palette.dark ? $0.opacity.dark : $0.opacity.light)
        }
    }, glow: { palette in
        StageGlow(name: "Face", colour: face(dark: palette.dark), strength: palette.dark ? 0.5 : 0.55)
    })

    /// The face's light: white in the dark, as it is on the icon, and by day the accent,
    /// since something dark gives off nothing and white would be lost on a light ground.
    static func face(dark: Bool) -> CSSColor { dark ? CSSColor(red: 1, green: 1, blue: 1) : .appAccentLight }

    /// The radius of a bar's corners in the model: the icon's, at the size the model is made.
    static let corner: Float = 0.089
}

/// The app's own icon: the one on the Home Screen or in the Dock, whichever build this is.
struct AppIconImage: View {
    var size: CGFloat = 104

    var body: some View {
        Group {
            #if os(macOS)
            // A Mac hands its icon over, with its shape and its margin: it is drawn a
            // little larger, so that what is seen of it is the size asked for.
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .frame(width: size * 1.22, height: size * 1.22)
                .frame(width: size, height: size)
            #else
            Image(Self.artwork)
                .resizable()
                .frame(width: size, height: size)
                .shadow(color: .black.opacity(0.25), radius: 10, y: 4)
            #endif
        }
        .accessibilityHidden(true)
    }

    /// An iPhone doesn't hand its icon over: asked for an Icon Composer icon by name, the
    /// asset catalogue stops the app. So the app carries a picture of each of its icons,
    /// made from the same file by the script that makes the web's
    /// (`src/Solo.Desktop/scripts/build-icon.mjs`), and shows the one this build wears.
    static var artwork: String {
        let icons = Bundle.main.object(forInfoDictionaryKey: "CFBundleIcons") as? [String: Any]
        let name = (icons?["CFBundlePrimaryIcon"] as? [String: Any])?["CFBundleIconName"] as? String
        return name == "Solo Preview" ? "AppIconArtworkPreview" : "AppIconArtwork"
    }
}
