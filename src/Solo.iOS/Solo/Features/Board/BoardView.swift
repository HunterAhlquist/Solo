import SwiftUI
import UniformTypeIdentifiers

/// The Kanban board: three fixed columns, with epics as lanes spanning them.
///
/// On an iPad the three columns sit side by side, as on the web. On an iPhone
/// one column fills the screen and the others are a swipe away — the same
/// rows, slid sideways, so an epic stays where it is on the page whichever
/// column is showing. The column switcher doubles as a drop target so a card
/// can still be carried across, and the page follows the card there.
struct BoardView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.horizontalSizeClass) private var sizeClass

    var projectAlias: String
    var boardAlias: String
    var taskNumber: Int?

    @State private var model: BoardModel?

    var body: some View {
        Group {
            if let model, model.board != nil {
                BoardContent(model: model)
            } else if let error = model?.store.loadError {
                ContentUnavailableView("Couldn't open that board", systemImage: "exclamationmark.triangle",
                                       description: Text(error))
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .task {
            let model = self.model ?? BoardModel(session: session)
            self.model = model
            await model.store.load(projectAlias: projectAlias, boardAlias: boardAlias)
            if let board = model.board {
                session.projects.recordVisit(board.projectId)
                if let taskNumber, model.selection == nil {
                    model.openTask(number: taskNumber)
                }
            }
        }
        .onDisappear {
            Task { await model?.store.detach() }
        }
    }
}

private struct BoardContent: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Bindable var model: BoardModel

    var body: some View {
        let board = model.board!
        // Read here, not only inside the inspector's closures: that is what
        // makes this body run again when the selection changes.
        let selection = model.selection
        Group {
            if sizeClass == .regular {
                BoardGrid(model: model)
            } else {
                BoardPager(model: model)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        // What extensions with no native half add to cards: the board is mounted once, for all of them.
        .task(id: board.id) {
            session.kit.mount(BoardKitMount.id(for: board.id), .taskCard, project: board.projectId, board: board.id)
            session.kit.mount(BoardKitMount.columns(for: board.id), .boardColumn, project: board.projectId, board: board.id)
        }
        .task(id: board.tasks) {
            // The cards changed: once they have settled, the contributions are handed them afresh.
            try? await Task.sleep(for: .milliseconds(700))
            if !Task.isCancelled {
                session.kit.refresh(BoardKitMount.id(for: board.id))
                session.kit.refresh(BoardKitMount.columns(for: board.id))
            }
        }
        .onDisappear {
            session.kit.unmount(BoardKitMount.id(for: board.id))
            session.kit.unmount(BoardKitMount.columns(for: board.id))
        }
        .navigationTitle(board.name)
        .navigationBarTitleDisplayMode(.inline)
        .titleIsInBar()
        #if os(macOS)
        .focusedSceneValue(\.newTask, model.canWrite ? { model.promptNewTask(epicId: nil) } : nil)
        #endif
        .toolbar {
            ToolbarItem(placement: .principal) {
                HStack(spacing: 8) {
                    EntityIcon(imageId: board.imageId, icon: board.icon, size: 20, fallback: "rectangle.split.3x1.fill")
                        .foregroundStyle(theme.primary)
                    Text(board.name).font(.headline).lineLimit(1)
                    if !board.isOpen {
                        Text("Closed")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(theme.backgroundGray, in: .capsule)
                    }
                }
            }
            ToolbarItemGroup(placement: .topBarTrailing) {
                ConnectionIndicator()
                PresenceCluster(entries: model.store.presence(on: EntityTypes.board, board.id), max: 4)
            }
            if model.canWrite {
                ToolbarSpacer(.fixed, placement: .topBarTrailing)
                ToolbarItem(placement: .topBarTrailing) {
                    Menu("Add", systemImage: "plus") {
                        Button("New Task", systemImage: "checklist") { model.promptNewTask(epicId: nil) }
                        Button("New Epic", systemImage: "rectangle.stack") { model.promptNewEpic() }
                        Button("New Group", systemImage: "square.stack.3d.up") { model.promptNewGroup() }
                    } primaryAction: {
                        model.promptNewTask(epicId: nil)
                    }
                }
            }
        }
        .detailPanel(isPresented: Binding(get: { selection != nil }, set: { if !$0 { model.closeDetail() } }),
                     id: selection) {
            if let selection {
                DetailView(store: model.store, selection: selection) { model.closeDetail() }
                    .id(selection)
            }
        }
        .textPrompt($model.prompt)
        .confirmation($model.confirmation)
    }
}

// MARK: - iPad: the full grid

private struct BoardGrid: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel

    var body: some View {
        FillLayout {
            grid
        }
        .catchesDrops(model.drag)
        .environment(model.drag)
    }

    private var grid: some View {
        ScrollView {
            LazyVStack(spacing: 14, pinnedViews: .sectionHeaders) {
                Section {
                    if !model.store.isOpen {
                        InfoBanner(text: "You cannot make changes to a board that is closed, reopen the board to enable editing.")
                    }
                    ForEach(model.rows) { row in
                        switch row {
                        case .group(let group, let piece):
                            GroupRow(model: model, group: group, piece: piece, spacing: 14)
                        case .lane(let epic, let piece):
                            LaneRow(model: model, epic: epic, piece: piece, spacing: 14) {
                                GridLane(model: model, epic: epic)
                            }
                        }
                    }
                    LaneRow(model: model, epic: nil, piece: nil, spacing: 14) {
                        GridLane(model: model, epic: nil)
                    }
                    if model.isEmpty {
                        EmptyBoard(model: model)
                    }
                } header: {
                    HStack(spacing: 8) {
                        ForEach(TaskState.allCases) { status in
                            HStack(spacing: 6) {
                                Image(systemName: status.symbol)
                                Text(status.title)
                                Text("\(model.count(status))").monospacedDigit().foregroundStyle(theme.textSecondary)
                                // What extensions add beside a column's count. Here only: the phone
                                // has no such header, as on the web.
                                if let board = model.board {
                                    KitItemContributions(mount: BoardKitMount.columns(for: board.id), item: status.rawValue)
                                }
                            }
                            .font(.footnote.weight(.semibold))
                            .frame(maxWidth: .infinity)
                        }
                    }
                    .padding(.vertical, 10)
                    .padding(.horizontal, 16)
                    .glassEffect(.regular, in: .capsule)
                    .padding(.bottom, 2)
                }
            }
            .padding(.horizontal, 16)
            .padding(.bottom, 24)
            .boardPage(model)
        }
        .followsLane(model.laneLanded)
    }
}

/// One lane of the grid: an epic's header over its three zones. `epic` nil is
/// the ad-hoc band — tasks that belong to no epic — which always comes last.
private struct GridLane: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    /// How tall each active zone stands, which its Finished zone is held to.
    @State private var heights = LaneHeights()

    var body: some View {
        VStack(spacing: 8) {
            LaneHeader(model: model, epic: epic, count: model.taskCount(epicId: epic?.id))
            if model.isFolded(epic) {
                FoldedLaneNote(count: model.taskCount(epicId: epic?.id))
            } else {
                // Every zone is as tall as the tallest, so a card can be let go
                // anywhere under a short column and still land in it.
                ZoneRow(spacing: 8) {
                    ForEach(TaskState.allCases) { status in
                        Zone(model: model, epicId: epic?.id, status: status, heights: heights)
                    }
                }
            }
        }
        .padding(10)
        .glassEffect(.regular, in: theme.cardShape)
        // In hand, its header is what is carried and the lane has left its place.
        .leaves(epic != nil && model.drag.epic == epic?.id, closed: epic != nil && model.drag.left == epic?.id)
    }
}

