import SwiftUI

/// The Kanban board: three fixed columns, with epics as lanes spanning them.
///
/// On an iPad the three columns sit side by side, as on the web. On an iPhone
/// one column fills the screen and the others are a swipe away, with the
/// column switcher doubling as a drop target so a card can still be carried
/// across.
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
        // Inside the panel modifier below: an inspector lays its own backing
        // under the page it sits beside.
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
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
        .detailPanel(isPresented: Binding(get: { selection != nil }, set: { if !$0 { model.closeDetail() } })) {
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
        ScrollView {
            LazyVStack(spacing: 14, pinnedViews: .sectionHeaders) {
                Section {
                    if !model.store.isOpen {
                        InfoBanner(text: "You cannot make changes to a board that is closed, reopen the board to enable editing.")
                    }
                    ForEach(model.topLevel) { item in
                        switch item {
                        case .group(let group):
                            GroupContainer(model: model, group: group) { epic in
                                GridLane(model: model, epic: epic)
                            }
                        case .epic(let epic):
                            GridLane(model: model, epic: epic)
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
            LaneHeader(model: model, epic: epic, count: model.tasks(epicId: epic?.id).count)
            if let epic, model.isCollapsed(epic) {
                Text("\(model.tasks(epicId: epic.id).count) task(s) hidden")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                HStack(alignment: .top, spacing: 8) {
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

/// A drop zone: one status of one lane.
private struct Zone: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epicId: UUID?
    var status: TaskState
    @State private var targeted = false

    var body: some View {
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
        .frame(maxWidth: .infinity, alignment: .top)
        .background(targeted ? theme.accentWash : .clear, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay {
            if targeted {
                RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(theme.primary.opacity(0.6))
            }
        }
        .dropDestination(for: String.self) { payloads, _ in
            model.drop(payloads, epicId: epicId, status: status, index: nil)
        } isTargeted: { targeted = $0 }
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
    @State private var targeted = false

    var body: some View {
        let store = model.store
        let card = TaskCardView(
            task: task,
            projectId: store.board?.projectId ?? UUID(),
            label: store.label(task.labelId),
            assignees: store.members(task.assigneeIds),
            presence: store.editors(on: EntityTypes.task, task.id),
            pulse: store.pulsing.contains(task.id),
            canWrite: model.canWrite)

        Button { model.open(task) } label: { card }
            .buttonStyle(.plain)
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
                model.drop(payloads, epicId: epicId, status: status, index: index)
            } isTargeted: { targeted = $0 }
            .animation(.easeOut(duration: 0.12), value: targeted)
    }
}

private struct CardDrag<Preview: View>: ViewModifier {
    @Environment(\.theme) private var theme
    var enabled: Bool
    var payload: String
    var preview: Preview

    func body(content: Content) -> some View {
        if enabled {
            content.draggable(payload) {
                preview.frame(width: 240).environment(\.theme, theme)
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

/// A titled container around the epics of one group.
private struct GroupContainer<Lane: View>: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var group: EpicGroup
    @ViewBuilder var lane: (Epic) -> Lane

    var body: some View {
        let collapsed = model.isCollapsed(group)
        VStack(spacing: 10) {
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
            if !collapsed {
                ForEach(model.epics(in: group)) { epic in
                    lane(epic)
                }
            }
        }
        .padding(10)
        .overlay(RoundedRectangle(cornerRadius: 28, style: .continuous).strokeBorder(theme.divider, lineWidth: 1))
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

    var body: some View {
        TabView(selection: $model.column) {
            ForEach(TaskState.allCases) { status in
                ScrollView {
                    LazyVStack(spacing: 12) {
                        if !model.store.isOpen {
                            InfoBanner(text: "You cannot make changes to a board that is closed, reopen the board to enable editing.")
                        }
                        ForEach(model.topLevel) { item in
                            switch item {
                            case .group(let group):
                                GroupContainer(model: model, group: group) { epic in
                                    PagerLane(model: model, epic: epic, status: status)
                                }
                            case .epic(let epic):
                                PagerLane(model: model, epic: epic, status: status)
                            }
                        }
                        PagerLane(model: model, epic: nil, status: status)
                        if model.isEmpty {
                            EmptyBoard(model: model)
                        }
                    }
                    .padding(.horizontal, 14)
                    .padding(.bottom, 24)
                }
                .tag(status)
            }
        }
        .tabViewStyle(.page(indexDisplayMode: .never))
        .safeAreaInset(edge: .top, spacing: 8) {
            ColumnSwitcher(selection: $model.column, count: model.count) { payloads, status in
                model.drop(payloads, epicId: model.task(dropped: payloads.first ?? "")?.epicId, status: status, index: nil)
            }
            .padding(.horizontal, 14)
        }
    }
}

private struct PagerLane: View {
    @Environment(\.theme) private var theme
    @Bindable var model: BoardModel
    var epic: Epic?
    var status: TaskState

    var body: some View {
        VStack(spacing: 8) {
            LaneHeader(model: model, epic: epic, count: model.tasks(epicId: epic?.id, status: status).count)
            if let epic, model.isCollapsed(epic) {
                Text("\(model.tasks(epicId: epic.id).count) task(s) hidden")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Zone(model: model, epicId: epic?.id, status: status)
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
    }
}

private struct ColumnSwitcherSegment: View {
    @Environment(\.theme) private var theme
    var status: TaskState
    var count: Int
    @Binding var selection: TaskState
    var onDrop: ([String], TaskState) -> Bool
    @State private var targeted = false
    @State private var springLoad: Task<Void, Never>?

    var body: some View {
        let selected = selection == status
        Button {
            withAnimation(.snappy) { selection = status }
        } label: {
            HStack(spacing: 5) {
                Text(status.title).lineLimit(1).minimumScaleFactor(0.8)
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
            onDrop(payloads, status)
        } isTargeted: { isTargeted in
            targeted = isTargeted
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
