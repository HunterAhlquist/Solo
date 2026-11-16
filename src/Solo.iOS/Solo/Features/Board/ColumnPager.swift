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

// MARK: - Carrying a card to the next column

/// The space the board's page is laid out in: its scrolling content. Where the finger is, where
/// each card stands and where the card in hand is drawn are all said in it, so that none of them
/// has to be worked out again as the page scrolls.
enum BoardSpace {
    static let name = "board"
}

/// What is in hand on a board: whether anything is, what it is, where the finger has it, and
/// where it would land.
///
/// The system carries the thing — its drag and drop, which is what reaches other apps, scrolls
/// the page at its edges, turns the phone's columns and shares a long press with the card's
/// menu — and says nothing of it here beyond asking for what to carry when a press has gone on
/// long enough. So the beginning is known. The end is not told: neither the preview going nor
/// the item being let go of is reported in any useful time, and both were waited ten seconds
/// for. A drop on any part of the board is the end, since every part of it takes one; a drag
/// given up over the system's bars, which take none, is taken to be over once nothing has been
/// under it for a while.
///
/// What the system shows of the thing it carries is not used for a card. It is a picture of the
/// card shrunk to a size of the system's choosing, whatever size it is handed, and a card
/// picked up should still be that card. So the system is handed nothing to show, and the card
/// is drawn here, where the finger is (`spot`), as itself and a little larger.
@Observable
final class CardDragWatch {
    /// What can be picked up.
    enum Lift: Equatable {
        /// A card, and where it stood: the room it will want where it lands, and where it is
        /// first drawn in hand.
        case card(UUID, frame: CGRect)
        /// A lane, by its header, and a group, by its title, and where each stood.
        case epic(UUID, frame: CGRect)
        case group(UUID, frame: CGRect)

        var frame: CGRect {
            switch self {
            case .card(_, let frame), .epic(_, let frame), .group(_, let frame): frame
            }
        }
    }

    /// A place a card could land: before the card at `index` of a zone, or after the last.
    struct Slot: Equatable {
        var epicId: UUID?
        var status: TaskState
        var index: Int
    }

    /// A place a lane or a group could land, among the rows of the page.
    nonisolated enum Berth: Equatable, Sendable {
        /// Before a row: a lane, or a group's title. Before a lane that is in a group is in
        /// that group; before a title is among the groups, in none.
        case before(UUID)
        /// At the end of a group.
        case inside(UUID)
        /// After every lane and group.
        case last
    }

    private(set) var active = false
    /// What has been picked up, while something of this board's is.
    private(set) var lifted: Lift?
    /// Where the card in hand would land.
    private(set) var slot: Slot?
    /// Where the lane or the group in hand would land.
    private(set) var berth: Berth?
    /// How tall the thing in hand is drawn, which for a lane is not how tall it stood: it is
    /// carried folded. The room made for it is this tall.
    private(set) var held: CGFloat = 0
    /// Lanes are folded for the sake of what is in hand, and groups: from when it is picked up
    /// until just after it is put down, so that it is put down among the names it was carried
    /// over and the page opens again around it.
    private(set) var foldsLanes = false
    private(set) var foldsGroups = false
    /// Where each row of the page stands, in the board's space: a lane by its epic, a group's
    /// title by its group. What the rows say of themselves, for working out which two a lane
    /// in hand is between.
    @ObservationIgnored var rows: [UUID: CGRect] = [:]
    /// Where the board's space is on the screen, for somewhere outside the page to say where
    /// the finger is (`move(toScreen:)`).
    @ObservationIgnored var pageOrigin = CGPoint.zero
    /// Where the finger is, in the board's space, while a card is in hand over the page.
    private(set) var spot: CGPoint?
    /// How far into the card the finger took hold of it.
    private(set) var grab = CGSize.zero
    /// Where the room for the card in hand is, in the board's space.
    private(set) var landing: CGRect?
    /// The card has been let go and is on its way to where it lands.
    private(set) var settling = false
    /// What the thing in hand is called to whoever takes it, as it was handed to the system.
    @ObservationIgnored private(set) var payload: String?
    @ObservationIgnored private var idle: Task<Void, Never>?
    @ObservationIgnored private var leaving: Task<Void, Never>?
    @ObservationIgnored private var arriving: Task<Void, Never>?
    @ObservationIgnored private var unfolding: Task<Void, Never>?
    /// What a thing in hand is, from what it is called: the board's to say, since it is the
    /// board's cards and lanes that are known by those names. See `offered`.
    @ObservationIgnored var resolve: ((String) -> Lift?)?
    /// Where each card that is built stands, in the board's space: where one is picked up from.
    @ObservationIgnored var cards: [UUID: CGRect] = [:]

