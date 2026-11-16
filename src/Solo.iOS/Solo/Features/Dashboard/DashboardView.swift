import SwiftUI

/// The landing page: one person's work across every project, laid out as a
/// board. The same cards and the same columns, moved with the same drag — a card is picked up
/// as itself and carried, as on a board, though here it is only ever put in another column
/// and no room is made for it among the cards: where it lands there is its board's to say. And
/// nothing is created here. A task belongs to a board, and this page only
/// shows where each one came from and lets the person move it along.
struct DashboardView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.horizontalSizeClass) private var sizeClass

    @State private var column: TaskState = .unstarted
    @State private var watch = CardDragWatch()
    /// A card that has just changed column, for a phone to follow there. It matters most here:
    /// this page keeps no order of its own, so a card that changes column lands wherever its
    /// board's rank puts it — usually far from where it was let go.
    @State private var landed: Landing?

    private struct Landing: Equatable {
        var taskId: UUID
        var status: TaskState
        var token = UUID()
    }
    @State private var finishedExpanded = false
    /// Whether the Finished column, when it overflows and so starts put away, has been opened.
    @State private var finishedOpen = false
    /// How tall each active column stands, which the Finished column is held to.
    @State private var heights = LaneHeights()
    /// Columns whose cards are all built, not only the ones in view. A column is built as it
    /// is scrolled into, which is what lets a person with hundreds of tasks open the app without
    /// waiting for every card. Following a card to where it landed needs the card to exist,
    /// though, so a column something has just landed in is built whole from then on.
    @State private var whole: Set<TaskState> = []
    /// Whether a phone's column selector has been scrolled up to the header and is held there.
    /// Kept where only the selector's two copies are listening: as this page's own state it
    /// would have every card looked over again each time it changed.
    @State private var selector = SelectorPlace()
    /// The page's scroll view, once it has been found.
    @State private var pageScroll = FoundScrollView()
    /// How tall whatever stands above the page's selector is.
    @State private var aboveSelector: CGFloat = 0
    @State private var openTask: UUID?
    @State private var failed: String?

    private struct Card: Identifiable {
        var task: TaskItem
        var board: Board
        var project: ProjectSummary
        var origin: TaskOrigin
        var id: UUID { task.id }
        var serial: String { Aliases.serial(project.alias, board.alias, task.number) }
    }

    var body: some View {
        let store = session.assigned
        Group {
            if let work = store.work {
                let cards = Self.cards(from: work)
                if sizeClass == .regular {
                    // Neither the bars around the page nor the row of columns asks how tall
                    // every card is: see `FillLayout` and `ZoneRow`.
                    FillLayout {
                        ScrollView {
                            VStack(spacing: 16) {
                                DashboardExtensions(openTask: $openTask)
                                ZoneRow(spacing: 10) {
                                    ForEach(TaskState.allCases) { status in
                                        VStack(spacing: 8) {
                                            HStack(spacing: 6) {
                                                Image(systemName: status.symbol)
                                                Text(status.title)
                                                Text("\(cards.count { $0.task.status == status })")
                                                    .monospacedDigit().foregroundStyle(theme.textSecondary)
                                            }
                                            .font(.footnote.weight(.semibold))
                                            .frame(maxWidth: .infinity)
                                            .padding(.vertical, 9)
                                            .glassEffect(.regular, in: .capsule)
                                            zone(cards, status, work)
                                        }
                                    }
                                }
                                if cards.isEmpty { empty }
                            }
                            .padding(16)
                            .carryingPage(watch) { carried($0, cards, work) }
                        }
                    }
                } else {
                    FillLayout {
                        ScrollView {
                            LazyVStack(spacing: 12, pinnedViews: .sectionHeaders) {
                                // Once, above the columns: it is the same roadmap whichever one is showing.
                                // In a stack of its own so that there is always a row here, whose height
                                // is how far down the page the selector starts.
                                VStack(spacing: 0) {
                                    DashboardExtensions(openTask: $openTask)
                                }
                                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { aboveSelector = $0 }
                                Section {
                                    // Here a column is only as tall as its own cards: there are no rows to keep in line.
                                    ColumnPages(height: .showing) {
                                        ForEach(TaskState.allCases) { status in
                                            zone(cards, status, work)
                                                .accessibilityHidden(status != column)
                                        }
                                    }
                                    // The room under the selector is its header's, which is more than
                                    // this page wants between the selector and the cards.
                                    .padding(.top, 2 - ColumnSwitcher.footing)
                                    if cards.isEmpty { empty }
                                } header: {
                                    // Held at the top by the stack. Held there, it is the header's own
                                    // copy that is seen, and this one keeps its place under it: see
                                    // `SelectorInHeader`.
                                    SelectorInPage(place: selector) {
                                        switcher(cards).padding(.bottom, ColumnSwitcher.footing)
                                    }
                                }
                            }
                            .padding(.horizontal, 14)
                            .padding(.bottom, 24)
                            .carryingPage(watch) { carried($0, cards, work) }
                            // Says which scroll view this is, for the header's effect to be asked of.
                            .background(ScrollViewFinder(found: pageScroll))
                        }
                        .columnPaging($column)
                        .followsLanding(landed?.token) {
                            if let landed { session.assigned.pulse(landed.taskId) }
                            landed = nil
                        }
                        // Whether the selector has been scrolled up to the header and is held there: it
                        // starts one row's gap under what is above it. A flag and not a position, so
                        // that something is drawn again when it gets there and not on every frame;
                        // and with a little give between getting there and leaving, so that a finger
                        // resting on the very point does not have it going back and forth.
                        .onScrollGeometryChange(for: Int.self) { [aboveSelector] geometry in
                            let left = aboveSelector + 12 - (geometry.contentOffset.y + geometry.contentInsets.top)
                            return left <= 0.5 ? 1 : left > 3 ? 0 : -1
                        } action: { _, state in
                            if state >= 0 { selector.held = state == 1 }
                        }
                    }
                    // The selector starts under whatever extensions put above the columns, so it cannot
                    // live in a bar as a board's does: a bar that took its room only once the selector
                    // got there moved the whole page as it did. Held at the top, it is shown from here
                    // instead, over the page, and the system is asked to carry its header on behind it
                    // — the same blur and the same edge as a board's, and nothing in the page moves.
                    .overlay(alignment: .top) {
                        SelectorInHeader(place: selector, scroll: pageScroll) {
                            switcher(cards)
                                .padding(.horizontal, 14)
                                .padding(.bottom, ColumnSwitcher.footing)
                        }
                    }
                    // A card held at either side is carried to the column that way.
                    .overlay {
                        ColumnEdgeSensors(selection: $column, watch: watch) { payloads, status in
                            drop(payloads, status, cards)
                        }
                    }
                    .environment(watch)
                    .onChange(of: landed) { _, landing in
                        guard let landing else { return }
                        withAnimation(.snappy(duration: 0.32)) { column = landing.status }
                    }
                }
            } else if let failed {
                ContentUnavailableView("Couldn't load your tasks", systemImage: "exclamationmark.triangle",
                                       description: Text(failed))
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .task {
            // What a card carried over this page is, from the serial number it goes by.
            watch.resolve = { [session, watch] serial in
                guard let work = session.assigned.work,
                      let card = Self.cards(from: work).first(where: { $0.serial == serial }), card.project.canWrite,
                      let frame = watch.frame(of: card.task.id, in: card.task.status) else { return nil }
                return .card(card.task.id, frame: frame)
            }
            // What extensions with no native half add to cards, for every card here at once.
            session.kit.mount(BoardKitMount.assigned, .assignedCards)
            do {
                try await store.load()
            } catch {
                failed = (error as? LocalizedError)?.errorDescription ?? "Try again."
            }
        }
        .task(id: store.work?.tasks) {
            // The cards changed: once they have settled, the contributions are handed them afresh.
            try? await Task.sleep(for: .milliseconds(700))
            if !Task.isCancelled { session.kit.refresh(BoardKitMount.assigned) }
        }
        .onDisappear { session.kit.unmount(BoardKitMount.assigned) }
        .taskDetail($openTask)
        // Outside the detail panel: a title set within an inspector's content never reaches the bar.
        .navigationTitle("Your Tasks")
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) { ConnectionIndicator() }
        }
    }

    private var empty: some View {
        ContentUnavailableView("Nothing is assigned to you.", systemImage: "person.crop.rectangle.stack")
            .padding(.top, 24)
    }

    private static func cards(from work: AssignedTasks) -> [Card] {
        let boards = Dictionary(uniqueKeysWithValues: work.boards.map { ($0.id, $0) })
        let projects = Dictionary(uniqueKeysWithValues: work.projects.map { ($0.id, $0) })
        let epics = Dictionary(uniqueKeysWithValues: (work.epics ?? []).map { ($0.id, $0) })
        let groups = Dictionary(uniqueKeysWithValues: (work.epicGroups ?? []).map { ($0.id, $0) })
        return work.tasks.compactMap { task -> Card? in
            guard let board = boards[task.boardId], let project = projects[board.projectId] else { return nil }
            let epic = task.epicId.flatMap { epics[$0] }
            let group = epic?.groupId.flatMap { groups[$0] }
            let epicLine = epic.map { epic in group.map { "\($0.name) • \(epic.name)" } ?? epic.name }
            return Card(task: task, board: board, project: project,
                        origin: TaskOrigin(projectName: project.name, boardName: board.name,
                                           route: .board(project: project.alias, board: board.alias), epicLine: epicLine))
        }
        .sorted {
            ($0.project.name.lowercased(), $0.board.name.lowercased(), $0.task.rank)
                < ($1.project.name.lowercased(), $1.board.name.lowercased(), $1.task.rank)
        }
    }

    private func switcher(_ cards: [Card]) -> some View {
        ColumnSwitcher(selection: $column, count: { status in cards.count { $0.task.status == status } }) { payloads, status in
            drop(payloads, status, cards)
        }
    }

    @ViewBuilder
    private func zone(_ all: [Card], _ status: TaskState, _ work: AssignedTasks) -> some View {
        let inColumn = all.filter { $0.task.status == status }
        let folded = status == .finished && !finishedExpanded
        let shown = folded ? Array(inColumn.prefix(BoardDrag.finishedVisibleLimit)) : inColumn
        // What is finished is held to the height of what is not, as on a board: see `FinishedFold`.
        let fold = status == .finished
            ? DashboardFold(count: inColumn.count, showing: shown.count,
                            beside: Dictionary(uniqueKeysWithValues: TaskState.allCases.filter { $0 != .finished }.map { other in
                                (other, all.count { $0.task.status == other })
                            }),
                            open: finishedOpen, landing: landed?.status == .finished ? landed?.taskId : nil,
                            onToggle: { finishedOpen.toggle() })
            : nil
        DashboardZone(status: status, lazy: !whole.contains(status), fold: fold, heights: heights, watch: watch,
                      onDrop: { drop($0, status, all) }) {
            ForEach(shown) { card in
                Button { openTask = card.task.id } label: { cardView(card, work) }
                    .buttonStyle(.plain)
                    // Where it stands: where it is picked up from, and how wide it is in hand.
                    .standing { watch.stands(card.task.id, in: status, at: $0) }
                    .onDisappear { watch.stands(card.task.id, in: status, at: nil) }
                    .landingTarget(landed?.taskId == card.task.id)
                    .contextMenu {
                        Button("Open", systemImage: "arrow.up.forward.app") { openTask = card.task.id }
                        Button("Go to Board", systemImage: "rectangle.split.3x1") {
                            session.navigator.open(card.origin.route)
                        }
                        if card.project.canWrite {
                            Menu("Move to", systemImage: "arrow.left.arrow.right") {
                                ForEach(TaskState.allCases.filter { $0 != card.task.status }) { status in
                                    Button(status.title, systemImage: status.symbol) { setStatus(card, status) }
                                }
                            }
                        }
                    }
                    .modifier(CardDrag(enabled: card.project.canWrite, payload: card.serial, drag: watch, id: card.task.id))
            }
            if shown.count < inColumn.count {
                ShowAllButton(total: inColumn.count) { withAnimation { finishedExpanded = true } }
            }
        }
    }

    private func cardView(_ card: Card, _ work: AssignedTasks) -> TaskCardView {
        TaskCardView(
            task: card.task, projectId: card.project.id,
            label: card.task.labelId.flatMap { id in work.labels.first { $0.id == id } },
            assignees: work.members.filter { member in member.userId.map(card.task.assigneeIds.contains) ?? false },
            pulse: session.assigned.pulsing.contains(card.task.id),
            origin: card.origin, canWrite: card.project.canWrite, kitMount: BoardKitMount.assigned)
    }

    /// The card in hand, drawn as the card it is.
    @ViewBuilder
    private func carried(_ lift: CardDragWatch.Lift, _ cards: [Card], _ work: AssignedTasks) -> some View {
        if case .card(let id, _) = lift, let card = cards.first(where: { $0.id == id }) {
            cardView(card, work)
        }
    }

    private func drop(_ payloads: [String], _ status: TaskState, _ cards: [Card]) -> Bool {
        guard let payload = payloads.first, let card = cards.first(where: { $0.serial == payload }),
              card.project.canWrite, card.task.status != status else { return false }
        setStatus(card, status)
        return true
    }

    /// Only the column changes here: a card's place among its neighbours is
    /// its board's business.
    private func setStatus(_ card: Card, _ status: TaskState) {
        var moved = card.task
        moved.status = status
        withAnimation(.snappy(duration: 0.25)) { session.assigned.apply(moved) }
        // A card that lands past the fold of a long Finished column would be out of sight.
        if status == .finished,
           (session.assigned.work?.tasks.count { $0.status == .finished } ?? 0) > BoardDrag.finishedVisibleLimit {
            finishedExpanded = true
        }
        // On a phone the page goes to the card, which has to be there to say where it is.
        if sizeClass != .regular { whole.insert(status) }
        landed = Landing(taskId: card.task.id, status: status)
        Task {
            if let saved = await session.save({ try await session.api.setTaskStatus(card.task.id, status) },
                                              refresh: session.assigned.refresh) {
                session.assigned.apply(saved)
            } else {
                await session.assigned.refresh()
            }
        }
    }
}

