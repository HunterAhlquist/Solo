import SwiftUI

// MARK: - Sub-tasks

/// A task's checklist. Ticking the last open box moves the task to Finished;
/// un-ticking never moves it back — that is a decision, not arithmetic.
struct SubTaskSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var store: BoardStore
    var task: TaskItem

    @State private var newName = ""
    @State private var editingId: UUID?
    @State private var editName = ""
    @State private var targetedId: UUID?
    @FocusState private var editFocused: Bool
    @FocusState private var addFocused: Bool

    private var api: APIClient { session.api }
    private var canWrite: Bool { store.canWrite }
    private var ordered: [SubTask] { task.subTasks.sorted { $0.rank < $1.rank } }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                SectionTitle("Sub-tasks")
                if !task.subTasks.isEmpty {
                    Text("\(task.finishedSubTasks)/\(task.subTasks.count)")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(theme.textSecondary)
                }
            }

            VStack(spacing: 0) {
                if task.subTasks.isEmpty {
                    Text("None. Adding one makes this task's status follow the checklist.")
                        .font(.footnote)
                        .foregroundStyle(theme.textSecondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(12)
                }
                ForEach(ordered) { subTask in
                    row(subTask)
                    Divider().overlay(theme.divider).padding(.leading, 44)
                }
                if canWrite {
                    HStack(spacing: 10) {
                        Image(systemName: "plus.circle")
                            .foregroundStyle(theme.textSecondary)
                            .frame(width: 24)
                        TextField("Add a sub-task…", text: $newName)
                            .focused($addFocused)
                            .submitLabel(.done)
                            .onSubmit(add)
                        if newName.nilIfBlank != nil {
                            Button("Add", action: add).fontWeight(.semibold)
                        }
                    }
                    .font(.subheadline)
                    .padding(.horizontal, 12)
                    .frame(minHeight: 44)
                }
            }
            .card(padding: 0)
        }
    }

    @ViewBuilder
    private func row(_ subTask: SubTask) -> some View {
        let label = store.label(subTask.labelId)
        HStack(spacing: 10) {
            Button {
                toggle(subTask)
            } label: {
                Image(systemName: subTask.isFinished ? "checkmark.circle.fill" : "circle")
                    .font(.title3)
                    .foregroundStyle(subTask.isFinished ? theme.primary : theme.textSecondary)
                    .contentTransition(.symbolEffect(.replace))
                    .frame(width: 24, height: 32)
            }
            .buttonStyle(.plain)
            .disabled(!canWrite)
            .accessibilityLabel(subTask.isFinished ? "Mark \(subTask.name) not done" : "Mark \(subTask.name) done")

            if editingId == subTask.id {
                TextField("Sub-task", text: $editName)
                    .focused($editFocused)
                    .submitLabel(.done)
                    .onSubmit { commitEdit(subTask) }
                    .onChange(of: editFocused) { _, focused in
                        if !focused { commitEdit(subTask) }
                    }
            } else {
                Text(subTask.name)
                    .strikethrough(subTask.isFinished)
                    .opacity(subTask.isFinished ? 0.6 : 1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(.rect)
                    .onTapGesture {
                        guard canWrite else { return }
                        editName = subTask.name
                        editingId = subTask.id
                        editFocused = true
                    }
            }

            if canWrite {
                Menu {
                    Picker("Label", selection: Binding(get: { subTask.labelId }, set: { setLabel(subTask, $0) })) {
                        Text("No label").tag(UUID?.none)
                        ForEach(store.labels(.subTask)) { label in
                            Text(label.name).tag(UUID?.some(label.id))
                        }
                    }
                } label: {
                    if let label {
                        LabelChip(label: label, compact: true)
                    } else {
                        Image(systemName: "tag").foregroundStyle(theme.textDisabled).frame(width: 28, height: 28)
                    }
                }
                .accessibilityLabel(label.map { "Label: \($0.name)" } ?? "Set label")
            } else if let label {
                LabelChip(label: label, compact: true)
            }
        }
        .font(.subheadline)
        .padding(.horizontal, 12)
        .frame(minHeight: 44)
        .overlay(alignment: .top) {
            if targetedId == subTask.id {
                Capsule().fill(theme.primary).frame(height: 3)
            }
        }
        .contentShape(.rect)
        .contextMenu {
            if canWrite {
                Button("Rename", systemImage: "pencil") {
                    editName = subTask.name
                    editingId = subTask.id
                    editFocused = true
                }
                Button("Delete", systemImage: "trash", role: .destructive) { delete(subTask) }
            }
        }
        .modifier(SubTaskReorder(enabled: canWrite && task.subTasks.count > 1, subTask: subTask, targetedId: $targetedId) { payload in
            reorder(payload, before: subTask)
        })
    }

    private func refetch() async {
        await store.refetchTask(task.id)
    }

    private func add() {
        guard let name = newName.nilIfBlank else { return }
        newName = ""
        Task {
            await session.save { try await api.createSubTask(task.id, name: String(name.prefix(FieldLimits.itemName))) }
            await refetch()
            addFocused = true
        }
    }

    private func toggle(_ subTask: SubTask) {
        let isFinished = !subTask.isFinished
        // Applied here first for instant feedback; the server's answer settles it.
        var updated = task
        if let index = updated.subTasks.firstIndex(where: { $0.id == subTask.id }) {
            updated.subTasks[index].isFinished = isFinished
        }
        let autoFinish = updated.subTasks.allSatisfy(\.isFinished) && task.status != .finished
        if autoFinish {
            updated.status = .finished
        }
        store.apply(updated)
        Task {
            await session.save { try await api.setSubTaskFinished(subTask.id, isFinished) }
            if autoFinish {
                await session.save { try await api.setTaskStatus(task.id, .finished) }
            }
            await refetch()
        }
    }

    private func commitEdit(_ subTask: SubTask) {
        guard editingId == subTask.id else { return }
        editingId = nil
        let name = String(editName.trimmingCharacters(in: .whitespacesAndNewlines).prefix(FieldLimits.itemName))
        guard !name.isEmpty, name != subTask.name else { return }
        Task {
            await session.save({ try await api.updateSubTask(subTask, name: name, labelId: subTask.labelId) }, refresh: refetch)
            await refetch()
        }
    }

    private func setLabel(_ subTask: SubTask, _ labelId: UUID?) {
        guard subTask.labelId != labelId else { return }
        Task {
            await session.save({ try await api.updateSubTask(subTask, name: subTask.name, labelId: labelId) }, refresh: refetch)
            await refetch()
        }
    }

    private func delete(_ subTask: SubTask) {
        Task {
            await session.perform { try await api.deleteSubTask(subTask.id) }
            await refetch()
        }
    }

    private func reorder(_ payload: String, before target: SubTask) -> Bool {
        guard let id = BoardDrag.id(payload, prefix: BoardDrag.subTaskPrefix), id != target.id,
              ordered.contains(where: { $0.id == id }) else { return false }
        let others = ordered.map(\.id).filter { $0 != id }
        let (preceding, following) = RankNeighbors.compute(others, index: others.firstIndex(of: target.id) ?? others.count)
        Task {
            await session.save({ try await api.rerankSubTask(id, preceding: preceding, following: following) }, refresh: refetch)
            await refetch()
        }
        return true
    }
}