    /// The card in hand, when it is a card.
    var card: UUID? {
        if case .card(let id, _) = lifted { return id }
        return nil
    }

    /// The lane in hand, or the group, when it is one.
    var epic: UUID? {
        if case .epic(let id, _) = lifted { return id }
        return nil
    }

    var group: UUID? {
        if case .group(let id, _) = lifted { return id }
        return nil
    }

    /// Whether a lane is in hand, and of which kind.
    var carriesEpic: Bool {
        if case .epic = lifted { return true }
        return false
    }

    var carriesGroup: Bool {
        if case .group = lifted { return true }
        return false
    }

    /// Whether what is in hand is a card, as far as is known: something from elsewhere is
    /// taken for one.
    var carriesCard: Bool { active && !carriesEpic && !carriesGroup }

    /// The room to leave at a berth for the lane or the group in hand to land in.
    func room(for place: Berth) -> CGFloat {
        berth == place && (carriesEpic || carriesGroup) ? held : 0
    }

    /// Whether there is anywhere the thing in hand would land.
    var aimed: Bool { slot != nil || berth != nil }

    /// The room to leave before the card at `index` of a zone — or after its last, for the
    /// index one past it — for the card in hand to land in.
    func gap(epicId: UUID?, status: TaskState, index: Int) -> CGFloat {
        guard case .card(_, let frame) = lifted, slot == Slot(epicId: epicId, status: status, index: index) else { return 0 }
        return frame.height
    }

    /// Something has come over a place to let go, and this is what it calls itself: the name
    /// its provider was given when it was handed to the system (`BoardDrag.provider`). If it is
    /// one of this board's it is now in hand, and leaves its place.
    ///
    /// The moment to learn what is carried would seem to be when the system asks for it, and it
    /// is not. The system asks when it likes: at a press that goes on to open a menu and carry
    /// nothing, and then not at all for the next press on the same thing, which it answers from
    /// what it was told before. What does not vary is that a thing picked up is at once over
    /// somewhere it could be let go, having been picked up from there, and says its name.
    func offered(_ name: String?) {
        guard lifted == nil, !settling, let name, let lift = resolve?(name) else { return }
        payload = name
        unfolding?.cancel()
        held = 0
        withAnimation(.snappy(duration: 0.25)) {
            lifted = lift
            foldsLanes = carriesEpic || carriesGroup
            foldsGroups = carriesGroup
        }
    }

    /// A drop target reports a card arriving over it, or leaving.
    func hover(_ over: Bool) {
        idle?.cancel()
        if over {
            // The first target a card is over is where it was picked up from.
            if !active { Haptics.shared.play(.pickUp) }
            // Said after what is in hand is known (`offered`). Said before, there is a moment
            // when something is in hand and it is not yet a lane, and whatever shows itself
            // for a card — the arrows at the page's edges — shows itself and is then stuck.
            active = true
        } else {
            // Between two targets there is a moment over neither. A drag given up over nothing
            // ends without a word, so after a while of that it is taken to be over.
            idle = Task { [weak self] in
                try? await Task.sleep(for: .seconds(1.5))
                guard !Task.isCancelled else { return }
                self?.finish()
            }
        }
    }

    /// The finger is here, in the board's space.
    func move(to point: CGPoint) {
        guard !settling, let frame = lifted?.frame else { return }
        if spot == nil {
            // Where it was taken hold of: the card stays under the finger as it was, and does
            // not jump to be held by a corner.
            grab = CGSize(width: min(max(point.x - frame.minX, 0), frame.width),
                          height: min(max(point.y - frame.minY, 0), frame.height))
        }
        spot = point
    }

    /// The finger is here, on the screen: from somewhere that is not on the board's page.
    func move(toScreen point: CGPoint) {
        move(to: CGPoint(x: point.x - pageOrigin.x, y: point.y - pageOrigin.y))
    }

    /// The thing in hand is drawn this tall.
    func holds(height: CGFloat) {
        if held != height { held = height }
    }

    /// The lane or the group in hand is over this berth.
    func steer(to place: Berth?) {
        guard !settling, carriesEpic || carriesGroup, berth != place else { return }
        withAnimation(.snappy(duration: 0.22)) { berth = place }
    }