/// Whether a phone's column selector is in the page or held at the top of it.
@Observable private final class SelectorPlace {
    var held = false
}

/// The page's own copy of the selector: seen until it is held at the top, and from then on only
/// keeping its place.
private struct SelectorInPage<Content: View>: View {
    var place: SelectorPlace
    @ViewBuilder var content: Content

    var body: some View {
        content
            .opacity(place.held ? 0 : 1)
            .allowsHitTesting(!place.held)
            .accessibilityHidden(place.held)
    }
}

/// The header's copy of the selector, laid over the top of the page exactly where the page
/// holds its own, and seen only while it does. Behind it is a band that asks the system to carry
/// the page's header on down behind it (`ScrollEdgeBand`).
///
/// Two copies, because the header's effect blurs what is in the page, and the page's copy is in
/// the page. Both are there from the start: glass that has just been made fades in, and the
/// selector would blink as one took over from the other.
private struct SelectorInHeader<Content: View>: View {
    var place: SelectorPlace
    var scroll: FoundScrollView
    @ViewBuilder var content: Content

    var body: some View {
        content
            .background { ScrollEdgeBand(scroll: scroll, on: place.held) }
            .opacity(place.held ? 1 : 0)
            .allowsHitTesting(place.held)
            .accessibilityHidden(!place.held)
    }
}

