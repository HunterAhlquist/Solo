import SwiftUI

// What is finished is history, and the web keeps it from taking a lane over
// (KanbanZone.razor, js/finished-column.js). A Finished column may stand as
// tall as the work beside it that is not finished, or a fixed height where
// that is short, and no taller: past that it scrolls inside itself. And a
// column that has to scroll starts out put away behind "Show finished", so a
// lane is as tall as what is left to do in it. This is the same, to the same
// numbers.

extension EnvironmentValues {
    /// False inside a column that scrolls by itself, where a card's place is not a place on the
    /// page: the page then goes to the column (`landingTarget`).
    @Entry var landsInPage = true
}

nonisolated enum FinishedColumn {
    /// The room a Finished column's cards have however little stands beside them: the web's 32rem × ⅔.
    static let floor: CGFloat = 340
    /// A card's height, near enough, for a column nothing has measured yet.
    static let guess: CGFloat = 70
    /// The height of the button that shows and hides the column.
    static let toggle: CGFloat = 30

    /// Whether cards this tall are more than a Finished column may show beside active columns this tall.
    static func overflows(cards: CGFloat, beside active: CGFloat) -> Bool {
        cards > max(floor, active) + 1
    }

    /// The room for the cards of a column that overflows: what the active columns take, less the button.
    static func room(beside active: CGFloat) -> CGFloat {
        max(floor, active - toggle)
    }

    /// A card's height and the gap under it, going by the active columns beside a Finished one:
    /// how tall each stands and how many cards it holds. `extra` is what a column's height
    /// has in it besides cards and the gaps between them.
    static func guess(from columns: [(height: CGFloat, cards: Int)], extra: CGFloat, spacing: CGFloat) -> CGFloat {
        let filled = columns.filter { $0.cards > 0 && $0.height > extra }
        let cards = filled.reduce(0) { $0 + $1.cards }
        guard cards > 0 else { return guess }
        return filled.reduce(0) { $0 + $1.height - extra + spacing } / CGFloat(cards)
    }
}

/// How tall a lane's active columns stand, kept where only its Finished column is listening.
/// The active columns say so as they are laid out, which while the page scrolls is many times a
/// second; were this the lane's own state, each word would have every column of the lane worked
/// out again, and on a warm phone that was a third of its frames.
@Observable final class LaneHeights {
    private(set) var active: [TaskState: CGFloat] = [:]

    func set(_ status: TaskState, _ height: CGFloat) {
        if active[status] != height { active[status] = height }
    }

    var tallest: CGFloat { active.values.max() ?? 0 }
}

/// A Finished column's cards: no taller than they are allowed, scrolling inside
/// that when there are more, and then behind a button until they are asked for.
///
/// Whether they are more than allowed is a matter of how tall they stand, which
/// only building them says. So a column put away, whose cards are not built, is
/// judged by the last time they were, or failing that by a guess; one that is
/// showing is measured, and puts itself away if the guess was kind.
struct FinishedFold<Cards: View>: View {
    @Environment(\.theme) private var theme
    /// Every finished card of the column, the ones folded past 25 included: what the button counts.
    var count: Int
    /// How many of them are in `cards`.
    var showing: Int
    /// How tall the lane's active columns stand.
    var beside: CGFloat
    /// A card's height, as near as the lane can say before these are built.
    var guess: CGFloat = FinishedColumn.guess
    /// Whether a column that overflows has been opened.
    var open: Bool
    var onToggle: () -> Void
    /// A card in here that has just landed, for the column to go to.
    var landing: UUID?
    var spacing: CGFloat = 6
    var inset: CGFloat = 6
    /// Draw the cards' glass as one, which suits a column of a lane's size and not a long one (see `Zone`).
    var sharedGlass = false
    /// The cards, as the rows of a stack.
    @ViewBuilder var cards: Cards

    /// How tall the cards stood when they were last built, and how many there were. `exact` when
    /// every one of them was built, and not only the ones in view with the rest reckoned.
    @State private var measured: (height: CGFloat, showing: Int, exact: Bool)?
    /// A card the page goes to has to exist to be found, so a column one has landed in is built
    /// whole from then on.
    @State private var whole = false

