import SwiftUI
import WidgetKit

/// Chooses between the launch states and, once signed in, the app itself.
struct RootView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.scenePhase) private var scenePhase
    /// Pages read "today" when they draw — a deadline ring's days left, the
    /// roadmap's today column — so one left open overnight shows yesterday's.
    /// When the day changes the app is rebuilt in place: the navigator keeps
    /// where the person was, and every page loads again and reads the date.
    @State private var day = Day.today

    var body: some View {
        @Bindable var model = model
        ThemedRoot {
            ZStack {
                switch model.phase {
                case .launching:
                    LaunchView()
                case .needsInstance:
                    FirstRunView()
                case .setupRequired:
                    SetupRequiredView()
                case .signedOut:
                    SignInView()
                case .unreachable(let message):
                    UnreachableView(message: message)
                case .localNetworkDenied:
                    LocalNetworkDeniedView()
                case .signedIn:
                    if let session = model.session {
                        AppShell()
                            .environment(session)
                            .environment(session.toasts)
                            .environment(session.navigator)
                            .overlay { RestartOverlay(session: session) }
                            .overlay { ToastOverlay(isMain: true).environment(session.toasts) }
                            .id([AnyHashable(ObjectIdentifier(session)), AnyHashable(day)])
                            // Going from one instance to another, the last one's shell is still
                            // fading out as the next one's arrives. A Mac's window has one bar and
                            // one title, which the two then both lay claim to — and the one that
                            // goes takes them with it, leaving the page and the sidebar under the
                            // window's buttons. There a shell goes at once, before the next comes.
                            .transition(Platform.isMac ? .identity : .opacity)
                    }
                }
            }
            .animation(.smooth(duration: 0.3), value: model.phase)
            // Adding an instance, from wherever it was asked for: over whatever is showing,
            // which is left alone until the new address has answered.
            .sheet(isPresented: $model.addingInstance) {
                AddInstanceSheet()
            }
        }
        // The instances that aren't showing have no live connection: they are asked what is
        // unread now and then, for as long as the app is up.
        .task {
            while !Task.isCancelled {
                await model.refreshElsewhere()
                try? await Task.sleep(for: .seconds(300))
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .NSCalendarDayChanged).receive(on: RunLoop.main)) { _ in
            day = .today
        }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active {
                if day != .today { day = .today }
                model.session?.hub.resume()
                model.session?.hosted.cameForward()
                model.session?.docsOffline.sync()
                Task {
                    // Out of reach when it was last in front: it may not be now.
                    if model.session?.offlineMode == true { await model.session?.checkConnection() }
                    await model.session?.notifications.refresh()
                    await model.refreshElsewhere()
                    // A theme change isn't announced to anyone, so coming back to the app is when it is noticed.
                    if let api = model.session?.api {
                        await model.themes.load(from: api)
                        model.publishToWidgets()
                    }
                }
            } else if phase == .background {
                // Whatever was done here, the widgets should say so by the time the Home Screen shows them.
                #if os(iOS)
                WidgetCenter.shared.reloadAllTimelines()
                #endif
            }
        }
        .onOpenURL { model.open($0) }
    }
}

struct LaunchView: View {
    @Environment(\.theme) private var theme

    var body: some View {
        ZStack {
            AppBackground()
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
