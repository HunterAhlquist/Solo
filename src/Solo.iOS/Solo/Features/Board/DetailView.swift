import SwiftUI

/// The detail panel for a task or an epic: an inspector beside the board on an
/// iPad, a sheet over it on an iPhone. Every field commits on a save event —
/// leaving the field, choosing from a menu, or an explicit Save — never per
/// keystroke. Opening the description editor reports Editing presence, which
/// is what warns the next person off.
struct DetailView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @Environment(\.detailInInspector) private var inInspector

    var store: BoardStore
    var selection: BoardSelection
    var onClose: () -> Void

    @State private var name = ""
    @State private var boundTo: UUID?
    @State private var draft: MarkdownDraft?
    @State private var confirmation: Confirmation?
    @State private var moving = false
    @FocusState private var nameFocused: Bool

    private var api: APIClient { session.api }

    private var task: TaskItem? {
        if case .task(let id) = selection { return store.board?.tasks.first { $0.id == id } }
        return nil
    }

    private var epic: Epic? {
        if case .epic(let id) = selection { return store.board?.epics.first { $0.id == id } }
        return nil
    }

    private var entityId: UUID {
        switch selection {
        case .task(let id), .epic(let id): id
        }
    }

    private var entityType: String { task != nil ? EntityTypes.task : EntityTypes.epic }
    private var canWrite: Bool { store.canWrite }

    var body: some View {
        Group {
            if inInspector {
                // The shell's column is a pane of glass outside any navigation
                // stack, so it has no bar: the panel brings its own header, and
                // no background, which would only flatten the glass.
                VStack(spacing: 0) {
                    HStack(spacing: 10) {
                        Text(title).font(.headline)
                        PresenceCluster(entries: store.presence(on: entityType, entityId), max: 4)
                        Spacer()
                        Button("Close", systemImage: "xmark", action: onClose)
                            .labelStyle(.iconOnly)
                            .buttonStyle(.clearGlass)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    panel
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            } else {
                NavigationStack {
                    panel
                        .themedBackground()
                        .navigationTitle(title)
                        .navigationBarTitleDisplayMode(.inline)
                        .toolbar {
                            ToolbarItem(placement: .topBarLeading) {
                                PresenceCluster(entries: store.presence(on: entityType, entityId), max: 4)
                            }
                            // Closing isn't confirming anything, so it doesn't get the filled accent button.
                            ToolbarItem(placement: .topBarTrailing) {
                                Button("Close", systemImage: "xmark", action: onClose)
                            }
                        }
                }
            }
        }
        .sheet(item: $draft) { draft in
            MarkdownEditorSheet(draft: draft)
                .onDisappear { report(editing: false) }
        }
        .sheet(isPresented: $moving) {
            if let task, let board = store.board {
                MoveTaskSheet(projectId: board.projectId, currentBoardId: board.id) { target, epicId in
                    await move(task, to: target, epicId: epicId)
                }
            }
        }
        .confirmation($confirmation)
        .onAppear(perform: seed)
        .onChange(of: entityId) { _, _ in seed() }
    }

    private var title: String {
        if let task { return store.serial(task) }
        return "Epic"
    }

    @ViewBuilder
    private var panel: some View {
        if task == nil && epic == nil {
            // Deleted or moved away while open.
            ContentUnavailableView("This is no longer on the board", systemImage: "questionmark.folder")
        } else {
            content
        }
    }

    private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                EditingWarning(editors: store.editors(on: entityType, entityId))

                TextField("Name", text: $name, axis: .vertical)
                    .font(.title3.weight(.semibold))
                    .focused($nameFocused)
                    .disabled(!canWrite)
                    .submitLabel(.done)
                    .onChange(of: name) { _, value in
                        // A return in a multi-line field is the commit, not a newline.
                        if value.contains("\n") {
                            name = value.replacingOccurrences(of: "\n", with: "")
                            nameFocused = false
                        }
                    }
                    .onChange(of: nameFocused) { _, focused in
                        if !focused { commitName() }
                    }
                    .card()

                if let task {
                    VStack(alignment: .leading, spacing: 0) {
                        taskFields(task)
                        TaskDetailExtensions(task: task, store: store)
                    }
                    .task(id: task.id) {
                        // A contribution drawn from the web saved something to this task: the board's copy is stale.
                        session.kit.watch(task: task.id, as: "detail") { Task { await store.refresh() } }
                    }
                    .onDisappear { session.kit.watch(task: task.id, as: "detail", nil) }
                } else if let epic {
                    AssigneeField(store: store, assigneeIds: epic.assigneeIds, canWrite: canWrite) { ids in
                        if let saved = await session.save({ try await api.setEpicAssignees(epic.id, ids) },
                                                          refresh: store.refresh) {
                            store.apply(saved)
                        }
                    }
                }

                VStack(alignment: .leading, spacing: 6) {
                    SectionTitle("Description")
                    MarkdownField(text: task?.description ?? epic?.description, canWrite: canWrite) {
                        editDescription()
                    }
                }

                if let task {
                    SubTaskSection(store: store, task: task)
                }

                CommentsSection(store: store, taskId: task?.id, epicId: epic?.id)

                HStack(spacing: 10) {
                    if canWrite {
                        if task != nil {
                            Button("Move to Board", systemImage: "arrow.right.doc.on.clipboard") { moving = true }
                                .buttonStyle(.clearGlass)
                        }
                        Button("Delete \(task != nil ? "Task" : "Epic")", systemImage: "trash", role: .destructive) {
                            confirmDelete()
                        }
                        .buttonStyle(.clearGlass)
                        .tint(theme.error)
                    }
                    // What extensions add to the task's menu. Theirs to offer whoever is looking,
                    // so not only to someone who can write.
                    if let task {
                        KitTaskMenu(task: task.id, project: store.board?.projectId, revision: Int(task.version))
                    }
                }
                .font(.subheadline)
                .padding(.top, 4)
            }
            .padding(16)
        }
        .scrollDismissesKeyboard(.interactively)
    }

    @ViewBuilder
    private func taskFields(_ task: TaskItem) -> some View {
        VStack(spacing: 0) {
            FieldRow(title: "Status", symbol: task.status.symbol) {
                Menu {
                    Picker("Status", selection: Binding(get: { task.status }, set: { setStatus(task, $0) })) {
                        ForEach(TaskState.allCases) { status in
                            Label(status.title, systemImage: status.symbol).tag(status)
                        }
                    }
                    .listedInMenu()
                } label: {
                    FieldValue(text: task.status.title)
                }
                .menuToggles()
                .disabled(!canWrite || !task.subTasks.isEmpty)
            }
            if !task.subTasks.isEmpty {
                Text("Status follows sub-tasks — tick them off instead.")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12)
                    .padding(.bottom, 8)
            }
            Divider().overlay(theme.divider)
            FieldRow(title: "Label", symbol: "tag") {
                // The control a sub-task's label has (`LabelMenu`): the chip itself is what is
                // pressed, with nothing drawn round it.
                let label = store.label(task.labelId)
                if canWrite {
                    LabelMenu(labels: store.labels(.task), selection: task.labelId, label: label) { setLabel(task, $0) }
                } else if let label {
                    LabelChip(label: label)
                } else {
                    Text("None").foregroundStyle(theme.textSecondary)
                }
            }
            Divider().overlay(theme.divider)
            AssigneeField(store: store, assigneeIds: task.assigneeIds, canWrite: canWrite, embedded: true) { ids in
                if let saved = await session.save({ try await api.setTaskAssignees(task.id, ids) }, refresh: store.refresh) {
                    store.apply(saved)
                }
            }
            Divider().overlay(theme.divider)
            // A start only means something against a deadline, so it waits for one.
            FieldRow(title: "Start", symbol: "calendar.badge.clock") {
                DayField(day: task.startAt, placeholder: "Created \(task.createdAt.formatted(.dateTime.day().month(.abbreviated)))",
                         latest: task.deadlineAt, disabled: !canWrite || task.deadlineAt == nil) { start in
                    guard task.startAt != start else { return }
                    await saveSchedule(task, start: start, deadline: task.deadlineAt)
                }
            }
            Divider().overlay(theme.divider)
            FieldRow(title: "Deadline", symbol: "flag.checkered") {
                DayField(day: task.deadlineAt, placeholder: "No deadline", disabled: !canWrite) { deadline in
                    guard task.deadlineAt != deadline else { return }
                    // A start left with no deadline, or stranded past a new one, goes rather than blocks.
                    let start = deadline.flatMap { end in task.startAt.flatMap { $0 <= end ? $0 : nil } }
                    await saveSchedule(task, start: start, deadline: deadline)
                }
            }
        }
        .card(padding: 0)
    }

    // MARK: Saves

    /// Re-seeds the name buffer only when the panel switches entity, so a live
    /// refetch never clobbers what is being typed.
    private func seed() {
        guard boundTo != entityId else { return }
        boundTo = entityId
        name = task?.name ?? epic?.name ?? ""
    }

    private func commitName() {
        let trimmed = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(FieldLimits.itemName))
        guard !trimmed.isEmpty else {
            name = task?.name ?? epic?.name ?? ""
            return
        }
        if let task, trimmed != task.name {
            Task {
                if let saved = await session.save({ try await api.updateTask(task, name: trimmed) }, refresh: store.refresh) {
                    store.apply(saved)
                }
            }
        } else if let epic, trimmed != epic.name {
            Task {
                if let saved = await session.save({
                    try await api.updateEpic(epic, name: trimmed, description: epic.description)
                }, refresh: store.refresh) {
                    store.apply(saved)
                }
            }
        }
    }

    private func setStatus(_ task: TaskItem, _ status: TaskState) {
        guard task.status != status else { return }
        Task {
            if let saved = await session.save({ try await api.setTaskStatus(task.id, status) }, refresh: store.refresh) {
                store.apply(saved)
            }
        }
    }

    private func setLabel(_ task: TaskItem, _ labelId: UUID?) {
        guard task.labelId != labelId else { return }
        Task {
            if let saved = await session.save({ try await api.updateTask(task, labelId: .some(labelId)) },
                                              refresh: store.refresh) {
                store.apply(saved)
            }
        }
    }

    private func saveSchedule(_ task: TaskItem, start: Day?, deadline: Day?) async {
        if let saved = await session.save({ try await api.setTaskSchedule(task, start: start, deadline: deadline) },
                                          refresh: store.refresh) {
            store.apply(saved)
        }
    }

    private func editDescription() {
        report(editing: true)
        draft = MarkdownDraft(title: "Description", text: task?.description ?? epic?.description ?? "",
                              limit: FieldLimits.description, projectId: store.board?.projectId) { text in
            if let task {
                if let saved = await session.save({ try await api.updateTask(task, description: .some(text)) },
                                                  refresh: store.refresh) {
                    store.apply(saved)
                }
            } else if let epic {
                if let saved = await session.save({ try await api.updateEpic(epic, name: epic.name, description: text) },
                                                  refresh: store.refresh) {
                    store.apply(saved)
                }
            }
        }
    }

    private func report(editing: Bool) {
        Task { await session.hub.setPresence(entityType, entityId, editing ? .editing : .viewing) }
    }

    private func move(_ task: TaskItem, to target: Board, epicId: UUID?) async {
        guard let board = store.board,
              let moved = await session.save({ try await api.moveTask(task.id, toBoard: target.id, epicId: epicId) },
                                             refresh: store.refresh) else { return }
        // Off the board being left before navigating, so it can't flash back
        // in the frame between the save and the new board's load.
        store.removeTask(task.id)
        session.assigned.apply(moved)
        onClose()
        navigator.open(.board(project: board.projectAlias, board: target.alias, task: moved.number))
    }

    private func confirmDelete() {
        let noun = task != nil ? "task" : "epic"
        confirmation = Confirmation(title: "Delete \(noun)", message: "Delete the \(noun) \"\(task?.name ?? epic?.name ?? "")\"?") {
            if let task {
                if await session.perform({ try await api.deleteTask(task.id) }) {
                    store.removeTask(task.id)
                }
            } else if let epic {
                await session.perform { try await api.deleteEpic(epic.id) }
                await store.refresh()
            }
            onClose()
        }
    }
}

