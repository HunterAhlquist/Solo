import SwiftUI

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
        .refreshable { await model.store.refresh() }
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
                            GroupHeader(model: model, group: group).groupPiece(piece, gap: 14)
                        case .lane(let epic, let piece):
                            GridLane(model: model, epic: epic).groupPiece(piece, gap: 14)
                        }
                    }
                    GridLane(model: model, epic: nil)
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
        }
    }
}

/// One lane of the grid: an epic's header over its three zones. `epic` nil is
/// the ad-hoc band — tasks that belong to no epic — which always comes last.
private struct GridLane: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?

    var body: some View {
        VStack(spacing: 8) {
            LaneHeader(model: model, epic: epic, count: model.taskCount(epicId: epic?.id))
            if let epic, model.isCollapsed(epic) {
                Text("\(model.taskCount(epicId: epic.id)) task(s) hidden")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                // Every zone is as tall as the tallest, so a card can be let go
                // anywhere under a short column and still land in it.
                ZoneRow(spacing: 8) {
                    ForEach(TaskState.allCases) { status in
                        Zone(model: model, epicId: epic?.id, status: status)
                    }
                }
            }
        }
        .padding(10)
        .glassEffect(.regular, in: theme.cardShape)
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
private struct Zone: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epicId: UUID?
    var status: TaskState
    @Environment(CardDragWatch.self) private var watch: CardDragWatch?
    @State private var targeted = false

    var body: some View {
        let _ = Perf.count("zone")
        let cards = model.cards(epicId: epicId, status: status)
        let hidden = model.hiddenCount(epicId: epicId, status: status)
        VStack(spacing: 6) {
            ForEach(Array(cards.enumerated()), id: \.element.id) { index, task in
                BoardCard(model: model, task: task, epicId: epicId, status: status, index: index)
            }
            if hidden > 0 {
                ShowAllButton(hidden: hidden) { withAnimation { model.expandFinished(epicId) } }
            }
            if cards.isEmpty {
                Color.clear.frame(height: 36)
            }
        }
        .padding(6)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .contentShape(.rect)
        .background(targeted ? theme.accentWash : .clear, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay {
            if targeted {
                RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(theme.primary.opacity(0.6))
            }
        }
        .dropDestination(for: String.self) { payloads, _ in
            watch?.ended()
            return model.drop(payloads, epicId: epicId, status: status, index: nil)
        } isTargeted: {
            targeted = $0
            watch?.hover($0)
        }
        .animation(.easeOut(duration: 0.15), value: targeted)
    }
}

/// A card in its zone: tap opens it, a long press lifts it, and another card
/// let go on top of it lands just above.
private struct BoardCard: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var task: TaskItem
    var epicId: UUID?
    var status: TaskState
    var index: Int
    @Environment(CardDragWatch.self) private var watch: CardDragWatch?
    @State private var targeted = false

    var body: some View {
        let _ = Perf.count("card")
        let store = model.store
        let card = TaskCardView(
            task: task,
            projectId: store.board?.projectId ?? UUID(),
            label: store.label(task.labelId),
            assignees: store.members(task.assigneeIds),
            presence: store.editors(on: EntityTypes.task, task.id),
            pulse: store.pulsing.contains(task.id),
            canWrite: model.canWrite,
            kitMount: store.board.map { BoardKitMount.id(for: $0.id) })

        Button { model.open(task) } label: { card }
            .buttonStyle(.plain)
            .landingTarget(model.landed?.taskId == task.id)
            .overlay(alignment: .top) {
                if targeted {
                    Capsule().fill(theme.primary).frame(height: 3).offset(y: -5)
                }
            }
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
            .modifier(CardDrag(enabled: model.canWrite, payload: store.serial(task), preview: card))
            .dropDestination(for: String.self) { payloads, _ in
                watch?.ended()
                return model.drop(payloads, epicId: epicId, status: status, index: index)
            } isTargeted: {
                targeted = $0
                watch?.hover($0)
            }
            .animation(.easeOut(duration: 0.12), value: targeted)
    }
}

private struct CardDrag<Preview: View>: ViewModifier {
    @Environment(\.theme) private var theme
    @Environment(Session.self) private var session
    var enabled: Bool
    var payload: String
    var preview: Preview

    func body(content: Content) -> some View {
        if enabled {
            content.draggable(payload) {
                // A drag preview is drawn outside the view tree, so it has none of
                // the environment: everything a card reads has to be handed to it.
                preview.frame(width: 240).environment(\.theme, theme).environment(session)
                    .environment(session.navigator)
            }
        } else {
            content
        }
    }
}

// MARK: - Lanes and groups

private struct LaneHeader: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    var count: Int
    @State private var targeted = false

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
        .overlay(alignment: .top) {
            if targeted {
                Capsule().fill(theme.primary).frame(height: 3).offset(y: -8)
            }
        }
        .modifier(EpicDrag(model: model, epic: epic, targeted: $targeted))
    }
}

/// Lanes reorder by dragging one header onto another, or from the menu.
private struct EpicDrag: ViewModifier {
    @Bindable var model: BoardModel
    var epic: Epic?
    @Binding var targeted: Bool