/// A lane's zones side by side: the same width each, and all as tall as the tallest.
///
/// A stack would do it — a row of columns told to take their ideal height — but a stack asks
/// each child how small and how large it could be before deciding, and so does every stack
/// inside it, so a lane of twenty cards was measured many times over to be laid out once.
/// The answer here is known without asking: a third of the width each.
nonisolated struct ZoneRow: Layout {
    var spacing: CGFloat = 8

    private func column(_ width: CGFloat, _ count: Int) -> CGFloat {
        count > 0 ? max((width - spacing * CGFloat(count - 1)) / CGFloat(count), 0) : 0
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.replacingUnspecifiedDimensions(by: CGSize(width: 900, height: 0)).width
        let each = ProposedViewSize(width: column(width, subviews.count), height: nil)
        // Each column is asked once. The answer is kept by the column itself, so asking for it
        // again when placing costs nothing.
        return CGSize(width: width, height: subviews.map { $0.sizeThatFits(each).height }.max() ?? 0)
    }

    /// Nothing in a row of zones is aligned to from outside it. Said outright, because the
    /// answer a layout gives when it says nothing is found by laying its children out again.
    func explicitAlignment(of guide: HorizontalAlignment, in bounds: CGRect, proposal: ProposedViewSize,
                           subviews: Subviews, cache: inout ()) -> CGFloat? { nil }

    func explicitAlignment(of guide: VerticalAlignment, in bounds: CGRect, proposal: ProposedViewSize,
                           subviews: Subviews, cache: inout ()) -> CGFloat? { nil }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let width = column(bounds.width, subviews.count)
        for (index, subview) in subviews.enumerated() {
            subview.place(at: CGPoint(x: bounds.minX + CGFloat(index) * (width + spacing), y: bounds.minY),
                          anchor: .topLeading, proposal: ProposedViewSize(width: width, height: bounds.height))
        }
    }
}

/// Takes the room it is offered and gives all of it to its content, without asking the
/// content how large it would like to be.
///
/// A scroll view asked that question has to ask its content, and for a lazy stack to answer
/// it has to build rows it would not otherwise have built yet. Bars around a page — a
/// navigation bar, a tab bar, anything in the safe area — ask it of the page every time they
/// are laid out. For a page that is a scroll view filling the screen the answer is always
/// "whatever there is", so this gives it without the cost.
nonisolated struct FillLayout: Layout {
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        proposal.replacingUnspecifiedDimensions(by: CGSize(width: 320, height: 480))
    }

    func explicitAlignment(of guide: HorizontalAlignment, in bounds: CGRect, proposal: ProposedViewSize,
                           subviews: Subviews, cache: inout ()) -> CGFloat? { nil }

    func explicitAlignment(of guide: VerticalAlignment, in bounds: CGRect, proposal: ProposedViewSize,
                           subviews: Subviews, cache: inout ()) -> CGFloat? { nil }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for subview in subviews {
            subview.place(at: bounds.origin, anchor: .topLeading, proposal: ProposedViewSize(bounds.size))
        }
    }
}