// MARK: - Field pieces

struct FieldRow<Value: View>: View {
    @Environment(\.theme) private var theme
    @Environment(\.dynamicTypeSize) private var typeSize
    /// The icon's column grows with the text, or at the largest sizes the two collide.
    @ScaledMetric(relativeTo: .subheadline) private var iconWidth: CGFloat = 22
    var title: String
    var symbol: String
    @ViewBuilder var value: Value

    var body: some View {
        // At accessibility sizes a name and its value no longer share a line.
        let layout = typeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 4)) : AnyLayout(HStackLayout(spacing: 10))
        layout {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                    .foregroundStyle(theme.textSecondary)
                    .frame(width: iconWidth)
                Text(title)
                    .foregroundStyle(theme.textSecondary)
            }
            if !typeSize.isAccessibilitySize {
                Spacer(minLength: 12)
            }
            value
                .frame(maxWidth: typeSize.isAccessibilitySize ? .infinity : nil, alignment: .trailing)
        }
        .font(.subheadline)
        .padding(.horizontal, 12)
        .padding(.vertical, typeSize.isAccessibilitySize ? 8 : 0)
        .frame(minHeight: 46)
    }
}

struct FieldValue: View {
    @Environment(\.theme) private var theme
    var text: String
    var dimmed = false

    var body: some View {
        HStack(spacing: 4) {
            Text(text)
            Image(systemName: "chevron.up.chevron.down").font(.caption2)
        }
        .foregroundStyle(dimmed ? theme.textSecondary : theme.textPrimary)
    }
}

