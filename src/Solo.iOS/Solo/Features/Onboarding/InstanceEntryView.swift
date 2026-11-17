import SwiftUI

/// First launch, or after choosing to change instance: where is your Solo?
struct InstanceEntryView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.theme) private var theme
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
                        Image(systemName: "rectangle.split.3x1.fill")
                            .font(.system(size: 44, weight: .semibold))
                            .foregroundStyle(theme.primary)
                            .padding(22)
                            .glassEffect(.regular.tint(theme.primary.opacity(0.12)), in: .rect(cornerRadius: 28))
                        Text("Solo")
                            .font(.largeTitle.weight(theme.headingWeight))
                        Text("Connect to your team's instance.")
                            .font(.callout)
                            .foregroundStyle(theme.textSecondary)
                    }
                    .padding(.top, 48)

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
                        .disabled(connecting || address.nilIfBlank == nil)
                    }

                    if !model.recentInstances.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            Text("Recent")
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(theme.textSecondary)
                                .padding(.horizontal, 6)
                            ForEach(model.recentInstances, id: \.self) { recent in
                                Button {
                                    address = recent
                                    connect(recent)
                                } label: {
                                    HStack {
                                        Image(systemName: "clock.arrow.circlepath")
                                            .foregroundStyle(theme.textSecondary)
                                        Text(URL(string: recent)?.host() ?? recent)
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
            }
            .scrollBounceBehavior(.basedOnSize)
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
            await model.connect(to: url)
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
                Button("Use a Different Instance") {
                    Task { await model.changeInstance() }
                }
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
                Button("Use a Different Instance") {
                    Task { await model.changeInstance() }
                }
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
                Button("Use a Different Instance") {
                    Task { await model.changeInstance() }
                }
                .buttonStyle(.clearGlass)
            }
        }
    }
}
