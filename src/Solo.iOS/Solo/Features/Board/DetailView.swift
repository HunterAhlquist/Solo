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
                // An inspector column sits inside the page's own navigation
                // stack. A second stack in there takes the first one's
                // pushes with it, so the column gets a header of its own.
                VStack(spacing: 0) {
                    HStack(spacing: 10) {
                        Text(title).font(.headline)
                        PresenceCluster(entries: store.presence(on: entityType, entityId), max: 4)
                        Spacer()
                        Button("Close", systemImage: "xmark", action: onClose)
                            .labelStyle(.iconOnly)
                            .buttonStyle(.glass)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                    panel
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .themedBackground()
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
                            ToolbarItem(placement: .confirmationAction) {
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
                    taskFields(task)
                    TaskDetailExtensions(task: task, store: store)
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

                if canWrite {
                    HStack(spacing: 10) {
                        if task != nil {
                            Button("Move to Board", systemImage: "arrow.right.doc.on.clipboard") { moving = true }
                                .buttonStyle(.glass)
                        }
                        Button("Delete \(task != nil ? "Task" : "Epic")", systemImage: "trash", role: .destructive) {
                            confirmDelete()
                        }
                        .buttonStyle(.glass)
                        .tint(theme.error)
                    }
                    .font(.subheadline)
                    .padding(.top, 4)
                }
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
                } label: {
                    FieldValue(text: task.status.title)
                }
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
                Menu {
                    Picker("Label", selection: Binding(get: { task.labelId }, set: { setLabel(task, $0) })) {
                        Text("No label").tag(UUID?.none)
                        ForEach(store.labels(.task)) { label in
                            Text(label.name).tag(UUID?.some(label.id))
                        }
                    }
                } label: {
                    if let label = store.label(task.labelId) {
                        LabelChip(label: label)
                    } else {
                        FieldValue(text: "None", dimmed: true)
                    }
                }
                .disabled(!canWrite)
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
                              limit: FieldLimits.description) { text in
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
    var title: String
    var symbol: String
    @ViewBuilder var value: Value

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .foregroundStyle(theme.textSecondary)
                .frame(width: 22)
            Text(title)
                .foregroundStyle(theme.textSecondary)
            Spacer(minLength: 12)
            value
        }
        .font(.subheadline)
        .padding(.horizontal, 12)
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

/// Who it is assigned to. Each tick is its own save.
struct AssigneeField: View {
    @Environment(\.theme) private var theme
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
                    Toggle(member.name, isOn: Binding(
                        get: { assigneeIds.contains(userId) },
                        set: { isOn in
                            let ids = isOn ? assigneeIds + [userId] : assigneeIds.filter { $0 != userId }
                            Task { await onChange(ids) }
                        }))
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
            }
            .menuActionDismissBehavior(.disabled)
            .disabled(!canWrite)
        }
        if embedded {
            row
        } else {
            row.card(padding: 0)
        }
    }
}

/// An optional calendar day: shows it, opens a calendar to change it, and can clear it.
struct DayField: View {
    @Environment(\.theme) private var theme
    var day: Day?
    var placeholder: String
    var latest: Day?
    var disabled = false
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
        .buttonStyle(.plain)
        .disabled(disabled)
        .opacity(disabled ? 0.5 : 1)
        .popover(isPresented: $picking) {
            VStack(spacing: 8) {
                DatePicker("Date", selection: $date, in: ...(latest?.date() ?? .distantFuture), displayedComponents: .date)
                    .datePickerStyle(.graphical)
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