/// A drop zone: one status of one lane.
///
/// Its cards are built as they scroll into view, and their glass is one container's. Both were
/// measured on a phone scrolling a board of 580 tasks: a lane arriving whole, every card with a
/// layer of glass to itself, held it to half its frame rate once the phone had warmed up, and
/// neither change alone got it back (README, "A big board is built a lane at a time").
private struct Zone: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epicId: UUID?
    var status: TaskState
    /// Build every card at once. A phone's page goes to a card that has just landed, which has
    /// to exist to be found, so there a zone something has landed in is built whole from then on.
    var whole = false
    /// How tall the lane's active zones stand: an active zone says, and a Finished zone, whose
    /// cards may stand as tall and no taller, listens.
    var heights: LaneHeights
    /// Where each of this zone's cards stands.
    @State private var standing = ZoneStanding()

    var body: some View {
        let _ = Perf.count("zone")
        let drag = model.drag
        // Somewhere in this zone is where the card in hand would land.
        let targeted = drag.slot.map { $0.epicId == epicId && $0.status == status } ?? false
        Group {
            if status == .finished {
                // History does not get to stretch the lane: see `FinishedFold`.
                FinishedFold(count: model.tasks(epicId: epicId, status: status).count,
                             showing: model.cards(epicId: epicId, status: status).count,
                             beside: heights.tallest,
                             guess: FinishedColumn.guess(from: heights.active.map { status, height in
                                 (height, model.cards(epicId: epicId, status: status).count)
                             }, extra: 12, spacing: 6),
                             open: model.isFinishedOpen(epicId),
                             onToggle: { model.toggleFinished(epicId) },
                             landing: landing,
                             // The gap between cards is each card's own, and not the stack's: see `BoardCard`.
                             spacing: 0,
                             sharedGlass: model.cards(epicId: epicId, status: status).count <= Self.sharedGlassLimit) { cards }
            } else {
                Group {
                    // A container as tall as a long column costs more than it saves: Your Tasks, whose
                    // columns are that, scrolled at half the rate with one. So only a zone of a lane's
                    // usual size has one, which is every size this was measured at.
                    if model.cards(epicId: epicId, status: status).count <= Self.sharedGlassLimit {
                        // No spacing: the cards share the container's drawing, and are not to run into each other.
                        GlassEffectContainer(spacing: 0) { stack }
                    } else {
                        stack
                    }
                }
                // The last card's own gap is the room at the foot.
                .padding([.top, .horizontal], 6)
                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { heights.set(status, $0) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .contentShape(.rect)
        .background(targeted ? theme.accentWash : .clear, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay {
            if targeted {
                RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(theme.primary.opacity(0.6))
            }
        }
        // What is left of the zone once its cards have taken what is over them: under the last
        // card, or all of it when it is empty. A card let go here lands at the end.
        .boardDrop(drag, place: { place(at: $0) }) { payload in
            let aimed = drag.slot.flatMap { $0.epicId == epicId && $0.status == status ? $0.index : nil }
            return model.land(payload, epicId: epicId, status: status, index: aimed)
        }
        .animation(.easeOut(duration: 0.15), value: targeted)
    }

    /// The place for a card that the finger is over: before the first card whose middle is
    /// below it, or after the last.
    ///
    /// By the cards' middles as they stand, room and all, which holds still. The card the room
    /// is made above has been moved down by it, so the finger in the room is above its middle
    /// and the place is the same one; carried on down past that middle, the room moves to
    /// under the card, which comes up and away from the finger, and the place is again settled.
    private func place(at point: CGPoint) -> CardDragWatch.Slot {
        let cards = model.cards(epicId: epicId, status: status)
        let carried = model.drag.card
        let index = cards.firstIndex { card in
            guard card.id != carried, let frame = standing.cards[card.id] else { return false }
            return point.y < frame.midY
        }
        return CardDragWatch.Slot(epicId: epicId, status: status, index: index ?? cards.count)
    }

    /// The most cards a zone can hold and still draw their glass as one.
    private static let sharedGlassLimit = 30

    /// The card that has just landed in this zone, when one has.
    private var landing: UUID? {
        guard let landed = model.landed, landed.status == status,
              model.cards(epicId: epicId, status: status).contains(where: { $0.id == landed.taskId }) else { return nil }
        return landed.taskId
    }

    /// No spacing: the gap between cards is each card's own (`BoardCard`).
    @ViewBuilder private var stack: some View {
        if whole {
            VStack(spacing: 0) { cards }
        } else {
            LazyVStack(spacing: 0) { cards }
        }
    }

    @ViewBuilder private var cards: some View {
        let cards = model.cards(epicId: epicId, status: status)
        let hidden = model.hiddenCount(epicId: epicId, status: status)
        ForEach(Array(cards.enumerated()), id: \.element.id) { index, task in
            BoardCard(model: model, task: task, epicId: epicId, status: status, index: index, zone: standing)
        }
        if hidden > 0 {
            ShowAllButton(total: hidden + cards.count) { withAnimation { model.expandFinished(epicId) } }
        }
        // The room for a card that would land after the last.
        SlotGap(drag: model.drag, height: model.drag.gap(epicId: epicId, status: status, index: cards.count))
        if cards.isEmpty {
            Color.clear.frame(height: 42)
        }
    }
}

/// The room left where the thing in hand would land: as tall as it is, with the gap that goes
/// between any two of its kind, and marked faintly so that it reads as a place and not as
/// nothing. It says where it is, which is where the thing goes when it is let go.
private struct SlotGap: View {
    @Environment(\.theme) private var theme
    var drag: CardDragWatch
    /// The height of the thing in hand, or nothing where it would not land.
    var height: CGFloat
    var radius: CGFloat = 12
    /// The gap between the room and what it is made beside, which is under it unless said otherwise.
    var spacing: CGFloat = BoardCard.gap
    var above = false

    var body: some View {
        RoundedRectangle(cornerRadius: radius, style: .continuous)
            .fill(theme.primary.opacity(0.10))
            .strokeBorder(theme.primary.opacity(0.45), style: StrokeStyle(lineWidth: 1, dash: [5, 4]))
            .frame(height: height)
            .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(BoardSpace.name)) } action: { frame in
                if height > 0 { drag.room(at: frame) }
            }
            .onChange(of: height > 0) { _, open in
                if !open, !drag.aimed { drag.room(at: nil) }
            }
            .padding(above ? .top : .bottom, height > 0 ? spacing : 0)
            .opacity(height > 0 ? 1 : 0)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
    }
}

/// A card in its zone: tap opens it, and a long press lifts it out of its place, which closes
/// behind it. A card in hand that would land just above this one is left the room by this one
/// moving down. The gap under a card is the card's own, so that it goes when the card does.
private struct BoardCard: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var task: TaskItem
    var epicId: UUID?
    var status: TaskState
    var index: Int
    /// Where the zone keeps where its cards stand, for working out which two a card in hand is between.
    var zone: ZoneStanding

    /// The gap under every card.
    static let gap: CGFloat = 6

    var body: some View {
        let _ = Perf.count("card")
        let store = model.store
        let drag = model.drag
        let column = BoardModel.ZoneKey(epicId: epicId, status: status)
        let card = TaskCardView(
            task: task,
            projectId: store.board?.projectId ?? UUID(),
            label: store.label(task.labelId),
            assignees: store.members(task.assigneeIds),
            presence: store.editors(on: EntityTypes.task, task.id),
            pulse: store.pulsing.contains(task.id),
            canWrite: model.canWrite,
            kitMount: store.board.map { BoardKitMount.id(for: $0.id) })

        VStack(spacing: 0) {
            SlotGap(drag: drag, height: drag.gap(epicId: epicId, status: status, index: index))

            Button { model.open(task) } label: { card }
                .buttonStyle(.plain)
                .standing {
                    zone.cards[task.id] = $0
                    drag.stands(task.id, in: column, at: $0)
                }
                .onDisappear {
                    zone.cards[task.id] = nil
                    drag.stands(task.id, in: column, at: nil)
                }
                .landingTarget(model.landed?.taskId == task.id)
                .contextMenu {
                    Button("Open", systemImage: "arrow.up.forward.app") { model.open(task) }
                    if model.canWrite {
                        Menu("Move to", systemImage: "arrow.left.arrow.right") {
                            ForEach(TaskState.allCases.filter { $0 != task.status }) { status in
                                Button(status.title, systemImage: status.symbol) { model.setStatus(task, status) }
                            }
                        }
                        Divider()
                        Button("Delete", systemImage: "trash", role: .destructive) { model.confirmDelete(task) }
                    }
                }
                .modifier(CardDrag(enabled: model.canWrite, payload: store.serial(task), drag: drag, id: task.id))
                .padding(.bottom, drag.left == task.id ? 0 : Self.gap)
        }
    }
}

struct CardDrag: ViewModifier {
    var enabled: Bool
    var payload: String
    /// What is in hand, and which card this is: it leaves its place while it is the one. Read
    /// here so that only the cards are looked over again when one is picked up, and not the
    /// page that holds them.
    var drag: CardDragWatch
    var id: UUID

    func body(content: Content) -> some View {
        if enabled {
            // A preview of nothing: what the system would show is the card shrunk, and the card
            // in hand is drawn by the page (`CarriedThing`).
            content
                .onDrag { BoardDrag.provider(payload) } preview: { NothingToShow() }
                .leaves(drag.card == id, closed: drag.left == id)
        } else {
            content
        }
    }
}

/// What the system is handed to show of a thing it carries: nothing, and shaped as nothing. The
/// system backs whatever it carries with a pale plate cut to this shape, and at any size that
/// plate shows.
struct NothingToShow: View {
    var body: some View {
        Color.clear.frame(width: 1, height: 1)
            .contentShape(.dragPreview, Rectangle().size(width: 0.01, height: 0.01))
    }
}

/// Where each card of a zone stands, in the board's space. Kept aside from any view's state,
/// since it changes whenever anything above moves and nothing is drawn from it.
private final class ZoneStanding {
    var cards: [UUID: CGRect] = [:]
}

extension View {
    /// Says where this stands, in the board's space, whenever that changes: where it is picked
    /// up from, how large it is drawn in hand, and which two of its kind a thing in hand is between.
    func standing(_ tell: @escaping (CGRect) -> Void) -> some View {
        onGeometryChange(for: CGRect.self) { $0.frame(in: .named(BoardSpace.name)) } action: { frame in
            // Not while it is in hand: it has no height then, and where it stood is what is wanted.
            if frame.height > 1 { tell(frame) }
        }
    }

