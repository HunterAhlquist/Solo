import SwiftUI

/// Chooses between the launch states and, once signed in, the app itself.
struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase

    var body: some View {
        ThemedRoot {
            ZStack {
                switch model.phase {
                case .launching:
                    LaunchView()
                case .needsInstance:
                    InstanceEntryView()
                case .setupRequired:
                    SetupRequiredView()
                case .signedOut:
                    SignInView()
                case .unreachable(let message):
                    UnreachableView(message: message)
                case .signedIn:
                    if let session = model.session {
                        AppShell()
                            .environment(session)
                            .environment(session.toasts)
                            .environment(session.navigator)
                            .overlay { RestartOverlay(session: session) }
                            .overlay { ToastOverlay().environment(session.toasts) }
                            .id(ObjectIdentifier(session))
                    }
                }
            }
            .animation(.smooth(duration: 0.3), value: model.phase)
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                model.session?.hub.resume()
                Task { await model.session?.notifications.refresh() }
            }
        }
    }
}

struct LaunchView: View {
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack {
            theme.background.ignoresSafeArea()
            ProgressView()
                .controlSize(.large)
        }
    }
}

/// Covers the app while the instance restarts: nothing underneath can work until it is back.
struct RestartOverlay: View {
    @Environment(\.theme) private var theme
    var session: Session

    var body: some View {
        if session.restarting {
            ZStack {
                Rectangle().fill(.ultraThinMaterial).ignoresSafeArea()
                VStack(spacing: 14) {
                    ProgressView().controlSize(.large)
                    Text("\(session.siteName) is restarting…").font(.headline)
                    Text("This page comes back on its own once the instance is up.")
                        .font(.footnote)
                        .foregroundStyle(theme.textSecondary)
                }
                .padding(28)
                .glassEffect(.regular, in: .rect(cornerRadius: 28))
            }
            .transition(.opacity)
        }
    }
}
