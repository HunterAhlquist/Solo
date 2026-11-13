import SwiftUI

/// Opens a task's detail over a page that isn't its board — Your Tasks, a
/// roadmap, the inbox. The task's board is loaded behind the panel, so the
/// detail has its labels, members and live updates exactly as on the board.
private struct TaskDetailHost: ViewModifier {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Binding var taskId: UUID?
    @State private var store: BoardStore?

    func body(content: Content) -> some View {
        content
            .inspector(isPresented: Binding(get: { taskId != nil }, set: { if !$0 { close() } })) {
                Group {
                    if let taskId, let store, store.board != nil {
                        DetailView(store: store, selection: .task(taskId), onClose: close)
                            .id(taskId)
                    } else if let error = store?.loadError {
                        ContentUnavailableView("Couldn't open that task", systemImage: "exclamationmark.triangle",
                                               description: Text(error))
                    } else {
                        ProgressView()
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .themedBackground()
                    }
                }
                .inspectorColumnWidth(min: 340, ideal: 440, max: 560)
            }
            .task(id: taskId) { await load() }
    }

    private func load() async {
        guard let taskId else { return }
        do {
            let task = try await session.api.task(taskId)
            if store?.board?.id != task.boardId {
                await store?.detach()
                let fresh = BoardStore(session: session)
                store = fresh
                await fresh.load(boardId: task.boardId)
            }
            await session.hub.setPresence(EntityTypes.task, taskId, .viewing)
        } catch {
            session.toasts.error(error, fallback: "Couldn't open that task.")
            self.taskId = nil
        }
    }

    private func close() {
        taskId = nil
        let old = store
        store = nil
        Task { await old?.detach() }
    }
}

extension View {
    func taskDetail(_ taskId: Binding<UUID?>) -> some View {
        modifier(TaskDetailHost(taskId: taskId))
    }
}