    /// Something in hand has left its place. It is still there, because the system is carrying
    /// the drag by this view and gives up if the view goes; so it is put far off to one side,
    /// and its place is closed.
    ///
    /// Off to the side, and not only made clear, because the system keeps a picture of what it
    /// lifted and draws it where the view is for as long as it carries it, whatever is said of
    /// the view's opacity inside the drag or outside it. `gone` is that, and is always all at
    /// once: see `CardDragWatch.offered` for why, and for `closed`, which follows a moment
    /// after and is the place closing behind it — and opening again when it is given back,
    /// which is when it comes back into view.
    func leaves(_ gone: Bool, closed: Bool) -> some View {
        offset(x: gone ? 3000 : 0)
            .opacity(gone || closed ? 0 : 1)
            .frame(height: closed ? 0 : nil, alignment: .top)
    }
}

/// The thing in hand, where the finger has it, a little larger than it stood and with a shadow
/// under it, as a thing picked up off a table is. It is drawn in the page's scrolling content,
/// which is the space the finger's place is said in, so it needs nothing worked out to be put
/// there. Let go where room was made for it, it goes to the room and is then what is there.
/// What it looks like is the page's to say: a board's card, lane or group, or a card of Your Tasks.
struct CarriedThing<Thing: View>: View {
    var drag: CardDragWatch
    @ViewBuilder var thing: (CardDragWatch.Lift) -> Thing
    /// Raised: it is drawn first as it stood, and then comes up.
    @State private var raised = false

    var body: some View {
        if let lifted = drag.lifted, let spot = drag.spot {
            let frame = lifted.frame
            let held = CGPoint(x: spot.x - drag.grab.width, y: spot.y - drag.grab.height)
            let at = drag.settling ? (drag.landing?.origin ?? held) : held
            let up = raised && !drag.settling
            thing(lifted)
                // As wide as it stood, until it is let go into a place of another width: a
                // lane put into a group, which is narrower than the page.
                .frame(width: drag.settling ? (drag.landing?.width ?? frame.width) : frame.width)
                .onGeometryChange(for: CGFloat.self) { $0.size.height.rounded() } action: { drag.holds(height: $0) }
                .scaleEffect(up ? 1.05 : 1)
                .shadow(color: .black.opacity(up ? 0.35 : 0), radius: up ? 16 : 0, y: up ? 10 : 0)
                .offset(x: at.x, y: at.y)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
                .onAppear { withAnimation(.snappy(duration: 0.25)) { raised = true } }
                .onDisappear { raised = false }
        }
    }
}

/// What a board's thing in hand looks like: the card itself, or the lane, or the group.
private struct CarriedCard: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var lifted: CardDragWatch.Lift

    var body: some View {
        thing(lifted)
    }

    @ViewBuilder private func thing(_ lifted: CardDragWatch.Lift) -> some View {
        let store = model.store
        switch lifted {
        case .card(let id, _):
            if let task = model.task(id) {
                TaskCardView(
                    task: task,
                    projectId: store.board?.projectId ?? UUID(),
                    label: store.label(task.labelId),
                    assignees: store.members(task.assigneeIds),
                    presence: store.editors(on: EntityTypes.task, task.id),
                    canWrite: model.canWrite,
                    kitMount: store.board.map { BoardKitMount.id(for: $0.id) })
            }
        case .epic(let id, _):
            // A lane in hand is the lane, folded as every lane is while one is carried.
            if let epic = model.board?.epics.first(where: { $0.id == id }) {
                VStack(spacing: 8) {
                    LaneHeader(model: model, epic: epic, count: model.taskCount(epicId: epic.id), carried: true)
                    FoldedLaneNote(count: model.taskCount(epicId: epic.id))
                }
                .padding(10)
                .glassEffect(.regular, in: theme.cardShape)
            }
        case .group(let id, _):
            // And a group is its title in its outline, on glass: the outline alone is drawn
            // on the page, and in hand it would be a line around whatever is under it.
            if let group = model.board?.groups?.first(where: { $0.id == id }) {
                GroupHeader(model: model, group: group, carried: true)
                    .groupPiece(.whole, gap: 0)
                    .glassEffect(.regular, in: RoundedRectangle(cornerRadius: GroupOutline.radius, style: .continuous))
            }
        }
    }
}

/// Somewhere on the board's page that a thing in hand can be let go, or pass over. Each says
/// where the finger is while it is over it, in the board's space, which is what the thing in
/// hand is drawn by; a zone says as well which of its places for a card that is.
///
/// Only things that stay where they are while a card is carried are these: a zone, a header,
/// the page. A card is not, though it is cards that a card is put between. The finger's place
/// is reckoned from the corner of whatever it is over, and a card it is over is at that moment
/// moving out of its way: the two were a frame apart, and the card in hand shook.
private struct BoardDrop: ViewModifier {
    var drag: CardDragWatch
    /// For a zone: the place for a card that the finger, here in the board's space, is over.
    var place: ((CGPoint) -> CardDragWatch.Slot)?
    /// For the page: the berth for a lane or a group that the finger is over.
    var berth: ((CGPoint) -> CardDragWatch.Berth?)?
    /// Somewhere that is not on the board's page, and so says where the finger is on the screen.
    var onScreen = false
    /// For somewhere that is several places in one — the selector's columns: where in it the
    /// finger is, as it moves.
    var located: ((CGPoint) -> Void)?
    var targeted: ((Bool) -> Void)?
    var perform: (String) -> Bool
    /// This view's corner in the board's space, or on the screen: what turns a place in it
    /// into a place there.
    @State private var origin = CGPoint.zero

    func body(content: Content) -> some View {
        content
            .onGeometryChange(for: CGPoint.self) {
                $0.frame(in: onScreen ? .global : .named(BoardSpace.name)).origin
            } action: { origin = $0 }
            .onDrop(of: [.plainText], delegate: Delegate(drag: drag, place: place, berth: berth, onScreen: onScreen,
                                                         origin: origin, located: located, targeted: targeted,
                                                         perform: perform))
    }

    struct Delegate: DropDelegate {
        var drag: CardDragWatch
        var place: ((CGPoint) -> CardDragWatch.Slot)?
        var berth: ((CGPoint) -> CardDragWatch.Berth?)?
        var onScreen: Bool
        var origin: CGPoint
        var located: ((CGPoint) -> Void)?
        var targeted: ((Bool) -> Void)?
        var perform: (String) -> Bool

        func validateDrop(info: DropInfo) -> Bool { info.hasItemsConforming(to: [.plainText]) }

        func dropEntered(info: DropInfo) {
            // Not a thing arriving, if one was let go a moment ago: `CardDragWatch.justLetGo`.
            guard !drag.justLetGo else { return }
            targeted?(true)
            over(info, true)
        }

