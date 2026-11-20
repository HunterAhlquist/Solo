import SwiftUI

/// The app's snackbar: one short message at a time, said and then gone.
///
/// On a Mac the app has several windows and one of these between them, so a message is said in
/// one window: the one in front when it was raised, which is where whatever raised it was done.
@Observable
final class ToastCenter {
    enum Style {
        case info, success, warning, error

        var symbol: String {
            switch self {
            case .info: "info.circle.fill"
            case .success: "checkmark.circle.fill"
            case .warning: "exclamationmark.triangle.fill"
            case .error: "xmark.octagon.fill"
            }
        }
    }

    struct Toast: Identifiable, Equatable {
        let id = UUID()
        var message: String
        var style: Style
        /// The window it is said in. None is every window, which is the one there is anywhere but a Mac.
        var window: ObjectIdentifier?
    }

    private(set) var current: Toast?
    @ObservationIgnored private var dismissal: Task<Void, Never>?
    /// The windows with somewhere to show a toast, and whether each is the main one.
    @ObservationIgnored private var hosts: [ObjectIdentifier: Bool] = [:]
    /// The last window to have been in front, for a toast raised while none is: the app is behind
    /// another, or what raised it has only now come back from the server.
    @ObservationIgnored private var lastActed: ObjectIdentifier?

    func host(_ window: ObjectIdentifier, isMain: Bool) { hosts[window] = isMain }
    func unhost(_ window: ObjectIdentifier) { hosts[window] = nil }
    /// A window that shows toasts has come to the front.
    func acted(in window: ObjectIdentifier) { lastActed = window }

    func show(_ message: String, _ style: Style = .info) {
        var toast = Toast(message: message, style: style)
        toast.window = target()
        current = toast
        dismissal?.cancel()
        dismissal = Task { [weak self] in
            // Long enough to read a sentence; errors stay a little longer.
            try? await Task.sleep(for: .seconds(style == .error ? 5 : 3.5))
            if !Task.isCancelled, self?.current == toast {
                self?.current = nil
            }
        }
    }

    func error(_ error: Error, fallback: String = "Something went wrong. Try again.") {
        show((error as? LocalizedError)?.errorDescription ?? fallback, .error)
    }

    func dismiss() {
        current = nil
    }

    /// The window a toast raised now belongs in: the one in front, if it can show one, or else
    /// the last that was. Failing both the main window, so that nothing is said to no one.
    private func target() -> ObjectIdentifier? {
        #if os(macOS)
        // A sheet, a popover and a menu are windows of their own on a Mac, hung from the one they
        // belong to. What is done in one of them is done in that window.
        var front = NSApp.keyWindow
        while let owner = front?.sheetParent ?? front?.parent { front = owner }
        if let front, hosts[ObjectIdentifier(front)] != nil {
            lastActed = ObjectIdentifier(front)
        }
        if let lastActed, hosts[lastActed] != nil { return lastActed }
        return hosts.first { $0.value }?.key ?? hosts.keys.first
        #else
        return nil
        #endif
    }
}

struct ToastOverlay: View {
    /// Whether this is the main window's, which says what was raised with no window to say it in.
    var isMain = false

    @Environment(ToastCenter.self) private var toasts
    @Environment(\.theme) private var theme
    @State private var window: ObjectIdentifier?

    /// The toast, if it is this window's to show.
    private var shown: ToastCenter.Toast? {
        guard let toast = toasts.current else { return nil }
        return toast.window == nil || toast.window == window ? toast : nil
    }

    var body: some View {
        VStack {
            if let toast = shown {
                HStack(spacing: 10) {
                    Image(systemName: toast.style.symbol)
                        .foregroundStyle(color(toast.style))
                    Text(toast.message)
                        .font(.subheadline.weight(.medium))
                        .foregroundStyle(theme.textPrimary)
                        .multilineTextAlignment(.leading)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
                .glassEffect(.regular, in: .capsule)
                .padding(.horizontal, 20)
                .transition(.move(edge: .top).combined(with: .opacity))
                .onTapGesture { toasts.dismiss() }
                .accessibilityAddTraits(.isStaticText)
                .id(toast.id)
            }
            Spacer()
        }
        .padding(.top, 6)
        .animation(.spring(duration: 0.35), value: shown)
        #if os(macOS)
        .background {
            WindowProbe { found in
                if let window { toasts.unhost(window) }
                window = found
                if let found { toasts.host(found, isMain: isMain) }
            } front: {
                toasts.acted(in: $0)
            }
        }
        .onDisappear {
            if let window { toasts.unhost(window) }
        }
        #endif
        .sensoryFeedback(trigger: shown) { _, toast in
            switch toast?.style {
            case .error: .error
            case .warning: .warning
            case .success: .success
            default: nil
            }
        }
    }

    private func color(_ style: ToastCenter.Style) -> Color {
        switch style {
        case .info: theme.info
        case .success: theme.success
        case .warning: theme.warning
        case .error: theme.error
        }
    }
}

#if os(macOS)
/// Tells a view which window it is in, again when that changes, and each time the window comes
/// to the front.
private struct WindowProbe: NSViewRepresentable {
    var found: (ObjectIdentifier?) -> Void
    var front: (ObjectIdentifier) -> Void

    final class Probe: NSView {
        var found: ((ObjectIdentifier?) -> Void)?
        var front: ((ObjectIdentifier) -> Void)?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            NotificationCenter.default.removeObserver(self)
            let id = window.map(ObjectIdentifier.init)
            if let window {
                // Key is the window being typed in; main is the one in front while a panel has the
                // keys, and what a window becomes when it is opened behind another app.
                for name in [NSWindow.didBecomeKeyNotification, NSWindow.didBecomeMainNotification] {
                    NotificationCenter.default.addObserver(self, selector: #selector(cameFront), name: name, object: window)
                }
            }
            // After this pass: the view is put in its window in the middle of one.
            DispatchQueue.main.async { [weak self] in
                self?.found?(id)
                if let self, let window = self.window, window.isKeyWindow || window.isMainWindow { self.front?(ObjectIdentifier(window)) }
            }
        }

        @objc private func cameFront() {
            if let window { front?(ObjectIdentifier(window)) }
        }
    }

    func makeNSView(context: Context) -> Probe {
        let probe = Probe()
        probe.found = found
        probe.front = front
        return probe
    }

    func updateNSView(_ probe: Probe, context: Context) {
        probe.found = found
        probe.front = front
    }
}
#endif
