import SwiftUI
import UIKit.UIGestureRecognizerSubclass

// A phone shows one column of a board at a time. The three columns are not
// three pages with a scroll position each, though: they are one page that
// scrolls down, in which every row's columns slide sideways together. So a
// row — an epic — is at the same height whichever column is showing, its
// header stays where it is while its cards change under it, and switching
// column never loses the place.

extension EnvironmentValues {
    /// The column a phone's board is showing, as a fraction while it is moving between two.
    @Entry var columnPage: CGFloat = 0
}

/// Lays its subviews out as pages side by side, one page wide each, and shows
/// the one `page` names — or parts of two, between whole numbers.
nonisolated struct PagedColumns: Layout, Animatable {
    enum Height {
        /// As tall as the tallest page: a row of a grid, the same in every column.
        case tallest
        /// As tall as the page showing, and in between while two are.
        case showing
    }

    var page: CGFloat
    var height: Height = .tallest
    var spacing: CGFloat = 24

    var animatableData: CGFloat {
        get { page }
        set { page = newValue }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width ?? 320
        let heights = subviews.map { $0.sizeThatFits(ProposedViewSize(width: width, height: nil)).height }
        guard !heights.isEmpty else { return CGSize(width: width, height: 0) }
        switch height {
        case .tallest:
            return CGSize(width: width, height: heights.max() ?? 0)
        case .showing:
            let position = min(max(page, 0), CGFloat(heights.count - 1))
            let lower = Int(position.rounded(.down)), upper = min(lower + 1, heights.count - 1)
            let fraction = position - CGFloat(lower)
            return CGSize(width: width, height: heights[lower] + (heights[upper] - heights[lower]) * fraction)
        }
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for (index, subview) in subviews.enumerated() {
            let x = bounds.minX + (CGFloat(index) - page) * (bounds.width + spacing)
            // Offered the row's whole height, so a short column is still a full-height place to drop a card.
            subview.place(at: CGPoint(x: x, y: bounds.minY), anchor: .topLeading,
                          proposal: ProposedViewSize(width: bounds.width, height: bounds.height))
        }
    }
}

/// A row's columns, following the page's column.
struct ColumnPages<Content: View>: View {
    @Environment(\.columnPage) private var page
    var height: PagedColumns.Height = .tallest
    @ViewBuilder var content: Content

    var body: some View {
        PagedColumns(page: page, height: height) { content }
            // The columns out of view are still there, off to either side.
            .clipped()
    }
}

/// Turns a sideways drag anywhere on the page into a change of column, and
/// tells the rows inside where the columns are while the finger moves them.
private struct ColumnPaging: ViewModifier {
    @Binding var selection: TaskState
    /// How far the finger has carried the columns, in pages.
    @State private var carried: CGFloat = 0

    private var index: Int { TaskState.allCases.firstIndex(of: selection) ?? 0 }

    func body(content: Content) -> some View {
        let last = TaskState.allCases.count - 1
        content
            .environment(\.columnPage, min(max(CGFloat(index) + carried, -0.12), CGFloat(last) + 0.12))
            .gesture(ColumnPan(index: index, last: last) { pages in
                carried = pages
            } onEnd: { target in
                withAnimation(.snappy(duration: 0.32)) {
                    carried = 0
                    selection = TaskState.allCases[target]
                }
            })
            .sensoryFeedback(.selection, trigger: selection)
    }
}

extension View {
    /// Makes this page's `PagedColumns` rows follow `selection`, and a sideways drag change it.
    func columnPaging(_ selection: Binding<TaskState>) -> some View {
        modifier(ColumnPaging(selection: selection))
    }
}

// MARK: - Following a moved card

/// Where the card the page should go to is, in the scroll view's own frame.
private struct LandingFrameKey: PreferenceKey {
    static let defaultValue: CGRect? = nil
    static func reduce(value: inout CGRect?, nextValue: () -> CGRect?) {
        value = value ?? nextValue()
    }
}

/// Scrolls the page so that a card which has just changed column sits in the
/// middle of it. The card says where it is; this does the arithmetic — a
/// scroll view asked to find a card by its identity inside rows like these
/// does not find it.
private struct LandingFollower: ViewModifier {
    /// Changes with every move that should be followed.
    var token: UUID?
    var onArrive: () -> Void

    @State private var position = ScrollPosition()
    @State private var metrics = Metrics()
    @State private var followed: UUID?
    @State private var target: CGRect?

    private struct Metrics: Equatable {
        var offset: CGFloat = 0
        var viewport: CGFloat = 0
        var content: CGFloat = 0
        var top: CGFloat = 0
        var bottom: CGFloat = 0
    }