        func dropUpdated(info: DropInfo) -> DropProposal? {
            if !drag.justLetGo {
                located?(info.location)
                over(info, true)
                // Still in hand, and still here: see `CardDragWatch.alive`. Somewhere off the
                // page — an edge, a column of the selector — is somewhere a thing is let go
                // on purpose, so one that goes quiet there is taken to have been.
                drag.alive(at: info.location, lapse: onScreen ? { letGo() } : nil)
            }
            // A move: the system then draws no badge beside the finger.
            return DropProposal(operation: .move)
        }

        func dropExited(info: DropInfo) {
            drag.quiet()
            targeted?(false)
            over(info, false)
        }

        /// What is in hand is let go here.
        private func letGo() {
            if let payload = drag.payload { _ = perform(payload) }
            // After, and not before: where it was over may be what says where it goes.
            targeted?(false)
            if !drag.settling { drag.ended() }
        }

        private func over(_ info: DropInfo, _ over: Bool) {
            let point = CGPoint(x: origin.x + info.location.x, y: origin.y + info.location.y)
            if over {
                // Whatever has just arrived says what it is, once: see `CardDragWatch.offered`.
                if !drag.active { drag.offered(info.itemProviders(for: [.plainText]).first?.suggestedName) }
                if let place { drag.aim(place(point), true) } else { drag.hover(true) }
                if onScreen { drag.move(toScreen: point) } else { drag.move(to: point) }
                if let berth { drag.steer(to: berth(point)) }
            } else if place != nil, let aimed = drag.slot {
                drag.aim(aimed, false)
            } else {
                drag.hover(false)
            }
        }

        func performDrop(info: DropInfo) -> Bool {
            drag.quiet()
            // Something of this board's: what it is was kept when it was picked up.
            if drag.payload != nil {
                letGo()
                // Taken, whether or not it went anywhere. Refused, the system carries the thing
                // back to where it came from, which there is nothing of to see, and goes on
                // reporting it over whatever is under it on the way: a card let go on the edge
                // of the column it was already in was back in hand, with no finger on it, and
                // the edge went on turning the columns.
                return true
            }
            // Something from elsewhere, which has to be asked what it is.
            targeted?(false)
            drag.ended()
            guard let provider = info.itemProviders(for: [.plainText]).first else { return false }
            _ = provider.loadObject(ofClass: NSString.self) { text, _ in
                guard let text = text as? String else { return }
                Task { @MainActor in _ = perform(text) }
            }
            return true
        }
    }
}

extension View {
    /// Takes any drop that nothing inside took, and refuses it: so that a card let go anywhere
    /// on the page is known to have been let go (`CardDragWatch`).
    /// A place on the board's page to let something go, or to carry it over.
    func boardDrop(_ drag: CardDragWatch, place: ((CGPoint) -> CardDragWatch.Slot)? = nil,
                   berth: ((CGPoint) -> CardDragWatch.Berth?)? = nil, onScreen: Bool = false,
                   located: ((CGPoint) -> Void)? = nil, targeted: ((Bool) -> Void)? = nil,
                   perform: @escaping (String) -> Bool) -> some View {
        modifier(BoardDrop(drag: drag, place: place, berth: berth, onScreen: onScreen, located: located,
                           targeted: targeted, perform: perform))
    }

    /// On a page's scrolling content: makes it the space everything on the page is placed in,
    /// and draws the thing in hand over it all. It is also somewhere to let go, wherever on the
    /// page that is, so that the thing in hand follows the finger between the places meant for
    /// it; what is let go there goes nowhere unless the page says otherwise.
    func carryingPage<Thing: View>(_ drag: CardDragWatch, berth: ((CGPoint) -> CardDragWatch.Berth?)? = nil,
                                   perform: @escaping (String) -> Bool = { _ in false },
                                   @ViewBuilder carried: @escaping (CardDragWatch.Lift) -> Thing) -> some View {
        contentShape(.rect)
            .boardDrop(drag, berth: berth, perform: perform)
            .overlay(alignment: .topLeading) { CarriedThing(drag: drag, thing: carried) }
            .onGeometryChange(for: CGPoint.self) { $0.frame(in: .global).origin } action: { drag.pageOrigin = $0 }
            .coordinateSpace(.named(BoardSpace.name))
    }

    /// A board's page. It is where a lane or a group in hand is let go: the rows it goes
    /// between are moving out of its way, and the page is not (see `BoardDrop`).
    func boardPage(_ model: BoardModel) -> some View {
        carryingPage(model.drag, berth: { model.berth(at: $0) }, perform: { _ in model.landLane() }) {
            CarriedCard(model: model, lifted: $0)
        }
    }

    func catchesDrops(_ drag: CardDragWatch) -> some View {
        dropDestination(for: String.self) { _, _ in
            drag.ended()
            return false
        } isTargeted: {
            drag.hover($0)
        }
    }
}

// MARK: - Lanes and groups

private struct LaneHeader: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    var count: Int
    /// Drawn in hand, where it is a picture of itself and picks nothing up.
    var carried = false

    var body: some View {
        HStack(spacing: 8) {
            if let epic {
                Button {
                    withAnimation(.snappy) { model.toggle(epic) }
                } label: {
                    Image(systemName: "chevron.down")
                        .rotationEffect(.degrees(model.isCollapsed(epic) ? -90 : 0))
                        .font(.footnote.weight(.semibold))
                        .frame(width: 28, height: 28)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(model.isCollapsed(epic) ? "Expand epic" : "Collapse epic")

                Button { model.open(epic) } label: {
                    HStack(spacing: 8) {
                        Text(epic.name)
                            .font(.subheadline.weight(.semibold))
                            .multilineTextAlignment(.leading)
                        Spacer(minLength: 0)
                        PresenceCluster(entries: model.store.presence(on: EntityTypes.epic, epic.id), max: 2)
                        AvatarStack(members: model.store.members(epic.assigneeIds), max: 3, size: 22)
                    }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Open epic \(epic.name)")

                if model.canWrite {
                    Button("Add task", systemImage: "plus") { model.promptNewTask(epicId: epic.id) }
                        .labelStyle(.iconOnly)
                        .frame(width: 30, height: 30)
                    EpicMenu(model: model, epic: epic)
                }
            } else {
                Image(systemName: "tray")
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: 28, height: 28)
                Text("No epic")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(theme.textSecondary)
                Spacer()
                if model.canWrite {
                    Button("Add task", systemImage: "plus") { model.promptNewTask(epicId: nil) }
                        .labelStyle(.iconOnly)
                        .frame(width: 30, height: 30)
                }
            }
        }
        .modifier(EpicDrag(model: model, epic: carried ? nil : epic))
    }
}

/// What a folded lane says in place of its zones.
private struct FoldedLaneNote: View {
    @Environment(\.theme) private var theme
    var count: Int