/// A scroll view that a view inside it has found and said.
@Observable private final class FoundScrollView {
    @ObservationIgnored weak var view: UIScrollView?
    /// Counts up each time `view` is set: what a reader depends on, since `view` itself is not watched.
    private(set) var changes = 0

    func found(_ scroll: UIScrollView?) {
        guard scroll !== view else { return }
        view = scroll
        changes += 1
    }
}

/// In a scroll view's content: finds the scroll view it is in.
private struct ScrollViewFinder: UIViewRepresentable {
    var found: FoundScrollView

    func makeUIView(context: Context) -> Probe {
        let probe = Probe()
        probe.found = found
        probe.isUserInteractionEnabled = false
        return probe
    }

    func updateUIView(_ probe: Probe, context: Context) {
        probe.found = found
    }

    final class Probe: UIView {
        var found: FoundScrollView?

        override func didMoveToWindow() {
            super.didMoveToWindow()
            guard window != nil else { return }
            var view = superview
            while let current = view {
                if let scroll = current as? UIScrollView {
                    found?.found(scroll)
                    return
                }
                view = current.superview
            }
        }
    }
}

/// A band over the top of a scroll view that the system carries the scroll view's header on
/// behind, as it does behind a bar: the page out of focus under it, ending as the header ends.
/// It is the system's own effect, asked for, and takes no room from the page.
///
/// The band grows down from the header's edge when it is wanted, and the effect with it
/// (`BandReach`). That is the one way found of having the effect arrive at a pace that is not
/// the system's, which is nearly at once. The system follows the size of the view that asks,
/// as that size stands — not an animation of it, which it skips to the end of — and only while
/// what is inside fills that view: a label growing inside a band of fixed size got no effect
/// until it was whole. So it is the band itself that is walked from nothing to its full height.
private struct ScrollEdgeBand: UIViewRepresentable {
    var scroll: FoundScrollView
    var on: Bool

