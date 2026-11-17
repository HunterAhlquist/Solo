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
                LabelMenu(labels: store.labels(.subTask), selection: subTask.labelId, label: label, compact: true) {
                    setLabel(subTask, $0)
                }
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
    /// Where the caret is in what is being typed: where Shift-Return's new line goes.
    @State private var selection: TextSelection?
    @FocusState private var composing: Bool
    @State private var draft: MarkdownDraft?
    @State private var confirmation: Confirmation?
    @State private var posting = false
    /// How tall the box a comment is typed in stands at one line, which is how tall and how
    /// wide the buttons beside it are.
    @ScaledMetric(relativeTo: .subheadline) private var composerHeight: CGFloat = 40

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
                    let box = RoundedRectangle(cornerRadius: composerHeight / 2, style: .continuous)
                    TextField("Leave a comment…", text: $newBody, selection: $selection, axis: .vertical)
                        .lineLimit(1...6)
                        .font(.subheadline)
                        .focused($composing)
                        // Return sends, as it does wherever a message is typed in a box like this.
                        .submitLabel(.send)
                        .onSubmit { send() }
                        // Shift-Return is a new line everywhere else, and a comment with more than
                        // one line is one for the editor: it opens with what is typed and the new
                        // line where the caret was.
                        .onKeyPress(.return, phases: .down) { press in
                            guard press.modifiers.contains(.shift) else { return .ignored }
                            openEditor(newLine: true)
                            return .handled
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 8)
                        .frame(minHeight: composerHeight)
                        .background(theme.surface, in: box)
                        .overlay(box.strokeBorder(theme.divider))
                    ComposerButton(title: "Open the full editor", symbol: "arrow.up.left.and.arrow.down.right",
                                   side: composerHeight) {
                        openEditor(newLine: false)
                    }
                    ComposerButton(title: "Comment", symbol: "paperplane.fill", side: composerHeight) { send() }
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

    /// A comment, as a message is drawn: a bubble with its tail towards the face of whoever
    /// wrote it, which stands outside it. The reader's own are on the trailing side and
    /// everyone else's on the leading one.
    private func commentView(_ comment: Comment) -> some View {
        let isMine = comment.authorId == session.user.id
        let canAct = canComment && (isMine || store.access.canModerate)
        let face = UserAvatar(userId: comment.authorId, name: comment.authorDisplayName,
                              hasPicture: comment.authorAvatarUrl?.nilIfBlank != nil, size: 28)
        return HStack(alignment: .bottom, spacing: 2) {
            if !isMine { face }
            VStack(alignment: isMine ? .trailing : .leading, spacing: 3) {
                HStack(spacing: 6) {
                    if isMine, canAct { actions(comment, isMine: isMine) }
                    // Whose it is goes without saying on the reader's own.
                    if !isMine {
                        Text(comment.authorDisplayName ?? "Unknown")
                            .font(.caption.weight(.semibold))
                    }
                    Text(comment.createdAt.stamp)
                        .font(.caption)
                    if !isMine, canAct { actions(comment, isMine: isMine) }
                }
                .foregroundStyle(theme.textSecondary)
                .padding(.horizontal, CommentBubble.tail + 10)

                SoloMarkdown(text: comment.body)
                    .font(.subheadline)
                    .padding(.vertical, 8)
                    .padding(.horizontal, 12)
                    .padding(isMine ? .trailing : .leading, CommentBubble.tail)
                    .glassEffect(isMine ? .regular.tint(theme.accentWash) : .regular,
                                 in: CommentBubble(trailing: isMine))
                    .contextMenu {
                        if canAct { actionItems(comment, isMine: isMine) }
                    }
            }
            if isMine { face }
        }
        // Never the whole width: which side a comment is on is who wrote it.
        .padding(isMine ? .leading : .trailing, 28)
        .frame(maxWidth: .infinity, alignment: isMine ? .trailing : .leading)
    }

    /// What can be done with a comment, behind a button beside its date and on the bubble's own menu.
    private func actions(_ comment: Comment, isMine: Bool) -> some View {
        Menu {
            actionItems(comment, isMine: isMine)
        } label: {
            Image(systemName: "ellipsis.circle")
                .font(.footnote)
                .contentShape(.rect)
        }
        // The glyph alone: a Mac would draw it a bezel, a flat pill around three dots.
        .bareControl()
        .tint(theme.textSecondary)
        .accessibilityLabel("Comment actions")
    }

    @ViewBuilder
    private func actionItems(_ comment: Comment, isMine: Bool) -> some View {
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

    /// Sends what is typed, and leaves the caret in the box on a Mac, where the next thing
    /// typed is as likely another comment. On a touch screen the keyboard goes, so that what
    /// was sent can be seen.
    private func send() {
        Task {
            await post(newBody)
            if Platform.isMac { composing = true }
        }
    }

    /// Opens the editor on what is typed. From Shift-Return, with the new line that was asked
    /// for put where the caret was — in place of whatever was selected — and the caret after it.
    private func openEditor(newLine: Bool) {
        var text = newBody
        var caret: Int?
        if newLine {
            var range = text.endIndex..<text.endIndex
            if let selection, case .selection(let chosen) = selection.indices,
               chosen.lowerBound >= text.startIndex, chosen.upperBound <= text.endIndex {
                range = chosen
            }
            let before = text.distance(from: text.startIndex, to: range.lowerBound)
            text.replaceSubrange(range, with: "\n")
            caret = before + 1
        }
        draft = MarkdownDraft(title: "Comment", text: text, caret: caret, limit: FieldLimits.comment,
                              saveLabel: "Comment", placeholder: "Leave a comment…",
                              projectId: store.board?.projectId) { body in
            await post(body)
        }
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

/// One of the buttons beside the box a comment is typed in: a circle of glass as tall as the
/// box stands at one line.
private struct ComposerButton: View {
    var title: String
    var symbol: String
    var side: CGFloat
    var action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.body.weight(.medium))
                .frame(width: side, height: side)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .foregroundStyle(.tint)
        .glassEffect(.regular.interactive(), in: .circle)
        .accessibilityLabel(title)
        .help(title)
    }
}