    var body: some View {
        Text("\(count) task(s) hidden")
            .font(.caption)
            .foregroundStyle(theme.textSecondary)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A lane as a row of the page: the lane, and the room made beside it for a lane in hand —
/// above it, or under it when it is the last of its group and the end of the group is where
/// the lane in hand would go. All inside its piece of the group's outline, since that is
/// where a lane put there will be. `epic` nil is the lane of tasks in no epic, which is last
/// and has above it the place after everything.
private struct LaneRow<Lane: View>: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    var piece: GroupPiece?
    /// The room the page leaves between rows.
    var spacing: CGFloat
    @ViewBuilder var lane: Lane

    var body: some View {
        let drag = model.drag
        VStack(spacing: 0) {
            SlotGap(drag: drag, height: drag.room(for: epic.map { .before($0.id) } ?? .last),
                    radius: theme.cardShape.cornerSize.width, spacing: spacing)
            if let epic {
                lane
                    .standing { drag.rows[epic.id] = $0 }
                    .onDisappear { drag.rows[epic.id] = nil }
                if piece == .bottom, let group = epic.groupId {
                    // Under the lane by the page's usual gap; and where the lane has left, being
                    // the one in hand, the row's own place is already that far under the one before.
                    SlotGap(drag: drag, height: drag.room(for: .inside(group)), radius: theme.cardShape.cornerSize.width,
                            spacing: drag.left == epic.id ? 0 : spacing, above: true)
                }
            } else {
                lane
            }
        }
        .groupPiece(piece, gap: spacing)
    }
}

/// A group's title as a row of the page, with the room made beside it: above it and outside
/// its outline, for what would go before the group; under it and inside, when the group is
/// folded or empty and a lane in hand would join it.
private struct GroupRow: View {
    @Bindable var model: BoardModel
    var group: EpicGroup
    var piece: GroupPiece
    var spacing: CGFloat

    var body: some View {
        let drag = model.drag
        VStack(spacing: 0) {
            SlotGap(drag: drag, height: drag.room(for: .before(group.id)), radius: GroupOutline.radius, spacing: spacing)
            VStack(spacing: 0) {
                GroupHeader(model: model, group: group)
                    .standing { drag.rows[group.id] = $0 }
                    .onDisappear { drag.rows[group.id] = nil }
                if piece == .whole {
                    SlotGap(drag: drag, height: drag.room(for: .inside(group.id)),
                            radius: 20, spacing: spacing, above: true)
                }
            }
            .groupPiece(piece, gap: spacing)
            .leaves(drag.group == group.id, closed: drag.left == group.id)
        }
    }
}

/// A lane is picked up by its header, and a group by its title, to be put in another place
/// among the rows; the menu does the same a step at a time. While one is in hand every lane is
/// folded (`BoardModel.isCollapsed`), and every group too when it is a group, so that it is
/// carried over a short list of names, which part to make room for it as cards do for a card.
///
/// Neither is anywhere that something in hand is let go. A header moves as the room is made
/// above it, and what is let go over one is the page's to place (`boardPage`).
private struct EpicDrag: ViewModifier {
    @Bindable var model: BoardModel
    var epic: Epic?

    func body(content: Content) -> some View {
        if let epic, model.canWrite {
            content.onDrag { BoardDrag.provider(BoardDrag.epic(epic.id)) } preview: { NothingToShow() }
        } else {
            content
        }
    }
}

private struct GroupDrag: ViewModifier {
    @Bindable var model: BoardModel
    var group: EpicGroup
    var enabled = true

    func body(content: Content) -> some View {
        if enabled, model.canWrite {
            content
                .contentShape(.rect)
                .onDrag { BoardDrag.provider(BoardDrag.group(group.id)) } preview: { NothingToShow() }
        } else {
            content
        }
    }
}

private struct EpicMenu: View {
    @Bindable var model: BoardModel
    var epic: Epic

    var body: some View {
        Menu("Epic actions", systemImage: "ellipsis") {
            Button("Open Epic", systemImage: "arrow.up.forward.app") { model.open(epic) }
            Button("Add Task", systemImage: "plus") { model.promptNewTask(epicId: epic.id) }
            Divider()
            Button("Move Up", systemImage: "arrow.up") { Task { await model.move(epic, by: -1) } }
                .disabled(!model.canMove(epic, by: -1))
            Button("Move Down", systemImage: "arrow.down") { Task { await model.move(epic, by: 1) } }
                .disabled(!model.canMove(epic, by: 1))
            let groups = model.board?.groups ?? []
            if !groups.isEmpty {
                Menu("Move to Group", systemImage: "square.stack.3d.up") {
                    ForEach(groups.sorted { $0.rank < $1.rank }) { group in
                        Button { Task { await model.move(epic, toGroup: group.id) } } label: {
                            if epic.groupId == group.id {
                                Label(group.name, systemImage: "checkmark")
                            } else {
                                Text(group.name)
                            }
                        }
                    }
                    if epic.groupId != nil {
                        Divider()
                        Button("No Group") { Task { await model.move(epic, toGroup: nil) } }
                    }
                }
            }
            Divider()
            Button("Delete Epic", systemImage: "trash", role: .destructive) { model.confirmDelete(epic) }
        }
        .labelStyle(.iconOnly)
        .frame(width: 30, height: 30)
    }
}

/// The title of a group of epics, which heads its outline. The epics are rows of their own
/// below it (`BoardModel.rows`), not children of this.
private struct GroupHeader: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var group: EpicGroup
    /// Drawn in hand, where it is a picture of itself and picks nothing up.
    var carried = false

