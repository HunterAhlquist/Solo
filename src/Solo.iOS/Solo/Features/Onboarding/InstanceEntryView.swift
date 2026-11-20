import SwiftUI

/// First launch, or adding an instance to the ones already kept: where is your Solo?
struct InstanceEntryView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    /// True in the sheet that adds an instance. The address is asked whether it answers
    /// before anything is left for it, and the sheet closes once it has.
    var adding = false
    /// On the first launch's screen: back to the start of what led here. Nil in the sheet,
    /// which is opened by someone who is already using the app.
    var restart: (() -> Void)?
    /// The first run's face, when this is the last of its screens.
    @Environment(OnboardingGuide.self) private var guide: OnboardingGuide?
    @State private var restartHeight: CGFloat = 34
    @State private var address = ""
    @State private var problem: String?
    @State private var connecting = false
    @FocusState private var focused: Bool

    var body: some View {
        ZStack {
            AppBackground()

            ScrollView {
                VStack(spacing: 28) {
                    VStack(spacing: 10) {
                        AppIconImage()
                        Text("Solo")
                            .font(.largeTitle.weight(theme.headingWeight))
                        Text(adding ? "Connect to another instance." : "Connect to your team's instance.")
                            .font(.callout)
                            .foregroundStyle(theme.textSecondary)
                    }
                    .padding(.top, adding ? 20 : 48)

                    VStack(alignment: .leading, spacing: 10) {
                        HStack(spacing: 10) {
                            Image(systemName: "server.rack")
                                .foregroundStyle(theme.textSecondary)
                            TextField("solo.example.com", text: $address)
                                .textContentType(.URL)
                                .keyboardType(.URL)
                                .textInputAutocapitalization(.never)
                                .autocorrectionDisabled()
                                .submitLabel(.go)
                                .focused($focused)
                                .onSubmit { connect(address) }
                                // The capsule around it is the field: a Mac's own bezel and
                                // focus ring inside that are a second one.
                                #if os(macOS)
                                .textFieldStyle(.plain)
                                .focusEffectDisabled()
                                #endif
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 14)
                        .glassEffect(.regular.interactive(), in: .capsule)

                        if let problem {
                            Label(problem, systemImage: "exclamationmark.triangle.fill")
                                .font(.footnote)
                                .foregroundStyle(theme.error)
                                .padding(.horizontal, 6)
                        }

                        Button {
                            connect(address)
                        } label: {
                            HStack {
                                if connecting {
                                    ProgressView().tint(theme.onPrimary)
                                }
                                Text(connecting ? "Connecting…" : "Connect")
                                    .fontWeight(.semibold)
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 6)
                            .onAccent()
                        }
                        .buttonStyle(.glassProminent)
                        // The field above it is a capsule; a Mac's button is a rounded box unless told.
                        .buttonBorderShape(.capsule)
                        .disabled(connecting || address.nilIfBlank == nil)
                    }

                    let recents = model.recentsToOffer
                    if !recents.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Recent")
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(theme.textSecondary)
                                .padding(.horizontal, 6)
                            ForEach(recents, id: \.self) { recent in
                                Button {
                                    address = recent
                                    connect(recent)
                                } label: {
                                    HStack {
                                        Image(systemName: "clock.arrow.circlepath")
                                            .foregroundStyle(theme.textSecondary)
                                        Text(URL(string: recent).map { SavedInstance(url: $0, name: "").address } ?? recent)
                                            .foregroundStyle(theme.textPrimary)
                                        Spacer()
                                        Image(systemName: "chevron.right")
                                            .font(.footnote.weight(.semibold))
                                            .foregroundStyle(theme.textDisabled)
                                    }
                                    .padding(.horizontal, 16)
                                    .padding(.vertical, 13)
                                    .glassEffect(.regular.interactive(), in: theme.cardShape)
                                }
                                // As it is drawn, with no bezel of a Mac's behind the glass.
                                .buttonStyle(.plain)
                                .contextMenu {
                                    Button("Forget", systemImage: "trash", role: .destructive) {
                                        model.forgetRecent(recent)
                                    }
                                }
                            }
                        }
                    }
                }
                .padding(.horizontal, 24)
                .frame(maxWidth: 460)
                .frame(maxWidth: .infinity)
                // Room under the last recent address for the button that sits over the corner.
                .padding(.bottom, restart == nil ? 0 : 64)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .overlay(alignment: .bottomLeading) {
            if let restart {
                HStack(spacing: 10) {
                    Button(action: restart) {
                        Label("Restart Onboarding", systemImage: "arrow.left")
                            .font(.subheadline.weight(.medium))
                    }
                    .buttonStyle(.clearGlass)
                    .buttonBorderShape(.capsule)
                    .onGeometryChange(for: CGFloat.self, of: \.size.height) { restartHeight = $0 }

                    // Where the face comes to rest, as tall as the button. It is the picture
                    // of the face that stays here: the one that came down from the screen
                    // before is over it until it has arrived, and then this is shown in its
                    // place.
                    GuyMark()
                        .frame(height: restartHeight)
                        .onGeometryChange(for: CGRect.self, of: { $0.frame(in: .named(Onboarding.pageSpace)) }) {
                            guide?.anchors[.connect] = $0
                        }
                        .opacity(guide?.landed ?? true ? 1 : 0)
                        .animation(.easeOut(duration: 0.18), value: guide?.landed)
                }
                .padding(.horizontal, 20)
                .padding(.bottom, 12)
                // It stays in its corner under the keyboard and doesn't ride up over the field.
                .ignoresSafeArea(.keyboard)
            }
        }
    }

    private func connect(_ input: String) {
        guard let url = AppModel.normalize(input) else {
            problem = "That doesn't look like an address. Try something like solo.example.com."
            return
        }
        problem = nil
        connecting = true
        focused = false
        Task {
            if adding {
                if model.instances.contains(where: { $0.url == url }) {
                    // Already kept: adding it again is going to it.
                } else if let failure = await model.probe(url) {
                    problem = failure
                    connecting = false
                    return
                }
                dismiss()
                await model.switchTo(url)
            } else {
                await model.connect(to: url)
            }
            connecting = false
        }
    }
}