/// A comment's bubble: a rounded rectangle with a tail at the foot of one side, which is the
/// side its writer's face is on. The tail is outside the rectangle, so what is written keeps
/// clear of it by `tail` on that side.
nonisolated struct CommentBubble: Shape {
    /// How far the tail reaches past the bubble's side.
    static let tail: CGFloat = 6

    /// The tail is on the trailing side, for the reader's own; otherwise on the leading one.
    var trailing: Bool
    var radius: CGFloat = 17

    func path(in rect: CGRect) -> Path {
        // Drawn with the tail on the right, and turned over for the left.
        let side = rect.maxX - Self.tail
        let radius = min(radius, rect.height / 2, (side - rect.minX) / 2)
        var path = Path()
        path.move(to: CGPoint(x: rect.minX + radius, y: rect.minY))
        path.addLine(to: CGPoint(x: side - radius, y: rect.minY))
        path.addArc(tangent1End: CGPoint(x: side, y: rect.minY), tangent2End: CGPoint(x: side, y: rect.minY + radius),
                    radius: radius)
        // Down the side to where the tail leaves it, and out to the tail's point at the foot.
        // It leaves the side going straight down, as the side was, so there is no corner
        // where it starts. Its underside is the bubble's own foot carried on to the point:
        // one straight line, with nothing to join. Curved back in under the bubble instead,
        // it met the foot at a dip that showed as a notch.
        path.addLine(to: CGPoint(x: side, y: rect.maxY - radius * 0.75))
        path.addCurve(to: CGPoint(x: rect.maxX, y: rect.maxY),
                      control1: CGPoint(x: side, y: rect.maxY - radius * 0.3),
                      control2: CGPoint(x: side + Self.tail * 0.35, y: rect.maxY - 1.5))
        path.addLine(to: CGPoint(x: rect.minX + radius, y: rect.maxY))
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.maxY), tangent2End: CGPoint(x: rect.minX, y: rect.maxY - radius),
                    radius: radius)
        path.addLine(to: CGPoint(x: rect.minX, y: rect.minY + radius))
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.minY), tangent2End: CGPoint(x: rect.minX + radius, y: rect.minY),
                    radius: radius)
        path.closeSubpath()
        guard !trailing else { return path }
        return path.applying(CGAffineTransform(translationX: rect.minX + rect.maxX, y: 0).scaledBy(x: -1, y: 1))
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

/// How a label is chosen, for a task and for a sub-task alike: the label's own chip — or a
/// tag, where there is none — and under it a menu of the labels there are.
struct LabelMenu: View {
    @Environment(\.theme) private var theme
    var labels: [SoloLabel]
    var selection: UUID?
    /// The label chosen, where it is one that still exists.
    var label: SoloLabel?
    /// A sub-task's is the smaller chip, beside its name.
    var compact = false
    var onChange: (UUID?) -> Void

    var body: some View {
        Menu {
            Picker("Label", selection: Binding(get: { selection }, set: onChange)) {
                Text("No label").tag(UUID?.none)
                ForEach(labels) { label in
                    Text(label.name).tag(UUID?.some(label.id))
                }
            }
            .listedInMenu()
        } label: {
            if let label {
                LabelChip(label: label, compact: compact)
            } else {
                Image(systemName: "tag").foregroundStyle(theme.textDisabled).frame(width: 28, height: 28)
                    .contentShape(.rect)
            }
        }
        .menuToggles()
        // A tag, or the label's own chip, and nothing around either.
        .bareControl()
        .accessibilityLabel(label.map { "Label: \($0.name)" } ?? "Set label")
    }
}
