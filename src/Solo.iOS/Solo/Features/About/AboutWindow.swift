#if os(macOS)
import SwiftUI

/// About Solo, in the app's menu. It stands in for AppKit's own About panel and is laid out
/// after it — the icon, the name, the version — because this one hides a game, as the desktop
/// shell's does (`Solo.Desktop/about`): three clicks on the icon swap it for a tiny
/// falling-blocks game, and that swap is the only thing that looks different.
struct AboutWindow: View {
    static let scene = "about"

    static var appName: String {
        let bundle = Bundle.main
        return bundle.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
            ?? bundle.object(forInfoDictionaryKey: "CFBundleName") as? String
            ?? "Solo"
    }

    private static let source = URL(string: "https://github.com/HunterAhlquist/Solo")!

    @State private var playing = false
    @State private var clicks = 0
    @State private var lastClick = Date.distantPast

    // Counted rather than asked of the click, so three unhurried clicks count as well as a
    // triple-click.
    private static let clickGap: TimeInterval = 0.6
    /// The window grows to hold the game and shrinks back to the icon, its top edge where it was.
    private static let swap = Animation.easeOut(duration: 0.18)

    var body: some View {
        VStack(spacing: 0) {
            stage
                .padding(.bottom, 8)
            Text(Self.appName)
                .font(.system(size: 14, weight: .bold))
                .padding(.bottom, 2)
            Text(version)
                .font(.system(size: 11))
            VStack(spacing: 2) {
                if let copyright {
                    Text(copyright)
                }
                // The licence the copyright is held under (LICENSE), and where the source is.
                Text("Released under the MIT License")
                Link("github.com/HunterAhlquist/Solo", destination: Self.source)
                    // The system's colour for a link: the panel is outside the instance's theme.
                    .foregroundStyle(Color(nsColor: .linkColor))
                    .pointerStyle(.link)
                    .padding(.top, 2)
            }
            .font(.system(size: 10))
            .foregroundStyle(.secondary)
            .padding(.top, 10)
        }
        .multilineTextAlignment(.center)
        // Nothing above the icon: the room the hidden bar still keeps for the window's
        // buttons is the panel's top margin.
        .padding(EdgeInsets(top: 0, leading: 20, bottom: 18, trailing: 20))
        .frame(width: 300)
        // Closed mid-game, it opens next on the icon.
        .onDisappear { playing = false }
    }

    @ViewBuilder
    private var stage: some View {
        if playing {
            FallingBlocksView { withAnimation(Self.swap) { playing = false } }
                .transition(.asymmetric(insertion: .scale(scale: 0.92).combined(with: .opacity), removal: .identity))
        } else {
            Button(action: click) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .interpolation(.high)
                    .frame(width: 96, height: 96)
            }
            .buttonStyle(Unmarked())
            .accessibilityLabel(Self.appName)
        }
    }

    private func click() {
        let now = Date.now
        clicks = now.timeIntervalSince(lastClick) < Self.clickGap ? clicks + 1 : 1
        lastClick = now
        if clicks == 3 {
            clicks = 0
            withAnimation(Self.swap) { playing = true }
        }
    }

    private var version: String {
        let bundle = Bundle.main
        let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        let build = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        return build.map { "Version \(version) (\($0))" } ?? "Version \(version)"
    }

    private var copyright: String? {
        Bundle.main.object(forInfoDictionaryKey: "NSHumanReadableCopyright") as? String
    }

    /// A button that looks pressed gives the game away: the icon stays an icon.
    private struct Unmarked: ButtonStyle {
        func makeBody(configuration: Configuration) -> some View { configuration.label }
    }
}

/// The game, where the icon was: the board with the score over it and the keys under it. It
/// is played from the keyboard and nothing else, so it is a Mac's alone.
private struct FallingBlocksView: View {
    /// Escape: back to the icon.
    var close: () -> Void

    @Environment(\.colorScheme) private var scheme
    @Environment(\.appearsActive) private var active
    @FocusState private var focused: Bool
    @State private var game = FallingBlocks()