    /// How tall the cards stand, or would.
    private var height: CGFloat {
        guard let measured, measured.showing > 0 else { return CGFloat(showing) * guess + inset * 2 }
        // Cards have come or gone since: near enough until they are built again.
        return measured.height * CGFloat(showing) / CGFloat(measured.showing)
    }

    private var overflows: Bool { FinishedColumn.overflows(cards: height, beside: beside) }
    private var collapsed: Bool { overflows && !open }
    /// Past the fold a column can be hundreds of cards, which are then built as they come into
    /// view — and a stack that does that cannot say how tall it is.
    private var lazy: Bool { showing > BoardDrag.finishedVisibleLimit }

    var body: some View {
        VStack(spacing: 0) {
            if overflows {
                Button(action: onToggle) {
                    HStack(spacing: 5) {
                        Image(systemName: "chevron.right")
                            .font(.caption2.weight(.bold))
                            .rotationEffect(.degrees(collapsed ? 0 : 90))
                        Text("\(collapsed ? "Show" : "Hide") finished (\(count))")
                            .font(.footnote.weight(.medium))
                    }
                    .foregroundStyle(theme.textSecondary)
                    .padding(.horizontal, 8)
                    .frame(maxWidth: .infinity, minHeight: FinishedColumn.toggle, alignment: .leading)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityHint(collapsed ? "Shows this column's finished tasks" : "Hides this column's finished tasks")
            }

            if collapsed {
                // Nothing: the point of putting them away is not to build them.
            } else if overflows {
                ScrollViewReader { proxy in
                    ScrollView {
                        Group {
                            if lazy {
                                LazyVStack(spacing: spacing) { cards }
                            } else {
                                VStack(spacing: spacing) { cards }
                                    .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { measure($0) }
                            }
                        }
                        .padding(inset)
                        .background(PassesOnAtEnds())
                    }
                    .environment(\.landsInPage, false)
                    .frame(height: FinishedColumn.room(beside: beside))
                    .task(id: landing) {
                        guard let landing else { return }
                        // Once the columns have finished sliding, as the page waits before it goes to the column.
                        try? await Task.sleep(for: .milliseconds(360))
                        withAnimation(.smooth(duration: 0.45)) { proxy.scrollTo(landing, anchor: .center) }
                    }
                }
            } else {
                // They fit: a zone like the ones beside it, its cards built as they come into view.
                Group {
                    if sharedGlass {
                        GlassEffectContainer(spacing: 0) { fitting }
                    } else {
                        fitting
                    }
                }
                .padding(inset)
            }
        }
        // The page goes to a column that scrolls, and the column to the card: a card inside it
        // cannot say where it is on the page, only where it is in the column.
        .landingTarget(landing != nil && overflows)
        .onChange(of: landing) { _, landing in
            if landing != nil { whole = true }
        }
        .animation(.snappy(duration: 0.25), value: collapsed)
    }

    @ViewBuilder private var fitting: some View {
        if whole || landing != nil {
            VStack(spacing: spacing) { cards }
                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { measure($0) }
        } else {
            LazyVStack(spacing: spacing) { cards }
                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { measure($0, exact: false) }
        }
    }

    private func measure(_ stack: CGFloat, exact: Bool = true) {
        guard stack > 0 else { return }
        // A stack that builds its cards as they come into view reckons the rest, and reckons
        // again as more arrive. Its first word is taken and the later ones are not: a column
        // that was let stand does not put itself away while it is being read.
        if !exact, let measured, measured.showing == showing { return }
        measured = (stack + inset * 2, showing, exact)
    }
}


/// Inside a scroll view that sits in a page that scrolls the same way: stops it from bouncing.
/// A scroll view that bounces keeps every drag that starts on it, so at its end the drag pulls
/// at the column and the page stays where it is — and on a phone the column is most of the
/// screen. One that does not bounce lets a drag past its end go on to the page.
private struct PassesOnAtEnds: UIViewRepresentable {
    func makeUIView(context: Context) -> UIView { Probe() }
    func updateUIView(_ view: UIView, context: Context) {}

    private final class Probe: UIView {
        override func didMoveToWindow() {
            super.didMoveToWindow()
            var view = superview
            while let current = view {
                if let scroll = current as? UIScrollView {
                    scroll.bounces = false
                    return
                }
                view = current.superview
            }
        }
    }
}