private struct SubTaskReorder: ViewModifier {
    var enabled: Bool
    var subTask: SubTask
    @Binding var targetedId: UUID?
    var onDrop: (String) -> Bool

    func body(content: Content) -> some View {
        if enabled {
            content
                .draggable(BoardDrag.subTask(subTask.id)) {
                    Text(subTask.name).font(.subheadline).padding(10)
                }
                .dropDestination(for: String.self) { payloads, _ in
                    payloads.first.map(onDrop) ?? false
                } isTargeted: { targeted in
                    if targeted {
                        targetedId = subTask.id
                    } else if targetedId == subTask.id {
                        targetedId = nil
                    }
                }
        } else {
            content
        }
    }
}

// MARK: - Comments

/// A flat chronological thread — no nesting. Commenting needs membership, not
/// write access, so read-only members comment too. Only the author edits a
/// comment; the author or a moderator deletes it. A closed board stops all three.
struct CommentsSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var store: BoardStore
    var taskId: UUID?
    var epicId: UUID?

    @State private var comments: [Comment]?
    @State private var newBody = ""
    @State private var draft: MarkdownDraft?
    @State private var confirmation: Confirmation?
    @State private var posting = false

    private var api: APIClient { session.api }
    private var canComment: Bool { store.isOpen }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            SectionTitle("Comments", count: comments?.isEmpty == false ? comments?.count : nil)

            if let comments {
                if comments.isEmpty {
                    Text("No comments yet.")
                        .font(.footnote)
                        .foregroundStyle(theme.textSecondary)
                }
                ForEach(comments) { comment in
                    commentView(comment)
                }
            } else {
                ProgressView()
            }

            if canComment {
                HStack(alignment: .bottom, spacing: 8) {
                    TextField("Leave a comment…", text: $newBody, axis: .vertical)
                        .lineLimit(1...6)
                        .font(.subheadline)
                        .padding(.horizontal, 12)
                        .padding(.vertical, 10)
                        .background(theme.surface, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(theme.divider))
                    Button("Open the full editor", systemImage: "arrow.up.left.and.arrow.down.right") {
                        draft = MarkdownDraft(title: "Comment", text: newBody, limit: FieldLimits.comment,
                                              saveLabel: "Comment", placeholder: "Leave a comment…",
                                              projectId: store.board?.projectId) { body in
                            await post(body)
                        }
                    }
                    .labelStyle(.iconOnly)
                    .buttonStyle(.clearGlass)
                    Button("Comment", systemImage: "paperplane.fill") {
                        Task { await post(newBody) }
                    }
                    .labelStyle(.iconOnly)
                    .buttonStyle(.clearGlass)
                    .disabled(newBody.nilIfBlank == nil || posting)
                }
            }
        }
        .task(id: taskId ?? epicId) { await load() }
        .onChange(of: store.commentsRevision) { _, _ in
            Task { await load() }
        }
        .sheet(item: $draft) { MarkdownEditorSheet(draft: $0) }
        .confirmation($confirmation)
    }

    private func commentView(_ comment: Comment) -> some View {
        let isMine = comment.authorId == session.user.id
        return VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                UserAvatar(userId: comment.authorId, name: comment.authorDisplayName,
                           hasPicture: comment.authorAvatarUrl?.nilIfBlank != nil, size: 24)
                Text(comment.authorDisplayName ?? "Unknown")
                    .font(.footnote.weight(.semibold))
                Text(comment.createdAt.stamp)
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                Spacer()
                if canComment, isMine || store.access.canModerate {
                    Menu("Comment actions", systemImage: "ellipsis") {
                        if isMine {
                            Button("Edit", systemImage: "pencil") { edit(comment) }
                        }
                        Button("Delete", systemImage: "trash", role: .destructive) {
                            confirmation = Confirmation(title: "Delete comment", message: "Delete this comment?") {
                                await session.perform { try await api.deleteComment(comment.id) }
                                await load()
                            }
                        }
                    }
                    .labelStyle(.iconOnly)
                    .foregroundStyle(theme.textSecondary)
                }
            }
            SoloMarkdown(text: comment.body)
                .font(.subheadline)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 10)
    }

    private func load() async {
        do {
            if let taskId {
                comments = try await api.comments(taskId: taskId)
            } else if let epicId {
                comments = try await api.comments(epicId: epicId)
            }
        } catch {
            comments = comments ?? []
        }
    }

    private func post(_ body: String) async {
        guard let body = body.nilIfBlank else { return }
        posting = true
        defer { posting = false }
        let saved: Comment? = await session.save {
            if let taskId {
                return try await api.addComment(taskId: taskId, body: body)
            }
            return try await api.addComment(epicId: epicId!, body: body)
        }
        if saved != nil {
            newBody = ""
        }
        await load()
    }

    private func edit(_ comment: Comment) {
        draft = MarkdownDraft(title: "Edit comment", text: comment.body, limit: FieldLimits.comment,
                              projectId: store.board?.projectId) { body in
            guard let body = body.nilIfBlank, body != comment.body else { return }
            await session.save({ try await api.updateComment(comment, body: body) }, refresh: load)
            await load()
        }
    }
}