    private static let cell: CGFloat = 8
    // A one-point gutter on every side of every cell, so the board is a point wider and
    // taller than the cells alone.
    private static let width = CGFloat(FallingBlocks.columns) * cell + 1
    private static let height = CGFloat(FallingBlocks.rows) * cell + 1
    // Room for the widest piece (the I, four across) and the tallest lying flat.
    private static let nextSize = CGSize(width: 4 * cell + 1, height: 2 * cell + 1)
    private static let label = Font.system(size: 9, weight: .semibold, design: .monospaced)

    var body: some View {
        let palette = Palette(dark: scheme == .dark)
        VStack(spacing: 4) {
            VStack(spacing: 3) {
                stat("SCORE", game.score)
                stat("LINES", game.lines)
            }
            .frame(width: Self.width)

            // SwiftUI's, by its full name: the roadmap's own view is a TimelineView too.
            SwiftUI.TimelineView(.animation(paused: game.phase != .playing)) { frame in
                board(palette, at: frame.date.timeIntervalSinceReferenceDate)
                    .onChange(of: frame.date) { _, date in
                        game.tick(at: date.timeIntervalSinceReferenceDate)
                    }
            }
            .overlay { veil }
            // Hung off the board's side and not laid out beside it, so the board stays
            // centred where the icon was.
            .overlay(alignment: .topLeading) {
                VStack(spacing: 4) {
                    Text("NEXT")
                        .font(Self.label)
                        .tracking(0.36)
                        .foregroundStyle(.secondary)
                    nextPiece(palette)
                }
                .fixedSize()
                .offset(x: Self.width + 8)
            }

            Text("← → ↓ move · ↑ drop · space turn")
                .font(.system(size: 9))
                .foregroundStyle(.secondary)
        }
        .focusable()
        .focused($focused)
        .focusEffectDisabled()
        .onKeyPress(phases: .all, action: key)
        .onAppear { focused = true }
        // Nobody is playing a window they've clicked away from.
        .onChange(of: active) { _, active in
            if !active { game.pause(at: Self.now) }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Falling blocks. Score \(game.score), lines \(game.lines).")
    }

    private static var now: TimeInterval { Date.now.timeIntervalSinceReferenceDate }

    private func stat(_ name: String, _ value: Int) -> some View {
        HStack {
            Text(name).foregroundStyle(.secondary)
            Spacer(minLength: 0)
            Text("\(value)").monospacedDigit()
        }
        .font(Self.label)
        .tracking(0.36)
    }

    // MARK: Keys

    private func key(_ press: KeyPress) -> KeyPress.Result {
        // Leaves ⌘W and the rest of the menu's shortcuts alone.
        guard press.modifiers.isDisjoint(with: [.command, .control, .option]) else { return .ignored }
        let key: FallingBlocks.Key = switch press.key {
        case .leftArrow: .left
        case .rightArrow: .right
        case .downArrow: .down
        case .upArrow: .up
        case .space: .space
        case .return: .enter
        default:
            switch press.characters.lowercased() {
            case "x": .turnClockwise
            case "z": .turnCounterclockwise
            case "p": .pause
            default: .other
            }
        }
        switch press.phase {
        case .up:
            game.release(key)
        case _ where press.key == .escape:
            close()
        default:
            game.press(key, repeated: press.phase == .repeat, at: Self.now)
        }
        // Every key is the game's while it is showing: one handed back is one the window
        // answers with the alert sound.
        return .handled
    }

    // MARK: Drawing

    private func board(_ palette: Palette, at now: TimeInterval) -> some View {
        Canvas { context, _ in
            for (y, row) in game.board.enumerated() {
                for (x, kind) in row.enumerated() {
                    if let kind {
                        fill(.init(x: x, y: y), palette.piece(kind), in: context)
                    } else {
                        context.fill(Path(Self.rect(.init(x: x, y: y))), with: .color(palette.cell))
                    }
                }
            }

            let flash = game.flashStrength(at: now)
            if flash > 0, let cells = game.flash?.cells {
                for cell in cells {
                    context.fill(Path(Self.rect(cell)), with: .color(.white.opacity(flash * 0.85)))
                }
            }

            guard game.phase != .over else { return }
            let color = palette.piece(game.piece.kind)
            var shadow = context
            shadow.opacity = 0.22
            let fall = game.landingRow - game.piece.y
            for cell in game.piece.filled where cell.y + fall >= 0 {
                fill(.init(x: cell.x, y: cell.y + fall), color, in: shadow)
            }
            for cell in game.piece.filled where cell.y >= 0 {
                fill(cell, color, in: context)
            }
        }
        .frame(width: Self.width, height: Self.height)
        .background(palette.board, in: .rect(cornerRadius: 3))
    }

    /// The next piece, trimmed to the cells it fills and centred in its box.
    private func nextPiece(_ palette: Palette) -> some View {
        Canvas { context, size in
            let cells = FallingBlocks.Piece.filled(game.next.cells)
            guard let left = cells.map(\.x).min(), let right = cells.map(\.x).max(),
                  let top = cells.map(\.y).min(), let bottom = cells.map(\.y).max() else { return }
            var context = context
            context.translateBy(
                x: (size.width - 1 - CGFloat(right - left + 1) * Self.cell) / 2 - CGFloat(left) * Self.cell,
                y: (size.height - 1 - CGFloat(bottom - top + 1) * Self.cell) / 2 - CGFloat(top) * Self.cell)
            for cell in cells {
                fill(cell, palette.piece(game.next), in: context)
            }
        }
        .frame(width: Self.nextSize.width, height: Self.nextSize.height)
    }

    private static func rect(_ cell: FallingBlocks.Cell) -> CGRect {
        CGRect(x: CGFloat(cell.x) * Self.cell + 1, y: CGFloat(cell.y) * Self.cell + 1, width: Self.cell - 1, height: Self.cell - 1)
    }

    /// A block: its colour, and a line of light along its top edge.
    private func fill(_ cell: FallingBlocks.Cell, _ color: Color, in context: GraphicsContext) {
        let rect = Self.rect(cell)
        context.fill(Path(rect), with: .color(color))
        context.fill(Path(CGRect(x: rect.minX, y: rect.minY, width: rect.width, height: 1)), with: .color(.white.opacity(0.3)))
    }

    @ViewBuilder
    private var veil: some View {
        let words: (String, String)? = switch game.phase {
        case .ready: ("Ready", "press any key")
        case .paused: ("Paused", "press any key")
        case .over: ("Game over", "space to retry")
        case .playing: nil
        }
        if let words {
            VStack(spacing: 2) {
                Text(words.0).font(.system(size: 10, weight: .bold))
                Text(words.1).font(.system(size: 9)).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .windowBackgroundColor).opacity(0.82), in: .rect(cornerRadius: 3))
        }
    }

    /// The desktop game's colours: shades of Solo's own orange in the light and its yellow in
    /// the dark — the two themes' primaries — light to dark, one per piece.
    private struct Palette {
        var dark: Bool

        var board: Color { (dark ? Color.white : .black).opacity(0.06) }
        var cell: Color { (dark ? Color.white : .black).opacity(0.04) }

        func piece(_ kind: FallingBlocks.Kind) -> Color {
            let hex = switch kind {
            case .o: dark ? "#fffcc2" : "#f4b77f"
            case .s: dark ? "#fffa8a" : "#ef9a52"
            case .l: dark ? "#fff84a" : "#e8802f"
            case .i: dark ? "#fff600" : "#df6610"
            case .z: dark ? "#dcd400" : "#c4570c"
            case .t: dark ? "#b5af00" : "#a8480a"
            case .j: dark ? "#8a8600" : "#7a3206"
            }
            return Color(hex: hex)
        }
    }
}
#endif
