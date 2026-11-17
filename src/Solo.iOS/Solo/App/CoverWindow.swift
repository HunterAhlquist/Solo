#if os(macOS)
import SwiftUI

// An iPhone shows a sketch being drawn, or a picture being looked at, over the whole screen,
// and the page it came from waits underneath. A Mac has room for both: there the same thing
// opens in a window of its own, with its own bar, to be put beside the note it belongs to.
//
// The page still says it the iPhone's way — `fullScreenCover(item:)` — and on a Mac that is
// this. A window is a scene, built by the app and not by the page, so what the page wants
// shown is left here for the window to pick up, under an id the two share.

/// What each open cover window shows.
@Observable
final class CoverCenter {
    static let shared = CoverCenter()

    struct Cover {
        var view: AnyView
        /// Told when the window has gone, however it went.
        var onClose: () -> Void
    }

    private(set) var covers: [UUID: Cover] = [:]

    func show(_ id: UUID, _ view: AnyView, onClose: @escaping () -> Void) {
        covers[id] = Cover(view: view, onClose: onClose)
    }

    /// The window closed: its page hears of it, once.
    func closed(_ id: UUID) {
        covers.removeValue(forKey: id)?.onClose()
    }

    func forget(_ id: UUID) {
        covers[id] = nil
    }
}

/// The scene's side: one window, showing whatever was left for its id, with the signed-in
/// app's environment around it — a window starts with none of the page's.
struct CoverWindow: View {
    static let scene = "cover"

    var id: UUID?
    var model: AppModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Group {
            if let id, let cover = CoverCenter.shared.covers[id], let session = model.session {
                ThemedRoot {
                    cover.view
                        .environment(session)
                        .environment(session.toasts)
                        .environment(session.navigator)
                        .overlay { ToastOverlay().environment(session.toasts) }
                }
            } else {
                // Nothing was left for it: a window the system brought back, or one whose page has gone.
                Color.clear.onAppear { dismiss() }
            }
        }
        .environment(model)
        .environment(model.themes)
        .preferredColorScheme(model.themes.preference.colorScheme)
        .onDisappear {
            if let id { CoverCenter.shared.closed(id) }
        }
    }
}

/// The page's side: opens the window when there is something to show, and closes it when
/// there no longer is.
struct WindowCover<Item: Identifiable, Cover: View>: ViewModifier {
    @Binding var item: Item?
    var onDismiss: (() -> Void)?
    @ViewBuilder var cover: (Item) -> Cover
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var window = UUID()
    @State private var showing = false

    func body(content: Content) -> some View {
        content
            .onChange(of: item?.id, initial: true) { sync() }
            // A page that goes takes what it was showing with it, as a cover goes with its page.
            .onDisappear {
                guard showing else { return }
                CoverCenter.shared.forget(window)
                dismissWindow(id: CoverWindow.scene, value: window)
            }
    }

    private func sync() {
        if let item {
            let binding = $item
            CoverCenter.shared.show(window, AnyView(cover(item))) { binding.wrappedValue = nil }
            if !showing {
                showing = true
                openWindow(id: CoverWindow.scene, value: window)
            }
        } else if showing {
            showing = false
            CoverCenter.shared.forget(window)
            dismissWindow(id: CoverWindow.scene, value: window)
            onDismiss?()
        }
    }
}
#endif