    func makeUIView(context: Context) -> UIView {
        let room = UIView()
        room.backgroundColor = .clear
        room.isUserInteractionEnabled = false

        // The view that asks. As wide as the room and as tall as it is told.
        let band = UIView()
        band.backgroundColor = .clear
        band.translatesAutoresizingMaskIntoConstraints = false
        room.addSubview(band)
        let height = band.heightAnchor.constraint(equalToConstant: 0)
        NSLayoutConstraint.activate([
            band.topAnchor.constraint(equalTo: room.topAnchor),
            band.leadingAnchor.constraint(equalTo: room.leadingAnchor),
            band.trailingAnchor.constraint(equalTo: room.trailingAnchor),
            height,
        ])

        // The system shapes the effect from the labels, images, glass and controls it finds in
        // the view that asks, and from nothing else: an empty view gets no effect, and neither
        // does one holding only what SwiftUI draws. So a label that shows nothing fills the band.
        let stand = UILabel()
        stand.text = "\u{2060}"
        stand.textColor = .clear
        stand.isAccessibilityElement = false
        stand.translatesAutoresizingMaskIntoConstraints = false
        band.addSubview(stand)
        NSLayoutConstraint.activate([
            stand.topAnchor.constraint(equalTo: band.topAnchor),
            stand.bottomAnchor.constraint(equalTo: band.bottomAnchor),
            stand.leadingAnchor.constraint(equalTo: band.leadingAnchor),
            stand.trailingAnchor.constraint(equalTo: band.trailingAnchor),
        ])

        let interaction = UIScrollEdgeElementContainerInteraction()
        interaction.edge = .top
        band.addInteraction(interaction)
        context.coordinator.interaction = interaction
        context.coordinator.reach = BandReach(height: height, in: room)
        return room
    }

