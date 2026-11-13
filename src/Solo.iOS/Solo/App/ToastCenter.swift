import SwiftUI

/// The app's snackbar: one short message at a time, said and then gone.
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
    }

    private(set) var current: Toast?
    @ObservationIgnored private var dismissal: Task<Void, Never>?

    func show(_ message: String, _ style: Style = .info) {
        let toast = Toast(message: message, style: style)
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
}

struct ToastOverlay: View {
    @Environment(ToastCenter.self) private var toasts
    @Environment(\.theme) private var theme

    var body: some View {
        VStack {
            if let toast = toasts.current {
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
        .animation(.spring(duration: 0.35), value: toasts.current)
        .sensoryFeedback(trigger: toasts.current) { _, toast in
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