    func body(content: Content) -> some View {
        if let epic, model.canWrite {
            content
                .draggable(BoardDrag.epic(epic.id)) {
                    Text(epic.name).font(.subheadline.weight(.semibold)).padding(10)
                }
                .dropDestination(for: String.self) { payloads, _ in
                    guard let payload = payloads.first, payload.hasPrefix(BoardDrag.epicPrefix) else { return false }
                    Task { await model.dropEpic(payload, before: epic) }
                    return true
                } isTargeted: { targeted = $0 }
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
    }
}

/// A group's outline, or the part of it one row carries: open at the bottom for the title,
/// open at both ends for an epic in the middle, open at the top for the last.
private nonisolated struct GroupOutline: Shape {
    var piece: GroupPiece
    var radius: CGFloat = 28

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
                Button("Add a Task") { model.promptNewTask(epicId: nil) }.buttonStyle(.glass)
                Button("Add an Epic") { model.promptNewEpic() }.buttonStyle(.glass)
            }
        }
        .padding(.top, 24)
    }
}

// MARK: - iPhone: one column at a time

private struct BoardPager: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    @State private var watch = CardDragWatch()

    var body: some View {
        FillLayout {
            pages
        }
        // A card held at either side is carried to the column that way.
        .overlay {
            ColumnEdgeSensors(selection: $model.column, watch: watch) { payloads, status in
                // Let go on the edge itself, it joins the column showing — unless that is where it already is.
                guard let task = model.task(dropped: payloads.first ?? ""), task.status != status else { return false }
                return model.drop(payloads, epicId: task.epicId, status: status, index: nil)
            }
        }
        .safeAreaBar(edge: .top, spacing: 8) {
            ColumnSwitcher(selection: $model.column, count: model.count) { payloads, status in
                model.drop(payloads, epicId: model.task(dropped: payloads.first ?? "")?.epicId, status: status, index: nil)
            }
            .padding(.horizontal, 14)
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
                        GroupHeader(model: model, group: group).groupPiece(piece, gap: 12)
                    case .lane(let epic, let piece):
                        PagerLane(model: model, epic: epic).groupPiece(piece, gap: 12)
                    }
                }
                PagerLane(model: model, epic: nil)
                if model.isEmpty {
                    EmptyBoard(model: model)
                }
            }
            .padding(.horizontal, 14)
            .padding(.bottom, 24)
        }
        .columnPaging($model.column)
        // A moved card is followed: to its column, then down the page to the card itself.
        .followsLanding(model.landed?.token) { model.landingFollowed() }
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

    var body: some View {
        VStack(spacing: 8) {
            LaneHeader(model: model, epic: epic, count: model.taskCount(epicId: epic?.id))
            if let epic, model.isCollapsed(epic) {
                Text("\(model.taskCount(epicId: epic.id)) task(s) hidden")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                ColumnPages {
                    ForEach(TaskState.allCases) { status in
                        Zone(model: model, epicId: epic?.id, status: status)
                            .accessibilityHidden(status != model.column)
                    }
                }
            }
        }
        .padding(10)
        .glassEffect(.regular, in: theme.cardShape)
    }
}

/// The three columns as a switch. Also where a dragged card goes to change
/// column on a phone: let go over a column to send the card there, or hold
/// over it to be taken there with the card still in hand.
struct ColumnSwitcher: View {
    @Environment(\.theme) private var theme
    @Binding var selection: TaskState
    var count: (TaskState) -> Int
    var onDrop: ([String], TaskState) -> Bool
    @Namespace private var namespace

    var body: some View {
        GlassEffectContainer {
            HStack(spacing: 4) {
                ForEach(TaskState.allCases) { status in
                    ColumnSwitcherSegment(status: status, count: count(status), selection: $selection, onDrop: onDrop)
                }
            }
            .padding(4)
            .glassEffect(.regular, in: .capsule)
        }
        // Three names share one line, as a tab bar's do: past this size they would only truncate.
        .dynamicTypeSize(...DynamicTypeSize.xxxLarge)
    }
}

private struct ColumnSwitcherSegment: View {
    @Environment(\.theme) private var theme
    var status: TaskState
    var count: Int
    @Binding var selection: TaskState
    var onDrop: ([String], TaskState) -> Bool
    @Environment(CardDragWatch.self) private var watch: CardDragWatch?
    @State private var targeted = false
    @State private var springLoad: Task<Void, Never>?

    var body: some View {
        let selected = selection == status
        Button {
            withAnimation(.snappy) { selection = status }
        } label: {
            HStack(spacing: 5) {
                Text(status.title).lineLimit(1).minimumScaleFactor(0.7)
                Text("\(count)").monospacedDigit().opacity(0.7)
            }
            .font(.footnote.weight(.semibold))
            .frame(maxWidth: .infinity)
            .padding(.vertical, 9)
            .foregroundStyle(selected ? theme.primary : theme.textPrimary)
            .background {
                if selected || targeted {
                    Capsule().fill(theme.accentWash)
                }
            }
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .dropDestination(for: String.self) { payloads, _ in
            watch?.ended()
            return onDrop(payloads, status)
        } isTargeted: { isTargeted in
            targeted = isTargeted
            watch?.hover(isTargeted)
            springLoad?.cancel()
            guard isTargeted, selection != status else { return }
            springLoad = Task {
                try? await Task.sleep(for: .milliseconds(700))
                guard !Task.isCancelled else { return }
                withAnimation(.snappy) { selection = status }
            }
        }
        .sensoryFeedback(.selection, trigger: selection)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