    var body: some View {
        let collapsed = model.isCollapsed(group)
        HStack(spacing: 8) {
            Button {
                withAnimation(.snappy) { model.toggle(group) }
            } label: {
                Image(systemName: "chevron.down")
                    .rotationEffect(.degrees(collapsed ? -90 : 0))
                    .font(.footnote.weight(.bold))
                    .frame(width: 28, height: 28)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(collapsed ? "Expand group" : "Collapse group")

            Text(group.name)
                .font(.headline.weight(theme.headingWeight))
            Text("\(model.epics(in: group).count)")
                .font(.caption.weight(.semibold).monospacedDigit())
                .foregroundStyle(theme.textSecondary)
            Spacer()
            if model.canWrite {
                Button("Add epic to group", systemImage: "plus") { model.promptNewEpic(groupId: group.id) }
                    .labelStyle(.iconOnly)
                    .frame(width: 30, height: 30)
                Menu("Group actions", systemImage: "ellipsis") {
                    Button("Rename", systemImage: "pencil") { model.promptRename(group) }
                    Button("Add Epic", systemImage: "plus") { model.promptNewEpic(groupId: group.id) }
                    Divider()
                    Button("Move Up", systemImage: "arrow.up") { Task { await model.move(group, by: -1) } }
                        .disabled(!model.canMove(group, by: -1))
                    Button("Move Down", systemImage: "arrow.down") { Task { await model.move(group, by: 1) } }
                        .disabled(!model.canMove(group, by: 1))
                    Divider()
                    Button("Delete Group", systemImage: "trash", role: .destructive) { model.confirmDelete(group) }
                }
                .labelStyle(.iconOnly)
                .frame(width: 30, height: 30)
            }
        }
        .modifier(GroupDrag(model: model, group: group, enabled: !carried))
    }
}

/// A group's outline, or the part of it one row carries: open at the bottom for the title,
/// open at both ends for an epic in the middle, open at the top for the last.
private nonisolated struct GroupOutline: Shape {
    static let radius: CGFloat = 28

    var piece: GroupPiece
    var radius: CGFloat = GroupOutline.radius

    func path(in rect: CGRect) -> Path {
        let box = rect.insetBy(dx: 0.5, dy: 0.5)
        guard piece != .whole else { return Path(roundedRect: box, cornerRadius: radius, style: .continuous) }
        var path = Path()
        let top = piece == .top, bottom = piece == .bottom
        if top {
            // Up the leading side, over the head, and down the trailing side.
            path.move(to: CGPoint(x: box.minX, y: rect.maxY))
            path.addLine(to: CGPoint(x: box.minX, y: box.minY + radius))
            path.addArc(tangent1End: CGPoint(x: box.minX, y: box.minY), tangent2End: CGPoint(x: box.minX + radius, y: box.minY), radius: radius)
            path.addLine(to: CGPoint(x: box.maxX - radius, y: box.minY))
            path.addArc(tangent1End: CGPoint(x: box.maxX, y: box.minY), tangent2End: CGPoint(x: box.maxX, y: box.minY + radius), radius: radius)
            path.addLine(to: CGPoint(x: box.maxX, y: rect.maxY))
        } else if bottom {
            // Down the leading side, under the foot, and up the trailing side.
            path.move(to: CGPoint(x: box.minX, y: rect.minY))
            path.addLine(to: CGPoint(x: box.minX, y: box.maxY - radius))
            path.addArc(tangent1End: CGPoint(x: box.minX, y: box.maxY), tangent2End: CGPoint(x: box.minX + radius, y: box.maxY), radius: radius)
            path.addLine(to: CGPoint(x: box.maxX - radius, y: box.maxY))
            path.addArc(tangent1End: CGPoint(x: box.maxX, y: box.maxY), tangent2End: CGPoint(x: box.maxX, y: box.maxY - radius), radius: radius)
            path.addLine(to: CGPoint(x: box.maxX, y: rect.minY))
        } else {
            path.move(to: CGPoint(x: box.minX, y: rect.minY))
            path.addLine(to: CGPoint(x: box.minX, y: rect.maxY))
            path.move(to: CGPoint(x: box.maxX, y: rect.minY))
            path.addLine(to: CGPoint(x: box.maxX, y: rect.maxY))
        }
        return path
    }
}

private struct GroupPieceModifier: ViewModifier {
    @Environment(\.theme) private var theme
    var piece: GroupPiece
    /// The room the page leaves between rows, which the outline has to cross to meet the next piece.
    var gap: CGFloat

    func body(content: Content) -> some View {
        let closes = piece == .bottom || piece == .whole
        content
            .padding(.horizontal, 10)
            .padding(.top, piece == .top || piece == .whole ? 10 : 0)
            .padding(.bottom, closes ? 10 : 0)
            .background {
                GroupOutline(piece: piece)
                    .stroke(theme.divider, lineWidth: 1)
                    .padding(.bottom, closes ? 0 : -gap)
            }
    }
}

private extension View {
    /// Draws this row's part of its group's outline. A row in no group is left as it is.
    @ViewBuilder func groupPiece(_ piece: GroupPiece?, gap: CGFloat) -> some View {
        if let piece {
            modifier(GroupPieceModifier(piece: piece, gap: gap))
        } else {
            self
        }
    }
}

private struct EmptyBoard: View {
    @Bindable var model: BoardModel

    var body: some View {
        ContentUnavailableView {
            Label("This board is empty.", systemImage: "rectangle.split.3x1")
        } actions: {
            if model.canWrite {
                Button("Add a Task") { model.promptNewTask(epicId: nil) }.buttonStyle(.clearGlass)
                Button("Add an Epic") { model.promptNewEpic() }.buttonStyle(.clearGlass)
            }
        }
        .padding(.top, 24)
    }
}

// MARK: - iPhone: one column at a time

private struct BoardPager: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    private var watch: CardDragWatch { model.drag }

    var body: some View {
        FillLayout {
            pages
        }
        .catchesDrops(model.drag)
        // A card held at either side is carried to the column that way.
        .overlay {
            ColumnEdgeSensors(selection: $model.column, watch: watch) { payloads, status in
                // Let go on the edge itself, it goes to the foot of its lane in the column beyond
                // the edge — unless that is where it already is.
                guard let task = model.task(dropped: payloads.first ?? ""), task.status != status else { return false }
                return model.drop(payloads, epicId: task.epicId, status: status, index: nil)
            }
        }
        .safeAreaBar(edge: .top, spacing: 8) {
            ColumnSwitcher(selection: $model.column, count: model.count) { payloads, status in
                model.drop(payloads, epicId: model.task(dropped: payloads.first ?? "")?.epicId, status: status, index: nil)
            }
            .padding(.horizontal, 14)
            // The header ends a little below the selector, not along its bottom edge.
            .padding(.bottom, ColumnSwitcher.footing)
        }
        .environment(watch)
    }

    private var pages: some View {
        ScrollView {
            LazyVStack(spacing: 12) {
                if !model.store.isOpen {
                    InfoBanner(text: "You cannot make changes to a board that is closed, reopen the board to enable editing.")
                }
                ForEach(model.rows) { row in
                    switch row {
                    case .group(let group, let piece):
                        GroupRow(model: model, group: group, piece: piece, spacing: 12)
                    case .lane(let epic, let piece):
                        LaneRow(model: model, epic: epic, piece: piece, spacing: 12) {
                            PagerLane(model: model, epic: epic)
                        }
                    }
                }
                LaneRow(model: model, epic: nil, piece: nil, spacing: 12) {
                    PagerLane(model: model, epic: nil)
                }
                if model.isEmpty {
                    EmptyBoard(model: model)
                }
            }
            .padding(.horizontal, 14)
            .padding(.bottom, 24)
            .boardPage(model)
        }
        .columnPaging($model.column)
        // A moved card is followed: to its column, then down the page to the card itself.
        .followsLanding(model.landed?.token) { model.landingFollowed() }
        .followsLane(model.laneLanded)
        .onChange(of: model.landed) { _, landing in
            guard let landing else { return }
            withAnimation(.snappy(duration: 0.32)) { model.column = landing.status }
        }
    }
}

/// A row of the phone's board: an epic's header, which stays put, over its
/// three zones, of which the page's column is the one in view.
private struct PagerLane: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    /// How tall each active zone stands, which its Finished zone is held to.
    @State private var heights = LaneHeights()

    var body: some View {
        VStack(spacing: 8) {
            LaneHeader(model: model, epic: epic, count: model.taskCount(epicId: epic?.id))
            if model.isFolded(epic) {
                FoldedLaneNote(count: model.taskCount(epicId: epic?.id))
            } else {
                ColumnPages {
                    ForEach(TaskState.allCases) { status in
                        Zone(model: model, epicId: epic?.id, status: status,
                             whole: model.isWhole(epicId: epic?.id, status: status), heights: heights)
                            .accessibilityHidden(status != model.column)
                    }
                }
            }
        }
        .padding(10)
        .glassEffect(.regular, in: theme.cardShape)
        // In hand, its header is what is carried and the lane has left its place.
        .leaves(epic != nil && model.drag.epic == epic?.id, closed: epic != nil && model.drag.left == epic?.id)
    }
}