    func updateUIView(_ room: UIView, context: Context) {
        let coordinator = context.coordinator
        // Read so that this is done again when the scroll view is found.
        _ = scroll.changes
        if on { coordinator.interaction?.scrollView = scroll.view }
        guard coordinator.on != on else { return }
        coordinator.on = on
        if on {
            // Coming, the header reaches down behind the selector.
            coordinator.reach?.go(to: 1, with: ScrollEdgeBand.arrival)
        } else {
            // Going, the effect is let go whole and the system takes it away, which is quick and
            // reads right; the band is put back at nothing for the next time.
            coordinator.interaction?.scrollView = nil
            coordinator.reach?.reset()
        }
    }

    /// How the header reaches down behind the selector: the system's own smooth spring, which
    /// sets off quickly and settles without a bounce, as its sheets and bars do.
    static var arrival: Spring { .smooth(duration: 0.3) }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator {
        var interaction: UIScrollEdgeElementContainerInteraction?
        var reach: BandReach?
        var on = false
    }
}

/// How far down its room the band reaches, walked a step a frame.
private final class BandReach: NSObject {
    private let height: NSLayoutConstraint
    private weak var room: UIView?
    private var link: CADisplayLink?
    /// Nought, no reach, to one, the room's whole height.
    private var now: CGFloat = 0
    private var from: CGFloat = 0
    private var to: CGFloat = 0
    private var began: CFTimeInterval = 0
    private var spring = Spring.smooth