/// Who it is assigned to. Each tick is its own save, and "Nobody" takes everyone off at once.
struct AssigneeField: View {
    @Environment(\.theme) private var theme
    @Environment(Session.self) private var session
    /// Each person's face, drawn for the menu as it becomes ready.
    @State private var faces: [UUID: Image] = [:]
    var store: BoardStore
    var assigneeIds: [UUID]
    var canWrite: Bool
    var embedded = false
    var onChange: ([UUID]) async -> Void

    var body: some View {
        let row = FieldRow(title: "Assignees", symbol: "person.2") {
            Menu {
                ForEach(store.members.filter { $0.userId != nil }) { member in
                    let userId = member.userId!
                    Toggle(isOn: Binding(
                        get: { assigneeIds.contains(userId) },
                        set: { isOn in
                            let ids = isOn ? assigneeIds + [userId] : assigneeIds.filter { $0 != userId }
                            Task { await onChange(ids) }
                        })) {
                        Label {
                            Text(member.name)
                        } icon: {
                            faces[member.avatarId] ?? MenuFaces.ready(member) ?? Image(systemName: "person.crop.circle")
                        }
                        // Said outright: left to itself a Mac's menu shows a label's title alone.
                        .labelStyle(.titleAndIcon)
                    }
                }
                if !assigneeIds.isEmpty {
                    Divider()
                    Button("Nobody", systemImage: "person.slash") {
                        Task { await onChange([]) }
                    }
                }
            } label: {
                let assigned = store.members(assigneeIds)
                HStack(spacing: 6) {
                    if assigned.isEmpty {
                        Text("Nobody").foregroundStyle(theme.textSecondary)
                    } else {
                        AvatarStack(members: assigned, max: 4, size: 24)
                        Text(assigned.count == 1 ? assigned[0].name : "\(assigned.count) people")
                            .foregroundStyle(theme.textPrimary)
                            .lineLimit(1)
                    }
                    Image(systemName: "chevron.up.chevron.down").font(.caption2).foregroundStyle(theme.textSecondary)
                }
                // As wide as the row has room for, whoever is in it. The menu hangs from this, and
                // stays open while people are ticked: hung from a label that was only as wide as
                // what it said, it moved across the row when the last name went and "Nobody" came.
                .frame(maxWidth: .infinity, alignment: .trailing)
                .contentShape(.rect)
            }
            .layoutPriority(1)
            // Several people can be ticked without the menu being opened again for each.
            #if os(iOS)
            .menuActionDismissBehavior(.disabled)
            #endif
            .menuToggles()
            .disabled(!canWrite)
            .task(id: store.members.map(\.avatarId)) {
                for member in store.members where member.userId != nil {
                    faces[member.avatarId] = await MenuFaces.face(of: member, session: session)
                }
            }
        }
        if embedded {
            row
        } else {
            row.card(padding: 0)
        }
    }
}