    /// A place a card could land reports the card arriving over it, or leaving.
    func aim(_ place: Slot, _ over: Bool) {
        hover(over)
        guard !settling else { return }
        if over {
            leaving?.cancel()
            guard slot != place else { return }
            withAnimation(.snappy(duration: 0.22)) { slot = place }
        } else if slot == place {
            // Usually on its way to the place next door, which says so a moment later: the
            // room is not closed here only to be opened there.
            leaving?.cancel()
            leaving = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(140))
                guard !Task.isCancelled, let self, self.slot == place, !self.settling else { return }
                withAnimation(.snappy(duration: 0.22)) { self.slot = nil }
            }
        }
    }

    /// The room for the card in hand is here, or is nowhere.
    func room(at frame: CGRect?) {
        if landing != frame { landing = frame }
    }

    /// The card was let go and has a place: it goes to it, and is then the card that is there.
    func settle(at place: Slot) {
        idle?.cancel()
        leaving?.cancel()
        Haptics.shared.play(.drop)
        active = false
        slot = place
        withAnimation(.snappy(duration: 0.22)) { settling = true }
        arriving?.cancel()
        arriving = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(230))
            guard !Task.isCancelled else { return }
            self?.finish(animated: false)
        }
    }

    /// The lane or the group was let go over a berth. Its place there is the server's to give,
    /// which takes a moment: it goes to the room made for it and waits there, until `landed`.
    func settle() {
        idle?.cancel()
        leaving?.cancel()
        Haptics.shared.play(.drop)
        active = false
        withAnimation(.snappy(duration: 0.22)) { settling = true }
    }

    /// The lane or the group that was let go is where it was put: it is the row that is there.
    func landed() {
        finish(animated: false)
    }

    /// Whatever was in hand was let go.
    func ended() {
        Haptics.shared.play(.drop)
        finish()
    }

    private func finish(animated: Bool = true) {
        idle?.cancel()
        leaving?.cancel()
        arriving?.cancel()
        payload = nil
        active = false
        guard lifted != nil || slot != nil else { return }
        let clear = {
            self.lifted = nil
            self.slot = nil
            self.berth = nil
            self.spot = nil
            self.landing = nil
            self.settling = false
        }
        let unfold = {
            self.foldsLanes = false
            self.foldsGroups = false
        }
        unfolding?.cancel()
        if animated {
            withAnimation(.snappy(duration: 0.25)) {
                clear()
                unfold()
            }
        } else {
            // Put down without a flourish, since the thing drawn in hand and the thing in its
            // place are in the same spot and one is swapped for the other. The page opening
            // again is a change to be seen, and so is made a moment after, by itself.
            clear()
            guard foldsLanes || foldsGroups else { return }
            unfolding = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(40))
                guard !Task.isCancelled else { return }
                withAnimation(.snappy(duration: 0.3)) { self?.foldsLanes = false; self?.foldsGroups = false }
            }
        }
    }
}

/// The page's two side edges, while a card is in hand: holding it at one turns
/// to the column on that side, and goes on turning while it is held there.
///
/// The columns used to be pages of a scroll view, which scrolls by itself when
/// a drag nears its edge. They are rows that slide now, with no scroll view to
/// do that, so the edges are made to.
struct ColumnEdgeSensors: View {
    @Binding var selection: TaskState
    var watch: CardDragWatch
    /// A card let go on an edge lands in the column showing.
    var onDrop: ([String], TaskState) -> Bool

    var body: some View {
        HStack(spacing: 0) {
            EdgeSensor(selection: $selection, step: -1, watch: watch, onDrop: onDrop)
            Spacer(minLength: 0)
            EdgeSensor(selection: $selection, step: 1, watch: watch, onDrop: onDrop)
        }
        // Only there while a card is: an edge that took touches the rest of the time would be a
        // strip of the page that can't be scrolled from.
        .allowsHitTesting(watch.carriesCard)
    }
}

private struct EdgeSensor: View {
    @Environment(\.theme) private var theme
    @Binding var selection: TaskState
    var step: Int
    var watch: CardDragWatch
    var onDrop: ([String], TaskState) -> Bool

    @State private var targeted = false
    @State private var turning: Task<Void, Never>?

    private var neighbour: TaskState? {
        let all = TaskState.allCases
        guard let index = all.firstIndex(of: selection), all.indices.contains(index + step) else { return nil }
        return all[index + step]
    }