/// The three columns as a switch: the system's own (`ColumnSegments`), so that what is chosen
/// is a piece of glass that slides to where it is put and can be taken hold of and slid there,
/// as on a project's page. Also where a dragged card goes to change column on a phone: let go over a column to
/// send the card there, or hold over it to be taken there with the card still in hand.
///
/// The system's control is one view and not three, so the column a card is over is worked out
/// from how far across the finger is, and marked by a wash drawn over that third of it.
struct ColumnSwitcher: View {
    /// The room a header leaves under the selector, so that its edge does not run along the selector's.
    static let footing: CGFloat = 8

    @Environment(\.theme) private var theme
    @Binding var selection: TaskState
    var count: (TaskState) -> Int
    var onDrop: ([String], TaskState) -> Bool
    @Environment(CardDragWatch.self) private var watch: CardDragWatch?
    /// How wide the control is, for telling which column a finger is over.
    @State private var width: CGFloat = 0
    /// The column a card in hand is over.
    @State private var over: TaskState?
    @State private var springLoad: Task<Void, Never>?

    var body: some View {
        ColumnSegments(titles: TaskState.allCases.map { "\($0.title) \(count($0))" },
                       index: Binding(get: { TaskState.allCases.firstIndex(of: selection) ?? 0 },
                                      set: { index in withAnimation(.snappy) { selection = TaskState.allCases[index] } }),
                       wash: theme.accentWash, chosen: theme.primary, plain: theme.textPrimary)
        .frame(height: 44)
        .accessibilityLabel("Column")
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { width = $0 }
        .overlay(alignment: .leading) {
            // Which column a card would go to, said over the control and not by it.
            if let over, let index = TaskState.allCases.firstIndex(of: over), width > 0 {
                let each = width / CGFloat(TaskState.allCases.count)
                Capsule()
                    .fill(theme.accentWash)
                    .padding(3)
                    .frame(width: each)
                    .offset(x: each * CGFloat(index))
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
        }
        .animation(.snappy(duration: 0.2), value: over)
        .modifier(SelectorDrop(watch: watch, located: { point in
            let all = TaskState.allCases
            guard width > 0 else { return }
            let index = min(max(Int(point.x / width * CGFloat(all.count)), 0), all.count - 1)
            if over != all[index] { over = all[index] }
        }, targeted: { isTargeted in
            if !isTargeted { over = nil }
        }) { payload in
            guard let over else { return false }
            return onDrop([payload], over)
        })
        .onChange(of: over) { _, status in
            // Held over a column that is not showing, the page turns to it.
            springLoad?.cancel()
            guard let status, status != selection else { return }
            springLoad = Task {
                try? await Task.sleep(for: .milliseconds(700))
                guard !Task.isCancelled, watch?.stalled != true, over == status else { return }
                withAnimation(.snappy) { selection = status }
            }
        }
        .sensoryFeedback(.selection, trigger: selection)
        // Three names share one line, as a tab bar's do: past this size they would only truncate.
        .dynamicTypeSize(...DynamicTypeSize.xxxLarge)
    }
}

/// The selector as somewhere a card is let go. It is not on the board's page, so it says where
/// the finger is on the screen, and the card in hand comes up to it with the finger.
private struct SelectorDrop: ViewModifier {
    var watch: CardDragWatch?
    var located: (CGPoint) -> Void
    var targeted: (Bool) -> Void
    var perform: (String) -> Bool

    func body(content: Content) -> some View {
        if let watch {
            content.boardDrop(watch, onScreen: true, located: located, targeted: targeted, perform: perform)
        } else {
            content
        }
    }
}

/// The system's segmented control, as itself and not through a picker: a picker has no way to
/// say what colour the chosen segment is — a tint on it does nothing — and the control does,
/// as one of its own properties. The chosen column is washed in the theme's colour and written
/// in it, as a chosen tab is.
///
/// Turned some other way — a swipe across the page, a card held at its edge — the glass goes to
/// the new column at the control's own pace, which is not to be had by asking: told to take
/// two seconds over it, here or through a picker, it was there within a quarter of one.
#if os(iOS)
private struct ColumnSegments: UIViewRepresentable {
    var titles: [String]
    @Binding var index: Int
    var wash: Color
    var chosen: Color
    var plain: Color

    func makeCoordinator() -> Coordinator { Coordinator(index: $index) }

    func makeUIView(context: Context) -> UISegmentedControl {
        let control = UISegmentedControl(items: titles)
        control.selectedSegmentIndex = index
        control.addTarget(context.coordinator, action: #selector(Coordinator.changed(_:)), for: .valueChanged)
        // Wide as it is given, and no wider for a long title.
        control.setContentHuggingPriority(.defaultLow, for: .horizontal)
        control.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        control.apportionsSegmentWidthsByContent = false
        return control
    }

    func updateUIView(_ control: UISegmentedControl, context: Context) {
        context.coordinator.index = $index
        for (segment, title) in titles.enumerated() where control.titleForSegment(at: segment) != title {
            control.setTitle(title, forSegmentAt: segment)
        }
        if control.selectedSegmentIndex != index { control.selectedSegmentIndex = index }
        control.selectedSegmentTintColor = UIColor(wash)
        let font = UIFont.preferredFont(forTextStyle: .footnote)
        control.setTitleTextAttributes([.foregroundColor: UIColor(plain), .font: font], for: .normal)
        control.setTitleTextAttributes([.foregroundColor: UIColor(chosen), .font: UIFont.systemFont(ofSize: font.pointSize, weight: .semibold)],
                                       for: .selected)
    }

    func sizeThatFits(_ proposal: ProposedViewSize, uiView: UISegmentedControl, context: Context) -> CGSize? {
        CGSize(width: proposal.width ?? uiView.intrinsicContentSize.width,
               height: proposal.height ?? uiView.intrinsicContentSize.height)
    }

    final class Coordinator: NSObject {
        var index: Binding<Int>
        init(index: Binding<Int>) { self.index = index }

        @objc func changed(_ control: UISegmentedControl) {
            index.wrappedValue = control.selectedSegmentIndex
        }
    }
}
#else
/// A Mac shows every column side by side, so this is only seen in a window made very narrow —
/// where the system's own picker does.
private struct ColumnSegments: View {
    var titles: [String]
    @Binding var index: Int
    var wash: Color
    var chosen: Color
    var plain: Color

    var body: some View {
        Picker("Column", selection: $index) {
            ForEach(titles.indices, id: \.self) { Text(titles[$0]).tag($0) }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
    }
}
#endif