/// The instance didn't answer. Offers another try, or another instance.
struct UnreachableView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.theme) private var theme
    var message: String

    var body: some View {
        ZStack {
            AppBackground()
            ContentUnavailableView {
                Label("Can't reach your instance", systemImage: "icloud.slash")
            } description: {
                Text(message)
                if let host = model.instanceURL?.host() {
                    Text(host).font(.footnote.monospaced())
                }
            } actions: {
                Button {
                    Task { await model.retry() }
                } label: {
                    Text("Try Again").onAccent()
                }
                .buttonStyle(.glassProminent)
                InstanceChoice { Text("Use a Different Instance") }
                    .buttonStyle(.clearGlass)
            }
        }
    }
}

/// The instance is on the local network and Solo isn't allowed on it. Only the person can
/// change that, and only in Settings, so that is where the button goes; coming back tries again.
struct LocalNetworkDeniedView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openURL) private var openURL
    @Environment(\.scenePhase) private var scenePhase
    @State private var wentToSettings = false

    var body: some View {
        ZStack {
            AppBackground()
            ContentUnavailableView {
                Label("Local network access is off", systemImage: "wifi.exclamationmark")
            } description: {
                Text("Solo tried to connect to an instance on your local network, but it hasn't been allowed to use the local network. Turn on Local Network for Solo in Settings to connect.")
                if let host = model.instanceURL?.host() {
                    Text(host).font(.footnote.monospaced())
                }
            } actions: {
                Button {
                    wentToSettings = true
                    if let url = Platform.settingsURL { openURL(url) }
                } label: {
                    Text("Open Settings").onAccent()
                }
                .buttonStyle(.glassProminent)
                Button("Try Again") {
                    Task { await model.retry() }
                }
                .buttonStyle(.clearGlass)
                InstanceChoice { Text("Use a Different Instance") }
                    .buttonStyle(.clearGlass)
            }
        }
        .onChange(of: scenePhase) { _, phase in
            // Back from Settings: whatever was changed there, find out.
            guard phase == .active, wentToSettings else { return }
            wentToSettings = false
            Task { await model.retry() }
        }
    }
}

/// First-run setup proves the operator with the instance's static credentials
/// and then hands the instance to whoever signs in with the provider — a step
/// the provider only allows on the instance's own web origin. So the wizard
/// stays on the web, and the app waits for it.
struct SetupRequiredView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.theme) private var theme
    @Environment(\.openURL) private var openURL

    var body: some View {
        ZStack {
            AppBackground()
            ContentUnavailableView {
                Label("This instance isn't set up yet", systemImage: "wrench.and.screwdriver")
            } description: {
                Text("Finish first-run setup in a browser, then come back and sign in.")
            } actions: {
                if let url = model.instanceURL {
                    Button {
                        openURL(url.appending(path: "setup"))
                    } label: {
                        Text("Open Setup in Safari").onAccent()
                    }
                    .buttonStyle(.glassProminent)
                }
                Button("Check Again") {
                    Task { await model.retry() }
                }
                .buttonStyle(.clearGlass)
                InstanceChoice { Text("Use a Different Instance") }
                    .buttonStyle(.clearGlass)
            }
        }
    }
}
