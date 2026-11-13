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
            .detailPanel(isPresented: Binding(get: { taskId != nil }, set: { if !$0 { close() } })) {
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

/// The detail panel: an inspector column beside the page where there is room
/// for one, a sheet over it where there is not.
///
/// An inspector collapses to a sheet by itself on a narrow screen, but the
/// panel wants a navigation bar there and must not have one in the column
/// (see DetailView), so the two cases are presented apart and the panel is
/// told which it is in.
private struct DetailPanel<Panel: View>: ViewModifier {
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Binding var isPresented: Bool
    @ViewBuilder var panel: Panel

    func body(content: Content) -> some View {
        if sizeClass == .regular {
            content.inspector(isPresented: $isPresented) {
                panel
                    .environment(\.detailInInspector, true)
                    .inspectorColumnWidth(min: 340, ideal: 440, max: 560)
            }
        } else {
            content.sheet(isPresented: $isPresented) {
                panel.presentationDragIndicator(.visible)
            }
        }
    }
}

extension View {
    func detailPanel<Panel: View>(isPresented: Binding<Bool>, @ViewBuilder content: () -> Panel) -> some View {
        modifier(DetailPanel(isPresented: isPresented, panel: content))
    }
}

extension EnvironmentValues {
    /// True for a detail panel shown as an inspector column rather than a sheet.
    @Entry var detailInInspector = false
}
