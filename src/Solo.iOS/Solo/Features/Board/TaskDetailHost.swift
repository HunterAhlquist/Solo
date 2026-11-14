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
            .detailPanel(isPresented: Binding(get: { taskId != nil }, set: { if !$0 { close() } }),
                         id: PanelState(taskId: taskId, boardId: store?.board?.id, error: store?.loadError)) {
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
                    }
                }
            }
            .task(id: taskId) { await load() }
    }

    /// What the panel is showing: it is drawn again whenever this changes.
    private struct PanelState: Hashable {
        var taskId: UUID?
        var boardId: UUID?
        var error: String?
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

/// The detail panel: a column beside the page where there is room for one, a
/// sheet over it where there is not.
///
/// The column is not the system's inspector. That one is laid out inside the
/// page's navigation stack: it sits under the bar, with the bar's buttons
/// still above it, and the page beside it snaps to its new width. This one
/// belongs to the shell — it stands over the bar's trailing end, from the top
/// of the screen down — and the page gives it room through its safe area, a
/// change the page animates like any other.
private struct DetailPanel<ID: Hashable, Panel: View>: ViewModifier {
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(DetailPanelCenter.self) private var center: DetailPanelCenter?
    @Binding var isPresented: Bool
    /// What the panel shows. The shell is handed a built view, so the page
    /// has to say when that view is out of date.
    var id: ID
    @ViewBuilder var panel: () -> Panel
    @State private var owner = UUID()

    private struct Showing: Equatable {
        var presented: Bool
        var id: ID
    }

    func body(content: Content) -> some View {
        if sizeClass == .regular, let center {
            content
                // The page's backgrounds still run under the panel; only what it lays out moves over.
                .safeAreaPadding(.trailing, center.isShowing(owner) ? center.width : 0)
                .onChange(of: Showing(presented: isPresented, id: id), initial: true) { sync(center) }
                // A page comes back from under a push without its state having changed.
                .onAppear { sync(center) }
                .onDisappear { center.hide(owner) }
        } else {
            content.sheet(isPresented: $isPresented) {
                panel().presentationDragIndicator(.visible)
            }
        }
    }

    private func sync(_ center: DetailPanelCenter) {
        if isPresented {
            center.show(AnyView(panel().environment(\.detailInInspector, true)), for: owner, id: AnyHashable(id))
        } else {
            center.hide(owner)
        }
    }
}

extension View {
    func detailPanel<ID: Hashable, Panel: View>(isPresented: Binding<Bool>, id: ID,
                                                @ViewBuilder content: @escaping () -> Panel) -> some View {
        modifier(DetailPanel(isPresented: isPresented, id: id, panel: content))
    }
}

/// The shell's trailing panel: which page has put what in it.
@Observable
final class DetailPanelCenter {
    struct Panel {
        var owner: UUID
        var id: AnyHashable
        var view: AnyView
    }

    private(set) var panel: Panel?
    /// The room the panel takes from the page, its margins included. Set by the shell.
    var width: CGFloat = 448

    static let animation = Animation.smooth(duration: 0.38)

    func isShowing(_ owner: UUID) -> Bool { panel?.owner == owner }

    func show(_ view: AnyView, for owner: UUID, id: AnyHashable) {
        if panel?.owner == owner {
            // The same page's panel showing something else: swapped in place.
            guard panel?.id != id else { return }
            panel = Panel(owner: owner, id: id, view: view)
        } else {
            withAnimation(Self.animation) { panel = Panel(owner: owner, id: id, view: view) }
        }
    }

    func hide(_ owner: UUID) {
        guard panel?.owner == owner else { return }
        withAnimation(Self.animation) { panel = nil }
    }
}

/// Where the shell draws the panel: a pane of glass down the trailing edge,
/// over the page and its bar alike.
struct DetailPanelColumn: View {
    @Environment(DetailPanelCenter.self) private var center
    private let margin: CGFloat = 8

    var body: some View {
        GeometryReader { proxy in
            if let panel = center.panel {
                let shape = RoundedRectangle(cornerRadius: 26, style: .continuous)
                panel.view
                    // The pane runs up behind the status bar; what is in it does not.
                    .padding(.top, max(proxy.safeAreaInsets.top - margin, 0))
                    .frame(width: center.width - margin * 2)
                    .frame(maxHeight: .infinity)
                    .clipShape(shape)
                    .glassEffect(.regular, in: shape)
                    .padding(margin)
                    .frame(maxWidth: .infinity, alignment: .trailing)
                    .ignoresSafeArea(.container, edges: .vertical)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }
        }
    }
}

extension EnvironmentValues {
    /// True for a detail panel shown as the shell's column rather than a sheet.
    @Entry var detailInInspector = false
}
