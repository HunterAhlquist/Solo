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