    init(height: NSLayoutConstraint, in room: UIView) {
        self.height = height
        self.room = room
    }

    func go(to target: CGFloat, with spring: Spring) {
        from = now
        to = target
        self.spring = spring
        began = CACurrentMediaTime()
        guard link == nil else { return }
        let link = CADisplayLink(target: self, selector: #selector(step))
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    /// Back to nothing, at once.
    func reset() {
        link?.invalidate()
        link = nil
        now = 0
        to = 0
        height.constant = 0
    }

    @objc private func step(_ link: CADisplayLink) {
        let passed = CACurrentMediaTime() - began
        // The spring is the system's; only the walking of it is done here, since the system
        // skips an animation of this view and follows its size as it stands.
        let settled = passed >= spring.settlingDuration
        now = settled ? to : from + spring.value(target: to - from, initialVelocity: 0, time: passed)
        height.constant = max((room?.bounds.height ?? 0) * now, 0)
        if settled {
            link.invalidate()
            self.link = nil
        }
    }
}

/// What the Finished column is told, to hold itself to the height of the others.
private struct DashboardFold {
    var count: Int
    var showing: Int
    /// How many cards each active column holds.
    var beside: [TaskState: Int]
    var open: Bool
    var landing: UUID?
    var onToggle: () -> Void
}

private struct DashboardZone<Content: View>: View {
    @Environment(\.theme) private var theme
    var status: TaskState
    /// Build cards as they come into view, and not all of them at once.
    var lazy = true
    /// What a Finished column needs to hold itself to the height of the others.
    var fold: DashboardFold?
    /// How tall the active columns stand: they say, and the Finished one listens.
    var heights: LaneHeights
    var watch: CardDragWatch
    var onDrop: ([String]) -> Bool
    @ViewBuilder var content: Content
    @State private var targeted = false

    var body: some View {
        Group {
            if let fold {
                FinishedFold(count: fold.count, showing: fold.showing, beside: heights.tallest,
                             guess: FinishedColumn.guess(from: heights.active.map { ($1, fold.beside[$0] ?? 0) },
                                                         extra: 24, spacing: 8),
                             open: fold.open,
                             onToggle: fold.onToggle, landing: fold.landing, spacing: 8, inset: 0) {
                    content
                    Color.clear.frame(height: 24)
                }
            } else {
                Group {
                    if lazy {
                        LazyVStack(spacing: 8) {
                            content
                            Color.clear.frame(height: 24)
                        }
                    } else {
                        VStack(spacing: 8) {
                            content
                            Color.clear.frame(height: 24)
                        }
                    }
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { heights.set(status, $0) }
            }
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .padding(6)
        .glassEffect(targeted ? .regular.tint(theme.accentWash) : .regular, in: theme.cardShape)
        // The column is where a card is let go, and never the cards in it: see `BoardDrop`.
        .boardDrop(watch, targeted: { targeted = $0 }) { onDrop([$0]) }
        .animation(.easeOut(duration: 0.15), value: targeted)
    }
}