/// An optional calendar day: shows it, opens a calendar to change it, and can clear it.
private struct DayFieldStyle: ViewModifier {
    var lens: Bool

    func body(content: Content) -> some View {
        // One style or the other: of two said on a button, it is the nearer that is used.
        #if os(macOS)
        if lens {
            content.rowLens()
        } else {
            content.buttonStyle(.plain)
        }
        #else
        content.buttonStyle(.plain)
        #endif
    }
}

struct DayField: View {
    @Environment(\.theme) private var theme
    var day: Day?
    var placeholder: String
    var latest: Day?
    var disabled = false
    /// On a lens of glass on a Mac, where the row's other controls are (`RowMenu`).
    var lens = false
    var onChange: (Day?) async -> Void

    @State private var picking = false
    @State private var date = Date()

    var body: some View {
        Button {
            date = day?.date() ?? min(Date(), latest?.date() ?? .distantFuture)
            picking = true
        } label: {
            HStack(spacing: 4) {
                Text(day?.formatted ?? placeholder)
                    .foregroundStyle(day == nil ? theme.textSecondary : theme.textPrimary)
                Image(systemName: "chevron.up.chevron.down").font(.caption2).foregroundStyle(theme.textSecondary)
            }
        }
        .modifier(DayFieldStyle(lens: lens))
        .disabled(disabled)
        .opacity(disabled ? 0.5 : 1)
        .popover(isPresented: $picking) {
            VStack(spacing: 8) {
                #if os(macOS)
                // A Mac's own calendar is a fraction of this size (CalendarPicker).
                CalendarPicker(selection: $date, latest: latest?.date())
                #else
                DatePicker("Date", selection: $date, in: ...(latest?.date() ?? .distantFuture), displayedComponents: .date)
                    .datePickerStyle(.graphical)
                #endif
                HStack {
                    if day != nil {
                        Button("Clear", role: .destructive) {
                            picking = false
                            Task { await onChange(nil) }
                        }
                    }
                    Spacer()
                    Button("Set") {
                        picking = false
                        Task { await onChange(Day(date)) }
                    }
                    .fontWeight(.semibold)
                }
                .padding(.horizontal, 8)
            }
            .padding(12)
            .frame(minWidth: 320)
            .presentationCompactAdaptation(.popover)
        }
    }
}