    var body: some View {
        Color.clear
            .frame(width: 30)
            .frame(maxHeight: .infinity)
            .contentShape(.rect)
            .boardDrop(watch, onScreen: true, targeted: { over in
                targeted = over
                turning?.cancel()
                guard over else { return }
                turning = Task {
                    // A beat before the first turn, so that passing the edge is not holding at it;
                    // longer between the rest, so that each column can be seen before the next.
                    var pause = Duration.milliseconds(550)
                    while !Task.isCancelled {
                        try? await Task.sleep(for: pause)
                        guard !Task.isCancelled, let neighbour else { return }
                        withAnimation(.snappy(duration: 0.32)) { selection = neighbour }
                        pause = .milliseconds(1000)
                    }
                }
            }) { payload in
                onDrop([payload], selection)
            }
            .overlay {
                // Says the edge is somewhere to go, while there is a column that way.
                if watch.carriesCard, neighbour != nil {
                    Image(systemName: step < 0 ? "chevron.compact.left" : "chevron.compact.right")
                        .font(.title.weight(.semibold))
                        .foregroundStyle(targeted ? theme.primary : theme.textSecondary)
                        .frame(width: 26, height: 64)
                        .glassEffect(targeted ? .regular.tint(theme.accentWash) : .regular, in: .capsule)
                        .scaleEffect(targeted ? 1.12 : 1)
                        .opacity(targeted ? 1 : 0.7)
                        .transition(.opacity.combined(with: .scale(scale: 0.8)))
                        .allowsHitTesting(false)
                }
            }
            .animation(.snappy(duration: 0.2), value: targeted)
            .animation(.snappy(duration: 0.2), value: watch.carriesCard)
            .accessibilityHidden(true)
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

/// Scrolls the page to a lane or a group that has just been put down. It was put down while
/// every lane was folded, and the page opens again as soon as it is: the lanes above it are
/// then as tall as their cards, and it is wherever that leaves it.
private struct LaneFollower: ViewModifier {
    var landed: BoardModel.Landed?

    func body(content: Content) -> some View {
        ScrollViewReader { proxy in
            content.task(id: landed?.token) {
                guard let landed else { return }
                // Once the page has opened again: only then is the row where it will be.
                try? await Task.sleep(for: .milliseconds(420))
                guard !Task.isCancelled else { return }
                withAnimation(.smooth(duration: 0.45)) { proxy.scrollTo(landed.id, anchor: .top) }
            }
        }
    }
}

extension View {
    /// On the scroll view: go to the row of a lane or a group that has just been put down.
    func followsLane(_ landed: BoardModel.Landed?) -> some View {
        modifier(LaneFollower(landed: landed))
    }

    /// On the scroll view: go to whichever card inside is marked with `landingTarget`.
    func followsLanding(_ token: UUID?, onArrive: @escaping () -> Void) -> some View {
        modifier(LandingFollower(token: token, onArrive: onArrive))
    }

    /// On a card: this is the one the page should go to.
    func landingTarget(_ isTarget: Bool) -> some View {
        modifier(LandingTarget(isTarget: isTarget))
    }
}

private struct LandingTarget: ViewModifier {
    /// Not inside a column that scrolls by itself: there the column is what the page goes to.
    @Environment(\.landsInPage) private var landsInPage
    var isTarget: Bool

    func body(content: Content) -> some View {
        content.background {
            if isTarget, landsInPage {
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


/// Which berth a lane or a group in hand is over, from where the page's rows stand.
nonisolated enum LaneBerths {
    /// A row of the page: a lane, by its epic, or a group's title, by its group; and the group it is of.
    struct Row: Equatable, Sendable {
        var id: UUID
        var group: UUID?
    }

    /// The berth at a height on the page: before the first row whose middle is below it, or
    /// after the last. `rows` are the page's in order, without the one in hand; a row that is
    /// not built has nowhere it stands, and is passed over.
    ///
    /// The end of a group and the place after the group are between the same two rows. Which
    /// is meant is told by the group's last row: level with it is inside the group, and past
    /// it is outside. A group in hand goes among the groups and into none (`joins` false).
    ///
    /// By the rows' middles as they stand, room and all, for the reason a card's place is
    /// (`Zone.place`): the row the room is made above is moved down by it, and the answer
    /// with the room open is the answer that opened it.
    static func berth(at y: CGFloat, rows: [Row], frames: [UUID: CGRect], joins: Bool = true) -> CardDragWatch.Berth {
        var above: (row: Row, frame: CGRect)?
        func ends(before next: Row?) -> UUID? {
            guard joins, let above, let group = above.row.group, group != next?.group, y < above.frame.maxY else { return nil }
            return group
        }
        for row in rows {
            guard let frame = frames[row.id] else { continue }
            if y < frame.midY {
                if let group = ends(before: row) { return .inside(group) }
                return .before(row.id)
            }
            above = (row, frame)
        }
        if let group = ends(before: nil) { return .inside(group) }
        return .last
    }
}