// MARK: - Moving to another board

/// Where should this task live? The project's other open boards, and then —
/// once one is picked — that board's epics, because an epic only means
/// anything on the board that owns it.
struct MoveTaskSheet: View {
    @Environment(Session.self) private var session
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    var projectId: UUID
    var currentBoardId: UUID
    var onMove: (Board, UUID?) async -> Void

    @State private var boards: [Board]?
    @State private var epics: [Epic] = []
    @State private var boardId: UUID?
    @State private var epicId: UUID?

    var body: some View {
        NavigationStack {
            Form {
                if let boards {
                    if boards.isEmpty {
                        Text("This project has no other open board to move the task to.")
                            .themedRow()
                    } else {
                        Section {
                            Picker("Board", selection: $boardId) {
                                Text("Choose…").tag(UUID?.none)
                                ForEach(boards) { board in
                                    Text(board.name).tag(UUID?.some(board.id))
                                }
                            }
                            Picker("Epic", selection: $epicId) {
                                Text("No epic").tag(UUID?.none)
                                ForEach(epics) { epic in
                                    Text(epic.name).tag(UUID?.some(epic.id))
                                }
                            }
                            .disabled(epics.isEmpty)
                        } footer: {
                            Text("The task is renumbered for the board it lands on, so links to its old serial number "
                                + "stop resolving. Labels carry over where the destination has the same one.")
                        }
                        .themedRow()
                    }
                } else {
                    ProgressView().themedRow()
                }
            }
            .themedList()
            .navigationTitle("Move task")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Move") {
                        guard let board = boards?.first(where: { $0.id == boardId }) else { return }
                        dismiss()
                        Task { await onMove(board, epicId) }
                    }
                    .disabled(boardId == nil)
                }
            }
            .task {
                do {
                    boards = try await session.api.boards(projectId).filter { $0.id != currentBoardId && $0.isOpen }
                } catch {
                    boards = []
                    session.toasts.error(error, fallback: "Couldn't list this project's boards.")
                }
            }
            .onChange(of: boardId) { _, id in
                epicId = nil
                epics = []
                guard let id else { return }
                Task {
                    epics = ((try? await session.api.epics(id)) ?? []).sorted { $0.rank < $1.rank }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}