    func body(content: Content) -> some View {
        content
            .scrollPosition($position)
            .onScrollGeometryChange(for: Metrics.self) { geometry in
                Metrics(offset: geometry.contentOffset.y, viewport: geometry.containerSize.height,
                        content: geometry.contentSize.height, top: geometry.contentInsets.top,
                        bottom: geometry.contentInsets.bottom)
            } action: { _, new in
                metrics = new
            }
            .onPreferenceChange(LandingFrameKey.self) { frame in
                Task { @MainActor in
                    target = frame
                    arrive()
                }
            }
    }

    private func arrive() {
        guard target != nil, let token, token != followed else { return }
        followed = token
        Task {
            // Once the columns have finished sliding: only then is the page as tall as it will be.
            try? await Task.sleep(for: .milliseconds(360))
            guard let frame = target else { return }
            // The middle of what the bars leave uncovered, not of the whole scroll view.
            let visibleMiddle = metrics.top + (metrics.viewport - metrics.top - metrics.bottom) / 2
            // A scroll position counts from where the page rests under its bars; the geometry's
            // offset counts from the scroll view's own top edge, the bars' height further up.
            let wanted = metrics.offset + metrics.top + frame.midY - visibleMiddle
            let limit = max(metrics.content - (metrics.viewport - metrics.top - metrics.bottom), 0)
            withAnimation(.smooth(duration: 0.45)) {
                position.scrollTo(y: min(max(wanted, 0), limit))
            }
            onArrive()
        }
    }
}

extension View {
    /// On the scroll view: go to whichever card inside is marked with `landingTarget`.
    func followsLanding(_ token: UUID?, onArrive: @escaping () -> Void) -> some View {
        modifier(LandingFollower(token: token, onArrive: onArrive))
    }

    /// On a card: this is the one the page should go to.
    func landingTarget(_ isTarget: Bool) -> some View {
        background {
            if isTarget {
                GeometryReader { proxy in
                    Color.clear.preference(key: LandingFrameKey.self, value: proxy.frame(in: .scrollView))
                }
            }
        }
    }
}

private struct ColumnPan: UIGestureRecognizerRepresentable {
    var index: Int
    var last: Int
    var onChange: (CGFloat) -> Void
    var onEnd: (Int) -> Void

    func makeUIGestureRecognizer(context: Context) -> Recognizer { Recognizer() }

    func updateUIGestureRecognizer(_ recognizer: Recognizer, context: Context) {
        recognizer.index = index
        recognizer.last = last
    }

    func handleUIGestureRecognizerAction(_ recognizer: Recognizer, context: Context) {
        let width = max(recognizer.view?.bounds.width ?? 1, 1)
        let pages = -recognizer.translation(in: recognizer.view).x / width
        switch recognizer.state {
        case .began, .changed:
            onChange(pages)
        case .ended:
            // A flick turns the page whatever the distance; a slow drag has to get half way.
            let flick = -recognizer.velocity(in: recognizer.view).x / width
            let step = abs(flick) > 0.9 ? (flick > 0 ? 1 : -1) : Int(pages.rounded())
            onEnd(min(max(index + min(max(step, -1), 1), 0), last))
        case .cancelled, .failed:
            onEnd(index)
        default:
            break
        }
    }

    /// A pan that only ever begins sideways, towards a column that exists.
    /// Other pans wait for it to decide — which it does within a few points —
    /// so the page's own scrolling is unchanged, and at the first column a
    /// swipe from the left is still the swipe back.
    final class Recognizer: UIPanGestureRecognizer {
        var index = 0
        var last = 0
        private var start: CGPoint?

        override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
            if start == nil, let touch = touches.first {
                start = touch.location(in: nil)
                // Something that scrolls sideways itself — a roadmap — keeps its own drags.
                var view = touch.view
                while let current = view, current !== self.view {
                    if let scroll = current as? UIScrollView, scroll.contentSize.width > scroll.bounds.width + 1 {
                        state = .failed
                        return
                    }
                    view = current.superview
                }
            }
            super.touchesBegan(touches, with: event)
        }

        override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent) {
            if state == .possible, let start, let touch = touches.first {
                let now = touch.location(in: nil)
                let dx = now.x - start.x, dy = now.y - start.y
                if hypot(dx, dy) > 4 {
                    let sideways = abs(dx) > abs(dy) * 1.2
                    let towardsNothing = (dx > 0 && index == 0) || (dx < 0 && index == last)
                    if !sideways || towardsNothing {
                        state = .failed
                        return
                    }
                }
            }
            super.touchesMoved(touches, with: event)
        }

        override func reset() {
            super.reset()
            start = nil
        }

        override func shouldBeRequiredToFail(by other: UIGestureRecognizer) -> Bool {
            other is UIPanGestureRecognizer
        }
    }
}
